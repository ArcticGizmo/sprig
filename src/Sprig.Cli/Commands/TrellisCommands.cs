using System.ComponentModel;
using Sprig.Engine.Definitions;
using Sprig.Engine.Planning;
using Sprig.Engine.Store;
using Sprig.Engine.Trellises;
using Sprig.Model;
using Spectre.Console;
using Spectre.Console.Cli;
using SpectreText = Spectre.Console.Text;

namespace Sprig.Cli.Commands;

/// <summary>Settings shared by commands that may need to run definitions.</summary>
public class TrustingSettings : GlobalSettings
{
    [CommandOption("--trust")]
    [Description("Approve and run any definition that isn't approved yet, without asking")]
    public bool Trust { get; set; }
}

static class TrustFlow
{
    /// <summary>
    /// Approve each definition in <paramref name="pending"/>: all of them with <c>--trust</c>, one by one at a terminal,
    /// otherwise none. Returns false when something is still unapproved, after saying how to approve it.
    /// </summary>
    public static bool Approve(CliContext cli, IReadOnlyList<NeedsTrust> pending, TrustingSettings s)
    {
        if (pending.Count == 0) return true;
        var interactive = cli.Interactive(s.Json);
        foreach (var item in pending)
        {
            var approve = s.Trust;
            if (!approve && interactive)
            {
                var why = item.ApprovedSha256 is null ? "hasn't been approved" : "changed since you approved it";
                cli.Ansi.MarkupLine($"[yellow]{Markup.Escape(item.Path)}[/] {why}.");
                cli.Ansi.MarkupLine("[dim]  Running it runs its code with your permissions. Read it first if you didn't write it.[/]");
                approve = cli.Ansi.Prompt(new ConfirmationPrompt("  Approve and run it?") { DefaultValue = false });
            }
            if (!approve)
            {
                var message = $"{item.Path} isn't approved to run. Read it, then: sprig trust add \"{item.Path}\" (or pass --trust)";
                if (s.Json) CliOutput.Json(new { ok = false, error = message, untrusted = pending.Select(p => p.Path) });
                else Console.Error.WriteLine(message);
                return false;
            }
            cli.Trust.Approve(item.Path, item.Sha256);
        }
        return true;
    }
}

[Description("Register a trellis file so commands can name it")]
public sealed class TrellisAddCommand(CliContext cli) : Command<TrellisAddCommand.Settings>
{
    public sealed class Settings : TrustingSettings
    {
        [CommandArgument(0, "<file>")]
        [Description("The trellis file, e.g. storefront.trellis.cs")]
        public string File { get; set; } = "";
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        var path = LongPath.Of(s.File);
        if (!System.IO.File.Exists(path)) throw new FileNotFoundException($"no file at {path}");

        var result = cli.Definitions.Evaluate(path);
        if (result is NeedsTrust pending)
        {
            if (!TrustFlow.Approve(cli, [pending], s)) return 1;
            result = cli.Definitions.Evaluate(path);
        }

        var model = result switch
        {
            Evaluated { Model: TrellisModel t } => t,
            Evaluated => throw new InvalidOperationException($"{Path.GetFileName(path)} describes a repo, not a trellis"),
            Failed f => throw new InvalidOperationException($"{Path.GetFileName(path)} {f.Reason}\n{f.Output.Trim()}"),
            Rejected r => throw new InvalidOperationException(string.Join("\n", r.Violations.Select(v => $"line {v.Line}: {v.Directive}: {v.Reason}"))),
            _ => throw new InvalidOperationException("the trellis couldn't be read"),
        };

        var entry = cli.Trellises.Add(model.Name, path);
        return CliOutput.Ok(s.Json, $"registered '{entry.Name}' ({model.Repos.Count} repos) from {entry.Path}",
            new { ok = true, name = entry.Name, path = entry.Path, repos = model.Repos.Select(r => r.Path) });
    }
}

[Description("List registered trellises")]
public sealed class TrellisLsCommand(CliContext cli) : Command<GlobalSettings>
{
    protected override int Execute(CommandContext context, GlobalSettings s, CancellationToken cancellation)
    {
        var entries = cli.Trellises.List();
        if (s.Json) { CliOutput.Json(entries); return 0; }
        if (entries.Count == 0)
        {
            cli.Ansi.MarkupLine("[dim]No trellises registered. Add one with: sprig trellis add <file>[/]");
            return 0;
        }
        var table = new Table().Border(TableBorder.Rounded).AddColumns("Name", "File");
        foreach (var e in entries)
            table.AddRow(Markup.Escape(e.Name), File.Exists(e.Path) ? Markup.Escape(e.Path) : $"[red]{Markup.Escape(e.Path)} (missing)[/]");
        cli.Ansi.Write(table);
        return 0;
    }
}

[Description("Forget a registered trellis (the file is left alone)")]
public sealed class TrellisRmCommand(CliContext cli) : Command<TrellisRmCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "<name>")]
        public string Name { get; set; } = "";
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        if (!cli.Trellises.Remove(s.Name)) throw new InvalidOperationException($"no trellis named '{s.Name}'");
        return CliOutput.Ok(s.Json, $"forgot '{s.Name}'", new { ok = true, name = s.Name });
    }
}

[Description("Approve a definition file to run, by its exact content")]
public sealed class TrustAddCommand(CliContext cli) : Command<TrustAddCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "<file>")]
        public string File { get; set; } = "";
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        var path = LongPath.Of(s.File);
        var sha = DefinitionHash.Of(System.IO.File.ReadAllText(path));
        cli.Trust.Approve(path, sha);
        return CliOutput.Ok(s.Json, $"approved {path} ({sha[..12]})", new { ok = true, path, sha256 = sha });
    }
}

[Description("List approved definition files")]
public sealed class TrustLsCommand(CliContext cli) : Command<GlobalSettings>
{
    protected override int Execute(CommandContext context, GlobalSettings s, CancellationToken cancellation)
    {
        var entries = cli.Trust.List();
        if (s.Json) { CliOutput.Json(entries); return 0; }
        var table = new Table().Border(TableBorder.Rounded).AddColumns("File", "Content", "Approved", "Now");
        foreach (var e in entries)
        {
            var now = File.Exists(e.Path)
                ? DefinitionHash.Of(File.ReadAllText(e.Path)) == e.Sha256 ? "[green]unchanged[/]" : "[yellow]changed[/]"
                : "[red]missing[/]";
            table.AddRow(Markup.Escape(e.Path), e.Sha256[..12], e.ApprovedAt.LocalDateTime.ToString("g"), now);
        }
        cli.Ansi.Write(table);
        return 0;
    }
}

[Description("Show everything a sprout of a trellis would get, and where each value comes from")]
public sealed class PlanCommand(CliContext cli) : Command<PlanCommand.Settings>
{
    public sealed class Settings : TrustingSettings
    {
        [CommandArgument(0, "<trellis>")]
        [Description("A registered trellis name, or a trellis file")]
        public string Trellis { get; set; } = "";

        [CommandOption("--sprout <name>")]
        [Description("The sprout name to plan for (default: plan)")]
        public string Sprout { get; set; } = "plan";
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        var path = cli.Trellises.Resolve(s.Trellis)
            ?? throw new InvalidOperationException($"no trellis named '{s.Trellis}'. Register one with: sprig trellis add <file>");

        var loader = new TrellisLoader(cli.Definitions);
        var loaded = loader.Load(path);
        if (loaded.Untrusted.Count > 0)
        {
            if (!TrustFlow.Approve(cli, loaded.Untrusted, s)) return 1;
            loaded = loader.Load(path);
        }

        if (loaded.Trellis is null || loaded.Problems.Count > 0)
        {
            var problems = loaded.Problems;
            if (s.Json) CliOutput.Json(new { ok = false, problems });
            else PlanPrinter.Problems(cli.Ansi, problems, Path.GetDirectoryName(path)!);
            return 1;
        }

        var plan = Planner.Build(new PlanInput(loaded.Trellis, loaded.Repos, s.Sprout, cli.Settings.Get(), PortAllocator.Listening()));
        if (s.Json) CliOutput.Json(plan);
        else PlanPrinter.Print(cli.Ansi, plan, Path.GetDirectoryName(path)!);
        return plan.HasErrors ? 1 : 0;
    }
}

static class PlanPrinter
{
    public static void Print(IAnsiConsole console, Plan plan, string root)
    {
        var policy = $"{plan.PortPolicy} ports" + (plan.WorktreesFrom is { } from ? $"  ·  worktrees from {from}" : "  ·  repos run in place");
        console.MarkupLine($"[bold]{Markup.Escape(plan.Trellis)}[/]  ·  sprout [bold]{Markup.Escape(plan.Sprout)}[/]  ·  {Markup.Escape(policy)}");
        console.MarkupLine("[dim]Ports are a preview; they're leased when a sprout comes up.[/]");
        console.WriteLine();

        var ports = new Table().Border(TableBorder.Rounded).Title("[bold]Ports[/]").AddColumns("Owner", "Resource", "Endpoint", "Port", "Pin");
        foreach (var p in plan.Ports)
            ports.AddRow(Markup.Escape(p.Owner), Markup.Escape(p.Resource), Markup.Escape(p.Endpoint),
                p.Port?.ToString() ?? "[red]none[/]", Markup.Escape(p.Pin ?? ""));
        console.Write(ports);

        foreach (var app in plan.Apps)
        {
            var table = new Table().Border(TableBorder.Rounded)
                .Title($"[bold]{Markup.Escape(app.Owner)} / {Markup.Escape(app.Name)}[/] [dim]({Markup.Escape(app.Kind)}, {Markup.Escape(app.Path)})[/]")
                .AddColumns("Setting", "Value", "Comes from");
            foreach (var env in app.Env)
            {
                var key = env.File is null ? env.Key : $"{env.Key} [dim]({env.File})[/]";
                table.AddRow(new Markup(env.File is null ? Markup.Escape(env.Key) : key), Value(env.Value), Via(env.Value));
            }
            if (app.Run is { } run) table.AddRow(new Markup("[dim]run[/]"), Value(run), Via(run));
            if (app.WaitFor.Count > 0) table.AddRow(new Markup("[dim]waits for[/]"), new SpectreText(string.Join(", ", app.WaitFor)), new SpectreText(""));
            console.Write(table);
        }

        if (plan.Containers.Count > 0)
        {
            var table = new Table().Border(TableBorder.Rounded).Title("[bold]Containers[/]").AddColumns("Owner", "Name", "Image", "Ports", "Volumes");
            foreach (var c in plan.Containers)
                table.AddRow(Markup.Escape(c.Owner), Markup.Escape(c.Name), Markup.Escape(c.Image),
                    Markup.Escape(string.Join(", ", c.Ports)), Markup.Escape(string.Join(", ", c.Volumes)));
            console.Write(table);
        }

        foreach (var compose in plan.Compose)
        {
            var table = new Table().Border(TableBorder.Rounded)
                .Title($"[bold]{Markup.Escape(compose.Owner)} / {Markup.Escape(compose.File)}[/]").AddColumns("Path", "Value", "Comes from");
            foreach (var o in compose.Overrides) table.AddRow(new SpectreText(o.Path), Value(o.Value), Via(o.Value));
            console.Write(table);
        }

        if (plan.Groups.Count > 0)
        {
            var table = new Table().Border(TableBorder.Rounded).Title("[bold]Groups[/]").AddColumns("Group", "Starts");
            foreach (var g in plan.Groups) table.AddRow(Markup.Escape(g.Name), Markup.Escape(string.Join(", ", g.Apps)));
            console.Write(table);
        }

        Problems(console, plan.Problems, root);
    }

    public static void Problems(IAnsiConsole console, IReadOnlyList<PlanProblem> problems, string root)
    {
        if (problems.Count == 0)
        {
            console.MarkupLine("[green]No problems.[/]");
            return;
        }
        console.WriteLine();
        foreach (var p in problems)
        {
            var colour = p.Severity == ProblemSeverity.Error ? "red" : "yellow";
            var where = p.Location is { } l ? $"{Relative(l.File, root)}{(l.Line > 0 ? $":{l.Line}" : "")}  " : "";
            console.MarkupLine($"[{colour}]{p.Severity.ToString().ToLowerInvariant()}[/] [dim]{Markup.Escape(where)}[/]{Markup.Escape(p.Message)}");
        }
    }

    static SpectreText Value(PlannedValue v) => v.Value is null ? new SpectreText("unresolved", new Style(Color.Red)) : new SpectreText(ValueMask.Apply(v.Value));

    static SpectreText Via(PlannedValue v) => new(string.Join(" → ", v.Via), new Style(Color.Grey));

    static string Relative(string file, string root)
    {
        var relative = Path.GetRelativePath(root, file);
        return relative.StartsWith("..", StringComparison.Ordinal) && Path.IsPathRooted(file) ? file : relative;
    }
}
