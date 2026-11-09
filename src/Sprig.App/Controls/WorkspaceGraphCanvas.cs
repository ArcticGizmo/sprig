using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Sprig.App.ViewModels;
using Sprig.Engine.Planning;

namespace Sprig.App.Controls;

/// <summary>
/// Draws a <see cref="WorkspaceGraph"/>: a soft box per repo, a circle per node ringed in its state's colour, and an
/// arrow from what uses a value to what gives it. A click picks a node, a drag moves it (and keeps it there), dragging
/// the background pans, the wheel zooms, a double-click asks for the node's log, and a right-click for its menu.
/// </summary>
public sealed class WorkspaceGraphCanvas : Control
{
    public static readonly StyledProperty<WorkspaceGraph?> GraphProperty =
        AvaloniaProperty.Register<WorkspaceGraphCanvas, WorkspaceGraph?>(nameof(Graph));

    public WorkspaceGraph? Graph
    {
        get => GetValue(GraphProperty);
        set => SetValue(GraphProperty, value);
    }

    /// <summary>A node was right-clicked; the view opens its menu here.</summary>
    public event Action<GraphNode, Control>? NodeMenuRequested;

    static readonly IBrush Back = Brushes.Transparent;
    static readonly IBrush NodeFill = new SolidColorBrush(Color.Parse("#1E1E2A"));
    static readonly IBrush NodeFillHover = new SolidColorBrush(Color.Parse("#272735"));
    static readonly IBrush Icon = new SolidColorBrush(Color.Parse("#C9CBD6"));
    static readonly IBrush Text = new SolidColorBrush(Color.Parse("#E5E7EB"));
    static readonly IBrush Dim = new SolidColorBrush(Color.Parse("#8B8FA3"));
    static readonly IBrush LinkBrush = new SolidColorBrush(Color.Parse("#4B5068"));
    static readonly IBrush LinkLit = new SolidColorBrush(Color.Parse("#60A5FA"));
    static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#60A5FA"));
    static readonly IBrush GroupFill = new SolidColorBrush(Color.Parse("#0CFFFFFF"));
    static readonly IBrush GroupLine = new SolidColorBrush(Color.Parse("#1FFFFFFF"));
    static readonly IBrush LabelBack = new SolidColorBrush(Color.Parse("#181820"));
    static readonly IBrush LabelBorder = new SolidColorBrush(Color.Parse("#2D2D3C"));
    static readonly Typeface Sans = new(FontFamily.Default);
    static readonly Typeface SansBold = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
    static readonly Typeface Mono = new("Cascadia Mono,Consolas,monospace");

    public static IBrush HealthBrush(NodeHealth health) => health switch
    {
        NodeHealth.Good => Good,
        NodeHealth.Busy => Busy,
        NodeHealth.Bad => Bad,
        NodeHealth.Off => Off,
        _ => Unknown,
    };

    static readonly IBrush Good = new SolidColorBrush(Color.Parse("#4ADE80"));
    static readonly IBrush Busy = new SolidColorBrush(Color.Parse("#FBBF24"));
    static readonly IBrush Bad = new SolidColorBrush(Color.Parse("#F87171"));
    static readonly IBrush Off = new SolidColorBrush(Color.Parse("#6B7280"));
    static readonly IBrush Unknown = new SolidColorBrush(Color.Parse("#9CA3AF"));

    const double R = WorkspaceGraph.NodeRadius;

    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(16) };
    double _scale = 1;
    Vector _offset;
    bool _userMoved;

    string? _dragKey;
    Point _pressAt;
    bool _moved;
    bool _panning;
    Vector _panFrom;

    public WorkspaceGraphCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
        _tick.Tick += (_, _) =>
        {
            if (Graph is not { } graph) return;
            for (var i = 0; i < 2; i++) graph.Layout.Step();
            InvalidateVisual();
            if (graph.Layout.Settled && _dragKey is null) _tick.Stop();
        };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != GraphProperty) return;
        if (change.OldValue is WorkspaceGraph old)
        {
            old.Changed -= InvalidateVisual;
            old.FitRequested -= FitNow;
        }
        if (change.NewValue is WorkspaceGraph now)
        {
            now.Changed += InvalidateVisual;
            now.FitRequested += FitNow;
        }
        _userMoved = false;
        InvalidateVisual();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _tick.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    void FitNow()
    {
        _userMoved = false;
        InvalidateVisual();
    }

    Point ToScreen(double x, double y) => new(x * _scale + _offset.X, y * _scale + _offset.Y);

    /// <summary>Where a node is drawn now, for a rendered sample to point at.</summary>
    public Point? PointOf(string key) => Graph?.Layout.Find(key) is { } body ? ToScreen(body.X, body.Y) : null;

    (double X, double Y) ToWorld(Point p) => ((p.X - _offset.X) / _scale, (p.Y - _offset.Y) / _scale);

    /// <summary>The whole graph in view, labels and repo boxes included, no larger than life.</summary>
    void Fit(WorkspaceGraph graph)
    {
        if (graph.Layout.Bodies.Count == 0 || Bounds.Width < 10 || Bounds.Height < 10) return;
        var bodies = graph.Layout.Bodies;
        var left = bodies.Min(b => b.X - b.Radius) - LayoutBox.Side - 4;
        var right = bodies.Max(b => b.X + b.Radius) + LayoutBox.Side + 4;
        var top = bodies.Min(b => b.Y) - LayoutBox.Above - 4;
        var bottom = bodies.Max(b => b.Y) + LayoutBox.Below + 4;
        var scale = Math.Min((Bounds.Width - 40) / (right - left), (Bounds.Height - 40) / (bottom - top));
        _scale = Math.Clamp(scale, 0.35, 1.15);
        _offset = new Vector(Bounds.Width / 2 - (left + right) / 2 * _scale, Bounds.Height / 2 - (top + bottom) / 2 * _scale);
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Back, new Rect(Bounds.Size));
        if (Graph is not { } graph) return;
        if (!_userMoved) Fit(graph);

        // Focus is the node pointed at, else the one picked: its links are drawn in full and the rest fade, so a dense
        // workspace reads one node at a time.
        var focus = graph.Hovered ?? graph.Selected;
        var positions = graph.Layout.Bodies.ToDictionary(b => b.Key, b => ToScreen(b.X, b.Y), StringComparer.Ordinal);
        var r = R * _scale;

        DrawGroups(context, graph);

        // Nodes and their labels are placed first, so a link's label steps aside rather than covering one.
        var labels = graph.Nodes.Where(n => positions.ContainsKey(n.Key) && graph.Layout.Find(n.Key) is not null).Select(n =>
        {
            var p = positions[n.Key];
            var room = graph.Layout.Find(n.Key)!.Radius * _scale;
            return new Rect(p.X - room, p.Y - r - 2, room * 2, r + 2 + LabelBottom(n));
        }).ToList();
        foreach (var link in graph.Links.OrderBy(l => focus is not null && (l.From == focus || l.To == focus)))
        {
            if (!positions.ContainsKey(link.From) || !positions.ContainsKey(link.To)) continue;
            var lit = focus is not null && (link.From == focus || link.To == focus);
            if (!lit && graph.IsTagLink(link)) continue;
            var back = graph.Layout.RunsBack(link.From, link.To);
            var opacity = lit ? 1 : focus is not null ? 0.07 : link.OrderOnly || back ? 0.2 : 0.4;
            DrawLink(context, graph, link, positions, r, lit, opacity, labels);
        }

        foreach (var node in graph.Nodes.OrderBy(n => n.Key == graph.Selected))
        {
            if (!positions.TryGetValue(node.Key, out var at)) continue;
            var dimmed = focus is not null && !graph.IsNear(node.Key, focus);
            DrawNode(context, node, at, r, node.Key == graph.Selected, node.Key == graph.Hovered, dimmed);
        }
    }

    double NameSize => Math.Max(9.5, 12 * _scale);
    // Tags shrink with the picture, unlike names: a row of them is as wide as the layout made room for, and a floor on
    // their size would push them into their neighbours' when zoomed out.
    double TagSize => 10 * _scale;

    /// <summary>How far below a node's centre its label ends: its circle, name, state line and tags.</summary>
    double LabelBottom(GraphNode node)
        => R * _scale + 6 + NameSize * 1.3 + (NameSize - 1.5) * 1.3 + (node.Tags.Count > 0 ? TagSize * 1.5 + 6 : 0);

    void DrawGroups(DrawingContext context, WorkspaceGraph graph)
    {
        foreach (var (group, b) in graph.Layout.Boxes())
        {
            var box = new Rect(ToScreen(b.Left, b.Top), ToScreen(b.Right, b.Bottom));
            context.DrawRectangle(GroupFill, new Pen(GroupLine, 1), box, 14, 14);
            // Scaled with the picture, so it stays in the room above the box's top row of nodes, but never too small to read.
            var title = new FormattedText(group, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, SansBold, Math.Max(8.5, 11 * _scale), Dim)
            {
                MaxTextWidth = Math.Max(10, box.Width - 20 * _scale),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            context.DrawText(title, new Point(box.X + 12 * _scale, box.Y + 6 * _scale));
        }
    }

    /// <summary>
    /// A link from what uses to what's used. By forces, a straight line between the circles. In rows, it leaves the
    /// lower edge of its user's label and enters the top of what it uses (or, running back up, the top of the user and
    /// the foot of the other's label), bending through the gaps the layout found, with each bend upright so the curve
    /// reads as flowing down the page.
    /// </summary>
    void DrawLink(DrawingContext context, WorkspaceGraph graph, TopologyLink link, Dictionary<string, Point> positions, double r,
        bool lit, double opacity, List<Rect> labels)
    {
        var from = positions[link.From];
        var to = positions[link.To];
        var points = new List<Point>();
        if (graph.Layout.Rows && graph.Find(link.From) is { } user && graph.Find(link.To) is { } used)
        {
            var route = graph.Layout.Route(link.From, link.To).Select(p => ToScreen(p.X, p.Y)).ToList();
            if (Math.Abs(to.Y - from.Y) < 1)
            {
                points.Add(new Point(from.X, from.Y + LabelBottom(user)));
                points.AddRange(route);
                points.Add(new Point(to.X, to.Y + LabelBottom(used) + 4));
            }
            else if (to.Y > from.Y)
            {
                points.Add(new Point(from.X, from.Y + LabelBottom(user)));
                points.AddRange(route);
                points.Add(new Point(to.X, to.Y - r - 4));
            }
            else
            {
                points.Add(new Point(from.X, from.Y - r - 2));
                points.AddRange(route);
                points.Add(new Point(to.X, to.Y + LabelBottom(used) + 4));
            }
        }
        else
        {
            var dx = to.X - from.X;
            var dy = to.Y - from.Y;
            var d = Math.Sqrt(dx * dx + dy * dy);
            if (d < r * 2 + 4) return;
            points.Add(new Point(from.X + dx / d * (r + 2), from.Y + dy / d * (r + 2)));
            points.Add(new Point(to.X - dx / d * (r + 4), to.Y - dy / d * (r + 4)));
        }

        var start = points[0];
        var end = points[^1];
        var brush = lit ? LinkLit : LinkBrush;
        using var _ = context.PushOpacity(opacity);
        var pen = new Pen(brush, lit ? 2.2 : 1.4, link.OrderOnly ? new DashStyle([4, 3], 0) : null);
        var path = new StreamGeometry();
        var lastControl = start;
        using (var g = path.Open())
        {
            g.BeginFigure(start, false);
            for (var i = 1; i < points.Count; i++)
            {
                var (p0, p1) = (points[i - 1], points[i]);
                if (!graph.Layout.Rows)
                {
                    g.LineTo(p1);
                    lastControl = p0;
                    continue;
                }
                var half = (p1.Y - p0.Y) / 2;
                lastControl = new Point(p1.X, p1.Y - half);
                g.CubicBezierTo(new Point(p0.X, p0.Y + half), lastControl, p1);
            }
            g.EndFigure(false);
        }
        context.DrawGeometry(null, pen, path);

        var hx = end.X - lastControl.X;
        var hy = end.Y - lastControl.Y;
        var hd = Math.Sqrt(hx * hx + hy * hy);
        if (hd < 0.01) (hx, hy, hd) = (end.X - start.X, end.Y - start.Y, Math.Max(0.01, Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2))));
        var ux = hx / hd;
        var uy = hy / hd;
        var head = new StreamGeometry();
        using (var g = head.Open())
        {
            var back = new Point(end.X - ux * 9, end.Y - uy * 9);
            g.BeginFigure(new Point(back.X - uy * 4.5, back.Y + ux * 4.5), true);
            g.LineTo(end);
            g.LineTo(new Point(back.X + uy * 4.5, back.Y - ux * 4.5));
            g.EndFigure(true);
        }
        context.DrawGeometry(brush, null, head);

        if (!lit) return;
        var label = link.OrderOnly ? "starts after it" : link.Values.Count <= 2 ? string.Join(", ", link.Values) : $"{link.Values[0]}, {link.Values[1]} +{link.Values.Count - 2}";
        var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Mono, 10.5, Text);
        var mid = points.Count > 2 ? points[points.Count / 2] : new Point((start.X + end.X) / 2, (start.Y + end.Y) / 2);
        ux = (end.X - start.X) / Math.Max(0.01, Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2)));
        uy = (end.Y - start.Y) / Math.Max(0.01, Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2)));
        var box = new Rect(mid.X - text.Width / 2 - 7, mid.Y - text.Height / 2 - 2, text.Width + 14, text.Height + 4);
        // Along the link first, then beside it, for the first spot that covers nothing. With none, it goes unlabelled:
        // the picked node's card lists every value anyway, and a label over a node's name hides more than it tells.
        var along = new[] { 0, -0.25, 0.25, -0.15, 0.15, 0.35, -0.35 };
        var beside = new[] { 0.0, 1, -1, 2, -2 };
        var spot = beside.SelectMany(n => along.Select(t => box.Translate(new Vector(
                (end.X - start.X) * t - uy * n * (box.Height + 4), (end.Y - start.Y) * t + ux * n * (box.Height + 4)))))
            .Cast<Rect?>()
            .FirstOrDefault(b => !labels.Exists(l => l.Intersects(b!.Value)));
        if (spot is not { } free) return;
        box = free;
        labels.Add(box);
        context.DrawRectangle(LabelBack, new Pen(LabelBorder, 1), box, 9, 9);
        context.DrawText(text, new Point(box.X + 7, box.Y + 2));
    }

    void DrawNode(DrawingContext context, GraphNode node, Point at, double r, bool selected, bool hovered, bool dimmed)
    {
        using var _ = context.PushOpacity(dimmed ? 0.3 : 1);
        var state = HealthBrush(node.Health);
        if (selected) context.DrawEllipse(null, new Pen(Accent, 2), at, r + 6, r + 6);
        var ring = new Pen(state, hovered ? 3.2 : 2.6, node.Health == NodeHealth.Busy || node.Kind == TopologyKind.Outside ? new DashStyle([3, 2], 0) : null);
        context.DrawEllipse(hovered ? NodeFillHover : NodeFill, ring, at, r, r);
        DrawIcon(context, node, at, r);

        var size = NameSize;
        var name = new FormattedText(node.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, SansBold, size, Text);
        context.DrawText(name, new Point(at.X - name.Width / 2, at.Y + r + 5));
        var line = node.IsRunner && node.Detail.Length > 0 && node.Health != NodeHealth.Bad ? $"{node.State} · {node.Detail}" : node.State;
        var sub = new FormattedText(line, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Sans, size - 1.5,
            node.Health is NodeHealth.Off or NodeHealth.Unknown ? Dim : state);
        context.DrawText(sub, new Point(at.X - sub.Width / 2, at.Y + r + 6 + name.Height));
        if (node.Tags.Count > 0) DrawTags(context, node, new Point(at.X, at.Y + r + 10 + name.Height + sub.Height));
    }

    /// <summary>The infra and outside values a node uses, as a row of tags centred under it, each with its state's dot;
    /// past <see cref="WorkspaceGraph.MaxTags"/>, the rest are counted.</summary>
    void DrawTags(DrawingContext context, GraphNode node, Point top)
    {
        var size = TagSize;
        if (size < 7.5)
        {
            // Too small to read: each tag is its state's dot alone, until zoomed in.
            var r = Math.Max(2, size * 0.32);
            var x0 = top.X - (node.Tags.Count - 1) * r * 1.5;
            for (var i = 0; i < node.Tags.Count; i++)
                context.DrawEllipse(HealthBrush(node.Tags[i].Health), null, new Point(x0 + i * r * 3, top.Y + size * 0.75), r, r);
            return;
        }
        var shown = node.Tags.Take(WorkspaceGraph.MaxTags)
            .Select(t => (Text: new FormattedText(t.TagName, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Sans, size, Text), Dot: (IBrush?)HealthBrush(t.Health)))
            .ToList();
        if (node.Tags.Count > WorkspaceGraph.MaxTags)
            shown.Add((new FormattedText($"+{node.Tags.Count - WorkspaceGraph.MaxTags}", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Sans, size, Dim), null));
        var pad = size * 0.6;
        var dot = size * 0.32;
        var height = size * 1.5;
        var gap = WorkspaceGraph.TagGap * size / 10;
        double Width((FormattedText Text, IBrush? Dot) t) => t.Text.Width + pad * 2 + (t.Dot is null ? 0 : dot * 2 + pad * 0.6);
        var x = top.X - (shown.Sum(Width) + gap * (shown.Count - 1)) / 2;
        foreach (var tag in shown)
        {
            var w = Width(tag);
            context.DrawRectangle(TagFill, new Pen(TagLine, 1), new Rect(x, top.Y, w, height), height / 2, height / 2);
            var textX = x + pad;
            if (tag.Dot is { } brush)
            {
                context.DrawEllipse(brush, null, new Point(x + pad + dot, top.Y + height / 2), dot, dot);
                textX += dot * 2 + pad * 0.6;
            }
            context.DrawText(tag.Text, new Point(textX, top.Y + (height - tag.Text.Height) / 2));
            x += w + gap;
        }
    }

    static readonly IBrush TagFill = new SolidColorBrush(Color.Parse("#262634"));
    static readonly IBrush TagLine = new SolidColorBrush(Color.Parse("#34344A"));

    /// <summary>A small picture of what it is: a window for an app, a play mark for a job, a cylinder for a store, a box
    /// for other containers and compose services, a globe for an outside value, a question mark for one missing.</summary>
    static void DrawIcon(DrawingContext context, GraphNode node, Point c, double r)
    {
        var s = r / 22;
        var pen = new Pen(Icon, 1.6 * s);
        switch (node.Kind)
        {
            case TopologyKind.App:
            {
                var box = new Rect(c.X - 9 * s, c.Y - 7 * s, 18 * s, 14 * s);
                context.DrawRectangle(null, pen, box, 2 * s, 2 * s);
                context.DrawLine(pen, new Point(box.X, box.Y + 4 * s), new Point(box.Right, box.Y + 4 * s));
                break;
            }
            case TopologyKind.Job:
            {
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    ctx.BeginFigure(new Point(c.X - 5 * s, c.Y - 7 * s), true);
                    ctx.LineTo(new Point(c.X + 7 * s, c.Y));
                    ctx.LineTo(new Point(c.X - 5 * s, c.Y + 7 * s));
                    ctx.EndFigure(true);
                }
                context.DrawGeometry(null, pen, g);
                break;
            }
            case TopologyKind.Container or TopologyKind.ComposeService when node.IsStore:
            {
                var w = 8 * s;
                context.DrawEllipse(null, pen, new Point(c.X, c.Y - 6 * s), w, 3 * s);
                context.DrawLine(pen, new Point(c.X - w, c.Y - 6 * s), new Point(c.X - w, c.Y + 6 * s));
                context.DrawLine(pen, new Point(c.X + w, c.Y - 6 * s), new Point(c.X + w, c.Y + 6 * s));
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    ctx.BeginFigure(new Point(c.X - w, c.Y + 6 * s), false);
                    ctx.ArcTo(new Point(c.X + w, c.Y + 6 * s), new Size(w, 3 * s), 0, false, SweepDirection.CounterClockwise);
                    ctx.EndFigure(false);
                }
                context.DrawGeometry(null, pen, g);
                break;
            }
            case TopologyKind.Container or TopologyKind.ComposeService:
            {
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    ctx.BeginFigure(new Point(c.X, c.Y - 8 * s), true);
                    ctx.LineTo(new Point(c.X + 8 * s, c.Y - 4 * s));
                    ctx.LineTo(new Point(c.X + 8 * s, c.Y + 5 * s));
                    ctx.LineTo(new Point(c.X, c.Y + 9 * s));
                    ctx.LineTo(new Point(c.X - 8 * s, c.Y + 5 * s));
                    ctx.LineTo(new Point(c.X - 8 * s, c.Y - 4 * s));
                    ctx.EndFigure(true);
                    ctx.BeginFigure(new Point(c.X - 8 * s, c.Y - 4 * s), false);
                    ctx.LineTo(new Point(c.X, c.Y));
                    ctx.LineTo(new Point(c.X + 8 * s, c.Y - 4 * s));
                    ctx.EndFigure(false);
                    ctx.BeginFigure(new Point(c.X, c.Y), false);
                    ctx.LineTo(new Point(c.X, c.Y + 9 * s));
                    ctx.EndFigure(false);
                }
                context.DrawGeometry(null, pen, g);
                break;
            }
            case TopologyKind.Outside:
                context.DrawEllipse(null, pen, c, 8 * s, 8 * s);
                context.DrawEllipse(null, pen, c, 3.5 * s, 8 * s);
                context.DrawLine(pen, new Point(c.X - 8 * s, c.Y), new Point(c.X + 8 * s, c.Y));
                break;
            default:
            {
                var q = new FormattedText("?", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, SansBold, 18 * s, Icon);
                context.DrawText(q, new Point(c.X - q.Width / 2, c.Y - q.Height / 2));
                break;
            }
        }
    }

    /// <summary>The node under <paramref name="p"/>, its circle or its name.</summary>
    GraphNode? NodeAt(Point p)
    {
        if (Graph is not { } graph) return null;
        var (x, y) = ToWorld(p);
        foreach (var body in graph.Layout.Bodies.Reverse())
        {
            var dx = x - body.X;
            var dy = y - body.Y;
            if (dx * dx + dy * dy <= (R + 4) * (R + 4) || (Math.Abs(dx) <= body.Radius && dy > 0 && dy <= R + 32))
                return graph.Find(body.Key);
        }
        return null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Graph is not { } graph) return;
        Focus();
        var point = e.GetCurrentPoint(this);
        var node = NodeAt(point.Position);
        _pressAt = point.Position;
        _moved = false;

        if (point.Properties.IsRightButtonPressed)
        {
            if (node is null) return;
            graph.Select(node.Key);
            NodeMenuRequested?.Invoke(node, this);
            e.Handled = true;
            return;
        }
        if (!point.Properties.IsLeftButtonPressed) return;
        if (node is not null)
        {
            graph.Select(node.Key);
            if (e.ClickCount == 2)
            {
                graph.RequestLog(node);
                e.Handled = true;
                return;
            }
            // In rows, places follow from the links, so a node is picked but not moved.
            _dragKey = graph.Layout.CanDrag ? node.Key : null;
        }
        else
        {
            _panning = true;
            _panFrom = _offset;
        }
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Graph is not { } graph) return;
        var p = e.GetPosition(this);
        if (!_moved && (p - _pressAt) is var delta && Math.Abs(delta.X) + Math.Abs(delta.Y) > 3) _moved = _dragKey is not null || _panning;

        if (_dragKey is not null && _moved && graph.Layout.Find(_dragKey) is { } body)
        {
            (body.X, body.Y) = ToWorld(p);
            body.Pinned = true;
            graph.Layout.Reheat(0.12);
            _userMoved = true;
            if (!_tick.IsEnabled) _tick.Start();
            InvalidateVisual();
            return;
        }
        if (_panning && _moved)
        {
            _offset = _panFrom + (p - _pressAt);
            _userMoved = true;
            InvalidateVisual();
            return;
        }
        var hover = NodeAt(p);
        graph.Hover(hover?.Key);
        Cursor = hover is null ? Cursor.Default : new Cursor(StandardCursorType.Hand);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (Graph is { } graph)
        {
            if (_dragKey is not null && _moved) graph.Pinned();
            // A click on the background, not a pan, lets go of the picked node.
            if (_panning && !_moved) graph.Select(null);
        }
        _dragKey = null;
        _panning = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        Graph?.Hover(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var p = e.GetPosition(this);
        var (x, y) = ToWorld(p);
        _scale = Math.Clamp(_scale * Math.Pow(1.12, e.Delta.Y), 0.25, 2.5);
        _offset = new Vector(p.X - x * _scale, p.Y - y * _scale);
        _userMoved = true;
        InvalidateVisual();
        e.Handled = true;
    }
}
