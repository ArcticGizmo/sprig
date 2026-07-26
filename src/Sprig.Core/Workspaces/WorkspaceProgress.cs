namespace Sprig.Core.Workspaces;

/// <summary>The visual state of one step in a create/teardown checklist.</summary>
public enum WorkspaceStepState
{
    /// <summary>Not started yet (empty circle).</summary>
    Pending,
    /// <summary>Currently executing (blue spinner).</summary>
    Running,
    /// <summary>Completed, but with a soft/non-fatal problem (yellow) — e.g. a failed setup command.</summary>
    Warning,
    /// <summary>Failed hard; the operation cannot continue past this point (red).</summary>
    Error,
    /// <summary>Completed successfully (green).</summary>
    Done,
}

/// <summary>One planned unit of work in a workspace operation, identified by a stable <paramref name="Id"/>
/// so a progress report can target the row the plan created for it. <see cref="SubStep"/> marks a child
/// row (e.g. one setup command) that a UI indents under its parent.</summary>
public sealed record WorkspaceStep(string Id, string Label)
{
    public bool SubStep { get; init; }
}

/// <summary>A single state transition of a planned step, reported to an
/// <see cref="IProgress{T}"/> as a create/teardown runs. <paramref name="Detail"/> carries a short
/// human note (e.g. the failing command) for the Warning/Error cases. When <see cref="Output"/> is set
/// the report is a streamed output line for the step (append to its live view), not a state change.</summary>
public sealed record WorkspaceStepProgress(string StepId, WorkspaceStepState State, string? Detail = null)
{
    public string? Output { get; init; }
}

/// <summary>Stable step ids for the create checklist. Shared by the planner and the executor so the
/// two never drift on what a report targets.</summary>
internal static class CreateStepIds
{
    public const string Ports = "ports";
    public const string Record = "record";
    public static string Worktree(string repo) => $"{repo}:worktree";
    public static string Env(string repo) => $"{repo}:env";
    public static string Compose(string repo) => $"{repo}:compose";
    public static string Setup(string repo) => $"{repo}:setup";
    /// <summary>Sub-step id for one setup command (keyed by its index in the repo's setup list).</summary>
    public static string SetupCommand(string repo, int index) => $"{repo}:setup:{index}";
}

/// <summary>Stable step ids for the teardown checklist.</summary>
internal static class RemoveStepIds
{
    public const string Ports = "ports";
    public const string Record = "record";
    public static string Infra(string repo) => $"{repo}:infra";
    public static string Worktree(string repo) => $"{repo}:worktree";
    public static string Branch(string repo) => $"{repo}:branch";
}
