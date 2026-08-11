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

    // --- Pool state (M2). A pooled workspace is one of a stack's bounded set; these describe where it
    // sits in that pool and whether it's currently in use. Absent/default on a pre-pool workspace. ---

    /// <summary>This workspace's index within its stack's pool (the <c>n</c> in <c>&lt;stack&gt;-&lt;n&gt;</c>).
    /// Null for a workspace not created through the pool flow.</summary>
    public int? WorkspaceIndex { get; init; }

    /// <summary>True while this workspace is checked out (in use). An <b>unclaimed</b> workspace is free
    /// to take — but not necessarily clean; how it's handled is decided at the next checkout.</summary>
    public bool Claimed { get; init; }

    /// <summary>The free-text label the user gave this workspace at checkout — metadata to recognise it
    /// by ("auth refactor"), never load-bearing. Null when unclaimed or never labelled.</summary>
    public string? Label { get; init; }

    /// <summary>When this workspace was last checked out; null if never claimed.</summary>
    public DateTimeOffset? ClaimedAt { get; init; }

    /// <summary>When this workspace was last released; lets checkout show "least recently used" hints so
    /// you can pick the leftover state you want. Null if never released.</summary>
    public DateTimeOffset? LastUsedAt { get; init; }

    /// <summary>True when a teardown ran but couldn't dismantle everything, so this record was kept
    /// (rather than deleted) to keep the workspace visible and the sweep resumable. Teardown is
    /// idempotent, so re-running it once the blocker is fixed picks up where it left off. Cleared on
    /// the next fully-successful teardown (which then deletes the record).</summary>
    public bool TeardownFailed { get; init; }

    /// <summary>Human notes on what a partial teardown couldn't finish (one per warned step), so the
    /// UI/CLI can explain what to fix before retrying. Empty unless <see cref="TeardownFailed"/>.</summary>
    public IReadOnlyList<string> TeardownIssues { get; init; } = [];

    /// <summary>Stack repos deliberately left out when this workspace was created (a <i>partial</i>
    /// workspace). Recorded, not acted on: teardown only ever walks <see cref="Repos"/>. Empty for a
    /// full workspace, and for one created from an ad-hoc repo.</summary>
    public IReadOnlyList<string> ExcludedRepos { get; init; } = [];

    /// <summary>Stack ports this workspace never provisioned because only <see cref="ExcludedRepos"/>
    /// referenced them — kept so the UI can explain why a stack port has no number here.</summary>
    public IReadOnlyList<string> SkippedPorts { get; init; } = [];

    /// <summary>True when this workspace holds a subset of its stack's repos.</summary>
    [JsonIgnore]
    public bool IsPartial => ExcludedRepos.Count > 0;

    /// <summary>True when any repo's last setup run had a failed command — a <b>degraded</b> workspace
    /// that stood up but may not actually work. Derived from the recorded outcomes, which create and
    /// every checkout/refresh rewrite, so it always reflects the latest run.</summary>
    [JsonIgnore]
    public bool SetupFailed => Repos.Any(r => r.Setup.Any(o => !o.Success));
}

/// <summary>One repo's materialised state within an instance.</summary>
public sealed record InstanceRepo
{
    public required string Name { get; init; }
    /// <summary>The source repo whose worktree this is.</summary>
    public required string SourcePath { get; init; }
    /// <summary>The sibling worktree path (<c>&lt;repo&gt;--&lt;workspace&gt;</c>).</summary>
    public required string WorktreePath { get; init; }
    /// <summary>The sprig-created branch (<c>sprig--&lt;workspace&gt;</c>), if any.</summary>
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
