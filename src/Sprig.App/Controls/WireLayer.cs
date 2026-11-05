using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Sprig.App.ViewModels;

namespace Sprig.App.Controls;

/// <summary>
/// Draws the trellis picture's wires behind its boxes: a curve from what offers a value to the app that needs it, with
/// an arrowhead and the import's name, or a dashed stub for a need nothing fills.
/// </summary>
public sealed class WireLayer : Control
{
    public static readonly StyledProperty<IReadOnlyList<GraphWire>?> WiresProperty =
        AvaloniaProperty.Register<WireLayer, IReadOnlyList<GraphWire>?>(nameof(Wires));

    public IReadOnlyList<GraphWire>? Wires
    {
        get => GetValue(WiresProperty);
        set => SetValue(WiresProperty, value);
    }

    static WireLayer() => AffectsRender<WireLayer>(WiresProperty);

    static readonly IBrush Line = new SolidColorBrush(Color.Parse("#60A5FA"));
    static readonly IBrush Strong = new SolidColorBrush(Color.Parse("#BFDBFE"));
    static readonly IBrush Unfilled = new SolidColorBrush(Color.Parse("#FBBF24"));
    static readonly IBrush LabelBack = new SolidColorBrush(Color.Parse("#181820"));
    static readonly IBrush LabelBorder = new SolidColorBrush(Color.Parse("#2D2D3C"));
    static readonly Typeface Mono = new("Cascadia Mono,Consolas,monospace");

    public override void Render(DrawingContext context)
    {
        if (Wires is not { } wires) return;
        // Highlighted wires last, so they sit on top of the rest.
        var ordered = new List<GraphWire>(wires);
        ordered.Sort((a, b) => a.Highlighted.CompareTo(b.Highlighted));
        var labels = new List<Rect>();
        foreach (var wire in ordered) Draw(context, wire, labels);
    }

    /// <summary>Moves a label down past any already placed, so two wires that cross keep both names readable.</summary>
    static Rect Clear(Rect box, List<Rect> placed)
    {
        for (var tries = 0; tries < 8 && placed.Exists(p => p.Intersects(box)); tries++)
            box = box.Translate(new Vector(0, box.Height + 2));
        placed.Add(box);
        return box;
    }

    static void Draw(DrawingContext context, GraphWire wire, List<Rect> labels)
    {
        var brush = !wire.Filled ? Unfilled : wire.Highlighted ? Strong : Line;
        var opacity = wire.Dimmed ? 0.18 : wire.Highlighted ? 1.0 : 0.75;
        var pen = new Pen(brush, wire.Highlighted ? 2.4 : 1.6, wire.Filled ? null : new DashStyle([4, 3], 0));
        using var _ = context.PushOpacity(opacity);

        var start = new Point(wire.X1, wire.Y1);
        var end = new Point(wire.X2 - 2, wire.Y2);
        var bend = Math.Max(40, Math.Abs(end.X - start.X) / 2);
        var c1 = new Point(start.X + bend, start.Y);
        var c2 = new Point(end.X - bend, end.Y);
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(start, false);
            g.CubicBezierTo(c1, c2, end);
            g.EndFigure(false);
        }
        context.DrawGeometry(null, pen, geometry);

        var head = new StreamGeometry();
        using (var g = head.Open())
        {
            g.BeginFigure(new Point(end.X - 8, end.Y - 4.5), true);
            g.LineTo(end);
            g.LineTo(new Point(end.X - 8, end.Y + 4.5));
            g.EndFigure(true);
        }
        context.DrawGeometry(brush, null, head);

        if (wire.Label is not { } label) return;
        // The curve's midpoint (t = 0.5 of the cubic), where the label sits.
        var mid = new Point(
            0.125 * start.X + 0.375 * c1.X + 0.375 * c2.X + 0.125 * end.X,
            0.125 * start.Y + 0.375 * c1.Y + 0.375 * c2.Y + 0.125 * end.Y);
        var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Mono, 11, wire.Filled ? Strong : Unfilled);
        var width = text.Width + 14;
        // A stub's label sits before it, where its source would be.
        var left = wire.Filled ? mid.X - width / 2 : start.X - width - 4;
        var box = Clear(new Rect(left, mid.Y - text.Height / 2 - 2, width, text.Height + 4), labels);
        context.DrawRectangle(LabelBack, new Pen(LabelBorder, 1), box, 9, 9);
        context.DrawText(text, new Point(box.X + 7, box.Y + 2));
    }
}
