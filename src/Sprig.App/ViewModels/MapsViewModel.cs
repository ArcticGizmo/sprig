using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprig.Core.Maps;

namespace Sprig.App.ViewModels;

/// <summary>
/// The Maps page (the Graph Turn): the maps of self-describing repos, each shown as its wiring. A map is
/// composed on its graph (repos are added and removed there) and its variables are linked or given constants
/// below it; this page only names, renames and deletes maps. Workspaces are checked out from the Workspaces
/// page.
/// </summary>
public sealed partial class MapsViewModel : PageViewModel
{
    readonly AppServices _services;

    public override string Title => "Maps";

    public ObservableCollection<MapDefinition> Maps { get; } = [];

    [ObservableProperty] private MapDefinition? _selected;

    // --- naming a new map ---
    [ObservableProperty] private bool _isNaming;
    [ObservableProperty] private string _newMapName = "";

    // --- renaming the selected map ---
    [ObservableProperty] private bool _isRenaming;
    [ObservableProperty] private string _renameText = "";

    [ObservableProperty] private string? _nameError;

    public bool HasMaps => Maps.Count > 0;

    /// <summary>How the selected map's needs are wired, and the fixes for what isn't.</summary>
    public MapWiringViewModel Wiring { get; }

    public MapsViewModel(AppServices services)
    {
        _services = services;
        Wiring = new MapWiringViewModel(services, Reload);
        Reload();
    }

    protected override void OnActivated() { if (!IsNaming && !IsRenaming) Reload(); }

    bool _reloading;

    void Reload()
    {
        var keep = Selected?.Name;
        _reloading = true;
        try
        {
            Maps.Clear();
            foreach (var map in _services.Maps.List())
                Maps.Add(map);
            NavCount = Maps.Count;
            OnPropertyChanged(nameof(HasMaps));
            Selected = Maps.FirstOrDefault(m => m.Name == keep) ?? Maps.FirstOrDefault();
        }
        finally { _reloading = false; }
        LoadWiringIfMoved();
    }

    // Clearing the list during a reload makes a bound list box drop its selection for a moment; reacting to
    // that would reload the wiring and lose the user's place. A reload after the wiring view changed the map
    // hands back the same map under a new record, and the wiring already shows the change, so only a move to
    // another map loads the wiring afresh.
    partial void OnSelectedChanged(MapDefinition? value)
    {
        IsRenaming = false;
        if (!_reloading) LoadWiringIfMoved();
    }

    void LoadWiringIfMoved()
    {
        if (Selected?.Name != Wiring.MapName || Selected is null)
            Wiring.Load(Selected?.Name);
    }

    /// <summary>Start a new map: ask for its name, then open its empty graph to add repos to.</summary>
    [RelayCommand]
    private void NewMap()
    {
        NewMapName = "";
        NameError = null;
        IsNaming = true;
    }

    [RelayCommand]
    private void CancelNewMap()
    {
        IsNaming = false;
        NameError = null;
    }

    [RelayCommand]
    private void CreateMap()
    {
        var name = NewMapName.Trim();
        try
        {
            if (_services.Maps.Get(name) is not null)
                throw new MapException($"a map called '{name}' already exists");
            _services.Maps.Save(new MapDefinition { Name = name });
            _services.NotifyStoreChanged();
            IsNaming = false;
            NameError = null;
            Reload();
            Selected = Maps.FirstOrDefault(m => m.Name == name) ?? Selected;
        }
        catch (Exception ex)
        {
            NameError = ex.Message;
        }
    }

    [RelayCommand]
    private void BeginRename()
    {
        if (Selected is null) return;
        RenameText = Selected.Name;
        NameError = null;
        IsRenaming = true;
    }

    [RelayCommand]
    private void CancelRename()
    {
        IsRenaming = false;
        NameError = null;
    }

    /// <summary>Save the selected map under a new name, keeping everything else about it.</summary>
    [RelayCommand]
    private void SaveRename()
    {
        if (Selected is null) return;
        var old = Selected.Name;
        var name = RenameText.Trim();
        if (name == old) { IsRenaming = false; return; }
        try
        {
            if (_services.Maps.Get(name) is not null)
                throw new MapException($"a map called '{name}' already exists");
            _services.Maps.Save((_services.Maps.Get(old) ?? Selected) with { Name = name });
            _services.Maps.Remove(old);
            _services.NotifyStoreChanged();
            IsRenaming = false;
            NameError = null;
            Reload();
            Selected = Maps.FirstOrDefault(m => m.Name == name) ?? Selected;
        }
        catch (Exception ex)
        {
            NameError = ex.Message;
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
}
