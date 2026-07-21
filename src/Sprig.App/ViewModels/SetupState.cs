namespace Sprig.App.ViewModels;

/// <summary>Where the user is along the repo → stack → workspace pipeline.</summary>
public enum SetupStage { Empty, ReposReady, StackReady, Running }

/// <summary>
/// Read-only projection of the three stores that drives Home's journey rail and next-best-action.
/// Pure — computed from counts only, no side effects — so the rail and the banner can never
/// disagree. This is the single source of truth for "where am I / what's next".
/// </summary>
public sealed class SetupState
{
    public int Repos { get; }
    public int Stacks { get; }
    public int Workspaces { get; }
    public SetupStage Stage { get; }

    public SetupState(int repos, int stacks, int workspaces)
    {
        Repos = repos;
        Stacks = stacks;
        Workspaces = workspaces;
        Stage = repos == 0 ? SetupStage.Empty
              : stacks == 0 ? SetupStage.ReposReady
              : workspaces == 0 ? SetupStage.StackReady
              : SetupStage.Running;
    }

    // Per-step rail status (a step is "done" once it has content; exactly one step is "next").
    public bool ReposDone => Repos > 0;
    public bool StacksDone => Stacks > 0;
    public bool WorkspacesDone => Workspaces > 0;

    public bool ReposNext => Stage == SetupStage.Empty;
    public bool StacksNext => Stage == SetupStage.ReposReady;
    public bool WorkspacesNext => Stage == SetupStage.StackReady;

    public string ReposCountLabel => Count(Repos, "registered");
    public string StacksCountLabel => Stacks == 0 ? "none yet" : Stacks == 1 ? "1 stack" : $"{Stacks} stacks";
    public string WorkspacesCountLabel => Count(Workspaces, "running");

    // Next-best-action content (drives the banner + the primary button).
    public string NextKicker => Stage == SetupStage.Empty ? "STEP 1 OF 3 · START HERE" : "NEXT BEST ACTION";

    public string NextTitle => Stage switch
    {
        SetupStage.Empty => "Point sprig at a repo you want to isolate",
        SetupStage.ReposReady => "Wire your repos into a stack",
        SetupStage.StackReady => "Spin up your first workspace",
        _ => "Spin up another workspace",
    };

    public string NextSub => Stage switch
    {
        SetupStage.Empty => "Pick a git folder; sprig reads (or writes) its .sprig.json.",
        SetupStage.ReposReady => "A stack owns the ports and supplies each repo the values it declares.",
        SetupStage.StackReady => "Fresh worktrees, branches and non-colliding ports in one step.",
        _ => "Each workspace is fully isolated — run as many side by side as you like.",
    };

    public string NextCta => Stage switch
    {
        SetupStage.Empty => "Add a repo  →",
        SetupStage.ReposReady => "Wire a stack  →",
        _ => "New workspace  →",
    };

    static string Count(int n, string noun) => n == 0 ? "none yet" : n == 1 ? $"1 {noun}" : $"{n} {noun}";
}
