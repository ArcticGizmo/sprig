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
    static Guide WireStack => Guides.All.Single(g => g.Id == Guides.WireStackId);
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
        Assert.Equal(Anchors.RepoInputs, h.Vm.Coach.Mark!.Anchor);
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

    // --- Guide 2: wire up a multi-repo stack ---------------------------------

    [Fact]
    public void Guide_two_starts_at_repos_registered_and_needs_two_repos()
    {
        var guide = WireStack;
        Assert.Equal(SampleStage.ReposRegistered, guide.Stage);
    }

    [Fact]
    public async Task Guide_two_opens_the_builder_and_ends_on_a_create_step()
    {
        using var h = new Harness(SampleStage.ReposRegistered);
        await h.Vm.StartGuide(WireStack, onFinished: () => { });

        // Step 1 orients on the New-stack button; the builder-opening step then wires both repos.
        Assert.Equal(Anchors.StackNew, h.Vm.Coach.Mark!.Anchor);
        await h.Vm.Coach.NextCommand.ExecuteAsync(null);
        await h.Vm.Coach.NextCommand.ExecuteAsync(null);
        await h.Vm.Coach.NextCommand.ExecuteAsync(null);

        // The create step waits on the user; its anchor is the Create-stack button.
        Assert.True(h.Vm.Coach.IsWaiting);
        Assert.Equal(Anchors.StackCreate, h.Vm.Coach.Mark!.Anchor);
        Assert.Empty(h.Services.Stacks.List());
    }

    [Fact]
    public async Task Show_me_creates_the_stack_and_advances_past_the_wait()
    {
        using var h = new Harness(SampleStage.ReposRegistered);
        await h.Vm.StartGuide(WireStack, onFinished: () => { });
        await h.Vm.Coach.NextCommand.ExecuteAsync(null);
        await h.Vm.Coach.NextCommand.ExecuteAsync(null);
        await h.Vm.Coach.NextCommand.ExecuteAsync(null);
        var createIndex = h.Vm.Coach.Index;

        await h.Vm.Coach.ShowMeCommand.ExecuteAsync(null);

        // A stack now exists, wiring both repos, and the wait advanced off the create step on its own.
        var stack = Assert.Single(h.Services.Stacks.List());
        Assert.Equal(2, stack.Repos.Count);
        Assert.True(h.Vm.Coach.Index > createIndex);
    }

    // --- Guide 3: create and run a workspace ---------------------------------

    [Fact]
    public void Guide_three_starts_at_stack_wired()
        => Assert.Equal(SampleStage.StackWired, RunWorkspace.Stage);

    [Fact]
    public async Task Guide_three_ends_on_a_create_step_then_shows_what_was_made()
    {
        using var h = new Harness(SampleStage.StackWired);
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
            [RegisterRepo.Title, WireStack.Title, RunWorkspace.Title, RepairDrift.Title],
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
