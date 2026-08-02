using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Sprig.App.ViewModels;

namespace Sprig.App.Coach;

/// <summary>
/// The coachmark layer: resolves the current mark's anchor to a rectangle, cuts the scrim around it, and
/// parks the callout beside it.
///
/// Geometry lives here rather than in the view model because it needs the visual tree. The layer is a
/// sibling of the whole app content, so it can highlight anything — nav item, page control, or a shape drawn
/// inside the wiring canvas — without any of them knowing it exists.
/// </summary>
public partial class CoachOverlay : UserControl
{
    CoachViewModel? _hooked;
    Visual? _anchorRoot;

    /// <summary>The visual anchors are resolved against — the window content. Set by the host.</summary>
    public Visual? AnchorRoot
    {
        get => _anchorRoot;
        set { _anchorRoot = value; Refresh(); }
    }

    public CoachOverlay()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Rehook();
        // The anchor moves when the window resizes or content reflows, so re-resolve on any size change.
        Layer.SizeChanged += (_, _) => Refresh();
    }

    const double Gap = 14;

    void Rehook()
    {
        if (_hooked is not null) _hooked.MarkChanged -= OnMarkChanged;
        _hooked = DataContext as CoachViewModel;
        if (_hooked is not null) _hooked.MarkChanged += OnMarkChanged;
        Refresh();
    }

    void OnMarkChanged() => Refresh();

    /// <summary>
    /// Re-resolve and reposition. Deferred to the next dispatcher pass: a step usually navigates or opens an
    /// overlay first, and the target doesn't exist until that layout has happened.
    /// </summary>
    void Refresh() => Dispatcher.UIThread.Post(Reposition, DispatcherPriority.Loaded);

    void Reposition()
    {
        if (DataContext is not CoachViewModel vm || !vm.IsActive || vm.Mark is not { } mark) return;
        if (AnchorRoot is null) return;

        var size = Layer.Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) return;

        Scrim.Width = size.Width;
        Scrim.Height = size.Height;
        // A waiting step needs the user to operate the highlighted control; an explanation step is
        // eye-direction only and swallows every click but the callout's, so it can't be clicked away from.
        Scrim.Interactive = mark.IsWaiting;

        // A whole-page step (no anchor) deliberately dims everything and centres the callout — an
        // opening/closing beat that isn't about one control. Not a failure, so no warning.
        if (mark.Anchor is null)
        {
            vm.AnchorMissing = false;
            Scrim.Hole = default;
            Place(Centre(size));
            return;
        }

        if (!AnchorResolver.TryResolve(AnchorRoot, mark.Anchor, out var target))
        {
            // Nothing to point at, but a step said there should be: dim everything, centre the callout, and
            // flag it rather than pointing at an arbitrary corner. The tests turn this state into a failure.
            vm.AnchorMissing = true;
            Scrim.Hole = default;
            Place(Centre(size));
            return;
        }

        vm.AnchorMissing = false;
        // The resolver works in AnchorRoot's coordinates; this layer may sit at a different origin.
        Scrim.Hole = AnchorRoot.TransformToVisual(Layer) is { } toLayer
            ? target.TransformToAABB(toLayer)
            : target;

        Place(Beside(Scrim.Hole, mark.Side, size));
    }

    /// <summary>
    /// Pick a callout origin that sits <b>beside</b> the highlighted element without ever covering it.
    ///
    /// The mark's <see cref="CoachSide"/> is a preference, not a command: it's honoured when the callout
    /// fits in that side's gap, otherwise the side with the most room that fits is chosen. This is the fix
    /// for a wide or tall anchor (a whole card, a full-width inputs strip): the old code parked on the
    /// requested side and then <i>clamped</i> the callout back on screen — straight over the thing it was
    /// explaining. Choosing a side that actually fits means the callout is flush against the hole with a
    /// gap, so it can't overlap it. Only when no side has room (an anchor that nearly fills the viewport)
    /// does it fall back to the roomiest side and accept the unavoidable overlap.
    /// </summary>
    Point Beside(Rect hole, CoachSide side, Size size)
    {
        Callout.Measure(size);
        var want = Callout.DesiredSize;

        // Room between the hole and each viewport edge — the space a callout on that side has to live in.
        double RoomOn(CoachSide s) => s switch
        {
            CoachSide.Above => hole.Y,
            CoachSide.Left => hole.X,
            CoachSide.Right => size.Width - hole.Right,
            _ => size.Height - hole.Bottom,   // Below
        };

        // The side fits when its room holds the callout's relevant dimension plus a gap to the hole and a
        // gap to the viewport edge — i.e. the callout can sit entirely outside the hole on that side.
        bool Fits(CoachSide s) =>
            RoomOn(s) >= (s is CoachSide.Above or CoachSide.Below ? want.Height : want.Width) + 2 * Gap;

        // Origin for a side: flush against the hole (with a gap), centred on the hole along the other axis
        // and clamped into the viewport. A fitting side has room, so this centre-clamp on the parallel axis
        // can never push the callout back over the hole.
        double CentreX() => Math.Clamp(hole.Center.X - want.Width / 2, Gap, Math.Max(Gap, size.Width - want.Width - Gap));
        double CentreY() => Math.Clamp(hole.Center.Y - want.Height / 2, Gap, Math.Max(Gap, size.Height - want.Height - Gap));
        Point Origin(CoachSide s) => s switch
        {
            CoachSide.Above => new(CentreX(), hole.Y - want.Height - Gap),
            CoachSide.Left => new(hole.X - want.Width - Gap, CentreY()),
            CoachSide.Right => new(hole.Right + Gap, CentreY()),
            _ => new(CentreX(), hole.Bottom + Gap),   // Below
        };

        // Try the requested side first, then the rest by how much room they have; take the first that fits.
        CoachSide[] all = [CoachSide.Above, CoachSide.Below, CoachSide.Left, CoachSide.Right];
        var order = new[] { side }.Concat(all.Where(s => s != side).OrderByDescending(RoomOn));
        foreach (var s in order)
            if (Fits(s)) return Origin(s);

        // No side has room (the anchor nearly fills the viewport): use the roomiest and clamp fully on
        // screen. Overlap is unavoidable here — this just keeps the callout from running off the edge.
        var p = Origin(all.OrderByDescending(RoomOn).First());
        return new Point(
            Math.Clamp(p.X, Gap, Math.Max(Gap, size.Width - want.Width - Gap)),
            Math.Clamp(p.Y, Gap, Math.Max(Gap, size.Height - want.Height - Gap)));
    }

    Point Centre(Size size)
    {
        Callout.Measure(size);
        var want = Callout.DesiredSize;
        return new Point((size.Width - want.Width) / 2, (size.Height - want.Height) / 2);
    }

    void Place(Point at)
    {
        Canvas.SetLeft(Callout, at.X);
        Canvas.SetTop(Callout, at.Y);
    }
}
