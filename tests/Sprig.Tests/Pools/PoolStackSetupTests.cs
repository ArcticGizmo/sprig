using Sprig.Core.Compose;
using Sprig.Core.Env;
using Sprig.Core.Git;
using Sprig.Core.Pools;
using Sprig.Core.Ports;
using Sprig.Core.Processes;
using Sprig.Core.Setup;
using Sprig.Core.Stacks;
using Sprig.Core.Store;
using Sprig.Core.Workspaces;

namespace Sprig.Tests.Pools;

/// <summary>M4: a stack can carry a repo's setup, so a config-less (name-only .sprig.json) repo stands up
/// entirely from the stack; and a failed setup marks the workspace degraded. Real git worktrees; setup
/// shells through a recording runner so no real command runs.</summary>
[Collection("git-heavy")]
public class PoolStackSetupTests
{
    static (PoolService pools, RecordingProcessRunner setup) Build(
        TempGitRepo repo, TempStore store, string config, IReadOnlyList<string> stackSetup, int setupExit = 0)
    {
        File.WriteAllText(Path.Combine(repo.Path, ".sprig.json"), config);
        repo.Git("add", "-A");
        repo.Git("-c", "user.email=t@sprig", "-c", "user.name=sprig", "commit", "-m", "config");

        var registry = new RepoRegistryStore(store.Paths);
        registry.Add(repo.Path); // name "app"

        var instances = new InstanceStore(store.Paths);
        var stacks = new StackStore(store.Paths, registry, instances);
        stacks.Save(new StackDefinition
        {
            Name = "app",
            Repos = ["app"],
            MaxSlots = 1,
            Setup = new Dictionary<string, IReadOnlyList<string>> { ["app"] = stackSetup },
        });

        var setup = new RecordingProcessRunner { ExitCode = setupExit, StdErr = setupExit == 0 ? "" : "boom" };
        var git = new GitService(new ProcessRunner());
        var workspaces = new WorkspaceService(git, new FilePortStore(store.Paths), instances,
            new EnvClobberService(), new ComposeGenerator(), new FakeDockerService { Available = false }, store.Paths,
            new SetupRunner(setup));
        var resolver = new StackResolver(registry, stacks, git);
        return (new PoolService(stacks, instances, resolver, workspaces, store.Paths), setup);
    }

    const string NameOnly = """{ "schema":2, "name":"app" }""";

    [Fact]
    public void A_config_less_repo_stands_up_from_stack_supplied_setup()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        var (pools, setup) = Build(repo, store, NameOnly, ["npm ci"]);

        var ws = pools.Checkout("app", null, "first");

        // The stack's setup ran in the worktree, and its outcome is recorded on the instance.
        Assert.Contains(setup.Calls, c => c.Arguments[^1] == "npm ci"
            && c.WorkingDirectory == repo.SiblingWorktree("app-1"));
        Assert.Contains(ws.Repos[0].Setup, o => o.Command == "npm ci" && o.Success);
        Assert.False(ws.SetupFailed);
    }

    [Fact]
    public void Stack_setup_runs_after_the_repo_s_own_setup()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        const string withRepoSetup = """{ "schema":2, "name":"app", "setup":["repo-cmd"] }""";
        var (pools, setup) = Build(repo, store, withRepoSetup, ["stack-cmd"]);

        pools.Checkout("app", null, "first");

        var commands = setup.Calls.Select(c => c.Arguments[^1]).ToList();
        Assert.Contains("repo-cmd", commands);
        Assert.Contains("stack-cmd", commands);
        Assert.True(commands.IndexOf("repo-cmd") < commands.IndexOf("stack-cmd")); // repo first, then stack
    }

    [Fact]
    public void PlanCheckout_for_a_new_workspace_lists_the_stand_up_steps()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        var (pools, _) = Build(repo, store, NameOnly, ["npm ci"]);

        var ids = pools.PlanCheckout("app", null, CheckoutMode.AsIs, null).Select(p => p.Id).ToList();

        Assert.Contains("ports", ids);
        Assert.Contains("app:setup", ids); // the stack setup shows as a step
        Assert.Contains("infra", ids);
    }

    [Fact]
    public void PlanCheckout_as_is_reuse_is_just_infra_but_fresh_lists_the_refresh_steps()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        var (pools, _) = Build(repo, store, NameOnly, ["npm ci"]);
        pools.Checkout("app", null, "first"); // materialise app-1

        Assert.Equal(["infra"], pools.PlanCheckout("app", "app-1", CheckoutMode.AsIs, null).Select(p => p.Id));

        var fresh = pools.PlanCheckout("app", "app-1", CheckoutMode.Fresh, null).Select(p => p.Id).ToList();
        Assert.Contains("app:resync", fresh);
        Assert.Contains("app:setup", fresh);
        Assert.Contains("infra", fresh);
    }

    [Fact]
    public void A_fresh_reuse_reruns_the_stack_setup()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        var (pools, setup) = Build(repo, store, NameOnly, ["npm ci"]);
        pools.Checkout("app", null, "first"); // stack setup ran once
        pools.Release("app-1");
        setup.Calls.Clear();

        pools.Checkout("app", "app-1", "again", CheckoutMode.Fresh);

        // The stack overlay is honoured on refresh, not just at first create.
        Assert.Contains(setup.Calls, c => c.Arguments[^1] == "npm ci");
    }

    [Fact]
    public void A_failed_setup_marks_the_workspace_degraded()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        var (pools, _) = Build(repo, store, NameOnly, ["npm ci"], setupExit: 1);

        var ws = pools.Checkout("app", null, "first");

        Assert.True(ws.SetupFailed);
        Assert.Equal(1, pools.Status("app").DegradedCount);
    }
}
