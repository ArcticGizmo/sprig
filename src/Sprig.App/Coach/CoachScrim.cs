using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering;

namespace Sprig.App.Coach;

/// <summary>
/// The dimming layer with a hole cut out around the coached element, plus a highlight ring on the hole.
///
/// Drawn rather than composed because a cut-out isn't expressible as a control tree. It swallows input
/// (<see cref="ICustomHitTest"/> over the whole surface) so a coached step can't be clicked past — except
/// inside the hole, which stays live so the user can actually do the thing being explained.
/// </summary>
public sealed class CoachScrim : Control, ICustomHitTest
{
    public static readonly StyledProperty<Rect> HoleProperty =
        AvaloniaProperty.Register<CoachScrim, Rect>(nameof(Hole));

    /// <summary>The element being coached, in this control's coordinates. Empty dims everything.</summary>
    public Rect Hole
    {
        get => GetValue(HoleProperty);
        set => SetValue(HoleProperty, value);
    }

    /// <summary>
    /// Whether the spotlit control can be clicked. True for a waiting step (the user must operate the
    /// highlighted control to advance); false for an explanation step, where the spotlight only directs the
    /// eye and every click outside the callout is swallowed — so a narrated step can't be clicked away from.
    /// </summary>
    public bool Interactive { get; set; }

    static CoachScrim()
    {
        AffectsRender<CoachScrim>(HoleProperty);
    }

    /// <summary>
    /// Block clicks so a walkthrough can't be navigated away from mid-step. The callout buttons sit above the
    /// scrim and always work. When the step is interactive, the hole is a pass-through so the user can
    /// operate the highlighted control; otherwise everything under the scrim is blocked.
    /// </summary>
    public bool HitTest(Point point) => !(Interactive && Hole.Inflate(Padding).Contains(point));

    const double Padding = 6;
    const double Radius = 8;

    static readonly IBrush Dim = new SolidColorBrush(Color.FromArgb(0xB0, 0x07, 0x0A, 0x12));
    static readonly IPen Ring = new Pen(new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA)), 2);

    public override void Render(DrawingContext ctx)
    {
        var full = new Rect(Bounds.Size);
        if (full.Width <= 0 || full.Height <= 0) return;

        if (Hole is { Width: <= 0 } or { Height: <= 0 })
        {
            ctx.FillRectangle(Dim, full);
            return;
        }

        var hole = Hole.Inflate(Padding).Intersect(full);

        // Dim everything except the hole. Four bands rather than a combined geometry: the same result with
        // no reliance on geometry-combination behaviour, and trivial to reason about.
        ctx.FillRectangle(Dim, new Rect(full.X, full.Y, full.Width, hole.Y - full.Y));
        ctx.FillRectangle(Dim, new Rect(full.X, hole.Bottom, full.Width, full.Bottom - hole.Bottom));
        ctx.FillRectangle(Dim, new Rect(full.X, hole.Y, hole.X - full.X, hole.Height));
        ctx.FillRectangle(Dim, new Rect(hole.Right, hole.Y, full.Right - hole.Right, hole.Height));

        ctx.DrawRectangle(null, Ring, hole, Radius, Radius);
    }
}
