using Sprig.Core.Processes;

namespace Sprig.Tests.Processes;

public class ProcessRunnerTests
{
    readonly ProcessRunner _runner = new();

    [Fact]
    public void Captures_stdout_and_zero_exit()
    {
        // `dotnet --version` is guaranteed present in this environment.
        var r = _runner.Run("dotnet", ["--version"]);

        Assert.True(r.Success);
        Assert.Equal(0, r.ExitCode);
        Assert.Matches(@"\d+\.\d+\.\d+", r.StdOut.Trim());
    }

    [Fact]
    public void Non_zero_exit_is_reported_and_EnsureSuccess_throws()
    {
        // An unknown subcommand makes git exit non-zero and write to stderr.
        var r = _runner.Run("git", ["not-a-real-command"]);

        Assert.False(r.Success);
        Assert.NotEqual(0, r.ExitCode);
        var ex = Assert.Throws<ProcessException>(() => r.EnsureSuccess());
        Assert.Contains("command failed", ex.Message);
    }

    [Fact]
    public void Missing_executable_throws_ProcessException()
        => Assert.Throws<ProcessException>(
            () => _runner.Run("definitely-not-an-exe-" + Guid.NewGuid().ToString("N"), []));

    [Fact]
    public void Runs_in_the_given_working_directory()
    {
        var temp = Directory.CreateTempSubdirectory("sprig-proc-");
        try
        {
            // git rev-parse from a non-repo temp dir exits non-zero — proves cwd was honoured.
            var r = _runner.Run("git", ["rev-parse", "--is-inside-work-tree"], temp.FullName);
            Assert.False(r.Success);
        }
        finally { temp.Delete(recursive: true); }
    }
}
