using Sprig.Core.Compose;
using Sprig.Core.Env;
using Sprig.Core.Git;
using Sprig.Core.Ports;
using Sprig.Core.Processes;
using Sprig.Core.Setup;
using Sprig.Core.Store;
using Sprig.Core.Workspaces;

namespace Sprig.Tests.Workspaces;

/// <summary>Create/teardown emit an ordered checklist that a UI can pre-render (PlanCreate/PlanRemove)
/// then follow live (the IProgress reports). Reports are synchronous, so a plain capturing sink sees
/// them in execution order.</summary>
public class WorkspaceProgressTests
{
    // Zero-input, no-compose, no-setup repo — the ad-hoc single-repo path stands it up.
    const string PlainConfig = """
        { "schema": 2, "name": "vue-app",
          "env": [ { "file": ".env", "set": { "NAME": "app--${sprig.workspace}" } } ] }
        """;

    const string ConfigWithSetup = """
        { "schema": 2, "name": "vue-app",
          "env": [ { "file": ".env", "set": { "NAME": "app--${sprig.workspace}" } } ],
          "setup": [ "npm ci" ] }
        """;

    const string ConfigWithTwoSetup = """
        { "schema": 2, "name": "vue-app",
          "env": [ { "file": ".env", "set": { "NAME": "app--${sprig.workspace}" } } ],
          "setup": [ "npm ci", "npm run build" ] }
        """;

    sealed class CaptureProgress : IProgress<WorkspaceStepProgress>
    {
        public List<WorkspaceStepProgress> Events { get; } = [];
        public void Report(WorkspaceStepProgress value) => Events.Add(value);
    }

    static WorkspaceService Build(TempStore s, SetupRunner? setup = null) =>
        new(new GitService(new ProcessRunner()), new FilePortStore(s.Paths), new InstanceStore(s.Paths),
            new EnvClobberService(), new ComposeGenerator(), new FakeDockerService { Available = false }, s.Paths, setup);

    static void Seed(TempGitRepo repo, string config)
    {
        File.WriteAllText(Path.Combine(repo.Path, ".sprig.json"), config);
        File.WriteAllText(Path.Combine(repo.Path, ".env"), "NAME=original\n");
    }

    [Fact]
    public void PlanCreate_lists_the_steps_for_a_plain_single_repo()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        Seed(repo, PlainConfig);
        var svc = Build(store);

        var plan = svc.PlanCreateFromMap(svc.ResolveSingleRepo(repo.Path), "feat-a");

        // No compose in the config and no setup runner → just ports, worktree, env, record.
        Assert.Equal(
            ["ports", "vue-app:worktree", "vue-app:env", "record"],
            plan.Select(p => p.Id).ToArray());
    }

    [Fact]
    public void Create_reports_each_planned_step_running_then_done_in_order()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        Seed(repo, PlainConfig);
        var svc = Build(store);
        var progress = new CaptureProgress();

        svc.Create(repo.Path, "feat-a", progress);

        var pairs = progress.Events.Select(e => (e.StepId, e.State)).ToArray();
        Assert.Equal(
        [
            ("ports", WorkspaceStepState.Running), ("ports", WorkspaceStepState.Done),
            ("vue-app:worktree", WorkspaceStepState.Running), ("vue-app:worktree", WorkspaceStepState.Done),
            ("vue-app:env", WorkspaceStepState.Running), ("vue-app:env", WorkspaceStepState.Done),
            ("record", WorkspaceStepState.Running), ("record", WorkspaceStepState.Done),
        ], pairs);
    }

    [Fact]
    public void A_failing_setup_command_reports_a_warning_and_keeps_the_workspace()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        Seed(repo, ConfigWithSetup);
        var runner = new RecordingProcessRunner { ExitCode = 1, StdErr = "install failed" };
        var svc = Build(store, new SetupRunner(runner));
        var progress = new CaptureProgress();

        var record = svc.Create(repo.Path, "feat-a", progress);

        // The setup row is a soft Warning, not an Error — the workspace survives.
        var setup = Assert.Single(progress.Events, e => e.StepId == "vue-app:setup" && e.State == WorkspaceStepState.Warning);
        Assert.NotNull(setup.Detail);
        Assert.DoesNotContain(progress.Events, e => e.State == WorkspaceStepState.Error);
        Assert.NotNull(new InstanceStore(store.Paths).TryLoad("feat-a"));
        Assert.False(record.Repos[0].Setup[0].Success);
    }

    [Fact]
    public void PlanCreate_expands_setup_into_a_parent_and_one_sub_step_per_command()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        Seed(repo, ConfigWithTwoSetup);
        var svc = Build(store, new SetupRunner(new RecordingProcessRunner()));

        var plan = svc.PlanCreateFromMap(svc.ResolveSingleRepo(repo.Path), "feat-a");

        // Parent row, then one indented sub-row per command labelled with the command itself.
        Assert.Contains(plan, s => s.Id == "vue-app:setup" && !s.SubStep);
        var subs = plan.Where(s => s.SubStep).ToArray();
        Assert.Equal(["vue-app:setup:0", "vue-app:setup:1"], subs.Select(s => s.Id).ToArray());
        Assert.Equal(["npm ci", "npm run build"], subs.Select(s => s.Label).ToArray());
    }

    [Fact]
    public void Create_runs_each_setup_command_as_a_sub_step_and_streams_its_output()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        Seed(repo, ConfigWithSetup);
        var runner = new RecordingProcessRunner { ExitCode = 0, StdOut = "added 42 packages\ndone\n" };
        var svc = Build(store, new SetupRunner(runner));
        var progress = new CaptureProgress();

        svc.Create(repo.Path, "feat-a", progress);

        // The command ran as its own sub-step: Running (a state change, no output) then Done.
        Assert.Contains(progress.Events, e => e.StepId == "vue-app:setup:0"
            && e.State == WorkspaceStepState.Running && e.Output is null);
        Assert.Contains(progress.Events, e => e.StepId == "vue-app:setup:0" && e.State == WorkspaceStepState.Done);

        // Its stdout was streamed line-by-line to the same sub-step (Output carries each line).
        var streamed = progress.Events
            .Where(e => e.StepId == "vue-app:setup:0" && e.Output is not null)
            .Select(e => e.Output)
            .ToArray();
        Assert.Contains("added 42 packages", streamed);
        Assert.Contains("done", streamed);

        // Parent completes Done when the command succeeds.
        Assert.Contains(progress.Events, e => e.StepId == "vue-app:setup" && e.State == WorkspaceStepState.Done);
    }

    [Fact]
    public void PlanRemove_and_Remove_emit_the_teardown_steps_and_complete()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        Seed(repo, PlainConfig);
        var svc = Build(store);
        svc.Create(repo.Path, "feat-a");
        var record = svc.Get("feat-a")!;

        // No infra and force off → just the worktree, ports and record rows.
        var plan = svc.PlanRemove(record, force: false);
        Assert.Equal(
            ["vue-app:worktree", "ports", "record"],
            plan.Select(p => p.Id).ToArray());

        var progress = new CaptureProgress();
        svc.Remove("feat-a", force: false, progress);

        // Every planned step reaches Done, and the workspace is gone.
        foreach (var id in new[] { "vue-app:worktree", "ports", "record" })
            Assert.Contains(progress.Events, e => e.StepId == id && e.State == WorkspaceStepState.Done);
        Assert.Null(svc.Get("feat-a"));
    }
}
