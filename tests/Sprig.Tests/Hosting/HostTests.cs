using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Sprig.Engine.Hosting;
using Sprig.Engine.Planning;
using Sprig.Engine.Workspaces;
using Sprig.Model;

namespace Sprig.Tests.Hosting;

/// <summary>
/// The real <c>sprig-host</c> exe against a throwaway store (so its own pipe), running real processes. Out of process
/// because stopping an app attaches the host to that app's console, which would take the test runner's own console
/// from it.
/// </summary>
public sealed class HostTests : IDisposable
{
    readonly TempStore _store = new();
    readonly HostClient _client;

    public HostTests()
    {
        _client = new HostClient(_store.Paths);
        Assert.True(_client.Send(new HostRequest("ping")).Ok);
    }

    public void Dispose()
    {
        _client.TrySend(new HostRequest("shutdown"));
        var clock = Stopwatch.StartNew();
        while (_client.TrySend(new HostRequest("ping"), TimeSpan.FromMilliseconds(100)) is not null && clock.Elapsed < TimeSpan.FromSeconds(30))
            Thread.Sleep(100);
        _store.Dispose();
    }

    static CommandSpec Wait(int seconds, string greeting = "sprig") => OperatingSystem.IsWindows()
        ? new CommandSpec("cmd.exe", ["/d", "/c", $"echo hello from {greeting} & ping -n {seconds} 127.0.0.1 > nul"])
        : new CommandSpec("/bin/sh", ["-c", $"echo hello from {greeting}; sleep {seconds}"]);

    /// <summary>The host keeps a running app's log open for writing, so it's read the way <c>sprig logs</c> reads it.</summary>
    static string ReadShared(string file)
    {
        if (!File.Exists(file)) return "";
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return new StreamReader(stream).ReadToEnd();
    }

    RunnerStatus Await(string name, Func<RunnerStatus, bool> until)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(20))
        {
            var r = _client.TrySend(new HostRequest("list"))?.Runners?.FirstOrDefault(x => x.Name == name);
            if (r is not null && until(r)) return r;
            Thread.Sleep(100);
        }
        throw new TimeoutException($"{name} never reached the expected state");
    }

    [Fact]
    public void A_runner_logs_its_output_becomes_ready_on_its_port_and_stopping_ends_its_process()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var spec = new RunnerSpec("storefront/feat-x", "shop/api", Wait(30), _store.Root, Port: port);

        Assert.True(_client.Send(new HostRequest("start", [spec])).Ok);
        var ready = Await("shop/api", r => r.State == RunnerState.Ready);
        Await("shop/api", r => ReadShared(r.LogFile).Contains("hello from sprig"));

        var stopped = Assert.Single(_client.StopAndWait("storefront/feat-x", "shop/api"));

        Assert.Equal(RunnerState.Stopped, stopped.State);
        Assert.True(stopped.Error is null, $"{stopped.Error}\n{ReadShared(ready.LogFile)}");
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(ready.Pid!.Value));
        var log = ReadShared(ready.LogFile);
        Assert.Contains("sprig: sent Ctrl+C", log);
        Assert.Contains("sprig: stopped in", log);
    }

    [Fact]
    public void A_failed_build_stops_the_app_from_starting_and_stopping_a_workspace_forgets_it()
    {
        var spec = new RunnerSpec("storefront/feat-x", "shop/web", Wait(30), _store.Root, Build: Exit(3), BuildKey: "shop");

        _client.Send(new HostRequest("start", [spec]));

        var failed = Await("shop/web", r => r.State == RunnerState.Failed);
        Assert.Equal("the build exited with code 3", failed.Error);

        _client.Send(new HostRequest("stop", Workspace: "storefront/feat-x"));
        var clock = Stopwatch.StartNew();
        while (_client.Send(new HostRequest("list")).Runners!.Count > 0 && clock.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(100);
        Assert.Empty(_client.Send(new HostRequest("list")).Runners!);
    }

    static CommandSpec Exit(int code) => OperatingSystem.IsWindows()
        ? new CommandSpec("cmd.exe", ["/d", "/c", $"exit {code}"])
        : new CommandSpec("/bin/sh", ["-c", $"exit {code}"]);

    RunnerSpec Spec(string name, CommandSpec command, bool job = false, params string[] waitFor)
        => new("storefront/feat-x", name, command, _store.Root, WaitFor: waitFor, Job: job);

    [Fact]
    public void An_app_waits_for_its_job_to_finish_and_starting_it_again_leaves_what_is_going_alone()
    {
        var specs = new[] { Spec("shop/migrate", Exit(0), job: true), Spec("shop/api", Wait(30), waitFor: "shop/migrate") };

        _client.Send(new HostRequest("start", specs));

        Assert.Equal(RunnerState.Completed, Await("shop/migrate", r => r.State.IsSettled()).State);
        var api = Await("shop/api", r => r.State == RunnerState.Ready);
        Assert.Contains("waiting for shop/migrate", ReadShared(api.LogFile));

        var again = _client.Send(new HostRequest("start", specs)).Runners!;
        Assert.Empty(again);
        Assert.Equal(api.Pid, Await("shop/api", _ => true).Pid);

        var restarted = _client.Send(new HostRequest("start", specs, Restart: ["shop/api"])).Runners!;
        Assert.Equal("shop/api", Assert.Single(restarted).Name);
    }

    [Fact]
    public void A_held_start_builds_then_waits_until_a_plain_start_releases_it()
    {
        var spec = Spec("shop/migrate", Exit(0), job: true) with { Build = Exit(0), BuildKey = "shop" };

        _client.Send(new HostRequest("start", [spec], Hold: true));

        var held = Await("shop/migrate", r => ReadShared(r.LogFile).Contains("waiting for the workspace's infra"));
        Assert.Equal(RunnerState.Waiting, held.State);
        Thread.Sleep(500);
        Assert.Equal(RunnerState.Waiting, Await("shop/migrate", _ => true).State);

        Assert.Empty(_client.Send(new HostRequest("start", [spec])).Runners!);
        Assert.Equal(RunnerState.Completed, Await("shop/migrate", r => r.State.IsSettled()).State);
    }

    [Fact]
    public void Stopping_a_held_workspace_ends_its_runners_without_starting_them()
    {
        _client.Send(new HostRequest("start", [Spec("shop/migrate", Exit(0), job: true)], Hold: true));
        Await("shop/migrate", r => r.State == RunnerState.Waiting);

        var stopped = Assert.Single(_client.StopAndWait("storefront/feat-x"));

        Assert.DoesNotContain("sprig: starting", ReadShared(stopped.LogFile));
    }

    bool HostGone()
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (_client.TrySend(new HostRequest("ping"), TimeSpan.FromMilliseconds(100)) is null) return true;
            Thread.Sleep(100);
        }
        return false;
    }

    [Fact]
    public void An_idle_host_is_ended_on_request_but_one_running_apps_is_left_alone()
    {
        _client.Send(new HostRequest("start", [Spec("shop/api", Wait(30))]));
        Await("shop/api", r => r.State == RunnerState.Ready);

        Assert.False(_client.ShutdownIfIdle());
        Assert.NotNull(_client.TrySend(new HostRequest("ping")));

        Assert.Single(_client.StopAndWait("storefront/feat-x"));
        Assert.True(_client.ShutdownIfIdle());
        Assert.True(HostGone());
        Assert.True(_client.ShutdownIfIdle());
    }

    [Fact]
    public void Stopping_everything_ends_every_workspaces_apps_and_then_the_host()
    {
        _client.Send(new HostRequest("start", [Spec("shop/api", Wait(30))]));
        _client.Send(new HostRequest("start", [Spec("shop/web", Wait(30)) with { Workspace = "storefront/feat-y" }]));
        Await("shop/api", r => r.State == RunnerState.Ready);
        Await("shop/web", r => r.State == RunnerState.Ready);

        var stopped = _client.StopAllAndShutdown();

        Assert.Equal(["shop/api", "shop/web"], stopped.Select(r => r.Name).Order());
        Assert.All(stopped, r => Assert.Equal(RunnerState.Stopped, r.State));
        Assert.True(HostGone());
    }

    [Fact]
    public void A_failed_job_keeps_what_waits_for_it_from_starting()
    {
        _client.Send(new HostRequest("start", [Spec("shop/migrate", Exit(4), job: true), Spec("shop/api", Wait(30), waitFor: "shop/migrate")]));

        Assert.Equal("exited with code 4", Await("shop/migrate", r => r.State.IsSettled()).Error);
        var api = Await("shop/api", r => r.State.IsSettled());
        Assert.Equal(RunnerState.Failed, api.State);
        Assert.Equal("waits for shop/migrate, which exited with code 4", api.Error);
        Assert.Null(api.Pid);
    }

    [Fact]
    public void The_host_survives_signalling_one_app_after_another()
    {
        _client.Send(new HostRequest("start", [Spec("shop/a", Wait(30)), Spec("shop/b", Wait(30))]));
        Await("shop/a", r => r.State == RunnerState.Ready);
        Await("shop/b", r => r.State == RunnerState.Ready);

        var a = Assert.Single(_client.StopAndWait("storefront/feat-x", "shop/a"));
        var b = Assert.Single(_client.StopAndWait("storefront/feat-x", "shop/b"));

        Assert.Null(a.Error);
        Assert.Null(b.Error);
        Assert.NotNull(_client.TrySend(new HostRequest("ping")));
    }

    [Fact]
    public async Task A_second_host_for_the_same_store_steps_aside()
        => Assert.Equal(2, await new HostServer(_store.Paths, TimeSpan.FromMinutes(1)).RunAsync());

    /// <summary>Prints <paramref name="line"/> after about <paramref name="after"/> seconds, then keeps running.</summary>
    static CommandSpec PrintLater(string line, int after) => OperatingSystem.IsWindows()
        ? new CommandSpec("cmd.exe", ["/d", "/c", $"ping -n {after + 1} 127.0.0.1 > nul & echo {line} & ping -n 30 127.0.0.1 > nul"])
        : new CommandSpec("/bin/sh", ["-c", $"sleep {after}; echo {line}; sleep 30"]);

    [Fact]
    public void An_app_with_a_log_pattern_is_ready_only_once_a_line_matches_even_with_its_port_open()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var spec = Spec("shop/web", PrintLater("VITE ready in 312 ms", after: 3)) with { Port = port, ReadyLog = @"ready in \d+" };

        _client.Send(new HostRequest("start", [spec]));

        Await("shop/web", r => r.State == RunnerState.Starting);
        Thread.Sleep(1000);
        Assert.Equal(RunnerState.Starting, Await("shop/web", _ => true).State);
        var ready = Await("shop/web", r => r.State == RunnerState.Ready);
        Assert.Contains(@"sprig: ready: a line matched ready in \d+", ReadShared(ready.LogFile));
    }

    [Fact]
    public void An_app_with_a_health_url_is_ready_only_once_it_answers_with_success()
    {
        using var health = new HealthServer(503);
        var spec = Spec("shop/api", Wait(30)) with { Port = health.Port, HealthUrls = [$"http://localhost:{health.Port}/health"] };

        _client.Send(new HostRequest("start", [spec]));

        var waiting = Await("shop/api", r => ReadShared(r.LogFile).Contains("answered 503"));
        Assert.Equal(RunnerState.Starting, waiting.State);
        health.Status = 200;
        var ready = Await("shop/api", r => r.State == RunnerState.Ready);
        Assert.Contains($"sprig: ready: http://localhost:{health.Port}/health answered 200", ReadShared(ready.LogFile));
    }

    [Fact]
    public void An_app_that_keeps_failing_is_restarted_with_backoff_then_left_exited()
    {
        _client.Send(new HostRequest("start", [Spec("shop/api", Exit(3)) with { Restart = new RestartSpec(Always: false, Max: 2) }]));

        Assert.Equal("exited with code 3; restarting in 1s (1 of 2)", Await("shop/api", r => r.State == RunnerState.Restarting).Error);
        var exited = Await("shop/api", r => r.State == RunnerState.Exited);

        Assert.Equal("exited with code 3 after 2 restarts", exited.Error);
        Assert.Equal(2, exited.Restarts);
        Assert.Equal(3, ReadShared(exited.LogFile).Split("sprig: starting").Length - 1);
    }

    [Fact]
    public void A_job_that_fails_once_is_retried_and_what_waits_for_it_then_starts()
    {
        // Unquoted: .NET escapes quotes in arguments as \", which cmd doesn't understand. The store path has no spaces.
        var marker = Path.Combine(_store.Root, "tried-once");
        Assert.DoesNotContain(' ', marker);
        var flaky = OperatingSystem.IsWindows()
            ? new CommandSpec("cmd.exe", ["/d", "/c", $"if exist {marker} (exit 0) else (type nul > {marker} & exit 5)"])
            : new CommandSpec("/bin/sh", ["-c", $"if [ -e '{marker}' ]; then exit 0; else touch '{marker}'; exit 5; fi"]);
        var specs = new[]
        {
            Spec("shop/migrate", flaky, job: true) with { Restart = new RestartSpec(Always: false, Max: 3) },
            Spec("shop/api", Wait(30), waitFor: "shop/migrate"),
        };

        _client.Send(new HostRequest("start", specs));

        var migrate = Await("shop/migrate", r => r.State.IsSettled());
        Assert.Equal(RunnerState.Completed, migrate.State);
        Assert.Equal(1, migrate.Restarts);
        Await("shop/api", r => r.State == RunnerState.Ready);
    }

    [Fact]
    public void Stopping_an_app_while_it_waits_to_restart_keeps_it_stopped()
    {
        _client.Send(new HostRequest("start", [Spec("shop/api", Exit(1)) with { Restart = new RestartSpec(Always: true, Max: 5) }]));
        Await("shop/api", r => r.State == RunnerState.Restarting);

        var stopped = Assert.Single(_client.StopAndWait("storefront/feat-x", "shop/api"));
        Thread.Sleep(2500);

        Assert.Equal(RunnerState.Stopped, stopped.State);
        var now = Await("shop/api", _ => true);
        Assert.Equal(RunnerState.Stopped, now.State);
        Assert.Equal(1, ReadShared(now.LogFile).Split("sprig: starting").Length - 1);
    }

    [Fact]
    public async Task Following_a_workspaces_apps_streams_their_logs_and_ctrl_c_stops_them()
    {
        var output = new StringWriter();
        var ansi = Spectre.Console.AnsiConsole.Create(new Spectre.Console.AnsiConsoleSettings
        {
            Ansi = Spectre.Console.AnsiSupport.No,
            ColorSystem = Spectre.Console.ColorSystemSupport.NoColors,
            Out = new Spectre.Console.AnsiConsoleOutput(output),
        });
        var processes = new Sprig.Engine.Processes.ProcessRunner();
        var settings = new Sprig.Engine.Settings.FileSettingsStore(_store.Paths);
        var trust = new Sprig.Engine.Definitions.FileTrustStore(_store.Paths);
        var cli = new Sprig.Cli.CliContext(_store.Paths, settings, trust,
            new Sprig.Engine.Definitions.DefinitionRunner(trust, new Sprig.Engine.Definitions.DotnetRunHost(), _store.Paths),
            new Sprig.Engine.Orchestrations.OrchestrationRegistry(_store.Paths),
            new WorkspaceService(new WorkspaceStore(_store.Paths), new PortLeases(_store.Paths), settings,
                new Sprig.Engine.Git.GitClient(processes), new Sprig.Engine.Compose.DockerCompose(processes), host: _client),
            _client, ansi);
        var record = new WorkspaceRecord("storefront", _store.Root, "feat-x", null, "stable", [], [], [], WorkspaceState.Up,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        using var stop = new CancellationTokenSource();

        var run = Task.Run(() => new Sprig.Cli.Commands.AppFollow(cli, record, [Spec("shop/api", Wait(30, "shop/api")), Spec("shop/web", Wait(30, "shop/web"))])
            .Run(stop.Token, output, colour: false));
        Await("shop/api", r => r.State == RunnerState.Ready);
        Await("shop/web", r => r.State == RunnerState.Ready);
        stop.Cancel();

        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(30)));
        var text = output.ToString();
        var alive = _client.TrySend(new HostRequest("ping"), TimeSpan.FromSeconds(2)) is { } pong ? $"host alive (pid {pong.Pid})" : "host gone";
        foreach (var expected in new[] { "api | hello from shop/api", "web | hello from shop/web", "Stopping the apps", "shop/api stopped", "shop/web stopped" })
            Assert.True(text.Contains(expected), $"no '{expected}' ({alive}) in:\n{text}");
        // A whole-workspace stop forgets each runner a moment after it reports stopped.
        var clock = Stopwatch.StartNew();
        while (_client.Send(new HostRequest("list")).Runners!.Count > 0 && clock.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(100);
        Assert.Empty(_client.Send(new HostRequest("list")).Runners!);
    }

    /// <summary>Answers every request with <see cref="Status"/>, which a test can change while it runs.</summary>
    sealed class HealthServer : IDisposable
    {
        readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        readonly CancellationTokenSource _stop = new();

        public HealthServer(int status)
        {
            Status = status;
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public volatile int Status;

        async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    var stream = client.GetStream();
                    var reader = new StreamReader(stream);
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync(_stop.Token))) { }
                    var reply = $"HTTP/1.1 {Status} X\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(reply), _stop.Token);
                }
                catch (OperationCanceledException) { return; }
                catch (IOException) { }
                catch (SocketException) { }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }
}

public class RunnerSpecsTests
{
    static readonly SourceLocation At = new("x", 1);

    [Fact]
    public void Apps_run_by_kind_as_you_would_run_them_yourself_with_their_port()
    {
        var repo = new WorkspaceRepo("../shop-services", "shop-services", @"C:\tb\shop-services", @"C:\tb\sf--x\shop-services", true, true);
        var record = new WorkspaceRecord("storefront", @"C:\tb\sf.orchestration.cs", "x", "x", "stable", [repo], [], [], WorkspaceState.Up,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        PlannedEnv Env(string key, string value) => new(key, new PlannedValue(value, value, []), "Properties/launchSettings.json", At, "launchSettings", "sprig");
        var plan = new Plan("storefront", "x", "stable", "main",
            [new PlannedPort("shop-services", "api", "http", 8001, null, null)],
            [
                new PlannedApp("shop-services", "../shop-services", "api", "dotnet", "src/Api", [Env("Urls", "http://localhost:8001")], null, [],
                    Health: [new PlannedHealth("http", "/health")], Restart: new RestartModel("always", 2, At)),
                new PlannedApp("shop-services", "../shop-services", "web", "node", "web", [], null, []),
                new PlannedApp("shop-services", "../shop-services", "tool", "app", ".", [], new PlannedValue("go run .", "go run .", []), []),
            ],
            [], [], [], []);

        var specs = RunnerSpecs.For(record, plan);

        var api = specs.Single(s => s.Name == "shop-services/api");
        // A plain dotnet run, launch profile and all, so the values in its files are what it gets.
        Assert.Equal("dotnet run --no-build", api.Command.ToString());
        Assert.Equal("dotnet build --nologo -v minimal", api.Build!.ToString());
        Assert.Equal(repo.Dir, api.BuildKey);
        Assert.Equal(Path.GetFullPath(@"C:\tb\sf--x\shop-services\src\Api"), api.WorkingDirectory);
        Assert.Equal(8001, api.Port);
        Assert.Equal("storefront/x", api.Workspace);
        Assert.Equal(["http://localhost:8001/health"], api.HealthUrls);
        Assert.Equal(new RestartSpec(Always: true, Max: 2), api.Restart);
        Assert.Null(specs.Single(s => s.Name == "shop-services/web").Restart);

        Assert.EndsWith("npm run dev", specs.Single(s => s.Name == "shop-services/web").Command.ToString());
        Assert.EndsWith("go run .", specs.Single(s => s.Name == "shop-services/tool").Command.ToString());

        var e = Assert.Throws<InvalidOperationException>(() => RunnerSpecs.For(record, plan, ["nope"]));
        Assert.Contains("no app 'nope'", e.Message);
    }

    [Fact]
    public void A_pnpm_workspace_s_apps_run_with_pnpm_and_share_one_install_done_again_when_the_lockfile_changes()
    {
        using var store = new TempStore();
        var dir = Path.Combine(store.Root, "shop");
        void Write(string relative, string text)
        {
            var path = Path.Combine(dir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        Write("frontend/pnpm-workspace.yaml", "packages: [apps/*]");
        Write("frontend/pnpm-lock.yaml", "lockfileVersion: '9.0'");
        Write("frontend/apps/web/package.json", """{ "scripts": { "dev": "vite" } }""");
        Write("frontend/apps/admin/package.json", """{ "scripts": { "dev": "vite" } }""");
        Write("legacy/package.json", """{ "scripts": { "dev": "vite" } }""");
        Write("legacy/package-lock.json", "{}");
        Write("solo/package.json", """{ "scripts": { "dev": "vite" }, "packageManager": "pnpm@10.0.0" }""");

        var repo = new WorkspaceRepo("../shop", "shop", dir, dir, false, false);
        var record = new WorkspaceRecord("shop", "x", "main", null, "stable", [repo], [], [], WorkspaceState.Up, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        PlannedApp Node(string name, string path) => new("shop", "../shop", name, "node", path, [], null, []);
        var plan = new Plan("shop", "main", "stable", null, [],
            [Node("web", "frontend/apps/web"), Node("admin", "frontend/apps/admin"), Node("legacy", "legacy"), Node("solo", "solo")], [], [], [], []);
        RunnerSpec Spec(string name) => RunnerSpecs.For(record, plan).Single(s => s.Name == $"shop/{name}");

        var frontend = Path.GetFullPath(Path.Combine(dir, "frontend"));
        Assert.EndsWith("pnpm run dev", Spec("web").Command.ToString());
        Assert.EndsWith("pnpm install --frozen-lockfile --ignore-scripts", Spec("web").Build!.ToString());
        Assert.Equal(frontend, Spec("web").BuildKey);
        Assert.Equal(frontend, Spec("admin").BuildKey);
        // An app with an npm lockfile of its own keeps npm, and one that only names pnpm installs without a lockfile.
        Assert.EndsWith("npm run dev", Spec("legacy").Command.ToString());
        Assert.EndsWith("npm ci --ignore-scripts", Spec("legacy").Build!.ToString());
        Assert.EndsWith("pnpm run dev", Spec("solo").Command.ToString());
        Assert.EndsWith("pnpm install --ignore-scripts", Spec("solo").Build!.ToString());

        Write("frontend/node_modules/.modules.yaml", "layoutVersion: 5");
        File.SetLastWriteTimeUtc(Path.Combine(dir, "frontend/pnpm-lock.yaml"), DateTime.UtcNow.AddMinutes(-5));
        Assert.Null(Spec("web").Build);
        Assert.Null(Spec("web").BuildKey);

        File.SetLastWriteTimeUtc(Path.Combine(dir, "frontend/pnpm-lock.yaml"), DateTime.UtcNow.AddMinutes(5));
        Assert.NotNull(Spec("admin").Build);

        // A workspace that lists which packages may run install scripts has pnpm hold every package to that list.
        Write("frontend/pnpm-workspace.yaml", "packages: [apps/*]\nallowBuilds:\n  esbuild: true\n");
        Assert.EndsWith("pnpm install --frozen-lockfile", Spec("web").Build!.ToString());
    }

    [Fact]
    public void Starting_one_app_brings_what_it_depends_on_first()
    {
        var repo = new WorkspaceRepo("../shop", "shop", @"C:\tb\shop", @"C:\tb\shop", false, false);
        var record = new WorkspaceRecord("storefront", @"C:\tb\sf.orchestration.cs", "local", null, "stable", [repo], [], [], WorkspaceState.Up,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        PlannedApp App(string name, bool job = false, params string[] dependsOn)
            => new("shop", "../shop", name, "app", ".", [], new PlannedValue("x", "x", []), [], job, dependsOn);
        var plan = new Plan("storefront", "local", "stable", null, [],
            [App("web", false, "shop/api"), App("api", false, "shop/migrate"), App("migrate", true), App("admin")], [], [], [], []);

        var specs = RunnerSpecs.For(record, plan, ["web"]);

        Assert.Equal(["shop/migrate", "shop/api", "shop/web"], specs.Select(s => s.Name));
        Assert.True(specs[0].Job);
        Assert.Equal(["shop/migrate"], specs[1].WaitFor);
        Assert.Null(specs[0].Port);
    }

    [Fact]
    public void Restarting_what_runs_leaves_stopped_apps_stopped_and_reruns_jobs_only_when_asked()
    {
        var repo = new WorkspaceRepo("../shop", "shop", @"C:\tb\shop", @"C:\tb\shop", false, false);
        var record = new WorkspaceRecord("storefront", @"C:\tb\sf.orchestration.cs", "local", null, "stable", [repo], [], [], WorkspaceState.Up,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        PlannedApp App(string name, bool job = false, params string[] dependsOn)
            => new("shop", "../shop", name, "app", ".", [], new PlannedValue("x", "x", []), [], job, dependsOn);
        var plan = new Plan("storefront", "local", "stable", null, [],
            [App("web", false, "shop/api", "shop/auth"), App("api", false, "shop/migrate"), App("auth"), App("migrate", true), App("admin")], [], [], [], []);
        RunnerStatus Is(string name, RunnerState state) => new(record.Key, $"shop/{name}", state, null, null, null, "x.log", DateTimeOffset.UtcNow);
        // auth was stopped to run it from an IDE; admin was never started.
        IReadOnlyList<RunnerStatus> runners = [Is("web", RunnerState.Ready), Is("api", RunnerState.Ready), Is("auth", RunnerState.Stopped),
            Is("migrate", RunnerState.Completed), new RunnerStatus("other/1", "shop/admin", RunnerState.Ready, null, null, null, "x.log", DateTimeOffset.UtcNow)];

        var (specs, restart) = RunnerSpecs.RestartRunning(record, plan, runners);

        Assert.Equal(["shop/web", "shop/api"], restart);
        Assert.Equal(["shop/migrate", "shop/api", "shop/web"], specs.Select(s => s.Name));
        Assert.Equal(["shop/api"], specs.Single(s => s.Name == "shop/web").WaitFor);

        Assert.Equal(["shop/web", "shop/api", "shop/migrate"], RunnerSpecs.RestartRunning(record, plan, runners, jobs: true).Restart);
        Assert.Equal(["shop/api"], RunnerSpecs.RestartRunning(record, plan, runners, within: ["api"]).Restart);
        Assert.Empty(RunnerSpecs.RestartRunning(record, plan, [Is("auth", RunnerState.Stopped)]).Specs);
    }
}
