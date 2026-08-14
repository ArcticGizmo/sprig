namespace Sprig.Core.Stacks;

/// <summary>
/// A repo box on the repo-graph canvas: the repo's declared input pins (drawn as small ports on the
/// node), the stack ports it owns (produces), and the shared/unowned port chips that attach to it.
/// </summary>
public sealed record RepoGraphNode(
    string Repo,
    IReadOnlyList<string> Inputs,
    IReadOnlyList<string> Owns,
    IReadOnlyList<RepoGraphChip> Chips);

/// <summary>
/// A port chip attached to a consuming repo: the port name and how many repos consume it. Drawn in
/// place of a fan-out cable when a port is shared across many repos, or has no owner to point a
/// directed line from — so a widely-used value adds one labelled chip per consumer instead of a mat
/// of crossing lines.
/// </summary>
public sealed record RepoGraphChip(string Port, int UsedBy);

/// <summary>
/// A directed dependency line: the owning repo produces <see cref="Port"/> and the consuming repo
/// reads it. Only emitted when a port has an assigned owner and exactly one <i>other</i> repo consuming
/// it — the clean "value maps between these two repos" case the graph draws as an arrow.
/// </summary>
public sealed record RepoGraphEdge(string Owner, string Consumer, string Port);

/// <summary>
/// A stack's wiring as a repo-centric graph: repos are the nodes, and each stack port becomes either a
/// directed <c>owner → consumer</c> line (a clean dependency between exactly two repos) or a labelled
/// chip with a usage count (a value shared across many repos, or one with no owner to point a line
/// from). This is the read-optimised counterpart to <see cref="WiringGraph"/>'s port-centric patchbay:
/// the same underlying data (repos, ports, bindings) plus the ownership overlay
/// (<see cref="StackDefinition.Owners"/>), arranged to make natural dependencies obvious and to keep
/// fan-out from turning into crossing cables. Pure and derived — positions are the view's job; this is
/// only what connects to what.
/// </summary>
public sealed record RepoGraph(
    IReadOnlyList<RepoGraphNode> Nodes,
    IReadOnlyList<RepoGraphEdge> Edges,
    IReadOnlyList<string> UnownedPorts)
{
    /// <param name="owners">Port → owning repo (the visualization overlay). Ports absent here have no
    /// owner and can only ever be drawn as chips; an owner naming a repo outside the stack is ignored.</param>
    public static RepoGraph Build(
        IReadOnlyList<string> repos,
        IReadOnlyList<string> ports,
        IReadOnlyDictionary<string, IReadOnlyList<string>> repoInputs,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> bindings,
        IReadOnlyDictionary<string, string> owners)
    {
        var declared = new HashSet<string>(ports, StringComparer.Ordinal);
        var repoSet = new HashSet<string>(repos, StringComparer.Ordinal);

        // Which repos consume each port, in stack order, each repo counted once however many of its
        // inputs reference the port. This distinct-per-repo count is the cross-repo fan-out the graph
        // cares about (and the "×N" a chip shows).
        var consumers = ports.ToDictionary(p => p, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var repo in repos)
        {
            if (!bindings.TryGetValue(repo, out var repoBindings)) continue;
            var referenced = new HashSet<string>(StringComparer.Ordinal);
            foreach (var expr in repoBindings.Values)
                foreach (var port in PortExpressions.ReferencedPorts(expr))
                    if (declared.Contains(port)) referenced.Add(port);
            foreach (var port in ports)
                if (referenced.Contains(port)) consumers[port].Add(repo);
        }

        var edges = new List<RepoGraphEdge>();
        var chipsByRepo = repos.ToDictionary(r => r, _ => new List<RepoGraphChip>(), StringComparer.Ordinal);
        var ownsByRepo = repos.ToDictionary(r => r, _ => new List<string>(), StringComparer.Ordinal);
        var unowned = new List<string>();

        foreach (var port in ports)
        {
            var owner = owners.TryGetValue(port, out var o) && repoSet.Contains(o) ? o : null;
            var cs = consumers[port];

            // Record ownership for the producer's badge regardless of who consumes it.
            if (owner is not null) ownsByRepo[owner].Add(port);

            if (cs.Count == 0) continue;                 // declared but nothing reads it — no line, no chip
            if (owner is null) unowned.Add(port);

            // The repos that read this port other than its producer — the ones a line or chip serves.
            var external = owner is null ? cs : cs.Where(c => c != owner).ToList();
            if (external.Count == 0) continue;           // only the owner reads its own port — internal

            // A single external consumer with a known producer is the clean dependency: draw the arrow.
            if (owner is not null && external.Count == 1)
            {
                edges.Add(new RepoGraphEdge(owner, external[0], port));
                continue;
            }

            // Otherwise it fans out (or has no producer): a chip on each consuming repo, labelled with
            // how many of them there are, so nothing crosses.
            foreach (var c in external)
                chipsByRepo[c].Add(new RepoGraphChip(port, external.Count));
        }

        var nodes = repos.Select(r => new RepoGraphNode(
            r,
            repoInputs.TryGetValue(r, out var ins) ? ins : [],
            ownsByRepo[r],
            chipsByRepo[r])).ToList();

        return new RepoGraph(nodes, edges, unowned);
    }
}
