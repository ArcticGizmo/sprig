using System.Text.Json;
using Sprig.Engine.Hosting;
using Sprig.Engine.Live;
using Sprig.Engine.Planning;
using Sprig.Engine.Workspaces;

namespace Sprig.Tests.Live;

public class WatchLinesTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);

    static WorkspaceRecord Record(string name = "2", string branch = "feat-cart") => new("storefront", @"C:\src\storefront.yaml", name, branch, "stable",
        [new WorkspaceRepo("../shop", "shop", @"C:\src\shop", $@"C:\src\storefront--{name}\shop", true, true)], [], [], WorkspaceState.Up, Now, Now,
        Folder: $@"C:\src\storefront--{name}") { MadeForAgent = "session-1" };

    static Plan Plan(string workspace = "2", params string[] apps) => new("storefront", workspace, "stable", null, [],
        apps.Select(a => new PlannedApp("shop", "../shop", a, "dotnet", ".", [], null, [])).ToArray(), [], [], [], []);

    static RunnerStatus Runner(string name, RunnerState state, int? port = null, string workspace = "storefront/2")
        => new(workspace, name, state, 42, port, null, $"{name}.log", Now, Usage: new ResourceUsage(5, 1000, 2));

    static HostResponse Host(params RunnerStatus[] runners) => new(true, Pid: 4120, Version: "0.9.10", Runners: runners);

    [Fact]
    public void A_snapshot_lists_every_app_of_the_plan_with_the_hosts_state_or_off_and_the_state_people_see()
    {
        var snapshot = WatchLines.Build([(Record(), Plan("2", "api", "web", "worker"))],
            Host(Runner("shop/api", RunnerState.Ready, 5123), Runner("shop/web", RunnerState.Building), Runner("shop/gone", RunnerState.Exited)), usage: false);

        var w = Assert.Single(snapshot.Workspaces);
        Assert.Equal("running", w.State);
        Assert.Equal(["shop/api", "shop/web", "shop/worker", "shop/gone"], w.Apps!.Select(a => a.Name));
        Assert.Equal(["ready", "building", "off", "exited"], w.Apps!.Select(a => a.State));
        Assert.Equal(5123, w.Apps![0].Port);
        // Running, but shop/gone has exited, which outweighs shop/web still building.
        Assert.Equal(("trouble", 1, 3, 3), (w.Readiness.Health, w.Readiness.Ready, w.Readiness.Started, w.Readiness.Total));
        Assert.Equal(["shop/gone"], w.Readiness.Down);
        Assert.All(w.Apps!, a => Assert.Null(a.Usage));
        Assert.Equal([@"C:\src\storefront--2\shop"], w.Dirs);
        Assert.Equal("session-1", w.MadeForAgent);
        Assert.Equal(new WatchHost(true, 4120, "0.9.10"), snapshot.Host);

        var withUsage = WatchLines.Build([(Record(), Plan("2", "api"))], Host(Runner("shop/api", RunnerState.Ready)), usage: true);
        Assert.Equal(new WatchUsage(5, 1000, 2), withUsage.Workspaces[0].Apps![0].Usage);
    }

    [Fact]
    public void Lines_are_camel_case_with_their_type_first_and_leave_out_what_is_null()
    {
        var snapshot = WatchLines.Build([(Record(), Plan("2", "api"))], null, usage: false);

        var text = WatchLines.Serialize(snapshot);

        Assert.StartsWith("""{"type":"snapshot","schema":1,"infra":"not-counted","host":{"running":false}""", text);
        using var json = JsonDocument.Parse(text);
        var app = json.RootElement.GetProperty("workspaces")[0].GetProperty("apps")[0];
        Assert.Equal("""{"name":"shop/api","state":"off"}""", app.GetRawText());
        Assert.Equal("stopped", json.RootElement.GetProperty("workspaces")[0].GetProperty("state").GetString());
        Assert.Equal("""{"health":"none","ready":0,"started":0,"total":1,"down":[]}""",
            json.RootElement.GetProperty("workspaces")[0].GetProperty("readiness").GetRawText());
    }

    [Fact]
    public void After_the_snapshot_only_what_changed_is_sent()
    {
        var diff = new WatchDiff();
        WatchSnapshot Snap(HostResponse? host, params (WorkspaceRecord, Plan?)[] workspaces) => WatchLines.Build(workspaces, host, usage: false);
        var two = (Record(), (Plan?)Plan("2", "api", "web"));

        Assert.IsType<WatchSnapshot>(Assert.Single(diff.Next(Snap(Host(), two))));
        Assert.Empty(diff.Next(Snap(Host(), two)));

        // One app moves on: one app line, carrying its workspace, and the workspace's state with it.
        var lines = diff.Next(Snap(Host(Runner("shop/api", RunnerState.Starting)), two));
        Assert.Equal(["workspace", "app"], lines.Select(Type));
        var app = Assert.IsType<WatchApp>(lines[1]);
        Assert.Equal(("storefront/2", "shop/api", "starting"), (app.Workspace, app.Name, app.State));
        Assert.Equal("running", Assert.IsType<WatchWorkspace>(lines[0]).State);
        Assert.Null(Assert.IsType<WatchWorkspace>(lines[0]).Apps);

        // A paused record, a new workspace, and an app gone from the plan.
        var paused = (Record() with { State = WorkspaceState.Down } with { Paused = new PausedRun(["shop/api"], [], Now) }, (Plan?)Plan("2", "api"));
        var three = (Record("3", "feat-search"), (Plan?)Plan("3", "api"));
        lines = diff.Next(Snap(Host(), paused, three));
        Assert.Equal(["workspace", "app", "app-removed", "workspace", "app"], lines.Select(Type));
        Assert.True(Assert.IsType<WatchWorkspace>(lines[0]).Paused);
        Assert.Equal("shop/web", Assert.IsType<WatchAppRemoved>(lines[2]).Name);

        // The host goes: said once, then each app that was running is off.
        diff.Next(Snap(Host(Runner("shop/api", RunnerState.Ready)), paused, three));
        lines = diff.Next(Snap(null, paused, three));
        Assert.Equal("host", Type(lines[0]));
        Assert.Contains(lines.OfType<WatchApp>(), a => a.Name == "shop/api" && a.State == WatchLines.Off);

        // It comes back, or another replaces it: start again from a snapshot.
        Assert.IsType<WatchSnapshot>(Assert.Single(diff.Next(Snap(Host(), paused, three))));
        Assert.IsType<WatchSnapshot>(Assert.Single(diff.Next(Snap(new HostResponse(true, Pid: 9, Runners: []), paused, three))));

        Assert.Equal(["workspace-removed"], diff.Next(Snap(new HostResponse(true, Pid: 9, Runners: []), paused)).Select(Type));
        Assert.IsType<WatchSnapshot>(Assert.Single(diff.Next(Snap(new HostResponse(true, Pid: 9, Runners: []), paused), fresh: true)));
    }

    static string Type(object line)
    {
        using var json = JsonDocument.Parse(WatchLines.Serialize(line));
        return json.RootElement.GetProperty("type").GetString()!;
    }
}
