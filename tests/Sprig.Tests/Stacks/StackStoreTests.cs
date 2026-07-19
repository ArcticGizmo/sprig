using Sprig.Core.Stacks;

namespace Sprig.Tests.Stacks;

public class StackStoreTests
{
    static (StackStore stacks, RepoRegistryStore registry) Build(TempStore s)
    {
        var registry = new RepoRegistryStore(s.Paths);
        foreach (var name in new[] { "vue", "api" })
        {
            var dir = Path.Combine(s.Root, name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, ".sprig.json"), $$"""{ "schema":1, "name":"{{name}}" }""");
            registry.Add(dir);
        }
        return (new StackStore(s.Paths, registry), registry);
    }

    static StackDefinition Stack() => new()
    {
        Name = "web+api",
        Repos = ["vue", "api"],
        Vars = new Dictionary<string, string> { ["env"] = "dev" },
    };

    [Fact]
    public void Save_get_list_remove()
    {
        using var s = new TempStore();
        var (stacks, _) = Build(s);

        stacks.Save(Stack());

        var got = stacks.Get("web+api");
        Assert.NotNull(got);
        Assert.Equal(["vue", "api"], got!.Repos);
        Assert.Equal("dev", got.Vars["env"]);
        Assert.Single(stacks.List());

        stacks.Remove("web+api");
        Assert.Null(stacks.Get("web+api"));
        Assert.Empty(stacks.List());
    }

    [Fact]
    public void Save_rejects_unknown_repo()
    {
        using var s = new TempStore();
        var (stacks, _) = Build(s);
        var bad = Stack() with { Repos = ["vue", "ghost"] };
        var ex = Assert.Throws<StackException>(() => stacks.Save(bad));
        Assert.Contains("ghost", ex.Message);
    }

    [Fact]
    public void Save_rejects_empty_repos_and_bad_name()
    {
        using var s = new TempStore();
        var (stacks, _) = Build(s);
        Assert.Throws<StackException>(() => stacks.Save(Stack() with { Repos = [] }));
        Assert.Throws<StackException>(() => stacks.Save(Stack() with { Name = "bad/name" }));
    }

    [Fact]
    public void Export_then_import_round_trips()
    {
        using var s = new TempStore();
        var (stacks, _) = Build(s);
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
        var (stacks, _) = Build(s);
        var path = Path.Combine(s.Root, "foreign.json");
        File.WriteAllText(path, """{ "schema":1, "name":"foreign", "repos":["not-registered"] }""");
        Assert.Throws<StackException>(() => stacks.Import(path));
    }
}
