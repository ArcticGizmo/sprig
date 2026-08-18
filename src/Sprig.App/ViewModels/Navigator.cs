using System;
using System.Linq;
using System.Threading.Tasks;

namespace Sprig.App.ViewModels;

/// <summary>
/// App-level navigation between the top-level pages, plus "go and start the action" shortcuts so an
/// empty state can be a one-click fix (e.g. Maps with no repos → jump to Repos and open Add).
/// Wired up by <see cref="MainWindowViewModel"/> once every page exists.
/// </summary>
public sealed class Navigator
{
    Action<PageViewModel> _navigate = static _ => { };
    Action _startGuide = static () => { };
    Action _enterTour = static () => { };
    Action<Coach.Guide> _enterGuide = static _ => { };
    PageViewModel? _home;
    ReposViewModel? _repos;
    MapsViewModel? _maps;
    WorkspacesViewModel? _workspaces;
    PageViewModel? _settings;

    public void Configure(Action<PageViewModel> navigate, PageViewModel home,
        ReposViewModel repos, MapsViewModel maps, WorkspacesViewModel workspaces,
        PageViewModel? settings = null)
    {
        _navigate = navigate;
        _home = home;
        _repos = repos;
        _maps = maps;
        _workspaces = workspaces;
        _settings = settings;
    }

    /// <summary>Wire the setup-guide launcher (owned by the main window).</summary>
    public void SetGuideLauncher(Action start) => _startGuide = start;

    /// <summary>Open the guided setup strip.</summary>
    public void StartSetupGuide() => _startGuide();

    /// <summary>Wire the guided-tour launcher (owned by the main window, which does the store swap).</summary>
    public void SetTourLauncher(Action enter) => _enterTour = enter;

    /// <summary>
    /// Enter the guided tour. Routed through here so a page can offer it without knowing that a store
    /// swap is involved — only the main window does.
    /// </summary>
    public void EnterTour() => _enterTour();

    /// <summary>Wire the guide launcher (owned by the main window — it resets the sandbox and swaps stores).</summary>
    public void SetGuideEntry(Action<Coach.Guide> enter) => _enterGuide = enter;

    /// <summary>Start a guided lesson. Routed through here so the Learn page needn't know about the swap.</summary>
    public void EnterGuide(Coach.Guide guide) => _enterGuide(guide);

    // --- Coachmark preconditions & escape hatches (used by the guides) ---

    /// <summary>Go to Repos and pre-fill the Add-repo modal with a folder, so a guide's Add is one click.</summary>
    public void PrimeAddRepo(string path)
    {
        if (_repos is null) return;
        Go(_repos);
        _repos.PrimeAdd(path);
    }

    /// <summary>Register a repo by path through the real add flow — a guide step's "Show me".</summary>
    public Task RegisterRepo(string path) => _repos?.AddPathAsync(path) ?? Task.CompletedTask;

    /// <summary>Select an already-registered repo and open its editor (so its inputs can be pointed at).</summary>
    public void EditRepo(string name)
    {
        if (_repos is null) return;
        Go(_repos);
        _repos.Selected = _repos.Repos.FirstOrDefault(r => r.Name == name) ?? _repos.Selected;
        if (_repos.Selected is not null && _repos.BeginEditCommand.CanExecute(null))
            _repos.BeginEditCommand.Execute(null);
    }

    /// <summary>
    /// Open a registered repo's editor, keeping any in-progress edit. Idempotent: if that repo's editor is
    /// already open it isn't reloaded, so this can precondition several consecutive module-guide steps
    /// without discarding a module the previous step added (BeginEdit rebuilds the editor from disk).
    /// </summary>
    public void PrepareRepoEditor(string name)
    {
        if (_repos is null) return;
        Go(_repos);
        if (_repos.Editor is not null && _repos.Selected?.Name == name) return;   // already editing it
        _repos.Selected = _repos.Repos.FirstOrDefault(r => r.Name == name) ?? _repos.Selected;
        if (_repos.Selected is not null && _repos.BeginEditCommand.CanExecute(null))
            _repos.BeginEditCommand.Execute(null);
    }

    /// <summary>
    /// Add a second module to a repo's open editor and select it — the modules guide's hands-on step. Adding
    /// a module is editor state, not a store change, so this is driven from a step's <c>Prepare</c> rather
    /// than waited on. Idempotent: a module with this name is added at most once, so stepping back and forth
    /// never stacks duplicates.
    /// </summary>
    public void AddModuleTo(string repo, string moduleName, string path)
    {
        PrepareRepoEditor(repo);
        if (_repos?.Editor is not { } editor) return;

        var existing = editor.Modules.FirstOrDefault(m => m.Name.Trim() == moduleName);
        if (existing is not null) { editor.SelectedModule = existing; return; }

        editor.AddModuleCommand.Execute(null);   // appends a blank, auto-selected tab
        if (editor.SelectedModule is { } tab)
        {
            tab.Name = moduleName;
            tab.Path = path;
        }
    }

    public void GoHome() => Go(_home);
    public void GoToRepos() => Go(_repos);
    public void GoToMaps() => Go(_maps);
    public void GoToWorkspaces() => Go(_workspaces);
    public void GoToSettings() => Go(_settings);

    /// <summary>Show the Maps page with no editor open, so the "New map" button is on screen.</summary>
    public void ShowMapsFresh()
    {
        if (_maps is null) return;
        Go(_maps);
        if (_maps.IsEditing && _maps.CancelEditCommand.CanExecute(null))
            _maps.CancelEditCommand.Execute(null);
    }

    /// <summary>Show the Workspaces page with no create form open, so "New workspace" is on screen.</summary>
    public void ShowWorkspacesFresh()
    {
        if (_workspaces is null) return;
        Go(_workspaces);
        if (_workspaces.IsCreating && _workspaces.CancelCreateCommand.CanExecute(null))
            _workspaces.CancelCreateCommand.Execute(null);
    }

    /// <summary>
    /// Open (or keep open) the New-workspace form, pre-filled with a name and the first map, infra off so a
    /// guide never depends on Docker. Idempotent, so it can precondition several consecutive steps.
    /// <para>Awaits the open: the form loads its maps and repo checklist asynchronously, and its own
    /// resets would otherwise land <i>after</i> the fields set here.</para>
    /// </summary>
    public async Task PrepareNewWorkspace(string name)
    {
        if (_workspaces is null) return;
        Go(_workspaces);
        if (!_workspaces.IsCreating) await _workspaces.NewWorkspaceCommand.ExecuteAsync(null);
        _workspaces.NewName = name;
        _workspaces.StartInfraOnCreate = false;   // teaching worktrees/ports/compose; no daemon needed
        _workspaces.NewMap ??= _workspaces.AvailableMaps.FirstOrDefault();
    }

    /// <summary>Create the workspace the form describes — a guide step's "Show me". Async (real worktrees).</summary>
    public Task CreateWorkspace()
        => _workspaces is { } w && w.CreateCommand.CanExecute(null)
            ? w.CreateCommand.ExecuteAsync(null)
            : Task.CompletedTask;

    /// <summary>Run Reconcile on the selected workspace, so any drift is detected and shown (the drift guide).</summary>
    public Task Reconcile()
        => _workspaces is { } w && w.ReconcileCommand.CanExecute(null)
            ? w.ReconcileCommand.ExecuteAsync(null)
            : Task.CompletedTask;

    /// <summary>Repair the selected workspace, rebuilding missing worktrees — a guide step's "Show me".</summary>
    public Task Repair()
        => _workspaces is { } w && w.RepairCommand.CanExecute(null)
            ? w.RepairCommand.ExecuteAsync(null)
            : Task.CompletedTask;

    /// <summary>Jump to Repos and open the Add-repo modal.</summary>
    public void AddRepo() { if (_repos is null) return; Go(_repos); _repos.OpenAddCommand.Execute(null); }

    /// <summary>Jump to Maps and open the New-map editor.</summary>
    public void NewMap() { if (_maps is null) return; Go(_maps); _maps.NewMapCommand.Execute(null); }

    /// <summary>Jump to Workspaces and open the New-workspace flow.</summary>
    public void NewWorkspace() { if (_workspaces is null) return; Go(_workspaces); _workspaces.NewWorkspaceCommand.Execute(null); }

    // "Go there and show something" — a page whose detail panel is empty teaches nothing, so these
    // select the first row on the way in. Generic navigation, not tour-specific.

    /// <summary>Jump to Repos with the first repo selected, so its config panel is populated.</summary>
    public void ShowFirstRepo()
    {
        if (_repos is null) return;
        Go(_repos);
        _repos.Selected ??= _repos.Repos.FirstOrDefault();
    }

    /// <summary>Jump to Maps with the first map selected, so its detail summary is populated.</summary>
    public void ShowFirstMap()
    {
        if (_maps is null) return;
        Go(_maps);
        _maps.Selected ??= _maps.Maps.FirstOrDefault();
    }

    /// <summary>Jump to Workspaces with the first workspace selected, so its detail panel is populated.</summary>
    public void ShowFirstWorkspace()
    {
        if (_workspaces is null) return;
        Go(_workspaces);
        _workspaces.Selected ??= _workspaces.Workspaces.FirstOrDefault();
    }

    /// <summary>
    /// Bring the first workspace's docker infra up, through the page's own Up command — so busy state,
    /// error surfacing, and the status refresh are the already-tested ones, not a second copy.
    /// </summary>
    public Task StartFirstWorkspaceInfra()
    {
        if (_workspaces is null) return Task.CompletedTask;
        ShowFirstWorkspace();
        return _workspaces.UpCommand.ExecuteAsync(null);
    }

    void Go(PageViewModel? page)
    {
        if (page is not null) _navigate(page);
    }
}
