using Sprig.Core.Ports;
using Sprig.Core.Settings;

namespace Sprig.Tests.Ports;

/// <summary>Covers per-port <see cref="PortRequest.Allowed"/> restrictions during allocation.</summary>
public class ConstrainedPortTests
{
    static IReadOnlySet<int> Set(params int[] ports) => new HashSet<int>(ports);

    [Fact]
    public void Constrained_port_is_drawn_from_the_allowed_set()
    {
        using var s = new TempStore();
        var store = new FilePortStore(s.Paths); // default 8000-8999

        var p = store.Acquire("ws1", [new PortRequest("web", Set(8100, 8101, 8102, 8103))]);

        Assert.Equal(8100, p["web"]); // lowest free port in the set
    }

    [Fact]
    public void Allowed_set_may_lie_outside_the_settings_range()
    {
        using var s = new TempStore();
        var store = new FilePortStore(s.Paths, 8000, 8010); // narrow range

        var p = store.Acquire("ws1", [new PortRequest("web", Set(9500, 9501))]);

        Assert.Equal(9500, p["web"]); // the pinned set wins over the range
    }

    [Fact]
    public void Restricted_ports_are_skipped_within_the_allowed_set()
    {
        using var s = new TempStore();
        var settings = new FileSettingsStore(s.Paths);
        settings.Save(new SprigSettings
        {
            PortRangeStart = 8000,
            PortRangeEndExclusive = 9000,
            RestrictedPorts = [8100],
        });
        var store = new FilePortStore(s.Paths, settings);

        var p = store.Acquire("ws1", [new PortRequest("web", Set(8100, 8101))]);

        Assert.Equal(8101, p["web"]); // 8100 is restricted even though it's in the set
    }

    [Fact]
    public void Allowed_set_caps_concurrency_with_a_clear_error()
    {
        using var s = new TempStore();
        var store = new FilePortStore(s.Paths);
        var allowed = Set(8100, 8101);

        store.Acquire("ws1", [new PortRequest("web", allowed)]);
        store.Acquire("ws2", [new PortRequest("web", allowed)]);

        var ex = Assert.Throws<PortAllocationException>(
            () => store.Acquire("ws3", [new PortRequest("web", allowed)]));
        Assert.Contains("allowed set", ex.Message);
    }

    [Fact]
    public void Constrained_ports_do_not_collide_across_workspaces()
    {
        using var s = new TempStore();
        var store = new FilePortStore(s.Paths);
        var allowed = Set(8100, 8101, 8102);

        var a = store.Acquire("ws1", [new PortRequest("web", allowed)]);
        var b = store.Acquire("ws2", [new PortRequest("web", allowed)]);

        Assert.NotEqual(a["web"], b["web"]);
    }

    [Fact]
    public void Reacquire_is_deterministic_for_a_constrained_port()
    {
        using var s = new TempStore();
        var store = new FilePortStore(s.Paths);
        var allowed = Set(8100, 8101);

        var first = store.Acquire("ws1", [new PortRequest("web", allowed)]);
        var again = store.Acquire("ws1", [new PortRequest("web", allowed)]);

        Assert.Equal(first["web"], again["web"]);
    }

    [Fact]
    public void Unconstrained_and_constrained_ports_mix_in_one_acquire()
    {
        using var s = new TempStore();
        var store = new FilePortStore(s.Paths);

        var p = store.Acquire("ws1",
            [new PortRequest("api"), new PortRequest("web", Set(8100, 8101))]);

        Assert.InRange(p["api"], 8000, 8999);
        Assert.Contains(p["web"], new[] { 8100, 8101 });
    }
}
