using Sprig.Core.Config;
using Sprig.Core.Shared;
using Sprig.Core.Stacks;
using Sprig.Core.Workspaces;

namespace Sprig.Core.Planning;

/// <summary>
/// One repo inside a <see cref="WorkspacePlan"/>: where it came from, the config that will actually be
/// materialised, and the expression that will produce each of its declared inputs.
/// </summary>
/// <param name="Source">The repo as resolved from the registry — root path and its on-disk config.</param>
/// <param name="EffectiveConfig">
/// The config materialisation reads. Starts as <c>Source.Config</c>; an overlay edits <b>this</b>, never
/// the file on disk, which is what keeps a machine-local optimisation out of a tracked file.
/// </param>
/// <param name="Bindings">Declared input name → the expression the stack supplies for it (unresolved).</param>
public sealed record PlannedRepo(
    ResolvedRepo Source,
    SprigRepoConfig EffectiveConfig,
    IReadOnlyDictionary<string, string> Bindings)
{
    public string Name => Source.Name;
    public string Root => Source.Root;

    /// <summary>
    /// Compose services a shared resource has taken responsibility for, so this repo's own copy isn't
    /// generated. Never something a repo can declare — suppression is a property of the plan, not of the
    /// tracked config, which is why it lives here rather than on <see cref="EffectiveConfig"/>.
    /// </summary>
    public IReadOnlyList<ComposeSuppression> Suppress { get; init; } = [];

    /// <summary>
    /// Extra variables an overlay published into this repo's scope, keyed as <c>shared.&lt;value&gt;</c> and
    /// <c>shared.&lt;resource&gt;.&lt;value&gt;</c>. Raw templates — they may reference each other and
    /// <c>${sprig.workspace}</c>/<c>${sprig.repo}</c>, and are resolved once at bind time along with
    /// everything else, so there is exactly one place where substitution happens.
    /// </summary>
    public IReadOnlyDictionary<string, string> SharedValues { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// The source repo re-pointed at <see cref="EffectiveConfig"/>, for the handful of collaborators that
    /// still take a <see cref="ResolvedRepo"/> (port-constraint resolution, the create checklist).
    /// </summary>
    public ResolvedRepo Effective => Source with { Config = EffectiveConfig };
}

/// <summary>
/// Everything sprig intends to do for one workspace, <b>before any port is allocated</b> — the object an
/// overlay transforms. Splitting this out of <see cref="ResolvedStack"/> is what lets a shared resource
/// rewrite a value and have the now-unreferenced stack port simply never be allocated.
/// </summary>
/// <param name="Notes">Decisions recorded while building the plan. Empty until an overlay adds to it.</param>
public sealed record WorkspacePlan(
    string Workspace,
    string? StackName,
    IReadOnlyList<PlannedRepo> Repos,
    IReadOnlyList<string> DeclaredPorts,
    IReadOnlyList<PlanNote> Notes)
{
    /// <summary>
    /// The declared stack ports some surviving binding actually references, in declaration order. This is
    /// the set that gets allocated — a port nothing points at did nothing before this change either, and
    /// once an overlay rewrites a binding it is how the freed port stops being reserved.
    /// </summary>
    /// <remarks>Recomputed on access, so it stays correct across <c>with</c> expressions.</remarks>
    public IReadOnlyList<string> ReferencedPorts
    {
        get
        {
            var referenced = new HashSet<string>(StringComparer.Ordinal);
            foreach (var repo in Repos)
                foreach (var expr in repo.Bindings.Values)
                    foreach (var port in PortExpressions.ReferencedPorts(expr))
                        referenced.Add(port);
            return [.. DeclaredPorts.Where(referenced.Contains)];
        }
    }

    /// <summary>Declared ports nothing references — reported by <c>sprig plan</c> rather than allocated.</summary>
    public IReadOnlyList<string> UnreferencedPorts
    {
        get
        {
            var referenced = ReferencedPorts.ToHashSet(StringComparer.Ordinal);
            return [.. DeclaredPorts.Where(p => !referenced.Contains(p))];
        }
    }

    /// <summary>The effective repos, for collaborators that work in <see cref="ResolvedRepo"/> terms.</summary>
    public IReadOnlyList<ResolvedRepo> EffectiveRepos => [.. Repos.Select(r => r.Effective)];

    /// <summary>The bindings as a repo → (input → expression) map, the shape the stack layer speaks.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> EffectiveBindings
        => Repos.ToDictionary(r => r.Name, r => r.Bindings, StringComparer.Ordinal);
}
