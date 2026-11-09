using System;
using System.Collections.Generic;
using System.Linq;

namespace Sprig.App.ViewModels;

/// <summary>
/// A force-directed layout: nodes push each other apart, links pull their ends together, a group's (a repo's) own nodes
/// gather into a cluster, and the clusters' boxes are kept from overlapping. It starts from the same place every time
/// (each group at its spot on a ring, nodes in key order), so a workspace lays out the same way on every visit. A pinned
/// node stays where it was put and the rest settle around it.
/// </summary>
public sealed class ForceLayout
{
    public sealed class Body(string key, string group, double radius)
    {
        public string Key => key;
        public string Group => group;
        public double Radius => radius;
        public double X { get; set; }
        public double Y { get; set; }
        public double Vx { get; set; }
        public double Vy { get; set; }
        public bool Pinned { get; set; }
    }

    /// <summary>A group's box: round its nodes, with room above for its title and below for their labels.</summary>
    public readonly record struct Box(double Left, double Top, double Right, double Bottom)
    {
        public bool Overlaps(Box other, double gap)
            => Left < other.Right + gap && other.Left < Right + gap && Top < other.Bottom + gap && other.Top < Bottom + gap;
    }

    public const double BoxAbove = 52;
    public const double BoxBelow = 62;
    public const double BoxSide = 6;

    // Tuned on workspaces of 5 to 40 nodes: far enough apart to read each label, close enough to fit a window.
    const double LinkLength = 150;
    const double LinkStrength = 0.5;
    // A link between groups pulls less, and nodes of different groups push harder, so each gathers into a cluster.
    const double CrossLinkStrength = 0.25;
    const double Charge = -1400;
    const double CrossCharge = 2.6;
    const double GroupPull = 0.22;
    // Weaker sideways than up and down, so the graph spreads to suit a window that's wider than it is tall.
    const double CentrePullX = 0.007;
    const double CentrePullY = 0.02;
    const double VelocityDecay = 0.55;
    const double CollisionGap = 18;
    const double BoxGap = 18;
    const double AlphaMin = 0.002;
    const double AlphaDecay = 0.025;

    readonly List<Body> _bodies;
    readonly Dictionary<string, Body> _byKey;
    readonly (Body From, Body To, double Strength)[] _links;
    readonly string? _loose;

    public double Alpha { get; private set; } = 1;
    public bool Settled => Alpha < AlphaMin;
    public IReadOnlyList<Body> Bodies => _bodies;

    /// <param name="loose">A group whose nodes aren't kept together or boxed (values from outside the workspace): each
    /// keeps clear of the boxes on its own.</param>
    public ForceLayout(IEnumerable<(string Key, string Group, double Radius)> nodes, IEnumerable<(string From, string To)> links,
        IReadOnlyDictionary<string, (double X, double Y)>? pinned = null, string? loose = null)
    {
        _loose = loose;
        _bodies = nodes.OrderBy(n => n.Group, StringComparer.Ordinal).ThenBy(n => n.Key, StringComparer.Ordinal)
            .Select(n => new Body(n.Key, n.Group, n.Radius)).ToList();
        _byKey = _bodies.ToDictionary(b => b.Key, StringComparer.Ordinal);

        // Each group starts at its own place on a ring, its nodes in a small spiral there with no two on top of each
        // other, so the first steps have somewhere to push from and a group doesn't have to find its way out of the others.
        var golden = Math.PI * (3 - Math.Sqrt(5));
        var groups = _bodies.Select(b => b.Group).Distinct().ToArray();
        var ring = groups.Length == 1 ? 0 : 140 * Math.Sqrt(groups.Length);
        for (var g = 0; g < groups.Length; g++)
        {
            var (cx, cy) = (1.6 * ring * Math.Cos(2 * Math.PI * g / groups.Length), ring * Math.Sin(2 * Math.PI * g / groups.Length));
            var members = _bodies.Where(b => b.Group == groups[g]).ToArray();
            for (var i = 0; i < members.Length; i++)
            {
                var r = 45 * Math.Sqrt(0.5 + i);
                members[i].X = cx + r * Math.Cos(i * golden);
                members[i].Y = cy + r * Math.Sin(i * golden);
            }
        }
        foreach (var (key, at) in pinned ?? new Dictionary<string, (double, double)>())
            if (_byKey.TryGetValue(key, out var body)) (body.X, body.Y, body.Pinned) = (at.X, at.Y, true);

        var pairs = links.Where(l => l.From != l.To && _byKey.ContainsKey(l.From) && _byKey.ContainsKey(l.To))
            .Select(l => string.CompareOrdinal(l.From, l.To) < 0 ? (l.From, l.To) : (l.To, l.From)).Distinct().ToArray();
        var degree = pairs.SelectMany(p => new[] { p.Item1, p.Item2 }).GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count());
        // A hub (the database every service uses) would otherwise drag its users into a knot.
        _links = pairs.Select(p => (_byKey[p.Item1], _byKey[p.Item2],
            (_byKey[p.Item1].Group == _byKey[p.Item2].Group ? LinkStrength : CrossLinkStrength) / Math.Min(degree[p.Item1], degree[p.Item2]))).ToArray();
    }

    public Body? Find(string key) => _byKey.GetValueOrDefault(key);

    /// <summary>The boxed groups, each with its box as it stands.</summary>
    public IEnumerable<(string Group, Box Box)> Boxes()
        => _bodies.Where(b => b.Group != _loose).GroupBy(b => b.Group).Select(g => (g.Key, BoxOf(g.ToArray())));

    static Box BoxOf(IReadOnlyCollection<Body> bodies)
    {
        var side = bodies.Max(b => b.Radius) + BoxSide;
        return new Box(bodies.Min(b => b.X) - side, bodies.Min(b => b.Y) - BoxAbove, bodies.Max(b => b.X) + side, bodies.Max(b => b.Y) + BoxBelow);
    }

    /// <summary>Run until it settles.</summary>
    public void Settle(int maxSteps = 800)
    {
        for (var i = 0; i < maxSteps && !Settled; i++) Step();
    }

    /// <summary>Wake it up again, gently, after a node was moved.</summary>
    public void Reheat(double alpha = 0.3) => Alpha = Math.Max(Alpha, alpha);

    public void Step()
    {
        if (Settled) return;
        var alpha = Alpha;

        foreach (var (a, b, strength) in _links)
        {
            var dx = b.X + b.Vx - a.X - a.Vx;
            var dy = b.Y + b.Vy - a.Y - a.Vy;
            var d = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
            var want = LinkLength + a.Radius + b.Radius;
            var f = (d - want) / d * alpha * strength;
            dx *= f;
            dy *= f;
            b.Vx -= dx / 2;
            b.Vy -= dy / 2;
            a.Vx += dx / 2;
            a.Vy += dy / 2;
        }

        for (var i = 0; i < _bodies.Count; i++)
            for (var j = i + 1; j < _bodies.Count; j++)
            {
                var a = _bodies[i];
                var b = _bodies[j];
                var dx = b.X - a.X;
                var dy = b.Y - a.Y;
                if (dx == 0 && dy == 0) dx = 0.5 * (j - i);
                var d2 = Math.Max(dx * dx + dy * dy, 400);
                var f = Charge * (a.Group == b.Group && a.Group != _loose ? 1 : CrossCharge) * alpha / d2;
                var d = Math.Sqrt(d2);
                a.Vx += dx / d * f;
                a.Vy += dy / d * f;
                b.Vx -= dx / d * f;
                b.Vy -= dy / d * f;
            }

        foreach (var group in _bodies.Where(b => b.Group != _loose).GroupBy(b => b.Group).Where(g => g.Count() > 1))
        {
            var cx = group.Average(b => b.X);
            var cy = group.Average(b => b.Y);
            foreach (var b in group)
            {
                b.Vx += (cx - b.X) * GroupPull * alpha;
                b.Vy += (cy - b.Y) * GroupPull * alpha;
            }
        }

        foreach (var b in _bodies)
        {
            b.Vx -= b.X * CentrePullX * alpha;
            b.Vy -= b.Y * CentrePullY * alpha;
        }

        foreach (var b in _bodies)
        {
            if (b.Pinned)
            {
                b.Vx = b.Vy = 0;
                continue;
            }
            b.Vx *= VelocityDecay;
            b.Vy *= VelocityDecay;
            b.X += b.Vx;
            b.Y += b.Vy;
        }

        SeparateBoxes();
        Separate();
        Alpha += (0 - Alpha) * AlphaDecay;
    }

    /// <summary>Nodes and their labels never overlap, whatever the other forces say.</summary>
    void Separate()
    {
        for (var pass = 0; pass < 2; pass++)
            for (var i = 0; i < _bodies.Count; i++)
                for (var j = i + 1; j < _bodies.Count; j++)
                {
                    var a = _bodies[i];
                    var b = _bodies[j];
                    if (a.Pinned && b.Pinned) continue;
                    var dx = b.X - a.X;
                    var dy = b.Y - a.Y;
                    var min = a.Radius + b.Radius + CollisionGap;
                    var d = Math.Sqrt(dx * dx + dy * dy);
                    if (d >= min) continue;
                    if (d < 0.01) (dx, dy, d) = (1, 0, 1);
                    var push = (min - d) / d;
                    var share = a.Pinned ? 0 : b.Pinned ? 1 : 0.5;
                    a.X -= dx * push * share;
                    a.Y -= dy * push * share;
                    b.X += dx * push * (1 - share);
                    b.Y += dy * push * (1 - share);
                }
    }

    /// <summary>Two groups' boxes that overlap move apart along whichever way is the shorter way out, a little each step,
    /// so a box never sits over another's nodes. A loose node counts as a box of its own.</summary>
    void SeparateBoxes()
    {
        var clusters = _bodies.Where(b => b.Group != _loose).GroupBy(b => b.Group).Select(g => g.ToArray())
            .Concat(_bodies.Where(b => b.Group == _loose).Select(b => new[] { b }))
            .ToArray();
        if (clusters.Length < 2) return;
        var boxes = clusters.Select(c => c.Length == 1 && c[0].Group == _loose
            ? new Box(c[0].X - c[0].Radius, c[0].Y - c[0].Radius, c[0].X + c[0].Radius, c[0].Y + c[0].Radius + 30)
            : BoxOf(c)).ToArray();

        for (var i = 0; i < clusters.Length; i++)
            for (var j = i + 1; j < clusters.Length; j++)
            {
                var a = boxes[i];
                var b = boxes[j];
                if (!a.Overlaps(b, BoxGap)) continue;
                var overX = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left) + BoxGap;
                var overY = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top) + BoxGap;
                var (ax, ay) = ((a.Left + a.Right) / 2, (a.Top + a.Bottom) / 2);
                var (bx, by) = ((b.Left + b.Right) / 2, (b.Top + b.Bottom) / 2);
                // A third of the way each step: enough to part them quickly, little enough not to jump.
                var (mx, my) = overX < overY ? (Math.Sign(bx - ax + 0.01) * overX / 3, 0.0) : (0.0, Math.Sign(by - ay + 0.01) * overY / 3);
                var pinnedA = clusters[i].Any(n => n.Pinned);
                var pinnedB = clusters[j].Any(n => n.Pinned);
                var share = pinnedA && !pinnedB ? 0 : pinnedB && !pinnedA ? 1 : 0.5;
                Move(clusters[i], -mx * share, -my * share);
                Move(clusters[j], mx * (1 - share), my * (1 - share));
            }
    }

    static void Move(Body[] cluster, double dx, double dy)
    {
        foreach (var b in cluster.Where(b => !b.Pinned))
        {
            b.X += dx;
            b.Y += dy;
        }
    }
}
