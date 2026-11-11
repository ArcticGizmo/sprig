using Sprig.Engine.Definitions;
using Sprig.Engine.Store;
using Sprig.Engine.Orchestrations;

namespace Sprig.Engine.Authoring;

/// <summary>
/// What the app does to orchestrations and repos on the user's behalf. What sprig writes, and what the user saves from its
/// editor, is approved as it's written, since the user is the author; anything else still asks. A copy keeps the
/// approval of what it was copied from, so code the user hasn't read is never approved by being moved.
/// </summary>
public sealed class OrchestrationAuthoring(StoredDefinitions stored, OrchestrationRegistry registry, ITrustStore trust)
{
    readonly TrustRules _rules = new(trust, stored);

    /// <summary>
    /// A new orchestration kept in sprig, over <paramref name="repoDirs"/>, naming each repo by its entry in the Repos list.
    /// An entry already there for the folder is reused; otherwise one is added (<see cref="AddRepo"/>).
    /// </summary>
    public OrchestrationEntry CreateOrchestration(string name, IReadOnlyList<string> repoDirs)
    {
        if (repoDirs.Count == 0) throw new ArgumentException("choose at least one repo folder");
        foreach (var dir in repoDirs)
            if (!Directory.Exists(dir)) throw new ArgumentException($"there's no folder at {dir}");

        var takenIds = registry.List().Select(e => e.Id).Concat(stored.OrchestrationIds()).Concat(stored.RepoIdsTaken()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var id = Ids.Unused(Ids.Slug(name, "orchestration"), takenIds);
        var repos = repoDirs.Select(dir =>
        {
            var full = LongPath.Of(dir);
            return new ScaffoldRepo(null, Use: (stored.RepoFor(full) ?? AddRepo(full)).Id);
        }).ToArray();

        var file = stored.CreateOrchestration(id, Scaffold.Orchestration(name, repos));
        Approve(file);
        return registry.Add(id, file);
    }

    /// <summary>A repo on the Repos list.</summary>
    /// <param name="inRepo">True to use the folder's own sprig.yaml, which must exist. False for a definition kept in
    /// sprig, starting from a copy of the folder's sprig.yaml if it has one, else a draft from what's in the folder.
    /// Null picks for you: the folder's own sprig.yaml if it has one, else a draft kept in sprig.</param>
    public RepoEntry AddRepo(string folder, string? id = null, bool? inRepo = null)
    {
        folder = LongPath.Of(folder);
        if (!Directory.Exists(folder)) throw new ArgumentException($"there's no folder at {folder}");
        var own = Path.Combine(folder, OrchestrationLoader.RepoDefinitionFile);
        var hasOwn = File.Exists(own);
        if (inRepo == true && !hasOwn) throw new ArgumentException($"there's no sprig.yaml in {folder}");
        id ??= SuggestNewId(folder, inRepo ?? hasOwn);

        if (inRepo ?? hasOwn)
        {
            if (stored.InRepoFor(folder) is { } existing)
                throw new ArgumentException($"its sprig.yaml is already on the list as '{existing.Id}'");
            return stored.AddRepo(id, folder, inRepo: true, definition: null);
        }
        if (hasOwn)
        {
            var text = File.ReadAllText(own);
            var copy = stored.AddRepo(id, folder, inRepo: false, text);
            CarryApproval(own, copy.DefinitionFile, text);
            return copy;
        }
        var entry = stored.AddRepo(id, folder, inRepo: false, Draft(folder));
        Approve(entry.DefinitionFile);
        return entry;
    }

    /// <summary>
    /// Make <paramref name="folder"/> runnable: its own sprig.yaml if it has one, else a draft from what's in it, kept in
    /// sprig or, with <paramref name="writeToFolder"/>, written into the folder as sprig.yaml (approved, since sprig wrote
    /// it). A folder already on the list keeps the entry it has. Returns the entry and whether it was already there.
    /// </summary>
    public (RepoEntry Entry, bool Existed) Init(string folder, string? id = null, bool writeToFolder = false)
    {
        folder = LongPath.Of(folder);
        if (!Directory.Exists(folder)) throw new ArgumentException($"there's no folder at {folder}");
        var listed = id is null ? stored.RepoFor(folder) : stored.Repo(id);
        if (listed is not null && SamePath(listed.Folder, folder)) return (listed, true);

        var own = Path.Combine(folder, OrchestrationLoader.RepoDefinitionFile);
        if (writeToFolder && !File.Exists(own))
        {
            File.WriteAllText(own, Draft(folder));
            Approve(own);
        }
        return (AddRepo(folder, id), false);
    }

    /// <summary>The ID offered for a new entry over <paramref name="folder"/>: its name, with "-sprig" after it for one
    /// kept in sprig beside a sprig.yaml of the folder's own, so the two read as a pair.</summary>
    public string SuggestNewId(string folder, bool inRepo)
    {
        var name = FolderName(folder);
        return stored.UnusedRepoId(!inRepo && HasOwn(folder) ? $"{name}-sprig" : name);
    }

    /// <summary>
    /// A new entry, <paramref name="newId"/>, kept in sprig and starting as a copy of <paramref name="id"/>'s sprig.yaml,
    /// so changes can be tried without touching the repo. <paramref name="id"/> and the orchestrations using it are left as
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
    /// Write <paramref name="id"/>'s definition into its folder as sprig.yaml, left uncommitted, and use it from a new
    /// entry, <paramref name="newId"/>. <paramref name="id"/> and the orchestrations using it are left as they are. A folder
    /// that already has a sprig.yaml is never written over.
    /// </summary>
    public RepoEntry WriteToRepo(string id, string newId)
    {
        var entry = Entry(id);
        if (entry.InRepo) throw new ArgumentException($"'{id}' already uses the repo's sprig.yaml");
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
            ? $"The folder already has its own sprig.yaml, on the list as '{existing.Id}'. Sprig won't write over it."
            : "The folder already has its own sprig.yaml. Sprig won't write over it; + Add the folder to use it.";
    }

    /// <summary>The ID offered for a new entry made from <paramref name="entry"/>: the folder's name for one in the
    /// repo, with "-sprig" after it for one kept in sprig.</summary>
    public string SuggestId(RepoEntry entry, bool inRepo)
        => stored.UnusedRepoId(inRepo ? FolderName(entry.Folder) : $"{FolderName(entry.Folder)}-sprig");

    /// <summary>Bring one of an orchestration's <c>path:</c> repos in through the Repos list instead, reusing an
    /// entry for its folder, so it shows on the Repos page and other orchestrations can use it by ID.</summary>
    public RepoEntry UseFromRepos(string orchestrationFile, string repoPath)
    {
        var folder = LongPath.Of(Path.Combine(Path.GetDirectoryName(orchestrationFile)!, repoPath));
        var entry = stored.RepoFor(folder) ?? AddRepo(folder);
        EditOrchestration(orchestrationFile, DefinitionEditor.RepoToUseRepo(File.ReadAllText(orchestrationFile), repoPath, entry.Id));
        return entry;
    }

    /// <summary>
    /// Give a repo kept in sprig a new ID, keeping its approval. Only while no orchestration names it, since their
    /// <c>use:</c> lines and their workspaces hold the ID; the repo's own workspaces are the caller's to check.
    /// </summary>
    public RepoEntry RenameRepo(string id, string newId)
    {
        var entry = Entry(id);
        if (RenameBlocker(entry) is { } blocked) throw new ArgumentException(blocked);
        var text = File.Exists(entry.DefinitionFile) ? File.ReadAllText(entry.DefinitionFile) : null;
        var approved = text is not null && _rules.Check(entry.DefinitionFile, DefinitionHash.Of(text)).State == TrustState.Trusted;
        var renamed = stored.RenameRepo(id, newId);
        if (approved) Approve(renamed.DefinitionFile);
        return renamed;
    }

    /// <summary>Why <see cref="RenameRepo"/> can't run for <paramref name="entry"/>, or null when it can.</summary>
    public string? RenameBlocker(RepoEntry entry)
    {
        if (entry.InRepo) return $"'{entry.Id}' uses the repo's own sprig.yaml. Only a repo kept in sprig can be renamed.";
        return UsedBy(entry.Id) is { Count: > 0 } users
            ? $"{string.Join(", ", users.Select(u => $"'{u}'"))} {(users.Count == 1 ? "uses" : "use")} it by this ID. Take it out of {(users.Count == 1 ? "that orchestration" : "those orchestrations")} first."
            : null;
    }

    /// <summary>Take a repo off the Repos list, with the definition sprig keeps for it. The folder's own sprig.yaml is
    /// left alone.</summary>
    public void DeleteRepo(string id) => stored.DeleteRepo(id);

    /// <summary>Registered orchestrations that name the repo <paramref name="id"/> with <c>use:</c>.</summary>
    public IReadOnlyList<string> UsedBy(string id)
        => registry.List()
            .Where(e => File.Exists(e.Path) && DefinitionEditor.UsedIds(File.ReadAllText(e.Path)).Contains(id))
            .Select(e => e.Id)
            .ToArray();

    /// <summary>Write what the user saved from the editor, approved as written.</summary>
    public void Save(string file, string text)
    {
        File.WriteAllText(file, text);
        trust.Approve(file, DefinitionHash.Of(text));
    }

    /// <summary>Forget an orchestration; one kept in sprig is deleted with it. Repos it used stay for others.</summary>
    public void RemoveOrchestration(string id)
    {
        var path = registry.Resolve(id);
        registry.Remove(id);
        if (path is not null && stored.IsKept(path)) stored.DeleteOrchestration(Path.GetFileName(Path.GetDirectoryName(path)!));
    }

    RepoEntry Entry(string id) => stored.Repo(id) ?? throw new ArgumentException($"'{id}' isn't in the Repos list", nameof(id));

    static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    static string Draft(string folder) => Scaffold.Repo(folder, Ids.Slug(FolderName(folder), "repo"));

    void CarryApproval(string from, string to, string text)
    {
        if (_rules.Check(from, DefinitionHash.Of(text)).State == TrustState.Trusted) Approve(to);
    }

    static bool HasOwn(string folder) => folder.Length > 0 && File.Exists(Path.Combine(folder, OrchestrationLoader.RepoDefinitionFile));

    static string FolderName(string folder) => Path.GetFileName(folder.TrimEnd('/', '\\'));

    void EditOrchestration(string orchestrationFile, EditResult edit)
    {
        if (!edit.Ok) throw new InvalidOperationException(edit.Error);
        ApplyEdit(orchestrationFile, edit.Text);
    }

    /// <summary>A change sprig wrote and the user accepted. It keeps the file approved only if it was before: the rest
    /// of it is still code the user may not have read.</summary>
    public void ApplyEdit(string file, string text)
    {
        var wasApproved = _rules.Check(file, DefinitionHash.Of(File.ReadAllText(file))).State == TrustState.Trusted;
        File.WriteAllText(file, text);
        if (wasApproved) Approve(file);
    }

    void Approve(string file) => trust.Approve(file, DefinitionHash.Of(File.ReadAllText(file)));
}
