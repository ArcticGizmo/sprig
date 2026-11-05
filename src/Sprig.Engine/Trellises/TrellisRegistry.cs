using Sprig.Engine.Store;

namespace Sprig.Engine.Trellises;

/// <param name="Id">What sprig knows the trellis by: chosen when it's registered and never taken from its code, so two
/// trellises that call themselves the same name still have separate sprouts.</param>
public sealed record TrellisEntry(string Id, string Path, DateTimeOffset AddedAt);

/// <summary>Where each registered trellis file lives, under its ID. A trellis can be in any folder; registering it once
/// lets every command name it by ID instead of its path.</summary>
public sealed class TrellisRegistry(ISprigPaths paths)
{
    string File => System.IO.Path.Combine(paths.Root, "trellises.json");

    public IReadOnlyList<TrellisEntry> List() => Load().OrderBy(e => e.Id, StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>
    /// Register a trellis under <paramref name="id"/>. A file already registered keeps the ID it has, since its
    /// sprouts are keyed by it; an ID already taken by another file is refused rather than replacing that trellis.
    /// </summary>
    public TrellisEntry Add(string id, string path)
    {
        var full = LongPath.Of(path);
        var entries = Load();
        if (entries.FirstOrDefault(e => SamePath(e.Path, full)) is { } existing) return existing;
        if (CheckId(id) is { } bad) throw new ArgumentException(bad, nameof(id));
        if (entries.Any(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"a trellis is already registered as '{id}'", nameof(id));
        var entry = new TrellisEntry(id, full, DateTimeOffset.UtcNow);
        entries.Add(entry);
        Save(entries);
        return entry;
    }

    public bool Remove(string id)
    {
        var entries = Load();
        var kept = entries.Where(e => !e.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).ToList();
        if (kept.Count == entries.Count) return false;
        Save(kept);
        return true;
    }

    /// <summary>A registered trellis by ID, or the path itself when it names an existing file.</summary>
    public string? Resolve(string idOrPath)
    {
        var entry = Load().FirstOrDefault(e => e.Id.Equals(idOrPath, StringComparison.OrdinalIgnoreCase));
        if (entry is not null) return entry.Path;
        return System.IO.File.Exists(idOrPath) ? LongPath.Of(idOrPath) : null;
    }

    /// <summary>The ID of the trellis at <paramref name="path"/>: its registered one, or for a file that was never
    /// registered, the one it would be offered.</summary>
    public string IdFor(string path)
        => Load().FirstOrDefault(e => SamePath(e.Path, path))?.Id ?? IdFromFile(path);

    /// <summary><paramref name="wanted"/>, or the first <c>wanted-2</c>, <c>wanted-3</c>… that no other file has.</summary>
    public string UnusedId(string wanted, string? forPath = null)
        => Ids.Unused(Ids.Slug(wanted, "trellis"),
            Load().Where(e => forPath is null || !SamePath(e.Path, forPath)).Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase));

    /// <summary>The ID a trellis file suggests: its name without <c>.trellis.cs</c>. A trellis kept in sprig is always
    /// <c>trellis.cs</c>, so its folder (named by its ID) says it instead.</summary>
    public static string IdFromFile(string path)
    {
        var file = System.IO.Path.GetFileName(path);
        if (file.Equals(Definitions.StoredDefinitions.TrellisFileName, StringComparison.OrdinalIgnoreCase)
            && System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path)) is { Length: > 0 } folder)
            return Ids.Slug(folder, "trellis");
        foreach (var suffix in (string[])[".trellis.cs", ".cs"])
            if (file.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && file.Length > suffix.Length)
                return Ids.Slug(file[..^suffix.Length], "trellis");
        return Ids.Slug(System.IO.Path.GetFileNameWithoutExtension(file), "trellis");
    }

    public static string? CheckId(string id) => Ids.Check(id, "a trellis ID");

    // Before IDs, entries were written with a "name", which was already the key every sprout used.
    sealed record Stored(string? Id, string? Name, string Path, DateTimeOffset AddedAt);

    List<TrellisEntry> Load() => (JsonFile.Read<List<Stored>>(File) ?? [])
        .Where(s => (s.Id ?? s.Name) is { Length: > 0 })
        .Select(s => new TrellisEntry(s.Id ?? s.Name!, s.Path, s.AddedAt))
        .ToList();

    void Save(List<TrellisEntry> entries) => JsonFile.Write(File, entries);

    static bool SamePath(string a, string b) => string.Equals(
        System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
