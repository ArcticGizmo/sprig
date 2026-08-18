namespace Sprig.Core.Git;

/// <summary>A branch (local or remote-tracking) offered as a start point, with the date of its tip commit
/// so a picker can surface the most recently active branches first. <paramref name="LastCommit"/> is null
/// when the date couldn't be parsed.</summary>
public sealed record BranchRef(string Name, DateTimeOffset? LastCommit);
