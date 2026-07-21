using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprig.Core.Stacks;

namespace Sprig.App.ViewModels;

public partial class ReposViewModel : PageViewModel
{
    protected readonly AppServices Services;

    public ReposViewModel(AppServices services)
    {
        Services = services;
        Reload();
    }

    public override string Title => "Repos";

    public ObservableCollection<RegisteredRepo> Repos { get; } = [];

    [ObservableProperty] private RegisteredRepo? _selected;
    [ObservableProperty] private RepoConfigViewModel? _selectedConfig;

    /// <summary>Non-null while the selected repo's config is being edited in place.</summary>
    [ObservableProperty] private RepoEditViewModel? _editor;
    [ObservableProperty] private string _newPath = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _busy;

    /// <summary>True while the "Add repo" modal is open.</summary>
    [ObservableProperty] private bool _isAdding;

    /// <summary>True when the entered path already contains a <c>.sprig.json</c>.</summary>
    [ObservableProperty] private bool _pathHasConfig;

    /// <summary>True when the entered path looks like a git repo (has a <c>.git</c>). Sprig can't
    /// create worktrees without one, so the modal highlights this loudly.</summary>
    [ObservableProperty] private bool _pathIsGitRepo;

    /// <summary>Plain-language explanation of what "Add" will do for the entered path.</summary>
    [ObservableProperty] private string _detectHint = "";

    public bool HasSelected => Selected is not null;

    /// <summary>True while the edit form is shown (hides the read-only config view).</summary>
    public bool IsEditing => Editor is not null;

    /// <summary>Show the read-only config view: a repo is selected and we're not editing.</summary>
    public bool ShowReadOnly => HasSelected && !IsEditing;

    /// <summary>A repo is selected, its config loaded cleanly, and we're not already editing.</summary>
    public bool CanEdit => HasSelected && SelectedConfig is { Ok: true } && !IsEditing;

    /// <summary>False when no repos are registered yet — drives the first-run empty state.</summary>
    public bool HasRepos => Repos.Count > 0;

    /// <summary>Adapts the modal's primary button to what the path actually needs.</summary>
    public string AddButtonLabel => PathHasConfig ? "Register" : "Initialize & register";

    /// <summary>A path has been entered — drives the git-status highlight in the modal.</summary>
    public bool PathEntered => !string.IsNullOrWhiteSpace(NewPath);

    /// <summary>Entered path is a git repo — safe to register.</summary>
    public bool GitOk => PathEntered && PathIsGitRepo;

    /// <summary>Entered path is NOT a git repo — sprig won't be able to create worktrees here.</summary>
    public bool GitMissing => PathEntered && !PathIsGitRepo;

    partial void OnSelectedChanged(RegisteredRepo? value)
    {
        Editor = null; // leave edit mode when switching repos
        SelectedConfig = value is null ? null : RepoConfigViewModel.Load(value.Path);
        OnPropertyChanged(nameof(HasSelected));
        OnPropertyChanged(nameof(ShowReadOnly));
        OnPropertyChanged(nameof(CanEdit));
    }

    partial void OnSelectedConfigChanged(RepoConfigViewModel? value) => OnPropertyChanged(nameof(CanEdit));

    partial void OnEditorChanged(RepoEditViewModel? value)
    {
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(ShowReadOnly));
        OnPropertyChanged(nameof(CanEdit));
    }

    [RelayCommand]
    private void BeginEdit()
    {
        if (Selected is null) return;
        Error = null;
        Status = null;
        try { Editor = RepoEditViewModel.Load(Selected.Path, Services.Git); }
        catch (Exception ex) { Error = ex.Message; }
    }

    [RelayCommand]
    private void CancelEdit() => Editor = null;

    [RelayCommand]
    private void SaveEdit()
    {
        if (Editor is null) return;
        if (!Editor.Save()) return; // Editor.Error surfaces the validation/write failure in the form

        var name = Editor.Name;
        Editor = null;
        // Reload the read-only view so the saved values show immediately.
        SelectedConfig = Selected is null ? null : RepoConfigViewModel.Load(Selected.Path);
        Status = $"saved changes to '{name}'";
    }

    partial void OnPathHasConfigChanged(bool value) => OnPropertyChanged(nameof(AddButtonLabel));

    partial void OnPathIsGitRepoChanged(bool value)
    {
        OnPropertyChanged(nameof(GitOk));
        OnPropertyChanged(nameof(GitMissing));
    }

    partial void OnNewPathChanged(string value)
    {
        OnPropertyChanged(nameof(PathEntered));
        OnPropertyChanged(nameof(GitOk));
        OnPropertyChanged(nameof(GitMissing));

        var p = value.Trim();
        if (p.Length == 0) { PathHasConfig = false; PathIsGitRepo = false; DetectHint = ""; return; }

        bool hasConfig, isGit;
        try { hasConfig = File.Exists(Path.Combine(p, ".sprig.json")); }
        catch { hasConfig = false; }
        // A normal repo has a .git directory; worktrees/submodules use a .git file. Either counts.
        try { isGit = Directory.Exists(Path.Combine(p, ".git")) || File.Exists(Path.Combine(p, ".git")); }
        catch { isGit = false; }

        PathHasConfig = hasConfig;
        PathIsGitRepo = isGit;
        DetectHint = hasConfig
            ? "Found a .sprig.json here — it will be registered as-is."
            : "No .sprig.json here — sprig will inspect the repo, create one, then register it.";
    }

    /// <summary>
    /// Directory suggestions for the path auto-complete: children of the typed-so-far directory
    /// whose name starts with the trailing fragment. Pure + best-effort (never throws).
    /// </summary>
    public IReadOnlyList<string> SuggestPaths(string input)
    {
        input = (input ?? "").Trim();
        if (input.Length == 0) return [];
        try
        {
            string dir, prefix;
            if (Directory.Exists(input) && (input.EndsWith('\\') || input.EndsWith('/')))
            {
                dir = input;
                prefix = "";
            }
            else
            {
                dir = Path.GetDirectoryName(input) ?? "";
                prefix = Path.GetFileName(input);
            }
            if (dir.Length == 0 || !Directory.Exists(dir)) return [];

            return Directory.EnumerateDirectories(dir)
                .Where(d => Path.GetFileName(d).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Take(25)
                .ToList();
        }
        catch { return []; }
    }

    [RelayCommand]
    private void OpenAdd()
    {
        NewPath = "";
        Error = null;
        Status = null;
        IsAdding = true;
    }

    [RelayCommand]
    private void CancelAdd()
    {
        IsAdding = false;
        Error = null;
    }

    /// <summary>Single primary action for the modal — inits only when the repo has no config yet.</summary>
    [RelayCommand]
    private Task ConfirmAdd() => AddInternal(runInit: !PathHasConfig);

    [RelayCommand]
    private Task Add() => AddInternal(runInit: false);

    [RelayCommand]
    private Task InitAndAdd() => AddInternal(runInit: true);

    async Task AddInternal(bool runInit)
    {
        var path = NewPath.Trim();
        if (string.IsNullOrEmpty(path)) { Error = "enter a repo path"; return; }

        Busy = true; Error = null; Status = null;
        try
        {
            var added = await AppServices.RunAsync(() =>
            {
                if (runInit)
                {
                    var proposal = Services.Init.Inspect(path);
                    ConfigJson.Write(proposal.Config, Path.Combine(path, ".sprig.json"));
                }
                return Services.Repos.Add(path);
            });
            NewPath = "";
            Status = $"registered '{added.Name}'";
            IsAdding = false;
            Reload();
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { Busy = false; }
    }

    /// <summary>Open the selected repo's folder in the OS file manager, for a quick look inside.</summary>
    [RelayCommand]
    private void OpenInExplorer()
        => Launch(p => new ProcessStartInfo { FileName = p, UseShellExecute = true });

    /// <summary>Open the selected repo in VS Code (<c>code &lt;path&gt;</c> on the PATH).</summary>
    [RelayCommand]
    private void OpenInVsCode()
        => Launch(p => new ProcessStartInfo { FileName = "code", Arguments = $"\"{p}\"", UseShellExecute = true });

    /// <summary>Open a terminal at the selected repo's folder (Windows Terminal, else a shell).</summary>
    [RelayCommand]
    private void OpenInTerminal()
    {
        var path = Selected?.Path;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (OperatingSystem.IsWindows())
                // Prefer Windows Terminal; fall back to PowerShell if wt isn't installed.
                try { Process.Start(new ProcessStartInfo { FileName = "wt.exe", Arguments = $"-d \"{path}\"", UseShellExecute = true }); }
                catch { Process.Start(new ProcessStartInfo { FileName = "powershell.exe", WorkingDirectory = path, UseShellExecute = true }); }
            else if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo { FileName = "open", Arguments = $"-a Terminal \"{path}\"" });
            else
                Process.Start(new ProcessStartInfo { FileName = "x-terminal-emulator", WorkingDirectory = path });
        }
        catch (Exception ex) { Error = ex.Message; }
    }

    /// <summary>Launch a process for the selected repo path; surfaces any failure in <see cref="Error"/>.</summary>
    void Launch(Func<string, ProcessStartInfo> build)
    {
        var path = Selected?.Path;
        if (string.IsNullOrEmpty(path)) return;
        try { Process.Start(build(path)); }
        catch (Exception ex) { Error = ex.Message; }
    }

    [RelayCommand]
    private void Remove()
    {
        if (Selected is null) return;
        Services.Repos.Remove(Selected.Name);
        Status = $"unregistered '{Selected.Name}'";
        Reload();
    }

    void Reload()
    {
        Repos.Clear();
        foreach (var r in Services.Repos.List()) Repos.Add(r);
        OnPropertyChanged(nameof(HasRepos));
    }
}
