using Sprig.Core.Docker;

namespace Sprig.Tests;

/// <summary>A controllable <see cref="IDockerService"/> that records calls instead of touching docker.</summary>
public sealed class FakeDockerService : IDockerService
{
    public bool Available { get; set; } = true;
    public List<string> Ups { get; } = [];
    public List<(string project, bool volumes)> Downs { get; } = [];
    public List<ContainerStatus> PsResult { get; } = [];

    /// <summary>The compose-file lists passed to each Up/Down/Ps call, for fan-out assertions.</summary>
    public List<IReadOnlyList<string>> ComposeFilesSeen { get; } = [];

    public bool IsAvailable() => Available;

    public void Up(IReadOnlyList<string> composeFiles, string projectDirectory, string projectName)
    {
        ComposeFilesSeen.Add(composeFiles);
        Ups.Add(projectName);
    }

    public void Down(IReadOnlyList<string> composeFiles, string projectDirectory, string projectName, bool removeVolumes = false)
    {
        ComposeFilesSeen.Add(composeFiles);
        Downs.Add((projectName, removeVolumes));
    }

    public IReadOnlyList<ContainerStatus> Ps(IReadOnlyList<string> composeFiles, string projectDirectory, string projectName)
    {
        ComposeFilesSeen.Add(composeFiles);
        return PsResult;
    }
}
