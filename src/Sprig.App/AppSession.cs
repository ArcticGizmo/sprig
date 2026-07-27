using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Sprig.App.Coach;
using Sprig.App.ViewModels;
using Sprig.App.Views;
using Sprig.Core.Demo;
using Sprig.Core.Settings;
using Sprig.Core.Store;
using Sprig.Core.Workspaces;

namespace Sprig.App;

/// <summary>
/// Owns the main window and decides which central store the app is looking at, so the guided tour can
/// run against a throwaway demo store and then hand the app back to the real one.
///
/// This is the <b>only</b> place that knows the tour exists. Entering it swaps in a fresh
/// <see cref="AppServices"/> rooted at <see cref="SprigPaths.DemoRoot"/> and a fresh
/// <see cref="MainWindowViewModel"/> over it; leaving swaps back to a fresh real-store pair (fresh, so
/// the real store is re-read rather than served from the stale view models that were parked). Every
/// page below is built the same way in both cases and cannot tell the difference — which is what keeps
/// the tour from costing anything to maintain (docs/guided-tour-plan.md §7).
/// </summary>
public sealed class AppSession(Window window)
{
    /// <summary>The services the window is currently bound to.</summary>
    public AppServices Services { get; private set; } = null!;

    /// <summary>Bind the window to the user's real store. Called once at startup.</summary>
    public void ShowReal() => Bind(new AppServices());

    /// <summary>
    /// Build the sample setup if needed, then bind the window to the demo store. The build is the slow
    /// part (two git repos and two worktrees), so it runs off the UI thread behind the same progress
    /// checklist a workspace create uses, and the window is only swapped once it has succeeded.
    /// </summary>
    public async Task EnterTourAsync(Action<OperationProgressViewModel> showProgress)
    {
        var demo = new AppServices(SprigPaths.DemoRoot, isDemoStore: true);

        // Already built (a previous tour the user left without cleaning up): swap straight in, no
        // checklist for work that isn't happening.
        if (await AppServices.RunAsync(() => demo.Sample.Existing()) is not null)
        {
            Bind(demo);
            StartTourNarration();
            return;
        }

        var modal = new OperationProgressViewModel("Building your sample setup");
        modal.Load(Core.Demo.SampleSetup.PlanBuild());
        showProgress(modal);

        try
        {
            var progress = new Progress<WorkspaceStepProgress>(modal.Apply);
            await AppServices.RunAsync(() => demo.Sample.Build(progress));
            modal.Finish("Sample setup ready — this is what a working sprig looks like.",
                WorkspaceStepState.Done);
            Bind(demo);
            StartTourNarration();
        }
        catch (Exception ex)
        {
            // SampleSetup already unwound its own mess; all that's left is to say so and stay put on
            // the real store, which was never touched.
            modal.Finish($"Couldn't build the sample: {ex.Message}", WorkspaceStepState.Error);
        }
    }

    /// <summary>
    /// Reset the demo sandbox to the guide's starting stage, bind the window to it, and start the guide's
    /// coachmarks. A guide always rebuilds the sandbox from clean (it hands the user a known starting point),
    /// so re-entering a guide can never inherit a previous run's mess.
    /// </summary>
    public async Task EnterGuideAsync(Guide guide, Action<OperationProgressViewModel> showProgress)
    {
        var demo = new AppServices(SprigPaths.DemoRoot, isDemoStore: true);

        var modal = new OperationProgressViewModel($"Setting up: {guide.Title}");
        modal.Load(SampleSetup.PlanBuild(guide.Stage));
        showProgress(modal);

        try
        {
            var progress = new Progress<WorkspaceStepProgress>(modal.Apply);
            await AppServices.RunAsync(() => demo.Sample.BuildTo(guide.Stage, progress));
            modal.Finish("Ready — follow the highlights.", WorkspaceStepState.Done);
        }
        catch (Exception ex)
        {
            // The sandbox unwinds itself on failure; stay on the real store, which was never touched.
            modal.Finish($"Couldn't set up the lesson: {ex.Message}", WorkspaceStepState.Error);
            return;
        }

        Bind(demo);
        if (window.DataContext is MainWindowViewModel vm)
            _ = vm.StartGuide(guide, () => MarkGuideComplete(guide.Id));
    }

    /// <summary>
    /// Record a finished guide against the <b>real</b> store's settings — never the demo store's, which is
    /// deleted on exit. Reads and writes a real settings store directly, independent of whichever store the
    /// window is currently bound to.
    /// </summary>
    static void MarkGuideComplete(string guideId)
    {
        try
        {
            var settingsStore = new FileSettingsStore(new SprigPaths());
            var settings = settingsStore.Get();
            if (settings.CompletedGuides.Contains(guideId)) return;
            settings.CompletedGuides.Add(guideId);
            settingsStore.Save(settings);
        }
        catch { /* completion ticks are a nicety; never let one fail a lesson */ }
    }

    /// <summary>
    /// Leave the tour and go back to the real store. <paramref name="deleteSample"/> removes the demo
    /// store entirely; keeping it makes re-entering instant, at the cost of some disk.
    /// </summary>
    public async Task ExitTourAsync(bool deleteSample)
    {
        var demo = Services;
        Bind(new AppServices());

        if (deleteSample && demo.IsDemoStore)
            await AppServices.RunAsync(demo.Sample.Destroy);
    }

    void Bind(AppServices services)
    {
        Services = services;
        window.DataContext = new MainWindowViewModel(services, this);
        window.Title = MainWindowViewModel.TitleFor(services);
    }

    void StartTourNarration()
    {
        if (window.DataContext is MainWindowViewModel vm) _ = vm.StartTourNarration();
    }
}
