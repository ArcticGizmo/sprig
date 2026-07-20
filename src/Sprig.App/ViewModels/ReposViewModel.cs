using System;
using System.Collections.ObjectModel;
using System.IO;
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
    [ObservableProperty] private string _newPath = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _busy;

    /// <summary>True while the "Add repo" modal is open.</summary>
    [ObservableProperty] private bool _isAdding;

    /// <summary>True when the entered path already contains a <c>.sprig.json</c>.</summary>
    [ObservableProperty] private bool _pathHasConfig;

    /// <summary>Plain-language explanation of what "Add" will do for the entered path.</summary>
    [ObservableProperty] private string _detectHint = "";

    public bool HasSelected => Selected is not null;

    /// <summary>False when no repos are registered yet — drives the first-run empty state.</summary>
    public bool HasRepos => Repos.Count > 0;

    /// <summary>Adapts the modal's primary button to what the path actually needs.</summary>
    public string AddButtonLabel => PathHasConfig ? "Register" : "Initialize & register";

    partial void OnSelectedChanged(RegisteredRepo? value)
    {
        SelectedConfig = value is null ? null : RepoConfigViewModel.Load(value.Path);
        OnPropertyChanged(nameof(HasSelected));
    }

    partial void OnPathHasConfigChanged(bool value) => OnPropertyChanged(nameof(AddButtonLabel));

    partial void OnNewPathChanged(string value)
    {
        var p = value.Trim();
        if (p.Length == 0) { PathHasConfig = false; DetectHint = ""; return; }

        bool has;
        try { has = File.Exists(Path.Combine(p, ".sprig.json")); }
        catch { has = false; }

        PathHasConfig = has;
        DetectHint = has
            ? "Found a .sprig.json here — it will be registered as-is."
            : "No .sprig.json here — sprig will inspect the repo, create one, then register it.";
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
