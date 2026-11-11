using Sprig.App;
using Sprig.App.Logs;
using Sprig.App.ViewModels;
using Sprig.Engine.Hosting;
using Sprig.Engine.Planning;
using Sprig.Engine.Workspaces;
using Sprig.Model;

namespace Sprig.Tests.App;

public class WorkspacesAppsTests
{
    const string Key = "storefront/feat-x";

    static readonly (string Repo, string App, RunnerState? State)[] Sample =
    [
        ("shop-services", "catalog-api", RunnerState.Ready),
        ("shop-services", "payments-api", RunnerState.Exited),
        ("shop-services", "migrate", RunnerState.Completed),
        ("shop-web", "web", RunnerState.Ready),
        ("shop-web", "storybook", null),
    ];

    static WorkspacesViewModel Make(TempStore store, out AppServices services, IReadOnlyList<PlannedGroup>? groups = null)
    {
        services = new AppServices(store.Root);
        var workspaces = new WorkspaceStore(services.Paths);
        var now = DateTimeOffset.Now;
        var repos = Sample.Select(a => a.Repo).Distinct().Select(r => new WorkspaceRepo($"../{r}", r, $@"C:\tb\{r}", $@"C:\tb\feat-x\{r}", true, true)).ToArray();
        workspaces.Save(new WorkspaceRecord("storefront", @"C:\tb\storefront.orchestration.yaml", "feat-x", "feat-x", "stable", repos, [], [], WorkspaceState.Up, now, now));
        var at = new SourceLocation(@"C:\tb\shop-services\sprig.yaml", 3);
        workspaces.SavePlan(new Plan("storefront", "feat-x", "stable", "main", [new PlannedPort("shop-services", "catalog-api", "http", 8000, null, null)],
            [.. Sample.Select(a => new PlannedApp(a.Repo, $"../{a.Repo}", a.App, "dotnet", ".",
                a.App == "catalog-api" ? [new PlannedEnv("Db", new PlannedValue("x", "Host=a;Password=hunter2", ["postgres.tcp port 8002"]), null, at)] : [],
                null, [], Job: a.App == "migrate",
                DependsOn: a.App == "catalog-api" ? ["shop-services/migrate"] : null,
                Health: a.App == "catalog-api" ? [new PlannedHealth("http", "/health")] : null,
                Links: a.App == "catalog-api" ? [new PlannedLink("Swagger", "http://localhost:8000/swagger")] : null))],
            [], [], groups ?? [], []));

        var day = LogLineFormat.DateMarker(DateOnly.FromDateTime(DateTime.Now)) + "\n";
        var clock = DateTime.Now.Date.AddHours(11);
        foreach (var (app, i) in Sample.Select((a, i) => (a, i)))
        {
            var file = HostProtocol.LogFile(services.Paths, Key, $"{app.Repo}/{app.App}");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var text = app.App == "payments-api"
                ? $"{LogLineFormat.Prefix(clock.AddSeconds(i))}info: starting\n{LogLineFormat.Prefix(clock.AddSeconds(10 + i))}fail: could not connect\n"
                : $"{LogLineFormat.Prefix(clock.AddSeconds(i))}{app.App} line one\n{LogLineFormat.Prefix(clock.AddSeconds(10 + i))}{app.App} line two\n";
            File.WriteAllText(file, day + text);
        }
        services.HostStatus.Apply([.. Sample.Where(a => a.State is not null).Select(a =>
            new RunnerStatus(Key, $"{a.Repo}/{a.App}", a.State!.Value, 1, null, a.State == RunnerState.Exited ? 1 : null, "x.log", DateTimeOffset.Now,
                a.State == RunnerState.Exited ? "exited with code 1" : null))]);

        var vm = new WorkspacesViewModel(services);
        vm.Show(Key);
        return vm;
    }

    static string[] Shown(WorkspacesViewModel vm) => vm.ListRows.Select(r => r switch
    {
        AllAppsEntry => "*all",
        AppListHeader h => $"#{h.Title}",
        AppListEntry e => (e.InPinned ? "pin:" : "") + e.Row.App,
        _ => "?",
    }).ToArray();

    [Fact]
    public void The_list_starts_with_all_apps_then_each_repo_and_the_chips_count_and_filter_it()
    {
        using var store = new TempStore();
        var vm = Make(store, out _);

        Assert.Equal(["*all", "#SHOP-SERVICES", "catalog-api", "payments-api", "migrate", "#SHOP-WEB", "web", "storybook"], Shown(vm));
        Assert.Equal(["All 5", "2 ready", "1 needs attention", "2 stopped or done"], vm.Chips.Select(c => c.Label));
        var services = vm.ListRows.OfType<AppListHeader>().First();
        Assert.Equal("1/3", services.Caption);
        Assert.Equal("1 of 3 ready, 1 needs attention", services.CaptionTip);
        Assert.True(services.CaptionIsBad);
        Assert.Equal("payments-api", vm.PickedApp!.App);

        vm.PickChipCommand.Execute(vm.Chips.Single(c => c.Filter == AppStateFilter.Problems));
        Assert.Equal(["*all", "#SHOP-SERVICES", "payments-api"], Shown(vm));
        Assert.Equal("Showing 1 of 5 apps (needs attention)", vm.ListCaption);

        vm.StateFilter = AppStateFilter.All;
        vm.AppFilter = "catalog";
        Assert.Equal(["*all", "#SHOP-SERVICES", "catalog-api"], Shown(vm));

        vm.AppFilter = "";
        vm.ToggleRepoCommand.Execute(vm.ListRows.OfType<AppListHeader>().First());
        Assert.Equal(["*all", "#SHOP-SERVICES", "#SHOP-WEB", "web", "storybook"], Shown(vm));
        Assert.Equal("Expand repos", vm.FoldLabel);
    }

    [Fact]
    public void Pins_are_listed_first_kept_per_orchestration_and_can_be_all_the_stream_shows()
    {
        using var store = new TempStore();
        var vm = Make(store, out var services);
        var web = vm.Apps.Single(a => a.App == "web");

        vm.TogglePinCommand.Execute(web);

        Assert.Equal(["*all", "#PINNED", "pin:web"], Shown(vm)[..3]);
        Assert.True(web.IsPinned);
        Assert.Equal(["shop-web/web"], services.Ui.Pins("storefront"));
        var again = new WorkspacesViewModel(new AppServices(store.Root));
        again.Show(Key);
        Assert.True(again.Apps.Single(a => a.App == "web").IsPinned);

        vm.StreamPinsCommand.Execute(null);
        Assert.Equal(1, vm.AllApps.Count);
        vm.TogglePinCommand.Execute(web);
        Assert.False(vm.StreamPinned);
    }

    [Fact]
    public void Ctrl_and_shift_clicks_mark_apps_for_one_menu_and_a_plain_click_lets_go()
    {
        using var store = new TempStore();
        var vm = Make(store, out var services);
        var catalog = vm.Apps.Single(a => a.App == "catalog-api");
        vm.OpenAppRowCommand.Execute(catalog);

        vm.ToggleMark(vm.Apps.Single(a => a.App == "web"));
        Assert.Equal(["catalog-api", "web"], vm.MarkedApps.Select(a => a.App));
        Assert.True(catalog.IsMarked);
        Assert.StartsWith("2 marked", vm.ListCaption);

        vm.MarkRange(vm.ListRows.OfType<AppListEntry>().Single(e => e.Row.App == "payments-api"));
        Assert.Equal(["payments-api", "migrate", "web"], vm.MarkedApps.Select(a => a.App));

        vm.PinMarkedCommand.Execute(null);
        Assert.Equal(["shop-services/payments-api", "shop-services/migrate", "shop-web/web"], services.Ui.Pins("storefront"));
        vm.PinMarkedCommand.Execute(null);
        Assert.Empty(services.Ui.Pins("storefront"));

        vm.AppFilter = "web";
        Assert.Equal(["web"], vm.MarkedApps.Select(a => a.App));

        vm.OpenAppRowCommand.Execute(catalog);
        Assert.False(vm.HasMarks);
        Assert.False(catalog.IsMarked);
        Assert.StartsWith("Showing", vm.ListCaption);
    }

    [Fact]
    public void An_apps_menu_offers_its_url_then_its_quick_links()
    {
        using var store = new TempStore();
        var vm = Make(store, out _);

        Assert.Equal([new PlannedLink("localhost:8000", "http://localhost:8000"), new PlannedLink("Swagger", "http://localhost:8000/swagger")],
            vm.LinksOf(vm.Apps.Single(a => a.App == "catalog-api")));
        Assert.Empty(vm.LinksOf(vm.Apps.Single(a => a.App == "migrate")));
    }

    [Fact]
    public void Start_offers_every_app_or_a_group_and_keeps_the_choice_for_the_orchestration()
    {
        using var store = new TempStore();
        var vm = Make(store, out var services, [new PlannedGroup("client", ["shop-web/web", "shop-web/storybook"])]);

        Assert.Equal(["Every app", "client"], vm.StartChoices.Select(c => c.Label));
        Assert.True(vm.ShowStartChoices);
        Assert.Equal("Start all", vm.StartLabel);

        vm.PickStartChoiceCommand.Execute(vm.StartChoices[1]);

        Assert.Equal("Start client", vm.StartLabel);
        Assert.StartsWith("web, storybook, after", vm.StartTip);
        Assert.Equal("client", services.Ui.StartGroup("storefront"));
        // Any workspace of the orchestration, seen afresh, starts with the same choice.
        var again = new WorkspacesViewModel(services);
        again.Show(Key);
        Assert.Equal("Start client", again.StartLabel);
    }

    [Fact]
    public void Without_groups_Start_starts_every_app_with_nothing_to_choose()
    {
        using var store = new TempStore();
        var vm = Make(store, out _);

        Assert.False(vm.ShowStartChoices);
        Assert.Equal("Start all", vm.StartLabel);
    }

    [Fact]
    public async Task A_new_branch_in_the_same_slot_drops_the_lines_read_from_the_last_ones_logs()
    {
        using var store = new TempStore();
        var vm = Make(store, out var services);
        await vm.Logs.PollAsync();
        Assert.NotEmpty(vm.Logs.Lines);

        var workspaces = new WorkspaceStore(services.Paths);
        workspaces.Save(workspaces.Get("storefront", "feat-x")! with { Branch = "feat-y", UpdatedAt = DateTimeOffset.Now });
        foreach (var file in Directory.EnumerateFiles(HostProtocol.WorkspaceLogFolder(services.Paths, Key), "*", SearchOption.AllDirectories))
            File.Delete(file);
        vm.RefreshCommand.Execute(null);
        await vm.Logs.PollAsync();

        Assert.Empty(vm.Logs.Lines);
    }

    [Fact]
    public async Task All_apps_interleaves_the_lines_the_list_shows_and_a_name_opens_that_apps_own_log()
    {
        using var store = new TempStore();
        var vm = Make(store, out _);

        await vm.Logs.PollAsync();

        Assert.True(vm.Logs.IsAll);
        Assert.Equal(10, vm.Logs.Lines.Count);
        Assert.Equal(["catalog-api line one", "starting", "migrate line one"], vm.Logs.Lines.Take(3).Select(l => l.Text.Replace("info: ", "")));
        Assert.Equal("payments-api", vm.Logs.Lines[1].Label);

        vm.AppFilter = "web";
        Assert.All(vm.Logs.Lines, l => Assert.StartsWith("shop-web/", l.App));

        vm.AppFilter = "";
        vm.OpenAppOfLineCommand.Execute(vm.Logs.Lines.First(l => l.IsError));
        Assert.Equal("shop-services/payments-api", vm.Logs.Key);
        Assert.Equal(["info: starting", "fail: could not connect"], vm.Logs.Lines.Select(l => l.Text));
        Assert.True(vm.Apps.Single(a => a.App == "payments-api").IsSelected);
        Assert.False(vm.AllApps.IsSelected);
        Assert.Equal("shop-services / payments-api", vm.LogTitle);
    }

    [Fact]
    public async Task Each_log_keeps_its_own_search_filter_and_pause()
    {
        using var store = new TempStore();
        var vm = Make(store, out _);
        await vm.Logs.PollAsync();

        vm.Logs.ProblemsOnly = true;
        Assert.Equal(["fail: could not connect"], vm.Logs.Lines.Select(l => l.Text));

        vm.OpenApp(vm.Apps.Single(a => a.App == "catalog-api"));
        Assert.False(vm.Logs.ProblemsOnly);
        vm.Logs.Search = "two";
        vm.Logs.SetFollowing(false, vm.Logs.Lines[0]);
        Assert.True(vm.Logs.IsPaused);

        vm.OpenAll();
        Assert.True(vm.Logs.ProblemsOnly);
        Assert.Equal("", vm.Logs.Search);
        Assert.True(vm.Logs.Follow);

        vm.OpenApp(vm.Apps.Single(a => a.App == "catalog-api"));
        Assert.Equal("two", vm.Logs.Search);
        Assert.True(vm.Logs.IsPaused);
        Assert.Equal(["catalog-api line two"], vm.Logs.Lines.Select(l => l.Text));
    }

    [Fact]
    public async Task A_paused_log_counts_what_arrives_and_jumping_to_the_latest_follows_again()
    {
        using var store = new TempStore();
        var vm = Make(store, out var services);
        await vm.Logs.PollAsync();
        var ends = 0;
        vm.Logs.ScrollToEnd += () => ends++;

        vm.Logs.SetFollowing(false, vm.Logs.Lines[0]);
        File.AppendAllText(HostProtocol.LogFile(services.Paths, Key, "shop-web/web"),
            $"{LogLineFormat.Prefix(DateTime.Now.Date.AddHours(12))}late one\n{LogLineFormat.Prefix(DateTime.Now.Date.AddHours(12).AddSeconds(1))}late two\n");
        await vm.Logs.PollAsync();

        Assert.Equal(2, vm.Logs.NewCount);
        Assert.Equal("Paused · 2 new lines · Jump to latest", vm.Logs.PausedText);
        Assert.Equal(0, ends);

        vm.Logs.JumpToLatestCommand.Execute(null);
        Assert.True(vm.Logs.Follow);
        Assert.Equal(0, vm.Logs.NewCount);
        Assert.Equal(1, ends);
    }

    [Fact]
    public async Task A_paused_log_takes_lines_older_than_the_last_one_in_their_place_without_being_moved()
    {
        using var store = new TempStore();
        var vm = Make(store, out var services);
        await vm.Logs.PollAsync();
        var moved = 0;
        vm.Logs.ScrollToEnd += () => moved++;
        vm.Logs.ScrollTo += _ => moved++;
        vm.Logs.SetFollowing(false, vm.Logs.Lines[0]);
        var lines = vm.Logs.Lines;

        // Older than the newest line shown (storybook's second, at 11:00:14), as a line read late from another log is.
        File.AppendAllText(HostProtocol.LogFile(services.Paths, Key, "shop-web/web"),
            $"{LogLineFormat.Prefix(DateTime.Now.Date.AddHours(11).AddSeconds(13.5))}web late\n");
        await vm.Logs.PollAsync();

        Assert.Same(lines, vm.Logs.Lines);
        Assert.Equal(["web line two", "web late", "storybook line two"], vm.Logs.Lines.TakeLast(3).Select(l => l.Text));
        Assert.Equal(1, vm.Logs.NewCount);
        Assert.Equal(0, moved);
    }

    [Fact]
    public async Task A_paused_log_rebuilt_under_it_is_put_back_where_it_was()
    {
        using var store = new TempStore();
        var vm = Make(store, out var services);
        await vm.Logs.PollAsync();
        LogRow? putBack = null;
        vm.Logs.ScrollTo += row => putBack = row;
        var top = vm.Logs.Lines.First(l => l.Text == "web line one");
        vm.Logs.TopRow = () => top;
        vm.Logs.SetFollowing(false, top);

        // catalog-api started afresh: its lines leave the interleaved list, so it's rebuilt.
        File.WriteAllText(HostProtocol.LogFile(services.Paths, Key, "shop-services/catalog-api"), "new\n");
        await vm.Logs.PollAsync();

        Assert.Equal("web line one", putBack?.Text);
        Assert.True(vm.Logs.IsPaused);
    }

    [Fact]
    public void The_details_show_what_the_picked_app_needs_with_secrets_masked()
    {
        using var store = new TempStore();
        var vm = Make(store, out _);

        vm.OpenApp(vm.Apps.Single(a => a.App == "catalog-api"));

        var details = vm.Details!;
        Assert.Equal("http://localhost:8000", details.Url);
        Assert.Equal("migrate", details.StartsAfter);
        Assert.Equal("http answers /health", details.ReadyWhen);
        var db = Assert.Single(details.Settings);
        Assert.Equal("Host=a;Password=***", db.Value);
        Assert.Equal("postgres.tcp port 8002", db.Via);
        Assert.EndsWith("logs/storefront/feat-x/shop-services/catalog-api.log", details.LogFile);
    }

    [Fact]
    public void Folding_the_details_is_remembered_but_a_narrow_window_folds_them_only_for_now()
    {
        using var store = new TempStore();
        var vm = Make(store, out _);
        Assert.True(vm.DetailsOpen);

        vm.ToggleDetailsCommand.Execute(null);
        var again = new WorkspacesViewModel(new AppServices(store.Root));
        Assert.False(again.DetailsOpen);

        again.ToggleDetailsCommand.Execute(null);
        again.FitDetails(narrow: true);
        Assert.False(again.DetailsOpen);
        again.ToggleDetailsCommand.Execute(null);
        again.ToggleDetailsCommand.Execute(null);
        again.FitDetails(narrow: false);
        Assert.True(again.DetailsOpen);
        Assert.True(new WorkspacesViewModel(new AppServices(store.Root)).DetailsOpen);
    }

    [Fact]
    public async Task The_time_column_shows_only_when_a_line_has_a_time()
    {
        using var store = new TempStore();
        var vm = Make(store, out var services);
        await vm.Logs.PollAsync();
        Assert.True(vm.Logs.HasTimes);
        Assert.Matches(@"^\d\d:\d\d:\d\d\.\d{3}$", vm.Logs.Lines[0].Time);

        // Shorter than what was read, so it reads as the log started afresh, by a host from before lines carried a time.
        File.WriteAllText(HostProtocol.LogFile(services.Paths, Key, "shop-web/web"), "old line\n");
        vm.OpenApp(vm.Apps.Single(a => a.App == "web"));
        await vm.Logs.PollAsync();

        Assert.Equal(["old line"], vm.Logs.Lines.Select(l => l.Text));
        Assert.False(vm.Logs.HasTimes);
    }

    [Fact]
    public async Task A_line_in_one_apps_log_opens_all_apps_paused_at_that_moment_and_back_again()
    {
        using var store = new TempStore();
        var vm = Make(store, out _);
        await vm.Logs.PollAsync();
        vm.OpenApp(vm.Apps.Single(a => a.App == "payments-api"));
        LogRow? revealed = null;
        vm.Logs.Reveal += row => revealed = row;
        var failed = vm.Logs.Lines.Single(l => l.IsError);

        vm.OpenAllAt(failed);

        Assert.True(vm.Logs.IsAll);
        Assert.True(vm.AllApps.IsSelected);
        Assert.True(vm.Logs.IsPaused);
        Assert.Equal(failed.Seq, revealed?.Seq);

        // The interleaved log leaves payments-api out, so the line nearest after it in time is shown instead.
        vm.OpenApp(vm.Apps.Single(a => a.App == "payments-api"));
        vm.AppFilter = "web";
        vm.OpenAllAt(failed);
        Assert.Equal("web line two", revealed?.Text);

        vm.AppFilter = "";
        vm.OpenAppAt(vm.Logs.Lines.First(l => l.Text == "catalog-api line two"));
        Assert.Equal("shop-services/catalog-api", vm.Logs.Key);
        Assert.Equal("catalog-api", vm.PickedApp?.App);
        Assert.Equal("catalog-api line two", revealed?.Text);
        Assert.True(vm.Logs.IsPaused);
    }

    [Fact]
    public async Task Copied_lines_carry_the_columns_showing_and_the_time_and_wrap_toggles_are_remembered()
    {
        using var store = new TempStore();
        var vm = Make(store, out _);
        await vm.Logs.PollAsync();
        var rows = vm.Logs.Lines.Take(2).ToArray();

        Assert.Matches(@"^\d\d:\d\d:\d\d\.\d{3}  catalog-api  catalog-api line one\r?\n\d\d:\d\d:\d\d\.\d{3}  payments-api  info: starting$", vm.Logs.CopyText(rows));

        vm.ToggleLogTimesCommand.Execute(null);
        vm.ToggleLogWrapCommand.Execute(null);
        Assert.False(vm.Logs.TimesShown);
        Assert.Equal($"catalog-api  catalog-api line one{Environment.NewLine}payments-api  info: starting", vm.Logs.CopyText(rows));

        vm.OpenApp(vm.Apps.Single(a => a.App == "payments-api"));
        Assert.Equal("info: starting", vm.Logs.CopyText(vm.Logs.Lines.Take(1)));

        var again = new WorkspacesViewModel(new AppServices(store.Root));
        Assert.False(again.Logs.ShowTimes);
        Assert.False(again.Logs.Wrap);
    }

    [Fact]
    public void Up_and_down_step_through_the_apps_in_list_order_with_all_apps_above_the_first()
    {
        using var store = new TempStore();
        var vm = Make(store, out _);

        vm.Step(1);
        Assert.Equal("shop-services/catalog-api", vm.Logs.Key);
        vm.Step(1);
        Assert.Equal("shop-services/payments-api", vm.Logs.Key);
        vm.Step(-1);
        vm.Step(-1);
        Assert.True(vm.Logs.IsAll);
    }
}
