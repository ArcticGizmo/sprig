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
    /// <summary>True if <c>docker compose</c> is usable on this machine.</summary>
    bool IsAvailable();

    void Up(IReadOnlyList<string> composeFiles, string projectDirectory, string projectName);

    /// <summary><c>down</c> keeps volumes; <paramref name="removeVolumes"/> = <c>down -v</c>.</summary>
    void Down(IReadOnlyList<string> composeFiles, string projectDirectory, string projectName, bool removeVolumes = false);

    IReadOnlyList<ContainerStatus> Ps(IReadOnlyList<string> composeFiles, string projectDirectory, string projectName);
}
