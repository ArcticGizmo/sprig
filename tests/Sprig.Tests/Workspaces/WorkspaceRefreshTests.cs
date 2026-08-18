using Sprig.Core.Compose;
using Sprig.Core.Env;
using Sprig.Core.Git;
using Sprig.Core.Ports;
using Sprig.Core.Processes;
using Sprig.Core.Setup;
using Sprig.Core.Store;
using Sprig.Core.Workspaces;

namespace Sprig.Tests.Workspaces;

/// <summary>
/// M1 probe: <see cref="WorkspaceService.RefreshToBase"/> resyncs a workspace's repos to their base
/// branch (a git operation) without deleting the expensive gitignored artifacts (a disk operation) —
/// the two axes the pool model keeps separate. Uses real git worktrees, so it runs in the git-heavy
/// (serial) collection.
/// </summary>
[Collection("git-heavy")]
public class WorkspaceRefreshTests
{
    // Zero declared inputs (so it stands up as an ad-hoc single repo), with an env override that
    // references ${sprig.workspace} — enough to prove env is re-clobbered on refresh.
    const string Config = """
        { "schema": 1, "name": "app",
          "env": [ { "file": ".env", "set": { "APP_ENV": "sprig-${sprig.workspace}" } } ] }
        """;

    const string ConfigWithSetup = """
        { "schema": 1, "name": "app", "setup": [ "npm ci" ] }
        """;

    static (WorkspaceService svc, InstanceStore instances) Build(TempStore s, IProcessRunner? setupRunner = null)
    {
        var svc = new WorkspaceService(
            new GitService(new ProcessRunner()), new FilePortStore(s.Paths), new InstanceStore(s.Paths),
            new EnvClobberService(), new ComposeGenerator(), new FakeDockerService { Available = false }, s.Paths,
            new SetupRunner(setupRunner ?? new ProcessRunner()));
        return (svc, new InstanceStore(s.Paths));
    }

    static void SeedRepo(TempGitRepo repo, string config = Config)
    {
        File.WriteAllText(Path.Combine(repo.Path, ".sprig.json"), config);
        File.WriteAllText(Path.Combine(repo.Path, ".gitignore"), "node_modules/\n.env\n");
        File.WriteAllText(Path.Combine(repo.Path, "src.txt"), "base\n");
        repo.Git("add", "-A");
        repo.Git("-c", "user.email=t@sprig", "-c", "user.name=sprig", "commit", "-m", "seed app");
    }

    static void GitIn(string dir, params string[] args)
        => new ProcessRunner().Run("git", args, dir).EnsureSuccess();

    [Fact]
    public void Refresh_resets_tracked_files_to_base_but_keeps_gitignored_artifacts()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        SeedRepo(repo);
        var (svc, _) = Build(store);
        var record = svc.Create(repo.Path, "feat-a");
        var wt = record.Repos[0].WorktreePath;

        // Simulate work: edit a tracked file, and drop an expensive gitignored artifact.
        File.WriteAllText(Path.Combine(wt, "src.txt"), "local edit");
        Directory.CreateDirectory(Path.Combine(wt, "node_modules"));
        File.WriteAllText(Path.Combine(wt, "node_modules", "lib.js"), "dep");

        svc.RefreshToBase("feat-a");

        // Git axis: the tracked file is back at base (line endings vary by platform checkout config).
        Assert.Equal("base", File.ReadAllText(Path.Combine(wt, "src.txt")).Trim());
        // Disk axis: node_modules was NOT re-downloaded — it's exactly as it was. This is the whole point.
        Assert.True(File.Exists(Path.Combine(wt, "node_modules", "lib.js")));
        Assert.Equal("dep", File.ReadAllText(Path.Combine(wt, "node_modules", "lib.js")));
    }

    [Fact]
    public void Refresh_reclobbers_the_env_block()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        SeedRepo(repo);
        var (svc, _) = Build(store);
        var record = svc.Create(repo.Path, "feat-a");
        var wt = record.Repos[0].WorktreePath;

        // Mangle the generated env, then refresh — it should be rewritten with the resolved block.
        File.WriteAllText(Path.Combine(wt, ".env"), "GARBAGE\n");

        svc.RefreshToBase("feat-a");

        var env = File.ReadAllText(Path.Combine(wt, ".env"));
        Assert.Contains("APP_ENV=sprig-feat-a", env);
        Assert.Contains(EnvClobberService.BeginMarker, env);
    }

    [Fact]
    public void Refresh_refuses_to_discard_commits_the_base_lacks_unless_forced()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        SeedRepo(repo);
        var (svc, _) = Build(store);
        var record = svc.Create(repo.Path, "feat-a");
        var wt = record.Repos[0].WorktreePath;

        // Commit new work on the workspace branch — now it's ahead of base.
        File.WriteAllText(Path.Combine(wt, "feature.txt"), "wip\n");
        GitIn(wt, "add", "-A");
        GitIn(wt, "-c", "user.email=t@sprig", "-c", "user.name=sprig", "commit", "-m", "wip");

        // Unforced refresh must refuse and change nothing.
        var ex = Assert.Throws<WorkspaceException>(() => svc.RefreshToBase("feat-a"));
        Assert.Contains("refusing to refresh", ex.Message);
        Assert.True(File.Exists(Path.Combine(wt, "feature.txt")));

        // Forced refresh discards the commit (resets to base).
        svc.RefreshToBase("feat-a", force: true);
        Assert.False(File.Exists(Path.Combine(wt, "feature.txt")));
    }

    [Fact]
    public void Refresh_reruns_setup_in_the_worktree()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        SeedRepo(repo, ConfigWithSetup);
        var recorder = new RecordingProcessRunner { ExitCode = 0 };
        var (svc, _) = Build(store, recorder);
        svc.Create(repo.Path, "feat-a");

        recorder.Calls.Clear();
        svc.RefreshToBase("feat-a");

        Assert.Contains(recorder.Calls, c => c.Arguments[^1] == "npm ci"
            && c.WorkingDirectory == repo.SiblingWorktree("feat-a"));
    }

    [Fact]
    public void Refresh_only_rejects_an_unknown_repo()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        SeedRepo(repo);
        var (svc, _) = Build(store);
        svc.Create(repo.Path, "feat-a");

        var ex = Assert.Throws<WorkspaceException>(() => svc.RefreshToBase("feat-a", new[] { "nope" }));
        Assert.Contains("no repo 'nope'", ex.Message);
    }

    [Fact]
    public void Refresh_rejects_an_unknown_workspace()
    {
        using var store = new TempStore();
        var (svc, _) = Build(store);
        var ex = Assert.Throws<WorkspaceException>(() => svc.RefreshToBase("ghost"));
        Assert.Contains("unknown workspace", ex.Message);
    }
}
