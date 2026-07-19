using Sprig.Core.Config;

namespace Sprig.Core.Workspaces;

/// <summary>A repo resolved to a concrete root path + its parsed config, ready to materialise.</summary>
public sealed record ResolvedRepo(string Name, string Root, SprigRepoConfig Config);

/// <summary>
/// The concrete input to a workspace create: one or more resolved repos plus optional stack-level
/// variables. Built either from a named stack (registry + stack def) or an ad-hoc single repo.
/// </summary>
public sealed record ResolvedStack(
    string? StackName,
    IReadOnlyList<ResolvedRepo> Repos,
    IReadOnlyDictionary<string, string> Vars);
