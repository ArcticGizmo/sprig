using System.ComponentModel;
using Sprig.Core.Stacks;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Sprig.Cli.Commands;

// `sprig stack <sub>` — define and manage stacks (named sets of repos wired together).

[Description("Define a stack")]
public sealed class StackCreateCommand(CliContext cli) : Command<StackCreateCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "<name>")]
        [Description("Stack name")]
        public string Name { get; set; } = "";

        [CommandOption("--repos <repos>")]
        [Description("Comma-separated repo names")]
        public string? Repos { get; set; }

        [CommandOption("--port <port>")]
        [Description("A named port (repeatable)")]
        public string[] Port { get; set; } = [];

        [CommandOption("--bind <repo:input=expr>")]
        [Description("Wire a repo input to an expression (repeatable)")]
        public string[] Bind { get; set; } = [];

        [CommandOption("--max-slots <n>")]
        [Description("Pool size ceiling — the most workspaces this stack may run at once")]
        public int? MaxSlots { get; set; }

        [CommandOption("--setup <repo:cmd>")]
        [Description("Stack-supplied setup command for a repo (repeatable)")]
        public string[] Setup { get; set; } = [];
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        var reposCsv = s.Repos ?? throw new ArgumentException("stack create requires --repos a,b");
        var bindings = CliFormat.ParseBindings(s.Bind);
        if (cli.Stacks.Get(s.Name) is not null)
            throw new ArgumentException($"stack '{s.Name}' already exists — use 'stack edit {s.Name}' to change it");
        var created = new StackDefinition
        {
            Name = s.Name,
            Repos = reposCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Ports = s.Port,
            Bindings = bindings,
            MaxSlots = s.MaxSlots ?? StackDefinition.DefaultMaxSlots,
            Setup = CliFormat.ParseRepoCommands(s.Setup),
        };
        // Populate the shared-port overlay from the bindings so a CLI-built stack shows its shares in
        // the app (and passes the store's share/binding consistency check).
        cli.Stacks.Save(created with { Shares = StackMigration.DeriveShares(created) });
        return CliOutput.Ok(s.Json, $"created stack '{s.Name}'", new { ok = true, name = s.Name, action = "create" });
    }
}

[Description("Amend a stack")]
public sealed class StackEditCommand(CliContext cli) : Command<StackEditCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "<name>")]
        [Description("Stack name")]
        public string Name { get; set; } = "";

        [CommandOption("--repos <repos>")]
        [Description("Replace the repo list (comma-separated)")]
        public string? Repos { get; set; }

        [CommandOption("--port <port>")]
        [Description("Replace the port list (repeatable)")]
        public string[] Port { get; set; } = [];

        [CommandOption("--bind <repo:input=expr>")]
        [Description("Merge a binding (repeatable)")]
        public string[] Bind { get; set; } = [];

        [CommandOption("--max-slots <n>")]
        [Description("Replace the pool size ceiling")]
        public int? MaxSlots { get; set; }

        [CommandOption("--setup <repo:cmd>")]
        [Description("Set a repo's stack setup (repeated per repo replaces that repo's commands)")]
        public string[] Setup { get; set; } = [];
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        var current = cli.Stacks.Get(s.Name)
            ?? throw new ArgumentException($"unknown stack '{s.Name}' — use 'stack create' to make one");
        var bindOpt = CliFormat.ParseBindings(s.Bind);
        // Each facet is replaced only if its flag was supplied; bindings and setup merge onto the existing
        // set (a repeated key overrides, others are kept). Shares are re-derived.
        var edited = current with
        {
            Repos = s.Repos is not null
                ? s.Repos.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : current.Repos,
            Ports = s.Port.Length > 0 ? s.Port : current.Ports,
            Bindings = CliFormat.MergeBindings(current.Bindings, bindOpt),
            MaxSlots = s.MaxSlots ?? current.MaxSlots,
            Setup = CliFormat.MergeRepoCommands(current.Setup, CliFormat.ParseRepoCommands(s.Setup)),
        };
        cli.Stacks.Save(edited with { Shares = StackMigration.DeriveShares(edited) });
        return CliOutput.Ok(s.Json, $"updated stack '{s.Name}'", new { ok = true, name = s.Name, action = "edit" });
    }
}

[Description("List stacks")]
public sealed class StackLsCommand(CliContext cli) : Command<GlobalSettings>
{
    protected override int Execute(CommandContext context, GlobalSettings s, CancellationToken cancellation)
    {
        var all = cli.Stacks.List();
        if (s.Json) { CliOutput.Json(all); return 0; }
        if (all.Count == 0) { cli.Ansi.MarkupLine("[dim]no stacks defined[/]"); return 0; }

        var table = CliFormat.Table("STACK", "REPOS");
        foreach (var stack in all)
            table.AddRow(Markup.Escape(stack.Name), Markup.Escape(string.Join(", ", stack.Repos)));
        cli.Ansi.Write(table);
        return 0;
    }
}

[Description("Show a stack's repos, ports, bindings and shares")]
public sealed class StackShowCommand(CliContext cli) : Command<StackShowCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "<name>")]
        [Description("Stack name")]
        public string Name { get; set; } = "";
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        var stack = cli.Stacks.Get(s.Name) ?? throw new ArgumentException($"unknown stack '{s.Name}'");
        if (s.Json) { CliOutput.Json(stack); return 0; }
        CliFormat.PrintStack(cli.Ansi, stack);
        return 0;
    }
}

[Description("Delete a stack")]
public sealed class StackRmCommand(CliContext cli) : Command<StackRmCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "<name>")]
        [Description("Stack name")]
        public string Name { get; set; } = "";
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        cli.Stacks.Remove(s.Name);
        return CliOutput.Ok(s.Json, $"removed stack '{s.Name}'", new { ok = true, name = s.Name, action = "remove" });
    }
}

[Description("Write a stack to a file")]
public sealed class StackExportCommand(CliContext cli) : Command<StackExportCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "<name>")]
        [Description("Stack name")]
        public string Name { get; set; } = "";

        [CommandArgument(1, "<path>")]
        [Description("Destination file")]
        public string Path { get; set; } = "";
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        var dest = cli.Stacks.Export(s.Name, s.Path);
        return CliOutput.Ok(s.Json, $"exported to {dest}", new { ok = true, name = s.Name, path = dest });
    }
}

[Description("Read a stack from a file")]
public sealed class StackImportCommand(CliContext cli) : Command<StackImportCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "<path>")]
        [Description("Source file")]
        public string Path { get; set; } = "";
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        var imported = cli.Stacks.Import(s.Path);
        return CliOutput.Ok(s.Json, $"imported stack '{imported.Name}'", new { ok = true, name = imported.Name, action = "import" });
    }
}
