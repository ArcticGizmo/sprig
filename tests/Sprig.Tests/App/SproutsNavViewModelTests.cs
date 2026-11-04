using Sprig.App;
using Sprig.App.ViewModels;
using Sprig.Engine.Hosting;
using Sprig.Engine.Planning;
using Sprig.Engine.Sprouts;

namespace Sprig.Tests.App;

public class SproutsNavViewModelTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.FromHours(11));

    static void Save(SproutStore store, string trellis, string name, SproutState state, TimeSpan age, string? error = null, bool inPlace = false, int apps = 0)
    {
        var at = Now - age;
        store.Save(new SproutRecord(trellis, $@"C:\tb\{trellis}.trellis.cs", name, inPlace ? null : name, "stable", [], [], [], state, at, at, Error: error));
        if (apps > 0)
            store.SavePlan(new Plan(trellis, name, "stable", null, [],
                [.. Enumerable.Range(1, apps).Select(i => new PlannedApp("shop", "../shop", $"app{i}", "dotnet", ".", [], null, [], [])),
                 new PlannedApp("shop", "../shop", "migrate", "dotnet", ".", [], null, [], [], Job: true)],
                [], [], [], []));
    }

    static (SproutsNavViewModel Nav, SproutsViewModel Page, AppServices Services, Func<int> Shown) Make(string root)
    {
        var services = new AppServices(root);
        var page = new SproutsViewModel(services);
        var shown = 0;
        return (new SproutsNavViewModel(services, page, () => shown++, () => Now), page, services, () => shown);
    }

    [Fact]
    public void Sprouts_are_grouped_by_trellis_with_the_ones_that_are_down_folded_away()
    {
        using var temp = new TempStore();
        var store = new SproutStore(new Sprig.Engine.Store.SprigPaths(temp.Root));
        Save(store, "storefront", "feat-a", SproutState.Up, TimeSpan.Zero, apps: 3);
        Save(store, "storefront", "demo", SproutState.Failed, TimeSpan.FromMinutes(20), error: "port 8002 is in use\nmore detail");
        Save(store, "storefront", "feat-c", SproutState.Down, TimeSpan.FromDays(3));
        Save(store, "storefront", "spike", SproutState.Down, TimeSpan.FromDays(15));
        Save(store, "analytics", "perf", SproutState.Down, TimeSpan.FromHours(5));
        Save(store, "services", "local", SproutState.Up, TimeSpan.Zero, inPlace: true, apps: 2);

        var (nav, _, _, _) = Make(temp.Root);

        Assert.Equal(["analytics", "services", "storefront"], nav.Groups.Select(g => g.Trellis));
        Assert.Equal(6, nav.Count);
        var storefront = nav.Groups[2];
        Assert.Equal(["demo", "feat-a"], storefront.Shown.Select(s => s.Name));
        Assert.Equal(["feat-c", "spike"], storefront.Down.Select(s => s.Name));
        Assert.Equal("1 up · 1 failed", storefront.Caption);
        Assert.True(storefront.CaptionIsBad);
        Assert.Equal("2 down", storefront.DownLabel);
        Assert.Equal("failed: port 8002 is in use", storefront.Shown[0].Sub);
        Assert.Equal("apps stopped", storefront.Shown[1].Sub);
        Assert.Equal(["down · 3 days ago", "down · 2 weeks ago"], storefront.Down.Select(s => s.Sub));
        Assert.Equal("1 down", nav.Groups[0].Caption);
        Assert.Empty(nav.Groups[0].Shown);
        Assert.Equal("down · 5 h ago", nav.Groups[0].Down[0].Sub);
        Assert.Equal("1 up · 1", nav.Groups[1].Caption);
        Assert.True(nav.Groups[1].CaptionIsGood);
        Assert.Equal("in place · apps stopped", nav.Groups[1].Shown[0].Sub);
    }

    [Fact]
    public void Running_apps_are_counted_from_the_host_without_counting_jobs()
    {
        using var temp = new TempStore();
        var store = new SproutStore(new Sprig.Engine.Store.SprigPaths(temp.Root));
        Save(store, "storefront", "feat-a", SproutState.Up, TimeSpan.Zero, apps: 3);
        var (nav, _, services, _) = Make(temp.Root);
        var item = nav.Groups[0].Shown[0];

        services.HostStatus.Apply([
            new RunnerStatus("storefront/feat-a", "shop/app1", RunnerState.Ready, 1, null, null, "a.log", Now),
            new RunnerStatus("storefront/feat-a", "shop/app2", RunnerState.Starting, 2, null, null, "b.log", Now),
            new RunnerStatus("storefront/feat-a", "shop/migrate", RunnerState.Completed, null, null, 0, "c.log", Now),
            new RunnerStatus("storefront/other", "shop/app1", RunnerState.Ready, 3, null, null, "d.log", Now),
        ]);

        Assert.Same(item, nav.Groups[0].Shown[0]);
        Assert.Equal("2 of 3 running", item.Sub);
    }

    [Fact]
    public void Picking_a_sprout_shows_it_and_marks_it_current_and_folding_is_remembered()
    {
        using var temp = new TempStore();
        var store = new SproutStore(new Sprig.Engine.Store.SprigPaths(temp.Root));
        Save(store, "storefront", "feat-a", SproutState.Up, TimeSpan.Zero);
        Save(store, "storefront", "old", SproutState.Down, TimeSpan.FromDays(1));
        var (nav, page, _, shown) = Make(temp.Root);
        page.IsActive = true;
        var group = nav.Groups[0];

        nav.OpenCommand.Execute(group.Shown[0]);
        Assert.Equal(1, shown());
        Assert.Equal("storefront/feat-a", page.Selected!.Record.Key);
        Assert.True(group.Shown[0].IsCurrent);
        Assert.False(nav.OverviewIsCurrent);

        nav.OpenOverviewCommand.Execute(null);
        Assert.Null(page.Selected);
        Assert.True(nav.OverviewIsCurrent);
        Assert.False(group.Shown[0].IsCurrent);

        group.ToggleCommand.Execute(null);
        group.ToggleDownCommand.Execute(null);
        var (again, _, _, _) = Make(temp.Root);
        Assert.False(again.Groups[0].IsOpen);
        Assert.True(again.Groups[0].IsDownOpen);
        Assert.Equal("Hide 1 down", again.Groups[0].DownLabel);
    }

    [Fact]
    public void Add_in_the_nav_opens_the_new_sprout_modal_on_the_sprouts_page()
    {
        using var temp = new TempStore();
        var (nav, page, _, shown) = Make(temp.Root);

        nav.AddCommand.Execute(null);

        Assert.Equal(1, shown());
        Assert.True(page.IsAddOpen);
    }
}
