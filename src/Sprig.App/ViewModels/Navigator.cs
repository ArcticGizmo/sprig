using System;

namespace Sprig.App.ViewModels;

/// <summary>
/// App-level navigation between the top-level pages, plus "go and start the action" shortcuts so an
/// empty state can be a one-click fix (e.g. Stacks with no repos → jump to Repos and open Add).
/// Wired up by <see cref="MainWindowViewModel"/> once every page exists.
/// </summary>
public sealed class Navigator
{
    Action<PageViewModel> _navigate = static _ => { };
    PageViewModel? _home;
    ReposViewModel? _repos;
    StacksViewModel? _stacks;
    WorkspacesViewModel? _workspaces;

    public void Configure(Action<PageViewModel> navigate, PageViewModel home,
        ReposViewModel repos, StacksViewModel stacks, WorkspacesViewModel workspaces)
    {
        _navigate = navigate;
        _home = home;
        _repos = repos;
        _stacks = stacks;
        _workspaces = workspaces;
    }

    public void GoHome() => Go(_home);
    public void GoToRepos() => Go(_repos);
    public void GoToStacks() => Go(_stacks);
    public void GoToWorkspaces() => Go(_workspaces);

    /// <summary>Jump to Repos and open the Add-repo modal.</summary>
    public void AddRepo() { if (_repos is null) return; Go(_repos); _repos.OpenAddCommand.Execute(null); }

    /// <summary>Jump to Stacks and open the New-stack builder.</summary>
    public void NewStack() { if (_stacks is null) return; Go(_stacks); _stacks.NewStackCommand.Execute(null); }

    /// <summary>Jump to Workspaces and open the New-workspace flow.</summary>
    public void NewWorkspace() { if (_workspaces is null) return; Go(_workspaces); _workspaces.NewWorkspaceCommand.Execute(null); }

    void Go(PageViewModel? page)
    {
        if (page is not null) _navigate(page);
    }
}
