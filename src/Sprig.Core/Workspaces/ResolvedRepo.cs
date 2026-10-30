using Sprig.Core.Config;

namespace Sprig.Core.Workspaces;

/// <summary>A repo resolved to a concrete root path + its parsed config, ready to materialise. The unit the
/// map path (<see cref="WorkspaceService.CreateFromMap"/>) works in — a selection is a list of these.</summary>
/// <param name="VirtualConfigPath">Where the config came from when sprig holds it rather than the repo (a
/// virtual repo); carried onto the workspace record so later reloads read the same file.</param>
public sealed record ResolvedRepo(string Name, string Root, SprigRepoConfig Config, string? VirtualConfigPath = null);
