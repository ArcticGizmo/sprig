using System.ComponentModel;
using Sprig.Engine.Definitions;
using Sprig.Engine.Planning;
using Sprig.Engine.Store;
using Sprig.Engine.Orchestrations;
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
                var why = (item.ApprovedSha256, item.IsCopy) switch
                {
                    (null, _) => "hasn't been approved",
                    (_, false) => "changed since you approved it",
                    _ => $"differs from every version of {item.TrustPath} you've approved",
                };
                cli.Ansi.MarkupLine($"[yellow]{Markup.Escape(item.Path)}[/] {Markup.Escape(why)}.");
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
            cli.Trust.Approve(item.TrustPath, item.Sha256);
        }
        return true;
    }
}

[Description("Register an orchestration file so commands can name it")]
public sealed class OrchestrationAddCommand(CliContext cli) : Command<OrchestrationAddCommand.Settings>
{
    public sealed class Settings : TrustingSettings
    {
        [CommandArgument(0, "<file>")]
        [Description("The orchestration file, e.g. storefront.orchestration.cs")]
        public string File { get; set; } = "";

        [CommandOption("--id <id>")]
        [Description("What to register it as (default: the name in its code, or that name with -2, -3… when taken). Its workspaces are keyed by this")]
        public string? Id { get; set; }
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
            Evaluated { Model: OrchestrationModel t } => t,
            Evaluated => throw new InvalidOperationException($"{Path.GetFileName(path)} describes a repo, not an orchestration"),
            Failed f => throw new InvalidOperationException($"{Path.GetFileName(path)} {f.Reason}\n{f.Output.Trim()}"),
            Rejected r => throw new InvalidOperationException(string.Join("\n", r.Violations.Select(v => $"line {v.Line}: {v.Directive}: {v.Reason}"))),
            _ => throw new InvalidOperationException("the orchestration couldn't be read"),
        };

        var entry = cli.Orchestrations.Add(s.Id ?? cli.Orchestrations.UnusedId(model.Name, path), path);
        var named = entry.Id == model.Name ? "" : $" (it calls itself '{model.Name}')";
        return CliOutput.Ok(s.Json, $"registered '{entry.Id}'{named} ({model.Repos.Count} repos) from {entry.Path}",
            new { ok = true, id = entry.Id, name = model.Name, path = entry.Path, repos = model.Repos.Select(r => r.Path) });
    }
}

[Description("List registered orchestrations")]
public sealed class OrchestrationLsCommand(CliContext cli) : Command<GlobalSettings>
{
    protected override int Execute(CommandContext context, GlobalSettings s, CancellationToken cancellation)
    {
        var entries = cli.Orchestrations.List();
        if (s.Json) { CliOutput.Json(entries); return 0; }
        if (entries.Count == 0)
        {
            cli.Ansi.MarkupLine("[dim]No orchestrations registered. Add one with: sprig orchestration add <file>[/]");
            return 0;
        }
        var table = new Table().Border(TableBorder.Rounded).AddColumns("ID", "File");
        foreach (var e in entries)
            table.AddRow(Markup.Escape(e.Id), File.Exists(e.Path) ? Markup.Escape(e.Path) : $"[red]{Markup.Escape(e.Path)} (missing)[/]");
        cli.Ansi.Write(table);
        return 0;
    }
}

[Description("Forget a registered orchestration (the file is left alone)")]
public sealed class OrchestrationRmCommand(CliContext cli) : Command<OrchestrationRmCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "<id>")]
        public string Id { get; set; } = "";
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        if (!cli.Orchestrations.Remove(s.Id)) throw new InvalidOperationException($"no orchestration registered as '{s.Id}'");
        return CliOutput.Ok(s.Json, $"forgot '{s.Id}'", new { ok = true, id = s.Id });
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
        var trustPath = cli.Workspaces.TrustPathFor(path);
        cli.Trust.Approve(trustPath, sha);
        var message = trustPath == path ? $"approved {path} ({sha[..12]})" : $"approved this version of {trustPath} ({sha[..12]}), from {path}";
        return CliOutput.Ok(s.Json, message, new { ok = true, path, trustPath, sha256 = sha });
    }
}

[Description("List approved definition files")]
public sealed class TrustLsCommand(CliContext cli) : Command<GlobalSettings>
{
    protected override int Execute(CommandContext context, GlobalSettings s, CancellationToken cancellation)
    {
        var entries = cli.Trust.List();
        if (s.Json) { CliOutput.Json(entries); return 0; }
        var table = new Table().Border(TableBorder.Rounded).AddColumns("File", "Versions", "Last approved", "Now");
        foreach (var file in entries.GroupBy(e => e.Path, StringComparer.OrdinalIgnoreCase))
        {
            var current = File.Exists(file.Key) ? DefinitionHash.Of(File.ReadAllText(file.Key)) : null;
            var now = current is null ? "[red]missing[/]" : file.Any(e => e.Sha256 == current) ? "[green]approved[/]" : "[yellow]changed[/]";
            table.AddRow(Markup.Escape(file.Key), file.Count().ToString(), file.Max(e => e.ApprovedAt).LocalDateTime.ToString("g"), now);
        }
        cli.Ansi.Write(table);
        return 0;
    }
}

[Description("Show everything a workspace of an orchestration would get, and where each value comes from")]
public sealed class PlanCommand(CliContext cli) : Command<PlanCommand.Settings>
{
    public sealed class Settings : TrustingSettings
    {
        [CommandArgument(0, "[id]")]
        [Description("A repo ID from the Repos list, an orchestration ID, or an orchestration file (default: the repo this folder is)")]
        public string? Orchestration { get; set; }

        [CommandOption("-w|--workspace <name>")]
        [Description("The workspace to plan for (default: 'local', or the one whose worktree this is). An existing workspace's definitions are read from its worktrees, and its values used")]
        public string? Workspace { get; set; }

        [CommandOption("--set <name=value>")]
        [Description("Plan with this value for a setting, over the workspace's own. Repeat for more")]
        public string[] Set { get; set; } = [];
    }

    protected override ValidationResult Validate(CommandContext context, Settings s)
        => SettingValues.Parse(s.Set, out _) is { } error ? ValidationResult.Error(error) : ValidationResult.Success();

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        var (id, worktreeOf) = s.Orchestration is { } named ? (named, null) : Here.Find(cli);
        var workspace = s.Workspace ?? worktreeOf ?? "local";
        if (OrchestrationLoad.Load(cli, id, s, workspace) is not { } loaded) return 1;

        SettingValues.Parse(s.Set, out var given);
        var values = SettingValues.Merge(cli.Workspaces.Get(loaded.Id, workspace)?.Values, given);
        var problems = new List<PlanProblem>();
        var compose = loaded.ComposeFiles(problems);
        var plan = Planner.Build(new PlanInput(loaded.Orchestration!, loaded.Repos, workspace, cli.Settings.Get(), PortAllocator.Listening(),
            ComposeFiles: compose, Values: values));
        if (!plan.HasErrors) problems.AddRange(cli.Workspaces.CheckFiles(plan, loaded.ReadFrom));
        plan = plan with { Problems = [.. problems, .. plan.Problems] };
        if (s.Json) CliOutput.Json(plan);
        else PlanPrinter.Print(cli.Ansi, plan, Path.GetDirectoryName(loaded.OrchestrationPath)!);
        return plan.HasErrors ? 1 : 0;
    }
}

static class OrchestrationLoad
{
    /// <summary>Load an orchestration by name or path, asking to approve what isn't yet. Null (after saying why) when it
    /// can't be planned.</summary>
    /// <param name="workspace">When it names a workspace with worktrees, the repos' definitions are read from those.</param>
    public static LoadedOrchestration? Load(CliContext cli, string nameOrPath, TrustingSettings s, string? workspace = null)
    {
        var path = cli.Orchestrations.Resolve(nameOrPath)
            ?? throw new InvalidOperationException($"no orchestration or repo named '{nameOrPath}'. Register an orchestration with: sprig orchestration add <file>, or a repo with: sprig repo add <folder>");

        var loader = new OrchestrationLoader(cli.Definitions, cli.Orchestrations, new StoredDefinitions(cli.Paths));
        var worktrees = workspace is null ? null : cli.Workspaces.Worktrees(path, workspace);
        var loaded = loader.Load(path, worktrees);
        if (loaded.Untrusted.Count > 0)
        {
            if (!TrustFlow.Approve(cli, loaded.Untrusted, s)) return null;
            loaded = loader.Load(path, worktrees);
        }

        if (loaded.Orchestration is not null && loaded.Problems.Count == 0) return loaded;
        if (s.Json) CliOutput.Json(new { ok = false, problems = loaded.Problems });
        else PlanPrinter.Problems(cli.Ansi, loaded.Problems, Path.GetDirectoryName(path)!);
        return null;
    }
}

static class PlanPrinter
{
    public static void Print(IAnsiConsole console, Plan plan, string root)
    {
        console.MarkupLine($"[bold]{Markup.Escape(plan.Orchestration)}[/]  ·  workspace [bold]{Markup.Escape(plan.Workspace)}[/]");
        console.MarkupLine("[dim]Ports are a preview; they're leased when a workspace comes up.[/]");
        console.WriteLine();

        if (plan.ToAsk.Count > 0)
        {
            var asks = new Table().Border(TableBorder.Rounded).Title("[bold]To ask[/]").AddColumns("Setting", "Type", "For", "Declared at");
            foreach (var a in plan.ToAsk)
                asks.AddRow(Markup.Escape(a.Name), Markup.Escape(a.Type), Markup.Escape(a.Owner),
                    Markup.Escape($"{Relative(a.Location.File, root)}{(a.Location.Line > 0 ? $":{a.Location.Line}" : "")}"));
            console.Write(asks);
            console.MarkupLine("[dim]Nothing gives these a value. Give them one when the workspace comes up: sprig up <id> --set name=value[/]");
            console.WriteLine();
        }

        var ports = new Table().Border(TableBorder.Rounded).Title("[bold]Ports[/]").AddColumns("Owner", "Resource", "Endpoint", "Port", "Pin");
        foreach (var p in plan.Ports)
            ports.AddRow(Markup.Escape(p.Owner), Markup.Escape(p.Resource), Markup.Escape(p.Endpoint),
                p.Port?.ToString() ?? "[red]none[/]", Markup.Escape(p.Pin ?? ""));
        console.Write(ports);

        foreach (var app in plan.Apps)
        {
            var table = new Table().Border(TableBorder.Rounded)
                .Title($"[bold]{Markup.Escape(app.Owner)} / {Markup.Escape(app.Name)}[/] [dim]({Markup.Escape(app.Kind)}{(app.Job ? " job" : "")}, {Markup.Escape(app.Path)})[/]")
                .AddColumns("Setting", "Value", "Comes from");
            foreach (var env in app.Env)
            {
                var key = env.File is null ? env.Key : $"{env.Key} [dim]({env.File})[/]";
                table.AddRow(new Markup(env.File is null ? Markup.Escape(env.Key) : key), Value(env.Value), Via(env.Value));
            }
            if (app.Run is { } run) table.AddRow(new Markup("[dim]run[/]"), Value(run), Via(run));
            if (app.WaitFor.Count > 0) table.AddRow(new Markup("[dim]waits for[/]"), new SpectreText(string.Join(", ", app.WaitFor)), new SpectreText(""));
            var implied = (app.DependsOn ?? []).Where(d => !app.WaitFor.Any(w => d == $"{app.Owner}/{w}")).ToArray();
            if (implied.Length > 0)
                table.AddRow(new Markup("[dim]starts after[/]"), new SpectreText(string.Join(", ", implied)), new SpectreText("its values point at them", new Style(Color.Grey)));
            var ready = (app.Health ?? []).Select(h => $"{h.Endpoint} answers {h.Path}")
                .Concat(app.ReadyLog is { } log ? [$"a log line matches {log}"] : []).ToArray();
            if (ready.Length > 0) table.AddRow(new Markup("[dim]ready when[/]"), new SpectreText(string.Join(" and ", ready)), new SpectreText(""));
            if (app.Restart is { } restart)
                table.AddRow(new Markup($"[dim]{(app.Job ? "retries" : "restarts")}[/]"),
                    new SpectreText(app.Job || restart.Policy != "always" ? $"after a failure, up to {restart.Max} in a row" : $"after any exit, up to {restart.Max} in a row"),
                    new SpectreText(""));
            if (app.Outputs.Count > 0)
                table.AddRow(new Markup("[dim]env vars also go to[/]"), new SpectreText(string.Join(", ", app.Outputs.Select(o => o.File))), new SpectreText(""));
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
            if (compose.Services.Count > 0)
                table.AddRow(new Markup("[dim]keeps[/]"), new SpectreText(string.Join(", ", compose.Services.Select(x => x.Name))), new SpectreText(""));
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

    static SpectreText Value(PlannedValue v)
    {
        if (v.Value is not null) return new SpectreText(ValueMask.Apply(v.Value));
        var asked = (v.Sources ?? []).Where(x => x.Owner is null && x.Setting is not null).Select(x => x.Setting!).ToArray();
        return asked.Length > 0
            ? new SpectreText($"to ask: {string.Join(", ", asked)}", new Style(Color.Yellow))
            : new SpectreText("unresolved", new Style(Color.Red));
    }

    static SpectreText Via(PlannedValue v) => new(string.Join(" → ", v.Via), new Style(Color.Grey));

    static string Relative(string file, string root)
    {
        var relative = Path.GetRelativePath(root, file);
        return relative.StartsWith("..", StringComparison.Ordinal) && Path.IsPathRooted(file) ? file : relative;
    }
}
