using Sprig.Core.Settings;

namespace Sprig.Tests.Settings;

public class SettingsStoreTests
{
    [Fact]
    public void Get_returns_defaults_when_nothing_saved()
    {
        using var s = new TempStore();
        var settings = new FileSettingsStore(s.Paths).Get();

        Assert.Equal(8000, settings.PortRangeStart);
        Assert.Equal(9000, settings.PortRangeEndExclusive);
        Assert.Empty(settings.RestrictedPorts);
    }

    [Fact]
    public void Save_then_get_roundtrips()
    {
        using var s = new TempStore();
        var store = new FileSettingsStore(s.Paths);

        store.Save(new SprigSettings
        {
            PortRangeStart = 9000,
            PortRangeEndExclusive = 9500,
            RestrictedPorts = [9100, 9200],
        });

        var got = store.Get();
        Assert.Equal(9000, got.PortRangeStart);
        Assert.Equal(9500, got.PortRangeEndExclusive);
        Assert.Equal([9100, 9200], got.RestrictedPorts);
    }

    [Fact]
    public void Save_dedupes_and_sorts_restricted_ports()
    {
        using var s = new TempStore();
        var store = new FileSettingsStore(s.Paths);

        store.Save(new SprigSettings { RestrictedPorts = [8080, 8000, 8080, 8443] });

        Assert.Equal([8000, 8080, 8443], store.Get().RestrictedPorts);
    }

    [Fact]
    public void Save_rejects_end_not_greater_than_start()
    {
        using var s = new TempStore();
        var store = new FileSettingsStore(s.Paths);

        Assert.Throws<ArgumentException>(() =>
            store.Save(new SprigSettings { PortRangeStart = 8000, PortRangeEndExclusive = 8000 }));
    }

    [Fact]
    public void Save_rejects_out_of_bounds_restricted_port()
    {
        using var s = new TempStore();
        var store = new FileSettingsStore(s.Paths);

        Assert.Throws<ArgumentException>(() =>
            store.Save(new SprigSettings { RestrictedPorts = [70000] }));
    }
}
