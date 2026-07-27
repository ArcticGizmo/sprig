using System;
using System.Linq;
using System.Threading.Tasks;

namespace Sprig.App.ViewModels;

/// <summary>
/// App-level navigation between the top-level pages, plus "go and start the action" shortcuts so an
/// empty state can be a one-click fix (e.g. Stacks with no repos → jump to Repos and open Add).
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
    StacksViewModel? _stacks;
    WorkspacesViewModel? _workspaces;
    PageViewModel? _settings;

    public void Configure(Action<PageViewModel> navigate, PageViewModel home,
        ReposViewModel repos, StacksViewModel stacks, WorkspacesViewModel workspaces,
        PageViewModel? settings = null)
    {
        _navigate = navigate;
        _home = home;
        _repos = repos;
        _stacks = stacks;
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

    public void GoHome() => Go(_home);
    public void GoToRepos() => Go(_repos);
    public void GoToStacks() => Go(_stacks);
    public void GoToWorkspaces() => Go(_workspaces);
    public void GoToSettings() => Go(_settings);

    /// <summary>
    /// Open the stack builder with every registered repo selected and auto-wired — the state in which the
    /// canvas has nodes, ports and cables to point at. A coachmark precondition, not a user-facing action.
    /// </summary>
    public Task OpenStackBuilderWired()
    {
        if (_stacks is null) return Task.CompletedTask;

        Go(_stacks);
        if (_stacks.NewStackCommand.CanExecute(null)) _stacks.NewStackCommand.Execute(null);
        foreach (var choice in _stacks.RepoChoices) choice.IsSelected = true;
        if (_stacks.AutoWireCommand.CanExecute(null)) _stacks.AutoWireCommand.Execute(null);

        return Task.CompletedTask;
    }

    /// <summary>Show the Stacks page with no builder open, so the "New stack" button is on screen.</summary>
    public void ShowStacksFresh()
    {
        if (_stacks is null) return;
        Go(_stacks);
        if (_stacks.IsCreating && _stacks.CancelCreateCommand.CanExecute(null))
            _stacks.CancelCreateCommand.Execute(null);
    }

    /// <summary>
    /// Open (or keep open) the stack builder over every registered repo, named and auto-wired, so a guide
    /// can point at the wiring. Idempotent: if the builder is already open it isn't reset, so this can be a
    /// precondition on several consecutive steps without wiping the user's progress.
    /// </summary>
    public void PrepareStackBuilder(string name)
    {
        if (_stacks is null) return;
        Go(_stacks);
        if (!_stacks.IsCreating && _stacks.NewStackCommand.CanExecute(null))
            _stacks.NewStackCommand.Execute(null);
        _stacks.NewName = name;
        foreach (var choice in _stacks.RepoChoices) choice.IsSelected = true;  // selection auto-wires
        if (_stacks.AutoWireCommand.CanExecute(null)) _stacks.AutoWireCommand.Execute(null);
    }

    /// <summary>Save the stack the builder is composing — a guide step's "Show me".</summary>
    public Task CreateStack()
    {
        if (_stacks is { } s && s.CreateCommand.CanExecute(null)) s.CreateCommand.Execute(null);
        return Task.CompletedTask;
    }

    /// <summary>Jump to Repos and open the Add-repo modal.</summary>
    public void AddRepo() { if (_repos is null) return; Go(_repos); _repos.OpenAddCommand.Execute(null); }

    /// <summary>Jump to Stacks and open the New-stack builder.</summary>
    public void NewStack() { if (_stacks is null) return; Go(_stacks); _stacks.NewStackCommand.Execute(null); }

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

    /// <summary>Jump to Stacks with the first stack selected, so its wiring summary is populated.</summary>
    public void ShowFirstStack()
    {
        if (_stacks is null) return;
        Go(_stacks);
        _stacks.Selected ??= _stacks.Stacks.FirstOrDefault();
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
