using Sprig.Engine.Compose;
using Sprig.Engine.Processes;

namespace Sprig.Tests.Compose;

public class DockerComposeUpTests
{
    /// <summary>Answers each docker call by its subcommand: up, ps or logs.</summary>
    sealed class FakeDocker(ProcessResult up, string ps, string logs = "") : IProcessRunner
    {
        public ProcessResult Run(string executable, IReadOnlyList<string> arguments, string? workingDirectory = null,
            CancellationToken cancellationToken = default, Action<string>? onOutput = null)
            => arguments.Contains("up") ? up
            : arguments.Contains("ps") ? new ProcessResult(executable, arguments, 0, ps, "")
            : arguments.Contains("logs") ? new ProcessResult(executable, arguments, 0, logs, "")
            : new ProcessResult(executable, arguments, 0, "", "");
    }

    static readonly ComposeProject Project = new("sprig-shop-feat-a-services", "missing.yml", ".");

    static ProcessResult FailedUp(string stderr)
        => new("docker", ["compose", "up"], 1, "", stderr);

    [Fact]
    public void A_service_that_stopped_is_named_with_its_exit_code_and_the_end_of_its_log()
    {
        var ps = """
            {"Service":"postgres","State":"running","Health":"healthy","ExitCode":0}
            {"Service":"rabbitmq","State":"exited","Health":"","ExitCode":1}
            """;
        var logs = string.Join("\n", Enumerable.Range(1, 30).Select(i => $"line {i}")) + "\nError when reading /var/lib/rabbitmq/.erlang.cookie: eacces";
        var docker = new DockerCompose(new FakeDocker(FailedUp(" Container rabbitmq-1 Starting\ncontainer rabbitmq-1 exited (1)"), ps, logs));

        var e = Assert.Throws<ComposeUpException>(() => docker.Up(Project, null, default));

        Assert.StartsWith("rabbitmq stopped while starting (exit 1).", e.Message);
        Assert.DoesNotContain("postgres", e.Message);
        Assert.Contains("The end of rabbitmq's log:", e.Message);
        Assert.Contains("eacces", e.Message);
        Assert.DoesNotContain("line 1\n", e.Message);
        Assert.DoesNotContain("docker compose", e.Message);
    }

    [Fact]
    public void A_health_check_that_never_passed_says_so()
    {
        var ps = """[{"Service":"web","State":"running","Health":"unhealthy","ExitCode":0}]""";
        var docker = new DockerCompose(new FakeDocker(FailedUp("container web-1 is unhealthy"), ps));

        var e = Assert.Throws<ComposeUpException>(() => docker.Up(Project, null, default));

        Assert.Equal("web is running, but its health check keeps failing.", e.Message);
    }

    [Fact]
    public void When_nothing_started_badly_compose_s_own_error_is_the_reason()
    {
        var docker = new DockerCompose(new FakeDocker(
            FailedUp(" Network x Creating\nError response from daemon: Bind for 127.0.0.1:8002 failed: port is already allocated"), ""));

        var e = Assert.Throws<ComposeUpException>(() => docker.Up(Project, null, default));

        Assert.Equal("docker compose couldn't start sprig-shop-feat-a-services: Error response from daemon: Bind for 127.0.0.1:8002 failed: port is already allocated", e.Message);
    }
}
