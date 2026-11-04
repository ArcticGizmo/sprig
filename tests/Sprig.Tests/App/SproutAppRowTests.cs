using Sprig.App;
using Sprig.App.ViewModels;
using Sprig.Engine.Hosting;
using Sprig.Engine.Planning;
using Sprig.Engine.Sprouts;

namespace Sprig.Tests.App;

public class SproutAppRowTests
{
    [Fact]
    public void App_rows_show_what_the_host_says_and_starting_needs_the_sprout_up()
    {
        using var store = new TempStore();
        var services = new AppServices(store.Root);
        var sprouts = new SproutStore(services.Paths);
        var now = DateTimeOffset.UtcNow;
        var repo = new SproutRepo("../shop", "shop", @"C:\tb\shop", @"C:\tb\shop", false, false);
        sprouts.Save(new SproutRecord("storefront", @"C:\tb\storefront.trellis.cs", "feat-x", null, "stable", [repo], [], [],
            SproutState.Down, now, now));
        sprouts.SavePlan(new Plan("storefront", "feat-x", "stable", null, [],
            [
                new PlannedApp("shop", "../shop", "api", "dotnet", ".", [], null, [], []),
                new PlannedApp("shop", "../shop", "migrate", "dotnet", ".", [], null, [], [], Job: true),
            ], [], [], [], []));
        var vm = new SproutsViewModel(services);
        vm.Show("storefront/feat-x");
        RunnerStatus Status(string name, RunnerState state, string? error = null, int restarts = 0)
            => new("storefront/feat-x", name, state, 1, null, null, "x.log", now, error, restarts);

        vm.ApplyRunners([
            Status("shop/api", RunnerState.Restarting, "exited with code 1; restarting in 2s (2 of 5)", restarts: 2),
            Status("shop/migrate", RunnerState.Completed),
            new RunnerStatus("other/sprout", "shop/api", RunnerState.Failed, null, null, null, "y.log", now),
        ]);

        var api = vm.Apps.Single(a => a.Key == "shop/api");
        Assert.Equal(("restarting", "exited with code 1; restarting in 2s (2 of 5)"), (api.State, api.Detail));
        Assert.Equal("restarting · exited with code 1; restarting in 2s (2 of 5)", api.StateLine);
        Assert.True(api.IsBusy);
        Assert.True(api.IsAlive);
        Assert.True(api.IsProblem);
        Assert.Equal(2, api.Restarts);
        var migrate = vm.Apps.Single(a => a.Key == "shop/migrate");
        Assert.Equal("finished", migrate.State);
        Assert.True(migrate.IsGood);
        Assert.True(migrate.IsJob);
        Assert.False(migrate.IsAlive);
        Assert.True(migrate.IsStoppedOrDone);
        Assert.Equal("job", migrate.Port);

        vm.ApplyRunners(null);
        Assert.Equal("not running", api.State);

        vm.StartAppCommand.Execute(api);
        Assert.True(vm.AppsMessageIsBad);
        Assert.Contains("Bring the sprout up first", vm.AppsMessage);

        vm.OpenAppRowCommand.Execute(api);
        Assert.Equal("shop / api", vm.LogTitle);
        Assert.Equal(HostProtocol.LogFile(services.Paths, "storefront/feat-x", "shop/api").Replace('\\', '/'), vm.Logs.FileShown);
        Assert.True(api.IsSelected);
    }

    [Fact]
    public void A_state_line_doesnt_repeat_the_state_when_the_detail_already_says_it()
    {
        var row = new SproutAppRow("shop/api", "shop / api", "", "", "", false);

        row.Apply(new RunnerStatus("s/x", "shop/api", RunnerState.Exited, null, null, 1, "x.log", DateTimeOffset.UtcNow, "exited with code 1 during start-up"));

        Assert.Equal("exited with code 1 during start-up", row.StateLine);
    }
}
