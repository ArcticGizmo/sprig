using System.ComponentModel;
using System.Diagnostics;
using Sprig.Engine.Hosting;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Sprig.Cli.Commands;

/// <summary>Settings for commands that act on some or all of a sprout's apps.</summary>
public class AppsSettings : SproutSettings
{
    [CommandArgument(2, "[apps]")]
    [Description("Only these apps, as <name> or <repo>/<name> (default: all of them)")]
    public string[] Apps { get; set; } = [];
}

[Description("Start a sprout's apps and jobs in the background sprig host, in dependency order, and wait until each is ready")]
public sealed class StartCommand(CliContext cli) : Command<StartCommand.Settings>
{
    public sealed class Settings : AppsSettings
    {
        [CommandOption("--group <name>")]
        [Description("Start one of the trellis's groups (and what its apps depend on)")]
        public string? Group { get; set; }

        [CommandOption("--no-wait")]
        [Description("Return once the apps are starting, without waiting for them to be ready")]
        public bool NoWait { get; set; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        var record = cli.Sprouts.Get(s.Trellis, s.Sprout) ?? throw new InvalidOperationException($"{s.Trellis} has no sprout named '{s.Sprout}'");
        var plan = cli.Sprouts.LastPlan(s.Trellis, s.Sprout) ?? throw new InvalidOperationException($"{s.Trellis}/{s.Sprout} hasn't come up yet; run sprig up first");
        if (record.State != Sprig.Engine.Sprouts.SproutState.Up)
            throw new InvalidOperationException($"{record.Key} is {record.State.ToString().ToLowerInvariant()}; bring its infra up first: sprig up {s.Trellis} --sprout {s.Sprout}");

        IReadOnlyCollection<string>? names = s.Apps.Length > 0 ? s.Apps : null;
        if (s.Group is { } group)
        {
            if (names is not null) throw new InvalidOperationException("name apps or a group, not both");
            names = plan.Groups.FirstOrDefault(g => g.Name == group)?.Apps
                ?? throw new InvalidOperationException($"{s.Trellis} has no group '{group}'; it has {string.Join(", ", plan.Groups.Select(g => g.Name))}");
        }
        var specs = RunnerSpecs.For(record, plan, names);
        // Apps named outright start afresh; everything else, including what they depend on, is left alone if it's going.
        var restart = s.Apps.Length > 0 ? RunnerSpecs.Resolve(record, plan, s.Apps) : [];

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
    internal static IReadOnlyList<RunnerStatus> WaitUntilReady(CliContext cli, string sprout, IReadOnlyList<RunnerSpec> specs, bool json,
        CancellationToken cancellation)
    {
        var watched = specs.Select(x => x.Name).ToHashSet();
        var seen = new Dictionary<string, RunnerState>();
        var clock = Stopwatch.StartNew();
        IReadOnlyList<RunnerStatus> mine = [];
        while (!cancellation.IsCancellationRequested)
        {
            mine = (cli.Host.Send(new HostRequest("list")).Runners ?? []).Where(r => r.Sprout == sprout && watched.Contains(r.Name)).ToArray();
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
            RunnerState.Building => ("blue", "building"),
            RunnerState.Waiting => ("blue", "waiting for what it depends on"),
            RunnerState.Starting => ("blue", "starting"),
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

[Description("Stop a sprout's apps (its infra keeps running; sprig down stops that)")]
public sealed class StopCommand(CliContext cli) : Command<AppsSettings>
{
    protected override int Execute(CommandContext context, AppsSettings s, CancellationToken cancellation)
    {
        var key = Sprig.Engine.Sprouts.SproutRecord.KeyOf(s.Trellis, s.Sprout);
        if (cli.Host.TrySend(new HostRequest("list")) is not { } list)
            return CliOutput.Ok(s.Json, "nothing is running", new { ok = true, stopped = Array.Empty<string>() });

        var targets = (list.Runners ?? [])
            .Where(r => r.Sprout == key && (r.State.IsAlive() || r.State == RunnerState.Stopping))
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
            cli.Ansi.MarkupLine("[dim]Nothing is running. Start a sprout's apps with: sprig start <trellis> <sprout>[/]");
            return 0;
        }
        var table = new Table().Border(TableBorder.Rounded).AddColumns("Sprout", "App", "State", "Port", "Pid", "Since");
        foreach (var r in runners)
            table.AddRow(Markup.Escape(r.Sprout), Markup.Escape(r.Name), r.State switch
                {
                    RunnerState.Ready => "[green]ready[/]",
                    RunnerState.Completed => "[green]finished[/]",
                    RunnerState.Stopped when r.Error is { } note => $"stopped [yellow]{Markup.Escape(note)}[/]",
                    RunnerState.Restarting => $"[yellow]restarting[/] [dim]{Markup.Escape(r.Error ?? "")}[/]",
                    RunnerState.Exited or RunnerState.Failed => $"[red]{r.State.ToString().ToLowerInvariant()}[/] [dim]{Markup.Escape(r.Error ?? $"code {r.Exit}")}[/]",
                    _ => r.State.ToString().ToLowerInvariant(),
                } + (r.Restarts > 0 && r.State != RunnerState.Restarting ? $" [dim](restarted {r.Restarts}×)[/]" : ""),
                r.Port?.ToString() ?? "", r.Pid?.ToString() ?? "", r.Since.LocalDateTime.ToString("T"));
        cli.Ansi.Write(table);
        return 0;
    }
}

[Description("Print the end of an app's log")]
public sealed class LogsCommand(CliContext cli) : Command<LogsCommand.Settings>
{
    public sealed class Settings : SproutSettings
    {
        [CommandArgument(2, "<app>")]
        public string App { get; set; } = "";

        [CommandOption("-n|--lines <count>")]
        public int Lines { get; set; } = 40;

        [CommandOption("-t|--timestamps")]
        [Description("Lead each line with the time the host wrote it")]
        public bool Timestamps { get; set; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        var key = Sprig.Engine.Sprouts.SproutRecord.KeyOf(s.Trellis, s.Sprout);
        var runner = (cli.Host.TrySend(new HostRequest("list"))?.Runners ?? [])
            .FirstOrDefault(r => r.Sprout == key && (r.Name == s.App || r.Name.EndsWith("/" + s.App, StringComparison.Ordinal)));
        var file = runner?.LogFile;
        if (file is null)
        {
            var plan = cli.Sprouts.LastPlan(s.Trellis, s.Sprout);
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
