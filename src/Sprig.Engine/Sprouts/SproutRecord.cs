using Sprig.Engine.Compose;

namespace Sprig.Engine.Sprouts;

public enum SproutState { Starting, Up, Down, Failed }

/// <param name="RepoPath">The repo as the trellis names it.</param>
/// <param name="Source">The repo's own checkout.</param>
/// <param name="Dir">Where this sprout runs it: a worktree, or <see cref="Source"/> when it runs in place.</param>
/// <param name="BranchCreated">Sprig made the branch for this sprout, rather than checking out one that existed.</param>
/// <param name="From">The branch sprig started the sprout's branch from, when it made it.</param>
public sealed record SproutRepo(string RepoPath, string Name, string Source, string Dir, bool Worktree, bool BranchCreated, string? From = null);

/// <summary>What sprig knows about one sprout: where its repos are, what it wrote, and the compose projects it
/// started. Kept in the store so <c>down</c> and <c>rm</c> undo exactly what <c>up</c> did.</summary>
/// <param name="Branch">Null when the repos run in place.</param>
/// <param name="Folder">The folder holding the sprout's worktrees; null when the repos run in place.</param>
/// <param name="LastUpAt">When it last finished coming up; null for a record from before this was kept.</param>
public sealed record SproutRecord(
    string Trellis,
    string TrellisPath,
    string Name,
    string? Branch,
    string PortPolicy,
    IReadOnlyList<SproutRepo> Repos,
    IReadOnlyList<ComposeProject> Compose,
    IReadOnlyList<WrittenFile> Files,
    SproutState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? Folder = null,
    string? Error = null,
    DateTimeOffset? LastUpAt = null)
{
    public bool InPlace => Branch is null;

    /// <summary>Names the sprout in the port leases.</summary>
    public string Key => KeyOf(Trellis, Name);

    public static string KeyOf(string trellis, string sprout) => $"{trellis}/{sprout}";
}
