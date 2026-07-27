using Sprig.App;
using Sprig.App.ViewModels;
using Sprig.Core.Demo;
using Sprig.Core.Store;

namespace Sprig.Tests.App;

/// <summary>
/// Cover for how the tour is found and how its leftovers are removed — M5 of docs/guided-tour-plan.md.
/// The tour is worthless if a new user has to hunt for it, and rude if abandoning it silently leaves two
/// git repos and a stopped container on their disk.
/// </summary>
public class TourDiscoveryTests
{
    [Fact]
    public void Home_offers_the_tour_through_the_navigator_without_knowing_about_stores()
    {
        using var store = new TempStore();
        var main = new MainWindowViewModel(new AppServices(store.Root), dockerIsRunning: () => false);
        var home = main.Pages.OfType<HomeViewModel>().Single();

        // Home holds no notion of a demo store — it asks the navigator, which the main window wired to
        // the store swap. Nothing here should throw or need a session.
        home.ShowWorkingSetupCommand.Execute(null);
    }

    [Fact]
    public void The_tour_is_offered_on_first_run()
    {
        using var store = new TempStore();
        var main = new MainWindowViewModel(new AppServices(store.Root), dockerIsRunning: () => false);
        var home = main.Pages.OfType<HomeViewModel>().Single();

        // An empty store is exactly when "what am I aiming at?" can't be answered by the app itself, so
        // the hero (which carries the tour button) has to be the thing on screen.
        Assert.True(home.IsEmptyStage);
        Assert.True(home.ShowWorkingSetupCommand.CanExecute(null));
    }

    [Fact]
    public void Settings_reports_no_sample_when_the_demo_store_is_absent()
    {
        using var store = new TempStore();
        var settings = new SettingsViewModel(new AppServices(store.Root));

        settings.IsActive = true;   // triggers OnActivated

        // This machine may or may not have a real demo store from a previous tour, so the assertion is
        // the invariant rather than the value: what Settings reports matches what's on disk.
        Assert.Equal(Directory.Exists(SprigPaths.DemoRoot), settings.HasSample);
    }

    [Fact]
    public async Task Deleting_the_sample_removes_a_built_demo_store()
    {
        // Build a sample in a temp root, then delete it the way the Settings button does — through a
        // SampleSetup rooted at the demo store, not at the page's own store.
        var root = Path.Combine(Path.GetTempPath(), "sprig-m5-" + Guid.NewGuid().ToString("N"));
        var demo = new AppServices(root, isDemoStore: true);
        try
        {
            await AppServices.RunAsync(() => demo.Sample.Build());
            Assert.True(Directory.Exists(root));
            Assert.NotNull(demo.Sample.Existing());

            await AppServices.RunAsync(demo.Sample.Destroy);

            Assert.False(Directory.Exists(root));
            Assert.Null(demo.Sample.Existing());
        }
        finally
        {
            try { demo.Sample.Destroy(); } catch { /* already gone */ }
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Destroy_never_touches_a_real_store()
    {
        // The guard that makes the Settings button safe: a store without the tour's marker is refused,
        // however it was reached.
        using var store = new TempStore();
        var real = new AppServices(store.Root);
        Directory.CreateDirectory(store.Root);
        var reposFile = store.Paths.ReposFile;
        File.WriteAllText(reposFile, """{"repos":{}}""");

        Assert.Throws<SampleSetupException>(real.Sample.Destroy);
        Assert.True(File.Exists(reposFile));
    }
}
