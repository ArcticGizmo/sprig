using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Sprig.Core.Graph;

namespace Sprig.App.Controls;

/// <summary>Draws one row's slice of the commit graph: the lane lines crossing this row and the commit dot,
/// GitKraken-style. Each row draws itself and fills its (content-sized) cell, so rows can be any height —
/// e.g. a wrapped, multi-line message — and dots still line up with the first (message) line.</summary>
public sealed class RowGraphControl : Control
{
    public const double DotLineCenter = 17; // dot y: centred on the row's first line (matches the message line)
    const double LaneWidth = 20;
    const double DotRadius = 7;
    const double Pad = 12;

    static readonly Color[] Palette =
    [
        Color.Parse("#4C9AFF"), Color.Parse("#F2994A"), Color.Parse("#27AE60"),
        Color.Parse("#BB6BD9"), Color.Parse("#EB5757"), Color.Parse("#F2C94C"),
    ];

    public static readonly StyledProperty<GraphRowRender?> CellProperty =
        AvaloniaProperty.Register<RowGraphControl, GraphRowRender?>(nameof(Cell));
    public static readonly StyledProperty<string?> NodeShaProperty =
        AvaloniaProperty.Register<RowGraphControl, string?>(nameof(NodeSha));
    public static readonly StyledProperty<string?> CurrentShaProperty =
        AvaloniaProperty.Register<RowGraphControl, string?>(nameof(CurrentSha));
    public static readonly StyledProperty<string?> SelectedShaProperty =
        AvaloniaProperty.Register<RowGraphControl, string?>(nameof(SelectedSha));

    public GraphRowRender? Cell { get => GetValue(CellProperty); set => SetValue(CellProperty, value); }
    public string? NodeSha { get => GetValue(NodeShaProperty); set => SetValue(NodeShaProperty, value); }
    public string? CurrentSha { get => GetValue(CurrentShaProperty); set => SetValue(CurrentShaProperty, value); }
    public string? SelectedSha { get => GetValue(SelectedShaProperty); set => SetValue(SelectedShaProperty, value); }

    static RowGraphControl()
    {
        AffectsRender<RowGraphControl>(CellProperty, NodeShaProperty, CurrentShaProperty, SelectedShaProperty);
        AffectsMeasure<RowGraphControl>(CellProperty);
    }

    static IBrush LaneBrush(int lane) => new SolidColorBrush(Palette[((lane % Palette.Length) + Palette.Length) % Palette.Length]);
    static double LaneX(int lane) => Pad + LaneWidth / 2 + lane * LaneWidth;

    // Width from the lane count; height 0 so the row's content (the message column) drives the row height.
    protected override Size MeasureOverride(Size availableSize)
        => Cell is { } cell ? new Size(Pad * 2 + cell.LaneCount * LaneWidth, 0) : new Size(0, 0);

    public override void Render(DrawingContext context)
    {
        if (Cell is not { } cell) return;
        var h = Bounds.Height;
        var dotY = DotLineCenter;

        foreach (var seg in cell.Segments)
        {
            switch (seg.Kind)
            {
                case SegmentKind.PassThrough:
                    Line(context, LaneBrush(seg.FromLane), new Point(LaneX(seg.FromLane), 0), new Point(LaneX(seg.FromLane), h), straight: true);
                    break;
                case SegmentKind.TopToNode:
                    Line(context, LaneBrush(seg.FromLane), new Point(LaneX(seg.FromLane), 0), new Point(LaneX(seg.ToLane), dotY),
                        straight: seg.FromLane == seg.ToLane);
                    break;
                case SegmentKind.NodeToBottom:
                    Line(context, LaneBrush(seg.ToLane), new Point(LaneX(seg.FromLane), dotY), new Point(LaneX(seg.ToLane), h),
                        straight: seg.FromLane == seg.ToLane);
                    break;
            }
        }

        var centre = new Point(LaneX(cell.NodeLane), dotY);
        context.DrawEllipse(LaneBrush(cell.NodeLane), null, centre, DotRadius, DotRadius);
        if (CurrentSha is { Length: > 0 } cur && NodeSha == cur)
            context.DrawEllipse(null, new Pen(Brushes.White, 2), centre, DotRadius + 3, DotRadius + 3);
        if (SelectedSha is { Length: > 0 } sel && NodeSha == sel)
            context.DrawEllipse(null, new Pen(new SolidColorBrush(Color.Parse("#F2C94C")), 3), centre, DotRadius + 4.5, DotRadius + 4.5);
    }

    static void Line(DrawingContext ctx, IBrush brush, Point a, Point b, bool straight)
    {
        var pen = new Pen(brush, 2);
        if (straight) { ctx.DrawLine(pen, a, b); return; }
        var midY = (a.Y + b.Y) / 2; // vertical-tangent bezier so lanes curve only where they change column
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(a, false);
            g.CubicBezierTo(new Point(a.X, midY), new Point(b.X, midY), b);
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, pen, geo);
    }
}
