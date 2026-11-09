using Sprig.App;
using Sprig.App.ViewModels;
using Sprig.Engine.Hosting;
using Sprig.Engine.Planning;
using Sprig.Engine.Workspaces;

namespace Sprig.Tests.App;

public class HostStatusTests
{
    [Fact]
    public async Task With_no_host_running_nothing_is_running_and_subscribers_hear_so()
    {
        using var store = new TempStore();
        var services = new AppServices(store.Root);
        var heard = new List<IReadOnlyList<RunnerStatus>?>();
        services.HostStatus.Changed += heard.Add;

        await services.HostStatus.PollAsync();

        Assert.Null(services.HostStatus.Runners);
        Assert.Single(heard);
        Assert.Null(heard[0]);
    }

    [Fact]
    public void The_workspaces_page_shows_each_answer_including_one_from_before_it_was_made()
    {
        using var store = new TempStore();
        var services = new AppServices(store.Root);
        var now = DateTimeOffset.UtcNow;
        var workspaces = new WorkspaceStore(services.Paths);
        var repo = new WorkspaceRepo("../shop", "shop", @"C:\tb\shop", @"C:\tb\shop", false, false);
        workspaces.Save(new WorkspaceRecord("storefront", @"C:\tb\storefront.orchestration.cs", "feat-x", null, "stable", [repo], [], [],
            WorkspaceState.Up, now, now));
        workspaces.SavePlan(new Plan("storefront", "feat-x", "stable", null, [],
            [new PlannedApp("shop", "../shop", "api", "dotnet", ".", [], null, [])], [], [], [], []));
        RunnerStatus[] Api(RunnerState state) => [new("storefront/feat-x", "shop/api", state, 42, 8000, null, "api.log", now)];

        services.HostStatus.Apply(Api(RunnerState.Ready));
        var page = new WorkspacesViewModel(services);
        page.Show("storefront/feat-x");
        Assert.Equal("ready", page.Apps.Single().State);

        services.HostStatus.Apply(Api(RunnerState.Stopped));
        Assert.Equal("stopped", page.Apps.Single().State);
    }
}
