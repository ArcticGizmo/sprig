using Sprig.Engine.Planning;
using Sprig.Engine.Settings;

namespace Sprig.Tests.Planning;

public sealed class PortAllocatorTests
{
    static readonly SprigSettings Settings = new() { PortRangeStart = 9000, PortRangeEndExclusive = 9003 };

    static PortAllocator With(IReadOnlyDictionary<int, PortHolder> held, params int[] listening)
        => new(Settings, listening.ToHashSet(), held: held);

    [Fact]
    public void A_port_pinned_alone_can_be_had_from_a_workspace_that_s_down_but_not_from_one_that_s_up()
    {
        var down = With(new Dictionary<int, PortHolder> { [5080] = new("storefront--1", Up: false) });
        Assert.True(down.TryAllocate("5080", out var port, out _));
        Assert.Equal(5080, port);
        Assert.True(With(new Dictionary<int, PortHolder> { [5080] = new("storefront--1", Up: false) }).TryKeep("5080", 5080));

        var up = With(new Dictionary<int, PortHolder> { [5080] = new("storefront--1", Up: true) });
        Assert.False(up.TryAllocate("5080", out _, out var error));
        Assert.Equal("5080 is held by storefront--1, which is up", error);
        Assert.False(up.TryKeep("5080", 5080));
    }

    [Fact]
    public void A_workspace_that_s_down_keeps_its_ports_from_a_range_or_any()
    {
        var held = new Dictionary<int, PortHolder> { [9000] = new("storefront--1", Up: false), [8100] = new("storefront--1", Up: false) };

        Assert.True(With(held).TryAllocate(null, out var any, out _));
        Assert.Equal(9001, any);
        Assert.True(With(held).TryAllocate("8100-8101", out var ranged, out _));
        Assert.Equal(8101, ranged);
        Assert.False(With(held).TryKeep("8100-8101", 8100));
    }

    [Fact]
    public void A_port_pinned_alone_that_something_listens_on_says_so()
    {
        Assert.False(With(new Dictionary<int, PortHolder>(), listening: 5080).TryAllocate("5080", out _, out var error));
        Assert.Equal("something on this machine is listening on 5080", error);
    }
}
