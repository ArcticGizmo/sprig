using Sprig.Engine.Store;

namespace Sprig.Engine.Trellises;

public sealed record TrellisEntry(string Name, string Path, DateTimeOffset AddedAt);

/// <summary>Where each registered trellis file lives. A trellis can be in any folder; registering it once lets every
/// command name it instead of its path.</summary>
public sealed class TrellisRegistry(ISprigPaths paths)
{
    string File => System.IO.Path.Combine(paths.Root, "trellises.json");

    public IReadOnlyList<TrellisEntry> List() => Load().OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Register (or re-point) a trellis. A second trellis with the same name replaces the first.</summary>
    public TrellisEntry Add(string name, string path)
    {
        var full = LongPath.Of(path);
        var entries = Load().Where(e => !e.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && !SamePath(e.Path, full)).ToList();
        var entry = new TrellisEntry(name, full, DateTimeOffset.UtcNow);
        entries.Add(entry);
        JsonFile.Write(File, entries);
        return entry;
    }

    public bool Remove(string name)
    {
        var entries = Load();
        var kept = entries.Where(e => !e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (kept.Count == entries.Count) return false;
        JsonFile.Write(File, kept);
        return true;
    }

    /// <summary>A registered trellis by name, or the path itself when it names an existing file.</summary>
    public string? Resolve(string nameOrPath)
    {
        var entry = Load().FirstOrDefault(e => e.Name.Equals(nameOrPath, StringComparison.OrdinalIgnoreCase));
        if (entry is not null) return entry.Path;
        return System.IO.File.Exists(nameOrPath) ? LongPath.Of(nameOrPath) : null;
    }

    List<TrellisEntry> Load() => JsonFile.Read<List<TrellisEntry>>(File) ?? [];

    static bool SamePath(string a, string b) => string.Equals(
        System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
