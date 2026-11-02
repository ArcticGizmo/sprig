using System.Text.RegularExpressions;
using Sprig.Core.Config;
using Sprig.Core.Env;
using Sprig.Core.Stacks;
using Sprig.Core.Workspaces;

namespace Sprig.Core.Maps;

/// <summary>
/// Builds a <see cref="MapInspection"/>. Unlike <see cref="MapResolver"/> it never clones or throws over a
/// repo: an unregistered or unreadable repo becomes a <see cref="RepoProblem"/>, so a broken map can still
/// be looked at. Need states come from <see cref="MapGraphProjection"/>, so they always match checkout.
/// </summary>
public sealed partial class MapInspector(RepoRegistryStore registry, MapStore maps)
{
    public MapInspection Inspect(string mapName)
        => Inspect(maps.Get(mapName) ?? throw new MapException($"unknown map '{mapName}'"));

    public MapInspection Inspect(MapDefinition map)
    {
        var problems = new List<RepoProblem>();
        var repos = new List<ResolvedRepo>();
        foreach (var entry in map.Repos)
        {
            if (Load(entry, out var problem) is { } repo) repos.Add(repo);
            else problems.Add(problem!);
        }
        return Inspect(map, repos, problems);
    }

    ResolvedRepo? Load(MapRepo entry, out RepoProblem? problem)
    {
        problem = null;
        if (registry.Get(entry.Name) is not { } reg)
        {
            problem = new(entry.Name, string.IsNullOrWhiteSpace(entry.Repo)
                ? "isn't registered on this machine, and the map gives no git URL to clone it from"
                : $"isn't registered on this machine yet; checkout will clone it from {entry.Repo}");
            return null;
        }
        if (!File.Exists(reg.ConfigPath))
        {
            problem = new(entry.Name, reg.IsVirtual ? "its virtual config is missing" : "it has no .sprig.json");
            return null;
        }
        SprigRepoConfig config;
        try { config = SprigConfigLoader.LoadFromFile(reg.ConfigPath); }
        catch (SprigConfigException ex)
        {
            problem = new(entry.Name, ex.Message);
            return null;
        }
        var validation = SprigConfigValidator.Validate(config);
        if (!validation.IsValid)
        {
            problem = new(entry.Name, "its config is invalid: " + string.Join("; ", validation.Issues.Take(3))
                + (validation.Issues.Count > 3 ? $" (and {validation.Issues.Count - 3} more)" : ""));
            return null;
        }
        return new ResolvedRepo(entry.Name, reg.Path, config, reg.VirtualConfigPath);
    }

    /// <summary>Inspect already-loaded repos. Reads the repos' env files for value hints, best-effort.</summary>
    public static MapInspection Inspect(MapDefinition map, IReadOnlyList<ResolvedRepo> repos, IReadOnlyList<RepoProblem>? problems = null)
    {
        var graph = MapGraphProjection.Project(map, repos);
        var providerModule = new Dictionary<(string Repo, string Cap), string>();
        var allProvides = new List<ProviderRef>();
        foreach (var repo in repos)
            foreach (var module in repo.Config.EffectiveModules)
                foreach (var p in module.Provides)
                {
                    providerModule.TryAdd((repo.Name, p.Capability), module.Name);
                    allProvides.Add(new ProviderRef(repo.Name, module.Name, p.Capability));
                }
        ProviderRef Ref(string repo, string cap) => new(repo, providerModule.GetValueOrDefault((repo, cap)) ?? "", cap);

        // Needs first: the provides' consumer lists and the modules' hub counts are derived from them.
        var needsByModule = new Dictionary<(string, string), List<NeedInfo>>();
        foreach (var (repo, node) in repos.Zip(graph.Nodes))
            foreach (var (module, gm) in repo.Config.EffectiveModules.Zip(node.Modules))
            {
                var list = needsByModule[(repo.Name, module.Name)] = [];
                var refs = References(module);
                foreach (var (need, wired) in module.Needs.Zip(gm.Needs))
                {
                    var (state, providers) = wired switch
                    {
                        { Status: WireStatus.Resolved, FromConstant: true } => (NeedState.Constant, (IReadOnlyList<ProviderRef>)[]),
                        { Status: WireStatus.Resolved, FromDefault: true } => (NeedState.Default, []),
                        { Status: WireStatus.Resolved } => (NeedState.Wired, [Ref(wired.ProviderRepo!, wired.ProviderCapability!)]),
                        { Status: WireStatus.Ambiguous } => (NeedState.Ambiguous, wired.Candidates.Select(r => Ref(r, wired.ProviderCapability!)).ToList()),
                        _ => (NeedState.Gap, []),
                    };
                    var target = MapGraphProjection.Target(map, repo.Name, need.Capability);
                    var values = state switch
                    {
                        NeedState.Constant => Lookup(map.Constants, repo.Name, need.Capability),
                        NeedState.Default => Lookup(map.Defaults, repo.Name, need.Capability),
                        _ => null,
                    };
                    var mine = refs.Where(r => r.Head == need.Alias).ToList();
                    var uses = mine.Select(r => r.Use).Distinct().ToList();
                    var outputs = mine.Select(r => r.Output).Distinct(StringComparer.Ordinal).ToList();
                    var similar = state == NeedState.Gap ? NearestName(need.Capability, target.Capability, allProvides) : null;
                    var hints = state is NeedState.Wired or NeedState.Ambiguous ? [] : Hints(repo, module, need.Alias);
                    list.Add(new NeedInfo(repo.Name, module.Name, need.Capability, need.Alias, state, target.ToString(),
                        providers, values, uses, similar, hints, outputs));
                }
            }

        var allNeeds = needsByModule.Values.SelectMany(n => n).ToList();
        var repoInfos = new List<RepoInfo>();
        foreach (var repo in repos)
        {
            // A module's own need alias shadows a same-named provide, so those references are the need's.
            var ownRefs = repo.Config.EffectiveModules.SelectMany(m =>
            {
                var aliases = m.Needs.Select(n => n.Alias).ToHashSet(StringComparer.Ordinal);
                return References(m).Where(r => !aliases.Contains(r.Head));
            }).ToList();
            var modules = new List<ModuleInfo>();
            foreach (var module in repo.Config.EffectiveModules)
            {
                var provides = module.Provides.Select(p => new ProvideInfo(
                    repo.Name, module.Name, p.Capability, p.OutputNames.ToList(), Constants(p),
                    allNeeds.Where(n => n.State == NeedState.Wired && n.Providers[0].Repo == repo.Name && n.Providers[0].Capability == p.Capability)
                        .Select(n => new ModuleRef(n.Repo, n.Module)).Distinct().ToList(),
                    ownRefs.Where(r => r.Head == p.Capability).Select(r => r.Use).Distinct().ToList()))
                    .ToList();
                var reliedOnBy = provides.SelectMany(p => p.Consumers)
                    .Where(c => !(c.Repo == repo.Name && c.Module == module.Name)).Distinct().Count();
                modules.Add(new ModuleInfo(repo.Name, module.Name, module.Path, provides, needsByModule[(repo.Name, module.Name)], reliedOnBy));
            }
            repoInfos.Add(new RepoInfo(repo.Name, repo.Root, repo.VirtualConfigPath is not null, modules));
        }

        return new MapInspection(map, repoInfos, problems ?? [], repos.SelectMany(FixedValues).ToList());
    }

    static IReadOnlyDictionary<string, string>? Lookup(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> outer, string repo, string cap)
        => outer.TryGetValue(repo, out var inner) && inner.TryGetValue(cap, out var v) ? v : null;

    /// <summary>A provide with no port and no references in its shapes hands every workspace the same values.</summary>
    static IReadOnlyDictionary<string, string>? Constants(ProvidedCapability p)
        => p.Ports.Count == 0 && p.Shapes.Count > 0 && p.Shapes.Values.All(t => !t.Contains("${sprig.", StringComparison.Ordinal))
            ? p.Shapes
            : null;

    readonly record struct Reference(string Head, string Output, TemplateUse Use);

    // Every ${sprig.<head>.<output>} a module's env and compose templates make, with where each one sits.
    static IReadOnlyList<Reference> References(ModuleDeclaration module)
    {
        var found = new List<Reference>();
        void Scan(string template, TemplateUse use)
        {
            foreach (var name in ConfigReferences.ReferencedNames(template))
            {
                var dot = name.IndexOf('.');
                if (dot > 0) found.Add(new Reference(name[..dot], name[(dot + 1)..], use));
            }
        }
        foreach (var env in module.Env)
            foreach (var (key, template) in env.Set)
                Scan(template, new TemplateUse(RepoRelative(module, env.File), key));
        foreach (var compose in module.Compose)
            foreach (var o in compose.Overrides)
                Scan(o.Template, new TemplateUse(RepoRelative(module, compose.File), string.Join(".", o.Path)));
        return found;
    }

    static string RepoRelative(ModuleDeclaration module, string file)
    {
        var f = file.Replace('\\', '/').TrimStart('/');
        var dir = module.Path.Replace('\\', '/').Trim('/');
        return dir.Length == 0 ? f : $"{dir}/{f}";
    }

    /// <summary>The provide whose name is close enough to <paramref name="need"/> (or the wiring target) to
    /// look like a typo: equal once case and <c>_</c>/<c>-</c> are ignored, else one edit for names of up to
    /// eight characters and two for longer ones. Short names are skipped, since nearly everything is close.</summary>
    internal static ProviderRef? NearestName(string need, string target, IReadOnlyList<ProviderRef> provides)
    {
        ProviderRef? best = null;
        var bestDistance = int.MaxValue;
        foreach (var p in provides)
            foreach (var name in new[] { need, target }.Distinct())
            {
                if (p.Capability == name) continue;
                var a = Normalise(name);
                var b = Normalise(p.Capability);
                var d = EditDistance(a, b);
                var shortest = Math.Min(a.Length, b.Length);
                var close = d == 0 || (shortest >= 4 && d <= (shortest <= 8 ? 1 : 2));
                if (close && d < bestDistance) { best = p; bestDistance = d; }
            }
        return best;
    }

    static string Normalise(string s) => s.ToLowerInvariant().Replace('_', '-');

    static int EditDistance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    /// <summary>
    /// Likely values for an unmet need, recovered from the env files a module seeds from. Where the module
    /// sets <c>KEY = "prefix${sprig.alias.output}suffix"</c> and an env file or template has
    /// <c>KEY=prefix…suffix</c>, the middle is what the output was before sprig wired it.
    /// </summary>
    static IReadOnlyList<ValueHint> Hints(ResolvedRepo repo, ModuleDeclaration module, string alias)
    {
        var hints = new Dictionary<string, ValueHint>(StringComparer.Ordinal);
        foreach (var env in module.Env)
            foreach (var (key, template) in env.Set)
            {
                var refs = RefPattern().Matches(template);
                if (refs.Count != 1) continue;
                var name = refs[0].Groups[1].Value.Trim();
                var dot = name.IndexOf('.');
                if (dot <= 0 || name[..dot] != alias) continue;
                var output = name[(dot + 1)..];
                if (hints.ContainsKey(output)) continue;
                var prefix = template[..refs[0].Index];
                var suffix = template[(refs[0].Index + refs[0].Length)..];
                foreach (var (source, value) in Examples(repo.Root, module, env, key))
                {
                    if (value.Length <= prefix.Length + suffix.Length || !value.StartsWith(prefix, StringComparison.Ordinal)
                        || !value.EndsWith(suffix, StringComparison.Ordinal)) continue;
                    var inner = value[prefix.Length..^suffix.Length];
                    if (inner.Contains("${", StringComparison.Ordinal)) continue;
                    hints[output] = new ValueHint(output, inner, $"{key} in {source}");
                    break;
                }
            }
        return hints.Values.OrderBy(h => h.Output, StringComparer.Ordinal).ToList();
    }

    // The values KEY has across the env file, its conventional template companions and its declared templates.
    static IEnumerable<(string Source, string Value)> Examples(string repoRoot, ModuleDeclaration module, EnvOverride env, string key)
    {
        if (EnvKeyReader.ExamplesForFile(repoRoot, RepoRelative(module, env.File)).TryGetValue(key, out var examples))
            foreach (var e in examples)
                yield return (e.Source, e.Value);
        foreach (var template in env.Templates ?? [])
        {
            var rel = RepoRelative(module, template);
            string text;
            try { text = File.ReadAllText(Path.Combine(repoRoot, rel)); }
            catch { continue; }
            foreach (var (k, v) in EnvKeyReader.Parse(text))
                if (k == key && v.Length > 0)
                    yield return (rel, v);
        }
    }

    static IEnumerable<FixedValue> FixedValues(ResolvedRepo repo)
    {
        foreach (var module in repo.Config.EffectiveModules)
        {
            foreach (var env in module.Env)
                foreach (var (key, value) in env.Set)
                    if (Classify(key, value) is { } kind)
                        yield return new FixedValue(repo.Name, module.Name, RepoRelative(module, env.File), key, value, kind);
            foreach (var compose in module.Compose)
                foreach (var o in compose.Overrides)
                    if (!o.Template.Contains("${sprig.", StringComparison.Ordinal) && HostPortMapping().IsMatch(o.Template))
                        yield return new FixedValue(repo.Name, module.Name, RepoRelative(module, compose.File),
                            string.Join(".", o.Path), o.Template, FixedKind.Port);
        }
    }

    static FixedKind? Classify(string key, string value)
    {
        if (value.Contains("${sprig.", StringComparison.Ordinal)) return null;
        if (LocalAddress().IsMatch(value)) return FixedKind.Address;
        if (key.Contains("PORT", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(value.Trim(), out var port) && port is >= 1 and <= 65535) return FixedKind.Port;
        return null;
    }

    [GeneratedRegex(@"\$\{sprig\.([^}]+)\}")]
    private static partial Regex RefPattern();

    [GeneratedRegex(@"(?i)(?<![\w.-])(localhost|127\.0\.0\.1|0\.0\.0\.0|\[::1\]):\d{2,5}(?!\d)")]
    private static partial Regex LocalAddress();

    [GeneratedRegex(@"^\s*(?:[\d.]+:)?\d{2,5}:\d{1,5}(?:/(?:tcp|udp))?\s*$")]
    private static partial Regex HostPortMapping();
}
