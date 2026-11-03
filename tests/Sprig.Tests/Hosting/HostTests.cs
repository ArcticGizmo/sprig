using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Sprig.Engine.Hosting;
using Sprig.Engine.Planning;
using Sprig.Engine.Sprouts;
using Sprig.Model;

namespace Sprig.Tests.Hosting;

/// <summary>The host's server in-process against a throwaway store (so its own pipe), running real processes.</summary>
public sealed class HostTests : IDisposable
{
    readonly TempStore _store = new();
    readonly HostServer _server;
    readonly Task<int> _running;
    readonly HostClient _client;

    public HostTests()
    {
        _server = new HostServer(_store.Paths, TimeSpan.FromMinutes(5));
        _running = _server.RunAsync();
        _client = new HostClient(_store.Paths);
    }

    public void Dispose()
    {
        _client.TrySend(new HostRequest("shutdown"));
        _running.Wait(TimeSpan.FromSeconds(10));
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

        Assert.True(_client.Send(new HostRequest("start", spec)).Ok);
        var ready = Await("shop/api", r => r.State == RunnerState.Ready);
        Await("shop/api", r => ReadShared(r.LogFile).Contains("hello from sprig"));

        _client.Send(new HostRequest("stop", Sprout: "storefront/feat-x", Name: "shop/api"));

        Assert.Equal(RunnerState.Stopped, Await("shop/api", r => r.State == RunnerState.Stopped).State);
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(ready.Pid!.Value));
    }

    [Fact]
    public void A_failed_build_stops_the_app_from_starting_and_stopping_a_sprout_forgets_it()
    {
        var fail = OperatingSystem.IsWindows() ? new CommandSpec("cmd.exe", ["/d", "/c", "exit 3"]) : new CommandSpec("/bin/sh", ["-c", "exit 3"]);
        var spec = new RunnerSpec("storefront/feat-x", "shop/web", Wait(30), _store.Root, new Dictionary<string, string>(), Build: fail, BuildKey: "shop");

        _client.Send(new HostRequest("start", spec));

        var failed = Await("shop/web", r => r.State == RunnerState.Failed);
        Assert.Equal("the build exited with code 3", failed.Error);

        _client.Send(new HostRequest("stop", Sprout: "storefront/feat-x"));
        Assert.Empty(_client.Send(new HostRequest("list")).Runners!);
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
}
