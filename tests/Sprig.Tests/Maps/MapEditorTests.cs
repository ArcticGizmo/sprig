using Sprig.Core.Config;
using Sprig.Core.Maps;
using Sprig.Core.Stacks;

namespace Sprig.Tests.Maps;

/// <summary>The fixes the Maps wiring view and <c>sprig map default/wire</c> apply.</summary>
public class MapEditorTests
{
    sealed class Fixture : IDisposable
    {
        public TempStore Store { get; } = new();
        public RepoRegistryStore Registry { get; }
        public MapStore Maps { get; }
        public MapEditor Editor { get; }

        public Fixture()
        {
            Registry = new RepoRegistryStore(Store.Paths);
            Maps = new MapStore(Store.Paths, Registry);
            Editor = new MapEditor(Maps, Registry);
        }

        public string Register(string name, string json)
        {
            var dir = Path.Combine(Store.Root, "repos", name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, ".sprig.json"), json);
            Registry.Add(dir);
            return dir;
        }

        public void Dispose() => Store.Dispose();
    }

    static string Empty(string name) => $$"""{ "schema": 1, "name": "{{name}}" }""";

    [Fact]
    public void Sets_replaces_and_removes_a_constant()
    {
        using var f = new Fixture();
        f.Register("billing", Empty("billing"));
        f.Maps.Save(new MapDefinition { Name = "m", Repos = [MapRepo.Local("billing")] });

        f.Editor.SetDefault("m", "billing", "payments", new Dictionary<string, string> { ["url"] = "https://a.test", ["key"] = "k" });
        f.Editor.SetDefault("m", "billing", "payments", new Dictionary<string, string> { ["url"] = "https://b.test" });
        Assert.Equal(["url"], f.Maps.Get("m")!.Defaults["billing"]["payments"].Keys);
        Assert.Equal("https://b.test", f.Maps.Get("m")!.Defaults["billing"]["payments"]["url"]);

        f.Editor.RemoveDefault("m", "billing", "payments");
        Assert.Empty(f.Maps.Get("m")!.Defaults);
        Assert.Throws<MapException>(() => f.Editor.RemoveDefault("m", "billing", "payments"));
    }

    [Theory]
    [InlineData("ghost", "payments", "url", "x")]        // repo not in the map
    [InlineData("billing", "pay.ments", "url", "x")]     // a dot would break ${sprig.cap.output}
    [InlineData("billing", "payments", "", "x")]
    [InlineData("billing", "payments", "url", " ")]
    public void A_constant_with_a_bad_repo_name_or_value_is_refused(string repo, string cap, string output, string value)
    {
        using var f = new Fixture();
        f.Register("billing", Empty("billing"));
        f.Maps.Save(new MapDefinition { Name = "m", Repos = [MapRepo.Local("billing")] });

        Assert.Throws<MapException>(() => f.Editor.SetDefault("m", repo, cap, new Dictionary<string, string> { [output] = value }));
        Assert.Empty(f.Maps.Get("m")!.Defaults);
    }

    [Fact]
    public void Sets_and_removes_a_wiring_entry_that_names_a_repo()
    {
        using var f = new Fixture();
        f.Register("shop", Empty("shop"));
        f.Register("billing", Empty("billing"));
        f.Maps.Save(new MapDefinition { Name = "m", Repos = [MapRepo.Local("shop"), MapRepo.Local("billing")] });

        f.Editor.SetWiring("m", "shop", "cache", "billing/cache");
        Assert.Equal("billing/cache", f.Maps.Get("m")!.Wiring["shop"]["cache"]);
        Assert.Throws<MapException>(() => f.Editor.SetWiring("m", "shop", "cache", "ghost/cache"));

        f.Editor.RemoveWiring("m", "shop", "cache");
        Assert.Empty(f.Maps.Get("m")!.Wiring);
    }

    [Fact]
    public void Converting_a_fixed_value_in_a_flat_config_adds_the_need_rewires_the_key_and_sets_the_constant()
    {
        using var f = new Fixture();
        var dir = f.Register("shop", """
            { "schema": 1, "name": "shop",
              "env": [ { "file": ".env", "set": { "MAIL_HOST": "localhost:1025", "OTHER": "x" } } ] }
            """);
        f.Maps.Save(new MapDefinition { Name = "m", Repos = [MapRepo.Local("shop")] });
        var fixedValue = MapInspector.Inspect(f.Maps.Get("m")!, [new("shop", dir, SprigConfigLoader.LoadFromFile(Path.Combine(dir, ".sprig.json")))])
            .FixedValues.Single();

        f.Editor.ConvertFixedValue("m", fixedValue, "mail", "address");

        var config = SprigConfigLoader.LoadFromFile(Path.Combine(dir, ".sprig.json"));
        Assert.Equal("mail", config.Needs.Single().Capability);
        Assert.Equal("${sprig.mail.address}", config.Env!.Single().Set["MAIL_HOST"]);
        Assert.Equal("x", config.Env!.Single().Set["OTHER"]);
        Assert.Equal("localhost:1025", f.Maps.Get("m")!.Constants["shop"]["mail"]["address"]);

        var after = new MapInspector(f.Registry, f.Maps).Inspect("m");
        Assert.Empty(after.FixedValues);
        Assert.Equal(NeedState.Constant, after.Needs.Single().State);
    }

    [Fact]
    public void Converting_a_fixed_value_in_a_module_edits_only_that_module()
    {
        using var f = new Fixture();
        var dir = f.Register("shop", """
            { "schema": 1, "name": "shop", "modules": [
                { "name": "api", "path": "services/api",
                  "env": [ { "file": ".env", "set": { "MAIL_HOST": "localhost:1025" } } ] },
                { "name": "web", "path": "apps/web",
                  "env": [ { "file": ".env", "set": { "MAIL_HOST": "localhost:1025" } } ] } ] }
            """);
        f.Maps.Save(new MapDefinition { Name = "m", Repos = [MapRepo.Local("shop")] });
        var fixedValue = new FixedValue("shop", "api", "services/api/.env", "MAIL_HOST", "localhost:1025", FixedKind.Address);

        f.Editor.ConvertFixedValue("m", fixedValue, "mail", "address");

        var config = SprigConfigLoader.LoadFromFile(Path.Combine(dir, ".sprig.json"));
        Assert.Equal("${sprig.mail.address}", config.Modules[0].Env.Single().Set["MAIL_HOST"]);
        Assert.Equal("mail", config.Modules[0].Needs.Single().Capability);
        Assert.Equal("localhost:1025", config.Modules[1].Env.Single().Set["MAIL_HOST"]);
        Assert.Empty(config.Modules[1].Needs);
    }

    [Fact]
    public void A_stale_fixed_value_is_refused_and_nothing_is_written()
    {
        using var f = new Fixture();
        var json = """
            { "schema": 1, "name": "shop",
              "env": [ { "file": ".env", "set": { "MAIL_HOST": "localhost:2525" } } ] }
            """;
        var dir = f.Register("shop", json);
        f.Maps.Save(new MapDefinition { Name = "m", Repos = [MapRepo.Local("shop")] });
        var stale = new FixedValue("shop", "app", ".env", "MAIL_HOST", "localhost:1025", FixedKind.Address);

        Assert.Throws<MapException>(() => f.Editor.ConvertFixedValue("m", stale, "mail", "address"));
        Assert.Equal(json, File.ReadAllText(Path.Combine(dir, ".sprig.json")));
        Assert.Empty(f.Maps.Get("m")!.Defaults);
    }

    static string Provides(string name, string cap) => $$"""
        { "schema": 1, "name": "{{name}}", "provides": [ { "capability": "{{cap}}", "ports": { "port": true } } ] }
        """;

    static string Needs(string name, string cap) => $$"""
        { "schema": 1, "name": "{{name}}", "needs": [ { "capability": "{{cap}}" } ] }
        """;

    [Fact]
    public void A_constant_overrides_the_provider_and_linking_switches_back_under_a_different_name()
    {
        using var f = new Fixture();
        f.Register("queue", Provides("queue", "rabbit"));
        f.Register("worker", Needs("worker", "mq"));
        f.Maps.Save(new MapDefinition { Name = "m", Repos = [MapRepo.Local("queue"), MapRepo.Local("worker")] });
        NeedState State() => new MapInspector(f.Registry, f.Maps).Inspect("m").Needs.Single().State;
        Assert.Equal(NeedState.Gap, State());

        f.Editor.Link("m", "worker", "mq", "queue", "rabbit");
        Assert.Equal(NeedState.Wired, State());
        Assert.Equal("queue/rabbit", f.Maps.Get("m")!.Wiring["worker"]["mq"]);

        f.Editor.SetConstant("m", "worker", "mq", new Dictionary<string, string> { ["port"] = "5672" });
        Assert.Equal(NeedState.Constant, State());

        f.Editor.Link("m", "worker", "mq", "queue", "rabbit");
        Assert.Equal(NeedState.Wired, State());
        Assert.Empty(f.Maps.Get("m")!.Constants);
    }

    [Fact]
    public void Removing_a_constant_hands_the_need_back_to_its_provider()
    {
        using var f = new Fixture();
        f.Register("queue", Provides("queue", "mq"));
        f.Register("worker", Needs("worker", "mq"));
        f.Maps.Save(new MapDefinition { Name = "m", Repos = [MapRepo.Local("queue"), MapRepo.Local("worker")] });
        f.Editor.SetConstant("m", "worker", "mq", new Dictionary<string, string> { ["port"] = "5672" });

        f.Editor.RemoveConstant("m", "worker", "mq");

        Assert.Equal(NeedState.Wired, new MapInspector(f.Registry, f.Maps).Inspect("m").Needs.Single().State);
    }

    [Fact]
    public void Adds_and_removes_repos_and_removing_one_drops_what_concerns_it()
    {
        using var f = new Fixture();
        f.Register("queue", Provides("queue", "mq"));
        f.Register("worker", Needs("worker", "mq"));
        f.Maps.Save(new MapDefinition { Name = "m" });

        f.Editor.AddRepo("m", "worker");
        f.Editor.AddRepo("m", "queue");
        Assert.Throws<MapException>(() => f.Editor.AddRepo("m", "queue"));
        f.Editor.Link("m", "worker", "mq", "queue", "mq");
        f.Editor.SetConstant("m", "queue", "other", new Dictionary<string, string> { ["x"] = "1" });

        f.Editor.RemoveRepo("m", "queue");

        var map = f.Maps.Get("m")!;
        Assert.Equal(["worker"], map.Repos.Select(r => r.Name));
        Assert.Empty(map.Wiring);
        Assert.Empty(map.Constants);
    }

    [Fact]
    public void WithRepos_keeps_urls_and_drops_only_what_concerns_departing_repos()
    {
        var map = new MapDefinition
        {
            Name = "m",
            Repos = [new MapRepo { Name = "a", Repo = "https://example.test/a.git" }, MapRepo.Local("b")],
            Wiring = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["a"] = new Dictionary<string, string> { ["x"] = "b/x", ["y"] = "y2" },
                ["b"] = new Dictionary<string, string> { ["z"] = "z2" },
            },
        };

        var next = map.WithRepos(["a", "c"]);

        Assert.Equal(["a", "c"], next.Repos.Select(r => r.Name));
        Assert.Equal("https://example.test/a.git", next.Repos[0].Repo);
        Assert.Equal(["a"], next.Wiring.Keys);
        Assert.Equal(["y"], next.Wiring["a"].Keys);
    }
}
