using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Sprig.Core.Graph;

namespace Sprig.App.Controls;

/// <summary>Draws the commit-graph column: a coloured dot per commit in its lane, and bezier links down to
/// its parents (GitKraken-style). Display-only — row selection is handled by the list rows beside it, which
/// share <see cref="RowHeight"/> so dots line up with their row. Height/width come from the laid-out graph.</summary>
public sealed class GraphLinesControl : Control
{
    public const double RowHeight = 30;
    const double LaneWidth = 16;
    const double DotRadius = 4.5;
    const double Pad = 10; // left inset before lane 0

    // A small, high-contrast lane palette, cycled by lane index.
    static readonly Color[] Palette =
    [
        Color.Parse("#4C9AFF"), Color.Parse("#F2994A"), Color.Parse("#27AE60"),
        Color.Parse("#BB6BD9"), Color.Parse("#EB5757"), Color.Parse("#F2C94C"),
    ];

    public static readonly StyledProperty<CommitGraph?> GraphProperty =
        AvaloniaProperty.Register<GraphLinesControl, CommitGraph?>(nameof(Graph));

    public static readonly StyledProperty<string?> CurrentShaProperty =
        AvaloniaProperty.Register<GraphLinesControl, string?>(nameof(CurrentSha));

    public CommitGraph? Graph { get => GetValue(GraphProperty); set => SetValue(GraphProperty, value); }
    public string? CurrentSha { get => GetValue(CurrentShaProperty); set => SetValue(CurrentShaProperty, value); }

    static GraphLinesControl()
    {
        AffectsRender<GraphLinesControl>(GraphProperty, CurrentShaProperty);
        AffectsMeasure<GraphLinesControl>(GraphProperty);
    }

    static IBrush LaneBrush(int lane) => new SolidColorBrush(Palette[((lane % Palette.Length) + Palette.Length) % Palette.Length]);
    static double LaneX(int lane) => Pad + LaneWidth / 2 + lane * LaneWidth;
    static double RowY(int row) => RowHeight / 2 + row * RowHeight;

    protected override Size MeasureOverride(Size availableSize)
    {
        var g = Graph;
        if (g is null || g.Nodes.Count == 0) return new Size(0, 0);
        return new Size(Pad * 2 + g.LaneCount * LaneWidth, g.Nodes.Count * RowHeight);
    }

    public override void Render(DrawingContext context)
    {
        var g = Graph;
        if (g is null || g.Nodes.Count == 0) return;

        // Links first (under the dots). A cubic bezier with vertical tangents reads as a smooth lane that
        // curves only where it changes column.
        foreach (var link in g.Links)
        {
            var p0 = new Point(LaneX(link.FromLane), RowY(link.FromRow));
            var p1 = new Point(LaneX(link.ToLane), RowY(link.ToRow));
            var midY = (p0.Y + p1.Y) / 2;
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(p0, false);
                ctx.CubicBezierTo(new Point(p0.X, midY), new Point(p1.X, midY), p1);
                ctx.EndFigure(false);
            }
            context.DrawGeometry(null, new Pen(LaneBrush(link.FromLane), 2), geo);
        }

        // Dots on top; the current commit gets a light ring so you can see where "you are now" sits.
        foreach (var node in g.Nodes)
        {
            var centre = new Point(LaneX(node.Lane), RowY(node.Row));
            context.DrawEllipse(LaneBrush(node.Lane), null, centre, DotRadius, DotRadius);
            if (CurrentSha is { Length: > 0 } cur && node.Commit.Sha == cur)
                context.DrawEllipse(null, new Pen(Brushes.White, 2), centre, DotRadius + 2.5, DotRadius + 2.5);
        }
    }
}
