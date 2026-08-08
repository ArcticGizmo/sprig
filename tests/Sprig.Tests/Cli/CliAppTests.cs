using Sprig.Cli;
using Sprig.Core.Stacks;
using Sprig.Core.Store;

namespace Sprig.Tests.Cli;

// Console capture swaps the process-global Console.Out/Error, so this collection must not run in
// parallel with anything else that writes to the console.
[CollectionDefinition("cli-console", DisableParallelization = true)]
public sealed class CliConsoleCollection { }

/// <summary>End-to-end dispatch tests: drive <see cref="CliApp"/> through its internal
/// (args, ISprigPaths) seam against a throwaway store and assert on exit code + captured output.</summary>
[Collection("cli-console")]
public sealed class CliAppTests : IDisposable
{
    readonly string _root;
    readonly SprigPaths _paths;

    public CliAppTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sprig-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _paths = new SprigPaths(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    (int exit, string @out, string err) Run(params string[] args)
    {
        var outW = new StringWriter();
        var errW = new StringWriter();
        var (prevOut, prevErr) = (Console.Out, Console.Error);
        Console.SetOut(outW);
        Console.SetError(errW);
        try { return (CliApp.Run(args, _paths), outW.ToString(), errW.ToString()); }
        finally { Console.SetOut(prevOut); Console.SetError(prevErr); }
    }

    // Register a repo with the minimum a stack needs: a directory holding a valid .sprig.json.
    void SeedRepo(string name)
    {
        var dir = Path.Combine(_root, "repos", name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".sprig.json"),
            $$"""{ "schema": 3, "name": "{{name}}", "inputs": [], "modules": [] }""");
        new RepoRegistryStore(_paths).Add(dir, name);
    }

    [Fact]
    public void Version_prints_and_exits_zero()
    {
        var (exit, o, _) = Run("--version");
        Assert.Equal(0, exit);
        Assert.False(string.IsNullOrWhiteSpace(o));
    }

    [Fact]
    public void Unknown_command_exits_one()
    {
        var (exit, _, err) = Run("wibble");
        Assert.Equal(1, exit);
        Assert.Contains("Unknown command", err);
    }

    [Fact]
    public void Ls_on_empty_store_is_friendly()
    {
        var (exit, o, _) = Run("ws", "ls");
        Assert.Equal(0, exit);
        Assert.Contains("no workspaces", o);
    }

    [Fact]
    public void Ls_json_on_empty_store_is_an_empty_array()
    {
        var (exit, o, _) = Run("ws", "ls", "--json");
        Assert.Equal(0, exit);
        Assert.Equal("[]", o.Trim());
    }

    [Fact]
    public void Unknown_flag_is_rejected()
    {
        // Strict parsing: a typo'd flag fails loudly rather than being silently ignored.
        var (exit, _, err) = Run("ws", "ls", "--bogus");
        Assert.Equal(1, exit);
        Assert.Contains("Unknown option", err);
    }

    [Fact]
    public void Workspace_verbs_are_only_under_ws()
    {
        // The verbs were removed from the top level to keep it uncluttered — a bare `ls` is now unknown,
        // while `ws ls` is the one true home.
        Assert.Equal(1, Run("ls").exit);
        Assert.Equal(0, Run("ws", "ls").exit);
    }

    [Fact]
    public void Ws_with_no_verb_shows_its_workspace_verbs()
    {
        // The `ws` branch on its own prints its command list (Spectre help) rather than running a verb.
        var (_, o, _) = Run("ws");
        Assert.Contains("COMMANDS", o);
        Assert.Contains("create", o);
        Assert.Contains("reconcile", o);
    }

    [Fact]
    public void Ws_create_without_a_name_fails()
    {
        // The name argument is optional (so -i can omit it), but a non-interactive create still needs one.
        var (exit, _, err) = Run("ws", "create");
        Assert.Equal(1, exit);
        Assert.Contains("requires a workspace name", err);
    }

    [Fact]
    public void Interactive_create_rejects_json()
    {
        // -i drives prompts; pairing it with the machine-output flag is nonsense, so it's refused up front.
        var (exit, o, _) = Run("ws", "create", "-i", "--json");
        Assert.Equal(1, exit);
        Assert.Contains("\"ok\": false", o);
    }

    [Fact]
    public void Interactive_create_refuses_without_a_terminal()
    {
        // Under the test host stdin isn't a terminal, so -i must bail (never block waiting on a prompt).
        var (exit, _, _) = Run("ws", "create", "-i");
        Assert.Equal(1, exit);
    }

    [Fact]
    public void Ws_rejects_a_non_workspace_verb()
    {
        // `stack` is a top-level namespace, not a workspace verb, so it's unknown under `ws`.
        var (exit, _, err) = Run("ws", "stack");
        Assert.Equal(1, exit);
        Assert.Contains("Unknown command", err);
    }

    [Theory]
    [InlineData("stack", "import")]
    [InlineData("repo", "add")]
    [InlineData("settings", "set")]
    public void Namespace_help_lists_its_own_subcommands(string ns, string marker)
    {
        var (exit, o, _) = Run(ns, "--help");
        Assert.Equal(0, exit);
        Assert.Contains("COMMANDS", o);    // Spectre's hierarchical help for the branch
        Assert.Contains(marker, o);        // one of the branch's own subcommands
        Assert.Contains($"sprig {ns}", o); // the USAGE line names the namespace (plain when redirected)
    }

    [Fact]
    public void Namespace_help_does_not_run_the_default_subcommand()
    {
        // `stack --help` prints the branch help; it must not fall through to a subcommand.
        var (exit, o, _) = Run("stack", "--help");
        Assert.Equal(0, exit);
        Assert.Contains("COMMANDS", o);
        Assert.DoesNotContain("no stacks defined", o);
    }

    [Fact]
    public void Rm_refuses_without_yes()
    {
        var (exit, _, err) = Run("ws", "rm", "nope");
        Assert.Equal(1, exit);
        Assert.Contains("--yes", err);
    }

    [Fact]
    public void Interactive_rm_rejects_json()
    {
        var (exit, o, _) = Run("ws", "rm", "-i", "--json");
        Assert.Equal(1, exit);
        Assert.Contains("\"ok\": false", o);
    }

    [Fact]
    public void Interactive_rm_refuses_without_a_terminal()
    {
        // stdin isn't a terminal under the test host, so -i must bail rather than block on a prompt.
        var (exit, _, _) = Run("ws", "rm", "-i");
        Assert.Equal(1, exit);
    }

    [Fact]
    public void Settings_roundtrip_through_show_json()
    {
        Assert.Equal(0, Run("settings", "set", "--start", "7000", "--end", "7500", "--restrict", "7100,7200").exit);
        var (exit, o, _) = Run("settings", "show", "--json");
        Assert.Equal(0, exit);
        Assert.Contains("\"portRangeStart\": 7000", o);
        Assert.Contains("7100", o);
    }

    [Fact]
    public void Settings_accepts_equals_form_and_reports_validation_errors_as_json()
    {
        Assert.Equal(0, Run("settings", "set", "--start=8000", "--end=9000").exit);
        var (exit, o, _) = Run("settings", "set", "--start", "9000", "--end", "8000", "--json");
        Assert.Equal(1, exit);
        Assert.Contains("\"ok\": false", o);
    }

    [Fact]
    public void Stack_create_derives_shares_and_rejects_a_duplicate_name()
    {
        SeedRepo("alpha");
        SeedRepo("beta");
        Assert.Equal(0, Run("stack", "create", "demo", "--repos", "alpha,beta", "--port", "shared",
            "--bind", "alpha:p=${sprig.ports.shared}", "--bind", "beta:p=${sprig.ports.shared}").exit);

        var dup = Run("stack", "create", "demo", "--repos", "alpha");
        Assert.Equal(1, dup.exit);
        Assert.Contains("already exists", dup.err);

        var show = Run("stack", "show", "demo");
        Assert.Contains("shared port shared", show.@out); // both repos bind it → derived share
    }

    [Fact]
    public void Stack_edit_merges_facets_and_guards_the_missing_case()
    {
        SeedRepo("alpha");
        SeedRepo("beta");
        Run("stack", "create", "demo", "--repos", "alpha,beta", "--port", "shared",
            "--bind", "alpha:p=${sprig.ports.shared}", "--bind", "beta:p=${sprig.ports.shared}");

        // Repoint beta at a new port; alpha's binding is untouched, so the port is no longer shared.
        Assert.Equal(0, Run("stack", "edit", "demo", "--port", "shared", "--port", "extra",
            "--bind", "beta:p=${sprig.ports.extra}").exit);

        var show = Run("stack", "show", "demo");
        Assert.Contains("extra", show.@out);
        Assert.DoesNotContain("shared port shared", show.@out);

        var ghost = Run("stack", "edit", "ghost");
        Assert.Equal(1, ghost.exit);
        Assert.Contains("unknown stack", ghost.err);
    }
}
