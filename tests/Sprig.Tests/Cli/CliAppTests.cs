using Sprig.Cli;

namespace Sprig.Tests.Cli;

[Collection("cli-console")]
public class CliAppTests
{
    static (int code, string stdout, string stderr) Run(params string[] args)
    {
        using var store = new TempStore();
        var (prevOut, prevErr) = (Console.Out, Console.Error);
        var (stdout, stderr) = (new StringWriter(), new StringWriter());
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            var code = CliApp.Run(args, store.Paths);
            return (code, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(prevOut);
            Console.SetError(prevErr);
        }
    }

    [Fact]
    public void Help_lists_only_the_commands_this_build_has()
    {
        var (code, stdout, _) = Run("--help");

        Assert.Equal(0, code);
        Assert.Contains("open", stdout);
        Assert.Contains("update", stdout);
        Assert.Contains("shell", stdout);
        Assert.DoesNotContain("pool", stdout);
        Assert.DoesNotContain("map", stdout);
    }

    [Fact]
    public void An_unknown_command_fails_with_exit_code_1_and_a_json_error_when_asked()
    {
        var (code, stdout, _) = Run("ws", "ls", "--json");

        Assert.Equal(1, code);
        Assert.Contains("\"ok\": false", stdout);
    }
}
