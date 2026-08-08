using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

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
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        var added = cli.Repos.Add(s.Path, s.Name);
        return CliOutput.Ok(s.Json, $"registered '{added.Name}' -> {added.Path}",
            new { ok = true, name = added.Name, path = added.Path });
    }
}

[Description("List registered repos")]
public sealed class RepoLsCommand(CliContext cli) : Command<GlobalSettings>
{
    protected override int Execute(CommandContext context, GlobalSettings s, CancellationToken cancellation)
    {
        var repos = cli.Repos.List();
        if (s.Json) { CliOutput.Json(repos); return 0; }
        if (repos.Count == 0) { Console.WriteLine("no repos registered"); return 0; }

        var table = CliFormat.Table("NAME", "PATH");
        foreach (var r in repos)
            table.AddRow(Markup.Escape(r.Name), Markup.Escape(r.Path));
        cli.Ansi.Write(table);
        return 0;
    }
}

[Description("Unregister a repo")]
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
        cli.Repos.Remove(s.Name);
        return CliOutput.Ok(s.Json, $"unregistered '{s.Name}'", new { ok = true, name = s.Name, action = "remove" });
    }
}
