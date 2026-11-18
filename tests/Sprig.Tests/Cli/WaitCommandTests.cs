using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Sprig.Cli;
using Sprig.Cli.Commands;
using Sprig.Engine.Hosting;
using Sprig.Engine.Planning;
using Sprig.Engine.Workspaces;

namespace Sprig.Tests.Cli;

/// <summary>Against the real <c>sprig-host</c> exe and a throwaway store holding one workspace with a plan.</summary>
[Collection("cli-console")]
public sealed class WaitCommandTests : IDisposable
{
    readonly TempStore _store = new();
    readonly HostClient _client;
    const string Key = "storefront/feat-x";

    public WaitCommandTests()
    {
        _client = new HostClient(_store.Paths);
        var now = DateTimeOffset.UtcNow;
        var workspaces = new WorkspaceStore(_store.Paths);
        workspaces.Save(new WorkspaceRecord("storefront", @"C:\src\storefront.yaml", "feat-x", "feat-x", "stable",
            [new WorkspaceRepo("../shop", "shop", _store.Root, _store.Root, false, false)], [], [], WorkspaceState.Up, now, now));
        workspaces.SavePlan(new Plan("storefront", "feat-x", "stable", null, [],
            new[] { "api", "slow", "worker" }.Select(a => new PlannedApp("shop", "../shop", a, "app", ".", [], null, [])).ToArray(), [], [], [], []));
    }

    public void Dispose()
    {
        _client.StopAllAndShutdown();
        _store.Dispose();
    }

    (int Code, JsonElement Json, TimeSpan Took) Wait(params string[] args)
    {
        var (prevOut, prevErr) = (Console.Out, Console.Error);
        var (stdout, stderr) = (new StringWriter(), new StringWriter());
        Console.SetOut(stdout);
        Console.SetError(stderr);
        var clock = Stopwatch.StartNew();
        try
        {
            var code = CliApp.Run(["wait", "feat-x", .. args, "--json"], _store.Paths);
            return (code, JsonDocument.Parse(stdout.ToString()).RootElement.Clone(), clock.Elapsed);
        }
        finally
        {
            Console.SetOut(prevOut);
            Console.SetError(prevErr);
        }
    }

    /// <summary>Prints <paramref name="line"/> after about <paramref name="after"/> seconds, then keeps running.</summary>
    RunnerSpec PrintLater(string name, string line, int after) => new(Key, $"shop/{name}", OperatingSystem.IsWindows()
        ? new CommandSpec("cmd.exe", ["/d", "/c", $"ping -n {after + 1} 127.0.0.1 > nul & echo {line} & ping -n 30 127.0.0.1 > nul"])
        : new CommandSpec("/bin/sh", ["-c", $"sleep {after}; echo {line}; sleep 30"]), _store.Root, ReadyLog: line);

    static int ClosedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [Fact]
    public void Waits_for_a_named_app_to_become_ready_and_with_none_named_for_what_the_workspace_has_going()
    {
        _client.Send(new HostRequest("start", [PrintLater("api", "api-up", after: 2)]));

        var named = Wait("api");
        Assert.True(named.Code == 0, named.Json.ToString());
        Assert.True(named.Json.GetProperty("ok").GetBoolean());
        Assert.True(named.Took > TimeSpan.FromSeconds(1), $"returned after {named.Took.TotalSeconds:0.0}s");

        var all = Wait();
        Assert.Equal(0, all.Code);
        Assert.Equal(["shop/api"], all.Json.GetProperty("runners").EnumerateArray().Select(r => r.GetProperty("Name").GetString()));
    }

    [Fact]
    public void An_app_that_is_not_running_is_said_at_once_rather_waited_for()
    {
        var nothing = Wait();
        Assert.Equal(1, nothing.Code);
        Assert.Equal(WaitCommand.NotRunning, nothing.Json.GetProperty("code").GetString());

        _client.Send(new HostRequest("start", [PrintLater("api", "api-up", after: 0)]));
        var named = Wait("api", "worker");

        Assert.Equal(1, named.Code);
        Assert.Equal(WaitCommand.NotRunning, named.Json.GetProperty("code").GetString());
        Assert.Equal(["shop/worker"], named.Json.GetProperty("notRunning").EnumerateArray().Select(n => n.GetString()));
        Assert.True(named.Took < TimeSpan.FromSeconds(15), $"took {named.Took.TotalSeconds:0.0}s");
    }

    [Fact]
    public void An_app_that_never_becomes_ready_times_out_with_what_start_says()
    {
        var never = OperatingSystem.IsWindows()
            ? new CommandSpec("cmd.exe", ["/d", "/c", "ping -n 30 127.0.0.1 > nul"])
            : new CommandSpec("/bin/sh", ["-c", "sleep 30"]);
        _client.Send(new HostRequest("start", [new RunnerSpec(Key, "shop/slow", never, _store.Root, Port: ClosedPort())]));

        var slow = Wait("slow", "--timeout", "2");

        Assert.Equal(1, slow.Code);
        Assert.Equal("start-timed-out", slow.Json.GetProperty("code").GetString());
        Assert.InRange(slow.Took, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(15));
        Assert.Equal("shop/slow", slow.Json.GetProperty("failed")[0].GetProperty("Name").GetString());
    }
}
