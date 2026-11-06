using Sprig.Engine.Store;

namespace Sprig.Engine.Orchestrations;

/// <param name="Id">What sprig knows the orchestration by: chosen when it's registered and never taken from its code, so two
/// orchestrations that call themselves the same name still have separate workspaces.</param>
public sealed record OrchestrationEntry(string Id, string Path, DateTimeOffset AddedAt);

/// <summary>Where each registered orchestration file lives, under its ID. An orchestration can be in any folder; registering it once
/// lets every command name it by ID instead of its path.</summary>
public sealed class OrchestrationRegistry(ISprigPaths paths)
{
    string File => System.IO.Path.Combine(paths.Root, "orchestrations.json");

    public IReadOnlyList<OrchestrationEntry> List() => Load().OrderBy(e => e.Id, StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>
    /// Register an orchestration under <paramref name="id"/>. A file already registered keeps the ID it has, since its
    /// workspaces are keyed by it; an ID already taken by another file is refused rather than replacing that orchestration.
    /// </summary>
    public OrchestrationEntry Add(string id, string path)
    {
        var full = LongPath.Of(path);
        var entries = Load();
        if (entries.FirstOrDefault(e => SamePath(e.Path, full)) is { } existing) return existing;
        if (CheckId(id) is { } bad) throw new ArgumentException(bad, nameof(id));
        if (entries.Any(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"an orchestration is already registered as '{id}'", nameof(id));
        if (RepoIds().Contains(id))
            throw new ArgumentException($"'{id}' is a repo on the Repos list; an orchestration needs an ID of its own", nameof(id));
        var entry = new OrchestrationEntry(id, full, DateTimeOffset.UtcNow);
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

    /// <summary>A registered orchestration by ID; a repo on the Repos list, run on its own, by ID; or the path itself when it
    /// names an existing file.</summary>
    public string? Resolve(string idOrPath)
    {
        var entry = Load().FirstOrDefault(e => e.Id.Equals(idOrPath, StringComparison.OrdinalIgnoreCase));
        if (entry is not null) return entry.Path;
        if (CheckId(idOrPath) is null && Definitions.StoredDefinitions.EntryFile(paths, idOrPath) is var repo && System.IO.File.Exists(repo))
            return repo;
        return System.IO.File.Exists(idOrPath) ? LongPath.Of(idOrPath) : null;
    }

    HashSet<string> RepoIds() => new Definitions.StoredDefinitions(paths).RepoIdsTaken().ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The ID of the orchestration at <paramref name="path"/>: its registered one, or for a file that was never
    /// registered, the one it would be offered.</summary>
    public string IdFor(string path)
        => Load().FirstOrDefault(e => SamePath(e.Path, path))?.Id ?? IdFromFile(path);

    /// <summary><paramref name="wanted"/>, or the first <c>wanted-2</c>, <c>wanted-3</c>… that no other file has.</summary>
    public string UnusedId(string wanted, string? forPath = null)
        => Ids.Unused(Ids.Slug(wanted, "orchestration"),
            Load().Where(e => forPath is null || !SamePath(e.Path, forPath)).Select(e => e.Id).Concat(RepoIds()).ToHashSet(StringComparer.OrdinalIgnoreCase));

    /// <summary>The ID an orchestration file suggests: its name without <c>.orchestration.cs</c>. An orchestration kept in sprig is always
    /// <c>orchestration.cs</c>, so its folder (named by its ID) says it instead.</summary>
    public static string IdFromFile(string path)
    {
        var file = System.IO.Path.GetFileName(path);
        if (file.Equals(Definitions.StoredDefinitions.OrchestrationFileName, StringComparison.OrdinalIgnoreCase)
            && System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path)) is { Length: > 0 } folder)
            return Ids.Slug(folder, "orchestration");
        foreach (var suffix in (string[])[".orchestration.cs", ".cs"])
            if (file.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && file.Length > suffix.Length)
                return Ids.Slug(file[..^suffix.Length], "orchestration");
        return Ids.Slug(System.IO.Path.GetFileNameWithoutExtension(file), "orchestration");
    }

    public static string? CheckId(string id) => Ids.Check(id, "an orchestration ID");

    // Before IDs, entries were written with a "name", which was already the key every workspace used.
    sealed record Stored(string? Id, string? Name, string Path, DateTimeOffset AddedAt);

    List<OrchestrationEntry> Load() => (JsonFile.Read<List<Stored>>(File) ?? [])
        .Where(s => (s.Id ?? s.Name) is { Length: > 0 })
        .Select(s => new OrchestrationEntry(s.Id ?? s.Name!, s.Path, s.AddedAt))
        .ToList();

    void Save(List<OrchestrationEntry> entries) => JsonFile.Write(File, entries);

    static bool SamePath(string a, string b) => string.Equals(
        System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
