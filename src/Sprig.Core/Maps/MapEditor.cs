using System.Text.RegularExpressions;
using Sprig.Core.Config;
using Sprig.Core.Stacks;

namespace Sprig.Core.Maps;

/// <summary>
/// The fixes the Maps view and <c>sprig map default/wire</c> apply: a map's constant values and wiring
/// entries, and turning a fixed env value into a need with a map default. Every edit goes through
/// <see cref="MapStore.Save"/>, so it is validated like any other map.
/// </summary>
public sealed partial class MapEditor(MapStore maps, RepoRegistryStore registry)
{
    /// <summary>Set the constant values a map gives <paramref name="repo"/>'s <paramref name="capability"/>
    /// need when nothing in the map provides it. Replaces any values already set for that need.</summary>
    public MapDefinition SetDefault(string mapName, string repo, string capability, IReadOnlyDictionary<string, string> values)
    {
        var map = Load(mapName);
        RequireRepo(map, repo);
        RequireName(capability, "capability");
        if (values.Count == 0)
            throw new MapException("give at least one output and its value, e.g. url=https://staging.example.test");
        foreach (var (output, value) in values)
        {
            RequireName(output, "output");
            if (string.IsNullOrWhiteSpace(value))
                throw new MapException($"the value for '{output}' is empty");
        }

        var defaults = Copy(map.Defaults);
        if (!defaults.TryGetValue(repo, out var caps)) defaults[repo] = caps = new(StringComparer.Ordinal);
        caps[capability] = new Dictionary<string, string>(values, StringComparer.Ordinal);
        return Save(map with { Defaults = Freeze(defaults) });
    }

    public MapDefinition RemoveDefault(string mapName, string repo, string capability)
    {
        var map = Load(mapName);
        var defaults = Copy(map.Defaults);
        if (!defaults.TryGetValue(repo, out var caps) || !caps.Remove(capability))
            throw new MapException($"map '{mapName}' has no constant for {repo}'s '{capability}'");
        if (caps.Count == 0) defaults.Remove(repo);
        return Save(map with { Defaults = Freeze(defaults) });
    }

    /// <summary>Wire <paramref name="repo"/>'s <paramref name="need"/> to <paramref name="target"/>: a
    /// capability name, or <c>repo/capability</c> to pick the providing repo.</summary>
    public MapDefinition SetWiring(string mapName, string repo, string need, string target)
    {
        var map = Load(mapName);
        RequireRepo(map, repo);
        RequireName(need, "need");
        var parsed = WiringTarget.Parse(target.Trim());
        RequireName(parsed.Capability, "capability");

        var wiring = map.Wiring.ToDictionary(kv => kv.Key, kv => new Dictionary<string, string>(kv.Value), StringComparer.Ordinal);
        if (!wiring.TryGetValue(repo, out var needs)) wiring[repo] = needs = new(StringComparer.Ordinal);
        needs[need] = parsed.ToString();
        return Save(map with { Wiring = wiring.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, string>)kv.Value, StringComparer.Ordinal) });
    }

    public MapDefinition RemoveWiring(string mapName, string repo, string need)
    {
        var map = Load(mapName);
        var wiring = map.Wiring.ToDictionary(kv => kv.Key, kv => new Dictionary<string, string>(kv.Value), StringComparer.Ordinal);
        if (!wiring.TryGetValue(repo, out var needs) || !needs.Remove(need))
            throw new MapException($"map '{mapName}' has no wiring entry for {repo}'s '{need}'");
        if (needs.Count == 0) wiring.Remove(repo);
        return Save(map with { Wiring = wiring.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, string>)kv.Value, StringComparer.Ordinal) });
    }

    /// <summary>
    /// Turn a literal env value into a need: the module declares <paramref name="capability"/>, the env key
    /// reads <c>${sprig.capability.output}</c>, and the map supplies today's value as a constant. Writes the
    /// repo's config (its <c>.sprig.json</c>, or sprig's copy for a virtual repo) and then the map; if the
    /// map can't be saved the config is put back.
    /// </summary>
    public MapDefinition ConvertFixedValue(string mapName, FixedValue fixedValue, string capability, string output)
    {
        var map = Load(mapName);
        RequireRepo(map, fixedValue.Repo);
        RequireName(capability, "capability");
        RequireName(output, "output");

        var reg = registry.Get(fixedValue.Repo) ?? throw new MapException($"repo '{fixedValue.Repo}' isn't registered");
        var original = File.ReadAllText(reg.ConfigPath);
        var config = SprigConfigLoader.Parse(original, reg.ConfigPath);
        var updated = WithNeed(config, fixedValue, capability, output);

        var validation = SprigConfigValidator.Validate(updated);
        if (!validation.IsValid)
            throw new MapException($"that change would make {fixedValue.Repo}'s config invalid: {string.Join("; ", validation.Issues)}");

        SprigConfigWriter.Write(updated, reg.ConfigPath);
        try
        {
            var defaults = Copy(map.Defaults);
            if (!defaults.TryGetValue(fixedValue.Repo, out var caps)) defaults[fixedValue.Repo] = caps = new(StringComparer.Ordinal);
            caps[capability] = new Dictionary<string, string>(StringComparer.Ordinal) { [output] = fixedValue.Value };
            return Save(map with { Defaults = Freeze(defaults) });
        }
        catch
        {
            File.WriteAllText(reg.ConfigPath, original);
            throw;
        }
    }

    static SprigRepoConfig WithNeed(SprigRepoConfig config, FixedValue fixedValue, string capability, string output)
    {
        var reference = $"${{sprig.{capability}.{output}}}";
        var found = false;

        IReadOnlyList<EnvOverride> Rewrite(ModuleDeclaration module, IReadOnlyList<EnvOverride> env) => env.Select(e =>
        {
            if (Normalise(Join(module.Path, e.File)) != Normalise(fixedValue.File)
                || !e.Set.TryGetValue(fixedValue.Key, out var value) || value != fixedValue.Value)
                return e;
            found = true;
            return e with { Set = new Dictionary<string, string>(e.Set, StringComparer.Ordinal) { [fixedValue.Key] = reference } };
        }).ToList();

        IReadOnlyList<Need> AddNeed(IReadOnlyList<Need> needs)
            => needs.Any(n => n.Capability == capability) ? needs : [.. needs, new Need { Capability = capability }];

        // A flat config's top-level sugar is its implicit module; declared modules are edited in place.
        var implicitModule = config.EffectiveModules.FirstOrDefault(m => !config.Modules.Contains(m));
        if (implicitModule is not null && implicitModule.Name == fixedValue.Module)
        {
            var env = Rewrite(implicitModule, config.Env ?? []);
            if (found) config = config with { Env = env, Needs = AddNeed(config.Needs) };
        }
        else
        {
            config = config with
            {
                Modules = config.Modules.Select(m =>
                {
                    if (m.Name != fixedValue.Module) return m;
                    var env = Rewrite(m, m.Env);
                    return found ? m with { Env = env, Needs = AddNeed(m.Needs) } : m;
                }).ToList(),
            };
        }

        if (!found)
            throw new MapException($"{fixedValue.Repo} › {fixedValue.Module} no longer sets {fixedValue.Key} = {fixedValue.Value} in {fixedValue.File}");
        return config;
    }

    static string Join(string dir, string file)
    {
        var d = dir.Replace('\\', '/').Trim('/');
        var f = file.Replace('\\', '/').TrimStart('/');
        return d.Length == 0 ? f : $"{d}/{f}";
    }

    static string Normalise(string path) => path.Replace('\\', '/').TrimStart('/');

    MapDefinition Load(string mapName) => maps.Get(mapName) ?? throw new MapException($"unknown map '{mapName}'");

    MapDefinition Save(MapDefinition map)
    {
        maps.Save(map);
        return map;
    }

    static void RequireRepo(MapDefinition map, string repo)
    {
        if (!map.Repos.Any(r => r.Name == repo))
            throw new MapException($"map '{map.Name}' doesn't include repo '{repo}'");
    }

    static void RequireName(string name, string what)
    {
        if (string.IsNullOrWhiteSpace(name) || !Identifier().IsMatch(name))
            throw new MapException($"'{name}' isn't a valid {what} name (use letters, digits, '-' or '_')");
    }

    static Dictionary<string, Dictionary<string, Dictionary<string, string>>> Copy(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> defaults)
        => defaults.ToDictionary(
            r => r.Key,
            r => r.Value.ToDictionary(c => c.Key, c => new Dictionary<string, string>(c.Value, StringComparer.Ordinal), StringComparer.Ordinal),
            StringComparer.Ordinal);

    static IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> Freeze(
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> defaults)
        => defaults.ToDictionary(
            r => r.Key,
            r => (IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>)r.Value.ToDictionary(
                c => c.Key, c => (IReadOnlyDictionary<string, string>)c.Value, StringComparer.Ordinal),
            StringComparer.Ordinal);

    [GeneratedRegex(@"^[A-Za-z0-9_-]+$")]
    private static partial Regex Identifier();
}
