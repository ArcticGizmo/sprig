using System;
using CommunityToolkit.Mvvm.Input;

namespace Sprig.App.ViewModels;

/// <summary>
/// The front-door page: what sprig is for, the model in one picture, and the way in to the first
/// step. Navigation-only for now (M1); the journey rail + next-best-action land in M2.
/// </summary>
public partial class HomeViewModel : PageViewModel
{
    readonly Action<PageViewModel> _navigate;
    readonly PageViewModel _repos;
    readonly PageViewModel _stacks;
    readonly PageViewModel _workspaces;

    public HomeViewModel(Action<PageViewModel> navigate,
        PageViewModel repos, PageViewModel stacks, PageViewModel workspaces)
    {
        _navigate = navigate;
        _repos = repos;
        _stacks = stacks;
        _workspaces = workspaces;
    }

    public override string Title => "Home";

    [RelayCommand] private void GoToRepos() => _navigate(_repos);
    [RelayCommand] private void GoToStacks() => _navigate(_stacks);
    [RelayCommand] private void GoToWorkspaces() => _navigate(_workspaces);
}
