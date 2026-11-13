using System.ComponentModel;
using System.Diagnostics;
using Sprig.Engine.Hosting;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Sprig.Cli.Commands;

/// <summary>Settings for commands that act on some or all of a workspace's apps.</summary>
public class AppsSettings : WorkspaceSettings
{
    [CommandArgument(2, "[apps]")]
    [Description("Only these apps, as <name> or <repo>/<name> (default: all of them)")]
    public string[] Apps { get; set; } = [];

    protected override bool TakesApps => true;

    /// <summary><see cref="WorkspaceSettings.Resolve"/>, with any words it hands back counted as apps.</summary>
    public void ResolveApps(CliContext cli) => Apps = [.. Resolve(cli), .. Apps];
}

[Description("Start a workspace's apps and jobs in the background sprig host, in dependency order, and wait until each is ready")]
public sealed class StartCommand(CliContext cli) : Command<StartCommand.Settings>
{
    public sealed class Settings : AppsSettings
    {
        [CommandOption("--group <name>")]
        [Description("Start one of the groups the definitions name (and what its apps depend on)")]
        public string? Group { get; set; }

        [CommandOption("--no-wait")]
        [Description("Return once the apps are starting, without waiting for them to be ready")]
        public bool NoWait { get; set; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        s.ResolveApps(cli);
        var record = cli.Workspaces.Get(s.Orchestration, s.Workspace) ?? throw new InvalidOperationException($"{s.Orchestration} has no workspace named '{s.Workspace}'");
        var plan = cli.Workspaces.LastPlan(s.Orchestration, s.Workspace) ?? throw new InvalidOperationException($"{s.Orchestration}/{s.Workspace} hasn't come up yet; run sprig up first");
        if (record.IsFree)
            throw new InvalidOperationException($"{record.SlotName} is free; check a branch out in it first: sprig up {record.Orchestration} -b <branch>");
        if (record.State != Sprig.Engine.Workspaces.WorkspaceState.Up)
            throw new InvalidOperationException($"{record.SlotName} is {record.State.ToString().ToLowerInvariant()}; bring its infra up first: {UpCommand.UpLine(record)}");

        IReadOnlyCollection<string>? names = s.Apps.Length > 0 ? s.Apps : null;
        if (s.Group is { } group)
        {
            if (names is not null) throw new InvalidOperationException("name apps or a group, not both");
            names = RunnerSpecs.Group(plan, group);
        }
        var specs = RunnerSpecs.For(record, plan, names);
        // Apps named outright start afresh; everything else, including what they depend on, is left alone if it's going.
        var restart = s.Apps.Length > 0 ? RunnerSpecs.Resolve(record, plan, s.Apps) : [];

        // A workspace brought up for one group has only that group's infra running; the next starts what it adds.
        if (cli.Workspaces.MissingInfra(record, plan, names) is { Count: > 0 } missing)
        {
            if (!s.Json) cli.Ansi.MarkupLine($"[blue]…[/] Starting what they use: {Markup.Escape(string.Join(", ", missing.Select(t => t.Key)))}");
            cli.Workspaces.StartInfra(missing, new SyncProgress<Sprig.Engine.Workspaces.WorkspaceEvent>(e => DownCommand.Echo(cli, e, s.Json)), cancellation);
        }

        Send(cli, specs, restart);
        if (s.NoWait)
            return CliOutput.Ok(s.Json, $"starting {specs.Count} apps", new { ok = true, apps = specs.Select(x => x.Name) });

        var mine = WaitUntilReady(cli, record.Key, specs, s.Json, cancellation);
        var failed = mine.Where(r => !r.State.Satisfies()).ToArray();
        if (s.Json) CliOutput.Json(new { ok = failed.Length == 0, runners = mine });
        return failed.Length == 0 ? 0 : 1;
    }

    internal static void Send(CliContext cli, IReadOnlyList<RunnerSpec> specs, IReadOnlyList<string> restart)
    {
        var answer = cli.Host.Send(new HostRequest("start", specs, Restart: restart));
        if (!answer.Ok) throw new InvalidOperationException(answer.Error ?? "the host wouldn't start the apps");
    }

    /// <summary>
    /// Until each of <paramref name="specs"/> is ready (or, for a job, finished) or has settled some other way, printing
    /// each change of state, and where to read the log of each that didn't make it. Gives up after five minutes.
    /// </summary>
    internal static IReadOnlyList<RunnerStatus> WaitUntilReady(CliContext cli, string workspace, IReadOnlyList<RunnerSpec> specs, bool json,
        CancellationToken cancellation)
    {
        var watched = specs.Select(x => x.Name).ToHashSet();
        var seen = new Dictionary<string, RunnerState>();
        var clock = Stopwatch.StartNew();
        IReadOnlyList<RunnerStatus> mine = [];
        while (!cancellation.IsCancellationRequested)
        {
            mine = (cli.Host.Send(new HostRequest("list")).Runners ?? []).Where(r => r.Workspace == workspace && watched.Contains(r.Name)).ToArray();
            foreach (var r in mine.Where(r => !seen.TryGetValue(r.Name, out var was) || was != r.State))
            {
                seen[r.Name] = r.State;
                if (!json) Report(cli, r);
            }
            if (mine.All(r => r.State.Satisfies() || r.State.IsSettled())) break;
            if (clock.Elapsed > TimeSpan.FromMinutes(5)) break;
            Thread.Sleep(300);
        }
        if (!json)
            foreach (var r in mine.Where(r => !r.State.Satisfies())) cli.Ansi.MarkupLine($"[dim]  log: {Markup.Escape(r.LogFile)}[/]");
        return mine;
    }

    internal static void Report(CliContext cli, RunnerStatus r)
    {
        var (colour, text) = r.State switch
        {
            RunnerState.Building => ("blue", r.Doing is { } doing ? $"building · {doing}" : "building"),
            RunnerState.Waiting => ("blue", "waiting for what it depends on"),
            RunnerState.Starting => ("blue", r.Error is { } why ? $"starting · {why}" : "starting"),
            RunnerState.Running => ("blue", "running"),
            RunnerState.Completed => ("green", "finished"),
            RunnerState.Ready => ("green", r.Port is { } p ? $"ready on http://localhost:{p}" : "running"),
            RunnerState.Exited => ("red", r.Error ?? $"exited with code {r.Exit}"),
            RunnerState.Restarting => ("yellow", r.Error ?? "restarting"),
            RunnerState.Failed => ("red", r.Error ?? "failed"),
            _ => ("dim", r.State.ToString().ToLowerInvariant()),
        };
        cli.Ansi.MarkupLine($"[{colour}]{Markup.Escape(r.Name)}[/]  {Markup.Escape(text)}");
    }
}

[Description("Restart a workspace's running apps, each built again and after what it depends on. Stopped apps stay stopped")]
public sealed class RestartCommand(CliContext cli) : Command<RestartCommand.Settings>
{
    public sealed class Settings : AppsSettings
    {
        [CommandOption("--group <name>")]
        [Description("Only the running apps of this group")]
        public string? Group { get; set; }

        [CommandOption("--jobs")]
        [Description("Run the jobs they depend on (migrations, say) again first, rather than leaving them finished")]
        public bool Jobs { get; set; }

        [CommandOption("--no-wait")]
        [Description("Return once the apps are restarting, without waiting for them to be ready")]
        public bool NoWait { get; set; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        s.ResolveApps(cli);
        var record = cli.Workspaces.Get(s.Orchestration, s.Workspace) ?? throw new InvalidOperationException($"{s.Orchestration} has no workspace named '{s.Workspace}'");
        var plan = cli.Workspaces.LastPlan(s.Orchestration, s.Workspace) ?? throw new InvalidOperationException($"{s.Orchestration}/{s.Workspace} hasn't come up yet; run sprig up first");
        if (s.Group is not null && s.Apps.Length > 0) throw new InvalidOperationException("name apps or a group, not both");
        IReadOnlyCollection<string>? within = s.Group is { } group ? RunnerSpecs.Group(plan, group) : s.Apps.Length > 0 ? s.Apps : null;

        var runners = cli.Host.TrySend(new HostRequest("list"))?.Runners ?? [];
        var (specs, restart) = RunnerSpecs.RestartRunning(record, plan, runners, within, s.Jobs);
        if (specs.Count == 0)
            return CliOutput.Ok(s.Json, "nothing is running to restart; start the apps with sprig start", new { ok = true, restarted = Array.Empty<string>() });

        StartCommand.Send(cli, specs, restart);
        if (s.NoWait)
            return CliOutput.Ok(s.Json, $"restarting {restart.Count} apps", new { ok = true, restarted = restart });

        var mine = StartCommand.WaitUntilReady(cli, record.Key, specs, s.Json, cancellation);
        var failed = mine.Where(r => !r.State.Satisfies()).ToArray();
        if (s.Json) CliOutput.Json(new { ok = failed.Length == 0, restarted = restart, runners = mine });
        return failed.Length == 0 ? 0 : 1;
    }
}

[Description("Stop a workspace's apps (its infra keeps running; sprig down stops that)")]
public sealed class StopCommand(CliContext cli) : Command<AppsSettings>
{
    protected override int Execute(CommandContext context, AppsSettings s, CancellationToken cancellation)
    {
        s.ResolveApps(cli);
        var key = Sprig.Engine.Workspaces.WorkspaceRecord.KeyOf(s.Orchestration, s.Workspace);
        if (cli.Host.TrySend(new HostRequest("list")) is not { } list)
            return CliOutput.Ok(s.Json, "nothing is running", new { ok = true, stopped = Array.Empty<string>() });

        var targets = (list.Runners ?? [])
            .Where(r => r.Workspace == key && (r.State.IsAlive() || r.State == RunnerState.Stopping))
            .Where(r => s.Apps.Length == 0 || s.Apps.Any(a => r.Name == a || r.Name.EndsWith("/" + a, StringComparison.Ordinal)))
            .Select(r => r.Name).ToArray();
        if (targets.Length == 0) return CliOutput.Ok(s.Json, "nothing to stop", new { ok = true, stopped = Array.Empty<string>() });

        // In parallel, so a slow one doesn't hold up the rest.
        var results = targets.AsParallel().SelectMany(name => cli.Host.StopAndWait(key, name)).ToArray();
        if (s.Json) { CliOutput.Json(new { ok = true, stopped = results }); return 0; }
        foreach (var r in results.OrderBy(r => r.Name))
        {
            if (r.Error is { } why) cli.Ansi.MarkupLine($"[yellow]{Markup.Escape(r.Name)}[/]  {Markup.Escape(why)}");
            else CliOutput.Success($"{r.Name} stopped");
        }
        return 0;
    }
}

[Description("List the apps the sprig host is running")]
public sealed class PsCommand(CliContext cli) : Command<GlobalSettings>
{
    protected override int Execute(CommandContext context, GlobalSettings s, CancellationToken cancellation)
    {
        var runners = cli.Host.TrySend(new HostRequest("list"))?.Runners ?? [];
        if (s.Json) { CliOutput.Json(runners); return 0; }
        if (runners.Count == 0)
        {
            cli.Ansi.MarkupLine("[dim]Nothing is running. Start a workspace's apps with: sprig start [[id]] [[workspace]][/]");
            return 0;
        }
        var table = new Table().Border(TableBorder.Rounded).AddColumns("Workspace", "App", "State", "Port", "Pid", "CPU", "Memory", "Since");
        table.Columns[5].RightAligned();
        table.Columns[6].RightAligned();
        // Runners are keyed "storefront/2"; people know the slot by its folder and branch.
        var records = cli.Workspaces.List().ToDictionary(w => w.Key);
        foreach (var r in runners)
            table.AddRow(Markup.Escape(records.TryGetValue(r.Workspace, out var w) ? $"{w.SlotName} ({w.Label})" : r.Workspace), Markup.Escape(r.Name), State(r),
                r.Port?.ToString() ?? "", r.Pid?.ToString() ?? "",
                r.Usage is { } u ? ResourceUsage.CpuText(u.Cpu) : "", r.Usage is { } m ? ResourceUsage.MemoryText(m.Memory) : "",
                r.Since.LocalDateTime.ToString("T"));
        cli.Ansi.Write(table);
        if (ResourceUsage.Sum(runners.Select(r => r.Usage)) is { } total)
            cli.Ansi.MarkupLine($"[dim]All apps: {Markup.Escape(total.Text)} (CPU as a share of the whole machine; docker stats has the infra's)[/]");
        return 0;
    }

    internal static string State(RunnerStatus r) => r.State switch
    {
        RunnerState.Ready => "[green]ready[/]",
        RunnerState.Completed => "[green]finished[/]",
        RunnerState.Building when r.Doing is { } doing => $"building [dim]{Markup.Escape(doing)}[/]",
        RunnerState.Starting when r.Error is { } why => $"starting [yellow]{Markup.Escape(why)}[/]",
        RunnerState.Stopped when r.Error is { } note => $"stopped [yellow]{Markup.Escape(note)}[/]",
        RunnerState.Restarting => $"[yellow]restarting[/] [dim]{Markup.Escape(r.Error ?? "")}[/]",
        RunnerState.Exited or RunnerState.Failed => $"[red]{r.State.ToString().ToLowerInvariant()}[/] [dim]{Markup.Escape(r.Error ?? $"code {r.Exit}")}[/]",
        _ => r.State.ToString().ToLowerInvariant(),
    } + (r.Restarts > 0 && r.State != RunnerState.Restarting ? $" [dim](restarted {r.Restarts}×)[/]" : "");
}

[Description("Print the end of an app's log")]
public sealed class LogsCommand(CliContext cli) : Command<LogsCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandArgument(2, "[app]")]
        [Description("The app, as <name> or <repo>/<name>")]
        public string App { get; set; } = "";

        protected override bool TakesApps => true;

        [CommandOption("-n|--lines <count>")]
        public int Lines { get; set; } = 40;

        [CommandOption("-t|--timestamps")]
        [Description("Lead each line with the time the host wrote it")]
        public bool Timestamps { get; set; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        s.App = s.Resolve(cli).FirstOrDefault() ?? s.App;
        if (s.App.Length == 0) throw new InvalidOperationException("name the app: sprig logs [id] [workspace] <app>");
        var key = Sprig.Engine.Workspaces.WorkspaceRecord.KeyOf(s.Orchestration, s.Workspace);
        var runner = (cli.Host.TrySend(new HostRequest("list"))?.Runners ?? [])
            .FirstOrDefault(r => r.Workspace == key &&(r.Name == s.App || r.Name.EndsWith("/" + s.App, StringComparison.Ordinal)));
        var file = runner?.LogFile;
        if (file is null)
        {
            var plan = cli.Workspaces.LastPlan(s.Orchestration, s.Workspace);
            var app = plan?.Apps.FirstOrDefault(a => a.Name == s.App || $"{a.Owner}/{a.Name}" == s.App)
                ?? throw new InvalidOperationException($"no app '{s.App}' in {key}");
            file = HostProtocol.LogFile(cli.Paths, key, $"{app.Owner}/{app.Name}");
        }
        if (!File.Exists(file)) throw new InvalidOperationException($"{s.App} has no log yet");
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var parser = new LogLineParser();
        var text = reader.ReadToEnd();
        var entries = text.Split('\n').Select(l => l.TrimEnd('\r')).SkipLast(text.EndsWith('\n') ? 1 : 0)
            .Select(parser.Parse).OfType<LogEntry>().TakeLast(s.Lines);
        foreach (var entry in entries)
            Console.WriteLine(s.Timestamps && entry.Time is { } time ? $"{time.LocalDateTime:HH:mm:ss.fff} {entry.Text}" : entry.Text);
        return 0;
    }
}

[Description("Show whether this store's sprig host is running")]
public sealed class HostStatusCommand(CliContext cli) : Command<GlobalSettings>
{
    protected override int Execute(CommandContext context, GlobalSettings s, CancellationToken cancellation)
    {
        var answer = cli.Host.TrySend(new HostRequest("ping"));
        if (answer is null) return CliOutput.Ok(s.Json, "the sprig host isn't running; it starts when something needs it", new { ok = true, running = false });
        var alive = answer.Runners?.Count(r => r.State.IsAlive()) ?? 0;
        return CliOutput.Ok(s.Json, $"the sprig host is running (pid {answer.Pid}, v{answer.Version}) with {alive} apps",
            new { ok = true, running = true, pid = answer.Pid, version = answer.Version, apps = alive });
    }
}

[Description("Stop this store's sprig host, and every app it runs")]
public sealed class HostStopCommand(CliContext cli) : Command<GlobalSettings>
{
    protected override int Execute(CommandContext context, GlobalSettings s, CancellationToken cancellation)
    {
        var answer = cli.Host.TrySend(new HostRequest("shutdown"));
        return CliOutput.Ok(s.Json, answer is null ? "the sprig host wasn't running" : "the sprig host is stopping", new { ok = true, wasRunning = answer is not null });
    }
}
