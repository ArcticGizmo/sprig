using Sprig.Core.Store;

namespace Sprig.Core.Settings;

/// <summary>Reads and writes <see cref="SprigSettings"/> from the central store.</summary>
public interface ISettingsStore
{
    /// <summary>Current settings, or defaults if none have been saved.</summary>
    SprigSettings Get();

    /// <summary>Validate and persist. Throws <see cref="ArgumentException"/> on an invalid range.</summary>
    void Save(SprigSettings settings);
}

/// <summary>File-backed <see cref="ISettingsStore"/> (one JSON file at <see cref="ISprigPaths.SettingsFile"/>).</summary>
public sealed class FileSettingsStore(ISprigPaths paths) : ISettingsStore
{
    public SprigSettings Get()
    {
        var loaded = JsonFile.Read<SprigSettings>(paths.SettingsFile) ?? new SprigSettings();
        // Normalise restricted ports so callers always see a clean, deduped, sorted list.
        loaded.RestrictedPorts = Normalise(loaded.RestrictedPorts);
        return loaded;
    }

    public void Save(SprigSettings settings)
    {
        Validate(settings);
        var toWrite = settings.Clone();
        toWrite.RestrictedPorts = Normalise(toWrite.RestrictedPorts);
        JsonFile.Write(paths.SettingsFile, toWrite);
    }

    /// <summary>Throws with a user-facing message if the settings can't be applied.</summary>
    public static void Validate(SprigSettings s)
    {
        if (s.PortRangeStart < SprigSettings.MinPort || s.PortRangeStart > SprigSettings.MaxPort)
            throw new ArgumentException($"start port must be between {SprigSettings.MinPort} and {SprigSettings.MaxPort}");
        if (s.PortRangeEndExclusive <= s.PortRangeStart)
            throw new ArgumentException("the end of the range must be greater than the start");
        if (s.PortRangeEndExclusive > SprigSettings.MaxPort + 1)
            throw new ArgumentException($"the range cannot extend past port {SprigSettings.MaxPort}");
        foreach (var p in s.RestrictedPorts)
            if (p < SprigSettings.MinPort || p > SprigSettings.MaxPort)
                throw new ArgumentException($"restricted port {p} is not a valid port number");
    }

    static List<int> Normalise(IEnumerable<int>? ports)
        => ports is null ? new List<int>() : ports.Distinct().OrderBy(p => p).ToList();
}
