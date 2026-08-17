using Sprig.Core.Config;
using Sprig.Core.Stacks;
using Sprig.Core.Workspaces;

namespace Sprig.Core.Maps;

/// <summary>
/// Resolves a named map + a repo selection into the concrete inputs a workspace create needs: the map
/// definition and each selected repo's path + validated config. Mirrors <c>StackResolver</c> for the map
/// model. Bootstrapping a git-URL repo that isn't registered yet is M5; until then an unregistered repo is
/// an error.
/// </summary>
public sealed class MapResolver(RepoRegistryStore registry, MapStore maps)
{
    public (MapDefinition Map, IReadOnlyList<ResolvedRepo> Repos) Resolve(string mapName, IReadOnlyList<string>? without = null)
    {
        var map = maps.Get(mapName) ?? throw new MapException($"unknown map '{mapName}'");
        var excluded = without is null ? new HashSet<string>() : new HashSet<string>(without, StringComparer.Ordinal);

        var repos = new List<ResolvedRepo>();
        foreach (var entry in map.Repos)
        {
            if (excluded.Contains(entry.Name)) continue;

            var reg = registry.Get(entry.Name)
                ?? throw new MapException(
                    $"repo '{entry.Name}' is not registered" +
                    (string.IsNullOrWhiteSpace(entry.Repo) ? "" : $" (bootstrap from {entry.Repo} lands in M5)"));

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
}
