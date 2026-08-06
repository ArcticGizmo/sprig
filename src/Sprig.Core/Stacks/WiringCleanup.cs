namespace Sprig.Core.Stacks;

/// <summary>
/// Crossing-reduction for the wiring canvas. Sources on the left rail and repos on the right are both
/// free to reorder (pins within a repo stay put — they're the repo's declared inputs). Ordering each
/// side by the barycentre of what it connects to on the other side pulls every cable towards
/// horizontal, which is what removes the crossings. Alternating a few sweeps lets the two sides settle
/// against each other. Pure and deterministic; the builder applies the returned orders to its rows.
/// </summary>
public static class WiringCleanup
{
    /// <summary>
    /// Reorder both the source rail and the repos to minimise cable crossings. Alternating barycentre
    /// sweeps — order ports against the current repo/pin layout, then repos against the new rail, and
    /// repeat — converge quickly; the sweep stops early once neither side moves. Stable under ties, so
    /// an already-tidy board comes back unchanged (the op is idempotent).
    /// </summary>
    public static (IReadOnlyList<string> Ports, IReadOnlyList<string> Repos) Tidy(
        IReadOnlyList<string> ports, IReadOnlyList<string> repos, WiringGraph graph)
    {
        var portOrder = ports.ToList();
        var repoOrder = repos.ToList();

        for (var sweep = 0; sweep < 4; sweep++)
        {
            var newPorts = OrderPortsBy(portOrder, repoOrder, graph);
            var newRepos = OrderReposBy(repoOrder, newPorts, graph);
            var stable = newPorts.SequenceEqual(portOrder) && newRepos.SequenceEqual(repoOrder);
            portOrder = newPorts;
            repoOrder = newRepos;
            if (stable) break;
        }

        return (portOrder, repoOrder);
    }

    /// <summary>
    /// Reorder just the source rail against the repos' fixed pin layout — a single barycentre pass. Kept
    /// as its own entry point for the rail-only case and for direct testing.
    /// </summary>
    public static IReadOnlyList<string> OrderPorts(IReadOnlyList<string> ports, WiringGraph graph) =>
        OrderPortsBy(ports, graph.Repos.Select(r => r.Repo).ToList(), graph);

    /// <summary>Order ports by the mean vertical slot of the pins they feed, given a repo order.</summary>
    static List<string> OrderPortsBy(IReadOnlyList<string> ports, IReadOnlyList<string> repoOrder, WiringGraph graph)
    {
        var pinSlot = PinSlots(repoOrder, graph);

        double Barycentre(string port)
        {
            double sum = 0;
            var n = 0;
            foreach (var e in graph.Edges)
                if (e.Port == port && pinSlot.TryGetValue((e.Repo, e.Input), out var s)) { sum += s; n++; }
            return n > 0 ? sum / n : double.MaxValue; // unconsumed ports sink to the bottom
        }

        return StableOrderBy(ports, Barycentre);
    }

    /// <summary>Order repos by the mean rail slot of the sources their pins reference, given a port order.</summary>
    static List<string> OrderReposBy(IReadOnlyList<string> repos, IReadOnlyList<string> portOrder, WiringGraph graph)
    {
        // Rail slots: the workspace source sits at the top (slot 0), the ports follow at 1..n.
        var railSlot = new Dictionary<string, double>(StringComparer.Ordinal);
        for (var i = 0; i < portOrder.Count; i++) railSlot[portOrder[i]] = i + 1;

        var sum = new Dictionary<string, double>(StringComparer.Ordinal);
        var count = new Dictionary<string, int>(StringComparer.Ordinal);
        void Add(string repo, double slot)
        {
            sum[repo] = (sum.TryGetValue(repo, out var s) ? s : 0) + slot;
            count[repo] = (count.TryGetValue(repo, out var c) ? c : 0) + 1;
        }

        foreach (var e in graph.Edges)
            if (railSlot.TryGetValue(e.Port, out var slot)) Add(e.Repo, slot);
        foreach (var node in graph.Repos)
            foreach (var pin in node.Pins)
                if (pin.UsesWorkspace) Add(node.Repo, 0); // workspace source at the top of the rail

        double Barycentre(string repo) =>
            count.TryGetValue(repo, out var c) && c > 0 ? sum[repo] / c : double.MaxValue;

        return StableOrderBy(repos, Barycentre);
    }

    /// <summary>Global top-to-bottom slot for each (repo, input) pin, given a repo order.</summary>
    static Dictionary<(string Repo, string Input), int> PinSlots(IReadOnlyList<string> repoOrder, WiringGraph graph)
    {
        var map = new Dictionary<(string, string), int>();
        var slot = 0;
        foreach (var repo in repoOrder)
        {
            var node = graph.Repos.FirstOrDefault(r => r.Repo == repo);
            if (node is null) continue;
            foreach (var pin in node.Pins) map[(repo, pin.Input)] = slot++;
        }
        return map;
    }

    /// <summary>Sort by barycentre, keeping the original order among ties and unconnected items.</summary>
    static List<string> StableOrderBy(IReadOnlyList<string> items, Func<string, double> barycentre) =>
        items
            .Select((item, index) => (item, bary: barycentre(item), index))
            .OrderBy(t => t.bary)
            .ThenBy(t => t.index)
            .Select(t => t.item)
            .ToList();
}
