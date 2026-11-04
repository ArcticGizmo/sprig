using Sprig.Engine.Trellises;

namespace Sprig.Tests.Trellises;

public sealed class TrellisRegistryTests : IDisposable
{
    readonly TempStore _store = new();
    readonly TrellisRegistry _registry;

    public TrellisRegistryTests() => _registry = new TrellisRegistry(_store.Paths);

    public void Dispose() => _store.Dispose();

    string File(string name)
    {
        var path = Sprig.Engine.Store.LongPath.Of(Path.Combine(_store.Root, name));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, "// trellis");
        return path;
    }

    [Fact]
    public void An_id_taken_by_another_file_is_refused_rather_than_replacing_it()
    {
        var first = File("a/storefront.trellis.cs");
        var second = File("b/storefront.trellis.cs");
        _registry.Add("storefront", first);

        Assert.Throws<ArgumentException>(() => _registry.Add("storefront", second));
        Assert.Equal("storefront-2", _registry.UnusedId("storefront", second));
        _registry.Add(_registry.UnusedId("storefront", second), second);

        Assert.Equal(first, _registry.Resolve("storefront"));
        Assert.Equal(second, _registry.Resolve("storefront-2"));
        Assert.Equal("storefront-2", _registry.IdFor(second));
    }

    [Fact]
    public void A_file_already_registered_keeps_its_id_because_its_sprouts_are_keyed_by_it()
    {
        var path = File("storefront.trellis.cs");
        _registry.Add("storefront", path);

        var again = _registry.Add("something-else", path);

        Assert.Equal("storefront", again.Id);
        Assert.Single(_registry.List());
        Assert.Equal("storefront", _registry.UnusedId("storefront", path));
    }

    [Fact]
    public void Entries_written_before_ids_read_their_name_as_the_id()
    {
        var path = File("storefront.trellis.cs");
        System.IO.File.WriteAllText(Path.Combine(_store.Root, "trellises.json"),
            $$"""[{"name":"storefront","path":{{System.Text.Json.JsonSerializer.Serialize(path)}},"addedAt":"2026-11-03T00:00:00+00:00"}]""");

        var entry = Assert.Single(_registry.List());

        Assert.Equal("storefront", entry.Id);
        Assert.Equal(path, _registry.Resolve("storefront"));
    }

    [Fact]
    public void An_unregistered_file_is_offered_an_id_from_its_name_and_bad_ids_are_refused()
    {
        Assert.Equal("shop", _registry.IdFor(Path.Combine(_store.Root, "shop.trellis.cs")));
        Assert.NotNull(TrellisRegistry.CheckId("has space"));
        Assert.Throws<ArgumentException>(() => _registry.Add("-bad", File("x.trellis.cs")));
    }
}
