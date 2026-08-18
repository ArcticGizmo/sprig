using Sprig.Core.Git;
using Sprig.Core.Maps;
using Sprig.Core.Processes;
using Sprig.Core.Stacks;

namespace Sprig.Tests.Maps;

/// <summary>M5 — a map that references a repo by git URL bootstraps it on checkout: clone into the store's
/// clones dir + register, so a shared map works on a machine that hasn't registered the repo.</summary>
public class MapResolverBootstrapTests
{
    const string Config =
        """{ "schema": 1, "name": "solo", "provides": [ { "capability": "api", "outputs": { "port": { "port": true } } } ] }""";

    static TempGitRepo SourceRepo()
    {
        var repo = new TempGitRepo("solo");
        File.WriteAllText(Path.Combine(repo.Path, ".sprig.json"), Config);
        repo.Git("add", "-A");
        repo.Git("-c", "user.email=t@t", "-c", "user.name=t", "commit", "-m", "cfg");
        return repo;
    }

    static MapResolver Resolver(TempStore store, RepoRegistryStore registry, MapStore maps)
        => new(registry, maps, new GitService(new ProcessRunner()), store.Paths);

    [Fact]
    public void An_unregistered_git_url_repo_is_cloned_into_the_store_and_registered()
    {
        using var store = new TempStore();
        using var source = SourceRepo();
        var registry = new RepoRegistryStore(store.Paths);
        var maps = new MapStore(store.Paths, registry);
        // A URL-carrying repo (its source path stands in for a git URL) — not registered.
        maps.Save(new MapDefinition { Name = "world", Repos = [new MapRepo { Name = "solo", Repo = source.Path }] });

        var (_, repos) = Resolver(store, registry, maps).Resolve("world");

        var resolved = Assert.Single(repos);
        Assert.Equal("solo", resolved.Name);
        Assert.Equal(store.Paths.ClonePath("solo"), resolved.Root);
        Assert.True(File.Exists(Path.Combine(store.Paths.ClonePath("solo"), ".sprig.json")));
        Assert.NotNull(registry.Get("solo"));   // now known for next time
        Assert.Single(resolved.Config.Provides);
    }

    [Fact]
    public void An_already_registered_repo_is_used_as_is_and_not_cloned()
    {
        using var store = new TempStore();
        using var source = SourceRepo();
        var registry = new RepoRegistryStore(store.Paths);
        registry.Add(source.Path);                 // already known at its real path
        var maps = new MapStore(store.Paths, registry);
        maps.Save(new MapDefinition { Name = "world", Repos = [new MapRepo { Name = "solo", Repo = source.Path }] });

        var (_, repos) = Resolver(store, registry, maps).Resolve("world");

        Assert.Equal(source.Path, Assert.Single(repos).Root);
        Assert.False(Directory.Exists(store.Paths.ClonePath("solo")));   // nothing cloned into the store
    }
}
