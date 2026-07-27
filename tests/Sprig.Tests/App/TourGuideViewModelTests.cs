using Sprig.App;
using Sprig.App.ViewModels;

namespace Sprig.Tests.App;

/// <summary>
/// Cover for the tour's narration script: stepping, the copy's discipline, and the fact that every stop
/// lands somewhere populated. A stop that navigates nowhere, or narrates chrome instead of the model, is
/// the failure mode this feature has to avoid (docs/guided-tour-plan.md §7, cost 2).
/// </summary>
public class TourGuideViewModelTests
{
    static (TourGuideViewModel tour, MainWindowViewModel main) Build()
    {
        var store = new TempStore();
        var main = new MainWindowViewModel(new AppServices(store.Root, isDemoStore: true));
        return (main.Tour, main);
    }

    [Fact]
    public void Starts_on_the_first_stop_and_shows_the_strip()
    {
        var (tour, _) = Build();

        // A demo session starts its own narration — the tour is the point, not an easter egg.
        Assert.True(tour.IsActive);
        Assert.Equal(0, tour.Index);
        Assert.Equal($"Step 1 of {tour.Count}", tour.StepCounter);
        Assert.False(tour.CanGoBack);
    }

    [Fact]
    public void Steps_forward_to_the_end_then_closes()
    {
        var (tour, _) = Build();

        for (var i = 0; i < tour.Count - 1; i++)
        {
            Assert.False(tour.IsLastStop);
            tour.NextCommand.Execute(null);
        }

        Assert.True(tour.IsLastStop);
        Assert.True(tour.IsActive);

        // Next on the last stop finishes the narration rather than running off the end.
        tour.NextCommand.Execute(null);
        Assert.False(tour.IsActive);
        Assert.Equal(tour.Count - 1, tour.Index);
    }

    [Fact]
    public void Steps_backward_and_never_past_the_start()
    {
        var (tour, _) = Build();

        tour.NextCommand.Execute(null);
        tour.NextCommand.Execute(null);
        Assert.Equal(2, tour.Index);

        tour.BackCommand.Execute(null);
        Assert.Equal(1, tour.Index);

        tour.BackCommand.Execute(null);
        tour.BackCommand.Execute(null);
        Assert.Equal(0, tour.Index);
    }

    [Fact]
    public void Skip_hides_the_narration_but_stays_in_the_tour()
    {
        var (tour, main) = Build();

        tour.SkipCommand.Execute(null);

        Assert.False(tour.IsActive);
        // Still the tour: the banner (and its way out) must remain.
        Assert.True(main.IsTour);
    }

    [Fact]
    public void Every_stop_navigates_somewhere_and_the_pages_visited_are_the_pipeline()
    {
        var (tour, main) = Build();

        var visited = new List<string>();
        for (var i = 0; i < tour.Count; i++)
        {
            visited.Add(main.CurrentPage.Title);
            if (!tour.IsLastStop) tour.NextCommand.Execute(null);
        }

        // Home → Repos → Stacks → Workspaces → Home: the model in the order it has to be learned.
        Assert.Equal(["Home", "Repos", "Stacks", "Workspaces", "Home"], visited);
    }

    [Fact]
    public void Every_stop_has_complete_copy()
    {
        var (tour, _) = Build();

        for (var i = 0; i < tour.Count; i++)
        {
            var stop = tour.Stop;
            Assert.False(string.IsNullOrWhiteSpace(stop.Kicker));
            Assert.False(string.IsNullOrWhiteSpace(stop.Heading));
            Assert.False(string.IsNullOrWhiteSpace(stop.Hint));
            Assert.False(string.IsNullOrWhiteSpace(stop.Cta));
            if (!tour.IsLastStop) tour.NextCommand.Execute(null);
        }
    }

    [Fact]
    public void No_stop_narrates_chrome()
    {
        var (tour, _) = Build();

        // Copy that names buttons or positions goes stale invisibly when the UI moves. Concepts and
        // values don't. This is a guardrail on the copy, not on the code.
        string[] banned = ["click the", "button on", "top right", "bottom left", "the third", "tab above"];

        for (var i = 0; i < tour.Count; i++)
        {
            var text = $"{tour.Stop.Heading} {tour.Stop.Hint}".ToLowerInvariant();
            foreach (var phrase in banned)
                Assert.DoesNotContain(phrase, text);
            if (!tour.IsLastStop) tour.NextCommand.Execute(null);
        }
    }

    [Fact]
    public void A_normal_session_does_not_narrate()
    {
        using var store = new TempStore();
        var main = new MainWindowViewModel(new AppServices(store.Root));

        Assert.False(main.Tour.IsActive);
    }
}
