using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Sprig.App.Maps;
using Sprig.Core.Maps;

namespace Sprig.App.Controls;

/// <summary>
/// Draws a <see cref="MapGraphLayout"/>: repo boxes, module nodes listing what they provide, placeholders
/// for unmet needs and map constants, and the edges between them. Clicking a node (or moving with the arrow
/// keys) selects it and fades everything not directly connected, which keeps a busy hub readable.
/// </summary>
public sealed class MapWiringCanvas : Control
{
    public static readonly StyledProperty<MapGraphLayout?> GraphProperty =
        AvaloniaProperty.Register<MapWiringCanvas, MapGraphLayout?>(nameof(Graph));
    public static readonly StyledProperty<string?> SelectedIdProperty =
        AvaloniaProperty.Register<MapWiringCanvas, string?>(nameof(SelectedId), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<MapWiringCanvas, double>(nameof(Zoom), 1.0);

    public MapGraphLayout? Graph { get => GetValue(GraphProperty); set => SetValue(GraphProperty, value); }
    public string? SelectedId { get => GetValue(SelectedIdProperty); set => SetValue(SelectedIdProperty, value); }
    public double Zoom { get => GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }

    string? _hover;

    static MapWiringCanvas()
    {
        AffectsRender<MapWiringCanvas>(GraphProperty, SelectedIdProperty, ZoomProperty);
        AffectsMeasure<MapWiringCanvas>(GraphProperty, ZoomProperty);
        FocusableProperty.OverrideDefaultValue<MapWiringCanvas>(true);
    }

    // The app's dark palette (App.axaml), plus the tints the canvas layers over it.
    static readonly Color Accent = Color.Parse("#60A5FA"), Danger = Color.Parse("#F87171"), Warn = Color.Parse("#FBBF24"),
        Constant = Color.Parse("#A78BFA"), Muted = Color.Parse("#8C8CA0"), Fg = Color.Parse("#E1E1EB"),
        Title = Color.Parse("#F5F5FA"), Border = Color.Parse("#2D2D3C"), NodeBg = Color.Parse("#22222E"),
        Ground = Color.Parse("#181820");

    static IBrush Solid(Color c, double alpha = 1) => new SolidColorBrush(c, alpha);

    static readonly Typeface Mono = new("Cascadia Mono, Consolas, Menlo, monospace");
    Typeface Sans(FontWeight weight) => new(TextElement.GetFontFamily(this), FontStyle.Normal, weight);

    protected override Size MeasureOverride(Size availableSize)
        => Graph is { } g ? new Size(g.Size.Width * Zoom, g.Size.Height * Zoom) : default;

    /// <summary>The node under a point in this control's coordinates, if any.</summary>
    public GraphNode? NodeAt(Point point)
    {
        if (Graph is not { } g || Zoom <= 0) return null;
        var p = new Point(point.X / Zoom, point.Y / Zoom);
        return g.Nodes.LastOrDefault(n => n.Bounds.Contains(p));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var id = NodeAt(e.GetPosition(this))?.Id;
        if (id == _hover) return;
        _hover = id;
        Cursor = id is null ? Cursor.Default : new Cursor(StandardCursorType.Hand);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hover is null) return;
        _hover = null;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var id = NodeAt(e.GetPosition(this))?.Id;
        SelectedId = id is not null && id == SelectedId ? null : id;
        Focus();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Graph is not { Nodes.Count: > 0 } g) return;
        // Reading order: row by row from the top, left to right within a row.
        var order = g.Nodes.OrderBy(n => Math.Round(n.Bounds.Top / 20)).ThenBy(n => n.Bounds.Left).ToList();
        var index = order.FindIndex(n => n.Id == SelectedId);
        switch (e.Key)
        {
            case Key.Right or Key.Down:
                SelectedId = order[(index + 1) % order.Count].Id;
                break;
            case Key.Left or Key.Up:
                SelectedId = order[index <= 0 ? order.Count - 1 : index - 1].Id;
                break;
            case Key.Escape:
                SelectedId = null;
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        // A transparent ground so clicks on empty space reach the control and clear the selection.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (Graph is not { } g) return;

        var selected = SelectedId is { } sel && g.Nodes.Any(n => n.Id == sel) ? sel : null;
        var near = new HashSet<string>(StringComparer.Ordinal);
        if (selected is not null)
        {
            near.Add(selected);
            foreach (var e in g.Edges.Where(e => e.From == selected || e.To == selected)) { near.Add(e.From); near.Add(e.To); }
        }
        bool Dim(string id) => selected is not null && !near.Contains(id);
        bool DimEdge(GraphEdge e) => selected is not null && e.From != selected && e.To != selected;

        using (context.PushTransform(Matrix.CreateScale(Zoom, Zoom)))
        {
            foreach (var c in g.Clusters) DrawCluster(context, c);
            foreach (var e in g.Edges) using (context.PushOpacity(DimEdge(e) ? 0.16 : 1)) DrawEdge(context, e);
            foreach (var n in g.Nodes) using (context.PushOpacity(Dim(n.Id) ? 0.2 : 1)) DrawNode(context, n, n.Id == selected, n.Id == _hover);
            // Labels last: MSAGL can place one over a node where an edge leaves a repo box, and a label
            // hidden behind a node is worse than one overlapping its edge.
            foreach (var e in g.Edges) using (context.PushOpacity(DimEdge(e) ? 0.16 : 1)) DrawEdgeLabel(context, e);
        }
    }

    void DrawCluster(DrawingContext ctx, GraphCluster c)
    {
        var pen = new Pen(Solid(Border), 1.2, new DashStyle([4, 3], 0));
        ctx.DrawRectangle(Solid(Colors.White, 0.025), pen, c.Bounds, 12, 12);
        ctx.DrawText(Text(c.Repo, Sans(FontWeight.SemiBold), 12.5, Solid(Muted), c.Bounds.Width - 24), c.Bounds.TopLeft + new Point(12, 8));
    }

    static (Color Colour, DashStyle? Dash) EdgeStyle(GraphEdgeKind kind) => kind switch
    {
        GraphEdgeKind.Gap => (Danger, new DashStyle([2.2, 2.2], 0)),
        GraphEdgeKind.Ambiguous => (Warn, new DashStyle([3.3, 2.2], 0)),
        GraphEdgeKind.Similar => (Warn, new DashStyle([0.1, 2.4], 0)),
        GraphEdgeKind.Default => (Constant, new DashStyle([3.3, 2.2], 0)),
        _ => (Accent, null),
    };

    static void DrawEdge(DrawingContext ctx, GraphEdge e)
    {
        if (e.Path.Count < 2) return;
        var (colour, dash) = EdgeStyle(e.Kind);
        var pen = new Pen(Solid(colour), 1.8, dash, PenLineCap.Round, PenLineJoin.Round);
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(e.Path[0], false);
            foreach (var p in e.Path.Skip(1)) g.LineTo(p);
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, pen, geometry);

        // The arrowhead fills the gap between the route's end and the tip MSAGL left for it.
        var from = e.Path[^1];
        var tip = e.ArrowTip;
        var dir = tip - from;
        var length = Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y);
        if (length < 0.5) return;
        var unit = dir / length;
        var side = new Point(-unit.Y, unit.X) * 4;
        var head = new StreamGeometry();
        using (var g = head.Open())
        {
            g.BeginFigure(tip, true);
            g.LineTo(from + side);
            g.LineTo(from - side);
            g.EndFigure(true);
        }
        ctx.DrawGeometry(Solid(colour), null, head);
    }

    void DrawEdgeLabel(DrawingContext ctx, GraphEdge e)
    {
        if (e.LabelBounds.Width <= 0) return;
        var (colour, _) = EdgeStyle(e.Kind);
        ctx.DrawRectangle(Solid(Ground, 0.92), null, e.LabelBounds, 4, 4);
        for (var i = 0; i < e.Labels.Count; i++)
        {
            var text = Text(e.Labels[i], Mono, GraphMetrics.LabelSize, Solid(colour), e.LabelBounds.Width);
            ctx.DrawText(text, new Point(e.LabelBounds.Center.X - text.Width / 2, e.LabelBounds.Top + 3 + i * 15));
        }
    }

    void DrawNode(DrawingContext ctx, GraphNode n, bool selected, bool hover)
    {
        var b = n.Bounds;
        switch (n.Kind)
        {
            case GraphNodeKind.Module:
                DrawModule(ctx, n, selected, hover);
                break;
            case GraphNodeKind.Gap:
            case GraphNodeKind.Default:
            {
                var need = n.Need!;
                var colour = n.Kind == GraphNodeKind.Gap ? Danger : Constant;
                var pen = new Pen(Solid(selected || hover ? Accent : colour), selected ? 2 : 1.2, new DashStyle([4, 3], 0));
                ctx.DrawRectangle(Solid(colour, n.Kind == GraphNodeKind.Gap ? 0.12 : 0.14), pen, b, 10, 10);
                Centred(ctx, Text(need.Capability, Mono, 13, Solid(colour), b.Width - 16), b, 10);
                Centred(ctx, Text(GraphMetrics.PlaceholderSubtitle(need), Mono, GraphMetrics.SubSize,
                    Solid(n.Kind == GraphNodeKind.Gap ? Danger : Fg), b.Width - 16), b, 30);
                if (GraphMetrics.PlaceholderNote(need) is { } note)
                    Centred(ctx, Text(note, Mono, GraphMetrics.SubSize, Solid(Warn), b.Width - 16), b, 47);
                break;
            }
        }
    }

    void DrawModule(DrawingContext ctx, GraphNode n, bool selected, bool hover)
    {
        var m = n.Module!;
        var b = n.Bounds;
        var pad = GraphMetrics.SidePad;
        ctx.DrawRectangle(Solid(NodeBg), new Pen(Solid(selected || hover ? Accent : Border), selected ? 2 : 1.2), b, 10, 10);

        var badgeWidth = 0.0;
        if (m.ReliedOnBy >= 2)
        {
            var badge = Text(GraphMetrics.HubBadge(m), Sans(FontWeight.SemiBold), 10.5, Solid(Accent), double.PositiveInfinity);
            badgeWidth = badge.Width + 14;
            var pill = new Rect(b.Right - badgeWidth - 10, b.Top + 10, badgeWidth, 18);
            ctx.DrawRectangle(Solid(Accent, 0.14), null, pill, 9, 9);
            ctx.DrawText(badge, new Point(pill.X + 7, pill.Y + (18 - badge.Height) / 2));
        }
        ctx.DrawText(Text(GraphMetrics.Title(m, n.Boxed), Sans(FontWeight.SemiBold), GraphMetrics.TitleSize, Solid(Title),
            b.Width - 2 * pad - (badgeWidth > 0 ? badgeWidth + 8 : 0)), b.TopLeft + new Point(pad, 9));
        ctx.DrawText(Text(GraphMetrics.Subtitle(m, n.Boxed), Mono, GraphMetrics.SubSize, Solid(Muted), b.Width - 2 * pad),
            b.TopLeft + new Point(pad, 29));

        var ruleY = b.Top + GraphMetrics.HeaderHeight - 2;
        ctx.DrawLine(new Pen(Solid(Border), 1), new Point(b.Left, ruleY), new Point(b.Right, ruleY));

        if (m.Provides.Count == 0)
            ctx.DrawText(Text("provides nothing", Mono, GraphMetrics.MetaSize, Solid(Muted), b.Width - 2 * pad),
                new Point(b.Left + pad, ruleY + 6));

        for (var i = 0; i < m.Provides.Count; i++)
        {
            var p = m.Provides[i];
            var cy = ruleY + 4 + i * GraphMetrics.RowHeight + GraphMetrics.RowHeight / 2;
            var shared = p.Use == ProvideUse.Shared;
            var marker = new Point(b.Left + pad + 4, cy);
            if (p.Constants is not null)
                DrawDiamond(ctx, marker, 4.5, Solid(Constant));
            else if (shared)
                ctx.DrawEllipse(Solid(Accent), null, marker, 3.5, 3.5);
            else
                ctx.DrawEllipse(null, new Pen(Solid(Muted), 1.3), marker, 3.5, 3.5);

            var meta = Text(GraphMetrics.ProvideMeta(p), Mono, GraphMetrics.MetaSize, Solid(p.Constants is null ? Muted : Constant), b.Width / 2);
            ctx.DrawText(meta, new Point(b.Right - pad - meta.Width, cy - meta.Height / 2));
            var cap = Text(p.Capability, Mono, GraphMetrics.CapSize, Solid(shared || p.Constants is not null ? Fg : Muted),
                b.Width - 2 * pad - 16 - meta.Width - 8);
            ctx.DrawText(cap, new Point(b.Left + pad + 16, cy - cap.Height / 2));
        }
    }

    static void DrawDiamond(DrawingContext ctx, Point centre, double r, IBrush fill)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(centre + new Point(0, -r), true);
            g.LineTo(centre + new Point(r, 0));
            g.LineTo(centre + new Point(0, r));
            g.LineTo(centre + new Point(-r, 0));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(fill, null, geometry);
    }

    static void Centred(DrawingContext ctx, FormattedText text, Rect box, double top)
        => ctx.DrawText(text, new Point(box.Center.X - text.Width / 2, box.Top + top));

    static FormattedText Text(string text, Typeface face, double size, IBrush brush, double maxWidth)
        => new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush)
        {
            MaxTextWidth = Math.Max(1, maxWidth),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
}
