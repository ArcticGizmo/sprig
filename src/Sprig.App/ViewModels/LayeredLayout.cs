using System;
using System.Collections.Generic;
using System.Linq;

namespace Sprig.App.ViewModels;

/// <summary>
/// A layered layout: what uses something sits above it, so links run down the page. Front ends come out on top, then
/// the services they call, with infra and outside values along the bottom row. Each group (a repo) keeps a column of
/// its own, rows lining up across them, and nodes are ordered within their row to cross as few links as can be found.
/// <para>
/// Services that use each other make loops; one link in each runs back up the page (<see cref="RunsBack"/>), chosen so as
/// few links as possible do. A link that passes rows on its way bends round the nodes in them.
/// </para>
/// </summary>
public sealed class LayeredLayout : IGraphLayout
{
    // A node's usage above it and its labels below, plus a box's title when rows fall in different boxes (LayoutBox).
    const double RowGap = 180;
    const double NodeGap = 24;
    const double GroupGap = 44;
    const int Sweeps = 12;

    readonly List<LayoutBody> _bodies;
    readonly Dictionary<string, LayoutBody> _byKey;
    readonly Dictionary<string, int> _row = new(StringComparer.Ordinal);
    readonly HashSet<(string, string)> _back = [];
    readonly Dictionary<(string, string), IReadOnlyList<(double X, double Y)>> _routes = [];
    readonly string? _loose;
    double _midRow;

    public IReadOnlyList<LayoutBody> Bodies => _bodies;

    double RowY(int row) => (row - _midRow) * RowGap;
    public bool Rows => true;
    public bool CanDrag => false;
    public bool Settled => true;
    public void Step() { }
    public void Reheat(double alpha = 0.3) { }

    /// <param name="nodes"><c>Bottom</c>: infra or an outside value, which goes in the bottom row whatever uses it.</param>
    /// <param name="links">From what uses to what's used.</param>
    /// <param name="loose">A group that gets no box of its own (outside values): it takes the last column.</param>
    public LayeredLayout(IEnumerable<(string Key, string Group, double Radius, bool Bottom)> nodes, IEnumerable<(string From, string To)> links,
        string? loose = null)
    {
        _loose = loose;
        var list = nodes.OrderBy(n => n.Group, StringComparer.Ordinal).ThenBy(n => n.Key, StringComparer.Ordinal).ToArray();
        _bodies = list.Select(n => new LayoutBody(n.Key, n.Group, n.Radius)).ToList();
        _byKey = _bodies.ToDictionary(b => b.Key, StringComparer.Ordinal);
        var bottom = list.Where(n => n.Bottom).Select(n => n.Key).ToHashSet(StringComparer.Ordinal);
        var edges = links.Where(l => l.From != l.To && _byKey.ContainsKey(l.From) && _byKey.ContainsKey(l.To)).Distinct().ToArray();

        AssignRows(bottom, edges);
        var groups = OrderGroups(edges);
        var order = OrderRows(groups, edges);
        Place(groups, order);
        RouteLinks(edges);
    }

    public LayoutBody? Find(string key) => _byKey.GetValueOrDefault(key);

    /// <summary>Each group's column, as wide as its widest row (or its title), from its top row to its bottom one.</summary>
    public IEnumerable<(string Group, LayoutBox Box)> Boxes()
        => _bodies.Where(b => b.Group != _loose).GroupBy(b => b.Group).Select(g => (g.Key, new LayoutBox(
            _columns[g.Key].Left - LayoutBox.Side, g.Min(b => b.Y) - LayoutBox.Above, _columns[g.Key].Right + LayoutBox.Side, g.Max(b => b.Y) + LayoutBox.Below)));

    readonly Dictionary<string, (double Left, double Right)> _columns = new(StringComparer.Ordinal);

    public IReadOnlyList<(double X, double Y)> Route(string from, string to) => _routes.GetValueOrDefault((from, to)) ?? [];

    public bool RunsBack(string from, string to) => _back.Contains((from, to));

    /// <summary>The row each node goes in: everything above what it uses, as few rows as that takes, and infra and
    /// outside values at the bottom.</summary>
    void AssignRows(HashSet<string> bottom, (string From, string To)[] edges)
    {
        var upper = _bodies.Where(b => !bottom.Contains(b.Key)).Select(b => b.Key).ToList();
        var inner = edges.Where(e => !bottom.Contains(e.From) && !bottom.Contains(e.To)).ToArray();
        var rank = FeedbackOrder(upper, inner);
        var down = inner.Where(e => rank[e.From] < rank[e.To]).GroupBy(e => e.From).ToDictionary(g => g.Key, g => g.Select(e => e.To).ToArray());
        var height = new Dictionary<string, int>(StringComparer.Ordinal);
        int Height(string key)
        {
            if (height.TryGetValue(key, out var known)) return known;
            height[key] = 0;
            return height[key] = down.TryGetValue(key, out var used) ? used.Max(Height) + 1 : 0;
        }
        var top = upper.Count == 0 ? -1 : upper.Max(Height);
        foreach (var key in upper) _row[key] = top - Height(key);
        foreach (var key in bottom) _row[key] = top + 1;

        // A node linked to nothing has no place in the flow; it sits with the top of its group, rather than dropping to
        // the bottom and stretching the group's box down the page.
        var linkedKeys = edges.SelectMany(e => new[] { e.From, e.To }).ToHashSet(StringComparer.Ordinal);
        foreach (var key in upper.Where(k => !linkedKeys.Contains(k)))
        {
            var group = _byKey[key].Group;
            var rows = upper.Where(k => k != key && _byKey[k].Group == group && linkedKeys.Contains(k)).Select(k => _row[k]).ToArray();
            _row[key] = rows.Length > 0 ? rows.Min() : 0;
        }

        // Judged by where they ended up: a link turned round to break a loop can still point down the page, and one from
        // infra up to an app (a container using an app's url) runs against the flow too.
        foreach (var e in edges.Where(e => _row[e.From] > _row[e.To])) _back.Add(e);
    }

    /// <summary>
    /// An order of the nodes that as many links as possible go forward in, so as few as possible are turned round to break
    /// loops (Eades, Lin and Smyth's greedy heuristic): what nothing uses goes first, what uses nothing goes last, and of
    /// the rest, the one that uses most more than it's used.
    /// </summary>
    static Dictionary<string, int> FeedbackOrder(List<string> keys, (string From, string To)[] edges)
    {
        var left = new SortedSet<string>(keys, StringComparer.Ordinal);
        var outs = keys.ToDictionary(k => k, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        var ins = keys.ToDictionary(k => k, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var (from, to) in edges)
        {
            outs[from].Add(to);
            ins[to].Add(from);
        }
        var head = new List<string>();
        var tail = new List<string>();
        void Remove(string key)
        {
            left.Remove(key);
            foreach (var o in outs[key]) ins[o].Remove(key);
            foreach (var i in ins[key]) outs[i].Remove(key);
        }
        while (left.Count > 0)
        {
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var sink in left.Where(k => outs[k].Count == 0).ToArray())
                {
                    tail.Insert(0, sink);
                    Remove(sink);
                    changed = true;
                }
                foreach (var source in left.Where(k => ins[k].Count == 0).ToArray())
                {
                    head.Add(source);
                    Remove(source);
                    changed = true;
                }
            }
            if (left.Count == 0) break;
            var pick = left.OrderByDescending(k => outs[k].Count - ins[k].Count).ThenBy(k => k, StringComparer.Ordinal).First();
            head.Add(pick);
            Remove(pick);
        }
        return head.Concat(tail).Select((k, i) => (k, i)).ToDictionary(x => x.k, x => x.i, StringComparer.Ordinal);
    }

    /// <summary>
    /// The groups left to right, each beside the ones it has most links with: starting from the most linked group, the
    /// group with most links to those placed goes at whichever end it has more links to. Groups with no links to others
    /// come after, and outside values last.
    /// </summary>
    List<string> OrderGroups((string From, string To)[] edges)
    {
        var groups = _bodies.Select(b => b.Group).Distinct().Where(g => g != _loose).ToList();
        var weight = new Dictionary<(string, string), int>();
        foreach (var e in edges)
        {
            var (a, b) = (_byKey[e.From].Group, _byKey[e.To].Group);
            if (a == b || a == _loose || b == _loose) continue;
            var pair = string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a);
            weight[pair] = weight.GetValueOrDefault(pair) + 1;
        }
        int W(string a, string b) => weight.GetValueOrDefault(string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a));

        var linked = groups.Where(g => groups.Any(o => o != g && W(g, o) > 0)).ToList();
        var order = new List<string>();
        if (linked.Count > 0)
        {
            order.Add(linked.OrderByDescending(g => linked.Sum(o => W(g, o))).ThenBy(g => g, StringComparer.Ordinal).First());
            while (order.Count < linked.Count)
            {
                var next = linked.Where(g => !order.Contains(g))
                    .OrderByDescending(g => order.Sum(o => W(g, o))).ThenBy(g => g, StringComparer.Ordinal).First();
                // Wherever its links, and those it comes between, end up shortest.
                var best = Enumerable.Range(0, order.Count + 1).MinBy(at =>
                {
                    var trial = order.ToList();
                    trial.Insert(at, next);
                    return trial.SelectMany((a, i) => trial.Skip(i + 1).Select((b, j) => W(a, b) * (j + 1))).Sum();
                });
                order.Insert(best, next);
            }
        }
        var result = order.Concat(groups.Where(g => !linked.Contains(g))).ToList();
        if (_loose is not null && _bodies.Any(b => b.Group == _loose)) result.Add(_loose);
        return result;
    }

    /// <summary>
    /// Each row's nodes, group by group, in the order that crosses the fewest links found: rows are swept down and up,
    /// each node going to the average place of what it's linked to in the rows already ordered (the barycentre method),
    /// and the best order seen is kept.
    /// </summary>
    Dictionary<(int Row, string Group), List<string>> OrderRows(List<string> groups, (string From, string To)[] edges)
    {
        var groupAt = groups.Select((g, i) => (g, i)).ToDictionary(x => x.g, x => x.i);
        var order = _bodies.GroupBy(b => (Row: _row[b.Key], b.Group)).ToDictionary(g => g.Key, g => g.Select(b => b.Key).ToList());
        var neighbours = _bodies.ToDictionary(b => b.Key, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var (from, to) in edges)
        {
            neighbours[from].Add(to);
            neighbours[to].Add(from);
        }
        var rows = _row.Values.Distinct().Order().ToArray();

        double Spot(string key, Dictionary<(int, string), List<string>> o)
        {
            var b = _byKey[key];
            var cell = o[(_row[key], b.Group)];
            return groupAt[b.Group] + (cell.IndexOf(key) + 0.5) / cell.Count;
        }

        int Crossings(Dictionary<(int, string), List<string>> o)
        {
            var spans = edges.Where(e => _row[e.From] != _row[e.To])
                .Select(e => _row[e.From] < _row[e.To] ? (Top: e.From, Low: e.To) : (Top: e.To, Low: e.From))
                .Select(s => (s.Top, s.Low, TopAt: Spot(s.Top, o), LowAt: Spot(s.Low, o), TopRow: _row[s.Top], LowRow: _row[s.Low]))
                .ToArray();
            var count = 0;
            for (var i = 0; i < spans.Length; i++)
                for (var j = i + 1; j < spans.Length; j++)
                {
                    var (a, c) = (spans[i], spans[j]);
                    // Only links that share rows can cross; for those, compare where each is on the rows they share.
                    var from = Math.Max(a.TopRow, c.TopRow);
                    var to = Math.Min(a.LowRow, c.LowRow);
                    if (from >= to) continue;
                    double At((string Top, string Low, double TopAt, double LowAt, int TopRow, int LowRow) s, int row)
                        => s.TopAt + (s.LowAt - s.TopAt) * (row - s.TopRow) / (s.LowRow - s.TopRow);
                    if ((At(a, from) - At(c, from)) * (At(a, to) - At(c, to)) < 0) count++;
                }
            return count;
        }

        var best = Copy(order);
        var fewest = Crossings(order);
        for (var sweep = 0; sweep < Sweeps && fewest > 0; sweep++)
        {
            var downward = sweep % 2 == 0;
            foreach (var row in downward ? rows : rows.Reverse())
                foreach (var group in groups)
                {
                    if (!order.TryGetValue((row, group), out var cell) || cell.Count < 2) continue;
                    var weights = cell.ToDictionary(k => k, k =>
                    {
                        var near = neighbours[k].Where(n => downward ? _row[n] < row : _row[n] > row).ToArray();
                        return near.Length == 0 ? Spot(k, order) : near.Average(n => Spot(n, order));
                    });
                    var sorted = cell.OrderBy(k => weights[k]).ThenBy(cell.IndexOf).ToList();
                    order[(row, group)] = sorted;
                }
            var crossings = Crossings(order);
            if (crossings < fewest)
            {
                fewest = crossings;
                best = Copy(order);
            }
        }
        return best;

        static Dictionary<(int Row, string Group), List<string>> Copy(Dictionary<(int Row, string Group), List<string>> o)
            => o.ToDictionary(p => p.Key, p => p.Value.ToList());
    }

    /// <summary>Rows top to bottom, groups as columns as wide as their widest row, and each row of a group centred in
    /// its column; the whole picture centred on the origin.</summary>
    void Place(List<string> groups, Dictionary<(int Row, string Group), List<string>> order)
    {
        double Width(IEnumerable<string> keys) => keys.Sum(k => _byKey[k].Radius * 2) + Math.Max(0, keys.Count() - 1) * NodeGap;
        var x = 0.0;
        var centres = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            // Wide enough for its box's title too, with room for the title's least size when zoomed out to fit.
            var width = Math.Max(order.Where(p => p.Key.Group == group).Select(p => Width(p.Value)).DefaultIfEmpty(0).Max(),
                group == _loose ? 0 : group.Length * 7.4 + 22);
            centres[group] = x + width / 2;
            _columns[group] = (x, x + width);
            x += width + GroupGap;
        }
        var middle = (x - GroupGap) / 2;
        foreach (var group in groups) _columns[group] = (_columns[group].Left - middle, _columns[group].Right - middle);
        var rows = _row.Values.DefaultIfEmpty(0);
        _midRow = (rows.Min() + rows.Max()) / 2.0;
        foreach (var ((row, group), keys) in order)
        {
            var left = centres[group] - Width(keys) / 2;
            foreach (var key in keys)
            {
                var b = _byKey[key];
                b.X = left + b.Radius - middle;
                b.Y = RowY(row);
                left += b.Radius * 2 + NodeGap;
            }
        }
    }

    /// <summary>A link that passes rows bends, at each, into the nearest gap between that row's nodes, so it never runs
    /// through one. Two nodes in the same row are joined by an arc below them.</summary>
    void RouteLinks((string From, string To)[] edges)
    {
        var byRow = _bodies.GroupBy(b => _row[b.Key]).ToDictionary(g => g.Key, g => g.OrderBy(b => b.X).ToArray());
        foreach (var (from, to) in edges)
        {
            var (a, b) = (_byKey[from], _byKey[to]);
            var (ra, rb) = (_row[from], _row[to]);
            if (ra == rb)
            {
                _routes[(from, to)] = [((a.X + b.X) / 2, a.Y + LayoutBox.Below + 14)];
                continue;
            }
            var points = new List<(double, double)>();
            var step = Math.Sign(rb - ra);
            for (var row = ra + step; row != rb; row += step)
            {
                var t = (double)(row - ra) / (rb - ra);
                var x = a.X + (b.X - a.X) * t;
                foreach (var other in byRow.GetValueOrDefault(row) ?? [])
                {
                    var room = other.Radius + NodeGap / 2;
                    if (Math.Abs(x - other.X) >= room) continue;
                    x = x < other.X ? other.X - room : other.X + room;
                }
                points.Add((x, RowY(row)));
            }
            _routes[(from, to)] = points;
        }
    }
}
