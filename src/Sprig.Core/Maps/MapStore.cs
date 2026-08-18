using System.Text.RegularExpressions;
using Sprig.Core.Stacks;
using Sprig.Core.Store;

namespace Sprig.Core.Maps;

/// <summary>Thrown when a map can't be defined, found, or imported.</summary>
public sealed class MapException(string message) : Exception(message);

/// <summary>
/// Persists <see cref="MapDefinition"/>s in the central store (<c>maps/&lt;name&gt;.json</c>) and handles
/// export/import. Unlike a stack, a map is never frozen by live workspaces — selection and wiring are
/// resolved at checkout, so editing a map never invalidates an existing workspace.
/// </summary>
public sealed partial class MapStore(ISprigPaths paths, RepoRegistryStore registry)
{
    public void Save(MapDefinition map)
    {
        Validate(map);
        JsonFile.Write(paths.MapFile(map.Name), map);
    }

    public MapDefinition? Get(string name) => JsonFile.Read<MapDefinition>(paths.MapFile(name));

    public IReadOnlyList<MapDefinition> List()
    {
        if (!Directory.Exists(paths.MapsDir)) return [];
        return Directory.EnumerateFiles(paths.MapsDir, "*.json")
            .Select(f => JsonFile.Read<MapDefinition>(f))
            .OfType<MapDefinition>()
            .OrderBy(m => m.Name)
            .ToList();
    }

    public void Remove(string name)
    {
        var file = paths.MapFile(name);
        if (File.Exists(file)) File.Delete(file);
    }

    /// <summary>Copy a map's JSON out for sharing; returns the destination path.</summary>
    public string Export(string name, string destPath)
    {
        var map = Get(name) ?? throw new MapException($"unknown map '{name}'");
        JsonFile.Write(destPath, map);
        return destPath;
    }

    /// <summary>Read a map JSON from a file, validate against the registry, and save it.</summary>
    public MapDefinition Import(string sourcePath)
    {
        if (!File.Exists(sourcePath))
            throw new MapException($"map file not found: {sourcePath}");
        var map = JsonFile.Read<MapDefinition>(sourcePath)
            ?? throw new MapException($"could not read map from {sourcePath}");
        Save(map);
        return map;
    }

    void Validate(MapDefinition map)
    {
        if (string.IsNullOrWhiteSpace(map.Name) || !NamePattern().IsMatch(map.Name))
            throw new MapException($"invalid map name '{map.Name}' (use letters, digits, '.', '-', '_', '+')");
        if (map.Repos.Count == 0)
            throw new MapException("a map must reference at least one repo");
        if (map.MaxSlots is { } slots and < 1)
            throw new MapException($"map '{map.Name}' needs a pool size of at least 1 (maxSlots was {slots})");

        // Each repo either resolves in the local registry OR carries a git URL to bootstrap from (M5).
        // Report every un-resolvable repo at once.
        var unresolvable = map.Repos
            .Where(r => string.IsNullOrWhiteSpace(r.Repo) && registry.Get(r.Name) is null)
            .Select(r => r.Name)
            .ToList();
        if (unresolvable.Count > 0)
            throw new MapException(
                $"map '{map.Name}' references repo{(unresolvable.Count == 1 ? "" : "s")} " +
                $"{string.Join(", ", unresolvable.Select(r => $"'{r}'"))} that {(unresolvable.Count == 1 ? "is" : "are")} " +
                "neither registered nor carrying a git URL — register or add a URL first");

        // Wiring/defaults may only name repos the map includes. (Capability-level checks need the repo
        // configs and so happen at resolve time — see CapabilityResolver, M3.)
        var repos = new HashSet<string>(map.Repos.Select(r => r.Name), StringComparer.Ordinal);
        foreach (var repo in map.Wiring.Keys)
            if (!repos.Contains(repo))
                throw new MapException($"map '{map.Name}' wires repo '{repo}', which the map doesn't include");
        foreach (var repo in map.Defaults.Keys)
            if (!repos.Contains(repo))
                throw new MapException($"map '{map.Name}' has defaults for repo '{repo}', which the map doesn't include");
    }

    // Map names are filenames (allow '+' like stacks), not git branches.
    [GeneratedRegex(@"^[A-Za-z0-9._+-]+$")]
    private static partial Regex NamePattern();
}
