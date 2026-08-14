using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering;
using Sprig.Core.Stacks;

namespace Sprig.App.Controls;

/// <summary>
/// The repo-graph: a free-form, repo-centric view of a stack's wiring. Every repo is a draggable node;
/// a stack port with an assigned owner and exactly one other consumer is drawn as a directed
/// <c>owner → consumer</c> dependency line labelled with the port, and every other consumption becomes a
/// labelled chip with a usage count attached to the consuming node — so a widely-shared value adds one
/// chip per repo instead of a mat of crossing cables. Nodes are auto-arranged (layered by the
/// dependency edges, roots on top) to minimise crossings on open; you can drag them to tidy by hand.
///
/// <para><b>Drag positions are not persisted yet</b> — they reset to the auto-layout when the set of
/// repos changes. Revisit persisting per-repo positions on the stack if hand-tidy turns out to be worth
/// keeping (see the pools-branch handoff notes).</para>
///
/// The one editing affordance here is ownership: click a chip to name the port's owner (promoting it to
/// a line), or an edge to change / clear it. All other editing stays on the patchbay
/// (<see cref="WiringCanvas"/>); this is the read-optimised second lens. Layout is derived from
/// <see cref="RepoGraph"/>; this control only draws and hit-tests.
/// </summary>
public sealed class RepoGraphCanvas : Control, ICustomHitTest
{
    public bool HitTest(Point point) => true;

    public static readonly StyledProperty<RepoGraph?> GraphProperty =
        AvaloniaProperty.Register<RepoGraphCanvas, RepoGraph?>(nameof(Graph));

    /// <summary>When true, clicking a chip or an edge opens the owner-assignment menu.</summary>
    public static readonly StyledProperty<bool> IsEditableProperty =
        AvaloniaProperty.Register<RepoGraphCanvas, bool>(nameof(IsEditable));

    /// <summary>Invoked with a <see cref="SetPortOwnerRequest"/> when an owner is assigned or cleared.</summary>
    public static readonly StyledProperty<ICommand?> SetOwnerCommandProperty =
        AvaloniaProperty.Register<RepoGraphCanvas, ICommand?>(nameof(SetOwnerCommand));

    /// <summary>Invoked with a repo name (string) when a repo node is clicked (to edit its inputs).</summary>
    public static readonly StyledProperty<ICommand?> EditRepoCommandProperty =
        AvaloniaProperty.Register<RepoGraphCanvas, ICommand?>(nameof(EditRepoCommand));

    public RepoGraph? Graph { get => GetValue(GraphProperty); set => SetValue(GraphProperty, value); }
    public bool IsEditable { get => GetValue(IsEditableProperty); set => SetValue(IsEditableProperty, value); }
    public ICommand? SetOwnerCommand { get => GetValue(SetOwnerCommandProperty); set => SetValue(SetOwnerCommandProperty, value); }
    public ICommand? EditRepoCommand { get => GetValue(EditRepoCommandProperty); set => SetValue(EditRepoCommandProperty, value); }

    // Palette (mirrors App.axaml / WiringCanvas).
    static readonly IBrush Bg = Brush.Parse("#181820");
    static readonly IBrush Panel = Brush.Parse("#1F1F2A");
    static readonly IBrush PanelHead = Brush.Parse("#14141B");
    static readonly IBrush Fg = Brush.Parse("#E1E1EB");
    static readonly IBrush Title = Brush.Parse("#F5F5FA");
    static readonly IBrush Muted = Brush.Parse("#8C8CA0");
    static readonly IBrush Border = Brush.Parse("#2D2D3C");
    static readonly IBrush Wire = Brush.Parse("#60A5FA");
    static readonly IBrush Signal = Brush.Parse("#4ADE80");   // producer / owned
    static readonly IBrush Share = Brush.Parse("#FBBF24");    // shared-port chips
    static readonly IBrush Danger = Brush.Parse("#F87171");

    const double NodeW = 196, HeadH = 30, InputRowH = 18, NodePadY = 10;
    const double HGap = 56, VGap = 104;          // spacing between nodes / layers
    const double ChipH = 22, ChipGapY = 6, ChipBandGap = 10; // chip band sits above a node
    const double Pad = 40;                                    // outer padding around the whole board

    readonly Typeface _mono = new("Consolas");

    // Layout, rebuilt each measure. Positions persist across rebuilds (keyed by repo) so a hand-drag
    // survives an owner-assignment re-render; they reset only when the set of repos changes.
    readonly Dictionary<string, Rect> _nodeRects = new(StringComparer.Ordinal);
    readonly Dictionary<string, Point> _manualPos = new(StringComparer.Ordinal); // top-left overrides from dragging
    readonly List<(string Port, string Owner, string Consumer, Point A, Point C1, Point C2, Point B, Point Label)> _edges = new();
    readonly List<(Rect Rect, string Port, string Repo)> _chips = new();
    string? _layoutSignature; // the repo-set the auto-layout was computed for

    RepoGraph? _laidOut;
    Size _size = new(400, 300);

    string? _hoverRepo;
    int _hoverEdge = -1;   // index into _edges of the line under the cursor, or -1
    string? _dragRepo;
    Vector _dragOffset;   // cursor→node-top-left offset held during a drag (Point − Point is a Vector)
    Point _pressPos;
    bool _dragMoved;

    static RepoGraphCanvas()
    {
        AffectsMeasure<RepoGraphCanvas>(GraphProperty);
        AffectsRender<RepoGraphCanvas>(GraphProperty);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        BuildLayout();
        return _size;
    }

    // -- layout ---------------------------------------------------------------

    void BuildLayout()
    {
        _nodeRects.Clear();
        _edges.Clear();
        _chips.Clear();
        var g = Graph;
        _laidOut = g;
        if (g is null || g.Nodes.Count == 0) { _size = new Size(400, 240); return; }

        // Drop manual positions for repos that have gone, and force a fresh auto-layout whenever the set
        // of repos changes (added/removed) — a hand-tidy is only meaningful for a stable node set.
        var signature = string.Join("", g.Nodes.Select(n => n.Repo));
        if (signature != _layoutSignature)
        {
            var live = new HashSet<string>(g.Nodes.Select(n => n.Repo), StringComparer.Ordinal);
            foreach (var repo in _manualPos.Keys.ToList())
                if (!live.Contains(repo)) _manualPos.Remove(repo);
            _layoutSignature = signature;
        }

        var layers = AssignLayers(g);
        var order = OrderWithinLayers(g, layers);
        var heights = g.Nodes.ToDictionary(n => n.Repo, NodeHeight, StringComparer.Ordinal);

        // Place each layer as a centred horizontal row; layer index grows downward (roots on top).
        var auto = new Dictionary<string, Point>(StringComparer.Ordinal);
        double y = Pad;
        var layerKeys = order.Keys.OrderBy(k => k).ToList();
        foreach (var layer in layerKeys)
        {
            var row = order[layer];
            var rowW = row.Count * NodeW + (row.Count - 1) * HGap;
            var x = Pad + Math.Max(0, (MaxRowWidth(order) - rowW) / 2);
            var rowH = row.Max(r => heights[r]);
            var chipBand = row.Max(r => ChipBandHeight(g.Nodes.First(n => n.Repo == r)));
            y += chipBand; // leave room for the chip band above this layer's nodes
            foreach (var repo in row)
            {
                auto[repo] = new Point(x, y);
                x += NodeW + HGap;
            }
            y += rowH + VGap;
        }

        // Node rects: a manual (dragged) position wins over the auto one.
        foreach (var node in g.Nodes)
        {
            var topLeft = _manualPos.TryGetValue(node.Repo, out var m) ? m : auto[node.Repo];
            _nodeRects[node.Repo] = new Rect(topLeft, new Size(NodeW, heights[node.Repo]));
        }

        // Chips: a row of pills just above each node's top edge (wrapping across the node width).
        foreach (var node in g.Nodes)
        {
            if (node.Chips.Count == 0) continue;
            var rect = _nodeRects[node.Repo];
            var perRow = Math.Max(1, (int)((NodeW + ChipGapY) / (ChipW(node) + ChipGapY)));
            for (var i = 0; i < node.Chips.Count; i++)
            {
                var col = i % perRow;
                var rowIdx = i / perRow;
                var w = ChipW(node);
                var cx = rect.X + col * (w + ChipGapY);
                var rowsTotal = (node.Chips.Count + perRow - 1) / perRow;
                var cy = rect.Y - ChipBandGap - (rowsTotal - rowIdx) * (ChipH + ChipGapY) + ChipGapY;
                _chips.Add((new Rect(cx, cy, w, ChipH), node.Chips[i].Port, node.Repo));
            }
        }

        // Edges: route from the owner's border to the consumer's border along the line between their
        // centres (so an edge never dives out the wrong side), and spread parallel edges — the classic
        // mutual A↔B pair, but any repos with more than one line between them — into separate curved
        // lanes on opposite sides so they don't overlap into an unreadable knot.
        var pairTotal = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var e in g.Edges) pairTotal[PairKey(e.Owner, e.Consumer)] = pairTotal.GetValueOrDefault(PairKey(e.Owner, e.Consumer)) + 1;
        var pairSeen = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var e in g.Edges)
        {
            if (!_nodeRects.TryGetValue(e.Owner, out var o) || !_nodeRects.TryGetValue(e.Consumer, out var c)) continue;
            var key = PairKey(e.Owner, e.Consumer);
            var total = pairTotal[key];
            var idx = pairSeen.GetValueOrDefault(key);
            pairSeen[key] = idx + 1;

            // A perpendicular axis fixed to the pair (min→max repo) so the two directions of a mutual
            // pair push to opposite, consistent sides rather than both landing on the same lane.
            var (lo, hi) = string.CompareOrdinal(e.Owner, e.Consumer) <= 0 ? (o, c) : (c, o);
            var perp = Norm(new Vector(-(hi.Center.Y - lo.Center.Y), hi.Center.X - lo.Center.X));
            var lane = idx - (total - 1) / 2.0;              // …,-1,0,1,… centred on 0
            var endShift = perp * (lane * 15);
            var bulge = perp * (lane * 46);

            var dir = c.Center - o.Center;
            var a = BorderPoint(o, dir) + endShift;
            var b = BorderPoint(c, new Vector(-dir.X, -dir.Y)) + endShift;
            var along = Norm(b - a);
            var handle = Math.Max(30, Distance(a, b) * 0.35);
            var c1 = a + along * handle + bulge;
            var c2 = b - along * handle + bulge;
            _edges.Add((e.Port, e.Owner, e.Consumer, a, c1, c2, b, BezierAt(a, c1, c2, b, 0.5)));
        }

        var maxX = _nodeRects.Values.Max(r => r.Right);
        var maxY = _nodeRects.Values.Max(r => r.Bottom);
        _size = new Size(maxX + Pad, maxY + Pad);
    }

    /// <summary>Longest-path layering from the dependency roots; bounded iteration tolerates cycles.</summary>
    static Dictionary<string, int> AssignLayers(RepoGraph g)
    {
        var layer = g.Nodes.ToDictionary(n => n.Repo, _ => 0, StringComparer.Ordinal);
        for (var iter = 0; iter < g.Nodes.Count; iter++)
        {
            var changed = false;
            foreach (var e in g.Edges)
                if (layer.TryGetValue(e.Owner, out var lo) && layer.TryGetValue(e.Consumer, out var lc) && lc < lo + 1)
                {
                    layer[e.Consumer] = lo + 1;
                    changed = true;
                }
            if (!changed) break; // converged (or a cycle capped by the iteration bound)
        }
        return layer;
    }

    /// <summary>Group repos by layer, then a barycentre sweep to reduce crossings between adjacent layers.</summary>
    static Dictionary<int, List<string>> OrderWithinLayers(RepoGraph g, Dictionary<string, int> layer)
    {
        var byLayer = new Dictionary<int, List<string>>();
        foreach (var node in g.Nodes) // seed in stack order for determinism
            (byLayer.TryGetValue(layer[node.Repo], out var l) ? l : byLayer[layer[node.Repo]] = new()).Add(node.Repo);

        var keys = byLayer.Keys.OrderBy(k => k).ToList();
        var parents = g.Edges.GroupBy(e => e.Consumer)
            .ToDictionary(gr => gr.Key, gr => gr.Select(e => e.Owner).ToList(), StringComparer.Ordinal);

        // One downward sweep: order each layer by the average index of its parents in the layer above.
        for (var ki = 1; ki < keys.Count; ki++)
        {
            var above = byLayer[keys[ki - 1]];
            var index = above.Select((r, i) => (r, i)).ToDictionary(t => t.r, t => (double)t.i, StringComparer.Ordinal);
            byLayer[keys[ki]] = byLayer[keys[ki]]
                .Select((r, i) => (r, bary: parents.TryGetValue(r, out var ps) && ps.Count > 0
                    ? ps.Where(index.ContainsKey).Select(p => index[p]).DefaultIfEmpty(i).Average()
                    : i))
                .OrderBy(t => t.bary)
                .Select(t => t.r)
                .ToList();
        }
        return byLayer;
    }

    static double MaxRowWidth(Dictionary<int, List<string>> order) =>
        order.Values.Max(row => row.Count * NodeW + (row.Count - 1) * HGap);

    static double NodeHeight(RepoGraphNode n) => HeadH + Math.Max(1, n.Inputs.Count) * InputRowH + NodePadY
                                                 + (n.Owns.Count > 0 ? InputRowH : 0);

    double ChipW(RepoGraphNode n) => Math.Min(NodeW, 84);
    double ChipBandHeight(RepoGraphNode n)
    {
        if (n.Chips.Count == 0) return 0;
        var perRow = Math.Max(1, (int)((NodeW + ChipGapY) / (ChipW(n) + ChipGapY)));
        var rows = (n.Chips.Count + perRow - 1) / perRow;
        return rows * (ChipH + ChipGapY) + ChipBandGap;
    }

    // -- render ---------------------------------------------------------------

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Bg, new Rect(default, _size));
        var g = _laidOut;
        if (g is null) return;

        // Edges first, under the nodes. Port labels are drill-in detail, so they stay hidden at rest and
        // reveal only on hover — of the line itself, or of a repo it touches — which keeps the resting
        // view about structure (who depends on whom) and never a mat of overlapping text.
        for (var i = 0; i < _edges.Count; i++)
        {
            var e = _edges[i];
            var dim = _hoverRepo is not null && _hoverRepo != e.Owner && _hoverRepo != e.Consumer;
            var hot = i == _hoverEdge;
            var brush = dim ? new SolidColorBrush(((ISolidColorBrush)Wire).Color, 0.18) : Wire;
            var pen = new Pen(brush, hot ? 3.4 : 2.4) { LineCap = PenLineCap.Round };
            ctx.DrawGeometry(null, pen, CubicPath(e.A, e.C1, e.C2, e.B));
            DrawArrowHead(ctx, e.B, Norm(e.B - e.C2), brush);
            var showLabel = hot || (_hoverRepo is not null && (e.Owner == _hoverRepo || e.Consumer == _hoverRepo));
            if (showLabel) DrawEdgeLabel(ctx, e.Label, e.Port);
        }

        // Chips (shared / unowned port pills above their consumer).
        foreach (var (rect, port, repo) in _chips)
        {
            var dim = _hoverRepo is not null && _hoverRepo != repo;
            using var _ = ctx.PushOpacity(dim ? 0.3 : 1.0);
            var count = _laidOut?.Nodes.FirstOrDefault(n => n.Repo == repo)?
                .Chips.FirstOrDefault(c => c.Port == port)?.UsedBy ?? 1;
            var label = count > 1 ? $"{port} ×{count}" : port;
            ctx.DrawRectangle(Brush.Parse("#2A2313"), new Pen(Share, 1.3), rect, ChipH / 2, ChipH / 2);
            DrawText(ctx, Truncate(label, rect.Width - 14, 10.5), rect, Share, 10.5, center: true);
            // Little stem down to the node top.
            var stem = new Pen(Share, 1.3);
            var sx = rect.Center.X;
            if (_nodeRects.TryGetValue(repo, out var nr))
                ctx.DrawLine(stem, new Point(sx, rect.Bottom), new Point(sx, Math.Min(nr.Top, rect.Bottom + 6)));
        }

        // Nodes.
        foreach (var node in g.Nodes)
        {
            if (!_nodeRects.TryGetValue(node.Repo, out var r)) continue;
            var dim = _hoverRepo is not null && _hoverRepo != node.Repo
                      && !_edges.Any(e => (e.Owner == _hoverRepo && e.Consumer == node.Repo)
                                       || (e.Consumer == _hoverRepo && e.Owner == node.Repo));
            using var _ = ctx.PushOpacity(dim ? 0.4 : 1.0);

            var owns = node.Owns.Count > 0;
            ctx.DrawRectangle(Panel, new Pen(owns ? Signal : Border, owns ? 1.6 : 1.4), r, 12, 12);
            ctx.DrawRectangle(PanelHead, null, new Rect(r.X, r.Y, NodeW, HeadH), 12, 12);
            DrawText(ctx, node.Repo, new Rect(r.X + 12, r.Y, NodeW - 24, HeadH), Title, 13, vcenter: true);

            var rowY = r.Y + HeadH + 4;
            if (owns)
            {
                DrawText(ctx, "serves " + Truncate(string.Join(", ", node.Owns), NodeW - 60, 10),
                    new Rect(r.X + 12, rowY, NodeW - 20, InputRowH), Signal, 10, vcenter: true);
                rowY += InputRowH;
            }

            // Input pins (the little dots), one per declared input.
            foreach (var input in node.Inputs)
            {
                var cy = rowY + InputRowH / 2;
                ctx.DrawEllipse(PanelHead, new Pen(Muted, 1.4), new Point(r.X + 16, cy), 3.5, 3.5);
                DrawText(ctx, Truncate(input, NodeW - 44, 10.5), new Rect(r.X + 28, rowY, NodeW - 40, InputRowH),
                    Fg, 10.5, vcenter: true);
                rowY += InputRowH;
            }
            if (node.Inputs.Count == 0)
                DrawText(ctx, "no inputs", new Rect(r.X + 16, rowY, NodeW - 24, InputRowH), Muted, 10, vcenter: true);
        }
    }

    void DrawEdgeLabel(DrawingContext ctx, Point at, string port)
    {
        var ft = Ft(port, 10.5, Wire);
        var pad = 5;
        var box = new Rect(at.X - ft.Width / 2 - pad, at.Y - ft.Height / 2 - 2, ft.Width + pad * 2, ft.Height + 4);
        ctx.DrawRectangle(Bg, new Pen(Wire, 1), box, 5, 5);
        ctx.DrawText(ft, new Point(box.X + pad, box.Y + 2));
    }

    /// <summary>An arrowhead at <paramref name="tip"/> pointing along <paramref name="dir"/> (unit).</summary>
    static void DrawArrowHead(DrawingContext ctx, Point tip, Vector dir, IBrush brush)
    {
        if (dir.X == 0 && dir.Y == 0) dir = new Vector(0, 1);
        const double s = 8;
        var back = tip - dir * s;
        var perp = new Vector(-dir.Y, dir.X) * (s * 0.55);
        var geo = new StreamGeometry();
        using (var c = geo.Open())
        {
            c.BeginFigure(new Point(back.X + perp.X, back.Y + perp.Y), true);
            c.LineTo(new Point(back.X - perp.X, back.Y - perp.Y));
            c.LineTo(tip);
            c.EndFigure(true);
        }
        ctx.DrawGeometry(brush, null, geo);
    }

    static StreamGeometry CubicPath(Point a, Point c1, Point c2, Point b)
    {
        var geo = new StreamGeometry();
        using var c = geo.Open();
        c.BeginFigure(a, false);
        c.CubicBezierTo(c1, c2, b);
        c.EndFigure(false);
        return geo;
    }

    /// <summary>Where the ray from <paramref name="r"/>'s centre in direction <paramref name="dir"/> meets its border.</summary>
    static Point BorderPoint(Rect r, Vector dir)
    {
        if (dir.X == 0 && dir.Y == 0) return r.Center;
        var tx = dir.X != 0 ? (r.Width / 2) / Math.Abs(dir.X) : double.PositiveInfinity;
        var ty = dir.Y != 0 ? (r.Height / 2) / Math.Abs(dir.Y) : double.PositiveInfinity;
        var t = Math.Min(tx, ty);
        return new Point(r.Center.X + dir.X * t, r.Center.Y + dir.Y * t);
    }

    static Vector Norm(Vector v)
    {
        var len = Math.Sqrt(v.X * v.X + v.Y * v.Y);
        return len < 1e-6 ? default : new Vector(v.X / len, v.Y / len);
    }

    static Point BezierAt(Point p0, Point p1, Point p2, Point p3, double t)
    {
        var u = 1 - t;
        double w0 = u * u * u, w1 = 3 * u * u * t, w2 = 3 * u * t * t, w3 = t * t * t;
        return new Point(w0 * p0.X + w1 * p1.X + w2 * p2.X + w3 * p3.X,
                         w0 * p0.Y + w1 * p1.Y + w2 * p2.Y + w3 * p3.Y);
    }

    /// <summary>A stable key for the unordered pair of repos an edge connects.</summary>
    static string PairKey(string a, string b) =>
        string.CompareOrdinal(a, b) <= 0 ? a + "\0" + b : b + "\0" + a;

    FormattedText Ft(string text, double size, IBrush brush) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _mono, size, brush);

    void DrawText(DrawingContext ctx, string text, Rect within, IBrush brush, double size,
        bool center = false, bool vcenter = false)
    {
        var ft = Ft(text, size, brush);
        var x = center ? within.X + (within.Width - ft.Width) / 2 : within.X;
        var y = (center || vcenter) ? within.Y + (within.Height - ft.Height) / 2 : within.Y;
        ctx.DrawText(ft, new Point(x, y));
    }

    string Truncate(string s, double maxWidth, double size)
    {
        if (Ft(s, size, Fg).Width <= maxWidth) return s;
        for (var len = s.Length - 1; len > 1; len--)
        {
            var t = s[..len] + "…";
            if (Ft(t, size, Fg).Width <= maxWidth) return t;
        }
        return "…";
    }

    // -- interaction ----------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var pos = e.GetPosition(this);
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            // A chip or an edge opens the ownership menu (editing only).
            if (IsEditable && ChipAt(pos) is { } chip)
            {
                OpenChipMenu(chip.Port);
                e.Handled = true;
            }
            else if (IsEditable && EdgeAt(pos) is { } edge)
            {
                OpenEdgeMenu(edge.Port, edge.Owner);
                e.Handled = true;
            }
            else if (NodeAt(pos) is { } repo && _nodeRects.TryGetValue(repo, out var r))
            {
                _dragRepo = repo;
                _dragOffset = pos - r.TopLeft;
                _pressPos = pos;
                _dragMoved = false;
                e.Pointer.Capture(this);
                e.Handled = true;
            }
        }
        base.OnPointerPressed(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var pos = e.GetPosition(this);
        if (_dragRepo is { } repo)
        {
            if (!_dragMoved && Distance(_pressPos, pos) > 3) _dragMoved = true;
            if (_dragMoved)
            {
                _manualPos[repo] = new Point(Math.Max(Pad, pos.X - _dragOffset.X), Math.Max(4, pos.Y - _dragOffset.Y));
                InvalidateMeasure();
                InvalidateVisual();
            }
            return;
        }

        // A node under the cursor reveals its edges' labels; otherwise a line under the cursor reveals
        // its own. Node wins so hovering a repo doesn't flicker between the two.
        var node = NodeAt(pos);
        var edge = node is null ? EdgeIndexAt(pos) : -1;
        if (node != _hoverRepo || edge != _hoverEdge)
        {
            _hoverRepo = node;
            _hoverEdge = edge;
            InvalidateVisual();
        }
        base.OnPointerMoved(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_dragRepo is { } repo)
        {
            e.Pointer.Capture(null);
            // A press that never moved is a click: open that repo's input editor. A drag just repositions.
            if (!_dragMoved && IsEditable) EditRepoCommand?.Execute(repo);
            _dragRepo = null;
            _dragMoved = false;
            e.Handled = true;
        }
        base.OnPointerReleased(e);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        if (_hoverRepo is not null || _hoverEdge >= 0) { _hoverRepo = null; _hoverEdge = -1; InvalidateVisual(); }
        base.OnPointerExited(e);
    }

    string? NodeAt(Point p)
    {
        foreach (var kv in _nodeRects)
            if (kv.Value.Contains(p)) return kv.Key;
        return null;
    }

    (string Port, string Repo)? ChipAt(Point p)
    {
        foreach (var (rect, port, repo) in _chips)
            if (rect.Contains(p)) return (port, repo);
        return null;
    }

    (string Port, string Owner)? EdgeAt(Point p) =>
        EdgeIndexAt(p) is var i && i >= 0 ? (_edges[i].Port, _edges[i].Owner) : null;

    /// <summary>Index of the line under <paramref name="p"/> (near its label or its actual bezier), or -1.</summary>
    int EdgeIndexAt(Point p)
    {
        for (var i = 0; i < _edges.Count; i++)
        {
            var e = _edges[i];
            if (Distance(e.Label, p) < 20) return i;
            // Sample the curve — the lanes bow away from the straight line, so test the actual bezier.
            var prev = e.A;
            for (var s = 1; s <= 16; s++)
            {
                var pt = BezierAt(e.A, e.C1, e.C2, e.B, s / 16.0);
                if (DistanceToSegment(prev, pt, p) < 7) return i;
                prev = pt;
            }
        }
        return -1;
    }

    /// <summary>Clicking a shared/unowned chip: pick which repo owns the port (promotes it to a line).</summary>
    void OpenChipMenu(string port)
    {
        var flyout = new MenuFlyout();
        var header = new MenuItem { Header = $"Owner of '{port}'", IsEnabled = false };
        flyout.Items.Add(header);
        foreach (var repo in RepoNames())
        {
            var item = new MenuItem { Header = repo };
            item.Click += (_, _) => SetOwnerCommand?.Execute(new SetPortOwnerRequest(port, repo));
            flyout.Items.Add(item);
        }
        flyout.ShowAt(this, showAtPointer: true);
    }

    /// <summary>Clicking a dependency line: change the owner, or clear it back to a shared chip.</summary>
    void OpenEdgeMenu(string port, string currentOwner)
    {
        var flyout = new MenuFlyout();
        flyout.Items.Add(new MenuItem { Header = $"'{port}' owned by {currentOwner}", IsEnabled = false });
        foreach (var repo in RepoNames().Where(r => r != currentOwner))
        {
            var item = new MenuItem { Header = "Owner → " + repo };
            item.Click += (_, _) => SetOwnerCommand?.Execute(new SetPortOwnerRequest(port, repo));
            flyout.Items.Add(item);
        }
        var clear = new MenuItem { Header = "Clear owner (make shared)" };
        clear.Click += (_, _) => SetOwnerCommand?.Execute(new SetPortOwnerRequest(port, null));
        flyout.Items.Add(clear);
        flyout.ShowAt(this, showAtPointer: true);
    }

    IEnumerable<string> RepoNames() => _laidOut?.Nodes.Select(n => n.Repo) ?? [];

    static double Distance(Point a, Point b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    static double DistanceToSegment(Point a, Point b, Point p)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var len2 = dx * dx + dy * dy;
        if (len2 < 1e-6) return Distance(a, p);
        var t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1);
        return Distance(new Point(a.X + t * dx, a.Y + t * dy), p);
    }
}
