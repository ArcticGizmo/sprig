using Sprig.Engine.Hosting;
using Sprig.Engine.Planning;
using Sprig.Engine.Workspaces;

namespace Sprig.Tests.Workspaces;

public class AppsReadinessTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    static readonly WorkspaceRecord Record = new("storefront", "x", "feat-x", "feat-x", "stable", [], [], [], WorkspaceState.Up, Now, Now);

    static readonly Plan Plan = new("storefront", "feat-x", "stable", null, [],
    [
        new PlannedApp("shop", "../shop", "api", "dotnet", ".", [], null, []),
        new PlannedApp("shop", "../shop", "web", "node", ".", [], null, []),
        new PlannedApp("shop", "../shop", "admin", "node", ".", [], null, []),
        new PlannedApp("shop", "../shop", "migrate", "app", ".", [], null, [], Job: true),
    ], [], [], [], []);

    static RunnerStatus Runner(string name, RunnerState state, string workspace = "storefront/feat-x")
        => new(workspace, $"shop/{name}", state, null, null, null, "", Now);

    static AppsReadiness Of(params RunnerStatus[] runners) => AppsReadiness.Of(Record, Plan, runners);

    [Fact]
    public void Starting_lasts_while_an_app_builds_waits_or_boots_or_a_job_runs()
    {
        foreach (var state in new[] { RunnerState.Building, RunnerState.Waiting, RunnerState.Starting })
            Assert.Equal(AppsHealth.Starting, Of(Runner("api", RunnerState.Ready), Runner("web", state)).Health);
        var migrating = Of(Runner("migrate", RunnerState.Running), Runner("api", RunnerState.Waiting));

        Assert.Equal(AppsHealth.Starting, migrating.Health);
        // A job isn't an app: it counts for health, not in the counts.
        Assert.Equal((0, 1, 3), (migrating.Ready, migrating.Started, migrating.Total));
        Assert.Equal("starting", migrating.Word("running"));
        Assert.Equal("starting · 0 of 1 ready", migrating.Shown("running"));
        Assert.Equal("starting · running its jobs", Of(Runner("migrate", RunnerState.Running)).Shown("running"));
    }

    [Fact]
    public void Up_once_everything_asked_for_is_ready_with_finished_jobs_and_stopped_apps_counting_for_nothing()
    {
        var up = Of(Runner("migrate", RunnerState.Completed), Runner("api", RunnerState.Ready), Runner("web", RunnerState.Stopped),
            Runner("web", RunnerState.Building, workspace: "storefront/other"));

        Assert.Equal(AppsHealth.Up, up.Health);
        Assert.Equal((1, 1, 3), (up.Ready, up.Started, up.Total));
        Assert.Equal("running", up.Shown("running"));
        Assert.True(up.Up("running"));
        Assert.Equal(AppsHealth.None, Of(Runner("api", RunnerState.Stopped)).Health);
        Assert.True(Of().Up("running"));
    }

    [Fact]
    public void An_app_that_exited_failed_or_is_restarting_is_trouble_which_outweighs_one_still_starting()
    {
        foreach (var state in new[] { RunnerState.Exited, RunnerState.Failed, RunnerState.Restarting })
        {
            var trouble = Of(Runner("api", RunnerState.Building), Runner("web", state));
            Assert.Equal(AppsHealth.Trouble, trouble.Health);
            Assert.Equal(["shop/web"], trouble.Down);
            // Still running: the word stays, the colour and detail say what's wrong.
            Assert.Equal("running · shop/web is down", trouble.Shown("running"));
            Assert.True(trouble.InTrouble("running"));
            Assert.False(trouble.Up("running"));
        }
        Assert.Equal("running · 2 apps down", Of(Runner("api", RunnerState.Failed), Runner("migrate", RunnerState.Failed)).Shown("running"));
    }

    [Fact]
    public void Only_a_running_workspace_is_coloured_by_its_apps()
    {
        var building = Of(Runner("api", RunnerState.Building));

        Assert.Equal("paused", building.Shown("paused"));
        Assert.Equal("stopped", building.Shown("stopped"));
        Assert.False(building.ComingUp("starting"));
        Assert.True(building.InTrouble("failed"));
    }
}
