using System.ComponentModel;
using Sprig.Engine.Hosting;
using Sprig.Engine.Planning;
using Sprig.Engine.Sprouts;
using Sprig.Engine.Trellises;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Sprig.Cli.Commands;

/// <summary>Settings for commands that name one sprout of a trellis.</summary>
public class SproutSettings : GlobalSettings
{
    [CommandArgument(0, "<trellis>")]
    [Description("A registered trellis name")]
    public string Trellis { get; set; } = "";

    [CommandArgument(1, "<sprout>")]
    [Description("The sprout's name")]
    public string Sprout { get; set; } = "";
}

[Description("Bring a sprout of a trellis up: worktrees, ports, env files and infra, in one step")]
public sealed class UpCommand(CliContext cli) : Command<UpCommand.Settings>
{
    public sealed class Settings : TrustingSettings
    {
        [CommandArgument(0, "<trellis>")]
        [Description("A registered trellis name, or a trellis file")]
        public string Trellis { get; set; } = "";

        [CommandOption("--sprout <name>")]
        [Description("The sprout's name (default: the branch name, or 'local' when the repos run in place)")]
        public string? Sprout { get; set; }

        [CommandOption("--branch <branch>")]
        [Description("The branch for the sprout's worktrees (default: the sprout's name). Made from the trellis's base branch when it doesn't exist")]
        public string? Branch { get; set; }

        [CommandOption("--run")]
        [Description("Then start every app and follow their logs together. Ctrl+C stops the apps; the infra keeps running")]
        public bool Run { get; set; }

        [CommandOption("-d|--detached")]
        [Description("Then start every app, wait until they're ready, and leave them running in the background")]
        public bool Detached { get; set; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        if (s.Json && s.Run && !s.Detached)
            throw new InvalidOperationException("--run follows the apps' logs, which isn't JSON; use --detached with --json");
        var named = s.Sprout ?? (s.Branch is { } b ? SproutService.NameFromBranch(b) : null);
        if (TrellisLoad.Load(cli, s.Trellis, s, named) is not { } loaded) return 1;
        var trellis = loaded.Trellis!;
        var root = Path.GetDirectoryName(loaded.TrellisPath)!;

        var sprout = named
            ?? (trellis.Worktrees is null ? "local" : throw new InvalidOperationException(
                $"{trellis.Name} gives each sprout its own worktrees; name it: sprig up {trellis.Name} --sprout <name>"));

        var progress = new SyncProgress<SproutEvent>(e => Report(e, s.Json));
        try
        {
            var record = UpApproving(loaded, sprout, s, progress, cancellation);
            if (record is null) return 1;
            var plan = cli.Sprouts.LastPlan(record.Trellis, record.Name)!;
            var runApps = s.Run || s.Detached;
            if (!runApps)
            {
                if (s.Json) CliOutput.Json(new { ok = true, sprout = record, plan });
                else Summary(record, plan, root, appsStart: false);
                return 0;
            }

            if (!s.Json) Summary(record, plan, root, appsStart: true);
            var specs = RunnerSpecs.For(record, plan);
            if (specs.Count == 0)
            {
                if (s.Json) CliOutput.Json(new { ok = true, sprout = record, plan, runners = Array.Empty<RunnerStatus>() });
                else cli.Ansi.MarkupLine("[dim]The trellis has no apps to run.[/]");
                return 0;
            }
            return s.Detached ? StartDetached(record, plan, specs, s.Json, cancellation) : new AppFollow(cli, record, specs).Run();
        }
        catch (SproutException e)
        {
            if (s.Json) CliOutput.Json(new { ok = false, error = e.Message, problems = e.Problems });
            else
            {
                Console.Error.WriteLine($"error: {e.Message}");
                if (e.Problems.Count > 0) PlanPrinter.Problems(cli.Ansi, e.Problems, root);
            }
            return 1;
        }
    }

    /// <summary>Up, asking to approve the definitions a new sprout's worktrees bring (its branch can change them) and
    /// going again. Null when they weren't approved.</summary>
    SproutRecord? UpApproving(LoadedTrellis loaded, string sprout, Settings s, IProgress<SproutEvent> progress,
        CancellationToken cancellation)
    {
        var runApps = s.Run || s.Detached;
        for (var attempt = 1; ; attempt++)
        {
            string? held = null;
            try
            {
                return cli.Sprouts.Up(loaded, sprout, s.Branch, progress, cancellation,
                    runApps ? (record, plan) => held = Prebuild(record, plan) : null);
            }
            catch (SproutException e) when (e.Untrusted.Count > 0 && attempt < 3)
            {
                if (!TrustFlow.Approve(cli, e.Untrusted, s)) return null;
            }
            catch (Exception) when (held is not null)
            {
                cli.Host.TrySend(new HostRequest("stop", Sprout: held));
                throw;
            }
        }
    }

    /// <summary>
    /// Has the host build the apps while the infra comes up, held until starting them after <c>up</c> releases them.
    /// Returns the sprout's key when it did. Only a head start: if it fails, starting them afterwards builds as usual.
    /// </summary>
    string? Prebuild(SproutRecord record, Plan plan)
    {
        var specs = RunnerSpecs.For(record, plan);
        if (specs.Count == 0) return null;
        try
        {
            return cli.Host.Send(new HostRequest("start", specs, Restart: [], Hold: true)).Ok ? record.Key : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    int StartDetached(SproutRecord record, Plan plan, IReadOnlyList<RunnerSpec> specs, bool json, CancellationToken cancellation)
    {
        cli.Ansi.WriteLine();
        StartCommand.Send(cli, specs, []);
        var runners = StartCommand.WaitUntilReady(cli, record.Key, specs, json, cancellation);
        var ok = runners.All(r => r.State.Satisfies());
        if (json) CliOutput.Json(new { ok, sprout = record, plan, runners });
        else
        {
            var names = $"{Markup.Escape(record.Trellis)} {Markup.Escape(record.Name)}";
            cli.Ansi.MarkupLine($"[dim]The apps keep running in the background. Watch them: sprig ps; read one's log: sprig logs {names} <app>; " +
                                $"stop them: sprig stop {names}[/]");
        }
        return ok ? 0 : 1;
    }

    string? _lastOutput;

    void Report(SproutEvent e, bool json)
    {
        if (json) return;
        // Compose prints some progress lines twice.
        if (e.State == StepState.Output && e.Detail == _lastOutput) return;
        _lastOutput = e.State == StepState.Output ? e.Detail : null;
        switch (e.State)
        {
            case StepState.Running:
                cli.Ansi.MarkupLine($"[blue]…[/] {Markup.Escape(e.Step)}");
                break;
            case StepState.Done:
                cli.Ansi.MarkupLine($"[green]{Glyph.Check(cli.Ansi)}[/] {Markup.Escape(e.Step)}{(e.Detail is { } d ? $"  [dim]{Markup.Escape(d)}[/]" : "")}");
                break;
            case StepState.Failed:
                cli.Ansi.MarkupLine($"[red]x[/] {Markup.Escape(e.Step)}");
                break;
            case StepState.Output when !string.IsNullOrWhiteSpace(e.Detail):
                cli.Ansi.MarkupLine($"    [dim]{Markup.Escape(e.Detail!.Trim())}[/]");
                break;
        }
    }

    /// <param name="appsStart">Sprig starts the apps next, so there's no need to say how to start them by hand.</param>
    void Summary(SproutRecord record, Plan plan, string root, bool appsStart)
    {
        cli.Ansi.WriteLine();
        var where = record.InPlace ? "repos in place" : $"branch {record.Branch}";
        cli.Ansi.MarkupLine($"[bold]{Markup.Escape(record.Trellis)}[/] / [bold]{Markup.Escape(record.Name)}[/] is up  [dim]({Markup.Escape(where)}, {Markup.Escape(record.PortPolicy)} ports)[/]");

        var apps = new Table().Border(TableBorder.Rounded).AddColumns("App", "Folder", "Url", "Values in");
        foreach (var app in plan.Apps)
        {
            var repo = record.Repos.Single(r => r.RepoPath == app.RepoPath);
            var dir = Path.GetFullPath(Path.Combine(repo.Dir, app.Path));
            var url = plan.Ports.FirstOrDefault(p => p.Owner == app.Owner && p.Resource == app.Name) is { Port: { } port } ? $"http://localhost:{port}" : "";
            var files = record.Files.Where(f => f.Path.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                .Select(f => Path.GetRelativePath(dir, f.Path).Replace('\\', '/')).ToArray();
            apps.AddRow(Markup.Escape($"{repo.Name}/{app.Name}"), Markup.Escape(dir.Replace('\\', '/')), Markup.Escape(url),
                files.Length > 0 ? Markup.Escape(string.Join(", ", files)) : $"[dim]none: sprig env {Markup.Escape(record.Trellis)} {Markup.Escape(record.Name)} {Markup.Escape(app.Name)}[/]");
        }
        cli.Ansi.Write(apps);

        if (plan.Containers.Count + plan.Compose.Count > 0)
        {
            var infra = new Table().Border(TableBorder.Rounded).AddColumns("Infra", "Ports");
            foreach (var c in plan.Containers) infra.AddRow(Markup.Escape($"{c.Owner}/{c.Name}"), Markup.Escape(string.Join(", ", c.Ports)));
            foreach (var c in plan.Compose)
                foreach (var svc in c.Services)
                    infra.AddRow(Markup.Escape($"{c.Owner}/{svc.Name}"), Markup.Escape(string.Join(", ", svc.Mappings)));
            cli.Ansi.Write(infra);
        }

        var warnings = plan.Problems.Where(p => p.Severity == ProblemSeverity.Warning).ToArray();
        if (warnings.Length > 0) PlanPrinter.Problems(cli.Ansi, warnings, root);
        var names = $"{Markup.Escape(record.Trellis)} {Markup.Escape(record.Name)}";
        if (!appsStart)
            cli.Ansi.MarkupLine($"[dim]Start the apps: sprig start {names} (or from their folders: dotnet run, npm run dev). " +
                                $"Stop the infra with: sprig down {names}[/]");
    }
}

[Description("Stop a sprout's infra, keeping its data, worktrees and ports")]
public sealed class DownCommand(CliContext cli) : Command<SproutSettings>
{
    protected override int Execute(CommandContext context, SproutSettings s, CancellationToken cancellation)
    {
        var record = cli.Sprouts.Down(s.Trellis, s.Sprout, new SyncProgress<SproutEvent>(e => Echo(cli, e, s.Json)), cancellation);
        return CliOutput.Ok(s.Json, $"{record.Trellis}/{record.Name} is down", new { ok = true, sprout = record });
    }

    internal static void Echo(CliContext cli, SproutEvent e, bool json)
    {
        if (json) return;
        if (e.State == StepState.Done) cli.Ansi.MarkupLine($"[green]{Glyph.Check(cli.Ansi)}[/] {Markup.Escape(e.Step)}{(e.Detail is { } d ? $"  [dim]{Markup.Escape(d)}[/]" : "")}");
        else if (e.State == StepState.Failed) cli.Ansi.MarkupLine($"[red]x[/] {Markup.Escape(e.Step)}");
    }
}

[Description("Remove a sprout: its containers and their data, its worktrees, and the values it wrote")]
public sealed class RmCommand(CliContext cli) : Command<RmCommand.Settings>
{
    public sealed class Settings : SproutSettings
    {
        [CommandOption("--delete-branch")]
        [Description("Also delete the sprout's branch in each repo")]
        public bool DeleteBranch { get; set; }

        [CommandOption("--force")]
        [Description("Remove worktrees with uncommitted changes, and delete branches with unpushed commits")]
        public bool Force { get; set; }

        [CommandOption("-y|--yes")]
        [Description("Don't ask first")]
        public bool Yes { get; set; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        var record = cli.Sprouts.Get(s.Trellis, s.Sprout) ?? throw new InvalidOperationException($"{s.Trellis} has no sprout named '{s.Sprout}'");
        if (!s.Yes)
        {
            if (!cli.Interactive(s.Json)) throw new InvalidOperationException("removing a sprout deletes its databases; pass --yes to confirm");
            var what = record.InPlace ? "its containers and their data, and the values it wrote into the repos"
                : $"its containers and their data, and its worktrees{(s.DeleteBranch ? $" and the branch '{record.Branch}'" : "")}";
            if (!cli.Ansi.Prompt(new ConfirmationPrompt($"Remove {Markup.Escape(record.Trellis)}/{Markup.Escape(record.Name)}: {Markup.Escape(what)}?") { DefaultValue = false }))
                return 1;
        }
        cli.Sprouts.Remove(s.Trellis, s.Sprout, new RemoveOptions(s.DeleteBranch, s.Force),
            new SyncProgress<SproutEvent>(e => DownCommand.Echo(cli, e, s.Json)), cancellation);
        return CliOutput.Ok(s.Json, $"removed {s.Trellis}/{s.Sprout}", new { ok = true, trellis = s.Trellis, sprout = s.Sprout });
    }
}

[Description("List sprouts")]
public sealed class SproutsCommand(CliContext cli) : Command<SproutsCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "[trellis]")]
        [Description("Only this trellis's sprouts")]
        public string? Trellis { get; set; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        var sprouts = cli.Sprouts.List().Where(r => s.Trellis is null || r.Trellis.Equals(s.Trellis, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (s.Json) { CliOutput.Json(sprouts); return 0; }
        if (sprouts.Length == 0)
        {
            cli.Ansi.MarkupLine("[dim]No sprouts. Make one with: sprig up <trellis> --sprout <name>[/]");
            return 0;
        }
        var table = new Table().Border(TableBorder.Rounded).AddColumns("Trellis", "Sprout", "Branch", "State", "Folder", "Updated");
        foreach (var r in sprouts)
        {
            var state = r.State switch
            {
                SproutState.Up => "[green]up[/]",
                SproutState.Failed => $"[red]failed[/] [dim]{Markup.Escape(r.Error ?? "")}[/]",
                SproutState.Starting => "[yellow]starting[/]",
                _ => "[dim]down[/]",
            };
            table.AddRow(Markup.Escape(r.Trellis), Markup.Escape(r.Name), Markup.Escape(r.Branch ?? "(in place)"), state,
                Markup.Escape((r.Folder ?? string.Join(", ", r.Repos.Select(x => x.Dir))).Replace('\\', '/')),
                r.UpdatedAt.LocalDateTime.ToString("g"));
        }
        cli.Ansi.Write(table);
        return 0;
    }
}

[Description("Print the env vars an app of a sprout gets, for an IDE launch profile or a shell")]
public sealed class EnvCommand(CliContext cli) : Command<EnvCommand.Settings>
{
    public sealed class Settings : SproutSettings
    {
        [CommandArgument(2, "<app>")]
        [Description("The app, as <name> or <repo>/<name>")]
        public string App { get; set; } = "";
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        var plan = cli.Sprouts.LastPlan(s.Trellis, s.Sprout)
            ?? throw new InvalidOperationException($"{s.Trellis} has no sprout named '{s.Sprout}' that has come up");
        var matches = plan.Apps.Where(a => a.Name == s.App || $"{a.Owner}/{a.Name}" == s.App).ToArray();
        var app = matches.Length switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException($"no app '{s.App}' in {s.Trellis}; it has {string.Join(", ", plan.Apps.Select(a => $"{a.Owner}/{a.Name}"))}"),
            _ => throw new InvalidOperationException($"'{s.App}' is in more than one repo; name it as <repo>/{s.App}"),
        };
        var env = app.Env.Where(e => e.File is null).Select(e => (e.Key, Value: e.Value.Value ?? "")).ToArray();
        if (s.Json) CliOutput.Json(env.ToDictionary(e => e.Key, e => e.Value));
        else foreach (var (key, value) in env) Console.WriteLine($"{key}={value}");
        return 0;
    }
}

/// <summary>Calls back on the reporting thread, so progress prints in order. A tool's stdout and stderr arrive on
/// different threads, hence the lock.</summary>
sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    readonly Lock _gate = new();

    public void Report(T value)
    {
        lock (_gate) report(value);
    }
}
