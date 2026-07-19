using Sprig.Core.Docker;

namespace Sprig.Tests;

/// <summary>A controllable <see cref="IDockerService"/> that records calls instead of touching docker.</summary>
public sealed class FakeDockerService : IDockerService
{
    public bool Available { get; set; } = true;
    public List<string> Ups { get; } = [];
    public List<(string project, bool volumes)> Downs { get; } = [];
    public List<ContainerStatus> PsResult { get; } = [];

    public bool IsAvailable() => Available;

    public void Up(string composeFile, string projectDirectory, string projectName) => Ups.Add(projectName);

    public void Down(string composeFile, string projectDirectory, string projectName, bool removeVolumes = false)
        => Downs.Add((projectName, removeVolumes));

    public IReadOnlyList<ContainerStatus> Ps(string composeFile, string projectDirectory, string projectName) => PsResult;
}
