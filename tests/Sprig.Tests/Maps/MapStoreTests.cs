using System.Text.Json;
using Sprig.Core.Maps;
using Sprig.Core.Stacks;

namespace Sprig.Tests.Maps;

public class MapStoreTests
{
    static (TempStore store, RepoRegistryStore registry, MapStore maps) Fresh()
    {
        var store = new TempStore();
        var registry = new RepoRegistryStore(store.Paths);
        return (store, registry, new MapStore(store.Paths, registry));
    }

    static void Register(TempStore store, RepoRegistryStore registry, string name)
    {
        var dir = Path.Combine(store.Root, "repos", name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".sprig.json"), $$"""{ "schema":3, "name":"{{name}}" }""");
        registry.Add(dir);
    }

    [Fact]
    public void Saves_and_round_trips_a_map()
    {
        var (store, registry, maps) = Fresh();
        using var _ = store;
        Register(store, registry, "acme");

        var map = new MapDefinition
        {
            Name = "orders-work",
            Repos = [MapRepo.Local("acme"), new MapRepo { Name = "billing", Repo = "git@github.com:me/billing.git" }],
            Wiring = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["acme"] = new Dictionary<string, string> { ["http-api"] = "orders-api" },
            },
            Defaults = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>
            {
                ["acme"] = new Dictionary<string, IReadOnlyDictionary<string, string>>
                {
                    ["auth"] = new Dictionary<string, string> { ["url"] = "https://auth.staging" },
                },
            },
            MaxSlots = 3,
        };
        maps.Save(map);

        var back = maps.Get("orders-work")!;
        Assert.Equal(2, back.Repos.Count);
        Assert.Equal("acme", back.Repos[0].Name);
        Assert.Null(back.Repos[0].Repo);
        Assert.Equal("git@github.com:me/billing.git", back.Repos[1].Repo);
        Assert.Equal("orders-api", back.Wiring["acme"]["http-api"]);
        Assert.Equal("https://auth.staging", back.Defaults["acme"]["auth"]["url"]);
        Assert.Equal(3, back.MaxSlots);
    }

    [Fact]
    public void MapRepo_bare_string_and_object_forms_both_parse()
    {
        var json = """{ "schema":1, "name":"m", "repos":[ "local", { "name":"remote", "repo":"git@x/y.git" } ] }""";
        var map = JsonSerializer.Deserialize<MapDefinition>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal("local", map.Repos[0].Name);
        Assert.Null(map.Repos[0].Repo);
        Assert.Equal("remote", map.Repos[1].Name);
        Assert.Equal("git@x/y.git", map.Repos[1].Repo);
    }

    [Fact]
    public void A_git_url_repo_needs_no_registration()
    {
        var (store, registry, maps) = Fresh();
        using var _ = store;
        var map = new MapDefinition
        {
            Name = "portable",
            Repos = [new MapRepo { Name = "remote-only", Repo = "git@github.com:me/remote.git" }],
        };
        maps.Save(map);   // does not throw despite no registration
        Assert.Single(maps.List());
    }

    [Fact]
    public void Unregistered_repo_without_a_url_is_rejected()
    {
        var (store, _, maps) = Fresh();
        using var _ = store;
        var map = new MapDefinition { Name = "m", Repos = [MapRepo.Local("ghost")] };
        var ex = Assert.Throws<MapException>(() => maps.Save(map));
        Assert.Contains("ghost", ex.Message);
    }

    [Fact]
    public void Wiring_for_an_absent_repo_is_rejected()
    {
        var (store, registry, maps) = Fresh();
        using var _ = store;
        Register(store, registry, "acme");
        var map = new MapDefinition
        {
            Name = "m",
            Repos = [MapRepo.Local("acme")],
            Wiring = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["absent"] = new Dictionary<string, string> { ["cap"] = "prov" },
            },
        };
        Assert.Throws<MapException>(() => maps.Save(map));
    }

    [Fact]
    public void Empty_repos_and_bad_name_and_zero_slots_are_rejected()
    {
        var (store, registry, maps) = Fresh();
        using var _ = store;
        Register(store, registry, "acme");
        Assert.Throws<MapException>(() => maps.Save(new MapDefinition { Name = "m", Repos = [] }));
        Assert.Throws<MapException>(() => maps.Save(new MapDefinition { Name = "bad name!", Repos = [MapRepo.Local("acme")] }));
        Assert.Throws<MapException>(() => maps.Save(new MapDefinition { Name = "m", Repos = [MapRepo.Local("acme")], MaxSlots = 0 }));
    }

    [Fact]
    public void Multiple_maps_over_the_same_repos_coexist()
    {
        var (store, registry, maps) = Fresh();
        using var _ = store;
        Register(store, registry, "acme");
        maps.Save(new MapDefinition { Name = "style-a", Repos = [MapRepo.Local("acme")] });
        maps.Save(new MapDefinition { Name = "style-b", Repos = [MapRepo.Local("acme")] });
        Assert.Equal(["style-a", "style-b"], maps.List().Select(m => m.Name));
    }

    [Fact]
    public void Export_then_import_round_trips_through_a_file()
    {
        var (store, registry, maps) = Fresh();
        using var _ = store;
        Register(store, registry, "acme");
        maps.Save(new MapDefinition { Name = "src", Repos = [MapRepo.Local("acme")], MaxSlots = 2 });

        var outFile = Path.Combine(store.Root, "exported.json");
        maps.Export("src", outFile);
        maps.Remove("src");
        Assert.Null(maps.Get("src"));

        var imported = maps.Import(outFile);
        Assert.Equal("src", imported.Name);
        Assert.Equal(2, maps.Get("src")!.MaxSlots);
    }
}
