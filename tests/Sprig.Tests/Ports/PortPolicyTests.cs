using Sprig.Core.Ports;
using Sprig.Core.Settings;

namespace Sprig.Tests.Ports;

/// <summary>Covers the settings-driven policy: default range, restricted ports, and status queries.</summary>
public class PortPolicyTests
{
    [Fact]
    public void Default_range_starts_at_8000()
    {
        using var s = new TempStore();
        var store = new FilePortStore(s.Paths);

        var p = store.Acquire("ws1", ["x"]);

        Assert.InRange(p["x"], 8000, 8999);
    }

    [Fact]
    public void Restricted_ports_are_never_allocated()
    {
        using var s = new TempStore();
        var settings = new FileSettingsStore(s.Paths);
        settings.Save(new SprigSettings
        {
            PortRangeStart = 8000,
            PortRangeEndExclusive = 8003,   // 8000, 8001, 8002
            RestrictedPorts = [8000, 8002],
        });
        var store = new FilePortStore(s.Paths, settings);

        var p = store.Acquire("ws1", ["only"]);

        Assert.Equal(8001, p["only"]); // the one non-restricted port in range
        Assert.Throws<PortAllocationException>(() => store.Acquire("ws2", ["b"]));
    }

    [Fact]
    public void Describe_classifies_each_status()
    {
        using var s = new TempStore();
        var settings = new FileSettingsStore(s.Paths);
        settings.Save(new SprigSettings
        {
            PortRangeStart = 8000,
            PortRangeEndExclusive = 8010,
            RestrictedPorts = [8005],
        });
        var store = new FilePortStore(s.Paths, settings);
        store.Acquire("feature-x", ["api"]); // takes 8000

        Assert.Equal(PortStatus.InUse, store.Describe(8000).Status);
        Assert.Equal("feature-x / api", store.Describe(8000).HeldBy);
        Assert.Equal(PortStatus.Restricted, store.Describe(8005).Status);
        Assert.Equal(PortStatus.Available, store.Describe(8001).Status);
        Assert.Equal(PortStatus.OutOfRange, store.Describe(9000).Status);
        Assert.Equal(PortStatus.OutOfRange, store.Describe(80).Status);
    }

    [Fact]
    public void ListLeases_returns_all_leases_sorted_by_port()
    {
        using var s = new TempStore();
        var store = new FilePortStore(s.Paths);
        store.Acquire("ws1", ["api", "db"]);
        store.Acquire("ws2", ["web"]);

        var leases = store.ListLeases();

        Assert.Equal(3, leases.Count);
        Assert.True(leases[0].Port <= leases[1].Port && leases[1].Port <= leases[2].Port);
        Assert.Contains(leases, l => l is { Workspace: "ws1", Name: "api" });
    }

    [Fact]
    public void Policy_changes_are_picked_up_live()
    {
        using var s = new TempStore();
        var settings = new FileSettingsStore(s.Paths);
        settings.Save(new SprigSettings { PortRangeStart = 8000, PortRangeEndExclusive = 8001 }); // only 8000
        var store = new FilePortStore(s.Paths, settings);

        Assert.Equal(8000, store.Acquire("ws1", ["a"])["a"]);

        // Widen/move the range; a fresh acquire must honour the new policy without a new store.
        settings.Save(new SprigSettings { PortRangeStart = 9000, PortRangeEndExclusive = 9100 });

        Assert.Equal(9000, store.Acquire("ws2", ["b"])["b"]);
    }
}
