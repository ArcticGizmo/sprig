using Sprig.Core.Config;
using Sprig.Core.Substitution;
using Sprig.Core.Workspaces;

namespace Sprig.Core.Planning;

/// <summary>One repo of a <see cref="BoundPlan"/>: its effective config plus the scope its templates resolve against.</summary>
/// <param name="Inputs">Declared input name → resolved value, in the shape the instance record stores.</param>
/// <param name="Scope">What this repo's env and compose templates resolve <c>${sprig.*}</c> against.</param>
public sealed record BoundRepo(
    ResolvedRepo Source,
    SprigRepoConfig EffectiveConfig,
    IReadOnlyDictionary<string, string> Inputs,
    IVariableSource Scope)
{
    public string Name => Source.Name;
    public string Root => Source.Root;
    public ResolvedRepo Effective => Source with { Config = EffectiveConfig };
}

/// <summary>
/// A <see cref="WorkspacePlan"/> with its ports allocated and every binding expression resolved — the
/// concrete instruction set materialisation works from, and the thing <c>sprig plan</c> prints.
/// </summary>
/// <param name="Notes">Every value's provenance, with expressions resolved to the values they produced.</param>
public sealed record BoundPlan(
    string Workspace,
    string? StackName,
    IReadOnlyDictionary<string, int> Ports,
    IReadOnlyList<BoundRepo> Repos,
    IReadOnlyList<string> UnreferencedPorts,
    IReadOnlyList<PlanNote> Notes)
{
    /// <summary>This repo's notes, in the order they were recorded.</summary>
    public IReadOnlyList<PlanNote> NotesFor(string repo)
        => [.. Notes.Where(n => string.Equals(n.Repo, repo, StringComparison.Ordinal))];

    /// <summary>Whether any layer above the base ones had a hand in this plan.</summary>
    public bool HasOverrides => Notes.Any(n => n.Layer == PlanLayer.Shared);
}
