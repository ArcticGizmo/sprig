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

        Assert.Equal(["storefront--feat-x", "storefront--old"], vm.Workspaces.Select(s => s.Title));
        Assert.Equal(1, vm.NavCount);
        Assert.Equal("storefront--feat-x", vm.Selected!.Title);
        var app = Assert.Single(vm.Apps);
        Assert.Equal("http://localhost:8000", app.Url);
        Assert.Equal("Properties/launchSettings.json", app.Values);
        Assert.Equal("8002:5432", Assert.Single(vm.Infra).Ports);
        Assert.True(vm.HasWarnings);
        Assert.True(vm.CanDeleteBranch);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Run_it_asks_to_approve_the_definitions_then_brings_a_worktree_workspace_up()
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
        Commit(repo);
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
        vm.NewBranch = "feat-x";
        Assert.Equal("Goes in web--1, a new slot.", vm.NewSlotLine);
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
        Assert.Equal("feat-x is up in web--1.", shown.Summary);
        Assert.Equal("web--1", vm.Selected!.Title);
        Assert.Equal("feat-x", vm.Selected.Branch);
        Assert.True(vm.Selected.IsUp);
        Assert.True(vm.Selected.CanRelease);
        Assert.Equal("stable", vm.Selected.Record.PortPolicy);
        var env = Path.Combine(store.Root, "work", "web--1", "shop-web", ".env.local");
        Assert.Matches(@"PORT=95\d\d", File.ReadAllText(env));
        Assert.False(File.Exists(Path.Combine(repo, ".env.local")));

        // Released, the slot is free; the form then offers it, with its data to keep or empty.
        await vm.ReleaseCommand.ExecuteAsync(null);
        Assert.True(vm.Selected!.IsFree);
        Assert.Equal("free", vm.Selected.Branch);
        Assert.False(vm.ShowRunControls);
        vm.CheckOutHereCommand.Execute(null);
        vm.NewBranch = "feat-y";
        Assert.StartsWith("Reuses web--1, which is free", vm.NewSlotLine);
        Assert.True(vm.NewReusesSlot);
        Assert.True(vm.NewKeepData);
        await vm.CreateCommand.ExecuteAsync(null);
        Assert.Equal(("web--1", "feat-y"), (vm.Selected!.Title, vm.Selected.Branch));
        Assert.True(File.Exists(env));

        services.Workspaces.Remove("web", "1", new RemoveOptions(DeleteBranch: true));
    }

    /// <summary>A first commit with everything in the folder, so worktrees have the definition and its .gitignore.</summary>
    static void Commit(string repo)
    {
        var processes = new Sprig.Engine.Processes.ProcessRunner();
        processes.Run("git", ["-C", repo, "init", "-q", "-b", "main"]).EnsureSuccess();
        processes.Run("git", ["-C", repo, "add", "-A"]).EnsureSuccess();
        processes.Run("git", ["-C", repo, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", "init"]).EnsureSuccess();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task A_setting_nothing_gives_is_asked_for_in_the_form_and_a_workspaces_settings_change_its_files()
    {
        using var store = new TempStore();
        var services = new AppServices(store.Root);
        services.Settings.Save(new Sprig.Engine.Settings.SprigSettings { PortRangeStart = 9600, PortRangeEndExclusive = 9700 });

        var sdk = Path.Combine(RepoRoot(), "src", "Sprig.Sdk", "Sprig.Sdk.csproj");
        var repo = Path.Combine(store.Root, "work", "shop-web");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, ".gitignore"), ".env.local\n");
        File.WriteAllText(Path.Combine(repo, "sprig.cs"), $$"""
            #:project {{sdk}}
            using Sprig;
            var repo = Repo.Describe("shop-web");
            var web = repo.Node("web", ".").Http();
            web.EnvFile(".env.local", e => e
                .Set("API", repo.Setting<Url>("api-url", "http://localhost:5100"))
                .Set("KEY", repo.Setting<Secret>("api-key")));
            return repo.Emit(args);
            """);
        Commit(repo);
        var entry = services.Authoring.AddRepo(repo, "shop-web");
        services.Trust.Approve(entry.DefinitionFile, Sprig.Engine.Definitions.DefinitionHash.Of(File.ReadAllText(entry.DefinitionFile)));

        var vm = new WorkspacesViewModel(services);
        vm.ProgressStarted += _ => { };
        vm.NewOrchestration = "shop-web";
        vm.OpenAddCommand.Execute(null);
        vm.NewBranch = "feat-x";
        await WaitFor(() => vm.HasNewSettings);

        var key = vm.NewSettings.Single(s => s.Name == "api-key");
        var url = vm.NewSettings.Single(s => s.Name == "api-url");
        Assert.True(key.Required);
        Assert.True(key.IsSecret);
        Assert.False(url.Required);
        Assert.Equal("Empty: http://localhost:5100", url.Hint);

        // Left empty, it's asked for and nothing is made.
        await vm.CreateCommand.ExecuteAsync(null);
        Assert.True(vm.IsAddOpen);
        Assert.Contains("Give api-key a value", vm.FormMessage);
        Assert.Empty(services.Workspaces.List());

        key.Value = "k1";
        await vm.CreateCommand.ExecuteAsync(null);
        Assert.False(vm.IsAddOpen);
        Assert.True(vm.Selected!.IsUp);
        var env = Path.Combine(store.Root, "work", "shop-web--1", ".env.local");
        Assert.Contains("KEY=k1", File.ReadAllText(env));
        Assert.Contains("API=http://localhost:5100", File.ReadAllText(env));

        await vm.OpenSettingsCommand.ExecuteAsync(null);
        Assert.True(vm.IsSettingsOpen);
        Assert.Equal("k1", vm.WorkspaceSettings.Single(s => s.Name == "api-key").Kept);
        vm.WorkspaceSettings.Single(s => s.Name == "api-url").Value = "https://api.example.com";
        await vm.SaveSettingsCommand.ExecuteAsync(null);

        Assert.False(vm.IsSettingsOpen);
        Assert.Contains("API=https://api.example.com", File.ReadAllText(env));
        Assert.Equal("Its files have the new values. Restart web to use them.", vm.AppsMessage);
        Assert.Equal("https://api.example.com", services.Workspaces.Get("shop-web", "1")!.Values!["api-url"]);

        // The slot keeps its values when it's released. Checking the next branch out starts from them, and a box
        // emptied clears that value, so its default applies again.
        await vm.ReleaseCommand.ExecuteAsync(null);
        vm.CheckOutHereCommand.Execute(null);
        vm.NewBranch = "feat-y";
        await WaitFor(() => vm.NewSettings.Any(s => s.Name == "api-url" && s.Value == "https://api.example.com"));
        Assert.Equal("k1", vm.NewSettings.Single(s => s.Name == "api-key").Value);
        vm.NewSettings.Single(s => s.Name == "api-url").Value = "";
        await vm.CreateCommand.ExecuteAsync(null);
        Assert.Equal("feat-y", vm.Selected!.Branch);
        Assert.Contains("API=http://localhost:5100", File.ReadAllText(env));
        Assert.Contains("KEY=k1", File.ReadAllText(env));

        services.Workspaces.Remove("shop-web", "1", new RemoveOptions(DeleteBranch: true));
    }

    static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 600 && !condition(); i++) await Task.Delay(100);
        Assert.True(condition());
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
