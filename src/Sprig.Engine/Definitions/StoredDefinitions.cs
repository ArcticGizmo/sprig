using Sprig.Engine.Store;

namespace Sprig.Engine.Definitions;

/// <summary>One repo in sprig's Repos list: a folder, and the definition that describes it. An ID is one or the other
/// for good, so what an orchestration using it runs never changes under it.</summary>
/// <param name="InRepo">Described by the folder's own sprig.cs; otherwise by a definition kept in sprig.</param>
/// <param name="DefinitionFile">The file that describes it.</param>
public sealed record RepoEntry(string Id, string Folder, bool InRepo, string DefinitionFile)
{
    public string OwnFile => Path.Combine(Folder, StoredDefinitions.RepoFileName);
}

/// <summary>
/// Orchestrations and the Repos list, kept in sprig's store rather than in a repo, so sprig can be tried on repos without
/// committing anything to them. Each lives in a folder named by its ID. A repo is only ever used through its entry,
/// so a copy kept in sprig never stands in for a repo's own sprig.cs unless the entry says so.
/// </summary>
public sealed class StoredDefinitions(ISprigPaths paths)
{
    public const string OrchestrationFileName = "orchestration.cs";
    public const string RepoFileName = "sprig.cs";
    const string EntryFileName = "repo.json";

    string ReposRoot => Path.Combine(paths.Root, "repos");
    string OrchestrationsRoot => Path.Combine(paths.Root, "orchestrations");

    /// <summary>Where sprig keeps the definition of a repo kept in sprig.</summary>
    public string CopyFile(string id) => LongPath.Of(Path.Combine(ReposRoot, id, RepoFileName));

    public string OrchestrationFile(string id) => LongPath.Of(Path.Combine(OrchestrationsRoot, id, OrchestrationFileName));

    /// <summary>The file that records a Repos entry. It also stands for the repo run on its own, as the orchestration path
    /// of its workspaces.</summary>
    public string EntryFile(string id) => EntryFile(paths, id);

    public static string EntryFile(ISprigPaths paths, string id) => LongPath.Of(Path.Combine(paths.Root, "repos", id, EntryFileName));

    /// <summary>The repo ID whose entry file <paramref name="path"/> is, or null for any other file. An entry since
    /// taken off the list still answers, so its workspaces can say the repo is gone rather than fail to read.</summary>
    public string? RepoIdOf(string path)
    {
        if (!Path.GetFileName(path).Equals(EntryFileName, StringComparison.OrdinalIgnoreCase)) return null;
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        return SamePath(Path.GetDirectoryName(folder)!, ReposRoot) ? Path.GetFileName(folder) : null;
    }

    sealed record StoredEntry(string Folder, bool InRepo);

    /// <summary>Every repo in the Repos list.</summary>
    public IReadOnlyList<RepoEntry> Repos()
        => Directory.Exists(ReposRoot)
            ? Directory.EnumerateDirectories(ReposRoot).Select(d => Repo(Path.GetFileName(d))).OfType<RepoEntry>()
                .OrderBy(e => e.Id, StringComparer.OrdinalIgnoreCase).ToArray()
            : [];

    public RepoEntry? Repo(string id)
    {
        if (Ids.Check(id, "") is not null) return null;
        if (JsonFile.Read<StoredEntry>(Path.Combine(ReposRoot, id, EntryFileName)) is not { Folder.Length: > 0 } stored) return null;
        var folder = LongPath.Of(stored.Folder);
        return new RepoEntry(id, folder, stored.InRepo, stored.InRepo ? Path.Combine(folder, RepoFileName) : CopyFile(id));
    }

    /// <summary>The entry a new orchestration over <paramref name="folder"/> reuses: the one using its own sprig.cs if there
    /// is one, else the first kept in sprig.</summary>
    public RepoEntry? RepoFor(string folder)
    {
        var matching = Repos().Where(e => SamePath(e.Folder, folder)).ToArray();
        return matching.FirstOrDefault(e => e.InRepo) ?? matching.FirstOrDefault();
    }

    /// <summary>The entry that uses <paramref name="folder"/>'s own sprig.cs. There's at most one, since two would
    /// describe the folder identically.</summary>
    public RepoEntry? InRepoFor(string folder) => Repos().FirstOrDefault(e => e.InRepo && SamePath(e.Folder, folder));

    /// <summary>A new entry. <paramref name="definition"/> is the text kept in sprig, needed when it isn't in the
    /// repo.</summary>
    public RepoEntry AddRepo(string id, string folder, bool inRepo, string? definition)
    {
        if (Ids.Check(id, "a repo ID") is { } bad) throw new ArgumentException(bad, nameof(id));
        var dir = Path.Combine(ReposRoot, id);
        if (Directory.Exists(dir) || OrchestrationIdsTaken().Contains(id)) throw new ArgumentException($"'{id}' is already taken", nameof(id));
        if (!inRepo && definition is null) throw new ArgumentException("a repo kept in sprig needs its definition", nameof(definition));
        Directory.CreateDirectory(dir);
        if (!inRepo) File.WriteAllText(CopyFile(id), definition);
        JsonFile.Write(Path.Combine(dir, EntryFileName), new StoredEntry(LongPath.Of(folder), inRepo));
        return Repo(id)!;
    }

    static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public IReadOnlyList<string> OrchestrationIds() => Directory.Exists(OrchestrationsRoot)
        ? Directory.EnumerateDirectories(OrchestrationsRoot)
            .Where(d => File.Exists(Path.Combine(d, OrchestrationFileName)))
            .Select(d => Path.GetFileName(d))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray()
        : [];

    /// <summary>Whether <paramref name="file"/> is one sprig keeps, rather than one in a repo or elsewhere.</summary>
    public bool IsKept(string file)
    {
        var full = Path.GetFullPath(file);
        return IsUnder(full, ReposRoot) || IsUnder(full, OrchestrationsRoot);
    }

    /// <summary>An unused repo ID: <paramref name="wanted"/>, or with -2, -3… after it. A repo can run on its own,
    /// keyed by its ID like an orchestration, so IDs are unique across both.</summary>
    public string UnusedRepoId(string wanted)
        => Ids.Unused(Ids.Slug(wanted, "repo"), RepoIdsTaken().Concat(OrchestrationIdsTaken()).ToHashSet(StringComparer.OrdinalIgnoreCase));

    /// <summary>The folder names under the Repos list, including any without a readable entry.</summary>
    public IReadOnlySet<string> RepoIdsTaken() => Directory.Exists(ReposRoot)
        ? Directory.EnumerateDirectories(ReposRoot).Select(d => Path.GetFileName(d)).ToHashSet(StringComparer.OrdinalIgnoreCase)
        : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    HashSet<string> OrchestrationIdsTaken()
        => new Orchestrations.OrchestrationRegistry(paths).List().Select(e => e.Id).Concat(OrchestrationIds()).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public string CreateOrchestration(string id, string text)
    {
        if (Ids.Check(id, "an orchestration ID") is { } bad) throw new ArgumentException(bad, nameof(id));
        var file = OrchestrationFile(id);
        if (File.Exists(file)) throw new ArgumentException($"'{id}' is already taken", nameof(id));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
        return file;
    }

    public void DeleteRepo(string id) => DeleteFolder(Path.Combine(ReposRoot, id));

    public void DeleteOrchestration(string id) => DeleteFolder(Path.GetDirectoryName(OrchestrationFile(id))!);

    static void DeleteFolder(string folder)
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

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
