using Sprig.Cli;
using Sprig.Engine.Compose;
using Sprig.Engine.Processes;
using Sprig.Engine.Store;
using Sprig.Engine.Workspaces;

namespace Sprig.Tests.Cli;

public class DockerUnavailableTests
{
    static ProcessResult Docker(int exit, string stderr, string exe = "docker") => new(exe, ["compose", "up"], exit, "", stderr);

    [Theory]
    [InlineData("error during connect: Get \"http://%2F%2F.%2Fpipe%2FdockerDesktopLinuxEngine/v1.47/info\": open //./pipe/dockerDesktopLinuxEngine: The system cannot find the file specified.")]
    [InlineData("Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?")]
    [InlineData("could not start 'docker': An error occurred trying to start process 'docker'. The system cannot find the file specified.")]
    public void Docker_not_answering_is_told_apart_from_a_failure_of_what_it_was_asked(string stderr)
    {
        // A docker that couldn't be started at all comes back as exit -1.
        var result = Docker(stderr.StartsWith("could not", StringComparison.Ordinal) ? -1 : 1, stderr);

        Assert.True(DockerCompose.Unreachable(result));
        Assert.Equal(CliErrors.DockerUnavailable, CliErrors.Code(new ProcessException(result)));
        Assert.Equal(CliErrors.DockerUnavailable, CliErrors.Code(new InvalidOperationException("starting infra", new ComposeUpException("x", result))));
    }

    [Fact]
    public void Other_failures_keep_their_own_code()
    {
        var portTaken = Docker(1, "Error response from daemon: Bind for 0.0.0.0:5432 failed: port is already allocated");

        Assert.False(DockerCompose.Unreachable(portTaken));
        Assert.False(DockerCompose.Unreachable(Docker(1, "Cannot connect to the Docker daemon", exe: "git")));
        Assert.Equal(CliErrors.Error, CliErrors.Code(new ProcessException(portTaken)));
        Assert.Equal(CliErrors.DockerUnavailable, CliErrors.Code(new WorkspaceException("docker isn't running") { Code = DockerCompose.UnavailableCode }));
    }

    [Fact]
    public void The_app_counts_as_running_while_its_claim_on_the_store_is_held()
    {
        using var store = new TempStore();
        Assert.False(AppInstance.IsRunning(store.Paths));

        using var claim = new Mutex(initiallyOwned: true, AppInstance.MutexName(store.Paths));

        Assert.True(AppInstance.IsRunning(store.Paths));
    }
}
