using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Sprig.Engine.Hosting;
using Sprig.Engine.Planning;
using Sprig.Engine.Sprouts;
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

    static CommandSpec Wait(int seconds) => OperatingSystem.IsWindows()
        ? new CommandSpec("cmd.exe", ["/d", "/c", $"echo hello from %GREETING% & ping -n {seconds} 127.0.0.1 > nul"])
        : new CommandSpec("/bin/sh", ["-c", $"echo hello from $GREETING; sleep {seconds}"]);

    /// <summary>The host keeps a running app's log open for writing, so it's read the way <c>sprig logs</c> reads it.</summary>
    static string ReadShared(string file)
    {
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
        var spec = new RunnerSpec("storefront/feat-x", "shop/api", Wait(30), _store.Root,
            new Dictionary<string, string> { ["GREETING"] = "sprig" }, Port: port);

        Assert.True(_client.Send(new HostRequest("start", [spec])).Ok);
        var ready = Await("shop/api", r => r.State == RunnerState.Ready);
        Await("shop/api", r => ReadShared(r.LogFile).Contains("hello from sprig"));

        var stopped = Assert.Single(_client.StopAndWait("storefront/feat-x", "shop/api"));

        Assert.Equal(RunnerState.Stopped, stopped.State);
        Assert.Null(stopped.Error);
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(ready.Pid!.Value));
        var log = ReadShared(ready.LogFile);
        Assert.Contains("sprig: sent Ctrl+C", log);
        Assert.Contains("sprig: stopped in", log);
    }

    [Fact]
    public void A_failed_build_stops_the_app_from_starting_and_stopping_a_sprout_forgets_it()
    {
        var spec = new RunnerSpec("storefront/feat-x", "shop/web", Wait(30), _store.Root, new Dictionary<string, string>(), Build: Exit(3), BuildKey: "shop");

        _client.Send(new HostRequest("start", [spec]));

        var failed = Await("shop/web", r => r.State == RunnerState.Failed);
        Assert.Equal("the build exited with code 3", failed.Error);

        _client.Send(new HostRequest("stop", Sprout: "storefront/feat-x"));
        Await("shop/web", _ => true);
        var clock = Stopwatch.StartNew();
        while (_client.Send(new HostRequest("list")).Runners!.Count > 0 && clock.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(100);
        Assert.Empty(_client.Send(new HostRequest("list")).Runners!);
    }

    static CommandSpec Exit(int code) => OperatingSystem.IsWindows()
        ? new CommandSpec("cmd.exe", ["/d", "/c", $"exit {code}"])
        : new CommandSpec("/bin/sh", ["-c", $"exit {code}"]);

    RunnerSpec Spec(string name, CommandSpec command, bool job = false, params string[] waitFor)
        => new("storefront/feat-x", name, command, _store.Root, new Dictionary<string, string> { ["GREETING"] = name }, WaitFor: waitFor, Job: job);

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
}

public class RunnerSpecsTests
{
    static readonly SourceLocation At = new("x", 1);

    [Fact]
    public void Apps_run_by_kind_with_their_process_env_and_port()
    {
        var repo = new SproutRepo("../shop-services", "shop-services", @"C:\tb\shop-services", @"C:\tb\sf--x\shop-services", true, true);
        var record = new SproutRecord("storefront", @"C:\tb\sf.trellis.cs", "x", "x", "stable", [repo], [], [], SproutState.Up,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        PlannedEnv Env(string key, string value, string? file = null) => new(key, new PlannedValue(value, value, []), file, At);
        var plan = new Plan("storefront", "x", "stable", "main",
            [new PlannedPort("shop-services", "api", "http", 8001, null, null)],
            [
                new PlannedApp("shop-services", "../shop-services", "api", "dotnet", "src/Api", [Env("Urls", "http://localhost:8001"), Env("A", "1", ".env.local")], null, [], []),
                new PlannedApp("shop-services", "../shop-services", "web", "node", "web", [], null, [], []),
                new PlannedApp("shop-services", "../shop-services", "tool", "app", ".", [], new PlannedValue("go run .", "go run .", []), [], []),
            ],
            [], [], [], []);

        var specs = RunnerSpecs.For(record, plan);

        var api = specs.Single(s => s.Name == "shop-services/api");
        Assert.Equal("dotnet run --no-build --no-launch-profile", api.Command.ToString());
        Assert.Equal("dotnet build --nologo -v minimal", api.Build!.ToString());
        Assert.Equal(repo.Dir, api.BuildKey);
        Assert.Equal(Path.GetFullPath(@"C:\tb\sf--x\shop-services\src\Api"), api.WorkingDirectory);
        Assert.Equal("http://localhost:8001", api.Env["Urls"]);
        Assert.Equal("Development", api.Env["ASPNETCORE_ENVIRONMENT"]);
        Assert.False(api.Env.ContainsKey("A"));
        Assert.Equal(8001, api.Port);
        Assert.Equal("storefront/x", api.Sprout);

        Assert.EndsWith("npm run dev", specs.Single(s => s.Name == "shop-services/web").Command.ToString());
        Assert.EndsWith("go run .", specs.Single(s => s.Name == "shop-services/tool").Command.ToString());

        var e = Assert.Throws<InvalidOperationException>(() => RunnerSpecs.For(record, plan, ["nope"]));
        Assert.Contains("no app 'nope'", e.Message);
    }

    [Fact]
    public void Starting_one_app_brings_what_it_depends_on_first()
    {
        var repo = new SproutRepo("../shop", "shop", @"C:\tb\shop", @"C:\tb\shop", false, false);
        var record = new SproutRecord("storefront", @"C:\tb\sf.trellis.cs", "local", null, "stable", [repo], [], [], SproutState.Up,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        PlannedApp App(string name, bool job = false, params string[] dependsOn)
            => new("shop", "../shop", name, "app", ".", [], new PlannedValue("x", "x", []), [], [], job, dependsOn);
        var plan = new Plan("storefront", "local", "stable", null, [],
            [App("web", false, "shop/api"), App("api", false, "shop/migrate"), App("migrate", true), App("admin")], [], [], [], []);

        var specs = RunnerSpecs.For(record, plan, ["web"]);

        Assert.Equal(["shop/migrate", "shop/api", "shop/web"], specs.Select(s => s.Name));
        Assert.True(specs[0].Job);
        Assert.Equal(["shop/migrate"], specs[1].WaitFor);
        Assert.Null(specs[0].Port);
    }
}
