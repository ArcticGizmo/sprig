using Sprig.Core.Config;
using Sprig.Core.Workspaces;

namespace Sprig.Core.Maps;

/// <summary>How a module's need is wired in the projected graph.</summary>
public enum WireStatus
{
    /// <summary>Exactly one provider (a repo-local sibling wins over any other), or a map default fills it.</summary>
    Resolved,
    /// <summary>More than one provider and no wiring entry to pick one — the map must choose (writes map.wiring).</summary>
    Ambiguous,
    /// <summary>No provider in the selection and no default — supply one (writes map.defaults).</summary>
    Gap,
}

/// <summary>One provided capability on a module node — a source a need can wire to.</summary>
public sealed record MapGraphProvide(string Capability, IReadOnlyList<string> Outputs);

/// <summary>One need on a module node: how it resolved, the chosen provider (when Resolved to a real
/// provider), and the candidate provider repos (when Ambiguous).</summary>
public sealed record MapGraphNeed(
    string Capability,
    string Alias,
    WireStatus Status,
    string? ProviderRepo,
    string? ProviderCapability,
    bool FromDefault,
    IReadOnlyList<string> Candidates);

/// <summary>A module within a repo node: its provides and needs (with their wiring status).</summary>
public sealed record MapGraphModule(
    string Repo, string Module, string Path,
    IReadOnlyList<MapGraphProvide> Provides,
    IReadOnlyList<MapGraphNeed> Needs);

/// <summary>A repo node, expandable to its modules.</summary>
public sealed record MapGraphNode(string Repo, IReadOnlyList<MapGraphModule> Modules);

/// <summary>A directed wiring edge from a needing module to the providing repo/capability.</summary>
public sealed record MapGraphEdge(
    string FromRepo, string FromModule, string Need, string ToRepo, string ToCapability);

/// <summary>
/// The structural projection of a map + selection: repo/module nodes and the derived provides→needs wiring
/// edges, with each need classified <see cref="WireStatus.Resolved"/> / <see cref="WireStatus.Ambiguous"/> /
/// <see cref="WireStatus.Gap"/>. Pure and value-free (no ports, no substitution) — the answer to "how does
/// this slice fit together, and where does the user still have to decide?", computed the same way
/// <see cref="CapabilityResolver"/> wires values, so the canvas and the checkout agree.
/// </summary>
public sealed record MapGraph(IReadOnlyList<MapGraphNode> Nodes, IReadOnlyList<MapGraphEdge> Edges)
{
    IEnumerable<(MapGraphModule Module, MapGraphNeed Need)> AllNeeds =>
        Nodes.SelectMany(n => n.Modules).SelectMany(m => m.Needs.Select(need => (m, need)));

    /// <summary>Needs with more than one provider and no wiring entry — each a choice the map must make.</summary>
    public IReadOnlyList<(MapGraphModule Module, MapGraphNeed Need)> Ambiguities =>
        AllNeeds.Where(x => x.Need.Status == WireStatus.Ambiguous).ToList();

    /// <summary>Needs with no provider and no default — each a value the map must supply.</summary>
    public IReadOnlyList<(MapGraphModule Module, MapGraphNeed Need)> Gaps =>
        AllNeeds.Where(x => x.Need.Status == WireStatus.Gap).ToList();

    /// <summary>True when every need is satisfied (resolved or defaulted) — the slice checks out cleanly.</summary>
    public bool IsComplete => AllNeeds.All(x => x.Need.Status == WireStatus.Resolved);
}

/// <summary>Projects a map + selected repos into a <see cref="MapGraph"/>. The wiring rules mirror
/// <see cref="CapabilityResolver"/> exactly (nearest-wins sibling, then a single map-wide provider, then a
/// map default, else a gap) so the canvas shows what a checkout would actually do.</summary>
public static class MapGraphProjection
{
    public static MapGraph Project(MapDefinition? map, IReadOnlyList<ResolvedRepo> repos)
    {
        var providers = CollectProviders(repos);
        var byCapability = providers.ToLookup(p => p.Capability, StringComparer.Ordinal);

        var nodes = new List<MapGraphNode>();
        var edges = new List<MapGraphEdge>();

        foreach (var repo in repos)
        {
            var modules = new List<MapGraphModule>();
            foreach (var module in repo.Config.EffectiveModules)
            {
                var provides = module.Provides
                    .Select(p => new MapGraphProvide(p.Capability, p.Outputs.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList()))
                    .ToList();

                var needs = new List<MapGraphNeed>();
                foreach (var need in module.Needs)
                {
                    var wired = Classify(repo.Name, need, map, byCapability);
                    needs.Add(wired);
                    if (wired.Status == WireStatus.Resolved && wired.ProviderRepo is { } toRepo && wired.ProviderCapability is { } toCap && !wired.FromDefault)
                        edges.Add(new MapGraphEdge(repo.Name, module.Name, need.Capability, toRepo, toCap));
                }

                modules.Add(new MapGraphModule(repo.Name, module.Name, module.Path, provides, needs));
            }
            nodes.Add(new MapGraphNode(repo.Name, modules));
        }

        return new MapGraph(nodes, edges);
    }

    static MapGraphNeed Classify(string repo, Need need, MapDefinition? map, ILookup<string, Provider> byCapability)
    {
        // A map may bridge a generic need to a specific provider capability (need name != provider name).
        var target = Lookup2(map?.Wiring, repo, need.Capability) ?? need.Capability;
        var candidates = byCapability[target].ToList();

        // Nearest-wins: a provider in the same repo beats any other.
        var sibling = candidates.FirstOrDefault(p => p.Repo == repo);
        if (sibling is not null)
            return Resolved(need, sibling, fromDefault: false);

        if (candidates.Count == 1)
            return Resolved(need, candidates[0], fromDefault: false);

        if (candidates.Count > 1)
            return new MapGraphNeed(need.Capability, need.Alias, WireStatus.Ambiguous, null, target, false,
                candidates.Select(c => c.Repo).OrderBy(r => r, StringComparer.Ordinal).ToList());

        // No provider — a map default fills the gap, else it's an open gap.
        if (Lookup3(map?.Defaults, repo, need.Capability) is not null)
            return new MapGraphNeed(need.Capability, need.Alias, WireStatus.Resolved, null, null, true, []);

        return new MapGraphNeed(need.Capability, need.Alias, WireStatus.Gap, null, null, false, []);
    }

    static MapGraphNeed Resolved(Need need, Provider p, bool fromDefault) =>
        new(need.Capability, need.Alias, WireStatus.Resolved, p.Repo, p.Capability, fromDefault, []);

    static IReadOnlyList<Provider> CollectProviders(IReadOnlyList<ResolvedRepo> repos)
    {
        var providers = new List<Provider>();
        foreach (var repo in repos)
            foreach (var module in repo.Config.EffectiveModules)
                foreach (var cap in module.Provides)
                    providers.Add(new Provider(repo.Name, cap.Capability));
        return providers;
    }

    static string? Lookup2(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? outer, string a, string b)
        => outer is not null && outer.TryGetValue(a, out var inner) && inner.TryGetValue(b, out var v) ? v : null;

    static IReadOnlyDictionary<string, string>? Lookup3(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>? outer, string a, string b)
        => outer is not null && outer.TryGetValue(a, out var inner) && inner.TryGetValue(b, out var v) ? v : null;

    sealed record Provider(string Repo, string Capability);
}
