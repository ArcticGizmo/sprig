using System;
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

    /// <summary>Pick a callout origin on the requested side, flipping or clamping when it won't fit.</summary>
    Point Beside(Rect hole, CoachSide side, Size size)
    {
        Callout.Measure(size);
        var want = Callout.DesiredSize;

        var (x, y) = side switch
        {
            CoachSide.Above => (hole.Center.X - want.Width / 2, hole.Y - want.Height - Gap),
            CoachSide.Right => (hole.Right + Gap, hole.Center.Y - want.Height / 2),
            CoachSide.Left => (hole.X - want.Width - Gap, hole.Center.Y - want.Height / 2),
            _ => (hole.Center.X - want.Width / 2, hole.Bottom + Gap),
        };

        // Flip to the opposite side when the preferred one overflows, then clamp so the callout is always
        // fully on screen — a coachmark half off the window edge is worse than one on the "wrong" side.
        if (side is CoachSide.Below && y + want.Height > size.Height) y = hole.Y - want.Height - Gap;
        else if (side is CoachSide.Above && y < 0) y = hole.Bottom + Gap;
        else if (side is CoachSide.Right && x + want.Width > size.Width) x = hole.X - want.Width - Gap;
        else if (side is CoachSide.Left && x < 0) x = hole.Right + Gap;

        return new Point(
            Math.Clamp(x, Gap, Math.Max(Gap, size.Width - want.Width - Gap)),
            Math.Clamp(y, Gap, Math.Max(Gap, size.Height - want.Height - Gap)));
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
