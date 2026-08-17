using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprig.Core.Maps;

namespace Sprig.App.ViewModels;

/// <summary>
/// The Maps page (the Graph Turn): browse the maps of self-describing repos and grow a workspace from a
/// slice of one. The wiring is derived from the repos' own provides/needs, so this page only picks a map,
/// chooses which repos to include, and names the workspace — no binding editor. Runs alongside the Stacks
/// page during the transition.
/// </summary>
public sealed partial class MapsViewModel : PageViewModel
{
    readonly AppServices _services;

    public override string Title => "Maps";

    public ObservableCollection<MapDefinition> Maps { get; } = [];

    /// <summary>Which repos of the selected map to include in a checkout (all selected by default; a
    /// deselected repo is passed to CreateFromMap's --without).</summary>
    public ObservableCollection<MapRepoChoice> RepoChoices { get; } = [];

    /// <summary>Registered repos, each with a checkbox for whether the map being authored includes it.</summary>
    public ObservableCollection<MapRepoChoice> EditRepos { get; } = [];

    [ObservableProperty] private MapDefinition? _selected;
    [ObservableProperty] private string _newWorkspaceName = "";
    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _statusIsError;
    [ObservableProperty] private bool _busy;

    // --- authoring a map (create / edit) ---
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string? _editError;
    string? _editingOriginal;   // the map being edited (for rename), or null when creating

    public bool HasMaps => Maps.Count > 0;

    public MapsViewModel(AppServices services)
    {
        _services = services;
        Reload();
    }

    protected override void OnActivated() { if (!IsEditing) Reload(); }

    void Reload()
    {
        var keep = Selected?.Name;
        Maps.Clear();
        foreach (var map in _services.Maps.List())
            Maps.Add(map);
        NavCount = Maps.Count;
        OnPropertyChanged(nameof(HasMaps));
        Selected = Maps.FirstOrDefault(m => m.Name == keep) ?? Maps.FirstOrDefault();
    }

    partial void OnSelectedChanged(MapDefinition? value)
    {
        RepoChoices.Clear();
        if (value is null) return;
        foreach (var repo in value.Repos)
            RepoChoices.Add(new MapRepoChoice(repo.Name));
        CreateWorkspaceCommand.NotifyCanExecuteChanged();
    }

    partial void OnNewWorkspaceNameChanged(string value) => CreateWorkspaceCommand.NotifyCanExecuteChanged();
    partial void OnBusyChanged(bool value) => CreateWorkspaceCommand.NotifyCanExecuteChanged();

    // --- authoring commands ---

    /// <summary>Start composing a brand-new map: a blank name and every registered repo unchecked.</summary>
    [RelayCommand]
    private void NewMap()
    {
        _editingOriginal = null;
        EditName = "";
        EditError = null;
        LoadEditRepos(null);
        IsEditing = true;
    }

    /// <summary>Edit the selected map: its name and which registered repos it includes.</summary>
    [RelayCommand]
    private void EditSelected()
    {
        if (Selected is null) return;
        _editingOriginal = Selected.Name;
        EditName = Selected.Name;
        EditError = null;
        LoadEditRepos(Selected);
        IsEditing = true;
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
        EditError = null;
    }

    /// <summary>Persist the map being authored. On a rename the old file is removed. Validation errors
    /// (bad name, no repos, an unregistered repo) surface inline rather than throwing.</summary>
    [RelayCommand]
    private void SaveMap()
    {
        var name = EditName.Trim();
        var repos = EditRepos.Where(c => c.IsSelected).Select(c => MapRepo.Local(c.Name)).ToList();
        try
        {
            _services.Maps.Save(new MapDefinition { Name = name, Repos = repos });
            if (_editingOriginal is { } old && !string.Equals(old, name, System.StringComparison.Ordinal))
                _services.Maps.Remove(old);
            _services.NotifyStoreChanged();
            IsEditing = false;
            EditError = null;
            Reload();
            Selected = Maps.FirstOrDefault(m => m.Name == name) ?? Selected;
        }
        catch (System.Exception ex)
        {
            EditError = ex.Message;
        }
    }

    /// <summary>Delete the selected map (the workspaces it created are untouched).</summary>
    [RelayCommand]
    private void DeleteSelected()
    {
        if (Selected is null) return;
        _services.Maps.Remove(Selected.Name);
        _services.NotifyStoreChanged();
        Reload();
    }

    void LoadEditRepos(MapDefinition? map)
    {
        EditRepos.Clear();
        var inMap = map is null
            ? new System.Collections.Generic.HashSet<string>()
            : map.Repos.Select(r => r.Name).ToHashSet(System.StringComparer.Ordinal);
        foreach (var repo in _services.Repos.List())
            EditRepos.Add(new MapRepoChoice(repo.Name) { IsSelected = inMap.Contains(repo.Name) });
    }

    bool CanCreate => !Busy && Selected is not null && !string.IsNullOrWhiteSpace(NewWorkspaceName);

    /// <summary>Grow a workspace from the selected map and the chosen repo slice. Resolves + materialises off
    /// the UI thread; an unmet need surfaces as the error status (nothing half-built is left behind).</summary>
    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateWorkspace()
    {
        if (Selected is null) return;
        var mapName = Selected.Name;
        var name = NewWorkspaceName.Trim();
        var without = RepoChoices.Where(c => !c.IsSelected).Select(c => c.Name).ToList();

        Busy = true;
        Status = null;
        try
        {
            await AppServices.RunAsync(() =>
            {
                var (map, repos) = _services.MapResolver.Resolve(mapName, without);
                _services.Workspaces.CreateFromMap(name, map, repos);
            });
            _services.NotifyStoreChanged();
            StatusIsError = false;
            Status = $"Created workspace '{name}' — open the Workspaces page to run it.";
            NewWorkspaceName = "";
        }
        catch (Exception ex)
        {
            StatusIsError = true;
            Status = ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }
}

/// <summary>One repo of a map, with a checkbox for whether to include it in a checkout.</summary>
public sealed partial class MapRepoChoice(string name) : ObservableObject
{
    public string Name { get; } = name;
    [ObservableProperty] private bool _isSelected = true;
}
