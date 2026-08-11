using Sprig.Core.Settings;
using Sprig.Core.Stacks;
using Sprig.Core.Store;

namespace Sprig.Tests.Stacks;

public class StackStoreTests
{
    static (StackStore stacks, RepoRegistryStore registry, InstanceStore instances) Build(TempStore s)
    {
        var registry = new RepoRegistryStore(s.Paths);
        foreach (var name in new[] { "vue", "api" })
        {
            var dir = Path.Combine(s.Root, name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, ".sprig.json"), $$"""{ "schema":2, "name":"{{name}}" }""");
            registry.Add(dir);
        }
        var instances = new InstanceStore(s.Paths);
        return (new StackStore(s.Paths, registry, instances), registry, instances);
    }

    static StackDefinition Stack() => new()
    {
        Name = "web+api",
        Repos = ["vue", "api"],
        Ports = ["api_port"],
        Bindings = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["vue"] = new Dictionary<string, string> { ["apiUrl"] = "http://localhost:${sprig.ports.api_port}" },
        },
    };

    [Fact]
    public void Save_get_list_remove()
    {
        using var s = new TempStore();
        var (stacks, _, _) = Build(s);

        stacks.Save(Stack());

        var got = stacks.Get("web+api");
        Assert.NotNull(got);
        Assert.Equal(["vue", "api"], got!.Repos);
        Assert.Equal(["api_port"], got.Ports);
        Assert.Equal("http://localhost:${sprig.ports.api_port}", got.Bindings["vue"]["apiUrl"]);
        Assert.Single(stacks.List());

        stacks.Remove("web+api");
        Assert.Null(stacks.Get("web+api"));
        Assert.Empty(stacks.List());
    }

    [Fact]
    public void Default_maxSlots_is_applied_and_roundtrips()
    {
        using var s = new TempStore();
        var (stacks, _, _) = Build(s);

        stacks.Save(Stack());

        Assert.Equal(StackDefinition.DefaultMaxSlots, stacks.Get("web+api")!.MaxSlots);
    }

    [Fact]
    public void Save_rejects_stack_setup_for_a_repo_not_in_the_stack()
    {
        using var s = new TempStore();
        var (stacks, _, _) = Build(s);
        var stack = Stack() with
        {
            Setup = new Dictionary<string, IReadOnlyList<string>> { ["ghost"] = ["npm ci"] },
        };

        var ex = Assert.Throws<StackException>(() => stacks.Save(stack));
        Assert.Contains("ghost", ex.Message);
    }

    [Fact]
    public void Stack_setup_roundtrips()
    {
        using var s = new TempStore();
        var (stacks, _, _) = Build(s);
        stacks.Save(Stack() with
        {
            Setup = new Dictionary<string, IReadOnlyList<string>> { ["vue"] = ["npm ci", "npm run build"] },
        });

        Assert.Equal(["npm ci", "npm run build"], stacks.Get("web+api")!.Setup["vue"]);
    }

    [Fact]
    public void Save_rejects_a_pool_that_cannot_fit_the_port_range()
    {
        using var s = new TempStore();
        var (_, registry, instances) = Build(s);
        var settings = new FileSettingsStore(s.Paths);
        settings.Save(new SprigSettings { PortRangeStart = 8000, PortRangeEndExclusive = 8002 }); // capacity 2
        var stacks = new StackStore(s.Paths, registry, instances, settings);

        // 1 port × maxSlots 3 = 3 needed > 2 available.
        var ex = Assert.Throws<StackException>(() => stacks.Save(Stack() with { MaxSlots = 3 }));
        Assert.Contains("can't fit", ex.Message);
    }

    [Fact]
    public void Save_accepts_a_pool_that_fits_the_port_range()
    {
        using var s = new TempStore();
        var (_, registry, instances) = Build(s);
        var settings = new FileSettingsStore(s.Paths);
        settings.Save(new SprigSettings { PortRangeStart = 8000, PortRangeEndExclusive = 8010 }); // capacity 10
        var stacks = new StackStore(s.Paths, registry, instances, settings);

        stacks.Save(Stack() with { MaxSlots = 5 }); // 1 × 5 = 5 ≤ 10

        Assert.Equal(5, stacks.Get("web+api")!.MaxSlots);
    }

    [Fact]
    public void Save_rejects_unknown_repo()
    {
        using var s = new TempStore();
        var (stacks, _, _) = Build(s);
        var bad = Stack() with { Repos = ["vue", "ghost"] };
        var ex = Assert.Throws<StackException>(() => stacks.Save(bad));
        Assert.Contains("ghost", ex.Message);
    }

    [Fact]
    public void Save_rejects_empty_repos_and_bad_name()
    {
        using var s = new TempStore();
        var (stacks, _, _) = Build(s);
        Assert.Throws<StackException>(() => stacks.Save(Stack() with { Repos = [] }));
        Assert.Throws<StackException>(() => stacks.Save(Stack() with { Name = "bad/name" }));
    }

    [Fact]
    public void Save_rejects_modifying_a_stack_that_workspaces_use()
    {
        using var s = new TempStore();
        var (stacks, _, instances) = Build(s);
        stacks.Save(Stack());

        // A workspace built from the stack freezes it.
        instances.Save(new InstanceRecord { Workspace = "ws1", Stack = "web+api" });

        var ex = Assert.Throws<StackException>(() => stacks.Save(Stack() with { Ports = ["api_port", "extra"] }));
        Assert.Contains("web+api", ex.Message);
        Assert.Contains("1 workspace", ex.Message);

        // The stack on disk is untouched, and a brand-new stack is unaffected by the guard.
        Assert.Equal(["api_port"], stacks.Get("web+api")!.Ports);
        stacks.Save(Stack() with { Name = "other" });
        Assert.NotNull(stacks.Get("other"));
    }

    [Fact]
    public void Export_then_import_round_trips()
    {
        using var s = new TempStore();
        var (stacks, _, _) = Build(s);
        stacks.Save(Stack());

        var exportPath = Path.Combine(s.Root, "exported-stack.json");
        stacks.Export("web+api", exportPath);
        Assert.True(File.Exists(exportPath));

        stacks.Remove("web+api");
        Assert.Null(stacks.Get("web+api"));

        var imported = stacks.Import(exportPath);
        Assert.Equal("web+api", imported.Name);
        Assert.Equal(["vue", "api"], stacks.Get("web+api")!.Repos);
    }

    [Fact]
    public void Import_validates_against_registry()
    {
        using var s = new TempStore();
        var (stacks, _, _) = Build(s);
        var path = Path.Combine(s.Root, "foreign.json");
        File.WriteAllText(path, """{ "schema":1, "name":"foreign", "repos":["not-registered"] }""");
        Assert.Throws<StackException>(() => stacks.Import(path));
    }

    // A stack whose api_port is shared by both repos, kept consistent with its bindings.
    static StackDefinition SharedStack() => new()
    {
        Name = "web+api",
        Repos = ["vue", "api"],
        Ports = ["api_port"],
        Bindings = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["vue"] = new Dictionary<string, string> { ["apiUrl"] = "http://localhost:${sprig.ports.api_port}" },
            ["api"] = new Dictionary<string, string> { ["port"] = "${sprig.ports.api_port}" },
        },
        Shares =
        [
            new SharedPort
            {
                Port = "api_port",
                Consumers = [new PortConsumer { Repo = "vue", Input = "apiUrl" }, new PortConsumer { Repo = "api", Input = "port" }],
            },
        ],
    };

    [Fact]
    public void Explicit_shares_round_trip_through_save_and_get()
    {
        using var s = new TempStore();
        var (stacks, _, _) = Build(s);

        stacks.Save(SharedStack());

        var got = stacks.Get("web+api");
        Assert.NotNull(got);
        Assert.Equal(2, got!.Schema);
        var share = Assert.Single(got.Shares);
        Assert.Equal("api_port", share.Port);
        Assert.Equal(2, share.Consumers.Count);
    }

    [Fact]
    public void Save_rejects_a_share_of_an_undeclared_port()
    {
        using var s = new TempStore();
        var (stacks, _, _) = Build(s);
        var bad = SharedStack() with { Ports = [] };  // api_port no longer declared
        var ex = Assert.Throws<StackException>(() => stacks.Save(bad));
        Assert.Contains("api_port", ex.Message);
    }

    [Fact]
    public void Save_rejects_a_share_whose_consumer_binding_doesnt_reference_the_port()
    {
        using var s = new TempStore();
        var (stacks, _, _) = Build(s);
        var bad = SharedStack() with
        {
            Bindings = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["vue"] = new Dictionary<string, string> { ["apiUrl"] = "http://localhost:4000" }, // literal, no port ref
                ["api"] = new Dictionary<string, string> { ["port"] = "${sprig.ports.api_port}" },
            },
        };
        var ex = Assert.Throws<StackException>(() => stacks.Save(bad));
        Assert.Contains("apiUrl", ex.Message);
    }

    [Fact]
    public void Importing_a_schema_one_file_upgrades_it_to_explicit_shares()
    {
        using var s = new TempStore();
        var (stacks, _, _) = Build(s);
        var path = Path.Combine(s.Root, "legacy.json");
        File.WriteAllText(path, """
        {
          "schema": 1,
          "name": "web+api",
          "repos": ["vue", "api"],
          "ports": ["api_port"],
          "bindings": {
            "vue": { "apiUrl": "http://localhost:${sprig.ports.api_port}" },
            "api": { "port": "${sprig.ports.api_port}" }
          }
        }
        """);

        var imported = stacks.Import(path);

        Assert.Equal(2, imported.Schema);
        Assert.Equal("api_port", Assert.Single(imported.Shares).Port);
        // and it was persisted as schema 2 with the shares filled in
        Assert.Equal(2, stacks.Get("web+api")!.Schema);
        Assert.Single(stacks.Get("web+api")!.Shares);
    }
}
