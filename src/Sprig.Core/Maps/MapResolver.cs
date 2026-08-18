using Sprig.Core.Config;
using Sprig.Core.Git;
using Sprig.Core.Stacks;
using Sprig.Core.Store;
using Sprig.Core.Workspaces;

namespace Sprig.Core.Maps;

/// <summary>
/// Resolves a named map + a repo selection into the concrete inputs a workspace create needs: the map
/// definition and each selected repo's path + validated config. Mirrors <c>StackResolver</c> for the map
/// model. A selected repo that isn't registered but carries a git URL is <b>bootstrapped</b> — cloned into
/// the store's clones dir and registered — so a shared map works on a fresh machine (the URL is the
/// canonical/upstream source; a fork workflow re-points origin afterwards).
/// </summary>
public sealed class MapResolver(RepoRegistryStore registry, MapStore maps, IGitService git, ISprigPaths paths)
{
    public (MapDefinition Map, IReadOnlyList<ResolvedRepo> Repos) Resolve(string mapName, IReadOnlyList<string>? without = null)
    {
        var map = maps.Get(mapName) ?? throw new MapException($"unknown map '{mapName}'");
        var excluded = without is null ? new HashSet<string>() : new HashSet<string>(without, StringComparer.Ordinal);

        var repos = new List<ResolvedRepo>();
        foreach (var entry in map.Repos)
        {
            if (excluded.Contains(entry.Name)) continue;

            var reg = registry.Get(entry.Name) ?? Bootstrap(entry);

            var config = SprigConfigLoader.LoadFromFile(Path.Combine(reg.Path, WorkspaceService.ConfigFileName));
            var validation = SprigConfigValidator.Validate(config);
            if (!validation.IsValid)
                throw new MapException($"repo '{entry.Name}' has an invalid .sprig.json:\n  " +
                    string.Join("\n  ", validation.Issues));

            repos.Add(new ResolvedRepo(entry.Name, reg.Path, config));
        }

        if (repos.Count == 0)
            throw new MapException($"map '{mapName}' has no repos to check out (after any --without)");

        return (map, repos);
    }

    /// <summary>Clone a map's git-URL repo into the store's clones dir and register it. The map URL becomes
    /// the clone's <c>origin</c> (canonical/upstream). An unregistered repo with no URL is a hard error.</summary>
    RegisteredRepo Bootstrap(MapRepo entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Repo))
            throw new MapException(
                $"repo '{entry.Name}' is not registered and the map gives no git URL to bootstrap it from");

        var dest = paths.ClonePath(entry.Name);
        if (!Directory.Exists(dest))
        {
            Directory.CreateDirectory(paths.ClonesDir);
            git.Clone(entry.Repo!, dest);
        }
        // registry.Add validates a committed .sprig.json is present and derives/pins the name.
        return registry.Add(dest, entry.Name);
    }
}
