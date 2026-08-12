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
    public const double RowHeight = 28;      // a single (message) line — the uniform fallback height
    const double FirstLineCenter = 14;       // dot y within a row: centred on the first (message) line
    const double LaneWidth = 20;
    const double DotRadius = 7;
    const double Pad = 12; // left inset before lane 0

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

    public static readonly StyledProperty<string?> SelectedShaProperty =
        AvaloniaProperty.Register<GraphLinesControl, string?>(nameof(SelectedSha));

    /// <summary>Per-row heights (aligned with the list rows beside the graph, which vary — taller when a
    /// commit carries branch tags). Null falls back to a uniform <see cref="RowHeight"/>.</summary>
    public static readonly StyledProperty<IReadOnlyList<double>?> RowHeightsProperty =
        AvaloniaProperty.Register<GraphLinesControl, IReadOnlyList<double>?>(nameof(RowHeights));

    public CommitGraph? Graph { get => GetValue(GraphProperty); set => SetValue(GraphProperty, value); }
    public string? CurrentSha { get => GetValue(CurrentShaProperty); set => SetValue(CurrentShaProperty, value); }
    public string? SelectedSha { get => GetValue(SelectedShaProperty); set => SetValue(SelectedShaProperty, value); }
    public IReadOnlyList<double>? RowHeights { get => GetValue(RowHeightsProperty); set => SetValue(RowHeightsProperty, value); }

    static GraphLinesControl()
    {
        AffectsRender<GraphLinesControl>(GraphProperty, CurrentShaProperty, SelectedShaProperty, RowHeightsProperty);
        AffectsMeasure<GraphLinesControl>(GraphProperty, RowHeightsProperty);
    }

    static IBrush LaneBrush(int lane) => new SolidColorBrush(Palette[((lane % Palette.Length) + Palette.Length) % Palette.Length]);
    static double LaneX(int lane) => Pad + LaneWidth / 2 + lane * LaneWidth;

    double HeightOf(int row)
    {
        var h = RowHeights;
        return h is not null && row < h.Count ? h[row] : RowHeight;
    }

    // Cumulative top of each row + the y-centre for a given row, honouring variable heights.
    double[] RowTops(int count)
    {
        var tops = new double[count];
        double y = 0;
        for (var i = 0; i < count; i++) { tops[i] = y; y += HeightOf(i); }
        return tops;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var g = Graph;
        if (g is null || g.Nodes.Count == 0) return new Size(0, 0);
        double total = 0;
        for (var i = 0; i < g.Nodes.Count; i++) total += HeightOf(i);
        return new Size(Pad * 2 + g.LaneCount * LaneWidth, total);
    }

    public override void Render(DrawingContext context)
    {
        var g = Graph;
        if (g is null || g.Nodes.Count == 0) return;

        var tops = RowTops(g.Nodes.Count);
        double RowY(int row) => tops[row] + FirstLineCenter; // align with the row's first (message) line

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

        // Dots on top. The current commit gets a light ring ("you are now"); the selected commit gets a
        // bright accent ring (what a click has picked) — drawn last so it wins when they coincide.
        foreach (var node in g.Nodes)
        {
            var centre = new Point(LaneX(node.Lane), RowY(node.Row));
            context.DrawEllipse(LaneBrush(node.Lane), null, centre, DotRadius, DotRadius);
            if (CurrentSha is { Length: > 0 } cur && node.Commit.Sha == cur)
                context.DrawEllipse(null, new Pen(Brushes.White, 2), centre, DotRadius + 3, DotRadius + 3);
            if (SelectedSha is { Length: > 0 } sel && node.Commit.Sha == sel)
                context.DrawEllipse(null, new Pen(new SolidColorBrush(Color.Parse("#F2C94C")), 3), centre, DotRadius + 4, DotRadius + 4);
        }
    }
}
