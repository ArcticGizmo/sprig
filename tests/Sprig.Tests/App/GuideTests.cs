using Sprig.App;
using Sprig.App.Coach;
using Sprig.App.ViewModels;
using Sprig.Core.Demo;

namespace Sprig.Tests.App;

/// <summary>
/// Cover for the guide layer: the waiting/advance machinery in <see cref="CoachViewModel"/>, and guide 1
/// driven the way a user drives it. A guide hand-holds by waiting for the user to act, so the behaviour that
/// matters most is that a store change advances a waiting step, and "Show me" reaches the same place.
/// </summary>
[Collection("git-heavy")]
public class GuideTests
{
    /// <summary>A demo store at a chosen stage, with a real MainWindowViewModel over it.</summary>
    sealed class Harness : IDisposable
    {
        public string Root { get; }
        public AppServices Services { get; }
        public MainWindowViewModel Vm { get; }

        public Harness(SampleStage stage)
        {
            Root = Path.Combine(Path.GetTempPath(), "sprig-guide-test-" + Guid.NewGuid().ToString("N"));
            Services = new AppServices(Root, isDemoStore: true);
            Services.Sample.BuildTo(stage);
            Vm = new MainWindowViewModel(Services, dockerIsRunning: () => false);
        }

        public void Dispose()
        {
            try { Services.Sample.Destroy(); } catch { /* best-effort */ }
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); } catch { /* best-effort */ }
        }
    }

    static Guide RegisterRepo => Guides.All.Single(g => g.Id == Guides.RegisterRepoId);
    static Guide SplitModules => Guides.All.Single(g => g.Id == Guides.SplitModulesId);
    static Guide RunWorkspace => Guides.All.Single(g => g.Id == Guides.RunWorkspaceId);
    static Guide RepairDrift => Guides.All.Single(g => g.Id == Guides.RepairDriftId);

    [Fact]
    public void The_catalog_exposes_guide_one()
    {
        var guide = RegisterRepo;
        Assert.Equal(SampleStage.RepoOnDisk, guide.Stage);
        Assert.False(string.IsNullOrWhiteSpace(guide.Title));
        Assert.False(string.IsNullOrWhiteSpace(guide.Subtitle));
    }

    [Fact]
    public async Task Guide_one_starts_on_a_waiting_step_that_highlights_add_repo()
    {
        using var h = new Harness(SampleStage.RepoOnDisk);

        await h.Vm.StartGuide(RegisterRepo, onFinished: () => { });

        Assert.True(h.Vm.Coach.IsActive);
        Assert.Equal(Anchors.ReposAdd, h.Vm.Coach.Mark!.Anchor);
        Assert.True(h.Vm.Coach.IsWaiting, "the first step should wait for the user to register the repo");
    }

    [Fact]
    public async Task Registering_the_repo_advances_the_waiting_step_by_itself()
    {
        using var h = new Harness(SampleStage.RepoOnDisk);
        await h.Vm.StartGuide(RegisterRepo, onFinished: () => { });

        // The user registers sample-api however they like — here through the repo page's own add flow, which
        // is what fires StoreChanged. The waiting step is watching for exactly that.
        var apiPath = Path.Combine(h.Services.Sample.SampleReposDir, SampleFixtures.ApiRepo);
        await new ReposViewModel(h.Services).AddPathAsync(apiPath);

        Assert.Equal(1, h.Vm.Coach.Index); // advanced off the waiting step on its own
        Assert.Equal(Anchors.RepoModules, h.Vm.Coach.Mark!.Anchor);
    }

    [Fact]
    public async Task Show_me_reaches_the_same_place_as_doing_it_by_hand()
    {
        using var h = new Harness(SampleStage.RepoOnDisk);
        await h.Vm.StartGuide(RegisterRepo, onFinished: () => { });

        Assert.Null(h.Services.Repos.Get(SampleFixtures.ApiRepo));

        await h.Vm.Coach.ShowMeCommand.ExecuteAsync(null);

        // Show me registered the repo and, via the same StoreChanged path, advanced the wait.
        Assert.NotNull(h.Services.Repos.Get(SampleFixtures.ApiRepo));
        Assert.Equal(1, h.Vm.Coach.Index);
    }

    [Fact]
    public async Task Finishing_the_last_step_reports_completion_but_skipping_does_not()
    {
        using var h = new Harness(SampleStage.RepoOnDisk);
        var finished = 0;
        await h.Vm.StartGuide(RegisterRepo, onFinished: () => finished++);

        await h.Vm.Coach.ShowMeCommand.ExecuteAsync(null);      // step 1 → 2
        await h.Vm.Coach.NextCommand.ExecuteAsync(null);        // step 2 → 3
        await h.Vm.Coach.NextCommand.ExecuteAsync(null);        // Done

        Assert.False(h.Vm.Coach.IsActive);
        Assert.Equal(1, finished);
    }

    [Fact]
    public async Task Skipping_does_not_report_completion()
    {
        using var h = new Harness(SampleStage.RepoOnDisk);
        var finished = 0;
        await h.Vm.StartGuide(RegisterRepo, onFinished: () => finished++);

        h.Vm.Coach.SkipCommand.Execute(null);

        Assert.False(h.Vm.Coach.IsActive);
        Assert.Equal(0, finished);
    }

    [Fact]
    public async Task Guide_one_third_step_points_at_the_module_card()
    {
        using var h = new Harness(SampleStage.RepoOnDisk);
        await h.Vm.StartGuide(RegisterRepo, onFinished: () => { });

        await h.Vm.Coach.ShowMeCommand.ExecuteAsync(null);   // step 1 (waiting) → 2: inputs
        Assert.Equal(Anchors.RepoModules, h.Vm.Coach.Mark!.Anchor);
        await h.Vm.Coach.NextCommand.ExecuteAsync(null);     // step 2 → 3: the module card

        // Step 3 now introduces modules (and forward-refs the modules guide), so it anchors the module card.
        Assert.Equal(Anchors.RepoModules, h.Vm.Coach.Mark!.Anchor);
    }

    // --- Guide 2: split a repo into modules ----------------------------------

    [Fact]
    public void Guide_split_modules_starts_at_repos_registered()
        => Assert.Equal(SampleStage.ReposRegistered, SplitModules.Stage);

    [Fact]
    public async Task Guide_split_modules_opens_the_editor_and_adds_a_second_module()
    {
        using var h = new Harness(SampleStage.ReposRegistered);
        await h.Vm.StartGuide(SplitModules, onFinished: () => { });

        var repos = h.Vm.Pages.OfType<ReposViewModel>().Single();

        // Step 1 opens sample-api's editor (one module: "app") and orients on the module card.
        Assert.Equal(Anchors.RepoModules, h.Vm.Coach.Mark!.Anchor);
        Assert.NotNull(repos.Editor);
        Assert.Single(repos.Editor!.Modules);

        await h.Vm.Coach.NextCommand.ExecuteAsync(null);   // inputs are shared
        Assert.Equal(Anchors.RepoModules, h.Vm.Coach.Mark!.Anchor);

        await h.Vm.Coach.NextCommand.ExecuteAsync(null);   // the add-module button
        Assert.Equal(Anchors.RepoAddModule, h.Vm.Coach.Mark!.Anchor);
        Assert.Single(repos.Editor!.Modules);              // not added until the next step

        await h.Vm.Coach.NextCommand.ExecuteAsync(null);   // the add step adds a real second module
        Assert.Equal(Anchors.RepoModules, h.Vm.Coach.Mark!.Anchor);
        Assert.Equal(2, repos.Editor!.Modules.Count);
        var added = repos.Editor!.Modules.Last();
        Assert.Equal("api", added.Name);
        Assert.Equal("apps/api", added.Path);
        Assert.Same(added, repos.Editor!.SelectedModule);

        await h.Vm.Coach.NextCommand.ExecuteAsync(null);   // whole-page handoff
        Assert.Null(h.Vm.Coach.Mark!.Anchor);
    }

    [Fact]
    public async Task Guide_split_modules_does_not_stack_duplicate_modules_when_stepping_back()
    {
        using var h = new Harness(SampleStage.ReposRegistered);
        await h.Vm.StartGuide(SplitModules, onFinished: () => { });

        var repos = h.Vm.Pages.OfType<ReposViewModel>().Single();

        // Advance to the add step, then step back and forward again — the add is idempotent.
        await h.Vm.Coach.NextCommand.ExecuteAsync(null);
        await h.Vm.Coach.NextCommand.ExecuteAsync(null);
        await h.Vm.Coach.NextCommand.ExecuteAsync(null);   // add step
        Assert.Equal(2, repos.Editor!.Modules.Count);

        await h.Vm.Coach.BackCommand.ExecuteAsync(null);   // back to the add-module button
        await h.Vm.Coach.NextCommand.ExecuteAsync(null);   // add step again
        Assert.Equal(2, repos.Editor!.Modules.Count);      // still two, not three
    }

    // --- Guide 3: create and run a workspace ---------------------------------

    [Fact]
    public void Guide_three_starts_at_map_ready()
        => Assert.Equal(SampleStage.MapReady, RunWorkspace.Stage);

    [Fact]
    public async Task Guide_three_ends_on_a_create_step_then_shows_what_was_made()
    {
        using var h = new Harness(SampleStage.MapReady);
        await h.Vm.StartGuide(RunWorkspace, onFinished: () => { });

        Assert.Equal(Anchors.WorkspaceNew, h.Vm.Coach.Mark!.Anchor);
        await h.Vm.Coach.NextCommand.ExecuteAsync(null);

        // The create step waits on the user, opening the pre-filled form (infra off, so no daemon needed).
        Assert.True(h.Vm.Coach.IsWaiting);
        Assert.Equal(Anchors.WorkspaceCreate, h.Vm.Coach.Mark!.Anchor);
        Assert.Empty(h.Services.Workspaces.List());

        await h.Vm.Coach.ShowMeCommand.ExecuteAsync(null);

        // A workspace now exists (real worktrees), and the wait advanced to the detail step.
        var record = Assert.Single(h.Services.Workspaces.List());
        Assert.Equal(2, record.Repos.Count);
        Assert.Equal(Anchors.WorkspaceDetail, h.Vm.Coach.Mark!.Anchor);
    }

    // --- Guide 4: recover from drift -----------------------------------------

    [Fact]
    public void Guide_four_starts_from_a_running_workspace()
        => Assert.Equal(SampleStage.Running, RepairDrift.Stage);

    [Fact]
    public async Task Guide_four_breaks_a_worktree_then_repair_resolves_the_drift()
    {
        using var h = new Harness(SampleStage.Running);
        await h.Vm.StartGuide(RepairDrift, onFinished: () => { });

        // The opening step deletes a worktree behind the user's back and reconciles, so drift is real.
        var drifted = h.Services.Reconciler.Inspect(SampleSetup.WorkspaceName);
        Assert.NotNull(drifted);
        Assert.True(drifted!.HasDrift, "the opening step should have broken a worktree");

        await h.Vm.Coach.NextCommand.ExecuteAsync(null);
        Assert.True(h.Vm.Coach.IsWaiting);
        Assert.Equal(Anchors.WorkspaceRepair, h.Vm.Coach.Mark!.Anchor);

        // Repair reconciles record and reality; the drift is gone and the wait advances on its own.
        await h.Vm.Coach.ShowMeCommand.ExecuteAsync(null);
        Assert.False(h.Services.Reconciler.Inspect(SampleSetup.WorkspaceName)!.HasDrift);
        Assert.Null(h.Vm.Coach.Mark!.Anchor);   // advanced to the whole-page finale
    }

    [Fact]
    public void Learn_page_lists_every_guide_in_ladder_order()
    {
        using var store = new TempStore();
        var learn = new LearnViewModel(new AppServices(store.Root), new Navigator());

        Assert.Equal(
            [RegisterRepo.Title, SplitModules.Title, RunWorkspace.Title, RepairDrift.Title],
            learn.Guides.Select(g => g.Title));
    }

    [Fact]
    public void Learn_page_reflects_completion_from_settings()
    {
        using var store = new TempStore();
        var services = new AppServices(store.Root);

        var before = new LearnViewModel(services, new Navigator());
        Assert.False(before.Guides.Single(g => g.Title == RegisterRepo.Title).Completed);

        var settings = services.Settings.Get();
        settings.CompletedGuides.Add(Guides.RegisterRepoId);
        services.Settings.Save(settings);

        var after = new LearnViewModel(services, new Navigator());
        var item = after.Guides.Single(g => g.Title == RegisterRepo.Title);
        Assert.True(item.Completed);
        Assert.Equal("Replay", item.ActionLabel);
    }
}
