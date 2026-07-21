using Sprig.Core.Config;

namespace Sprig.Core.Workspaces;

/// <summary>A repo resolved to a concrete root path + its parsed config, ready to materialise.</summary>
public sealed record ResolvedRepo(string Name, string Root, SprigRepoConfig Config);

/// <summary>
/// The concrete input to a workspace create: the repos, the stack's named ports, and the per-repo
/// input bindings. Built from a named stack (registry + stack def) or an ad-hoc single repo.
/// </summary>
public sealed record ResolvedStack(
    string? StackName,
    IReadOnlyList<ResolvedRepo> Repos,
    IReadOnlyList<string> Ports,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Bindings);
