namespace Sprig.Core.Git;

/// <summary>One entry from <c>git worktree list --porcelain</c>.</summary>
public sealed record WorktreeInfo(
    string Path,
    string? Head,
    string? Branch,
    bool IsPrunable,
    bool IsBare = false,
    bool IsDetached = false);
