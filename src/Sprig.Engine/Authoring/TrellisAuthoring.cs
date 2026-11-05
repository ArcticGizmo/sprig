using System.Text.RegularExpressions;
using Sprig.Engine.Definitions;
using Sprig.Engine.Store;
using Sprig.Engine.Trellises;

namespace Sprig.Engine.Authoring;

/// <summary>
/// What the app does to trellises and repos on the user's behalf. What sprig writes, and what the user saves from its
/// editor, is approved as it's written, since the user is the author; anything else still asks. A copy keeps the
/// approval of what it was copied from, so code the user hasn't read is never approved by being moved.
/// </summary>
public sealed class TrellisAuthoring(StoredDefinitions stored, TrellisRegistry registry, ITrustStore trust)
{
    /// <summary>
    /// A new trellis kept in sprig, over <paramref name="repoDirs"/>, naming each repo by its entry in the Repos list.
    /// An entry already there for the folder is reused; otherwise one is added (<see cref="AddRepo"/>).
    /// </summary>
    public TrellisEntry CreateTrellis(string name, IReadOnlyList<string> repoDirs, string? worktreesFrom, string portPolicy)
    {
        if (repoDirs.Count == 0) throw new ArgumentException("choose at least one repo folder");
        foreach (var dir in repoDirs)
            if (!Directory.Exists(dir)) throw new ArgumentException($"there's no folder at {dir}");

        var takenIds = registry.List().Select(e => e.Id).Concat(stored.TrellisIds()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var id = Ids.Unused(Ids.Slug(name, "trellis"), takenIds);
        var repos = repoDirs.Select(dir =>
        {
            var full = LongPath.Of(dir);
            return new ScaffoldRepo(null, Use: (stored.RepoFor(full) ?? AddRepo(full)).Id);
        }).ToArray();

        var file = stored.CreateTrellis(id, Scaffold.Trellis(name, repos, worktreesFrom, portPolicy));
        Approve(file);
        return registry.Add(id, file);
    }

    /// <summary>A repo on the Repos list. A folder with its own sprig.cs uses it; one without gets a definition kept
    /// in sprig, drafted from what's in the folder.</summary>
    public RepoEntry AddRepo(string folder, string? id = null)
    {
        folder = LongPath.Of(folder);
        if (!Directory.Exists(folder)) throw new ArgumentException($"there's no folder at {folder}");
        id ??= stored.UnusedRepoId(FolderName(folder));
        if (HasOwn(folder))
        {
            if (stored.InRepoFor(folder) is { } existing)
                throw new ArgumentException($"its sprig.cs is already on the list as '{existing.Id}'");
            return stored.AddRepo(id, folder, inRepo: true, definition: null);
        }
        var entry = stored.AddRepo(id, folder, inRepo: false, Draft(folder));
        Approve(entry.DefinitionFile);
        return entry;
    }

    /// <summary>
    /// A new entry, <paramref name="newId"/>, kept in sprig and starting as a copy of <paramref name="id"/>'s sprig.cs,
    /// so changes can be tried without touching the repo. <paramref name="id"/> and the trellises using it are left as
    /// they are.
    /// </summary>
    public RepoEntry CopyIntoSprig(string id, string newId)
    {
        var entry = Entry(id);
        if (!entry.InRepo) throw new ArgumentException($"'{id}' is already kept in sprig");
        if (!File.Exists(entry.OwnFile)) throw new IOException($"{entry.OwnFile} isn't there");
        var text = File.ReadAllText(entry.OwnFile);
        var copy = stored.AddRepo(newId, entry.Folder, inRepo: false, text);
        CarryApproval(entry.OwnFile, copy.DefinitionFile, text);
        return copy;
    }

    /// <summary>
    /// Write <paramref name="id"/>'s definition into its folder as sprig.cs, left uncommitted, and use it from a new
    /// entry, <paramref name="newId"/>. <paramref name="id"/> and the trellises using it are left as they are. A folder
    /// that already has a sprig.cs is never written over.
    /// </summary>
    public RepoEntry WriteToRepo(string id, string newId)
    {
        var entry = Entry(id);
        if (entry.InRepo) throw new ArgumentException($"'{id}' already uses the repo's sprig.cs");
        if (WriteToRepoBlocker(entry) is { } blocked) throw new IOException(blocked);
        var text = File.ReadAllText(entry.DefinitionFile);
        File.WriteAllText(entry.OwnFile, text);
        CarryApproval(entry.DefinitionFile, entry.OwnFile, text);
        return stored.InRepoFor(entry.Folder) ?? stored.AddRepo(newId, entry.Folder, inRepo: true, definition: null);
    }

    /// <summary>Why <see cref="WriteToRepo"/> can't run for <paramref name="entry"/>, or null when it can.</summary>
    public string? WriteToRepoBlocker(RepoEntry entry)
    {
        if (!Directory.Exists(entry.Folder)) return $"there's no folder at {entry.Folder}";
        if (!File.Exists(entry.OwnFile)) return null;
        return stored.InRepoFor(entry.Folder) is { } existing
            ? $"The folder already has its own sprig.cs, on the list as '{existing.Id}'. Sprig won't write over it."
            : "The folder already has its own sprig.cs. Sprig won't write over it; + Add the folder to use it.";
    }

    /// <summary>The ID offered for a new entry made from <paramref name="entry"/>: the folder's name for one in the
    /// repo, with "-sprig" after it for one kept in sprig.</summary>
    public string SuggestId(RepoEntry entry, bool inRepo)
        => stored.UnusedRepoId(inRepo ? FolderName(entry.Folder) : $"{FolderName(entry.Folder)}-sprig");

    /// <summary>Bring one of a trellis's <c>Repo("path")</c> repos in through the Repos list instead, reusing an entry
    /// for its folder, so it shows on the Repos page and other trellises can use it by ID.</summary>
    public RepoEntry UseFromRepos(string trellisFile, string repoPath)
    {
        var folder = LongPath.Of(Path.Combine(Path.GetDirectoryName(trellisFile)!, repoPath));
        var entry = stored.RepoFor(folder) ?? AddRepo(folder);
        EditTrellis(trellisFile, DefinitionEditor.RepoToUseRepo(File.ReadAllText(trellisFile), repoPath, entry.Id));
        return entry;
    }

    /// <summary>Take a repo off the Repos list, with the definition sprig keeps for it. The folder's own sprig.cs is
    /// left alone.</summary>
    public void DeleteRepo(string id) => stored.DeleteRepo(id);

    /// <summary>Registered trellises that name the repo <paramref name="id"/> with <c>UseRepo</c>.</summary>
    public IReadOnlyList<string> UsedBy(string id)
    {
        var call = new Regex($@"\bUseRepo\(\s*""{Regex.Escape(id)}""\s*\)");
        return registry.List()
            .Where(e => File.Exists(e.Path) && call.IsMatch(File.ReadAllText(e.Path)))
            .Select(e => e.Id)
            .ToArray();
    }

    /// <summary>Write what the user saved from the editor, approved as written.</summary>
    public void Save(string file, string text)
    {
        File.WriteAllText(file, text);
        trust.Approve(file, DefinitionHash.Of(text));
    }

    /// <summary>Forget a trellis; one kept in sprig is deleted with it. Repos it used stay for others.</summary>
    public void RemoveTrellis(string id)
    {
        var path = registry.Resolve(id);
        registry.Remove(id);
        if (path is not null && stored.IsKept(path)) stored.DeleteTrellis(Path.GetFileName(Path.GetDirectoryName(path)!));
    }

    RepoEntry Entry(string id) => stored.Repo(id) ?? throw new ArgumentException($"'{id}' isn't in the Repos list", nameof(id));

    static string Draft(string folder) => Scaffold.Repo(folder, Ids.Slug(FolderName(folder), "repo"));

    void CarryApproval(string from, string to, string text)
    {
        if (trust.Check(from, DefinitionHash.Of(text)).State == TrustState.Trusted) Approve(to);
    }

    static bool HasOwn(string folder) => File.Exists(Path.Combine(folder, TrellisLoader.RepoDefinitionFile));

    static string FolderName(string folder) => Path.GetFileName(folder.TrimEnd('/', '\\'));

    /// <summary>A one-line change sprig makes keeps the file approved only if it was before: the rest of it is still
    /// code the user may not have read.</summary>
    void EditTrellis(string trellisFile, EditResult edit)
    {
        if (!edit.Ok) throw new InvalidOperationException(edit.Error);
        var wasApproved = trust.Check(trellisFile, DefinitionHash.Of(File.ReadAllText(trellisFile))).State == TrustState.Trusted;
        File.WriteAllText(trellisFile, edit.Text);
        if (wasApproved) Approve(trellisFile);
    }

    void Approve(string file) => trust.Approve(file, DefinitionHash.Of(File.ReadAllText(file)));
}
