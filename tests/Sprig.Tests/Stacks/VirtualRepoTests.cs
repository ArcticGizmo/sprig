using Sprig.Core.Compose;
using Sprig.Core.Config;
using Sprig.Core.Env;
using Sprig.Core.Git;
using Sprig.Core.Maps;
using Sprig.Core.Ports;
using Sprig.Core.Processes;
using Sprig.Core.Stacks;
using Sprig.Core.Store;
using Sprig.Core.Workspaces;

namespace Sprig.Tests.Stacks;

/// <summary>Virtual repos: a registration whose config sprig holds instead of the repo, so a config can be tried
/// before it's committed and one checkout can carry several configs under different names.</summary>
public class VirtualRepoTests
{
    const string Config = """
        { "schema":1, "name":"client", "modules":[ { "name":"spa", "path":"",
          "provides":[ { "capability":"client-spa", "ports":{ "port":true },
                         "shapes":{ "url":"http://localhost:${sprig.client-spa.port}" }, "open":"url" } ] } ] }
        """;

    static SprigRepoConfig Parsed => SprigConfigLoader.Parse(Config);

    static WorkspaceService Service(TempStore s) =>
        new(new GitService(new ProcessRunner()), new FilePortStore(s.Paths), new InstanceStore(s.Paths),
            new EnvClobberService(), new ComposeGenerator(), new FakeDockerService(), s.Paths);

    [Fact]
    public void AddVirtual_keeps_the_config_in_the_store_and_leaves_the_repo_untouched()
    {
        using var s = new TempStore();
        var dir = Path.Combine(s.Root, "checkout");
        Directory.CreateDirectory(dir);
        var registry = new RepoRegistryStore(s.Paths);

        var added = registry.AddVirtual(dir, "client-try", Parsed);

        Assert.True(added.IsVirtual);
        Assert.Equal(s.Paths.RepoConfigFile("client-try"), added.ConfigPath);
        Assert.False(File.Exists(Path.Combine(dir, ".sprig.json")));
        Assert.Equal("client-try", SprigConfigLoader.LoadFromFile(added.ConfigPath).Name);
        Assert.True(Assert.Single(registry.List()).IsVirtual);
    }

    [Fact]
    public void One_checkout_can_be_registered_under_several_configs()
    {
        using var s = new TempStore();
        var dir = Path.Combine(s.Root, "checkout");
        Directory.CreateDirectory(dir);
        var registry = new RepoRegistryStore(s.Paths);

        registry.AddVirtual(dir, "try-a", Parsed);
        registry.AddVirtual(dir, "try-b", Parsed with { Modules = [] });

        Assert.Equal(["try-a", "try-b"], registry.List().Select(r => r.Name));
        Assert.Single(SprigConfigLoader.LoadFromFile(registry.Get("try-a")!.ConfigPath).Modules);
        Assert.Empty(SprigConfigLoader.LoadFromFile(registry.Get("try-b")!.ConfigPath).Modules);
    }

    [Fact]
    public void A_committed_repo_is_not_virtual()
    {
        using var s = new TempStore();
        var dir = Path.Combine(s.Root, "checkout");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".sprig.json"), Config);

        var added = new RepoRegistryStore(s.Paths).Add(dir);

        Assert.False(added.IsVirtual);
        Assert.Equal(Path.Combine(dir, ".sprig.json"), added.ConfigPath);
    }

    [Fact]
    public void Names_cannot_collide_across_virtual_and_committed_registrations()
    {
        using var s = new TempStore();
        var dir = Path.Combine(s.Root, "checkout");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".sprig.json"), Config);
        var registry = new RepoRegistryStore(s.Paths);

        registry.AddVirtual(dir, "client", Parsed);

        Assert.Throws<RepoRegistryException>(() => registry.Add(dir));                      // committed name is 'client'
        Assert.Throws<RepoRegistryException>(() => registry.AddVirtual(dir, "client", Parsed));
        Assert.Throws<RepoRegistryException>(() => registry.AddVirtual(dir, "bad/name", Parsed));
    }

    [Fact]
    public void Remove_deletes_the_virtual_config_with_the_registration()
    {
        using var s = new TempStore();
        var dir = Path.Combine(s.Root, "checkout");
        Directory.CreateDirectory(dir);
        var registry = new RepoRegistryStore(s.Paths);
        var added = registry.AddVirtual(dir, "client-try", Parsed);

        registry.Remove("client-try");

        Assert.Empty(registry.List());
        Assert.False(File.Exists(added.ConfigPath));
    }

    [Fact]
    public void A_map_resolves_a_virtual_repo_from_the_store()
    {
        using var s = new TempStore();
        using var repo = new TempGitRepo("client");
        var registry = new RepoRegistryStore(s.Paths);
        registry.AddVirtual(repo.Path, "client-try", Parsed);
        var maps = new MapStore(s.Paths, registry);
        maps.Save(new MapDefinition { Name = "world", Repos = [new MapRepo { Name = "client-try" }] });

        var (_, repos) = new MapResolver(registry, maps, new GitService(new ProcessRunner()), s.Paths).Resolve("world");

        var resolved = Assert.Single(repos);
        Assert.Equal(s.Paths.RepoConfigFile("client-try"), resolved.VirtualConfigPath);
        Assert.Equal("client-try", resolved.Config.Name);
    }

    [Fact]
    public void A_workspace_from_a_virtual_repo_reads_its_config_from_the_store()
    {
        using var s = new TempStore();
        using var repo = new TempGitRepo("client");
        var reg = new RepoRegistryStore(s.Paths).AddVirtual(repo.Path, "client-try", Parsed);
        var svc = Service(s);

        var record = svc.CreateFromMap("ws1", null, svc.ResolveSingleRepo(reg));

        var built = Assert.Single(record.Repos);
        Assert.Equal(reg.VirtualConfigPath, built.VirtualConfigPath);
        Assert.False(File.Exists(Path.Combine(built.WorktreePath, ".sprig.json")));   // nothing committed to read
        Assert.Equal("client-spa", Assert.Single(svc.Urls(record)).Capability);
        Assert.NotEmpty(svc.PlanRefresh(record, null));                               // reloads from the store
        svc.Remove("ws1");
    }

    [Fact]
    public void Two_configs_of_one_checkout_cannot_share_a_workspace()
    {
        using var s = new TempStore();
        using var repo = new TempGitRepo("client");
        var registry = new RepoRegistryStore(s.Paths);
        var svc = Service(s);
        var repos = new[]
        {
            svc.ResolveSingleRepo(registry.AddVirtual(repo.Path, "try-a", Parsed))[0],
            svc.ResolveSingleRepo(registry.AddVirtual(repo.Path, "try-b", Parsed))[0],
        };

        var ex = Assert.Throws<WorkspaceException>(() => svc.PlanCreateFromMap(repos, "ws1"));
        Assert.Contains("same checkout", ex.Message);
    }
}
