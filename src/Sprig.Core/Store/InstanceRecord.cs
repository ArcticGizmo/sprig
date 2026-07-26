using System.Text.Json.Serialization;
using Sprig.Core.Setup;

namespace Sprig.Core.Store;

/// <summary>
/// The central-store record for one workspace instance — the source of truth teardown reads
/// to know what to dismantle, even when reality has drifted (see docs/spike-findings.md S3).
/// Fields will grow as M2/M3 land; this is the M1 shape.
/// </summary>
public sealed record InstanceRecord
{
    public required string Workspace { get; init; }
    public string? Stack { get; init; }
    public IReadOnlyList<InstanceRepo> Repos { get; init; } = [];
    public IReadOnlyDictionary<string, int> Ports { get; init; } = new Dictionary<string, int>();
    public string? LastStatus { get; init; }
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// The shared resources whose overlays shaped this workspace, pinned at create. Read from here for the
    /// workspace's whole life rather than from whatever is enabled now: toggle a resource off with a live
    /// workspace built on it and <c>down</c> would otherwise try to release a slot it never held, while the
    /// repo's own postgres — suppressed at create, so never configured — would suddenly be expected to exist.
    /// </summary>
    public IReadOnlyList<string> AppliedOverlays { get; init; } = [];

    /// <summary>The slots this workspace holds on shared resources, and what they own there.</summary>
    public IReadOnlyList<InstanceSlot> Slots { get; init; } = [];
}

/// <summary>A workspace's hold on one shared resource, as recorded on its instance.</summary>
public sealed record InstanceSlot
{
    public required string Resource { get; init; }
    public int Slot { get; init; }

    /// <summary>One entry per repo the resource injected — usually one, more when a resource serves several.</summary>
    public IReadOnlyList<InstanceNamespace> Namespaces { get; init; } = [];
}

/// <summary>One namespace inside a slot: the resolved values (database, user, …) for one repo.</summary>
public sealed record InstanceNamespace
{
    public required string Repo { get; init; }
    public IReadOnlyDictionary<string, string> Values { get; init; } = new Dictionary<string, string>();
}

/// <summary>One repo's materialised state within an instance.</summary>
public sealed record InstanceRepo
{
    public required string Name { get; init; }
    /// <summary>The source repo whose worktree this is.</summary>
    public required string SourcePath { get; init; }
    /// <summary>The sibling worktree path (<c>&lt;repo&gt;--&lt;workspace&gt;</c>).</summary>
    public required string WorktreePath { get; init; }
    /// <summary>The sprig-created branch (<c>sprig/&lt;workspace&gt;</c>), if any.</summary>
    public string? Branch { get; init; }

    /// <summary>The generated compose files in the central store (one per overridden compose file in
    /// the repo's config), if this repo has infra.</summary>
    public IReadOnlyList<string> GeneratedComposePaths { get; init; } = [];

    /// <summary>Legacy single generated-compose path from records written before multi-compose support.
    /// Read-only compatibility shim — omitted on write; prefer <see cref="ComposePaths"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GeneratedComposePath { get; init; }

    /// <summary>Every generated compose file for this repo — the list, plus any legacy single path.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> ComposePaths =>
        GeneratedComposePaths.Count > 0 ? GeneratedComposePaths
        : GeneratedComposePath is { Length: > 0 } p ? [p]
        : [];

    /// <summary>This repo's resolved input values (input name → value) as supplied by the stack.</summary>
    public IReadOnlyDictionary<string, string> Inputs { get; init; } = new Dictionary<string, string>();

    /// <summary>Outcome of this repo's <c>setup</c> commands, in order (empty if none declared).
    /// Recorded even on failure — setup warns rather than rolling back — so the failure survives
    /// for the UI/CLI to surface.</summary>
    public IReadOnlyList<SetupOutcome> Setup { get; init; } = [];
}
