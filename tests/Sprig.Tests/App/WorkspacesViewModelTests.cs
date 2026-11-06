using Sprig.App;
using Sprig.App.ViewModels;
using Sprig.Engine.Planning;
using Sprig.Engine.Workspaces;
using Sprig.Model;

namespace Sprig.Tests.App;

[Collection("dotnet-run")]
public class WorkspacesViewModelTests
{
    [Fact]
    public void Progress_rows_appear_as_steps_start_and_output_lands_on_the_running_one()
    {
        var progress = new OperationProgressViewModel("Starting storefront / feat-x");

        progress.Apply(new WorkspaceEvent("Plan", StepState.Running));
        progress.Apply(new WorkspaceEvent("Plan", StepState.Done, "4 apps"));
        progress.Apply(new WorkspaceEvent("Start x", StepState.Running));
        progress.Apply(new WorkspaceEvent("Start x", StepState.Output, "Container x Started"));
        progress.Apply(new WorkspaceEvent("", StepState.Output, "a line with no step"));

        Assert.Equal(["Plan", "Start x"], progress.Steps.Select(s => s.Label));
        Assert.Equal(ProgressState.Done, progress.Steps[0].State);
        Assert.Equal("4 apps", progress.Steps[0].Detail);
        Assert.Equal("Container x Started\na line with no step", progress.Steps[1].LiveOutput);
        Assert.True(progress.Steps[1].ShowOutput);
        Assert.False(progress.CanClose);

        progress.Finish("boom", ProgressState.Error);

        Assert.Equal(ProgressState.Error, progress.Steps[1].State);
        Assert.True(progress.CanClose);
        Assert.Equal("boom", progress.Summary);
    }

    [Fact]
    public void Cancelling_a_running_operation_signals_its_work_once_and_not_after_it_has_finished()
    {
        var progress = new OperationProgressViewModel("Stopping storefront / feat-a");
        Assert.True(progress.IsRunning);
        Assert.True(progress.CancelCommand.CanExecute(null));

        progress.Cancel();

        Assert.True(progress.Cancellation.IsCancellationRequested);
        Assert.True(progress.IsCancelling);
        Assert.False(progress.CancelCommand.CanExecute(null));
        Assert.False(progress.CanClose);

        progress.Finish("Cancelled part-way.", ProgressState.Warning);
        Assert.False(progress.IsRunning);

        var finished = new OperationProgressViewModel("Bringing storefront / feat-a up");
        finished.Finish("up", ProgressState.Done);
        finished.Cancel();
        Assert.False(finished.Cancellation.IsCancellationRequested);
    }

    [Fact]
    public async Task Work_that_ignores_a_cancel_is_abandoned_so_the_window_can_close()
    {
        using var cancel = new CancellationTokenSource();
        var stuck = new TaskCompletionSource();
        var waiting = WorkspacesViewModel.FinishedOrAbandoned(stuck.Task, cancel.Token, TimeSpan.FromMilliseconds(50));
        Assert.False(waiting.IsCompleted);

        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.False(stuck.Task.IsCompleted);
    }

    [Fact]
    public async Task Work_that_stops_when_cancelled_reports_its_own_outcome()
    {
        using var cancel = new CancellationTokenSource();
        var work = Task.Run(() => Task.Delay(Timeout.Infinite, cancel.Token));
        var waiting = WorkspacesViewModel.FinishedOrAbandoned(work, cancel.Token, TimeSpan.FromSeconds(10));

        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.True(work.IsCompleted);

        await WorkspacesViewModel.FinishedOrAbandoned(Task.CompletedTask, CancellationToken.None);
    }

    [Fact]
    public void The_page_lists_workspaces_counts_those_up_and_shows_the_selected_ones_apps_and_infra()
    {
        using var store = new TempStore();
        var services = new AppServices(store.Root);
        var workspaces = new WorkspaceStore(services.Paths);
        var now = DateTimeOffset.UtcNow;
        var repo = new WorkspaceRepo("../shop-services", "shop-services", @"C:\tb\shop-services", @"C:\tb\storefront--feat-x\shop-services", true, true);
        workspaces.Save(new WorkspaceRecord("storefront", @"C:\tb\storefront.orchestration.cs", "feat-x", "feat-x", "stable", [repo], [],
            [new WrittenFile(@"C:\tb\storefront--feat-x\shop-services\src\Catalog.Api\Properties\launchSettings.json", "launchSettings", true, "sprig")],
            WorkspaceState.Up, now, now, @"C:\tb\storefront--feat-x"));
        workspaces.Save(new WorkspaceRecord("storefront", @"C:\tb\storefront.orchestration.cs", "old", "old", "stable", [], [], [], WorkspaceState.Down, now, now));
        var at = new SourceLocation("x", 1);
        workspaces.SavePlan(new Plan("storefront", "feat-x", "stable", "main",
            [new PlannedPort("shop-services", "catalog-api", "http", 8000, null, null)],
            [new PlannedApp("shop-services", "../shop-services", "catalog-api", "dotnet", "src/Catalog.Api", [], null, [], [])],
            [],
            [new PlannedCompose("shop-services", "../shop-services", "docker-compose.yml", [],
                [new PlannedComposeService("postgres", [new PlannedMapping("tcp", 8002, 5432)])])],
            [], [new PlanProblem(ProblemSeverity.Warning, "careful", at)]));

        var vm = new WorkspacesViewModel(services);
        Assert.Null(vm.Selected);
        vm.Show("storefront/feat-x");

        Assert.Equal(["storefront / feat-x", "storefront / old"], vm.Workspaces.Select(s => s.Title));
        Assert.Equal(1, vm.NavCount);
        Assert.Equal("storefront / feat-x", vm.Selected!.Title);
        var app = Assert.Single(vm.Apps);
        Assert.Equal("http://localhost:8000", app.Url);
        Assert.Equal("Properties/launchSettings.json", app.Values);
        Assert.Equal("8002:5432", Assert.Single(vm.Infra).Ports);
        Assert.True(vm.HasWarnings);
        Assert.True(vm.CanDeleteBranch);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Run_it_asks_to_approve_the_definitions_then_brings_an_in_place_workspace_up_and_remembers_the_choice()
    {
        using var store = new TempStore();
        var services = new AppServices(store.Root);
        services.Settings.Save(new Sprig.Engine.Settings.SprigSettings { PortRangeStart = 9500, PortRangeEndExclusive = 9600 });

        var sdk = Path.Combine(RepoRoot(), "src", "Sprig.Sdk", "Sprig.Sdk.csproj");
        var repo = Path.Combine(store.Root, "work", "shop-web");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, ".gitignore"), ".env.local\n");
        File.WriteAllText(Path.Combine(repo, "sprig.cs"), $$"""
            #:project {{sdk}}
            using Sprig;
            var repo = Repo.Describe("shop-web");
            var web = repo.Node("web", ".").Http();
            web.EnvFile(".env.local", e => e.Set("PORT", web.Port));
            return repo.Emit(args);
            """);
        var processes = new Sprig.Engine.Processes.ProcessRunner();
        processes.Run("git", ["-C", repo, "init", "-q", "-b", "main"]).EnsureSuccess();
        var orchestration = Path.Combine(store.Root, "work", "web.orchestration.cs");
        File.WriteAllText(orchestration, $$"""
            #:project {{sdk}}
            using Sprig;
            var orchestration = Orchestration.Describe("web");
            orchestration.Repo("shop-web");
            return orchestration.Emit(args);
            """);
        services.Orchestrations.Add("web", orchestration);

        var vm = new WorkspacesViewModel(services);
        OperationProgressViewModel? shown = null;
        vm.ProgressStarted += p => shown = p;

        // The orchestration is approved first; only then does sprig know which repos' definitions to ask about. Both asks
        // happen in the modal, which closes once the workspace starts coming up.
        vm.OpenAddCommand.Execute(null);
        Assert.True(vm.NewHasWorktrees);
        vm.NewInPlace = true;
        Assert.Equal("Default: local", vm.NewNameHint);
        await vm.CreateCommand.ExecuteAsync(null);
        Assert.EndsWith("web.orchestration.cs", Assert.Single(vm.PendingTrust).Path);
        Assert.True(vm.TrustInModal);
        Assert.False(vm.TrustOnPage);
        await vm.ApproveAllCommand.ExecuteAsync(null);
        Assert.EndsWith("sprig.cs", Assert.Single(vm.PendingTrust).Path);
        Assert.True(vm.IsAddOpen);
        await vm.ApproveAllCommand.ExecuteAsync(null);

        Assert.False(vm.IsAddOpen);
        Assert.NotNull(shown);
        Assert.True(shown!.CanClose);
        Assert.Equal("web / local is up.", shown.Summary);
        Assert.Equal("web / local", vm.Selected!.Title);
        Assert.True(vm.Selected.IsUp);
        Assert.Matches(@"PORT=95\d\d", File.ReadAllText(Path.Combine(repo, ".env.local")));
        Assert.True(vm.Selected.Record.InPlace);

        // The next workspace of the orchestration starts from the same choices.
        Assert.True(new WorkspacesViewModel(new AppServices(store.Root)).NewInPlace);
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "sprig.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }

    [Fact]
    public void Making_a_workspace_without_an_orchestration_says_to_add_one()
    {
        using var store = new TempStore();
        var vm = new WorkspacesViewModel(new AppServices(store.Root));
        vm.OpenAddCommand.Execute(null);

        vm.CreateCommand.Execute(null);

        Assert.False(vm.HasOrchestrations);
        Assert.True(vm.FormFailed);
        Assert.Contains("Choose an orchestration", vm.FormMessage);
        Assert.True(vm.IsAddOpen);
        Assert.True(vm.MessageInModal);
        Assert.False(vm.MessageOnPage);

        vm.CancelAddCommand.Execute(null);
        Assert.False(vm.IsAddOpen);
        Assert.False(vm.MessageOnPage);
    }
}
