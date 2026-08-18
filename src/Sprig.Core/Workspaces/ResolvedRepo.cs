using Sprig.Core.Config;

namespace Sprig.Core.Workspaces;

/// <summary>A repo resolved to a concrete root path + its parsed config, ready to materialise. The unit the
/// map path (<see cref="WorkspaceService.CreateFromMap"/>) works in — a selection is a list of these.</summary>
public sealed record ResolvedRepo(string Name, string Root, SprigRepoConfig Config);
