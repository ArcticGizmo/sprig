using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprig.App.Updates;

namespace Sprig.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    /// <summary>Owns the window and the store swap; null in headless renders and VM tests.</summary>
    readonly AppSession? _session;

    /// <summary>The store this window is bound to — the demo store during a tour or guide.</summary>
    readonly AppServices _services;

    /// <summary>Navigation + coachmark preconditions for scripts built at runtime.</summary>
    readonly Navigator _nav;

    /// <summary>The navigable pages, in workflow order: Home, then Repos, Stacks, Workspaces.</summary>
    public IReadOnlyList<PageViewModel> Pages { get; }

    /// <summary>Left-nav rows: page entries interleaved with section headers ("Set up" / "Run").</summary>
    public IReadOnlyList<object> NavItems { get; }

    /// <summary>The guided "Set up sprig" strip (opt-in from Home).</summary>
    public SetupGuideViewModel Guide { get; }

    /// <summary>The coachmark layer: highlights one element at a time and explains it. Drives both the tour
    /// and the guides — every walkthrough dims the page and rings its target.</summary>
    public CoachViewModel Coach { get; }

    /// <summary>Probes whether a Docker daemon is up, to gate the tour's optional infra step.</summary>
    readonly Func<bool> _dockerIsRunning;

    /// <summary>The coachmark spike's three marks, bound to this window's navigator (headless render uses it).</summary>
    internal IReadOnlyList<CoachMark> CoachSpikeMarks => Sprig.App.Coach.CoachSpikeScript.Marks(_nav);

    /// <summary>
    /// Start a guide's coachmarks over the (already prepared) demo store. Called by <see cref="AppSession"/>
    /// after it has reset the sandbox and bound this window. <paramref name="onFinished"/> fires only if the
    /// user completes the guide, so completion is recorded but abandonment isn't.
    /// </summary>
    public Task StartGuide(Sprig.App.Coach.Guide guide, System.Action onFinished)
        => Coach.StartAsync(guide.Build(_nav, _services), onFinished);

    /// <summary>Run the coachmark spike — three marks proving the mechanism against its three anchor cases.</summary>
    [RelayCommand]
    private Task StartCoachSpike() => Coach.StartAsync(CoachSpikeMarks);

    /// <summary>The Settings page — pinned to the bottom of the nav, outside the workflow sequence.</summary>
    public SettingsViewModel Settings { get; }

    /// <summary>The Learn page — the library of guided lessons.</summary>
    public LearnViewModel Learn { get; }

    /// <summary>The About page — pinned to the bottom of the nav, outside the workflow sequence.</summary>
    public AboutViewModel About { get; }

    [ObservableProperty]
    private PageViewModel _currentPage;

    /// <summary>Non-null when a newer version is available; drives the top notification bar.</summary>
    [ObservableProperty]
    private string? _updateNotice;

    /// <summary>True when this is an isolated dev instance — drives the pink "- DEV" nav badge.</summary>
    public bool IsDevInstance => Sprig.Core.Store.AppProfile.IsDev;

    /// <summary>
    /// True when the app is showing the guided tour's throwaway sample rather than the user's real
    /// store — drives the amber banner and swaps the nav's tour entry for an exit.
    /// </summary>
    public bool IsTour { get; }

    /// <summary>Raised when an operation wants its progress checklist shown in its own window.</summary>
    public event Action<OperationProgressViewModel>? OperationStarted;

    /// <summary>Window title for a session: the product name, the dev badge, and the tour marker.</summary>
    public static string TitleFor(AppServices services)
        => "Sprig" + Sprig.Core.Store.AppProfile.DisplaySuffix + (services.IsDemoStore ? " — Guided tour" : "");

    /// <param name="dockerIsRunning">Overrides the Docker probe the tour uses to decide whether to offer
    /// starting containers. Tests supply it so the script is deterministic on any machine; production
    /// leaves it null and the real daemon is asked.</param>
    public MainWindowViewModel(AppServices services, AppSession? session = null, Func<bool>? dockerIsRunning = null)
    {
        _session = session;
        _services = services;
        IsTour = services.IsDemoStore;
        var nav = new Navigator();
        _nav = nav;
        var repos = new ReposViewModel(services);
        var stacks = new StacksViewModel(services, nav);
        var workspaces = new WorkspacesViewModel(services, nav);
        var home = new HomeViewModel(services, nav);
        // Settings is built here (rather than lower down) so the navigator can reach it — coachmark
        // preconditions navigate to it.
        Settings = new SettingsViewModel(services);
        nav.Configure(Navigate, home, repos, stacks, workspaces, Settings);

        Guide = new SetupGuideViewModel(services, nav);
        nav.SetGuideLauncher(Guide.Start);
        nav.SetTourLauncher(() => EnterTourCommand.Execute(null));
        nav.SetGuideEntry(guide => EnterGuideCommand.Execute(guide));

        Learn = new LearnViewModel(services, nav);
        About = new AboutViewModel();

        // Settings + About are navigable (so they get active-state highlighting) but live in the
        // bottom nav slot rather than the workflow list, so they're not in NavItems.
        Pages = [home, repos, stacks, workspaces, Learn, Settings, About];
        NavItems =
        [
            home,
            new NavHeaderViewModel("Set up"),
            repos,
            stacks,
            new NavHeaderViewModel("Run"),
            workspaces,
            new NavHeaderViewModel("Learn"),
            Learn,
        ];

        // The tour offers to start containers only when a daemon is actually up; the probe shells out to
        // docker, so it's stored as a func to run off the UI thread when the tour starts.
        _dockerIsRunning = dockerIsRunning ?? services.Docker.IsEngineRunning;
        Coach = new CoachViewModel(services);

        // Land on Home (the front door), not on the last step of the pipeline.
        _currentPage = home;
        home.IsActive = true;

        // The tour narration is NOT auto-started here, because a guide runs in the same demo store and must
        // not also show the tour script. AppSession starts whichever one it entered.
        _ = CheckForUpdatesAsync();
    }

    /// <summary>
    /// Begin the guided tour as coachmarks. Called by <see cref="AppSession"/> for the tour only. Probes
    /// Docker off the UI thread first, so the optional infra step is only in the script when a daemon is up.
    /// </summary>
    internal async Task StartTour()
    {
        var dockerUp = await AppServices.RunAsync(_dockerIsRunning);
        await Coach.StartAsync(Sprig.App.Coach.TourScript.Marks(_nav, dockerUp));
    }

    [RelayCommand]
    private void Navigate(PageViewModel page) => CurrentPage = page;

    [RelayCommand]
    private void DismissUpdateNotice() => UpdateNotice = null;

    /// <summary>
    /// Show a complete, working setup by entering the guided tour. Builds the sample if it isn't
    /// there yet, then rebinds the window to the demo store — the real store is left untouched.
    /// </summary>
    [RelayCommand]
    private async Task EnterTour()
    {
        if (_session is null) return;
        await _session.EnterTourAsync(modal => OperationStarted?.Invoke(modal));
    }

    /// <summary>Leave the tour, delete the sample, and go back to the real store.</summary>
    [RelayCommand]
    private async Task ExitTour()
    {
        if (_session is null) return;
        await _session.ExitTourAsync(deleteSample: true);
    }

    /// <summary>Leave the tour but keep the sample on disk, so re-entering is instant.</summary>
    [RelayCommand]
    private async Task ExitTourKeepingSample()
    {
        if (_session is null) return;
        await _session.ExitTourAsync(deleteSample: false);
    }

    /// <summary>Start a guided lesson — resets the sandbox to its stage, then hand-holds the user through it.</summary>
    [RelayCommand]
    private async Task EnterGuide(Sprig.App.Coach.Guide? guide)
    {
        if (_session is null || guide is null) return;
        await _session.EnterGuideAsync(guide, modal => OperationStarted?.Invoke(modal));
    }

    partial void OnCurrentPageChanged(PageViewModel value)
    {
        foreach (var page in Pages)
            page.IsActive = ReferenceEquals(page, value);
    }

    async Task CheckForUpdatesAsync() => UpdateNotice = await UpdateChecker.CheckAsync();
}

/// <summary>A non-interactive section label in the left nav (e.g. "Set up", "Run").</summary>
public sealed class NavHeaderViewModel(string label)
{
    public string Label { get; } = label;
}
