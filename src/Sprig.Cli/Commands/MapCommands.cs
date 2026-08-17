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
