using Sprig.Cli;
using Sprig.Cli.Commands;
using Sprig.Engine.Processes;
using Sprig.Engine.Store;
using Sprig.Engine.Workspaces;

namespace Sprig.Tests.Cli;

/// <summary>The hooks the Claude Code plugins run, against real git repos in a temp folder. No infra, so docker is never
/// called.</summary>
[Collection("cli-console")]
public sealed class HookCommandTests : IDisposable
{
    readonly TempStore _store = new();
    readonly string _root = LongPath.Of(Path.Combine(Path.GetTempPath(), "sprig-hooks-" + Guid.NewGuid().ToString("N")));
    readonly ProcessRunner _processes = new();

    public HookCommandTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _store.Dispose();
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal); // git marks its objects read-only
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    (int code, string stdout, string stderr) Run(string? stdin, params string[] args)
    {
        var (prevOut, prevErr) = (Console.Out, Console.Error);
        var (stdout, stderr) = (new StringWriter(), new StringWriter());
        Console.SetOut(stdout);
        Console.SetError(stderr);
        HookCommand.Input = stdin is null ? null : new StringReader(stdin);
        try
        {
            var code = CliApp.Run(args, _store.Paths);
            return (code, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            HookCommand.Input = null;
            Console.SetOut(prevOut);
            Console.SetError(prevErr);
        }
    }

    static string Json(params (string key, string value)[] fields)
        => System.Text.Json.JsonSerializer.Serialize(fields.ToDictionary(f => f.key, f => f.value));

    static string LastLine(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Last().Trim();

    string Repo(string name, string? sprigYaml = null)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        Git(dir, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(dir, ".gitignore"), ".env\n.claude/\n");
        File.WriteAllText(Path.Combine(dir, "README.md"), "hello\n");
        if (sprigYaml is not null) File.WriteAllText(Path.Combine(dir, "sprig.yaml"), sprigYaml);
        Git(dir, "add", "-A");
        Git(dir, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", "init");
        return dir;
    }

    void Git(string dir, params string[] args) => _processes.Run("git", ["-C", dir, .. args]).EnsureSuccess();

    const string ShopServices = """
        schema: 1
        repo: shop-services
        apps:
          api:
            run: command
            command: node server.js
            ports: { http: any }
        files:
          .env:
            read-by: [api]
            values:
              PORT: ${api.port}
        """;

    [Fact]
    public void A_contract_this_sprig_doesnt_serve_is_refused_saying_which_side_to_update()
    {
        var (code, _, stderr) = Run(Json(("cwd", _root), ("name", "a")), "hook", "worktree-create", "--contract", "99");
        Assert.Equal(2, code);
        Assert.Contains("sprig update", stderr);

        (code, _, stderr) = Run(Json(("cwd", _root), ("name", "a")), "hook", "worktree-create", "--contract", "0");
        Assert.Equal(2, code);
        Assert.Contains("/plugin", stderr);

        // A session still starts.
        Assert.Equal(0, Run(Json(("cwd", _root)), "hook", "session-start", "--contract", "99").code);
    }

    [Fact]
    public void Outside_a_repo_sprig_knows_the_worktree_is_the_plain_one_claude_code_would_make_and_removing_it_takes_its_branch()
    {
        var repo = Repo("notes");

        var (code, stdout, stderr) = Run(Json(("cwd", repo), ("name", "bold-oak")), "hook", "worktree-create", "--contract", "1");

        Assert.True(code == 0, stderr);
        var path = LastLine(stdout);
        Assert.Equal(Path.Combine(repo, ".claude", "worktrees", "bold-oak"), path, ignoreCase: true);
        Assert.True(File.Exists(Path.Combine(path, "README.md")));
        Assert.Contains("worktree-bold-oak", _processes.Run("git", ["-C", repo, "branch"]).StdOut);

        (code, _, stderr) = Run(Json(("cwd", repo), ("worktree_path", path)), "hook", "worktree-remove", "--contract", "1");

        Assert.True(code == 0, stderr);
        Assert.False(Directory.Exists(path));
        Assert.DoesNotContain("worktree-bold-oak", _processes.Run("git", ["-C", repo, "branch"]).StdOut);
    }

    [Fact]
    public void In_a_repo_sprig_knows_the_worktree_is_a_slot_made_ready_on_its_own_branch_and_removing_it_frees_the_slot()
    {
        var repo = Repo("shop-services", ShopServices);
        Assert.Equal(0, Run(null, "repo", "add", repo).code);

        // Nobody approved this sprig.yaml, and a hook can't ask.
        var (code, _, stderr) = Run(Json(("cwd", repo), ("name", "calm-fox"), ("session_id", "s-1")), "hook", "worktree-create", "--contract", "1");
        Assert.Equal(1, code);
        Assert.Contains("sprig trust add", stderr);

        Assert.Equal(0, Run(null, "trust", "add", Path.Combine(repo, "sprig.yaml")).code);
        (code, var stdout, stderr) = Run(Json(("cwd", repo), ("name", "calm-fox"), ("session_id", "s-1")), "hook", "worktree-create", "--contract", "1");

        Assert.True(code == 0, stderr);
        var path = LastLine(stdout);
        Assert.Single(stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("PORT=", File.ReadAllText(Path.Combine(path, ".env")));
        var record = new WorkspaceStore(_store.Paths).List().Single(r => !r.IsMain);
        Assert.Equal("worktree-calm-fox", record.Branch);
        Assert.Equal("s-1", record.MadeForAgent);
        Assert.Equal(WorkspaceState.Up, record.State);

        (code, _, stderr) = Run(Json(("cwd", path), ("worktree_path", path)), "hook", "worktree-remove", "--contract", "1");

        Assert.True(code == 0, stderr);
        Assert.True(new WorkspaceStore(_store.Paths).Get(record.Orchestration, record.Name)!.IsFree);
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public void Free_agent_made_frees_the_slots_hooks_made_and_leaves_the_ones_people_made()
    {
        var repo = Repo("shop-services", ShopServices);
        Assert.Equal(0, Run(null, "repo", "add", repo).code);
        Assert.Equal(0, Run(null, "trust", "add", Path.Combine(repo, "sprig.yaml")).code);
        Assert.Equal(0, Run(Json(("cwd", repo), ("name", "calm-fox"), ("session_id", "s-1")), "hook", "worktree-create", "--contract", "1").code);
        var (code, _, stderr) = Run(null, "up", "shop-services", "-b", "mine", "--no-start", "--json");
        Assert.True(code == 0, stderr);

        (code, var stdout, stderr) = Run(null, "free", "--agent-made", "--json");

        Assert.True(code == 0, stderr);
        Assert.Contains("shop-services--1", stdout);
        var records = new WorkspaceStore(_store.Paths).List().Where(r => !r.IsMain).ToDictionary(r => r.Name);
        Assert.True(records["1"].IsFree);
        Assert.Equal("mine", records["2"].Branch);
        Assert.False(records["2"].IsFree);
        Assert.Contains("\"freed\": []", Run(null, "free", "--agent-made", "--json").stdout);
    }

    [Fact]
    public void A_session_learns_what_its_folder_is_to_sprig_and_hears_nothing_elsewhere()
    {
        var repo = Repo("shop-services", ShopServices);
        Assert.Equal(0, Run(null, "repo", "add", repo).code);

        var (code, stdout, _) = Run(Json(("cwd", repo)), "hook", "session-start", "--contract", "1");
        Assert.Equal(0, code);
        Assert.Contains("main checkout of sprig's 'shop-services'", stdout);
        Assert.Contains("sprig docs agent", stdout);

        (code, stdout, _) = Run(Json(("cwd", Repo("notes"))), "hook", "session-start", "--contract", "1");
        Assert.Equal(0, code);
        Assert.Empty(stdout);
    }
}
