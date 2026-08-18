namespace Sprig.Core.Git;

/// <summary>One commit as read for the branch-graph view: its identity and parents (the DAG edges), the ref
/// labels decorating it (branch/tag names at this commit), and the bits shown in a row. <paramref name="When"/>
/// is null when the date couldn't be parsed. Parents are ordered as git reports them (first parent first),
/// which the lane layout relies on to keep a branch's mainline in one lane.</summary>
public sealed record GraphCommit(
    string Sha,
    IReadOnlyList<string> Parents,
    IReadOnlyList<string> Refs,
    string Author,
    DateTimeOffset? When,
    string Subject);
