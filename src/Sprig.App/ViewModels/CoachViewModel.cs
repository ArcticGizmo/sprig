using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Sprig.App.ViewModels;

/// <summary>
/// One coachmark: what to point at, what to say, and how to make the target exist.
/// </summary>
/// <param name="Anchor">Anchor id (see <c>Coach.Anchors</c>) of the element to highlight.</param>
/// <param name="Heading">The claim.</param>
/// <param name="Body">The explanation — about the concept, not about where the control is.</param>
/// <param name="Prepare">
/// Puts the app in the state where the anchor exists — navigates, opens the overlay, selects a row.
/// Coachmarks point at fields that live inside overlays, so a step that assumes the target is already on
/// screen would silently point at nothing; this makes the precondition explicit and testable.
/// </param>
public sealed record CoachMark(string Anchor, string Heading, string Body, Func<Navigator, Task> Prepare)
{
    /// <summary>Preferred callout side. The view flips it when there isn't room.</summary>
    public CoachSide Side { get; init; } = CoachSide.Below;
}

/// <summary>Which side of the highlighted element the callout prefers to sit on.</summary>
public enum CoachSide { Below, Above, Right, Left }

/// <summary>
/// Drives the coachmark walkthrough: the current mark, stepping, and the "couldn't find it" state.
///
/// The view model knows nothing about screen geometry — resolving an anchor to a rectangle needs the visual
/// tree, so that lives in the view. What lives here is the script, the position in it, and the app-state
/// preconditions each step needs.
/// </summary>
public partial class CoachViewModel : ViewModelBase
{
    readonly Navigator _nav;
    IReadOnlyList<CoachMark> _marks = [];

    public CoachViewModel(Navigator nav) => _nav = nav;

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
        OnPropertyChanged(nameof(NextLabel));
    }

    public CoachMark? Mark => _marks.Count == 0 ? null : _marks[Index];
    public int Count => _marks.Count;
    public string StepCounter => $"{Index + 1} of {Count}";
    public bool CanGoBack => Index > 0;
    public bool IsLast => Index == Count - 1;
    public string NextLabel => IsLast ? "Done" : "Next  →";

    /// <summary>Raised when the current mark changes, so the view re-resolves and repositions.</summary>
    public event Action? MarkChanged;

    /// <summary>Load a script and show its first mark.</summary>
    public async Task StartAsync(IReadOnlyList<CoachMark> marks)
    {
        if (marks.Count == 0) return;

        _marks = marks;
        Index = 0;
        RaiseMarkProperties();
        IsActive = true;
        await PrepareCurrentAsync();
    }

    [RelayCommand]
    private async Task Next()
    {
        if (IsLast) { Stop(); return; }
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

    [RelayCommand]
    private void Skip() => Stop();

    void Stop()
    {
        IsActive = false;
        AnchorMissing = false;
    }

    /// <summary>Run the current mark's precondition, then tell the view to re-resolve its anchor.</summary>
    async Task PrepareCurrentAsync()
    {
        if (Mark is not { } mark) return;

        AnchorMissing = false;
        Busy = true;
        try { await mark.Prepare(_nav); }
        finally { Busy = false; }

        MarkChanged?.Invoke();
    }
}
