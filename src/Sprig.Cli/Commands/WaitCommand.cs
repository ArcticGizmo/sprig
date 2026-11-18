using System.ComponentModel;
using Sprig.Engine.Hosting;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Sprig.Cli.Commands;

[Description("Wait until a workspace's apps are ready (or, for jobs, finished), as after starting them with --no-wait. With nothing named, every app it has going")]
public sealed class WaitCommand(CliContext cli) : Command<WaitCommand.Settings>
{
    /// <summary>A named app the host isn't running, or with nothing named, a workspace running nothing.</summary>
    public const string NotRunning = "not-running";

    public sealed class Settings : AppsSettings
    {
        [CommandOption("--group <name>")]
        [Description("Wait for the apps of one of the groups the definitions name")]
        public string? Group { get; set; }

        [CommandOption("--timeout <seconds>")]
        [Description("How long to wait before giving up (default: 300, as start waits)")]
        public int Timeout { get; set; } = (int)StartCommand.WaitLimit.TotalSeconds;
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        s.ResolveApps(cli);
        var record = cli.Workspaces.Get(s.Orchestration, s.Workspace)
            ?? throw new InvalidOperationException($"{s.Orchestration} has no workspace named '{s.Workspace}'");
        var plan = cli.Workspaces.LastPlan(record.Orchestration, record.Name)
            ?? throw new InvalidOperationException($"{record.SlotName} hasn't been made ready, so nothing of it is running; start it with: sprig start {record.Orchestration} {record.Name}");
        var (apps, infra) = AppsSettings.Split(record, plan, s.Apps);
        if (infra.Count > 0) throw new InvalidOperationException($"wait is for apps; {string.Join(", ", infra)} is infra, which is up once start returns");
        if (s.Group is not null && apps.Count > 0) throw new InvalidOperationException("name apps or a group, not both");
        if (s.Timeout <= 0) throw new InvalidOperationException("--timeout is a number of seconds above 0");

        IReadOnlyCollection<string>? names = s.Group is { } group ? RunnerSpecs.Group(plan, group) : apps.Count > 0 ? apps : null;
        // Nothing named: what the workspace has going now, whatever state it's in, but not what was stopped on purpose.
        var watched = (names is null
            ? (cli.Host.TrySend(new HostRequest("list"))?.Runners ?? []).Where(r => r.Workspace == record.Key && r.State != RunnerState.Stopped).Select(r => r.Name)
            : RunnerSpecs.Resolve(record, plan, names)).ToHashSet(StringComparer.Ordinal);

        var mine = watched.Count == 0 ? [] : StartCommand.WaitUntilReady(cli, record.Key, watched, s.Json, TimeSpan.FromSeconds(s.Timeout), cancellation);
        var missing = watched.Where(n => mine.All(r => r.Name != n)).Order(StringComparer.Ordinal).ToArray();
        if (watched.Count == 0 || missing.Length > 0)
        {
            var error = watched.Count == 0
                ? $"nothing of {record.SlotName} is running; start it with: sprig start {record.Orchestration} {record.Name}"
                : $"{string.Join(", ", missing)} {(missing.Length == 1 ? "isn't" : "aren't")} running; start {(missing.Length == 1 ? "it" : "them")} with: sprig start {record.Orchestration} {record.Name} {string.Join(' ', missing)}";
            if (s.Json) CliOutput.Json(new { ok = false, code = NotRunning, error, notRunning = missing, runners = mine });
            else Console.Error.WriteLine($"error: {error}");
            return 1;
        }

        var failure = StartFailure.Of(plan, mine);
        if (s.Json) CliOutput.Json(new { ok = failure is null, code = failure?.Code, error = failure?.Error, runners = mine, failed = failure?.Apps });
        else if (failure is null) CliOutput.Success(mine.Count == 1 ? $"{mine[0].Name} is ready" : $"all {mine.Count} are ready");
        else cli.Ansi.MarkupLine($"[red]{Markup.Escape(failure.Error)}[/]");
        return failure is null ? 0 : 1;
    }
}
