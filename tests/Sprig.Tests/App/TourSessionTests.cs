using Sprig.App;
using Sprig.App.ViewModels;
using Sprig.Core.Demo;
using Sprig.Core.Store;

namespace Sprig.Tests.App;

/// <summary>
/// Cover for the one branch the guided tour is allowed to have: which store the app is bound to, and
/// the banner that says so. The pages below <see cref="MainWindowViewModel"/> are built identically in
/// both cases and are deliberately not represented here — if a test ever needs to assert "page X
/// behaves differently in the tour", the design has drifted (docs/guided-tour-plan.md §7).
/// </summary>
public class TourSessionTests
{
    [Fact]
    public void A_normal_session_is_not_a_tour()
    {
        using var store = new TempStore();
        var vm = new MainWindowViewModel(new AppServices(store.Root));

        Assert.False(vm.IsTour);
    }

    [Fact]
    public void A_demo_session_is_a_tour()
    {
        using var store = new TempStore();
        var vm = new MainWindowViewModel(new AppServices(store.Root, isDemoStore: true));

        Assert.True(vm.IsTour);
    }

    [Fact]
    public void Tour_title_is_marked_and_a_normal_title_is_not()
    {
        using var store = new TempStore();

        Assert.Contains("Guided tour",
            MainWindowViewModel.TitleFor(new AppServices(store.Root, isDemoStore: true)));
        Assert.DoesNotContain("Guided tour",
            MainWindowViewModel.TitleFor(new AppServices(store.Root)));
    }

    [Fact]
    public void The_demo_root_is_never_the_real_store_root()
    {
        // The whole safety story rests on these being different directories.
        Assert.NotEqual(new SprigPaths().Root, SprigPaths.DemoRoot);
        Assert.Contains("(Demo)", SprigPaths.DemoRoot);
    }

    [Fact]
    public void Tour_commands_are_inert_without_a_session()
    {
        // Headless renders and VM tests construct the view model with no AppSession; the tour commands
        // must no-op rather than throw.
        using var store = new TempStore();
        var vm = new MainWindowViewModel(new AppServices(store.Root));

        vm.EnterTourCommand.Execute(null);
        vm.ExitTourCommand.Execute(null);
        vm.ExitTourKeepingSampleCommand.Execute(null);
    }

    [Fact]
    public void The_build_checklist_matches_what_build_reports()
    {
        // The progress window renders rows from PlanBuild and then matches reports to them by id, so a
        // plan row with no matching report (or vice versa) would silently show a step that never moves.
        var plan = SampleSetup.PlanBuild();
        Assert.Equal(4, plan.Count);
        Assert.All(plan, step => Assert.False(string.IsNullOrWhiteSpace(step.Label)));

        using var store = new TempStore();
        var services = new AppServices(store.Root, isDemoStore: true);
        var modal = new OperationProgressViewModel("Building your sample setup");
        modal.Load(plan);

        var reported = new List<string>();
        var progress = new Progress<Core.Workspaces.WorkspaceStepProgress>(r => reported.Add(r.StepId));
        try
        {
            services.Sample.Build(progress);
            // Every planned row was reported against — no dead rows.
            Assert.Equal(plan.Select(s => s.Id).ToHashSet(), reported.ToHashSet());
        }
        finally
        {
            try { services.Sample.Destroy(); } catch { /* best-effort */ }
        }
    }
}
