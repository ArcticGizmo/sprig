using System.ComponentModel;
using System.Diagnostics;
using Sprig.Engine.Hosting;
using Sprig.Engine.Planning;
using Sprig.Engine.Workspaces;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Sprig.Cli.Commands;

/// <summary>Settings for commands that act on some or all of what a workspace runs: its apps and its infra.</summary>
public class AppsSettings : WorkspaceSettings
{
    [CommandArgument(2, "[names]")]
    [Description("Only these apps or infra, as <name> or <repo>/<name> (default: everything)")]
    public string[] Apps { get; set; } = [];

    protected override bool TakesApps => true;

    /// <summary><see cref="WorkspaceSettings.Resolve"/>, with any words it hands back counted as names.</summary>
    public void ResolveApps(CliContext cli) => Apps = [.. Resolve(cli), .. Apps];

    /// <summary>The names sorted into apps (as given, for <see cref="RunnerSpecs.Resolve"/>) and infra (by key,
    /// <c>owner/name</c>). One that's neither, or could be more than one piece of infra, is refused.</summary>
    public static (IReadOnlyList<string> Apps, IReadOnlyList<string> Infra) Split(WorkspaceRecord record, Plan plan, IEnumerable<string> names)
    {
        var infraKeys = plan.Containers.Select(c => $"{c.Owner}/{c.Name}")
            .Concat(plan.Compose.SelectMany(f => f.Services.Select(x => $"{f.Owner}/{x.Name}"))).ToArray();
        var (apps, infra) = (new List<string>(), new List<string>());
        foreach (var name in names)
        {
            if (plan.Apps.Any(a => a.Key == name || a.Name == name))
            {
                apps.Add(name);
                continue;
            }
            var matches = infraKeys.Where(k => k == name || k[(k.IndexOf('/') + 1)..] == name).ToArray();
            infra.Add(matches.Length switch
            {
                1 => matches[0],
                0 => throw new InvalidOperationException($"{record.Orchestration} has no app or infra '{name}'; it has "
                                                         + string.Join(", ", plan.Apps.Select(a => a.Key).Concat(infraKeys))),
                _ => throw new InvalidOperationException($"'{name}' is in more than one repo; name it as <repo>/{name}"),
            });
        }
        return (apps, infra);
    }
}

[Description("Start what a workspace runs, each after what it depends on (its infra included), and wait until each is ready. With nothing named, everything")]
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

        [CommandOption("--trust")]
        [Description("When the workspace has to be made ready first, approve any definition that isn't approved yet, without asking")]
        public bool Trust { get; set; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        s.ResolveApps(cli);
        var record = cli.Workspaces.Get(s.Orchestration, s.Workspace);
        if (record is null && s.Workspace != WorkspaceRecord.MainName)
            throw new InvalidOperationException($"{s.Orchestration} has no workspace named '{s.Workspace}'; check a branch out with: sprig up {s.Orchestration} -b <branch>");
        if (record is { IsFree: true })
            throw new InvalidOperationException($"{record.SlotName} is free; check a branch out in it first: sprig up {record.Orchestration} -b <branch>");
        // Stopped, failed or never made ready: made ready now, starting nothing yet, so what's named can be looked up.
        if (record is not { State: WorkspaceState.Up } || cli.Workspaces.LastPlan(s.Orchestration, s.Workspace) is null)
        {
            if (Prepare(s, cancellation) is not { } ready) return 1;
            record = ready;
        }
        var plan = cli.Workspaces.LastPlan(record.Orchestration, record.Name)!;

        var (apps, infra) = AppsSettings.Split(record, plan, s.Apps);
        IReadOnlyCollection<string>? names = s.Apps.Length > 0 ? apps : null;
        if (s.Group is { } group)
        {
            if (names is not null) throw new InvalidOperationException("name apps or a group, not both");
            names = RunnerSpecs.Group(plan, group);
        }
        if (record.IsMain && infra.Count > 0)
            throw new InvalidOperationException($"the main checkout runs with your own infra, so sprig doesn't start {string.Join(", ", infra)}; start it the way you usually do");
        var specs = names is { Count: 0 } ? [] : RunnerSpecs.For(record, plan, names);
        // Apps named outright start afresh; everything else, including what they depend on, is left alone if it's going.
        var restart = apps.Count > 0 ? RunnerSpecs.Resolve(record, plan, apps) : [];

        // What the apps use that isn't running, and what was named.
        var missing = cli.Workspaces.MissingInfra(record, plan, names).ToList();
        if (infra.Count > 0)
        {
            var states = cli.Workspaces.InfraStates(cli.Workspaces.InfraTargets(record, plan));
            missing.AddRange(cli.Workspaces.InfraTargets(record, plan)
                .Where(t => infra.Contains(t.Key) && missing.All(m => m.Key != t.Key) && states?.GetValueOrDefault(t.Key) is not { State: "running" }));
        }
        if (missing.Count > 0)
        {
            if (!s.Json) cli.Ansi.MarkupLine($"[blue]…[/] Starting infra: {Markup.Escape(string.Join(", ", missing.Select(t => t.Key)))}");
            cli.Workspaces.StartInfra(missing, new SyncProgress<WorkspaceEvent>(e => DownCommand.Echo(cli, e, s.Json)), cancellation);
        }
        if (specs.Count == 0)
            return CliOutput.Ok(s.Json, missing.Count == 0 ? "already running" : $"started {string.Join(", ", missing.Select(t => t.Key))}",
                new { ok = true, infra = missing.Select(t => t.Key) });

        Send(cli, specs, restart);
        if (s.NoWait)
            return CliOutput.Ok(s.Json, $"starting {specs.Count} apps", new { ok = true, apps = specs.Select(x => x.Name) });

        var mine = WaitUntilReady(cli, record.Key, specs, s.Json, cancellation);
        var failed = mine.Where(r => !r.State.Satisfies()).ToArray();
        if (s.Json) CliOutput.Json(new { ok = failed.Length == 0, runners = mine });
        return failed.Length == 0 ? 0 : 1;
    }

    /// <summary>Make the workspace ready (worktrees, ports, files, its infra's definitions), starting nothing. Null,
    /// after saying why, when it can't be.</summary>
    WorkspaceRecord? Prepare(Settings s, CancellationToken cancellation)
    {
        var trusting = new TrustingSettings { Json = s.Json, UnmaskSecrets = s.UnmaskSecrets, Trust = s.Trust };
        if (OrchestrationLoad.Load(cli, s.Orchestration, trusting, s.Workspace) is not { } loaded) return null;
        try
        {
            return cli.Workspaces.Up(loaded, s.Workspace, null, progress: new SyncProgress<WorkspaceEvent>(e => DownCommand.Echo(cli, e, s.Json)),
                cancellation: cancellation);
        }
        catch (WorkspaceException e)
        {
            Console.Error.WriteLine($"error: {e.Message}" + (e.HeldBy is not null ? $"; take the checkout over with: sprig up {s.Orchestration} --take-over" : ""));
            return null;
        }
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
        var plan = cli.Workspaces.LastPlan(s.Orchestration, s.Workspace) ?? throw new InvalidOperationException($"{s.Orchestration}/{s.Workspace} has never started, so there's nothing to restart; start it with: sprig start");
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

[Description("Stop what a workspace runs: the apps or infra named, just those (what uses them is left running), or with nothing named, everything, keeping its containers and data")]
public sealed class StopCommand(CliContext cli) : Command<AppsSettings>
{
    protected override int Execute(CommandContext context, AppsSettings s, CancellationToken cancellation)
    {
        s.ResolveApps(cli);
        if (s.Apps.Length == 0) return DownCommand.StopAll(cli, s, cancellation);

        var key = WorkspaceRecord.KeyOf(s.Orchestration, s.Workspace);
        var record = cli.Workspaces.Get(s.Orchestration, s.Workspace) ?? throw new InvalidOperationException($"{s.Orchestration} has no workspace named '{s.Workspace}'");
        var plan = cli.Workspaces.LastPlan(record.Orchestration, record.Name)
                   ?? throw new InvalidOperationException($"{record.SlotName} has never been made ready, so nothing of it is running");
        var (apps, infra) = AppsSettings.Split(record, plan, s.Apps);
        if (record.IsMain && infra.Count > 0)
            throw new InvalidOperationException($"the main checkout runs with your own infra, so sprig doesn't stop {string.Join(", ", infra)}");
        foreach (var target in cli.Workspaces.InfraTargets(record, plan).Where(t => infra.Contains(t.Key)))
        {
            cli.Workspaces.StopContainer(target);
            if (!s.Json) CliOutput.Success($"{target.Key} stopped; its data is kept");
        }
        if (apps.Count == 0)
        {
            if (s.Json) CliOutput.Json(new { ok = true, stopped = infra });
            return 0;
        }

        var list = cli.Host.TrySend(new HostRequest("list"));
        var targets = (list?.Runners ?? [])
            .Where(r => r.Workspace == key && (r.State.IsAlive() || r.State == RunnerState.Stopping))
            .Where(r => apps.Any(a => r.Name == a || r.Name.EndsWith("/" + a, StringComparison.Ordinal)))
            .Select(r => r.Name).ToArray();
        if (targets.Length == 0) return CliOutput.Ok(s.Json, infra.Count > 0 ? "no app named was running" : "nothing to stop", new { ok = true, stopped = infra });

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
