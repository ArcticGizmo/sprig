using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Sprig.App.ViewModels;

/// <summary>One narrated stop on the guided tour: where to go, and what to say once you're there.</summary>
/// <param name="Kicker">Short all-caps label (what part of the model this stop is about).</param>
/// <param name="Heading">The claim being made.</param>
/// <param name="Hint">The detail that makes the claim land.</param>
/// <param name="Cta">Label for the button that advances.</param>
/// <param name="Go">Navigates to (and populates) the surface this stop describes.</param>
public sealed record TourStop(string Kicker, string Heading, string Hint, string Cta, Action<Navigator> Go)
{
    /// <summary>
    /// Optional work this stop's button does before advancing — currently only "start the containers".
    /// A stop that performs something must be one the tour can also do without (see the Docker gate in
    /// <see cref="TourGuideViewModel.StartAsync"/>): the tour has to stay meaningful offline.
    /// </summary>
    public Func<Navigator, Task>? Perform { get; init; }
}

/// <summary>
/// The guided tour's narration strip: a fixed, ordered script over the already-built sample setup.
///
/// Deliberately separate from <see cref="SetupGuideViewModel"/> despite the similar chrome. That one is
/// a projection of store counts — it advances when the user creates something. This one is a script
/// advanced by an index, over a setup that already exists. Sharing a class would mean every property
/// meaning two things depending on a mode flag, which is the cost this feature is trying not to pay
/// (docs/guided-tour-plan.md §7). They share the strip's <c>wstep</c> styles, which is where restyling
/// actually happens.
///
/// The copy describes <b>concepts and values</b>, never chrome — no "click the third tile" — so
/// rearranging a page can't silently invalidate it.
/// </summary>
public partial class TourGuideViewModel : ViewModelBase
{
    readonly Navigator _nav;
    readonly Func<bool>? _dockerIsRunning;

    // Set by StartAsync once Docker has been probed. Starts as the offline script so the view model is
    // never in an invalid state before then.
    IReadOnlyList<TourStop> _stops;

    /// <param name="nav">Where each stop navigates.</param>
    /// <param name="dockerIsRunning">Probed once, off the UI thread, to decide whether the tour offers to
    /// start containers. Null means never offer it (headless renders and tests).</param>
    public TourGuideViewModel(Navigator nav, Func<bool>? dockerIsRunning = null)
    {
        _nav = nav;
        _dockerIsRunning = dockerIsRunning;
        _stops = Script(includeInfra: false);
    }

    /// <summary>Whether the strip is showing. The tour starts it automatically; Skip hides it.</summary>
    [ObservableProperty] private bool _isActive;

    /// <summary>True while a stop's action runs (starting containers), so its button can't be re-pressed.</summary>
    [ObservableProperty] private bool _busy;

    [ObservableProperty] private int _index;

    partial void OnIndexChanged(int value)
    {
        OnPropertyChanged(nameof(Stop));
        OnPropertyChanged(nameof(StepCounter));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(IsLastStop));
    }

    public TourStop Stop => _stops[Index];
    public int Count => _stops.Count;
    public string StepCounter => $"Step {Index + 1} of {Count}";
    public bool CanGoBack => Index > 0;
    public bool IsLastStop => Index == Count - 1;

    /// <summary>
    /// Probe Docker, pick the script, and go to the first stop. The probe shells out to docker, so it
    /// happens off the UI thread and the infra stop simply isn't in the script when there's no daemon —
    /// a tour must never offer an action that is going to fail.
    /// </summary>
    public async Task StartAsync()
    {
        var docker = _dockerIsRunning is not null && await AppServices.RunAsync(_dockerIsRunning);
        _stops = Script(includeInfra: docker);

        Index = 0;
        OnPropertyChanged(nameof(Stop));
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(StepCounter));
        OnPropertyChanged(nameof(IsLastStop));
        IsActive = true;
        Stop.Go(_nav);
    }

    /// <summary>Do this stop's action if it has one, then advance — or finish on the last stop.</summary>
    [RelayCommand]
    private async Task Next()
    {
        if (Stop.Perform is { } perform)
        {
            Busy = true;
            // A failure here is already surfaced by the page that owns the action (its own error line),
            // so the tour just stops being busy and lets the user move on or retry.
            try { await perform(_nav); }
            finally { Busy = false; }
        }

        if (IsLastStop) { IsActive = false; return; }
        Index++;
        Stop.Go(_nav);
    }

    [RelayCommand]
    private void Back()
    {
        if (!CanGoBack) return;
        Index--;
        Stop.Go(_nav);
    }

    /// <summary>Hide the narration but stay in the tour, so the sample can be explored freely.</summary>
    [RelayCommand] private void Skip() => IsActive = false;

    /// <summary>
    /// The script: stops that walk the one-directional model — a repo declares, a stack supplies, a
    /// workspace materialises — and then hand the user back to their own repos.
    /// </summary>
    /// <param name="includeInfra">Add the optional "start the containers" stop. Everything else stands
    /// alone without it: compose <i>generation</i> is pure file I/O, and that's the lesson.</param>
    static IReadOnlyList<TourStop> Script(bool includeInfra) =>
    [
        new("THE SHAPE OF IT",
            "This is one working sprig, built for you",
            "Two repos, one stack wiring them together, and a workspace running from it. Everything you're about to see is real — the same engine your own repos will use.",
            "Start with the repos  →",
            nav => nav.GoHome()),

        new("STEP 1 · REPOS",
            "A repo only declares what it needs",
            "sample-api asks for a port and a database port. It never says which numbers — that isn't its decision. A repo is a pure consumer, which is why it stays portable.",
            "Who supplies them?  →",
            nav => nav.ShowFirstRepo()),

        new("STEP 2 · STACKS",
            "The stack owns the ports and supplies every value",
            "Three named ports, and each repo's inputs bound to them. Look at sample-web's apiUrl: it's built from api_port — the same port sample-api runs on. One value, two consumers, and neither repo knows about the other.",
            "See what that produced  →",
            nav => nav.ShowFirstStack()),

        new("STEP 3 · WORKSPACES",
            "Real numbers, written into real files",
            "Each repo got its own worktree on a sprig/ branch, and the ports were allocated for this workspace alone. Those resolved values are already in each worktree's .env and in a generated compose file — open a worktree to see them.",
            includeInfra ? "And the infrastructure?  →" : "What about my repos?  →",
            nav => nav.ShowFirstWorkspace()),

        // Optional, and last of the teaching stops: it needs a running daemon and an image pull, so it
        // is only ever offered when Docker is actually up.
        .. includeInfra
            ?
            [
                new TourStop("STEP 4 · INFRA",
                    "Its database gets a port of its own too",
                    "sample-api's compose file declares one Postgres on 5432. sprig generated an isolated copy for this workspace with the container name and host port rewritten, so another workspace can run the same database at the same time. Starting it now proves the point — the first run pulls a small image.",
                    "Start the containers  →",
                    nav => nav.ShowFirstWorkspace())
                {
                    Perform = nav => nav.StartFirstWorkspaceInfra(),
                },
            ]
            : (TourStop[])[],

        new("YOUR TURN",
            "Now point sprig at something of yours",
            "Your repo needs one committed file — a .sprig.json declaring what it consumes — and sprig writes it for you when you add the repo. Leave the tour whenever you like; the sample is deleted, and nothing of yours was touched.",
            "Done",
            nav => nav.GoHome()),
    ];
}
