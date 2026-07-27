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
    readonly Navigator _nav;

    public HomeViewModel(AppServices services, Navigator nav)
    {
        _services = services;
        _nav = nav;
        _services.StoreChanged += () => _ = RefreshAsync();
    }

    public override string Title => "Home";

    /// <summary>Where the user is along repo → stack → workspace; drives the rail + next-best-action.</summary>
    [ObservableProperty] private SetupState _state = new(0, 0, 0);

    /// <summary>The most recent workspaces, for the configured-state panel.</summary>
    public ObservableCollection<WorkspaceItemViewModel> Recent { get; } = [];

    /// <summary>Toggles the model-picture card open when the user isn't in the first-run state.</summary>
    [ObservableProperty] private bool _showModelCard;

    /// <summary>First-run: nothing registered yet — show the teaching hero + model picture.</summary>
    public bool IsEmptyStage => State.Stage == SetupStage.Empty;

    /// <summary>Show the "your workspaces" + quick-actions panels once anything is running.</summary>
    public bool HasAnyWorkspaces => State.Workspaces > 0;

    /// <summary>The model picture shows automatically on first run, or on demand via "How it works".</summary>
    public bool ShowModel => IsEmptyStage || ShowModelCard;

    partial void OnStateChanged(SetupState value)
    {
        OnPropertyChanged(nameof(IsEmptyStage));
        OnPropertyChanged(nameof(HasAnyWorkspaces));
        OnPropertyChanged(nameof(ShowModel));
    }

    partial void OnShowModelCardChanged(bool value) => OnPropertyChanged(nameof(ShowModel));

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

    /// <summary>The one recommended step — opens the right flow for the current stage.</summary>
    [RelayCommand]
    private void PrimaryAction()
    {
        switch (State.Stage)
        {
            case SetupStage.Empty: _nav.AddRepo(); break;
            case SetupStage.ReposReady: _nav.NewStack(); break;
            default: _nav.NewWorkspace(); break;
        }
    }

    [RelayCommand] private void ToggleModelCard() => ShowModelCard = !ShowModelCard;

    /// <summary>Launch the guided setup strip (first-run "walk me through setup").</summary>
    [RelayCommand] private void StartGuide() => _nav.StartSetupGuide();

    /// <summary>
    /// Enter the guided tour — a complete working setup, built for the user to click around. Offered
    /// alongside "walk me through setup" because the two answer different first-run questions: how do I
    /// start, versus what am I aiming at.
    /// </summary>
    [RelayCommand] private void ShowWorkingSetup() => _nav.EnterTour();

    // Quick actions / links.
    [RelayCommand] private void NewWorkspace() => _nav.NewWorkspace();
    [RelayCommand] private void AddRepo() => _nav.AddRepo();

    // Journey-rail tiles navigate to their page.
    [RelayCommand] private void GoToRepos() => _nav.GoToRepos();
    [RelayCommand] private void GoToStacks() => _nav.GoToStacks();
    [RelayCommand] private void GoToWorkspaces() => _nav.GoToWorkspaces();
}
