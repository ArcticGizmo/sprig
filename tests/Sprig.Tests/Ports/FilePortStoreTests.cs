using Sprig.Core.Ports;

namespace Sprig.Tests.Ports;

public class FilePortStoreTests
{
    [Fact]
    public void Acquire_returns_a_port_per_name_within_range()
    {
        using var s = new TempStore();
        var store = new FilePortStore(s.Paths, 20000, 20010);

        var ports = store.Acquire("ws1", ["api", "postgres"]);

        Assert.Equal(["api", "postgres"], ports.Keys.Order());
        Assert.All(ports.Values, p => Assert.InRange(p, 20000, 20009));
        Assert.NotEqual(ports["api"], ports["postgres"]);
    }

    [Fact]
    public void Reacquire_is_deterministic_for_same_workspace()
    {
        using var s = new TempStore();
        var store = new FilePortStore(s.Paths);

        var first = store.Acquire("ws1", ["api", "postgres"]);
        var again = store.Acquire("ws1", ["api", "postgres"]);

        Assert.Equal(first, again);
    }

    [Fact]
    public void Deterministic_across_new_store_instances_persisted_to_disk()
    {
        using var s = new TempStore();
        var first = new FilePortStore(s.Paths).Acquire("ws1", ["api"]);

        // brand-new store object over the same on-disk file
        var again = new FilePortStore(s.Paths).Acquire("ws1", ["api"]);

        Assert.Equal(first["api"], again["api"]);
    }

    [Fact]
    public void Different_workspaces_never_collide()
    {
        using var s = new TempStore();
        var store = new FilePortStore(s.Paths);

        var a = store.Acquire("ws1", ["api", "postgres"]);
        var b = store.Acquire("ws2", ["api", "postgres"]);

        Assert.Empty(a.Values.Intersect(b.Values));
    }

    [Fact]
    public void Release_frees_ports_for_reuse()
    {
        using var s = new TempStore();
        var store = new FilePortStore(s.Paths, 20000, 20002); // only 20000, 20001

        var a = store.Acquire("ws1", ["x", "y"]); // takes both
        store.Release("ws1");
        var b = store.Acquire("ws2", ["z"]);       // must succeed from the freed pool

        Assert.Contains(b["z"], a.Values);
    }

    [Fact]
    public void Adding_a_new_name_keeps_existing_assignments()
    {
        using var s = new TempStore();
        var store = new FilePortStore(s.Paths);

        var first = store.Acquire("ws1", ["api"]);
        var second = store.Acquire("ws1", ["api", "postgres"]);

        Assert.Equal(first["api"], second["api"]);
        Assert.NotEqual(second["api"], second["postgres"]);
    }

    [Fact]
    public void Exhausted_range_throws()
    {
        using var s = new TempStore();
        var store = new FilePortStore(s.Paths, 20000, 20002); // capacity 2

        store.Acquire("ws1", ["a", "b"]);
        Assert.Throws<PortAllocationException>(() => store.Acquire("ws2", ["c"]));
    }

    [Fact]
    public void Peek_returns_null_then_the_lease()
    {
        using var s = new TempStore();
        var store = new FilePortStore(s.Paths);

        Assert.Null(store.Peek("ws1"));
        var acquired = store.Acquire("ws1", ["api"]);
        Assert.Equal(acquired, store.Peek("ws1"));
    }

    [Fact]
    public void Concurrent_acquires_do_not_double_allocate()
    {
        using var s = new TempStore();
        var store = new FilePortStore(s.Paths, 20000, 21000);

        var results = new System.Collections.Concurrent.ConcurrentBag<int>();
        Parallel.For(0, 50, i =>
        {
            var p = store.Acquire($"ws{i}", ["only"]);
            results.Add(p["only"]);
        });

        Assert.Equal(50, results.Count);
        Assert.Equal(50, results.Distinct().Count()); // all unique — no collisions
    }
}
