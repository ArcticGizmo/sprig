using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Sprig.App.Logs;
using Sprig.App.ViewModels;
using Sprig.App.Views;
using Sprig.Engine.Hosting;
using Sprig.Engine.Planning;
using Sprig.Engine.Workspaces;
using Sprig.Model;

namespace Sprig.App.Rendering;

internal static partial class HeadlessRenderer
{
    static readonly (string Repo, string App, RunnerState? State, int? Port, bool Job)[] SampleApps =
    [
        ("shop-gateway", "gateway", RunnerState.Ready, 8100, false),
        ("shop-gateway", "auth", RunnerState.Ready, 8101, false),
        ("shop-gateway", "bff", RunnerState.Waiting, 8102, false),
        ("shop-services", "migrate", RunnerState.Completed, null, true),
        ("shop-services", "seed", RunnerState.Completed, null, true),
        ("shop-services", "catalog-api", RunnerState.Ready, 8000, false),
        ("shop-services", "orders-api", RunnerState.Ready, 8001, false),
        ("shop-services", "payments-api", RunnerState.Exited, 8003, false),
        ("shop-services", "inventory-api", RunnerState.Ready, 8004, false),
        ("shop-services", "search-api", RunnerState.Starting, 8005, false),
        ("shop-services", "notifications", RunnerState.Ready, 8006, false),
        ("shop-analytics", "events-api", RunnerState.Ready, 8200, false),
        ("shop-analytics", "ingest-worker", RunnerState.Restarting, null, false),
        ("shop-analytics", "reports-api", RunnerState.Building, 8202, false),
        ("shop-analytics", "scheduler", RunnerState.Ready, null, false),
        ("shop-web", "web", RunnerState.Ready, 5170, false),
        ("shop-web", "admin", RunnerState.Ready, 5171, false),
        ("shop-web", "storybook", null, 6006, false),
        ("shop-mobile", "mobile", RunnerState.Ready, 8010, false),
        ("shop-docs", "docs", RunnerState.Ready, 5172, false),
    ];

    /// <summary>
    /// The Workspaces page with a made-up workspace of 20 apps (one failing, one restarting, some starting, finished or stopped)
    /// in a throwaway store, so its layout can be checked without a host: every app's log at 1440×900 and at 1024×700, and
    /// the failing app's own log. Writes <c>&lt;name&gt;-all.png</c>, <c>-small.png</c> and <c>-app.png</c>.
    /// </summary>
    public static int RenderWorkspacesSample(string outFile)
    {
        var store = Path.Combine(Path.GetTempPath(), "sprig-render-" + Guid.NewGuid().ToString("N"));
        try
        {
            AppBuilder.Configure<App>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .WithInterFont()
                .SetupWithoutStarting();

            var services = new AppServices(store);
            var runners = SampleStore(services);
            services.HostStatus.Hold(runners);
            services.Ui.SetPins("storefront", ["shop-services/orders-api", "shop-services/payments-api", "shop-web/web"]);

            var vm = new MainWindowViewModel(services, checkForUpdates: false);
            vm.NavigateCommand.Execute(vm.Workspaces);
            vm.Workspaces.Show("storefront/feat-a");
            var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
            window.Show();
            Pump(TimeSpan.FromMilliseconds(400));
            Wait(vm.Workspaces.Logs.PollAsync());
            Pump(TimeSpan.FromMilliseconds(600));
            Save(window, Sibling(outFile, "all"));

            window.Width = 1024;
            window.Height = 700;
            Pump(TimeSpan.FromMilliseconds(600));
            Save(window, Sibling(outFile, "small"));

            window.Width = 1440;
            window.Height = 900;
            vm.Workspaces.OpenApp(vm.Workspaces.Apps.First(a => a.App == "payments-api"));
            Pump(TimeSpan.FromMilliseconds(600));
            Save(window, Sibling(outFile, "app"));
            var lines = vm.Workspaces.Logs.Lines.Count;

            var linksButton = window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "LinksButton");
            linksButton.Flyout!.ShowAt(linksButton);
            Pump(TimeSpan.FromMilliseconds(400));
            Save(window, Sibling(outFile, "links"));
            linksButton.Flyout.Hide();

            vm.Workspaces.ToggleDetailsCommand.Execute(null);
            Pump(TimeSpan.FromMilliseconds(400));
            Save(window, Sibling(outFile, "folded"));
            vm.Workspaces.ToggleDetailsCommand.Execute(null);

            // Start set to one group, as its ▾ would leave it.
            vm.Workspaces.PickStartChoiceCommand.Execute(vm.Workspaces.StartChoices.First(c => c.Group == "client"));
            Pump(TimeSpan.FromMilliseconds(300));
            Save(window, Sibling(outFile, "start-group"));
            Console.WriteLine("start choices: " + string.Join(" | ", vm.Workspaces.StartChoices.Select(c => $"{c.Label} ({c.Apps})")));
            vm.Workspaces.PickStartChoiceCommand.Execute(vm.Workspaces.StartChoices.First());

            // The ▾'s choices, each with a ▶ that starts it there and then.
            var choices = window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "StartChoiceButton");
            choices.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(TimeSpan.FromMilliseconds(400));
            Save(window, Sibling(outFile, "start-choices"));
            // Pointed at, the first ▶ lights up the whole area that takes its click.
            var play = window.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("startnow"));
            window.MouseMove(play.TranslatePoint(new Point(play.Bounds.Width / 2, 2), window)!.Value);
            Pump(TimeSpan.FromMilliseconds(300));
            Save(window, Sibling(outFile, "start-choices-hover"));
            Console.WriteLine($"▶ click area: {play.Bounds.Width:0}×{play.Bounds.Height:0}, its row {((Control)play.FindAncestorOfType<MenuItem>()!).Bounds.Height:0} high");
            CloseMenus(window);

            // Three apps marked with Ctrl+click, then one of them right-clicked: one menu for them all.
            vm.Workspaces.OpenApp(vm.Workspaces.Apps.First(a => a.App == "catalog-api"));
            foreach (var name in new[] { "orders-api", "payments-api" }) vm.Workspaces.ToggleMark(vm.Workspaces.Apps.First(a => a.App == name));
            Pump(TimeSpan.FromMilliseconds(300));
            RightClickApp(window, "orders-api");
            Save(window, Sibling(outFile, "marked"));
            CloseMenus(window);
            vm.Workspaces.ClearMarks();

            // One app's own menu, with its url and quick links.
            RightClickApp(window, "payments-api");
            Save(window, Sibling(outFile, "app-menu"));
            CloseMenus(window);
            vm.Workspaces.OpenApp(vm.Workspaces.Apps.First(a => a.App == "payments-api"));

            // The nav folded to its icons, for the log's sake.
            vm.ToggleNavCommand.Execute(null);
            Pump(TimeSpan.FromMilliseconds(400));
            Save(window, Sibling(outFile, "nav-collapsed"));
            vm.ToggleNavCommand.Execute(null);

            // The new-workspace form when the slot the branch would take is broken. Nothing is registered in the sample
            // store, so the form is given a name to show.
            vm.Workspaces.OrchestrationNames.Add("storefront");
            vm.Workspaces.NewOrchestration = "storefront";
            vm.Workspaces.OpenAddCommand.Execute(null);
            vm.Workspaces.NewBranch = "feat-q";
            Pump(TimeSpan.FromMilliseconds(600));
            var stray = Path.Combine(services.Paths.Root, "tb", "storefront--3", "shop-web");
            vm.Workspaces.NewBroken = new BrokenSlot("3", "storefront--3", [stray], "4", "storefront--4");
            vm.Workspaces.NewSlotLine = $"{vm.Workspaces.NewBroken.Why}.";
            vm.Workspaces.NewSlotBad = true;
            Pump(TimeSpan.FromMilliseconds(300));
            Save(window, Sibling(outFile, "new-broken"));
            vm.Workspaces.CancelAddCommand.Execute(null);

            // The form's three cases, with branches as two remotes would give them, the list open for an existing one.
            vm.Workspaces.OpenAddCommand.Execute(null);
            Pump(TimeSpan.FromMilliseconds(300));
            vm.Workspaces.NewBranchChoices =
            [
                new BranchChoice("main", null, [], true),
                new BranchChoice("feat/payments", null, ["shop-services"], false),
                new BranchChoice("main", "origin", [], true),
                new BranchChoice("feat/injuries-v2", "origin", [], false),
                new BranchChoice("feat/mobile-events", "origin", ["shop-web"], false),
                new BranchChoice("main", "upstream", [], true),
                new BranchChoice("spike/kafka", "upstream", [], false),
            ];
            vm.Workspaces.NewModeNew = true;
            vm.Workspaces.NewBranch = "feat/orders-retry";
            Pump(TimeSpan.FromMilliseconds(500));
            Save(window, Sibling(outFile, "new-mode-new"));
            vm.Workspaces.NewModeExisting = true;
            Pump(TimeSpan.FromMilliseconds(300));
            var existingBox = window.GetVisualDescendants().OfType<AutoCompleteBox>().First(b => b.Name == "NewExistingBox");
            existingBox.Focus();
            existingBox.IsDropDownOpen = true;
            Pump(TimeSpan.FromMilliseconds(500));
            Save(window, Sibling(outFile, "new-mode-existing"));
            existingBox.IsDropDownOpen = false;
            vm.Workspaces.NewModeNone = true;
            Pump(TimeSpan.FromMilliseconds(500));
            Save(window, Sibling(outFile, "new-mode-none"));
            Console.WriteLine($"form, no branch: '{vm.Workspaces.NewSlotLine}', button '{vm.Workspaces.CreateLabel}'");
            vm.Workspaces.NewModeNew = true;
            vm.Workspaces.CancelAddCommand.Execute(null);

            // Unwrapped, the failure and its stack trace picked by dragging over them and copied, as a user would.
            vm.Workspaces.ToggleLogWrapCommand.Execute(null);
            Pump(TimeSpan.FromMilliseconds(400));
            var list = window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "LogList");
            Point At(int index)
            {
                var row = list.ContainerFromIndex(index)!;
                return row.TranslatePoint(new Point(160, row.Bounds.Height / 2), window)!.Value;
            }
            var fail = vm.Workspaces.Logs.Lines.ToList().FindIndex(l => l.IsError);
            window.MouseDown(At(fail), MouseButton.Left);
            window.MouseMove(At(fail + 1), RawInputModifiers.LeftMouseButton);
            window.MouseMove(At(fail + 2), RawInputModifiers.LeftMouseButton);
            window.MouseUp(At(fail + 2), MouseButton.Left);
            Pump(TimeSpan.FromMilliseconds(200));
            window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
            Pump(TimeSpan.FromMilliseconds(400));
            Save(window, Sibling(outFile, "select"));
            Console.WriteLine($"dragged over {list.SelectedItems?.Count} lines; paused: {vm.Workspaces.Logs.IsPaused}; copied:");
            var copied = window.Clipboard!.TryGetTextAsync();
            Wait(copied);
            Console.WriteLine(copied.Result);

            // Right-click the failure: every app's log, around the moment it was written.
            window.MouseDown(At(fail), MouseButton.Right);
            window.MouseUp(At(fail), MouseButton.Right);
            Pump(TimeSpan.FromMilliseconds(200));
            var menu = list.ContextMenu!.Items.OfType<MenuItem>().ToArray();
            Console.WriteLine("menu: " + string.Join(" | ", menu.Where(m => m.IsVisible).Select(m => m.Header)));
            list.ContextMenu.Close();
            menu.First(m => m.IsVisible && $"{m.Header}".StartsWith("Show all apps")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Pump(TimeSpan.FromMilliseconds(600));
            Save(window, Sibling(outFile, "around"));
            Console.WriteLine($"now showing {(vm.Workspaces.Logs.IsAll ? "all apps" : vm.Workspaces.Logs.Key)}, picked: {(list.SelectedItem as LogRow)?.Text}");
            vm.Workspaces.ToggleLogWrapCommand.Execute(null);

            vm.Workspaces.ShowInfraTabCommand.Execute(null);
            Pump(TimeSpan.FromMilliseconds(100));
            Save(window, Sibling(outFile, "infra-loading"));
            Wait(vm.Workspaces.LoadInfraAsync());
            Pump(TimeSpan.FromMilliseconds(400));
            Save(window, Sibling(outFile, "infra"));

            // Wider than the tab's 1200 cap, where it should sit centred.
            window.Width = 2000;
            window.Height = 1100;
            Pump(TimeSpan.FromMilliseconds(400));
            Save(window, Sibling(outFile, "infra-wide"));
            window.Width = 1440;
            window.Height = 900;

            // The graph, with the containers' states held as docker would give them.
            vm.Workspaces.HoldInfraStates(SampleInfraStates);
            Wait(vm.Workspaces.ShowGraphTabCommand.ExecuteAsync(null));
            Pump(TimeSpan.FromMilliseconds(600));
            Save(window, Sibling(outFile, "graph"));
            var boxes = vm.Workspaces.Graph!.Layout.Boxes().ToArray();
            var overlaps = boxes.SelectMany((a, i) => boxes.Skip(i + 1).Where(b => a.Box.Overlaps(b.Box, 0)).Select(b => $"{a.Group} and {b.Group}")).ToArray();
            Console.WriteLine($"repo boxes overlapping: {overlaps.Length} ({string.Join(", ", overlaps)})");
            var canvas = window.GetVisualDescendants().OfType<Sprig.App.Controls.WorkspaceGraphCanvas>().First();
            Point NodeAt(string key) => canvas.TranslatePoint(canvas.PointOf(key)!.Value, window)!.Value;

            window.MouseDown(NodeAt("shop-services/orders-api"), MouseButton.Left);
            window.MouseUp(NodeAt("shop-services/orders-api"), MouseButton.Left);
            Pump(TimeSpan.FromMilliseconds(400));
            Save(window, Sibling(outFile, "graph-picked"));

            window.MouseMove(NodeAt("shop-services/postgres"));
            Pump(TimeSpan.FromMilliseconds(300));
            Save(window, Sibling(outFile, "graph-hover"));

            // payments-api uses four, so its "+1" stands for one; pointed at, it says which. Brought into view first,
            // since fitted the sample is zoomed out to where tags are only dots.
            vm.Workspaces.Graph!.Reveal("shop-services/payments-api");
            vm.Workspaces.Graph.Select(null);
            Pump(TimeSpan.FromMilliseconds(300));
            window.MouseMove(canvas.TranslatePoint(canvas.TagAt("shop-services/payments-api", more: true)!.Value, window)!.Value);
            Pump(TimeSpan.FromMilliseconds(300));
            Save(window, Sibling(outFile, "graph-tag-more"));
            window.MouseMove(canvas.TranslatePoint(canvas.TagAt("shop-services/payments-api", more: false)!.Value, window)!.Value);
            Pump(TimeSpan.FromMilliseconds(300));
            Save(window, Sibling(outFile, "graph-tag"));

            // Looking for redis finds it and what uses it; Enter goes to each in turn.
            vm.Workspaces.GraphSearch = "redis";
            Pump(TimeSpan.FromMilliseconds(300));
            Save(window, Sibling(outFile, "graph-search"));
            var found = vm.Workspaces.GraphSearchCaption;
            vm.Workspaces.NextGraphMatchCommand.Execute(null);
            vm.Workspaces.NextGraphMatchCommand.Execute(null);
            Pump(TimeSpan.FromMilliseconds(300));
            Save(window, Sibling(outFile, "graph-search-next"));
            Console.WriteLine($"search 'redis': {found}, then {vm.Workspaces.GraphSearchCaption}, picked {vm.Workspaces.GraphPicked?.Name}");
            vm.Workspaces.ClearGraphSearchCommand.Execute(null);
            vm.Workspaces.FitGraphCommand.Execute(null);
            Pump(TimeSpan.FromMilliseconds(300));

            window.MouseDown(NodeAt("shop-services/payments-api"), MouseButton.Right);
            window.MouseUp(NodeAt("shop-services/payments-api"), MouseButton.Right);
            Pump(TimeSpan.FromMilliseconds(400));
            Save(window, Sibling(outFile, "graph-menu"));
            CloseMenus(window);

            // The same by forces, where a node can be dragged and keeps its place.
            vm.Workspaces.ShowForceGraphCommand.Execute(null);
            Pump(TimeSpan.FromMilliseconds(500));
            Save(window, Sibling(outFile, "graph-force"));
            var from = NodeAt("shop-analytics/kafka");
            window.MouseDown(from, MouseButton.Left);
            for (var i = 1; i <= 10; i++) window.MouseMove(from + new Vector(i * 18, i * 6), RawInputModifiers.LeftMouseButton);
            window.MouseUp(from + new Vector(180, 60), MouseButton.Left);
            Pump(TimeSpan.FromMilliseconds(1500));
            Save(window, Sibling(outFile, "graph-dragged"));
            Console.WriteLine($"graph: {vm.Workspaces.Graph!.Nodes.Count} nodes, {vm.Workspaces.Graph.Links.Count} links; picked {vm.Workspaces.GraphPicked?.Name}; "
                + $"kept places: {string.Join(", ", services.Ui.GraphPlaces("storefront").Keys)}");

            vm.Workspaces.ShowLayeredGraphCommand.Execute(null);
            vm.Workspaces.Graph!.Select(null);

            // Grouped by start group, then by role, then back by repo.
            foreach (var (by, name) in new[] { (GraphGrouping.StartGroup, "graph-by-start-group"), (GraphGrouping.Role, "graph-by-role") })
            {
                vm.Workspaces.GraphGroupBy = by;
                vm.Workspaces.FitGraphCommand.Execute(null);
                Pump(TimeSpan.FromMilliseconds(500));
                Save(window, Sibling(outFile, name));
                var grouped = vm.Workspaces.Graph!.Layout.Boxes().ToArray();
                Console.WriteLine($"{by}: grouped as {vm.Workspaces.GraphGroupBy}, boxes {string.Join(", ", grouped.Select(b => b.Group))}; overlapping: "
                    + grouped.SelectMany((a, i) => grouped.Skip(i + 1).Where(b => a.Box.Overlaps(b.Box, 0))).Count());
            }
            vm.Workspaces.GraphGroupBy = GraphGrouping.Repo;
            window.Width = 1024;
            window.Height = 700;
            vm.Workspaces.FitGraphCommand.Execute(null);
            Pump(TimeSpan.FromMilliseconds(500));
            Save(window, Sibling(outFile, "graph-small"));
            window.Width = 1440;
            window.Height = 900;

            // A node's Log button opens its log on the Apps tab.
            vm.Workspaces.GraphLogCommand.Execute(vm.Workspaces.Graph.Find("shop-services/orders-api"));
            Pump(TimeSpan.FromMilliseconds(400));
            Console.WriteLine($"Log from the graph: tab {vm.Workspaces.Tab}, showing {vm.Workspaces.LogTitle}");

            vm.NavigateCommand.Execute(vm.Workspaces);
            Wait(vm.Workspaces.LoadOverviewStatusAsync());
            Pump(TimeSpan.FromMilliseconds(500));
            Save(window, Sibling(outFile, "overview"));

            vm.Workspaces.OverviewSearch = "feat";
            Pump(TimeSpan.FromMilliseconds(300));
            Save(window, Sibling(outFile, "overview-search"));
            vm.Workspaces.OverviewSearch = "";

            vm.Workspaces.SelectStaleCommand.Execute(null);
            Pump(TimeSpan.FromMilliseconds(500));
            Save(window, Sibling(outFile, "overview-ticked"));
            vm.Workspaces.ClearCheckedCommand.Execute(null);

            // Main, run as the checkout is, with the user's own infra: one of it isn't listening.
            vm.Workspaces.Show("storefront/main");
            vm.Workspaces.ShowAppsTabCommand.Execute(null);
            Pump(TimeSpan.FromMilliseconds(500));
            Save(window, Sibling(outFile, "main"));
            Wait(vm.Workspaces.ShowGraphTabCommand.ExecuteAsync(null));
            vm.Workspaces.ApplyOwnInfra(
            [
                new OwnInfra("shop-services/postgres", [5432], true),
                new OwnInfra("shop-services/redis", [6379], true),
                new OwnInfra("storefront/mail", [1025], true),
                new OwnInfra("shop-analytics/kafka", [9092], false),
            ]);
            Pump(TimeSpan.FromMilliseconds(600));
            Save(window, Sibling(outFile, "main-graph"));
            canvas = window.GetVisualDescendants().OfType<Sprig.App.Controls.WorkspaceGraphCanvas>().First();
            window.MouseDown(NodeAt("shop-analytics/kafka"), MouseButton.Left);
            window.MouseUp(NodeAt("shop-analytics/kafka"), MouseButton.Left);
            Pump(TimeSpan.FromMilliseconds(400));
            Save(window, Sibling(outFile, "main-graph-picked"));

            // Home: what the sample has running, and the first steps, since nothing is on the Repos list.
            vm.NavigateCommand.Execute(vm.Home);
            Pump(TimeSpan.FromMilliseconds(500));
            Save(window, Sibling(outFile, "home"));
            window.Close();

            Console.WriteLine($"rendered {Sibling(outFile, "all")}, -small, -app, -infra and -overview ({lines} lines in the app's log)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"headless render failed: {ex}");
            return 1;
        }
        finally
        {
            try { Directory.Delete(store, recursive: true); } catch { /* temp store */ }
        }
    }

    static void RightClickApp(Window window, string app)
    {
        var row = window.GetVisualDescendants().OfType<Border>()
            .First(b => b.Classes.Contains("approw") && b.DataContext is AppListEntry { InPinned: false } e && e.Row.App == app);
        var at = row.TranslatePoint(new Point(60, row.Bounds.Height / 2), window)!.Value;
        window.MouseDown(at, MouseButton.Right);
        window.MouseUp(at, MouseButton.Right);
        Pump(TimeSpan.FromMilliseconds(400));
    }

    static void CloseMenus(Window window)
    {
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Pump(TimeSpan.FromMilliseconds(200));
    }

    /// <summary>Two orchestrations' worth of workspaces, the first with the sample apps' plan and logs; returns what the host
    /// would say about them.</summary>
    static IReadOnlyList<RunnerStatus> SampleStore(AppServices services)
    {
        var workspaces = new WorkspaceStore(services.Paths);
        var now = DateTimeOffset.Now;
        var root = Path.Combine(services.Paths.Root, "tb");
        var repos = SampleApps.Select(a => a.Repo).Distinct()
            .Select(r => new WorkspaceRepo($"../{r}", r, Path.Combine(root, r), Path.Combine(root, "storefront--feat-a", r), true, true)).ToArray();
        workspaces.Save(new WorkspaceRecord("storefront", Path.Combine(root, "storefront.orchestration.yaml"), "feat-a", "feat-a", "stable", repos, [], [],
            WorkspaceState.Up, now, now, Path.Combine(root, "storefront--feat-a")));
        workspaces.Save(new WorkspaceRecord("storefront", Path.Combine(root, "storefront.orchestration.yaml"), "feat-b", "feat-b", "stable", [], [], [], WorkspaceState.Up, now, now));
        workspaces.Save(new WorkspaceRecord("storefront", Path.Combine(root, "storefront.orchestration.yaml"), "demo-oct", "demo-oct", "stable", [], [], [],
            WorkspaceState.Failed, now.AddMinutes(-20), now.AddMinutes(-20), Error: "port 8002 is in use"));
        foreach (var (name, days) in new[] { ("feat-c", 3), ("hotfix-12", 6), ("spike-cache", 15) })
            workspaces.Save(new WorkspaceRecord("storefront", Path.Combine(root, "storefront.orchestration.yaml"), name, name, "stable", [], [], [],
                WorkspaceState.Down, now.AddDays(-days), now.AddDays(-days)));
        workspaces.Save(new WorkspaceRecord("services", Path.Combine(root, "services.orchestration.yaml"), "local", null, "stable", [], [], [], WorkspaceState.Up, now, now));
        workspaces.Save(new WorkspaceRecord("storefront", Path.Combine(root, "storefront.orchestration.yaml"), WorkspaceRecord.MainName, null, "stable",
            [.. repos.Select(r => r with { Dir = r.Source, Worktree = false, BranchCreated = false })], [], [], WorkspaceState.Up, now, now) { CheckoutBranch = "main" });
        workspaces.Save(new WorkspaceRecord("analytics", Path.Combine(root, "analytics.orchestration.yaml"), WorkspaceRecord.MainName, null, "stable", [], [], [],
            WorkspaceState.Down, now.AddDays(-1), now.AddDays(-1)) { CheckoutBranch = "develop" });
        workspaces.Save(new WorkspaceRecord("analytics", Path.Combine(root, "analytics.orchestration.yaml"), "ingest-perf", "ingest-perf", "stable", [], [], [],
            WorkspaceState.Down, now.AddDays(-4), now.AddDays(-4)));

        var at = new SourceLocation(Path.Combine(root, "shop-services", "sprig.yaml"), 12);
        workspaces.SavePlan(new Plan("storefront", "feat-a", "stable", "main",
            [.. SampleApps.Where(a => a.Port is not null).Select(a => new PlannedPort(a.Repo, a.App, "http", a.Port!.Value, null, null)),
             new PlannedPort("shop-services", "postgres", "tcp", 8002, null, 5432),
             new PlannedPort("shop-services", "redis", "tcp", 8003, null, 6379),
             new PlannedPort("storefront", "mail", "smtp", 8011, null, 1025),
             new PlannedPort("shop-analytics", "kafka", "tcp", 9092, null, 9092)],
            [.. SampleApps.Select(a => new PlannedApp(a.Repo, $"../{a.Repo}", a.App, "dotnet", a.Job ? $"tools/{a.App}" : ".",
                SampleEnv(a.Repo, a.App, a.Port, at),
                null, a.Job ? [] : a.Repo == "shop-services" ? ["migrate"] : [], Job: a.Job,
                DependsOn: a.Repo == "shop-services" && !a.Job ? ["shop-services/migrate", "shop-services/seed"] : null,
                Health: a.Port is not null && !a.Job ? [new PlannedHealth("http", "/health")] : null,
                Links: a.App switch
                {
                    "web" => [new PlannedLink("Storefront", $"http://localhost:{a.Port}"), new PlannedLink("Admin as dev", $"http://localhost:{a.Port}/admin/login?as=dev")],
                    "payments-api" => [new PlannedLink("Swagger", $"http://localhost:{a.Port}/swagger")],
                    _ => null,
                }))],
            [new PlannedContainer("shop-services", "postgres", "postgres:17", ["8002:5432"], [], ["data → /var/lib/postgresql/data"],
                 [new PlannedMapping("tcp", 8002, 5432)], [new VolumeModel("data", "/var/lib/postgresql/data")], "pg_isready"),
             new PlannedContainer("shop-services", "redis", "redis:7", ["8003:6379"], [], [], [new PlannedMapping("tcp", 8003, 6379)], [], null),
             new PlannedContainer("storefront", "mail", "axllent/mailpit:v1.27", ["8011:1025"], [], [], [new PlannedMapping("smtp", 8011, 1025)], [], null)],
            [new PlannedCompose("shop-analytics", "../shop-analytics", "docker-compose.yml", [],
                 [new PlannedComposeService("kafka", [new PlannedMapping("tcp", 9092, 9092)])])],
            [new PlannedGroup("client", [.. SampleApps.Where(a => a.Repo == "shop-web").Select(a => $"{a.Repo}/{a.App}")]),
             new PlannedGroup("apis", [.. SampleApps.Where(a => a.Repo == "shop-services" && !a.Job).Select(a => $"{a.Repo}/{a.App}")])],
            [new PlanProblem(ProblemSeverity.Warning, "ingest-worker has no health check, so \"ready\" only means it started",
                new SourceLocation(Path.Combine(root, "shop-analytics", "sprig.yaml"), 14))],
            [new PlannedLink("Mail inbox", "http://localhost:8025"), new PlannedLink("Checkout flow", "http://localhost:5170/cart?seed=demo")]));

        // Main runs the same definitions in the checkout as it is: no values, and its infra on its own ports, the user's.
        var feat = workspaces.GetPlan("storefront", "feat-a")!;
        workspaces.SavePlan(feat with
        {
            Workspace = WorkspaceRecord.MainName,
            WorktreesFrom = null,
            Ports = [.. feat.Ports.Select(p => p.TargetPort is { } own ? p with { Port = own } : p)],
            Apps = [.. feat.Apps.Select(a => a with
            {
                Env = [],
                Uses = [.. a.Env.SelectMany(e => e.Value.Sources ?? []).Where(s => s.Owner is not null && s.Resource is not null)
                    .Select(s => s with { Endpoint = null, Setting = null }).Distinct()],
            })],
        });

        var runners = new List<RunnerStatus>();
        var clock = DateTime.Now.AddMinutes(-2);
        foreach (var (app, i) in SampleApps.Select((a, i) => (a, i)))
        {
            var name = $"{app.Repo}/{app.App}";
            var file = HostProtocol.LogFile(services.Paths, "storefront/feat-a", name);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var log = new StringBuilder(LogLineFormat.DateMarker(DateOnly.FromDateTime(clock)) + "\n");
            foreach (var (text, n) in SampleLog(app.App, app.State, app.Port).Select((t, n) => (t, n)))
                log.Append(LogLineFormat.Prefix(clock.AddMilliseconds(i * 137 + n * 2900))).Append(text).Append('\n');
            File.WriteAllText(file, log.ToString());
            if (app.State is { } state)
                runners.Add(new RunnerStatus("storefront/feat-a", name, state, 1000 + i, app.Port, state == RunnerState.Exited ? 1 : null, file,
                    DateTimeOffset.Now, state switch
                    {
                        RunnerState.Exited => "exited with code 1 during start-up",
                        RunnerState.Restarting => "exited with code 134; restarting in 4s (3 of 5)",
                        _ => null,
                    }, state == RunnerState.Restarting ? 3 : 0));
        }
        return runners;
    }

    /// <summary>What the sample's containers would say: one still waiting on its health check, which is why the worker
    /// that reads from it keeps restarting.</summary>
    static readonly Dictionary<string, Sprig.Engine.Compose.ComposeService> SampleInfraStates = new()
    {
        ["shop-services/postgres"] = new("shop-services-postgres", "running", "healthy", 0),
        ["shop-services/redis"] = new("shop-services-redis", "running", "", 0),
        ["storefront/mail"] = new("storefront-mail", "running", "", 0),
        ["shop-analytics/kafka"] = new("kafka", "running", "starting", 0),
    };

    /// <summary>Each sample app's values, with where each came from, so the graph has links to draw: the services share a
    /// database, the gateway fronts them, the web apps go through the gateway, and payments uses a sandbox outside.</summary>
    static IReadOnlyList<PlannedEnv> SampleEnv(string repo, string app, int? port, SourceLocation at)
    {
        var env = new List<PlannedEnv>();
        int? PortOf(string other) => SampleApps.FirstOrDefault(a => a.App == other).Port;
        void Add(string key, string value, string via, ValueSource source)
            => env.Add(new PlannedEnv(key, new PlannedValue(value, value, [via], [source]), "appsettings.Local.json", at, "appSettings"));
        void Db() => Add("ConnectionStrings__Db", "Host=localhost;Port=8002;Password=hunter2", "postgres.tcp port 8002", new("shop-services", "postgres", "tcp", null));
        void Redis(string owner) => Add("Redis__Url", "redis://localhost:8003", "redis.tcp port 8003",
            new("shop-services", "redis", "tcp", owner == "shop-services" ? null : "cache-url"));
        void Mail() => Add("Mail__Smtp", "smtp://localhost:8011", "input mail-smtp ← storefront", new("storefront", "mail", "smtp", "mail-smtp"));
        void Kafka() => Add("Kafka__Brokers", "localhost:9092", "kafka.tcp port 9092", new("shop-analytics", "kafka", "tcp", null));
        void Uses(string key, string setting, string owner, string other)
            => Add(key, $"http://localhost:{PortOf(other)}", $"input {setting} ← {owner}", new(owner, other, "http", setting));

        if (port is { } p) Add("Urls", $"http://localhost:{p}", $"{app}.http port {p}", new(repo, app, "http", null));
        switch (app)
        {
            case "migrate" or "seed" or "orders-api" or "payments-api" or "search-api":
                Db();
                break;
            case "catalog-api" or "inventory-api":
                Db();
                Redis(repo);
                break;
            case "notifications":
                Mail();
                break;
            case "auth":
                Redis(repo);
                break;
            case "gateway":
                Uses("Routes__Catalog", "catalog-url", "shop-services", "catalog-api");
                Uses("Routes__Orders", "orders-url", "shop-services", "orders-api");
                Uses("Routes__Search", "search-url", "shop-services", "search-api");
                Uses("Auth__Authority", "auth-url", "shop-gateway", "auth");
                break;
            case "bff":
                Uses("Gateway__Url", "gateway-url", "shop-gateway", "gateway");
                break;
            case "events-api" or "ingest-worker":
                Kafka();
                break;
            case "reports-api":
                Add("ConnectionStrings__Reports", "Host=localhost;Port=8002;Password=hunter2", "input reports-db ← shop-services",
                    new("shop-services", "postgres", "tcp", "reports-db"));
                break;
            case "scheduler":
                Uses("Events__Url", "events-url", "shop-analytics", "events-api");
                break;
            case "web" or "admin":
                Uses("VITE_API", "bff-url", "shop-gateway", "bff");
                break;
            case "mobile":
                Uses("API_URL", "gateway-url", "shop-gateway", "gateway");
                break;
        }
        if (app == "orders-api") Mail();
        if (app == "payments-api")
        {
            Redis(repo);
            Mail();
        }
        if (app == "payments-api")
            Add("Payments__Url", "https://sandbox.payments.example.com", "input payments-gateway ← this workspace",
                new(ValueSource.Workspace, null, null, "payments-gateway"));
        return env;
    }

    static IEnumerable<string> SampleLog(string app, RunnerState? state, int? port)
    {
        if (state is null) yield break;
        yield return $"sprig: starting dotnet run --project {app}";
        if (app == "payments-api")
        {
            yield return "info: Starting payments-api";
            yield return "warn: Npgsql: connection to localhost:8002 refused, retry 1 of 3";
            yield return "warn: Npgsql: connection to localhost:8002 refused, retry 2 of 3";
            yield return "fail: Npgsql.NpgsqlException: Failed to connect to 127.0.0.1:8002";
            yield return "      at Npgsql.Internal.NpgsqlConnector.Connect(NpgsqlTimeout timeout) in /_/src/Npgsql/Internal/NpgsqlConnector.cs:line 712, after 3 attempts over 2.4s to 127.0.0.1:8002 (connection refused)";
            yield return "      at Payments.Api.Program.Main(String[] args) in Program.cs:line 31";
            yield return "sprig: exited with code 1 after 4.1s";
            yield break;
        }
        if (app == "ingest-worker")
        {
            for (var i = 0; i < 3; i++)
            {
                yield return "info: connecting to broker localhost:9092";
                yield return "warn: broker localhost:9092 not ready (NOT_COORDINATOR)";
                yield return "sprig: exited with code 134; restarting in 4s";
            }
            yield break;
        }
        if (state == RunnerState.Completed)
        {
            yield return "Applying migration 0007_orders_email";
            yield return "sprig: exited with code 0";
            yield break;
        }
        if (port is { } p) yield return $"info: Now listening on: http://localhost:{p}";
        if (state == RunnerState.Ready) yield return port is null ? "info: tick: 0 jobs due" : $"sprig: ready: http://localhost:{port}/health answered 200";
        for (var i = 0; i < 8 && state == RunnerState.Ready; i++)
            yield return i % 3 == 0 ? "GET /health 200 in 0.4ms" : $"GET /{app.Split('-')[0]}s/{i} 200 in {1 + i * 0.3:0.0}ms";
    }
}
