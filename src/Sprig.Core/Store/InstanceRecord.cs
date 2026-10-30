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

    /// <summary>The map this workspace was created from (the Graph Turn model), or null for a stack/ad-hoc
    /// workspace. Mutually exclusive with <see cref="Stack"/> in practice.</summary>
    public string? Map { get; init; }

    /// <summary>The repos selected from the map for this workspace (a slice of the map). Empty for a
    /// stack/ad-hoc workspace.</summary>
    public IReadOnlyList<string> SelectedRepos { get; init; } = [];

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

    /// <summary>The <b>claim branch</b> — the branch Sprig last cut or checked out across the workspace's
    /// repos (mirrored per repo on <see cref="InstanceRepo.Branch"/>). Not the workspace's identity (the slot
    /// is) and not necessarily what is checked out now: git can move a worktree at any time, so displays read
    /// the live branch with <see cref="Git.WorktreeHead"/>. Kept so release can report unpushed commits on it.
    /// Null while the workspace is parked.</summary>
    public string? Branch { get; init; }

    /// <summary>What the workspace is for ("Risk matrix overrides") — the headline wherever it is shown.
    /// Owned by the claimer and editable at any time; never filled in implicitly, so an unlabelled workspace
    /// shows its live branch instead. Belongs to the claim, so release moves it to <see cref="ReleasedLabel"/>.
    /// Null when unlabelled.</summary>
    public string? Label { get; init; }

    /// <summary>A short, editable status line beneath the label ("waiting on review"). Belongs to the claim:
    /// cleared on release and on every new claim, so the next claimer never inherits it. Null when unset.</summary>
    public string? Note { get; init; }

    /// <summary>The claimer's own <c>&lt;namespace&gt;.&lt;name&gt;</c> → value pairs (see <see cref="Workspaces.WorkspaceTags"/>),
    /// so a caller can mark the workspaces it holds and find them again. Stored, shown and filtered on — never
    /// acted on. Cleared on release.</summary>
    public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>();

    /// <summary>An explicit <c>#rrggbb</c> colour for this slot; null uses the slot's palette colour
    /// (see <see cref="Workspaces.WorkspaceColour"/>). Kept across claims.</summary>
    public string? Colour { get; init; }

    /// <summary>The tags the workspace carried when it was last released, so a caller that cleaned up can
    /// still see what it released. Empty if never released with tags.</summary>
    public IReadOnlyDictionary<string, string> ReleasedTags { get; init; } = new Dictionary<string, string>();

    /// <summary>The label the workspace carried when it was last released. Shown when picking a free
    /// workspace to reuse, as a hint to the state it was left in. Null if it had none.</summary>
    public string? ReleasedLabel { get; init; }

    /// <summary>The note the workspace carried when it was last released, kept for the same reason as
    /// <see cref="ReleasedTags"/>. Null if it had none.</summary>
    public string? ReleasedNote { get; init; }

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
    /// <summary>The real worktree path: <c>&lt;repo&gt;--&lt;workspace&gt;</c> beside the repo, or
    /// <c>&lt;repo&gt;/.claude/worktrees/&lt;workspace&gt;</c> under the Claude layout. Stable for the life of the
    /// workspace — machine-named, never renamed when a branch is claimed.</summary>
    public required string WorktreePath { get; init; }

    /// <summary>The sprig-held config this repo was built from when it's a virtual repo; null when its config
    /// is the <c>.sprig.json</c> committed to the repo. Claim, refresh and the worktree-side readers load this
    /// file instead, because the worktree has no config of its own.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VirtualConfigPath { get; init; }

    /// <summary>The config file to read for this worktree's modules and entry points.</summary>
    [JsonIgnore]
    public string WorktreeConfigPath => VirtualConfigPath ?? Path.Combine(WorktreePath, ".sprig.json");

    /// <summary>The junction at the sibling path that points at a nested <see cref="WorktreePath"/>, so the
    /// worktree can still be reached beside the repo. Null for a sibling-layout worktree, or when the link
    /// couldn't be made. Teardown removes it before the worktree, and never follows it.</summary>
    public string? LinkPath { get; init; }
    /// <summary>The branch checked out in this repo's worktree: the workspace's claim branch when claimed,
    /// <c>null</c> while parked (the worktree sits in detached HEAD with no branch of its own).</summary>
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

    /// <summary>This repo's resolved input values (input name → value) as supplied by the stack. Empty for a
    /// map workspace (which stores per-module capability scopes in <see cref="Modules"/> instead).</summary>
    public IReadOnlyDictionary<string, string> Inputs { get; init; } = new Dictionary<string, string>();

    /// <summary>Per-module resolved scopes for a <b>map</b> workspace — each module's env/compose templates
    /// resolve against its own capability values. Empty for a stack/ad-hoc workspace (which uses the
    /// repo-level <see cref="Inputs"/>). Lets claim/refresh rebuild each module's scope from the record alone,
    /// with no map re-resolution.</summary>
    public IReadOnlyList<InstanceModule> Modules { get; init; } = [];

    /// <summary>Outcome of this repo's <c>setup</c> commands, in order (empty if none declared).
    /// Recorded even on failure — setup warns rather than rolling back — so the failure survives
    /// for the UI/CLI to surface.</summary>
    public IReadOnlyList<SetupOutcome> Setup { get; init; } = [];
}

/// <summary>One module's resolved scope within a map workspace: the module name/path and the concrete
/// capability-qualified values (<c>&lt;capability&gt;.&lt;output&gt; → value</c>) its env/compose templates resolve
/// against. Rebuilt into an <c>IVariableSource</c> at claim/refresh time.</summary>
public sealed record InstanceModule
{
    public required string Name { get; init; }
    public string Path { get; init; } = "";
    public IReadOnlyDictionary<string, string> Values { get; init; } = new Dictionary<string, string>();
}
