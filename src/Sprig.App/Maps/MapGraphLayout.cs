using Avalonia;
using Microsoft.Msagl.Core.Geometry.Curves;
using Microsoft.Msagl.Core.Layout;
using Microsoft.Msagl.Core.Routing;
using Microsoft.Msagl.Layout.Layered;
using Microsoft.Msagl.Miscellaneous;
using Sprig.Core.Maps;
using MsaglPoint = Microsoft.Msagl.Core.Geometry.Point;

namespace Sprig.App.Maps;

public enum GraphNodeKind
{
    Module,
    /// <summary>A placeholder for an unmet need: nothing in the map provides it.</summary>
    Gap,
    /// <summary>A constant the map sets for a need nothing provides.</summary>
    Default,
}

public enum GraphEdgeKind { Wired, Ambiguous, Gap, Default, Similar }

/// <param name="Boxed">True when the module sits inside its repo's box (the repo has several modules), so the
/// node is titled by module rather than by repo.</param>
public sealed record GraphNode(string Id, GraphNodeKind Kind, Rect Bounds, ModuleInfo? Module, NeedInfo? Need, bool Boxed);

public sealed record GraphCluster(string Repo, Rect Bounds);

/// <param name="Path">The route as a polyline, ending where the arrowhead starts.</param>
public sealed record GraphEdge(string From, string To, GraphEdgeKind Kind, IReadOnlyList<string> Labels,
    IReadOnlyList<Point> Path, Point ArrowTip, Rect LabelBounds);

/// <summary>
/// Lays a <see cref="MapInspection"/> out as a layered graph: each module is a node, a module sits above
/// what it relies on, and edges point from the needing module to its provider. A repo with several modules
/// is a box around them. An unmet need ends in a placeholder node, and a constant set on the map is a node
/// of its own, so neither just disappears. Node sizes follow <see cref="GraphMetrics"/>, which the canvas
/// draws with, so text fits the boxes it was measured for.
/// </summary>
public sealed record MapGraphLayout(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphCluster> Clusters,
    IReadOnlyList<GraphEdge> Edges, Size Size)
{
    public static string ModuleId(string repo, string module) => $"M:{repo}/{module}";
    public static string GapId(NeedInfo n) => $"G:{n.Repo}/{n.Module}/{n.Capability}";
    public static string DefaultId(NeedInfo n) => $"D:{n.Repo}/{n.Module}/{n.Capability}";

    public static MapGraphLayout Compute(MapInspection inspection)
    {
        var graph = new GeometryGraph();
        // MSAGL keys per-cluster settings by UserData, the root included, and can't take a null key.
        graph.RootCluster.UserData = RootKey;
        var nodes =new Dictionary<string, (Node Node, GraphNodeKind Kind, ModuleInfo? Module, NeedInfo? Need, bool Boxed)>();

        Node Add(string id, Size size, GraphNodeKind kind, ModuleInfo? module, NeedInfo? need, bool boxed)
        {
            var node = new Node(CurveFactory.CreateRectangle(size.Width, size.Height, new MsaglPoint()), id);
            graph.Nodes.Add(node);
            nodes[id] = (node, kind, module, need, boxed);
            return node;
        }

        foreach (var repo in inspection.Repos)
        {
            var boxed = repo.Modules.Count > 1;
            var members = repo.Modules.Select(m => Add(ModuleId(m.Repo, m.Module), GraphMetrics.ModuleSize(m, boxed),
                GraphNodeKind.Module, m, null, boxed)).ToList();
            if (!boxed) continue;
            var cluster = new Cluster(members)
            {
                UserData = repo.Name,
                RectangularBoundary = new Microsoft.Msagl.Core.Geometry.RectangularClusterBoundary
                {
                    TopMargin = GraphMetrics.ClusterTitle, LeftMargin = GraphMetrics.ClusterPad,
                    RightMargin = GraphMetrics.ClusterPad, BottomMargin = GraphMetrics.ClusterPad,
                },
            };
            graph.RootCluster.AddChild(cluster);
        }

        // One edge per pair and kind, so a wired need and an ambiguous one between the same modules keep
        // their own colours; needs of the same kind share an edge and list their capabilities on it.
        var edges = new Dictionary<(string From, string To, GraphEdgeKind Kind), List<string>>();
        void Link(string from, string to, GraphEdgeKind kind, string label)
        {
            if (from == to) return;
            if (!edges.TryGetValue((from, to, kind), out var labels)) edges[(from, to, kind)] = labels = [];
            if (!labels.Contains(label)) labels.Add(label);
        }

        foreach (var need in inspection.Needs)
        {
            var from = ModuleId(need.Repo, need.Module);
            var label = need.Target == need.Capability ? need.Capability : $"{need.Capability} → {need.Target}";
            switch (need.State)
            {
                case NeedState.Wired:
                case NeedState.Ambiguous:
                    foreach (var p in need.Providers)
                        Link(from, ModuleId(p.Repo, p.Module), need.State == NeedState.Wired ? GraphEdgeKind.Wired : GraphEdgeKind.Ambiguous, label);
                    break;
                case NeedState.Default:
                    Add(DefaultId(need), GraphMetrics.PlaceholderSize(need), GraphNodeKind.Default, null, need, false);
                    Link(from, DefaultId(need), GraphEdgeKind.Default, need.Capability);
                    break;
                case NeedState.Gap:
                    Add(GapId(need), GraphMetrics.PlaceholderSize(need), GraphNodeKind.Gap, null, need, false);
                    Link(from, GapId(need), GraphEdgeKind.Gap, need.Capability);
                    if (need.Similar is { } s)
                        Link(GapId(need), ModuleId(s.Repo, s.Module), GraphEdgeKind.Similar, "similar name");
                    break;
            }
        }

        var msaglEdges = new List<(Edge Edge, string From, string To, GraphEdgeKind Kind, List<string> Labels)>();
        var proxies = new HashSet<(Node, Node)>();
        foreach (var ((from, to, kind), labels) in edges)
        {
            if (!nodes.ContainsKey(from) || !nodes.ContainsKey(to)) continue;
            var (source, target) = (nodes[from].Node, nodes[to].Node);
            var labelSize = GraphMetrics.EdgeLabelSize(labels);
            var edge = new Edge(source, target, labelSize.Width, labelSize.Height, 1.8);
            edge.Label.GeometryParent = edge;
            edge.EdgeGeometry.TargetArrowhead = new Arrowhead { Length = GraphMetrics.ArrowLength };
            graph.Edges.Add(edge);
            msaglEdges.Add((edge, from, to, kind, labels));

            // MSAGL lays each repo box out as one block and ignores edges crossing its border when it layers
            // the top level, so a box would float free of what it relies on. A stand-in edge between the
            // box and the outside end keeps it in order; it is never drawn.
            var outerSource = (Node?)Box(source) ?? source;
            var outerTarget = (Node?)Box(target) ?? target;
            if ((outerSource != source || outerTarget != target) && outerSource != outerTarget && proxies.Add((outerSource, outerTarget)))
                graph.Edges.Add(new Edge(outerSource, outerTarget) { Weight = 1 });
        }

        if (graph.Nodes.Count == 0)
            return new MapGraphLayout([], [], [], new Size(0, 0));

        var settings = new SugiyamaLayoutSettings
        {
            NodeSeparation = GraphMetrics.NodeSeparation,
            LayerSeparation = GraphMetrics.LayerSeparation,
            ClusterMargin = GraphMetrics.ClusterSeparation,
            RandomSeedForOrdering = 1,
            EdgeRoutingSettings = { EdgeRoutingMode = EdgeRoutingMode.SugiyamaSplines },
        };
        LayoutHelpers.CalculateLayout(graph, settings, null);

        // MSAGL's y axis points up; flip it and move the drawing's top-left corner to (margin, margin).
        var bounds = graph.BoundingBox;
        var m = GraphMetrics.Margin;
        Point Screen(MsaglPoint p) => new(p.X - bounds.Left + m, bounds.Top - p.Y + m);
        Rect ScreenRect(Microsoft.Msagl.Core.Geometry.Rectangle r) => new(r.Left - bounds.Left + m, bounds.Top - r.Top + m, r.Width, r.Height);

        var layoutNodes = nodes.Select(kv => new GraphNode(kv.Key, kv.Value.Kind, ScreenRect(kv.Value.Node.BoundingBox),
            kv.Value.Module, kv.Value.Need, kv.Value.Boxed)).ToList();
        var clusters = graph.RootCluster.AllClustersDepthFirst()
            .Where(c => c != graph.RootCluster && c.UserData is string)
            .Select(c => new GraphCluster((string)c.UserData, ScreenRect(c.BoundingBox)))
            .ToList();
        var layoutEdges = msaglEdges.Select(e =>
        {
            var curve = e.Edge.Curve;
            var path = Sample(curve).Select(Screen).ToList();
            var tip = e.Edge.EdgeGeometry.TargetArrowhead is { } head ? Screen(head.TipPosition) : path[^1];
            var label = e.Edge.Label is { } l
                ? new Rect(Screen(l.Center) - new Point(l.Width / 2, l.Height / 2), new Size(l.Width, l.Height))
                : default;
            return new GraphEdge(e.From, e.To, e.Kind, e.Labels, path, tip, label);
        }).ToList();

        return new MapGraphLayout(layoutNodes, clusters, layoutEdges, new Size(bounds.Width + 2 * m, bounds.Height + 2 * m));
    }

    static readonly object RootKey = new();

    static Cluster? Box(Node node) => node.ClusterParent is { UserData: string } box ? box : null;

    static IEnumerable<MsaglPoint> Sample(ICurve? curve)
    {
        if (curve is null) yield break;
        const int steps = 40;
        for (var i = 0; i <= steps; i++)
            yield return curve[curve.ParStart + (curve.ParEnd - curve.ParStart) * i / steps];
    }
}

/// <summary>The sizes the layout measures nodes with and the canvas draws them at. Text is estimated from
/// character counts in the fonts the canvas uses, so the two stay in step without a text-layout pass.</summary>
public static class GraphMetrics
{
    public const double Margin = 24;
    public const double NodeSeparation = 40;
    public const double LayerSeparation = 70;
    public const double ClusterSeparation = 18;
    public const double ClusterTitle = 30;
    public const double ClusterPad = 12;
    public const double ArrowLength = 8;

    public const double HeaderHeight = 50;
    public const double RowHeight = 22;
    public const double BottomPad = 10;
    public const double SidePad = 14;

    public const double TitleSize = 14;
    public const double SubSize = 11;
    public const double CapSize = 12;
    public const double MetaSize = 10.5;
    public const double LabelSize = 11;

    public const double MonoCharWidth = 0.6;   // per em, for the monospace face
    public const double SansCharWidth = 0.58;  // per em, a generous average for the UI face at semibold

    public static double Mono(string text, double size) => text.Length * size * MonoCharWidth;
    public static double Sans(string text, double size) => text.Length * size * SansCharWidth;

    public static string Title(ModuleInfo m, bool boxed) => boxed ? m.Module : m.Repo;
    public static string Subtitle(ModuleInfo m, bool boxed) => boxed ? (m.Path.Length == 0 ? "repo root" : m.Path) : $"module {m.Module}";
    public static string HubBadge(ModuleInfo m) => $"needed by {m.ReliedOnBy}";
    public static string ProvideMeta(ProvideInfo p)
        => p.Constants is { } c ? "= " + string.Join(", ", c.Values) : string.Join(" · ", p.Outputs);

    public static Size ModuleSize(ModuleInfo m, bool boxed)
    {
        var header = Sans(Title(m, boxed), TitleSize) + (m.ReliedOnBy >= 2 ? Sans(HubBadge(m), 10.5) + 30 : 0);
        var rows = m.Provides.Select(p => 30 + Mono(p.Capability, CapSize) + 16 + Mono(ProvideMeta(p), MetaSize));
        var width = Math.Clamp(Math.Max(Math.Max(header, Mono(Subtitle(m, boxed), SubSize)) + 2 * SidePad, rows.DefaultIfEmpty(0).Max() + SidePad), 200, 420);
        return new Size(Math.Ceiling(width), HeaderHeight + Math.Max(1, m.Provides.Count) * RowHeight + BottomPad);
    }

    public static string PlaceholderSubtitle(NeedInfo n) => n.State == NeedState.Default
        ? string.Join(", ", n.DefaultValues!.Select(v => $"{v.Key} = {v.Value}"))
        : "nothing provides this";

    public static string? PlaceholderNote(NeedInfo n) => n.Similar is { } s ? $"similar: {s.Capability}" : null;

    public static Size PlaceholderSize(NeedInfo n)
    {
        var width = new[] { Mono(n.Capability, 13), Mono(PlaceholderSubtitle(n), SubSize), Mono(PlaceholderNote(n) ?? "", SubSize) }.Max() + 2 * SidePad + 8;
        return new Size(Math.Ceiling(Math.Clamp(width, 180, 380)), PlaceholderNote(n) is null ? 56 : 72);
    }

    public static Size EdgeLabelSize(IReadOnlyList<string> lines)
        => new(Math.Ceiling(lines.Max(l => Mono(l, LabelSize)) + 14), lines.Count * 15 + 6);
}
