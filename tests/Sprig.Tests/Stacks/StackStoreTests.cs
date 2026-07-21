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
            File.WriteAllText(Path.Combine(dir, ".sprig.json"), $$"""{ "schema":1, "name":"{{name}}" }""");
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
}
