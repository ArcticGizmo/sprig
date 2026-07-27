using Sprig.App;
using Sprig.App.ViewModels;

namespace Sprig.Tests.App;

/// <summary>
/// Cover for the tour's narration script: stepping, the copy's discipline, the fact that every stop lands
/// somewhere populated, and the Docker gate. A stop that navigates nowhere, narrates chrome instead of
/// the model, or offers an action that can't work is the failure mode this feature has to avoid
/// (docs/guided-tour-plan.md §6, §7).
///
/// The Docker probe is injected so the script is the same on a machine with a daemon and one without —
/// otherwise these tests would assert different things depending on who ran them.
/// </summary>
public class TourGuideViewModelTests
{
    /// <summary>A started tour over a demo session, with Docker's availability pinned.</summary>
    static async Task<(TourGuideViewModel tour, MainWindowViewModel main, TempStore store)> StartAsync(
        bool dockerRunning = false)
    {
        var store = new TempStore();
        var main = new MainWindowViewModel(
            new AppServices(store.Root, isDemoStore: true), dockerIsRunning: () => dockerRunning);
        // The constructor kicks the tour off in the background; awaiting it here makes the state
        // deterministic rather than racing that continuation.
        await main.Tour.StartAsync();
        return (main.Tour, main, store);
    }

    [Fact]
    public async Task Starts_on_the_first_stop_and_shows_the_strip()
    {
        var (tour, _, store) = await StartAsync();
        using var _s = store;

        Assert.True(tour.IsActive);
        Assert.Equal(0, tour.Index);
        Assert.Equal($"Step 1 of {tour.Count}", tour.StepCounter);
        Assert.False(tour.CanGoBack);
    }

    [Fact]
    public async Task Steps_forward_to_the_end_then_closes()
    {
        var (tour, _, store) = await StartAsync();
        using var _s = store;

        for (var i = 0; i < tour.Count - 1; i++)
        {
            Assert.False(tour.IsLastStop);
            await tour.NextCommand.ExecuteAsync(null);
        }

        Assert.True(tour.IsLastStop);
        Assert.True(tour.IsActive);

        // Next on the last stop finishes the narration rather than running off the end.
        await tour.NextCommand.ExecuteAsync(null);
        Assert.False(tour.IsActive);
        Assert.Equal(tour.Count - 1, tour.Index);
    }

    [Fact]
    public async Task Steps_backward_and_never_past_the_start()
    {
        var (tour, _, store) = await StartAsync();
        using var _s = store;

        await tour.NextCommand.ExecuteAsync(null);
        await tour.NextCommand.ExecuteAsync(null);
        Assert.Equal(2, tour.Index);

        tour.BackCommand.Execute(null);
        Assert.Equal(1, tour.Index);

        tour.BackCommand.Execute(null);
        tour.BackCommand.Execute(null);
        Assert.Equal(0, tour.Index);
    }

    [Fact]
    public async Task Skip_hides_the_narration_but_stays_in_the_tour()
    {
        var (tour, main, store) = await StartAsync();
        using var _s = store;

        tour.SkipCommand.Execute(null);

        Assert.False(tour.IsActive);
        // Still the tour: the banner (and its way out) must remain.
        Assert.True(main.IsTour);
    }

    [Fact]
    public async Task Without_docker_the_script_is_five_stops_through_the_pipeline()
    {
        var (tour, main, store) = await StartAsync(dockerRunning: false);
        using var _s = store;

        Assert.Equal(5, tour.Count);

        var visited = new List<string>();
        for (var i = 0; i < tour.Count; i++)
        {
            visited.Add(main.CurrentPage.Title);
            if (!tour.IsLastStop) await tour.NextCommand.ExecuteAsync(null);
        }

        // Home → Repos → Stacks → Workspaces → Home: the model in the order it has to be learned.
        Assert.Equal(["Home", "Repos", "Stacks", "Workspaces", "Home"], visited);
    }

    [Fact]
    public async Task With_docker_an_extra_infra_stop_appears_before_the_last_one()
    {
        var (tour, _, store) = await StartAsync(dockerRunning: true);
        using var _s = store;

        Assert.Equal(6, tour.Count);

        // The infra stop is the only one that performs an action, and it is never the finale — the tour
        // still ends by handing the user back to their own repos.
        var performing = new List<int>();
        for (var i = 0; i < tour.Count; i++)
        {
            if (tour.Stop.Perform is not null) performing.Add(i);
            if (!tour.IsLastStop) { tour.Index++; }
        }

        Assert.Equal([tour.Count - 2], performing);
    }

    [Fact]
    public async Task No_stop_performs_an_action_when_docker_is_absent()
    {
        var (tour, _, store) = await StartAsync(dockerRunning: false);
        using var _s = store;

        // The whole tour must stand up offline: compose generation is file I/O, and that's the lesson.
        for (var i = 0; i < tour.Count; i++)
        {
            Assert.Null(tour.Stop.Perform);
            if (!tour.IsLastStop) tour.Index++;
        }
    }

    [Fact]
    public async Task Every_stop_has_complete_copy()
    {
        var (tour, _, store) = await StartAsync(dockerRunning: true);
        using var _s = store;

        for (var i = 0; i < tour.Count; i++)
        {
            var stop = tour.Stop;
            Assert.False(string.IsNullOrWhiteSpace(stop.Kicker));
            Assert.False(string.IsNullOrWhiteSpace(stop.Heading));
            Assert.False(string.IsNullOrWhiteSpace(stop.Hint));
            Assert.False(string.IsNullOrWhiteSpace(stop.Cta));
            if (!tour.IsLastStop) tour.Index++;
        }
    }

    [Fact]
    public async Task No_stop_narrates_chrome()
    {
        var (tour, _, store) = await StartAsync(dockerRunning: true);
        using var _s = store;

        // Copy that names buttons or positions goes stale invisibly when the UI moves. Concepts and
        // values don't. This is a guardrail on the copy, not on the code.
        string[] banned = ["click the", "press the", "button on", "top right", "bottom left", "the third", "tab above"];

        for (var i = 0; i < tour.Count; i++)
        {
            var text = $"{tour.Stop.Heading} {tour.Stop.Hint}".ToLowerInvariant();
            foreach (var phrase in banned)
                Assert.DoesNotContain(phrase, text);
            if (!tour.IsLastStop) tour.Index++;
        }
    }

    [Fact]
    public void A_normal_session_does_not_narrate()
    {
        using var store = new TempStore();
        var main = new MainWindowViewModel(new AppServices(store.Root), dockerIsRunning: () => false);

        Assert.False(main.Tour.IsActive);
    }
}
