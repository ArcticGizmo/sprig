using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Sprig.App.Controls;

/// <summary>
/// A small line of how a figure moved, oldest at the left and now at the right. Points come already scaled to 0..1, so
/// the page decides what the height means. A short gap (a container measured less often than the step) is bridged; a
/// longer one (nothing running, or nobody measuring) breaks the line.
/// </summary>
public sealed class Sparkline : Control
{
    public static readonly StyledProperty<IReadOnlyList<double?>?> PointsProperty =
        AvaloniaProperty.Register<Sparkline, IReadOnlyList<double?>?>(nameof(Points));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(Stroke));

    /// <summary>Empty slots bridged between two points; more breaks the line.</summary>
    public const int MaxGap = UsageHistory.Bridge;

    public IReadOnlyList<double?>? Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    static Sparkline() => AffectsRender<Sparkline>(PointsProperty, StrokeProperty);

    public override void Render(DrawingContext context)
    {
        if (Points is not { Count: > 1 } points || Stroke is not { } stroke) return;
        var (w, h) = (Bounds.Width, Bounds.Height);
        // Inset by the pen, so the top and bottom of the line aren't clipped.
        const double inset = 1.5;
        Point At(int i, double v) => new(w * i / (points.Count - 1), inset + (h - 2 * inset) * (1 - Math.Clamp(v, 0, 1)));

        var pen = new Pen(stroke, 1.4) { LineJoin = PenLineJoin.Round, LineCap = PenLineCap.Round };
        var geometry = new StreamGeometry();
        Point? last = null;
        using (var g = geometry.Open())
        {
            var open = false;
            var gap = 0;
            for (var i = 0; i < points.Count; i++)
            {
                if (points[i] is not { } v)
                {
                    gap++;
                    continue;
                }
                var p = At(i, v);
                if (open && gap <= MaxGap) g.LineTo(p);
                else
                {
                    if (open) g.EndFigure(false);
                    g.BeginFigure(p, false);
                    // A lone point still needs a length to show.
                    g.LineTo(p + new Vector(0.01, 0));
                    open = true;
                }
                gap = 0;
                last = p;
            }
            if (open) g.EndFigure(false);
        }
        context.DrawGeometry(null, pen, geometry);
        // The latest figure, marked where the line ends, when it's current.
        if (last is { } end && points[^1] is not null) context.DrawEllipse(stroke, null, end, 2, 2);
    }
}
