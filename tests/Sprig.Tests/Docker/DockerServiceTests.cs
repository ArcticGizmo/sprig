using Sprig.Core.Docker;
using Sprig.Core.Processes;

namespace Sprig.Tests.Docker;

public class DockerServiceTests
{
    const string Compose = @"C:\store\instances\ws\docker-compose.sprig.yml";
    const string Compose2 = @"C:\store\instances\ws\docker-compose.web.sprig.yml";
    const string WorktreeDir = @"C:\repos\app--ws";
    const string Project = "sprig-ws";

    static string[] Files(params string[] f) => f;

    [Fact]
    public void Up_uses_project_directory_and_project_name()
    {
        var runner = new RecordingProcessRunner();
        new DockerService(runner).Up(Files(Compose), WorktreeDir, Project);

        Assert.Equal("docker", runner.Last.Executable);
        Assert.Equal(
            ["compose", "-f", Compose, "--project-directory", WorktreeDir, "-p", Project, "up", "-d"],
            runner.Last.Arguments);
        Assert.Equal(WorktreeDir, runner.Last.WorkingDirectory);
    }

    [Fact]
    public void Up_passes_every_compose_file_as_its_own_dash_f()
    {
        var runner = new RecordingProcessRunner();
        new DockerService(runner).Up(Files(Compose, Compose2), WorktreeDir, Project);

        Assert.Equal(
            ["compose", "-f", Compose, "-f", Compose2, "--project-directory", WorktreeDir, "-p", Project, "up", "-d"],
            runner.Last.Arguments);
    }

    [Fact]
    public void Down_keeps_volumes_by_default()
    {
        var runner = new RecordingProcessRunner();
        new DockerService(runner).Down(Files(Compose), WorktreeDir, Project);
        Assert.Equal(["-p", Project, "down"], runner.Last.Arguments.TakeLast(3));
        Assert.DoesNotContain("-v", runner.Last.Arguments);
    }

    [Fact]
    public void Down_with_removeVolumes_adds_dash_v()
    {
        var runner = new RecordingProcessRunner();
        new DockerService(runner).Down(Files(Compose), WorktreeDir, Project, removeVolumes: true);
        Assert.Equal(["down", "-v"], runner.Last.Arguments.TakeLast(2));
    }

    [Fact]
    public void Non_zero_exit_throws()
    {
        var runner = new RecordingProcessRunner { ExitCode = 1, StdErr = "boom" };
        Assert.Throws<ProcessException>(() => new DockerService(runner).Up(Files(Compose), WorktreeDir, Project));
    }

    [Fact]
    public void Ps_parses_ndjson_and_array_forms()
    {
        var nd = "{\"Name\":\"a\",\"State\":\"running\"}\n{\"Name\":\"b\",\"Status\":\"exited\"}";
        var arr = "[{\"Name\":\"a\",\"State\":\"running\"},{\"Name\":\"b\",\"State\":\"paused\"}]";

        var fromNd = DockerService.ParsePs(nd);
        Assert.Equal(["a", "b"], fromNd.Select(c => c.Name));
        Assert.Equal("exited", fromNd[1].State); // falls back to Status

        var fromArr = DockerService.ParsePs(arr);
        Assert.Equal(2, fromArr.Count);
        Assert.Equal("paused", fromArr[1].State);

        Assert.Empty(DockerService.ParsePs("   "));
        Assert.Empty(DockerService.ParsePs("not json")); // tolerant
    }

    [Fact]
    public void IsAvailable_reflects_real_docker()
    {
        // docker is present in this environment (29.x); just assert it runs without throwing.
        var available = new DockerService(new ProcessRunner()).IsAvailable();
        Assert.True(available);
    }
}
