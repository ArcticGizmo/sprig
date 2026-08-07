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
        Assert.Contains("unknown command", err);
    }

    [Fact]
    public void Ls_on_empty_store_is_friendly()
    {
        var (exit, o, _) = Run("ls");
        Assert.Equal(0, exit);
        Assert.Contains("no workspaces", o);
    }

    [Fact]
    public void Ls_json_on_empty_store_is_an_empty_array()
    {
        var (exit, o, _) = Run("ls", "--json");
        Assert.Equal(0, exit);
        Assert.Equal("[]", o.Trim());
    }

    [Fact]
    public void Unknown_flag_is_rejected()
    {
        var (exit, _, err) = Run("create", "foo", "--stack", "s", "--bogus");
        Assert.Equal(1, exit);
        Assert.Contains("unknown option", err);
    }

    [Fact]
    public void Rm_refuses_without_yes()
    {
        var (exit, _, err) = Run("rm", "nope");
        Assert.Equal(1, exit);
        Assert.Contains("--yes", err);
    }

    [Fact]
    public void Settings_roundtrip_through_show_json()
    {
        Assert.Equal(0, Run("settings", "set", "--start", "7000", "--end", "7500", "--restrict", "7100,7200").exit);
        var (exit, o, _) = Run("settings", "--json");
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
