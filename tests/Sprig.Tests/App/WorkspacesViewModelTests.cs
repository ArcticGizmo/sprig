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
        workspaces.Save(new WorkspaceRecord("storefront", @"C:\tb\storefront.orchestration.yaml", "feat-x", "feat-x", "stable", [repo], [],
            [new WrittenFile(@"C:\tb\storefront--feat-x\shop-services\src\Catalog.Api\Properties\launchSettings.json", "launchSettings", true, "sprig")],
            WorkspaceState.Up, now, now, @"C:\tb\storefront--feat-x"));
        workspaces.Save(new WorkspaceRecord("storefront", @"C:\tb\storefront.orchestration.yaml", "old", "old", "stable", [], [], [], WorkspaceState.Down, now, now));
        var at = new SourceLocation("x", 1);
        workspaces.SavePlan(new Plan("storefront", "feat-x", "stable", "main",
            [new PlannedPort("shop-services", "catalog-api", "http", 8000, null, null)],
            [new PlannedApp("shop-services", "../shop-services", "catalog-api", "dotnet", "src/Catalog.Api",
                [new PlannedEnv("Urls", new PlannedValue("{catalog-api.http.url}", "http://localhost:8000", []), "Properties/launchSettings.json", at, "launchSettings", "sprig"),
                 new PlannedEnv("Seq", new PlannedValue("x", "x", []), "../../.env", at)], null, [])],
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
        // A file shared from outside the app's folder reads from the repo, the same from every app.
        Assert.Equal("Properties/launchSettings.json, .env", app.Values);
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

        var repo = Path.Combine(store.Root, "work", "shop-web");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, ".gitignore"), ".env.local\n");
        File.WriteAllText(Path.Combine(repo, "sprig.yaml"), """
            schema: 1
            repo: shop-web
            apps:
              web: { run: node, ports: { http: any } }
            files:
              .env.local:
                read-by: [web]
                values:
                  PORT: ${web.port}
            """);
        Commit(repo);
        var orchestration = Path.Combine(store.Root, "work", "web.orchestration.yaml");
        File.WriteAllText(orchestration, """
            schema: 1
            orchestration: web
            repos:
              shop-web: { path: shop-web }
            """);
        services.Orchestrations.Add("web", orchestration);

        var vm = new WorkspacesViewModel(services);
        OperationProgressViewModel? shown = null;
        vm.ProgressStarted += p => shown = p;

        // The orchestration is approved first; only then does sprig know which repos' definitions to ask about. Both asks
        // happen in the modal, which closes once the workspace starts coming up.
        vm.OpenAddCommand.Execute(null);
        vm.NewBranch = "feat-x";
        await WaitFor(() => vm.NewSlotLine == "Goes in web--1, a new slot.");
        await vm.CreateCommand.ExecuteAsync(null);
        Assert.EndsWith("web.orchestration.yaml", Assert.Single(vm.PendingTrust).Path);
        Assert.True(vm.TrustInModal);
        Assert.False(vm.TrustOnPage);
        await vm.ApproveAllCommand.ExecuteAsync(null);
        Assert.EndsWith("sprig.yaml", Assert.Single(vm.PendingTrust).Path);
        Assert.True(vm.IsAddOpen);
        await vm.ApproveAllCommand.ExecuteAsync(null);

        Assert.False(vm.IsAddOpen);
        Assert.NotNull(shown);
        Assert.True(shown!.CanClose);
        Assert.Equal("feat-x is up in web--1.", shown.Summary);
        Assert.Equal("web--1", vm.Selected!.Title);
        Assert.Equal("feat-x", vm.Selected.Branch);
        Assert.True(vm.Selected.IsUp);
        Assert.True(vm.Selected.CanFree);
        Assert.Equal("stable", vm.Selected.Record.PortPolicy);
        var env = Path.Combine(store.Root, "work", "web--1", "shop-web", ".env.local");
        Assert.Matches(@"PORT=95\d\d", File.ReadAllText(env));
        Assert.False(File.Exists(Path.Combine(repo, ".env.local")));

        // A folder in the way of the next slot makes it broken, said under the branch while typing, with the choice of
        // fixing it or using the slot after.
        var stray = Path.Combine(store.Root, "work", "web--2", "shop-web");
        Directory.CreateDirectory(stray);
        vm.OpenAddCommand.Execute(null);
        vm.NewBranch = "feat-z";
        await WaitFor(() => vm.NewSlotBroken && vm.NewSlotLine.StartsWith("web--2 is broken:") && vm.NewSlotLine.Contains("isn't a worktree sprig can use"));
        Assert.True(vm.NewSlotBad);
        Assert.Equal("Use web--3 instead", vm.NewNextSlotLabel);
        Assert.False(vm.NewFixSlot);
        Directory.Delete(Path.Combine(store.Root, "work", "web--2"), recursive: true);
        vm.NewBranch = "feat-zz";
        await WaitFor(() => vm.NewSlotLine == "Goes in web--2, a new slot." && !vm.NewSlotBad);
        vm.CancelAddCommand.Execute(null);

        // Released, the slot is free; the form then offers it, with its data to keep or empty.
        await vm.FreeCommand.ExecuteAsync(null);
        Assert.True(vm.Selected!.IsFree);
        Assert.Equal("free", vm.Selected.Branch);
        Assert.False(vm.ShowRunControls);
        vm.CheckOutHereCommand.Execute(null);
        vm.NewBranch = "feat-y";
        await WaitFor(() => vm.NewSlotLine.StartsWith("Reuses web--1, which is free", StringComparison.Ordinal));
        Assert.True(vm.NewReusesSlot);
        Assert.True(vm.NewKeepData);
        await vm.CreateCommand.ExecuteAsync(null);
        Assert.Equal(("web--1", "feat-y"), (vm.Selected!.Title, vm.Selected.Branch));
        Assert.True(File.Exists(env));

        // Main is listed before it has ever come up, and runs in the repo itself, with nothing to set, free or remove.
        var main = vm.Workspaces.Single(w => w.Record.Key == "web/main");
        Assert.Equal(("web", "main checkout"), (main.Title, main.Branch));
        Assert.False(main.HasSettings || main.CanRemove || main.CanFree);
        vm.Show("web/main");
        await vm.UpCommand.ExecuteAsync(null);
        Assert.True(vm.Selected!.IsUp);
        Assert.Equal("main checkout · main", vm.Selected.Branch);
        Assert.Matches(@"PORT=95\d\d", File.ReadAllText(Path.Combine(repo, ".env.local")));

        // A second definition of the same checkout asks before taking it over, then takes the first one down.
        var other = Path.Combine(store.Root, "work", "web-again.orchestration.yaml");
        File.WriteAllText(other, File.ReadAllText(orchestration).Replace("orchestration: web", "orchestration: web-again"));
        services.Orchestrations.Add("web-again", other);
        vm.RefreshCommand.Execute(null);
        vm.Show("web-again/main");
        await vm.UpCommand.ExecuteAsync(null);
        await vm.ApproveAllCommand.ExecuteAsync(null);
        Assert.True(vm.ConfirmingTakeOver);
        Assert.Equal("Take over from web?", vm.TakeOverTitle);
        await vm.TakeOverCommand.ExecuteAsync(null);
        Assert.Equal(("web-again", true), (vm.Selected!.Title, vm.Selected.IsUp));
        var taken = vm.Workspaces.Single(w => w.Record.Key == "web/main");
        Assert.Equal(("down", "web-again is up in this checkout"), (taken.State, taken.HeldLine));
        await vm.DownCommand.ExecuteAsync(null);

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

        var repo = Path.Combine(store.Root, "work", "shop-web");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, ".gitignore"), ".env.local\n");
        File.WriteAllText(Path.Combine(repo, "sprig.yaml"), """
            schema: 1
            repo: shop-web
            inputs:
              api-url: { type: url, default: http://localhost:5100 }
              api-key: { type: secret }
            apps:
              web: { run: node, ports: { http: any } }
            files:
              .env.local:
                read-by: [web]
                values:
                  API: ${inputs.api-url}
                  KEY: ${inputs.api-key}
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
        await vm.FreeCommand.ExecuteAsync(null);
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

    [Fact]
    [Trait("Category", "Integration")]
    public async Task The_form_makes_a_free_slot_refuses_a_taken_new_name_and_checks_out_an_existing_branch_into_the_slot_picked()
    {
        using var store = new TempStore();
        var services = new AppServices(store.Root);
        services.Settings.Save(new Sprig.Engine.Settings.SprigSettings { PortRangeStart = 9700, PortRangeEndExclusive = 9800 });

        var repo = Path.Combine(store.Root, "work", "shop-web");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, ".gitignore"), ".env.local\n");
        File.WriteAllText(Path.Combine(repo, "sprig.yaml"), """
            schema: 1
            repo: shop-web
            apps:
              web: { run: node, ports: { http: any } }
            files:
              .env.local:
                read-by: [web]
                values:
                  PORT: ${web.port}
            """);
        Commit(repo);
        new Sprig.Engine.Processes.ProcessRunner().Run("git", ["-C", repo, "branch", "feat/ready"]).EnsureSuccess();
        var entry = services.Authoring.AddRepo(repo, "shop-web");
        services.Trust.Approve(entry.DefinitionFile, Sprig.Engine.Definitions.DefinitionHash.Of(File.ReadAllText(entry.DefinitionFile)));

        var vm = new WorkspacesViewModel(services);
        vm.ProgressStarted += _ => { };
        vm.NewOrchestration = "shop-web";
        vm.OpenAddCommand.Execute(null);
        await WaitFor(() => vm.NewBranchChoices.Count > 0);
        Assert.Equal(["main", "feat/ready"], vm.NewBranchChoices.Select(c => c.Ref));
        Assert.Equal("default", vm.NewBranchChoices[0].Note);

        // No branch: a free slot, nothing started.
        vm.NewModeNone = true;
        await WaitFor(() => vm.NewSlotLine.StartsWith("Makes shop-web--1, a free slot", StringComparison.Ordinal));
        Assert.Equal("Make the slot", vm.CreateLabel);
        await vm.CreateCommand.ExecuteAsync(null);
        Assert.True(vm.Selected!.IsFree);
        Assert.Equal("shop-web--1", vm.Selected.Title);

        // A new branch can't take a name that's there already.
        vm.OpenAddCommand.Execute(null);
        vm.NewModeNew = true;
        vm.NewBranch = "feat/ready";
        await WaitFor(() => vm.NewSlotBad && vm.NewSlotLine.Contains("already exists in shop-web"));

        // Checked out as it is, into the slot picked: a new one rather than the free one.
        vm.NewModeExisting = true;
        vm.NewExisting = "feat/ready";
        Assert.True(vm.NewShowsSlotChoices);
        vm.NewSlotChoice = vm.NewSlotChoices.Last();
        await WaitFor(() => vm.NewSlotLine == "Goes in shop-web--2, a new slot.");
        await vm.CreateCommand.ExecuteAsync(null);
        Assert.Equal(("shop-web--2", "feat/ready"), (vm.Selected!.Title, vm.Selected.Branch));
        Assert.True(services.Workspaces.Get("shop-web", "1")!.IsFree);

        services.Workspaces.Remove("shop-web", "2", new RemoveOptions());
        services.Workspaces.Remove("shop-web", "1", new RemoveOptions());
    }

    static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 600 && !condition(); i++) await Task.Delay(100);
        Assert.True(condition());
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
