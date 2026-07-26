using Sprig.Core.Processes;
using Sprig.Core.Setup;

namespace Sprig.Tests.Setup;

public class SetupRunnerTests
{
    /// <summary>A runner whose result is decided per-invocation by the last argument (the command),
    /// so a test can make specific commands fail while recording every call.</summary>
    sealed class ScriptedRunner(Func<string, (int exit, string stdout, string stderr)> script) : IProcessRunner
    {
        public List<RecordingProcessRunner.Invocation> Calls { get; } = [];

        public ProcessResult Run(string executable, IReadOnlyList<string> arguments,
            string? workingDirectory = null, CancellationToken ct = default, Action<string>? onOutput = null)
        {
            Calls.Add(new RecordingProcessRunner.Invocation(executable, arguments, workingDirectory));
            var command = arguments[^1];
            var (exit, stdout, stderr) = script(command);
            if (onOutput is not null)
                foreach (var line in (stdout + stderr).Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    onOutput(line.TrimEnd('\r'));
            return new ProcessResult(executable, arguments, exit, stdout, stderr);
        }
    }

    static readonly (string exe, string flag) Shell =
        OperatingSystem.IsWindows() ? ("cmd.exe", "/c") : ("/bin/sh", "-c");

    [Fact]
    public void Runs_each_command_via_the_platform_shell_in_the_working_dir()
    {
        var runner = new ScriptedRunner(_ => (0, "ok", ""));
        var setup = new SetupRunner(runner);

        var outcomes = setup.Run(["npm ci", "dotnet restore"], @"C:\work\wt");

        Assert.Equal(2, runner.Calls.Count);
        Assert.All(runner.Calls, c =>
        {
            Assert.Equal(Shell.exe, c.Executable);
            Assert.Equal(Shell.flag, c.Arguments[0]);
            Assert.Equal(@"C:\work\wt", c.WorkingDirectory);
        });
        Assert.Equal("npm ci", runner.Calls[0].Arguments[1]);
        Assert.Equal("dotnet restore", runner.Calls[1].Arguments[1]);

        Assert.Equal(new[] { "npm ci", "dotnet restore" }, outcomes.Select(o => o.Command));
        Assert.All(outcomes, o => Assert.True(o.Success));
    }

    [Fact]
    public void A_failing_command_does_not_throw_and_stops_the_sequence()
    {
        var runner = new ScriptedRunner(cmd => cmd == "boom" ? (1, "", "kaboom") : (0, "ok", ""));
        var setup = new SetupRunner(runner);

        var outcomes = setup.Run(["ok-1", "boom", "never"], "wd");

        // ran the first two, stopped before "never"
        Assert.Equal(new[] { "ok-1", "boom" }, outcomes.Select(o => o.Command));
        Assert.True(outcomes[0].Success);
        Assert.False(outcomes[1].Success);
        Assert.Equal(1, outcomes[1].ExitCode);
        Assert.Contains("kaboom", outcomes[1].Output);
        Assert.DoesNotContain(runner.Calls, c => c.Arguments[1] == "never");
    }

    [Fact]
    public void Blank_commands_are_skipped()
    {
        var runner = new ScriptedRunner(_ => (0, "", ""));
        var setup = new SetupRunner(runner);

        var outcomes = setup.Run(["", "   ", "real"], "wd");

        Assert.Single(outcomes);
        Assert.Equal("real", outcomes[0].Command);
    }

    [Fact]
    public void A_shell_that_cannot_start_is_captured_as_a_failed_outcome_not_thrown()
    {
        var runner = new ScriptedRunner(_ =>
            throw new ProcessException(new ProcessResult("cmd", [], 127, "", "not found")));
        var setup = new SetupRunner(runner);

        var outcomes = setup.Run(["whatever"], "wd");

        Assert.Single(outcomes);
        Assert.False(outcomes[0].Success);
    }

    [Fact]
    public void Empty_command_list_returns_no_outcomes()
        => Assert.Empty(new SetupRunner(new ScriptedRunner(_ => (0, "", ""))).Run([], "wd"));
}
