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
    /// <summary>The generated compose file in the central store, if this repo has infra.</summary>
    public string? GeneratedComposePath { get; init; }
}
