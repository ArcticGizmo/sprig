using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Sprig.Cli.Commands;

// `sprig map <sub>` — EXPERIMENTAL (the Graph Turn). Check out workspaces from a map of self-describing
// repos. Runs alongside `stack`/`pool` through the transition; becomes primary when stacks are retired (M7).

[Description("List defined maps")]
public sealed class MapLsCommand(CliContext cli) : Command<GlobalSettings>
{
    protected override int Execute(CommandContext context, GlobalSettings s, CancellationToken cancellation)
    {
        var maps = cli.Maps.List();
        if (s.Json) { CliOutput.Json(maps.Select(m => new { m.Name, repos = m.Repos.Select(r => r.Name) })); return 0; }
        if (maps.Count == 0) { cli.Ansi.MarkupLine("[dim]no maps defined[/]"); return 0; }

        var table = CliFormat.Table("NAME", "REPOS");
        foreach (var m in maps)
            table.AddRow(Markup.Escape(m.Name), Markup.Escape(string.Join(", ", m.Repos.Select(r => r.Name))));
        cli.Ansi.Write(table);
        return 0;
    }
}

[Description("Import a map definition from a JSON file (validates + saves it)")]
public sealed class MapImportCommand(CliContext cli) : Command<MapImportCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "<file>")]
        [Description("Path to a map JSON file")]
        public string File { get; set; } = "";
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        var map = cli.Maps.Import(s.File);
        return CliOutput.Ok(s.Json, $"imported map '{map.Name}' ({map.Repos.Count} repo{(map.Repos.Count == 1 ? "" : "s")})",
            new { ok = true, name = map.Name, repos = map.Repos.Select(r => r.Name) });
    }
}

[Description("Show a map's repos, wiring and defaults")]
public sealed class MapShowCommand(CliContext cli) : Command<MapShowCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "<name>")]
        [Description("Map name")]
        public string Name { get; set; } = "";
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        var map = cli.Maps.Get(s.Name) ?? throw new Core.Maps.MapException($"unknown map '{s.Name}'");
        if (s.Json) { CliOutput.Json(map); return 0; }

        cli.Ansi.MarkupLine($"[bold]{Markup.Escape(map.Name)}[/]");
        foreach (var r in map.Repos)
            cli.Ansi.MarkupLine($"  repo [green]{Markup.Escape(r.Name)}[/]"
                + (string.IsNullOrWhiteSpace(r.Repo) ? "" : $" [dim]({Markup.Escape(r.Repo!)})[/]"));
        foreach (var (repo, caps) in map.Wiring)
            foreach (var (need, provider) in caps)
                cli.Ansi.MarkupLine($"  wire [yellow]{Markup.Escape(repo)}[/].{Markup.Escape(need)} -> {Markup.Escape(provider)}");
        foreach (var (repo, caps) in map.Defaults)
            foreach (var (cap, outs) in caps)
                foreach (var (o, v) in outs)
                    cli.Ansi.MarkupLine($"  default [yellow]{Markup.Escape(repo)}[/].{Markup.Escape(cap)}.{Markup.Escape(o)} = {Markup.Escape(v)}");
        return 0;
    }
}

[Description("Create a workspace from a map (selecting a slice of its repos)")]
public sealed class MapCreateCommand(CliContext cli) : Command<MapCreateCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "<workspace>")]
        [Description("Name for the new workspace")]
        public string Workspace { get; set; } = "";

        [CommandOption("--map <name>")]
        [Description("The map to check out from")]
        public string Map { get; set; } = "";

        [CommandOption("--without <repos>")]
        [Description("Comma-separated repos to leave out of this workspace")]
        public string? Without { get; set; }

        [CommandOption("--from <ref>")]
        [Description("Start point for the parked worktrees (defaults to each repo's base)")]
        public string? From { get; set; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(s.Map))
            throw new Core.Maps.MapException("a map is required: --map <name>");

        var without = string.IsNullOrWhiteSpace(s.Without)
            ? null
            : s.Without.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var (map, repos) = cli.MapResolver.Resolve(s.Map, without);
        var record = cli.Workspaces.CreateFromMap(s.Workspace, map, repos, startPoint: s.From);

        return CliOutput.Ok(s.Json,
            $"created '{record.Workspace}' from map '{s.Map}' ({string.Join(", ", record.SelectedRepos)})",
            new
            {
                ok = true,
                workspace = record.Workspace,
                map = record.Map,
                repos = record.SelectedRepos,
                ports = record.Ports,
            });
    }
}
