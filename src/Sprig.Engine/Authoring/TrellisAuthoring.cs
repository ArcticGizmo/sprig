using System.Text.RegularExpressions;
using Sprig.Engine.Definitions;
using Sprig.Engine.Store;
using Sprig.Engine.Trellises;

namespace Sprig.Engine.Authoring;

/// <summary>
/// What the app does to trellises and repos on the user's behalf. What sprig writes, and what the user saves from its
/// editor, is approved as it's written, since the user is the author; anything else still asks.
/// </summary>
public sealed class TrellisAuthoring(VirtualDefinitions virtuals, TrellisRegistry registry, ITrustStore trust)
{
    /// <summary>
    /// A new trellis kept in sprig, over <paramref name="repoDirs"/>, naming each repo by its entry in the Repos list.
    /// A folder with its own sprig.cs is referenced; one without gets a definition written in sprig, drafted from what's
    /// in it. An entry already there for the folder is reused.
    /// </summary>
    public TrellisEntry CreateTrellis(string name, IReadOnlyList<string> repoDirs, string? worktreesFrom, string portPolicy)
    {
        if (repoDirs.Count == 0) throw new ArgumentException("choose at least one repo folder");
        foreach (var dir in repoDirs)
            if (!Directory.Exists(dir)) throw new ArgumentException($"there's no folder at {dir}");

        var takenIds = registry.List().Select(e => e.Id).Concat(virtuals.TrellisIds()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var id = Ids.Unused(Ids.Slug(name, "trellis"), takenIds);
        var repos = repoDirs.Select(dir =>
        {
            var full = LongPath.Of(dir);
            var entry = HasOwn(full)
                ? virtuals.RepoFor(full, inRepo: true) ?? ReferenceRepo(full)
                : virtuals.RepoFor(full, inRepo: false) ?? WriteRepo(full);
            return new ScaffoldRepo(null, Use: entry.Id);
        }).ToArray();

        var file = virtuals.CreateTrellis(id, Scaffold.Trellis(name, repos, worktreesFrom, portPolicy));
        Approve(file);
        return registry.Add(id, file);
    }

    /// <summary>
    /// A repo in the Repos list described by a definition written in sprig. It starts as a copy of the folder's own
    /// sprig.cs when there is one, so a change can be tried without committing it; otherwise it's drafted from the folder.
    /// </summary>
    public RepoEntry WriteRepo(string folder, string? id = null)
    {
        folder = LongPath.Of(folder);
        if (!Directory.Exists(folder)) throw new ArgumentException($"there's no folder at {folder}");
        var own = Path.Combine(folder, TrellisLoader.RepoDefinitionFile);
        string text;
        bool approve;
        if (File.Exists(own))
        {
            text = File.ReadAllText(own);
            // A copy of code the user hasn't approved is still code they haven't read.
            approve = trust.Check(own, DefinitionHash.Of(text)).State == TrustState.Trusted;
        }
        else
        {
            text = Scaffold.Repo(folder, Ids.Slug(FolderName(folder), "repo"));
            approve = true;
        }
        var entry = virtuals.WriteRepo(id ?? virtuals.UnusedRepoId(FolderName(folder)), folder, text);
        if (approve) Approve(entry.DefinitionFile);
        return entry;
    }

    /// <summary>A repo in the Repos list described by the folder's own sprig.cs.</summary>
    public RepoEntry ReferenceRepo(string folder, string? id = null)
    {
        folder = LongPath.Of(folder);
        if (!HasOwn(folder)) throw new ArgumentException($"{folder} has no {TrellisLoader.RepoDefinitionFile}; write one in sprig instead");
        return virtuals.ReferenceRepo(id ?? virtuals.UnusedRepoId(FolderName(folder)), folder);
    }

    /// <summary>Take a repo off the Repos list. One written in sprig is deleted; a repo's own sprig.cs is left alone.</summary>
    public void DeleteRepo(string id) => virtuals.DeleteRepo(id);

    /// <summary>Registered trellises that name the repo <paramref name="id"/> with <c>UseRepo</c>.</summary>
    public IReadOnlyList<string> UsedBy(string id)
    {
        var call = new Regex($@"\bUseRepo\(\s*""{Regex.Escape(id)}""\s*\)");
        return registry.List()
            .Where(e => File.Exists(e.Path) && call.IsMatch(File.ReadAllText(e.Path)))
            .Select(e => e.Id)
            .ToArray();
    }

    /// <summary>
    /// Describe one of the trellis's repos with a definition written in sprig, starting from its own sprig.cs when it has
    /// one, and point the trellis at it. <paramref name="use"/> is the entry a <c>UseRepo</c> names, when it's one of
    /// those. Returns the new definition's file.
    /// </summary>
    public string DescribeInSprig(string trellisFile, string repoPath, string repoDir, string? use = null)
    {
        var entry = WriteRepo(repoDir);
        var text = File.ReadAllText(trellisFile);
        EditTrellis(trellisFile, use is not null
            ? DefinitionEditor.SetUseRepo(text, use, entry.Id)
            : DefinitionEditor.SetRepoDefinition(text, repoPath, entry.Id));
        return entry.DefinitionFile;
    }

    /// <summary>Point the trellis's repo back at the folder's own sprig.cs. The definition in sprig is kept.</summary>
    public void UseOwnDefinition(string trellisFile, string repoPath, string? use = null, string? folder = null)
    {
        var text = File.ReadAllText(trellisFile);
        if (use is null)
        {
            EditTrellis(trellisFile, DefinitionEditor.SetRepoDefinition(text, repoPath, null));
            return;
        }
        var dir = folder ?? throw new ArgumentException("the repo's folder is needed to find its own sprig.cs");
        var own = virtuals.RepoFor(dir, inRepo: true) ?? ReferenceRepo(dir);
        EditTrellis(trellisFile, DefinitionEditor.SetUseRepo(text, use, own.Id));
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
        if (path is not null && virtuals.IsVirtual(path)) virtuals.DeleteTrellis(Path.GetFileName(Path.GetDirectoryName(path)!));
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
