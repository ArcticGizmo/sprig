using Sprig.App;
using Sprig.App.ViewModels;
using Sprig.Engine.Hosting;
using Sprig.Engine.Planning;
using Sprig.Engine.Sprouts;

namespace Sprig.Tests.App;

public sealed class LogPaneViewModelTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("sprig-logpane-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Lines_arrive_without_colour_codes_and_a_search_shows_only_matches()
    {
        var log = new LogPaneViewModel();
        var added = 0;
        log.LinesAdded += () => added++;
        log.Open("shop / web", Path.Combine(_dir, "web.log"));

        log.Append(["sprig: starting npm run dev", "\u001B[32mVITE\u001B[39m ready in 312 ms", "GET /api/products 200"]);

        Assert.Equal(3, log.Lines.Count);
        Assert.Equal("VITE ready in 312 ms", log.Lines[1].Text);
        Assert.True(log.Lines[0].IsSprig);
        Assert.Equal("3 lines", log.Summary);
        Assert.Equal(1, added);

        log.Search = "READY";
        Assert.Equal([2], log.Lines.Select(l => l.Number));
        Assert.Equal("1 of 3 lines match", log.Summary);

        log.Append(["ready again", "no match"]);
        Assert.Equal([2, 4], log.Lines.Select(l => l.Number));
    }

    [Fact]
    public void Only_the_newest_lines_are_kept_and_the_summary_says_so()
    {
        var log = new LogPaneViewModel();
        log.Open("shop / api", Path.Combine(_dir, "api.log"));

        log.Append(Enumerable.Range(1, LogPaneViewModel.MaxLines + 5).Select(i => $"line {i}").ToArray());

        Assert.True(log.Lines.Count <= LogPaneViewModel.MaxLines);
        Assert.Equal($"line {LogPaneViewModel.MaxLines + 5}", log.Lines[^1].Text);
        Assert.Contains("the file has them all", log.Summary);
    }

    [Fact]
    public async Task A_log_started_afresh_replaces_what_was_shown()
    {
        var file = Path.Combine(_dir, "api.log");
        File.WriteAllText(file, "old run line one\nold run line two\n");
        var log = new LogPaneViewModel();
        log.Open("shop / api", file);
        await log.PollAsync();
        Assert.Equal(2, log.Lines.Count);

        File.WriteAllText(file, "new\n");
        await log.PollAsync();

        Assert.Equal(["new"], log.Lines.Select(l => l.Text));
        Assert.Equal(1, log.Lines[0].Number);
    }

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
        RunnerStatus Status(string name, RunnerState state, string? error = null, int restarts = 0)
            => new("storefront/feat-x", name, state, 1, null, null, "x.log", now, error, restarts);

        vm.ApplyRunners([
            Status("shop/api", RunnerState.Restarting, "exited with code 1; restarting in 2s (2 of 5)", restarts: 2),
            Status("shop/migrate", RunnerState.Completed),
            new RunnerStatus("other/sprout", "shop/api", RunnerState.Failed, null, null, null, "y.log", now),
        ]);

        var api = vm.Apps.Single(a => a.Key == "shop/api");
        Assert.Equal(("restarting", "exited with code 1; restarting in 2s (2 of 5)"), (api.State, api.Detail));
        Assert.True(api.IsBusy);
        Assert.True(api.IsAlive);
        var migrate = vm.Apps.Single(a => a.Key == "shop/migrate");
        Assert.Equal("finished", migrate.State);
        Assert.True(migrate.IsGood);
        Assert.True(migrate.IsJob);
        Assert.False(migrate.IsAlive);

        vm.ApplyRunners(null);
        Assert.Equal("not running", api.State);

        vm.StartAppCommand.Execute(api);
        Assert.True(vm.AppsMessageIsBad);
        Assert.Contains("Bring the sprout up first", vm.AppsMessage);

        vm.ShowLogCommand.Execute(api);
        Assert.Equal("shop / api", vm.Log.Title);
        Assert.Equal(HostProtocol.LogFile(services.Paths, "storefront/feat-x", "shop/api"), vm.Log.File);
    }
}
