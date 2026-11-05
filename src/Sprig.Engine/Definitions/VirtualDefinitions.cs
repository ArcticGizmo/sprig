using Sprig.Engine.Store;

namespace Sprig.Engine.Definitions;

/// <summary>One repo in sprig's Repos list: a folder and the definition that describes it.</summary>
/// <param name="Folder">The repo's folder; null for a definition written before entries had one.</param>
/// <param name="InRepo">Described by the folder's own sprig.cs, rather than one written in sprig.</param>
/// <param name="DefinitionFile">The file that describes it.</param>
public sealed record RepoEntry(string Id, string? Folder, bool InRepo, string DefinitionFile);

/// <summary>
/// Trellises and repo definitions kept in sprig's store instead of a repo, so sprig can be tried on repos without
/// committing anything to them. Each lives in a folder named by its ID, so any number can exist, several for the same
/// repo included, and none stands in for a repo's own sprig.cs unless a trellis names it.
/// </summary>
public sealed class VirtualDefinitions(ISprigPaths paths)
{
    public const string TrellisFileName = "trellis.cs";
    public const string RepoFileName = "sprig.cs";

    string ReposRoot => Path.Combine(paths.Root, "repos");
    string TrellisesRoot => Path.Combine(paths.Root, "trellises");

    public string RepoFile(string id) => LongPath.Of(Path.Combine(ReposRoot, id, RepoFileName));

    const string EntryFileName = "repo.json";

    sealed record StoredEntry(string? Folder, bool InRepo);

    /// <summary>Every repo in the Repos list, written in sprig or referencing a repo's own sprig.cs.</summary>
    public IReadOnlyList<RepoEntry> Repos()
        => Directory.Exists(ReposRoot)
            ? Directory.EnumerateDirectories(ReposRoot).Select(d => Repo(Path.GetFileName(d))).OfType<RepoEntry>()
                .OrderBy(e => e.Id, StringComparer.OrdinalIgnoreCase).ToArray()
            : [];

    public RepoEntry? Repo(string id)
    {
        if (Ids.Check(id, "") is not null) return null;
        var dir = Path.Combine(ReposRoot, id);
        var stored = JsonFile.Read<StoredEntry>(Path.Combine(dir, EntryFileName));
        var folder = stored?.Folder is { } f ? LongPath.Of(f) : null;
        if (stored is { InRepo: true } && folder is not null)
            return new RepoEntry(id, folder, true, Path.Combine(folder, RepoFileName));
        return File.Exists(RepoFile(id)) ? new RepoEntry(id, folder, false, RepoFile(id)) : null;
    }

    /// <summary>The first entry for <paramref name="folder"/> of the given kind, so a second trellis over the same
    /// repo reuses it rather than making another.</summary>
    public RepoEntry? RepoFor(string folder, bool inRepo)
        => Repos().FirstOrDefault(e => e.InRepo == inRepo && e.Folder is { } f && SamePath(f, folder));

    /// <summary>A repo described by a definition written in sprig.</summary>
    public RepoEntry WriteRepo(string id, string folder, string text)
    {
        CreateRepo(id, text);
        JsonFile.Write(Path.Combine(ReposRoot, id, EntryFileName), new StoredEntry(LongPath.Of(folder), false));
        return Repo(id)!;
    }

    /// <summary>A repo described by its own sprig.cs, listed so it can be used by ID and opened in the app.</summary>
    public RepoEntry ReferenceRepo(string id, string folder)
    {
        if (Ids.Check(id, "a repo ID") is { } bad) throw new ArgumentException(bad, nameof(id));
        var dir = Path.Combine(ReposRoot, id);
        if (Directory.Exists(dir)) throw new ArgumentException($"'{id}' is already taken", nameof(id));
        if (!File.Exists(Path.Combine(folder, RepoFileName))) throw new ArgumentException($"{folder} has no {RepoFileName} to reference");
        Directory.CreateDirectory(dir);
        JsonFile.Write(Path.Combine(dir, EntryFileName), new StoredEntry(LongPath.Of(folder), true));
        return Repo(id)!;
    }

    static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public string TrellisFile(string id) => LongPath.Of(Path.Combine(TrellisesRoot, id, TrellisFileName));

    public bool RepoExists(string id) => Ids.Check(id, "") is null && File.Exists(RepoFile(id));

    /// <summary>Repos with a definition written in sprig (not ones that reference a repo's own).</summary>
    public IReadOnlyList<string> RepoIds() => IdsIn(ReposRoot, RepoFileName);

    public IReadOnlyList<string> TrellisIds() => IdsIn(TrellisesRoot, TrellisFileName);

    /// <summary>Whether <paramref name="file"/> is one sprig keeps, rather than one in a repo or elsewhere.</summary>
    public bool IsVirtual(string file)
    {
        var full = Path.GetFullPath(file);
        return IsUnder(full, ReposRoot) || IsUnder(full, TrellisesRoot);
    }

    /// <summary>Unused repo definition ID: <paramref name="wanted"/>, or with -2, -3… after it.</summary>
    public string UnusedRepoId(string wanted)
        => Ids.Unused(Ids.Slug(wanted, "repo"), Directory.Exists(ReposRoot)
            ? Directory.EnumerateDirectories(ReposRoot).Select(d => Path.GetFileName(d)).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : []);

    public string CreateRepo(string id, string text) => Create(id, RepoFile(id), "a repo definition ID", text);

    public string CreateTrellis(string id, string text) => Create(id, TrellisFile(id), "a trellis ID", text);

    public void DeleteRepo(string id) => DeleteFolder(Path.GetDirectoryName(RepoFile(id))!);

    public void DeleteTrellis(string id) => DeleteFolder(Path.GetDirectoryName(TrellisFile(id))!);

    static string Create(string id, string file, string what, string text)
    {
        if (Ids.Check(id, what) is { } bad) throw new ArgumentException(bad, nameof(id));
        if (File.Exists(file)) throw new ArgumentException($"'{id}' is already taken", nameof(id));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
        return file;
    }

    static void DeleteFolder(string folder)
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    static IReadOnlyList<string> IdsIn(string root, string fileName)
        => Directory.Exists(root)
            ? Directory.EnumerateDirectories(root)
                .Where(d => File.Exists(Path.Combine(d, fileName)))
                .Select(d => Path.GetFileName(d))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];

    static bool IsUnder(string file, string root)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), file);
        return !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative) && relative != ".";
    }
}

/// <summary>The <c>#:</c> line a new definition starts with, so it builds against the SDK this sprig reads.</summary>
public static class SdkReference
{
    public static string Line()
        => LocalProject() is { } project ? $"#:project {project}" : $"#:package Sprig.Sdk@{Sprig.Sdk.Version}";

    // A dev build isn't published as a package yet, so its definitions build the SDK from this checkout.
    static string? LocalProject()
    {
        if (!AppProfile.IsDev) return null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var project = Path.Combine(dir.FullName, "src", "Sprig.Sdk", "Sprig.Sdk.csproj");
            if (File.Exists(Path.Combine(dir.FullName, "sprig.slnx")) && File.Exists(project)) return project;
        }
        return null;
    }
}
