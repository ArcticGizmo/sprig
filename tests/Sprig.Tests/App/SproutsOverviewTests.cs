using Sprig.App;
using Sprig.App.ViewModels;
using Sprig.Engine.Hosting;
using Sprig.Engine.Planning;
using Sprig.Engine.Sprouts;

namespace Sprig.Tests.App;

public class SproutsOverviewTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.FromHours(11));

    static SproutsViewModel Make(TempStore temp, out AppServices services)
    {
        services = new AppServices(temp.Root);
        var store = new SproutStore(services.Paths);
        void Save(string trellis, string name, SproutState state, double daysAgo, bool inPlace = false)
        {
            var at = Now.AddDays(-daysAgo);
            store.Save(new SproutRecord(trellis, $@"C:\tb\{trellis}.trellis.cs", name, inPlace ? null : name, "stable", [], [], [], state, at, at,
                LastUpAt: state == SproutState.Down ? at : null));
        }
        Save("storefront", "feat-a", SproutState.Up, 0);
        Save("storefront", "demo", SproutState.Failed, 0.01);
        Save("storefront", "feat-c", SproutState.Down, 3);
        Save("storefront", "spike", SproutState.Down, 15);
        Save("services", "local", SproutState.Up, 0, inPlace: true);
        Save("analytics", "q4", SproutState.Down, 21);
        store.SavePlan(new Plan("storefront", "feat-a", "stable", "main",
            [new PlannedPort("shop", "api", "http", 8000, null, null), new PlannedPort("shop", "web", "http", 5170, null, null)],
            [new PlannedApp("shop", "../shop", "api", "dotnet", ".", [], null, [], []), new PlannedApp("shop", "../shop", "web", "node", ".", [], null, [], [])],
            [], [], [], []));
        services.HostStatus.Apply([new RunnerStatus("storefront/feat-a", "shop/api", RunnerState.Ready, 1, 8000, null, "a.log", Now)]);

        var vm = new SproutsViewModel(services) { Clock = () => Now };
        vm.Show(null);
        vm.RefreshCommand.Execute(null);
        return vm;
    }

    [Fact]
    public void Every_sprout_is_listed_by_trellis_with_counts_and_what_each_has_running()
    {
        using var temp = new TempStore();
        var vm = Make(temp, out _);

        Assert.Equal(["q4", "local", "demo", "feat-a", "feat-c", "spike"], vm.OverviewRows.Select(r => r.Name));
        Assert.Equal("6 sprouts of 3 trellises · 2 up · 1 failed · 3 down", vm.OverviewSummary);
        Assert.Equal(["All 6", "2 up", "1 failed", "3 down"], vm.OverviewChips.Select(c => c.Label));
        var featA = vm.OverviewRows.Single(r => r.Name == "feat-a");
        Assert.Equal("1 of 2 running", featA.Apps);
        Assert.Equal("5170–8000", featA.Ports);
        Assert.Equal("now", featA.LastUp);
        Assert.Equal("3 days ago", vm.OverviewRows.Single(r => r.Name == "feat-c").LastUp);
        Assert.Equal("in place", vm.OverviewRows.Single(r => r.Name == "local").Worktrees);

        vm.PickOverviewChipCommand.Execute(vm.OverviewChips.Single(c => c.Filter == OverviewFilter.Down));
        Assert.Equal(["q4", "feat-c", "spike"], vm.OverviewRows.Select(r => r.Name));
    }

    [Fact]
    public void The_shortcut_ticks_what_has_been_down_over_a_week_and_ticks_survive_a_refresh()
    {
        using var temp = new TempStore();
        var vm = Make(temp, out _);
        Assert.Equal("Select the 2 down for over a week", vm.StaleLabel);

        vm.SelectStaleCommand.Execute(null);

        Assert.Equal(["q4", "spike"], vm.OverviewRows.Where(r => r.IsChecked).Select(r => r.Name));
        Assert.Equal("2 selected", vm.CheckedLabel);
        Assert.True(vm.HasChecked);

        vm.RefreshCommand.Execute(null);
        Assert.Equal(2, vm.CheckedCount);

        vm.ClearCheckedCommand.Execute(null);
        Assert.False(vm.HasChecked);
        vm.ToggleAllCheckedCommand.Execute(null);
        Assert.Equal(6, vm.CheckedCount);
        Assert.True(vm.AllChecked);
    }

    [Fact]
    public void Opening_a_row_shows_that_sprout()
    {
        using var temp = new TempStore();
        var vm = Make(temp, out _);

        vm.OpenOverviewRowCommand.Execute(vm.OverviewRows.Single(r => r.Name == "feat-a"));

        Assert.Equal("storefront/feat-a", vm.Selected!.Record.Key);

        vm.BackToListCommand.Execute(null);

        Assert.Null(vm.Selected);
    }

    [Fact]
    public void The_search_matches_every_word_against_name_trellis_or_branch_and_works_with_the_state_chips()
    {
        using var temp = new TempStore();
        var vm = Make(temp, out _);

        vm.OverviewSearch = "feat";
        Assert.Equal(["feat-a", "feat-c"], vm.OverviewRows.Select(r => r.Name));

        vm.OverviewSearch = "STOREFRONT down";
        Assert.Empty(vm.OverviewRows);
        Assert.True(vm.NoOverviewMatches);

        vm.OverviewSearch = "storefront";
        vm.PickOverviewChipCommand.Execute(vm.OverviewChips.Single(c => c.Filter == OverviewFilter.Down));
        Assert.Equal(["feat-c", "spike"], vm.OverviewRows.Select(r => r.Name));

        vm.OverviewSearch = "in place";
        vm.PickOverviewChipCommand.Execute(vm.OverviewChips.Single(c => c.Filter == OverviewFilter.All));
        Assert.Equal(["local"], vm.OverviewRows.Select(r => r.Name));

        vm.ClearOverviewSearchCommand.Execute(null);
        Assert.Equal(6, vm.OverviewRows.Count);
        Assert.False(vm.NoOverviewMatches);
    }
}
