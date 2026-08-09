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

    // Write a workspace record whose repos have real on-disk worktrees (so `cd` finds them), each with an
    // optional .sprig.json declaring modules. `modules` maps repo name → module (name, path) pairs.
    void SeedWorkspace(string workspace, params string[] repos)
        => SeedWorkspace(workspace, repos, modules: null);

    void SeedWorkspace(string workspace, string[] repos,
        IReadOnlyDictionary<string, (string Name, string Path)[]>? modules)
    {
        var instanceRepos = new List<InstanceRepo>();
        foreach (var name in repos)
        {
            var worktree = Path.Combine(_root, "worktrees", $"{name}--{workspace}");
            Directory.CreateDirectory(worktree);
            var mods = modules is not null && modules.TryGetValue(name, out var m) ? m : [];
            var modJson = string.Join(",", mods.Select(x =>
                $$"""{ "name": "{{x.Name}}", "path": "{{x.Path}}" }"""));
            File.WriteAllText(Path.Combine(worktree, ".sprig.json"),
                $$"""{ "schema": 3, "name": "{{name}}", "inputs": [], "modules": [{{modJson}}] }""");
            foreach (var mod in mods.Where(x => x.Path.Length > 0))
                Directory.CreateDirectory(Path.Combine(worktree, mod.Path.Replace('/', Path.DirectorySeparatorChar)));
            instanceRepos.Add(new InstanceRepo { Name = name, SourcePath = worktree, WorktreePath = worktree });
        }
        new InstanceStore(_paths).Save(new InstanceRecord { Workspace = workspace, Repos = instanceRepos });
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

    // -i and --ni are opposites. Asserting on "opposites" (not "Unknown option") also proves --ni is a
    // registered flag on each command — the shared Interactivity rule is wired in everywhere.
    [Theory]
    [InlineData("create")]
    [InlineData("rm")]
    public void Ws_verb_rejects_i_and_ni_together(string verb)
    {
        var (exit, _, err) = Run("ws", verb, "-i", "--ni");
        Assert.Equal(1, exit);
        Assert.Contains("opposites", err);
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

    // `sprig path` owns the resolution (workspace → repo → module) and prints the directory; `sprig cd`
    // is the window-opening front-end over the same resolver. Resolution is exercised here against `path`
    // so nothing spawns a terminal window under test.

    [Fact]
    public void Path_single_repo_resolves_the_worktree_path()
    {
        SeedWorkspace("feat", "api");
        var (exit, o, _) = Run("path", "feat");
        Assert.Equal(0, exit);
        Assert.EndsWith($"api--feat", o.Trim()); // one repo → implied, root module → worktree root
    }

    [Fact]
    public void Path_unknown_workspace_fails()
    {
        var (exit, _, err) = Run("path", "nope");
        Assert.Equal(1, exit);
        Assert.Contains("unknown workspace", err);
    }

    [Fact]
    public void Path_without_a_workspace_and_no_i_fails()
    {
        var (exit, _, err) = Run("path");
        Assert.Equal(1, exit);
        Assert.Contains("workspace is required", err);
    }

    [Fact]
    public void Path_multi_repo_needs_a_repo_named()
    {
        SeedWorkspace("feat", "api", "web");
        var (exit, _, err) = Run("path", "feat");
        Assert.Equal(1, exit);
        Assert.Contains("name one", err);
        Assert.Contains("api", err);
        Assert.Contains("web", err);
    }

    [Fact]
    public void Path_selects_a_named_repo()
    {
        SeedWorkspace("feat", "api", "web");
        var (exit, o, _) = Run("path", "feat", "web");
        Assert.Equal(0, exit);
        Assert.EndsWith("web--feat", o.Trim());
    }

    [Fact]
    public void Path_unknown_repo_lists_the_options()
    {
        SeedWorkspace("feat", "api", "web");
        var (exit, _, err) = Run("path", "feat", "ghost");
        Assert.Equal(1, exit);
        Assert.Contains("no repo 'ghost'", err);
    }

    [Fact]
    public void Path_resolves_a_module_subdirectory()
    {
        SeedWorkspace("feat", ["mono"],
            new Dictionary<string, (string, string)[]> { ["mono"] = [("web", "apps/web"), ("api", "apps/api")] });
        var (exit, o, _) = Run("path", "feat", "mono", "web");
        Assert.Equal(0, exit);
        var expected = Path.Combine("apps", "web");
        Assert.EndsWith(expected, o.Trim());
    }

    [Fact]
    public void Path_module_defaults_to_the_root()
    {
        SeedWorkspace("feat", ["mono"],
            new Dictionary<string, (string, string)[]> { ["mono"] = [("web", "apps/web")] });
        var (exit, o, _) = Run("path", "feat", "mono");
        Assert.Equal(0, exit);
        Assert.EndsWith("mono--feat", o.Trim()); // no module arg → worktree root, not apps/web
    }

    [Fact]
    public void Path_root_keyword_selects_the_worktree_root()
    {
        SeedWorkspace("feat", ["mono"],
            new Dictionary<string, (string, string)[]> { ["mono"] = [("web", "apps/web")] });
        var (exit, o, _) = Run("path", "feat", "mono", "root");
        Assert.Equal(0, exit);
        Assert.EndsWith("mono--feat", o.Trim());
    }

    [Fact]
    public void Path_emits_only_the_path()
    {
        SeedWorkspace("feat", "api");
        var (exit, o, _) = Run("path", "feat");
        Assert.Equal(0, exit);
        // A single clean line — nothing a script (or a `Set-Location (sprig path …)` wrapper) would trip on.
        Assert.Single(o.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void Path_unknown_module_lists_root_and_the_modules()
    {
        SeedWorkspace("feat", ["mono"],
            new Dictionary<string, (string, string)[]> { ["mono"] = [("web", "apps/web")] });
        var (exit, _, err) = Run("path", "feat", "mono", "ghost");
        Assert.Equal(1, exit);
        Assert.Contains("no module 'ghost'", err);
        Assert.Contains("(root)", err);
        Assert.Contains("web", err);
    }

    [Fact]
    public void Path_json_reports_the_resolved_target()
    {
        SeedWorkspace("feat", ["mono"],
            new Dictionary<string, (string, string)[]> { ["mono"] = [("web", "apps/web")] });
        var (exit, o, _) = Run("path", "feat", "mono", "web", "--json");
        Assert.Equal(0, exit);
        Assert.Contains("\"ok\": true", o);
        Assert.Contains("\"repo\": \"mono\"", o);
        Assert.Contains("\"module\": \"web\"", o);
    }

    [Fact]
    public void Path_interactive_refuses_without_a_terminal()
    {
        SeedWorkspace("feat", "api");
        var (exit, _, _) = Run("path", "-i");
        Assert.Equal(1, exit);
    }

    [Fact]
    public void Cd_without_a_workspace_and_no_i_fails()
    {
        var (exit, _, err) = Run("cd");
        Assert.Equal(1, exit);
        Assert.Contains("workspace is required", err);
    }

    [Fact]
    public void Cd_unknown_workspace_fails()
    {
        var (exit, _, err) = Run("cd", "nope");
        Assert.Equal(1, exit);
        Assert.Contains("unknown workspace", err);
    }

    [Fact]
    public void Cd_rejects_json() // cd has no machine output — `--json` isn't a flag it accepts
    {
        SeedWorkspace("feat", "api");
        var (exit, o, _) = Run("cd", "feat", "--json");
        Assert.Equal(1, exit);
        Assert.Contains("\"ok\": false", o);
    }

    [Fact]
    public void Cd_interactive_refuses_without_a_terminal()
    {
        SeedWorkspace("feat", "api");
        var (exit, _, _) = Run("cd", "-i");
        Assert.Equal(1, exit);
    }

    [Theory]
    [InlineData("cd")]
    [InlineData("path")]
    public void Navigate_rejects_i_and_ni_together(string cmd)
    {
        var (exit, _, err) = Run(cmd, "-i", "--ni");
        Assert.Equal(1, exit);
        Assert.Contains("opposites", err);
    }

    [Fact]
    public void Path_ni_forces_non_interactive() // --ni parses and keeps it non-interactive: it errors, never prompts
    {
        SeedWorkspace("feat", "api", "web");
        var (exit, _, err) = Run("path", "feat", "--ni");
        Assert.Equal(1, exit);
        Assert.Contains("name one", err); // resolved from args, not asked — a multi-repo workspace can't be inferred
    }

    // The single-target verbs take an optional workspace: named runs straight through, omitted picks at a
    // terminal. Under the test host stdin isn't a terminal, so an omitted name must fail rather than block.
    [Theory]
    [InlineData("up")]
    [InlineData("down")]
    [InlineData("reset")]
    [InlineData("status")]
    [InlineData("info")]
    public void Single_workspace_verb_without_a_name_and_no_terminal_fails(string verb)
    {
        var (exit, _, err) = Run("ws", verb);
        Assert.Equal(1, exit);
        Assert.Contains("workspace is required", err);
    }

    [Fact]
    public void Named_single_workspace_verb_bypasses_the_picker()
    {
        // A named verb resolves the positional directly (no pick), so an unknown name reports as such
        // rather than falling into the "requires a workspace" prompt path.
        var (exit, _, err) = Run("ws", "info", "nope");
        Assert.Equal(1, exit);
        Assert.Contains("unknown workspace", err);
    }
}
