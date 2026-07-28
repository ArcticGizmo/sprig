using Sprig.Core.Config;

namespace Sprig.Core.Workspaces;

/// <summary>A repo resolved to a concrete root path + its parsed config, ready to materialise.</summary>
public sealed record ResolvedRepo(string Name, string Root, SprigRepoConfig Config);

/// <summary>
/// The concrete input to a workspace create: the repos, the stack's named ports, and the per-repo
/// input bindings. Built from a named stack (registry + stack def) or an ad-hoc single repo.
/// <para>
/// For a <i>partial</i> workspace, <see cref="Repos"/> and <see cref="Ports"/> are already narrowed
/// to the selection — create materialises exactly what it is handed, so a deselected repo's
/// worktree, env and compose files are never generated. <see cref="ExcludedRepos"/> and
/// <see cref="SkippedPorts"/> carry what was left out, purely so it can be recorded and shown.
/// </para>
/// </summary>
public sealed record ResolvedStack(
    string? StackName,
    IReadOnlyList<ResolvedRepo> Repos,
    IReadOnlyList<string> Ports,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Bindings)
{
    /// <summary>Stack repos deliberately left out of this workspace; empty for a full one.</summary>
    public IReadOnlyList<string> ExcludedRepos { get; init; } = [];

    /// <summary>Stack ports not provisioned because only <see cref="ExcludedRepos"/> referenced them
    /// (see <c>StackSelection.OrphanedPorts</c>).</summary>
    public IReadOnlyList<string> SkippedPorts { get; init; } = [];

    /// <summary>True when this is a partial workspace — a subset of its stack's repos.</summary>
    public bool IsPartial => ExcludedRepos.Count > 0;
}
