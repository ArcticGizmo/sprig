using Sprig.Core.Docker;

namespace Sprig.Tests;

/// <summary>A controllable <see cref="IDockerService"/> that records calls instead of touching docker.</summary>
public sealed class FakeDockerService : IDockerService
{
    public bool Available { get; set; } = true;

    /// <summary>Whether the (fake) engine is reachable. Independent of <see cref="Available"/> so a
    /// test can model "CLI installed but Docker Desktop stopped".</summary>
    public bool EngineRunning { get; set; } = true;
    public List<string> Ups { get; } = [];
    public List<(string project, bool volumes)> Downs { get; } = [];
    public List<ContainerStatus> PsResult { get; } = [];

    /// <summary>The compose-file lists passed to each Up/Down/Ps call, for fan-out assertions.</summary>
    public List<IReadOnlyList<string>> ComposeFilesSeen { get; } = [];

    public bool IsAvailable() => Available;
    public bool IsEngineRunning() => Available && EngineRunning;

    /// <summary>Every exec issued, so a test can assert what attach/detach actually ran.</summary>
    public List<(string project, string service, string command)> Execs { get; } = [];

    /// <summary>Commands matching this predicate fail; everything else succeeds.</summary>
    public Func<string, bool> ExecFails { get; set; } = _ => false;

    /// <summary>Projects whose containers `Ps` should report as running, keyed by project name.</summary>
    public HashSet<string> RunningProjects { get; } = [];

    public void Up(IReadOnlyList<string> composeFiles, string projectDirectory, string projectName, bool wait = false)
    {
        ComposeFilesSeen.Add(composeFiles);
        Ups.Add(projectName);
        RunningProjects.Add(projectName);
    }

    public (bool Success, string Output) Exec(IReadOnlyList<string> composeFiles, string projectDirectory,
        string projectName, string service, string command)
    {
        Execs.Add((projectName, service, command));
        return ExecFails(command) ? (false, "boom") : (true, "");
    }

    public void Down(IReadOnlyList<string> composeFiles, string projectDirectory, string projectName, bool removeVolumes = false)
    {
        ComposeFilesSeen.Add(composeFiles);
        Downs.Add((projectName, removeVolumes));
        RunningProjects.Remove(projectName);
    }

    public IReadOnlyList<ContainerStatus> Ps(IReadOnlyList<string> composeFiles, string projectDirectory, string projectName)
    {
        ComposeFilesSeen.Add(composeFiles);
        if (PsResult.Count > 0) return PsResult;
        return RunningProjects.Contains(projectName) ? [new ContainerStatus($"{projectName}-svc", "running")] : [];
    }
}
