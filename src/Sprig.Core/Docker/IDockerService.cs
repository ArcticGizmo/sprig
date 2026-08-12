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

    void Up(IReadOnlyList<string> composeFiles, string projectDirectory, string projectName);

    /// <summary><c>down</c> keeps volumes; <paramref name="removeVolumes"/> = <c>down -v</c>.</summary>
    void Down(IReadOnlyList<string> composeFiles, string projectDirectory, string projectName, bool removeVolumes = false);

    /// <summary><c>stop</c> halts the project's containers without removing them: frees CPU/RAM but
    /// leaves the containers, networks and volumes in place, so a later <c>up</c> restarts them quickly.
    /// Unlike <see cref="Down"/>, nothing is torn down.</summary>
    void Stop(IReadOnlyList<string> composeFiles, string projectDirectory, string projectName);

    IReadOnlyList<ContainerStatus> Ps(IReadOnlyList<string> composeFiles, string projectDirectory, string projectName);
}
