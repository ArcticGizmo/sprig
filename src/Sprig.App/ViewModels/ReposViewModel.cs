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
using Sprig.Core.Workspaces;

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

    /// <summary>Raised when the isolate quick-create begins, carrying the checklist view-model for the
    /// view to open in its own non-blocking progress window.</summary>
    public event Action<OperationProgressViewModel>? OperationStarted;

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

    /// <summary>
    /// What sprig detected while scaffolding the repo just added — which env keys and compose ports became
    /// declared inputs, and what it chose not to touch.
    ///
    /// <c>InitInspector</c> has always produced these notes and the CLI has always printed them
    /// (<c>CliApp</c>); the app used to discard them, leaving a first-timer looking at a pre-filled form
    /// with no account of where any of it came from. Dismissable, because it explains one action rather
    /// than being a permanent panel.
    /// </summary>
    public ObservableCollection<string> ScaffoldNotes { get; } = [];

    [ObservableProperty] private bool _hasScaffoldNotes;

    void ShowScaffoldNotes(IReadOnlyList<string> notes)
    {
        ScaffoldNotes.Clear();
        foreach (var note in notes) ScaffoldNotes.Add(note);
        HasScaffoldNotes = ScaffoldNotes.Count > 0;
    }

    /// <summary>Dismiss the scaffold explanation.</summary>
    [RelayCommand]
    private void DismissScaffoldNotes() => HasScaffoldNotes = false;

    /// <summary>True while the edit form is shown (hides the read-only config view).</summary>
    public bool IsEditing => Editor is not null;

    /// <summary>Show the read-only config view: a repo is selected and we're not editing.</summary>
    public bool ShowReadOnly => HasSelected && !IsEditing;

    /// <summary>A repo is selected, its config loaded cleanly, and we're not already editing.</summary>
    public bool CanEdit => HasSelected && SelectedConfig is { Ok: true } && !IsEditing;

    /// <summary>A zero-input repo can stand up on its own (the ad-hoc create path) — no stack needed.</summary>
    public bool CanIsolate => HasSelected && SelectedConfig is { Ok: true, HasInputs: false } && !IsEditing;

    /// <summary>True while the inline "name this workspace" prompt of the fast path is open.</summary>
    [ObservableProperty] private bool _isIsolating;
    [ObservableProperty] private string _isolateName = "";
    [ObservableProperty] private string? _isolateError;

    /// <summary>False when no repos are registered yet — drives the first-run empty state.</summary>
    public bool HasRepos => Repos.Count > 0;

    /// <summary>Adapts the modal's primary button to what the path actually needs — either way it
    /// opens the editor afterwards, so the label says so.</summary>
    public string AddButtonLabel => PathHasConfig ? "Load & edit" : "Create & edit";

    /// <summary>A path has been entered — drives the git-status highlight in the modal.</summary>
    public bool PathEntered => !string.IsNullOrWhiteSpace(NewPath);

    /// <summary>Entered path is a git repo — safe to register.</summary>
    public bool GitOk => PathEntered && PathIsGitRepo;

    /// <summary>Entered path is NOT a git repo — sprig won't be able to create worktrees here.</summary>
    public bool GitMissing => PathEntered && !PathIsGitRepo;

    partial void OnSelectedChanged(RegisteredRepo? value)
    {
        Editor = null; // leave edit mode when switching repos
        IsIsolating = false;
        ConfirmingDelete = false;
        SelectedConfig = value is null ? null : RepoConfigViewModel.Load(value.Path);
        OnPropertyChanged(nameof(HasSelected));
        OnPropertyChanged(nameof(ShowReadOnly));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanIsolate));
    }

    partial void OnSelectedConfigChanged(RepoConfigViewModel? value)
    {
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanIsolate));
    }

    partial void OnEditorChanged(RepoEditViewModel? value)
    {
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(ShowReadOnly));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanIsolate));
    }

    /// <summary>Open the inline "name this workspace" prompt for the single-repo fast path.</summary>
    [RelayCommand]
    private void BeginIsolate()
    {
        IsolateError = null;
        IsolateName = Selected?.Name ?? "";
        IsIsolating = true;
    }

    [RelayCommand]
    private void CancelIsolate() => IsIsolating = false;

    /// <summary>Create an isolated workspace directly from this repo — no stack (ad-hoc engine path).</summary>
    [RelayCommand]
    private async Task ConfirmIsolate()
    {
        var repo = Selected;
        var name = IsolateName.Trim();
        if (repo is null) return;
        if (name.Length == 0) { IsolateError = "enter a workspace name"; return; }

        // Resolve + plan up front so pre-flight problems stay in the inline isolate form.
        ResolvedStack resolved;
        IReadOnlyList<WorkspaceStep> plan;
        try
        {
            resolved = await AppServices.RunAsync(() => Services.Workspaces.ResolveSingleRepo(repo.Path));
            plan = Services.Workspaces.PlanCreate(resolved, name);
        }
        catch (Exception ex) { IsolateError = ex.Message; return; }

        var modal = new OperationProgressViewModel($"Creating workspace '{name}'");
        modal.Load(plan);
        IsIsolating = false;
        OperationStarted?.Invoke(modal);

        Busy = true; IsolateError = null; Status = null;
        try
        {
            var progress = new Progress<WorkspaceStepProgress>(modal.Apply);
            var record = await AppServices.RunAsync(() => Services.Workspaces.Create(resolved, name, progress));
            var setupFail = SetupWarning.Summarize(record);
            Status = setupFail is null
                ? $"created workspace '{name}' — open the Workspaces tab to run or remove it"
                : $"created workspace '{name}', but {setupFail} — the worktree was kept; finish setup manually";
            Services.NotifyStoreChanged();
            modal.Finish(
                setupFail is null ? $"Created '{name}'." : $"Created '{name}', but {setupFail}.",
                setupFail is null ? WorkspaceStepState.Done : WorkspaceStepState.Warning);
        }
        catch (Exception ex)
        {
            modal.Finish($"Couldn't create '{name}': {ex.Message}", WorkspaceStepState.Error);
        }
        finally { Busy = false; }
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
            ? "Found a .sprig.json here — it'll be loaded so you can review and edit it."
            : "No .sprig.json here — sprig will scaffold one from the repo, then open it for editing.";
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

    /// <summary>
    /// A folder path a guide has suggested, so opening Add repo lands with it already filled in. Cleared
    /// once used. Lets "register your first repo" hand-hold to a single Confirm without the user having to
    /// know where the sample lives.
    /// </summary>
    string? _primedPath;

    /// <summary>Pre-fill the next Add-repo modal with this folder (a coachmark precondition).</summary>
    public void PrimeAdd(string path) => _primedPath = path;

    /// <summary>
    /// Register a repo by path, driving the exact same flow as the modal's Confirm — so a guide's "Show me"
    /// lands the user in the same editor, with the same StoreChanged, as doing it by hand. Detection is
    /// synchronous, so <see cref="PathHasConfig"/> is correct immediately after setting the path.
    /// </summary>
    public Task AddPathAsync(string path)
    {
        NewPath = path;
        return AddInternal(runInit: !PathHasConfig);
    }

    [RelayCommand]
    private void OpenAdd()
    {
        NewPath = _primedPath ?? "";
        _primedPath = null;
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
            IReadOnlyList<string> notes = [];
            var added = await AppServices.RunAsync(() =>
            {
                if (runInit)
                {
                    var proposal = Services.Init.Inspect(path);
                    ConfigJson.Write(proposal.Config, Path.Combine(path, ".sprig.json"));
                    // Keep the proposal's advisory notes. They explain which env keys and compose ports
                    // became inputs and why — the exact question someone asks on landing in the editor
                    // for the first time. The CLI has always printed these; the app used to bin them.
                    notes = proposal.Notes;
                }
                return Services.Repos.Add(path);
            });
            NewPath = "";
            IsAdding = false;
            Reload();
            Services.NotifyStoreChanged();

            // Drop straight into the editor for the repo just added — staying in the list view
            // (with only a status line) was easy to miss and left the config a step away.
            Selected = Repos.FirstOrDefault(r => r.Name == added.Name);
            if (Selected is not null) BeginEdit();
            Status = IsEditing
                ? $"registered '{added.Name}' — editing its configuration"
                : $"registered '{added.Name}'";

            ShowScaffoldNotes(notes);
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
        Services.NotifyStoreChanged();
    }

    /// <summary>True while the "delete .sprig.json" confirm bar is showing for the selected repo.</summary>
    [ObservableProperty] private bool _confirmingDelete;

    /// <summary>Ask for confirmation before deleting the repo's <c>.sprig.json</c>.</summary>
    [RelayCommand]
    private void Delete()
    {
        if (Selected is not null) ConfirmingDelete = true;
    }

    [RelayCommand]
    private void CancelDelete() => ConfirmingDelete = false;

    /// <summary>Delete the selected repo's <c>.sprig.json</c> and unregister it — a full state reset.
    /// The repo is no longer a sprig repo afterwards; re-add it to scaffold a fresh config.</summary>
    [RelayCommand]
    private void ConfirmDelete()
    {
        if (Selected is null) return;
        ConfirmingDelete = false;

        var name = Selected.Name;
        var configPath = Path.Combine(Selected.Path, ".sprig.json");
        try
        {
            if (File.Exists(configPath)) File.Delete(configPath);
        }
        catch (Exception ex) { Error = ex.Message; return; }

        Services.Repos.Remove(name);
        Status = $"deleted .sprig.json and unregistered '{name}'";
        Reload();
        Services.NotifyStoreChanged();
    }

    void Reload()
    {
        Repos.Clear();
        foreach (var r in Services.Repos.List()) Repos.Add(r);
        OnPropertyChanged(nameof(HasRepos));
        NavCount = Repos.Count;
    }
}
