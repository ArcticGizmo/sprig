using Sprig.Core.Compose;
using Sprig.Core.Env;
using Sprig.Core.Git;
using Sprig.Core.Ports;
using Sprig.Core.Processes;
using Sprig.Core.Setup;
using Sprig.Core.Store;
using Sprig.Core.Workspaces;

namespace Sprig.Tests.Workspaces;

/// <summary>Create runs the repo's declared setup commands and records their outcome — and a failing
/// command is a soft warning: the workspace is kept, not rolled back.</summary>
public class WorkspaceSetupTests
{
    const string ConfigWithSetup = """
        { "schema": 1, "name": "vue-app",
          "env": [ { "file": ".env", "set": { "NAME": "app--${sprig.workspace}" } } ],
          "setup": [ "npm ci" ] }
        """;

    // SetupRunner shells out through this fake, so no real command runs during the test.
    static (WorkspaceService svc, InstanceStore store, RecordingProcessRunner setupRunner) Build(TempStore s, int setupExit)
    {
        var git = new GitService(new ProcessRunner());
        var fakeSetupRunner = new RecordingProcessRunner { ExitCode = setupExit, StdErr = setupExit == 0 ? "" : "install failed" };
        var svc = new WorkspaceService(git, new FilePortStore(s.Paths), new InstanceStore(s.Paths),
            new EnvClobberService(), new ComposeGenerator(), new FakeDockerService { Available = false }, s.Paths,
            new SetupRunner(fakeSetupRunner));
        return (svc, new InstanceStore(s.Paths), fakeSetupRunner);
    }

    static void SeedRepo(TempGitRepo repo)
    {
        File.WriteAllText(Path.Combine(repo.Path, ".sprig.json"), ConfigWithSetup);
        File.WriteAllText(Path.Combine(repo.Path, ".env"), "NAME=original\n");
    }

    [Fact]
    public void Create_runs_setup_in_the_worktree_and_records_success()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        SeedRepo(repo);
        var (svc, instances, setupRunner) = Build(store, setupExit: 0);

        var record = svc.Create(repo.Path, "feat-a");

        // Ran in the worktree, not the source repo.
        Assert.Contains(setupRunner.Calls, c => c.Arguments[^1] == "npm ci"
            && c.WorkingDirectory == repo.SiblingWorktree("feat-a"));

        var outcome = Assert.Single(record.Repos[0].Setup);
        Assert.Equal("npm ci", outcome.Command);
        Assert.True(outcome.Success);
        // Persisted on the instance record too.
        Assert.True(instances.TryLoad("feat-a")!.Repos[0].Setup[0].Success);
    }

    [Fact]
    public void A_failing_setup_command_keeps_the_workspace_and_records_the_failure()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        SeedRepo(repo);
        var (svc, instances, _) = Build(store, setupExit: 1);

        var record = svc.Create(repo.Path, "feat-a");

        // Not rolled back: worktree, branch and record all survive.
        Assert.True(Directory.Exists(repo.SiblingWorktree("feat-a")));
        Assert.NotNull(instances.TryLoad("feat-a"));

        var outcome = Assert.Single(record.Repos[0].Setup);
        Assert.False(outcome.Success);
        Assert.Equal(1, outcome.ExitCode);
    }

    [Fact]
    public void No_setup_runner_means_no_setup()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        SeedRepo(repo);
        // The default WorkspaceService ctor omits the setup runner entirely.
        var git = new GitService(new ProcessRunner());
        var svc = new WorkspaceService(git, new FilePortStore(store.Paths), new InstanceStore(store.Paths),
            new EnvClobberService(), new ComposeGenerator(), new FakeDockerService { Available = false }, store.Paths);

        var record = svc.Create(repo.Path, "feat-a");

        Assert.Empty(record.Repos[0].Setup);
    }
}
