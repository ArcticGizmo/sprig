namespace Sprig.Core.Docker;

/// <summary>A container's status as reported by <c>docker compose ps</c>.</summary>
public sealed record ContainerStatus(string Name, string State);

/// <summary>
/// Docker Compose operations for one instance. Every call carries the S2 invariants:
/// <c>-f &lt;central compose&gt;… --project-directory &lt;worktree&gt; -p sprig-&lt;workspace&gt;</c>. A repo may
/// contribute several generated compose files, so each call takes an ordered list — all passed as
/// repeated <c>-f</c> flags in one invocation, which merges them under the single project.
/// </summary>
public interface IDockerService
{
    /// <summary>True if the <c>docker compose</c> CLI is installed on this machine. Note this does
    /// NOT prove the engine is running — use <see cref="IsEngineRunning"/> for that.</summary>
    bool IsAvailable();

    /// <summary>True if the Docker engine/daemon is actually reachable (Docker Desktop running), not
    /// merely that the CLI is installed. This is the check that decides whether <c>up</c> will work.</summary>
    bool IsEngineRunning();

    /// <summary>
    /// <c>up -d</c>. With <paramref name="wait"/> it adds <c>--wait</c>, so the call doesn't return until
    /// every container is running (or healthy, where a healthcheck is declared) — what a shared resource
    /// needs before anyone runs a command against it.
    /// </summary>
    void Up(IReadOnlyList<string> composeFiles, string projectDirectory, string projectName, bool wait = false);

    /// <summary>
    /// Run a command inside one of the project's containers (<c>exec -T</c>, via the platform shell).
    /// Returns whether it succeeded and whatever it wrote, rather than throwing — attaching a slot is
    /// retried while a just-started database finishes coming up, and a failed attempt isn't yet a failure.
    /// </summary>
    (bool Success, string Output) Exec(IReadOnlyList<string> composeFiles, string projectDirectory,
        string projectName, string service, string command);

    /// <summary><c>down</c> keeps volumes; <paramref name="removeVolumes"/> = <c>down -v</c>.</summary>
    void Down(IReadOnlyList<string> composeFiles, string projectDirectory, string projectName, bool removeVolumes = false);

    IReadOnlyList<ContainerStatus> Ps(IReadOnlyList<string> composeFiles, string projectDirectory, string projectName);
}
