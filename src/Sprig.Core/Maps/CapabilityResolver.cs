using System.Globalization;
using Sprig.Core.Config;
using Sprig.Core.Ports;
using Sprig.Core.Substitution;
using Sprig.Core.Workspaces;

namespace Sprig.Core.Maps;

/// <summary>Thrown when a map + selection can't be resolved (a genuinely ambiguous need, a missing port
/// allocation). A merely-unsatisfied need is <b>not</b> an exception — it is reported in
/// <see cref="ResolvedWorkspace.Unsatisfied"/> so a partial checkout produces a gap list, not a crash.</summary>
public sealed class MapResolutionException(string message) : Exception(message);

/// <summary>A module resolved to a concrete materialisation unit: its <see cref="Values"/> (the
/// capability-qualified values its env/compose templates resolve against, workspace-independent) and a
/// ready <see cref="Scope"/> built from them, plus everything needed to lay it down. The values are stored
/// on the workspace record so claim/refresh can rebuild the scope without re-resolving the map.</summary>
public sealed record ResolvedModule(
    string Repo, string RepoRoot, string Module, string Path, ModuleDeclaration Declaration,
    IReadOnlyDictionary<string, string> Values, IVariableSource Scope);

/// <summary>A need with no provider in the selection and no fallback — the gap surfaced to the user.</summary>
public sealed record UnsatisfiedNeed(string Repo, string Module, string Value);

/// <summary>The resolved wiring for a workspace: the modules to materialise, the allocated ports, and any gaps.</summary>
public sealed record ResolvedWorkspace(
    IReadOnlyList<ResolvedModule> Modules,
    IReadOnlyDictionary<string, int> Ports,
    IReadOnlyList<UnsatisfiedNeed> Unsatisfied);

/// <summary>
/// The map-model resolver (the Graph Turn). Turns a map + a selected repo set into per-module value
/// scopes, keyed by <c>${sprig.&lt;capability&gt;.&lt;output&gt;}</c>. The same algorithm serves both levels —
/// within a repo (a module's need finds a <b>sibling</b> module first: nearest-wins local wiring) and
/// across the map (else any selected provider). Unlike <c>StackWiring</c>, an unmet need is reported, not
/// thrown, so a partial selection yields a precise gap list. Port <b>allocation</b> is left to the caller
/// (IO): <see cref="PortRequests"/> enumerates the requests, the caller acquires them, and the allocated
/// numbers are fed back into <see cref="Resolve"/> — keeping the resolver pure and unit-testable.
/// </summary>
public static class CapabilityResolver
{
    /// <summary>The port-lease name for a provided port output — globally unique
    /// (<c>repo</c> unique in a map, <c>capability</c> unique in a repo, <c>output</c> unique in a capability).</summary>
    public static string PortName(string repo, string capability, string output) => $"{repo}.{capability}.{output}";

    /// <summary>Every port the selection needs allocated: one per declared <c>port</c> across every module of
    /// every selected repo (a provider needs its port to <i>run</i>, regardless of who consumes it).</summary>
    public static IReadOnlyList<PortRequest> PortRequests(IReadOnlyList<ResolvedRepo> repos)
    {
        var requests = new List<PortRequest>();
        foreach (var repo in repos)
            foreach (var module in repo.Config.EffectiveModules)
                foreach (var cap in module.Provides)
                    foreach (var (output, spec) in cap.Ports)
                        requests.Add(new PortRequest(
                            PortName(repo.Name, cap.Capability, output),
                            string.IsNullOrWhiteSpace(spec.Allowed) ? null : PortSetSpec.Parse(spec.Allowed!)));
        return requests;
    }

    /// <summary>Resolve the selection against the map, given the ports the caller has already allocated.</summary>
    /// <param name="inlineLiterals">Optional per-checkout fallbacks (<c>[repo][capability][output] = literal</c>),
    /// tried before the map's own <c>defaults</c> and before a need is reported unsatisfied.</param>
    public static ResolvedWorkspace Resolve(
        string workspace,
        MapDefinition? map,
        IReadOnlyList<ResolvedRepo> repos,
        IReadOnlyDictionary<string, int> allocatedPorts,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>? inlineLiterals = null)
    {
        var providers = CollectProviders(repos);
        var byCapability = providers.ToLookup(p => p.Capability, StringComparer.Ordinal);
        var resolvedOutputs = ResolveProviderOutputs(providers, allocatedPorts, workspace);

        var modules = new List<ResolvedModule>();
        var unsatisfied = new List<UnsatisfiedNeed>();

        foreach (var repo in repos)
        {
            foreach (var module in repo.Config.EffectiveModules)
            {
                var values = new Dictionary<string, string>(StringComparer.Ordinal);

                // This module's own provided outputs are referenceable by capability name.
                foreach (var cap in module.Provides)
                    foreach (var (output, value) in resolvedOutputs[(repo.Name, cap.Capability)])
                        values[$"{cap.Capability}.{output}"] = value;

                // Each need is wired to a provider — a sibling in this repo first (nearest-wins), then the map.
                foreach (var need in module.Needs)
                    WireNeed(repo.Name, module.Name, need, map, byCapability, resolvedOutputs, inlineLiterals, values, unsatisfied);

                modules.Add(new ResolvedModule(
                    repo.Name, repo.Root, module.Name, module.Path, module,
                    values, SprigScope.ForValues(workspace, values)));
            }
        }

        return new ResolvedWorkspace(modules, allocatedPorts, unsatisfied);
    }

    static void WireNeed(
        string repo, string moduleName, Need need, MapDefinition? map,
        ILookup<string, Provider> byCapability,
        IReadOnlyDictionary<(string Repo, string Cap), IReadOnlyDictionary<string, string>> resolvedOutputs,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>? inlineLiterals,
        Dictionary<string, string> values,
        List<UnsatisfiedNeed> unsatisfied)
    {
        // The head consumers reference the wired provider's outputs under — the need's own value name.
        var head = need.Value;

        // A map may bridge a generic need to a specific provider capability (need name != provider name).
        var target = Lookup2(map?.Wiring, repo, need.Value) ?? need.Value;
        var candidates = byCapability[target].ToList();

        // Nearest-wins: a provider in the same repo beats any other; otherwise a single map-wide provider.
        var chosen = candidates.FirstOrDefault(p => p.Repo == repo);
        if (chosen is null)
        {
            var others = candidates;
            if (others.Count == 1)
                chosen = others[0];
            else if (others.Count > 1)
                throw new MapResolutionException(
                    $"{repo}.{moduleName} needs '{need.Value}', but {others.Count} repos provide '{target}' " +
                    $"({string.Join(", ", others.Select(o => o.Repo))}). Add a map wiring entry to pick one.");
        }

        if (chosen is not null)
        {
            foreach (var (output, value) in resolvedOutputs[(chosen.Repo, chosen.Capability)])
                values[$"{head}.{output}"] = value;
            return;
        }

        // No provider in the selection — a per-checkout literal, then the map's default, else a reported gap.
        var fallback = Lookup3(inlineLiterals, repo, need.Value) ?? Lookup3(map?.Defaults, repo, need.Value);
        if (fallback is not null)
        {
            foreach (var (output, literal) in fallback)
                values[$"{head}.{output}"] = literal;
            return;
        }

        unsatisfied.Add(new UnsatisfiedNeed(repo, moduleName, need.Value));
    }

    /// <summary>Resolve every provider's outputs to concrete strings: a port is its allocated number; a
    /// derived shape is its template resolved against the capability's own outputs (+ <c>workspace</c>).</summary>
    static IReadOnlyDictionary<(string, string), IReadOnlyDictionary<string, string>> ResolveProviderOutputs(
        IReadOnlyList<Provider> providers, IReadOnlyDictionary<string, int> allocatedPorts, string workspace)
    {
        var result = new Dictionary<(string, string), IReadOnlyDictionary<string, string>>();
        foreach (var p in providers)
        {
            // Self-scope: the capability's own outputs (ports as numbers, shapes as raw templates) + workspace.
            var raw = new Dictionary<string, string>(StringComparer.Ordinal) { ["workspace"] = workspace };
            foreach (var (output, _) in p.Cap.Ports)
            {
                var lease = PortName(p.Repo, p.Capability, output);
                if (!allocatedPorts.TryGetValue(lease, out var number))
                    throw new MapResolutionException($"port '{lease}' was not allocated before resolve");
                raw[$"{p.Capability}.{output}"] = number.ToString(CultureInfo.InvariantCulture);
            }
            foreach (var (output, template) in p.Cap.Shapes)
                raw[$"{p.Capability}.{output}"] = template;

            var source = new DictionaryVariableSource(raw);
            var outputs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (output, _) in p.Cap.Ports)
                outputs[output] = raw[$"{p.Capability}.{output}"];
            foreach (var (output, template) in p.Cap.Shapes)
                outputs[output] = SubstitutionEngine.Resolve(template, source);
            result[(p.Repo, p.Capability)] = outputs;
        }
        return result;
    }

    static IReadOnlyList<Provider> CollectProviders(IReadOnlyList<ResolvedRepo> repos)
    {
        var providers = new List<Provider>();
        foreach (var repo in repos)
            foreach (var module in repo.Config.EffectiveModules)
                foreach (var cap in module.Provides)
                    providers.Add(new Provider(repo.Name, cap.Capability, module, cap));
        return providers;
    }

    static string? Lookup2(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? outer, string a, string b)
        => outer is not null && outer.TryGetValue(a, out var inner) && inner.TryGetValue(b, out var v) ? v : null;

    static IReadOnlyDictionary<string, string>? Lookup3(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>? outer, string a, string b)
        => outer is not null && outer.TryGetValue(a, out var inner) && inner.TryGetValue(b, out var v) ? v : null;

    sealed record Provider(string Repo, string Capability, ModuleDeclaration Module, ProvidedCapability Cap);
}
