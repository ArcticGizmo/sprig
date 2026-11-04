using Sprig.Engine.Definitions;
using Sprig.Engine.Store;
using Sprig.Engine.Trellises;

namespace Sprig.Engine.Authoring;

/// <summary>
/// What the app does to trellises and definitions on the user's behalf. What sprig writes, and what the user saves
/// from its editor, is approved as it's written, since the user is the author; anything else still asks.
/// </summary>
public sealed class TrellisAuthoring(VirtualDefinitions virtuals, TrellisRegistry registry, ITrustStore trust)
{
    /// <summary>
    /// A new trellis kept in sprig, over <paramref name="repoDirs"/>. A repo with its own sprig.cs uses it; one without
    /// gets a definition of its own in sprig, drafted from what's in its folder.
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
            var definition = File.Exists(Path.Combine(full, TrellisLoader.RepoDefinitionFile)) ? null : NewRepoDefinition(full);
            return new ScaffoldRepo(full, definition);
        }).ToArray();

        var file = virtuals.CreateTrellis(id, Scaffold.Trellis(name, repos, worktreesFrom, portPolicy));
        Approve(file);
        return registry.Add(id, file);
    }

    /// <summary>
    /// Describe the repo at <paramref name="repoPath"/> (as the trellis names it) with a new definition kept in sprig,
    /// and point the trellis at it. It starts as a copy of the repo's own sprig.cs when there is one, so a change can
    /// be tried without committing it; otherwise it's drafted from the folder. Returns the new definition's file.
    /// </summary>
    public string DescribeInSprig(string trellisFile, string repoPath, string repoDir)
    {
        var own = Path.Combine(repoDir, TrellisLoader.RepoDefinitionFile);
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
            text = Scaffold.Repo(repoDir, Ids.Slug(Path.GetFileName(repoDir.TrimEnd('/', '\\')), "repo"));
            approve = true;
        }

        var id = virtuals.UnusedRepoId(Path.GetFileName(repoDir.TrimEnd('/', '\\')));
        var file = virtuals.CreateRepo(id, text);
        if (approve) Approve(file);
        EditTrellis(trellisFile, DefinitionEditor.SetRepoDefinition(File.ReadAllText(trellisFile), repoPath, id));
        return file;
    }

    /// <summary>Point the trellis's repo back at the repo's own sprig.cs. The definition in sprig is kept.</summary>
    public void UseOwnDefinition(string trellisFile, string repoPath)
        => EditTrellis(trellisFile, DefinitionEditor.SetRepoDefinition(File.ReadAllText(trellisFile), repoPath, null));

    /// <summary>Write what the user saved from the editor, approved as written.</summary>
    public void Save(string file, string text)
    {
        File.WriteAllText(file, text);
        trust.Approve(file, DefinitionHash.Of(text));
    }

    /// <summary>Forget a trellis; one kept in sprig is deleted with it. Definitions it used are left for others.</summary>
    public void RemoveTrellis(string id)
    {
        var path = registry.Resolve(id);
        registry.Remove(id);
        if (path is not null && virtuals.IsVirtual(path)) virtuals.DeleteTrellis(Path.GetFileName(Path.GetDirectoryName(path)!));
    }

    string NewRepoDefinition(string repoDir)
    {
        var name = Ids.Slug(Path.GetFileName(repoDir.TrimEnd('/', '\\')), "repo");
        var id = virtuals.UnusedRepoId(name);
        Approve(virtuals.CreateRepo(id, Scaffold.Repo(repoDir, name)));
        return id;
    }

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
