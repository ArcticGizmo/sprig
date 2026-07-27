using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Sprig.App.ViewModels;

/// <summary>
/// One coachmark: what to point at, what to say, and either a Next button or a real thing to do.
///
/// The delegates are self-contained closures (built by a script factory that captures the navigator and
/// services), so the runner just invokes them — it doesn't thread app state through every step.
/// </summary>
/// <param name="Anchor">Anchor id (see <c>Coach.Anchors</c>) of the element to highlight.</param>
/// <param name="Heading">The claim.</param>
/// <param name="Body">The explanation — about the concept, not about where the control is.</param>
public sealed record CoachMark(string Anchor, string Heading, string Body)
{
    /// <summary>Preferred callout side. The view flips it when there isn't room.</summary>
    public CoachSide Side { get; init; } = CoachSide.Below;

    /// <summary>
    /// Put the app in the state where the anchor exists — navigate, open an overlay, select a row. Coachmarks
    /// point at things that live inside overlays, so a step that assumes its target is already on screen
    /// would silently point at nothing; this makes the precondition explicit and testable.
    /// </summary>
    public Func<Task>? Prepare { get; init; }

    /// <summary>
    /// When set, this step <b>waits</b> for the user to do the thing rather than offering a Next button: it's
    /// re-checked on every store change, and the step advances itself once true. A guide hand-holds by making
    /// the user perform each action, so most of its steps are waiting steps. Null means a plain explanation
    /// that advances on Next.
    /// </summary>
    public Func<bool>? Completed { get; init; }

    /// <summary>
    /// The escape hatch for a waiting step: performs the action for the user so nobody is ever trapped by a
    /// control they can't find. Required whenever <see cref="Completed"/> is set. Its store mutation drives
    /// the same wait the user's own action would, so the two routes can't diverge.
    /// </summary>
    public Func<Task>? ShowMe { get; init; }

    /// <summary>True for a waiting step (one the user must complete), false for a plain explanation.</summary>
    public bool IsWaiting => Completed is not null;
}

/// <summary>Which side of the highlighted element the callout prefers to sit on.</summary>
public enum CoachSide { Below, Above, Right, Left }

/// <summary>
/// Drives a coachmark run: the current mark, stepping, waiting for the user to act, and the "couldn't find
/// it" state. Used both by the tour spike (all plain steps) and by the guides (mostly waiting steps).
///
/// The view model knows nothing about screen geometry — resolving an anchor to a rectangle needs the visual
/// tree, so that lives in the view. What lives here is the script, the position in it, the app-state
/// preconditions each step needs, and the wait: on every store change a waiting step re-checks whether the
/// user has done the thing, and advances itself when they have.
/// </summary>
public partial class CoachViewModel : ViewModelBase
{
    readonly AppServices _services;
    IReadOnlyList<CoachMark> _marks = [];
    Action? _onFinished;
    bool _subscribed;

    /// <param name="services">Only for its <c>StoreChanged</c> event — the trigger to re-check a waiting
    /// step. The steps themselves close over whatever state they need.</param>
    public CoachViewModel(AppServices services) => _services = services;

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private int _index;
    [ObservableProperty] private bool _busy;

    /// <summary>
    /// Set by the view when the current mark's anchor can't be found. Surfaced rather than swallowed: a
    /// coachmark pointing at nothing is a bug, and it should be visible, not silent.
    /// </summary>
    [ObservableProperty] private bool _anchorMissing;

    partial void OnIndexChanged(int value) => RaiseMarkProperties();

    /// <summary>
    /// Announce everything derived from the current position. Called from both the index setter and
    /// <see cref="StartAsync"/> — the first mark of a script lands on index 0, which is usually not a
    /// *change*, so relying on the setter alone leaves the callout bound to a stale (or null) mark.
    /// </summary>
    void RaiseMarkProperties()
    {
        OnPropertyChanged(nameof(Mark));
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(StepCounter));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(IsLast));
        OnPropertyChanged(nameof(IsWaiting));
        OnPropertyChanged(nameof(NextLabel));
    }

    public CoachMark? Mark => _marks.Count == 0 ? null : _marks[Index];
    public int Count => _marks.Count;
    public string StepCounter => $"{Index + 1} of {Count}";
    public bool CanGoBack => Index > 0;
    public bool IsLast => Index == Count - 1;

    /// <summary>True when the current step is waiting for the user to act (no Next button).</summary>
    public bool IsWaiting => Mark?.IsWaiting ?? false;

    public string NextLabel => IsLast ? "Done" : "Next  →";

    /// <summary>Raised when the current mark changes, so the view re-resolves and repositions.</summary>
    public event Action? MarkChanged;

    /// <summary>
    /// Load a script and show its first mark.
    /// </summary>
    /// <param name="marks">The steps to run.</param>
    /// <param name="onFinished">Invoked only if the user reaches the end (the last step's "Done"), never on
    /// skip — so a caller can record a guide as completed without also recording an abandoned one.</param>
    public async Task StartAsync(IReadOnlyList<CoachMark> marks, Action? onFinished = null)
    {
        if (marks.Count == 0) return;

        _marks = marks;
        _onFinished = onFinished;
        Index = 0;
        RaiseMarkProperties();
        IsActive = true;
        Subscribe();
        await PrepareCurrentAsync();
    }

    [RelayCommand]
    private async Task Next()
    {
        if (IsLast) { Finish(); return; }
        Index++;
        await PrepareCurrentAsync();
    }

    [RelayCommand]
    private async Task Back()
    {
        if (!CanGoBack) return;
        Index--;
        await PrepareCurrentAsync();
    }

    /// <summary>Do the waiting step's action for the user, so a control they can't find never traps them.</summary>
    [RelayCommand]
    private async Task ShowMe()
    {
        if (Mark is not { ShowMe: { } showMe }) return;
        Busy = true;
        try { await showMe(); }
        finally { Busy = false; }
        // The action mutates the store, which fires StoreChanged, which advances the wait — same path as
        // the user doing it by hand. No explicit advance here, so the two routes can't diverge.
    }

    [RelayCommand]
    private void Skip() => Stop();

    void Finish()
    {
        var done = _onFinished;
        Stop();
        done?.Invoke();
    }

    void Stop()
    {
        Unsubscribe();
        IsActive = false;
        AnchorMissing = false;
    }

    void Subscribe()
    {
        if (_subscribed) return;
        _services.StoreChanged += OnStoreChanged;
        _subscribed = true;
    }

    void Unsubscribe()
    {
        if (!_subscribed) return;
        _services.StoreChanged -= OnStoreChanged;
        _subscribed = false;
    }

    /// <summary>
    /// A store mutation may be the thing the current step was waiting for. Re-check and advance if so.
    /// <c>StoreChanged</c> is raised on the UI thread (it fires from a command continuation after the
    /// background Core call), the same assumption <c>HomeViewModel</c> already relies on.
    /// </summary>
    void OnStoreChanged() => AdvanceIfSatisfied();

    void AdvanceIfSatisfied()
    {
        if (!IsActive || Mark is not { Completed: { } completed }) return;
        if (!completed()) return;
        _ = NextCommand.ExecuteAsync(null);
    }

    /// <summary>Run the current mark's precondition, then tell the view to re-resolve its anchor.</summary>
    async Task PrepareCurrentAsync()
    {
        if (Mark is not { } mark) return;

        AnchorMissing = false;
        Busy = true;
        try { if (mark.Prepare is { } prepare) await prepare(); }
        finally { Busy = false; }

        MarkChanged?.Invoke();

        // The user may have already satisfied a waiting step before reaching it (e.g. stepping back and
        // forward). Don't make them undo it — advance straight away if it's already true.
        if (mark.IsWaiting) AdvanceIfSatisfied();
    }
}
