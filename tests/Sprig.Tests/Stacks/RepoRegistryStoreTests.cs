using Sprig.Core.Stacks;

namespace Sprig.Tests.Stacks;

public class RepoRegistryStoreTests
{
    static string MakeRepo(string root, string folder, string name)
    {
        var dir = Path.Combine(root, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".sprig.json"), $$"""{ "schema":1, "name":"{{name}}" }""");
        return dir;
    }

    [Fact]
    public void Add_registers_using_config_name_then_lists_and_gets()
    {
        using var s = new TempStore();
        var repo = MakeRepo(s.Root, "vue", "sprig-example-vue");
        var store = new RepoRegistryStore(s.Paths);

        var added = store.Add(repo);

        Assert.Equal("sprig-example-vue", added.Name);
        Assert.Equal(repo, added.Path);
        Assert.Equal("sprig-example-vue", store.Get("sprig-example-vue")!.Name);
        Assert.Single(store.List());
    }

    [Fact]
    public void Add_honours_explicit_name()
    {
        using var s = new TempStore();
        var repo = MakeRepo(s.Root, "api", "dotnet-api");
        var added = new RepoRegistryStore(s.Paths).Add(repo, "api");
        Assert.Equal("api", added.Name);
    }

    [Fact]
    public void Add_is_idempotent_for_same_path()
    {
        using var s = new TempStore();
        var repo = MakeRepo(s.Root, "vue", "vue");
        var store = new RepoRegistryStore(s.Paths);
        store.Add(repo);
        store.Add(repo); // same path, same name — no throw
        Assert.Single(store.List());
    }

    [Fact]
    public void Add_rejects_name_collision_with_different_path()
    {
        using var s = new TempStore();
        var a = MakeRepo(s.Root, "a", "dupe");
        var b = MakeRepo(s.Root, "b", "dupe");
        var store = new RepoRegistryStore(s.Paths);
        store.Add(a);
        Assert.Throws<RepoRegistryException>(() => store.Add(b));
    }

    [Fact]
    public void Add_rejects_non_sprig_repo()
    {
        using var s = new TempStore();
        var dir = Path.Combine(s.Root, "plain");
        Directory.CreateDirectory(dir);
        Assert.Throws<RepoRegistryException>(() => new RepoRegistryStore(s.Paths).Add(dir));
    }

    [Fact]
    public void Remove_is_idempotent()
    {
        using var s = new TempStore();
        var repo = MakeRepo(s.Root, "vue", "vue");
        var store = new RepoRegistryStore(s.Paths);
        store.Add(repo);
        store.Remove("vue");
        Assert.Empty(store.List());
        store.Remove("vue"); // no throw
    }
}
