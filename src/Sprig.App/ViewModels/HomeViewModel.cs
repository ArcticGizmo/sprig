using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Sprig.App.ViewModels;

/// <summary>
/// The front-door page: what sprig is for, the model in one picture, a journey rail showing where
/// you are, and a single next-best-action. Everything is a read-only projection of the three stores
/// (recomputed each time Home becomes the current page).
/// </summary>
public partial class HomeViewModel : PageViewModel
{
    readonly AppServices _services;
    readonly Action<PageViewModel> _navigate;
    readonly PageViewModel _repos;
    readonly PageViewModel _stacks;
    readonly PageViewModel _workspaces;

    public HomeViewModel(AppServices services, Action<PageViewModel> navigate,
        PageViewModel repos, PageViewModel stacks, PageViewModel workspaces)
    {
        _services = services;
        _navigate = navigate;
        _repos = repos;
        _stacks = stacks;
        _workspaces = workspaces;
    }

    public override string Title => "Home";

    /// <summary>Where the user is along repo → stack → workspace; drives the rail + next-best-action.</summary>
    [ObservableProperty] private SetupState _state = new(0, 0, 0);

    /// <summary>The most recent workspaces, for the configured-state panel.</summary>
    public ObservableCollection<WorkspaceItemViewModel> Recent { get; } = [];

    /// <summary>First-run: nothing registered yet — show the teaching hero + model picture.</summary>
    public bool IsEmptyStage => State.Stage == SetupStage.Empty;

    /// <summary>Show the "your workspaces" + quick-actions panels once anything is running.</summary>
    public bool HasAnyWorkspaces => State.Workspaces > 0;

    partial void OnStateChanged(SetupState value)
    {
        OnPropertyChanged(nameof(IsEmptyStage));
        OnPropertyChanged(nameof(HasAnyWorkspaces));
    }

    protected override void OnActivated() => _ = RefreshAsync();

    async Task RefreshAsync()
    {
        var (repos, stacks, records) = await AppServices.RunAsync(() =>
            (_services.Repos.List().Count,
             _services.Stacks.List().Count,
             _services.Workspaces.List()));

        State = new SetupState(repos, stacks, records.Count);
        Recent.Clear();
        foreach (var r in records.OrderByDescending(r => r.CreatedAt).Take(4))
            Recent.Add(new WorkspaceItemViewModel(r));
    }

    /// <summary>The one recommended step — routes to the right page for the current stage.</summary>
    [RelayCommand]
    private void PrimaryAction() => _navigate(State.Stage switch
    {
        SetupStage.Empty => _repos,
        SetupStage.ReposReady => _stacks,
        _ => _workspaces,
    });

    [RelayCommand] private void GoToRepos() => _navigate(_repos);
    [RelayCommand] private void GoToStacks() => _navigate(_stacks);
    [RelayCommand] private void GoToWorkspaces() => _navigate(_workspaces);
}
