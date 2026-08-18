namespace Sprig.App.ViewModels;

/// <summary>Where the user is along the repo → map → workspace pipeline.</summary>
public enum SetupStage { Empty, ReposReady, MapReady, Running }

/// <summary>
/// Read-only projection of the three stores that drives Home's journey rail and next-best-action.
/// Pure — computed from counts only, no side effects — so the rail and the banner can never
/// disagree. This is the single source of truth for "where am I / what's next".
/// </summary>
public sealed class SetupState
{
    public int Repos { get; }
    public int Maps { get; }
    public int Workspaces { get; }
    public SetupStage Stage { get; }

    public SetupState(int repos, int maps, int workspaces)
    {
        Repos = repos;
        Maps = maps;
        Workspaces = workspaces;
        Stage = repos == 0 ? SetupStage.Empty
              : maps == 0 ? SetupStage.ReposReady
              : workspaces == 0 ? SetupStage.MapReady
              : SetupStage.Running;
    }

    // Per-step rail status (a step is "done" once it has content; exactly one step is "next").
    public bool ReposDone => Repos > 0;
    public bool MapsDone => Maps > 0;
    public bool WorkspacesDone => Workspaces > 0;

    public bool ReposNext => Stage == SetupStage.Empty;
    public bool MapsNext => Stage == SetupStage.ReposReady;
    public bool WorkspacesNext => Stage == SetupStage.MapReady;

    public string ReposCountLabel => Count(Repos, "registered");
    public string MapsCountLabel => Maps == 0 ? "none yet" : Maps == 1 ? "1 map" : $"{Maps} maps";
    public string WorkspacesCountLabel => Count(Workspaces, "running");

    // Next-best-action content (drives the banner + the primary button).
    public string NextKicker => Stage == SetupStage.Empty ? "STEP 1 OF 3 · START HERE" : "NEXT BEST ACTION";

    public string NextTitle => Stage switch
    {
        SetupStage.Empty => "Point sprig at a repo you want to isolate",
        SetupStage.ReposReady => "Compose your repos into a map",
        SetupStage.MapReady => "Spin up your first workspace",
        _ => "Spin up another workspace",
    };

    public string NextSub => Stage switch
    {
        SetupStage.Empty => "Pick a git folder; sprig reads (or writes) its .sprig.json.",
        SetupStage.ReposReady => "A map lists the repos; wiring is derived from what each provides and needs.",
        SetupStage.MapReady => "Fresh worktrees, branches and non-colliding ports in one step.",
        _ => "Each workspace is fully isolated — run as many side by side as you like.",
    };

    public string NextCta => Stage switch
    {
        SetupStage.Empty => "Add a repo  →",
        SetupStage.ReposReady => "Compose a map  →",
        _ => "New workspace  →",
    };

    static string Count(int n, string noun) => n == 0 ? "none yet" : n == 1 ? $"1 {noun}" : $"{n} {noun}";
}
