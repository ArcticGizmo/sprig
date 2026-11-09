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

    /// <summary>Two orchestrations' worth of workspaces, the first with the sample apps' plan and logs; returns what the host
    /// would say about them.</summary>
    static IReadOnlyList<RunnerStatus> SampleStore(AppServices services)
    {
        var workspaces = new WorkspaceStore(services.Paths);
        var now = DateTimeOffset.Now;
        var root = Path.Combine(services.Paths.Root, "tb");
        var repos = SampleApps.Select(a => a.Repo).Distinct()
            .Select(r => new WorkspaceRepo($"../{r}", r, Path.Combine(root, r), Path.Combine(root, "storefront--feat-a", r), true, true)).ToArray();
        workspaces.Save(new WorkspaceRecord("storefront", Path.Combine(root, "storefront.orchestration.cs"), "feat-a", "feat-a", "stable", repos, [], [],
            WorkspaceState.Up, now, now, Path.Combine(root, "storefront--feat-a")));
        workspaces.Save(new WorkspaceRecord("storefront", Path.Combine(root, "storefront.orchestration.cs"), "feat-b", "feat-b", "stable", [], [], [], WorkspaceState.Up, now, now));
        workspaces.Save(new WorkspaceRecord("storefront", Path.Combine(root, "storefront.orchestration.cs"), "demo-oct", "demo-oct", "stable", [], [], [],
            WorkspaceState.Failed, now.AddMinutes(-20), now.AddMinutes(-20), Error: "port 8002 is in use"));
        foreach (var (name, days) in new[] { ("feat-c", 3), ("hotfix-12", 6), ("spike-cache", 15) })
            workspaces.Save(new WorkspaceRecord("storefront", Path.Combine(root, "storefront.orchestration.cs"), name, name, "stable", [], [], [],
                WorkspaceState.Down, now.AddDays(-days), now.AddDays(-days)));
        workspaces.Save(new WorkspaceRecord("services", Path.Combine(root, "services.orchestration.cs"), "local", null, "stable", [], [], [], WorkspaceState.Up, now, now));
        workspaces.Save(new WorkspaceRecord("analytics", Path.Combine(root, "analytics.orchestration.cs"), "ingest-perf", "ingest-perf", "stable", [], [], [],
            WorkspaceState.Down, now.AddDays(-4), now.AddDays(-4)));

        var at = new SourceLocation(Path.Combine(root, "shop-services", "sprig.cs"), 12);
        PlannedValue Value(string v, params string[] via) => new(v, v, via);
        workspaces.SavePlan(new Plan("storefront", "feat-a", "stable", "main",
            [.. SampleApps.Where(a => a.Port is not null).Select(a => new PlannedPort(a.Repo, a.App, "http", a.Port!.Value, null, null))],
            [.. SampleApps.Select(a => new PlannedApp(a.Repo, $"../{a.Repo}", a.App, "dotnet", a.Job ? $"tools/{a.App}" : ".",
                a.Port is { } port
                    ? [new PlannedEnv("Urls", Value($"http://localhost:{port}", $"{a.App}.http port {port}"), "appsettings.Local.json", at, "appSettings"),
                       new PlannedEnv("ConnectionStrings__Db", Value("Host=localhost;Port=8002;Password=hunter2", "postgres.tcp port 8002"), "appsettings.Local.json", at, "appSettings"),
                       new PlannedEnv("Mail__Smtp", Value("smtp://localhost:8011", "import mail-smtp ← storefront"), "appsettings.Local.json", at, "appSettings")]
                    : [],
                null, a.Job ? [] : ["migrate"], Job: a.Job,
                DependsOn: a.Repo == "shop-services" && !a.Job ? ["shop-services/migrate", "shop-services/seed"] : null,
                Health: a.Port is not null && !a.Job ? [new PlannedHealth("http", "/health")] : null,
                Links: a.App switch
                {
                    "web" => [new PlannedLink("Storefront", $"http://localhost:{a.Port}"), new PlannedLink("Admin as dev", $"http://localhost:{a.Port}/admin/login?as=dev")],
                    "payments-api" => [new PlannedLink("Swagger", $"http://localhost:{a.Port}/swagger")],
                    _ => null,
                }))],
            [], [], [], [new PlanProblem(ProblemSeverity.Warning, "ingest-worker has no health check, so \"ready\" only means it started",
                new SourceLocation(Path.Combine(root, "shop-analytics", "sprig.cs"), 14))],
            [new PlannedLink("Mail inbox", "http://localhost:8025"), new PlannedLink("Checkout flow", "http://localhost:5170/cart?seed=demo")]));

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
