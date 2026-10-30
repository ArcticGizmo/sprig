using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Sprig.Core.Config;
using Sprig.Core.Init;

namespace Sprig.Cli.Commands;

// `sprig repo <sub>` — register the repositories sprig builds workspaces from.

[Description("Register a repo (name defaults to the folder)")]
public sealed class RepoAddCommand(CliContext cli) : Command<RepoAddCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "<path>")]
        [Description("Path to the repository")]
        public string Path { get; set; } = "";

        [CommandOption("--name <name>")]
        [Description("Registry name (defaults to the folder name)")]
        public string? Name { get; set; }

        [CommandOption("--virtual")]
        [Description("Keep the config in sprig instead of the repo, so nothing needs committing. Starts from --from, else the repo's .sprig.json, else a scaffold")]
        public bool Virtual { get; set; }

        [CommandOption("--from <file>")]
        [Description("With --virtual: the .sprig.json to start the virtual config from")]
        public string? From { get; set; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        if (s.From is not null && !s.Virtual)
            throw new ArgumentException("--from only applies to a virtual repo — add --virtual");

        if (!s.Virtual)
        {
            var added = cli.Repos.Add(s.Path, s.Name);
            return CliOutput.Ok(s.Json, $"registered '{added.Name}' -> {added.Path}",
                new { ok = true, name = added.Name, path = added.Path });
        }

        var root = System.IO.Path.GetFullPath(s.Path);
        var committed = System.IO.Path.Combine(root, ".sprig.json");
        var config = s.From is not null ? SprigConfigLoader.LoadFromFile(s.From)
            : File.Exists(committed) ? SprigConfigLoader.LoadFromFile(committed)
            : new InitInspector(cli.Git).InspectMap(root).Config;
        var name = s.Name ?? System.IO.Path.GetFileName(root.TrimEnd('\\', '/'));

        var reg = cli.Repos.AddVirtual(root, name, config);
        return CliOutput.Ok(s.Json, $"registered virtual repo '{reg.Name}' -> {reg.Path} (config: {reg.VirtualConfigPath})",
            new { ok = true, name = reg.Name, path = reg.Path, @virtual = true, config = reg.VirtualConfigPath });
    }
}

[Description("List registered repos")]
public sealed class RepoLsCommand(CliContext cli) : Command<GlobalSettings>
{
    protected override int Execute(CommandContext context, GlobalSettings s, CancellationToken cancellation)
    {
        var repos = cli.Repos.List();
        if (s.Json) { CliOutput.Json(repos); return 0; }
        if (repos.Count == 0) { cli.Ansi.MarkupLine("[dim]no repos registered[/]"); return 0; }

        var table = CliFormat.Table("NAME", "PATH", "CONFIG");
        foreach (var r in repos)
            table.AddRow(Markup.Escape(r.Name), Markup.Escape(r.Path),
                r.IsVirtual ? "[yellow]virtual[/] [dim](not committed)[/]" : "[dim]committed[/]");
        cli.Ansi.Write(table);
        return 0;
    }
}

[Description("Unregister a repo (a virtual repo's config is deleted with it)")]
public sealed class RepoRmCommand(CliContext cli) : Command<RepoRmCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "<name>")]
        [Description("Registry name to remove")]
        public string Name { get; set; } = "";
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        var wasVirtual = cli.Repos.Get(s.Name)?.IsVirtual == true;
        cli.Repos.Remove(s.Name);
        return CliOutput.Ok(s.Json,
            wasVirtual ? $"unregistered virtual repo '{s.Name}' and deleted its config" : $"unregistered '{s.Name}'",
            new { ok = true, name = s.Name, action = "remove" });
    }
}
