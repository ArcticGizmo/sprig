using System.ComponentModel;
using Sprig.Engine.Orchestrations;
using Sprig.Engine.Planning;
using Sprig.Engine.Workspaces;
using Sprig.Model;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Sprig.Cli.Commands;

[Description("Make the repo in this folder (or another) ready to run: its own sprig.cs, or a draft from its projects")]
public sealed class InitCommand(CliContext cli) : Command<InitCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "[folder]")]
        [Description("The repo's folder (default: this one)")]
        public string? Folder { get; set; }

        [CommandOption("--id <id>")]
        [Description("Its ID (default: the folder's name)")]
        public string? Id { get; set; }

        [CommandOption("--in-repo")]
        [Description("Write the draft into the folder as sprig.cs, rather than keeping it in sprig")]
        public bool InRepo { get; set; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        var folder = Sprig.Engine.Store.LongPath.Of(s.Folder ?? Environment.CurrentDirectory);
        var (entry, existed) = RepoList.Authoring(cli).Init(folder, s.Id, s.InRepo);
        var payload = new { ok = true, id = entry.Id, folder = entry.Folder, inRepo = entry.InRepo, definition = entry.DefinitionFile, existed };
        if (s.Json) { CliOutput.Json(payload); return 0; }

        if (existed) CliOutput.Success($"already on the Repos list as '{entry.Id}' ({RepoList.Kind(entry)}), using {entry.DefinitionFile}");
        else CliOutput.Success($"added '{entry.Id}' ({RepoList.Kind(entry)}), using {entry.DefinitionFile}");
        var here = string.Equals(Path.GetFullPath(folder).TrimEnd('\\', '/'), Path.GetFullPath(Environment.CurrentDirectory).TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);
        cli.Ansi.MarkupLine($"[dim]Run it where it is with: {Markup.Escape(here ? "sprig up" : $"sprig up {entry.Id}")}. " +
                            $"Check a branch out in a slot of its own with: {Markup.Escape(here ? "sprig up -b <branch>" : $"sprig up {entry.Id} -b <branch>")}[/]");
        return 0;
    }
}

/// <summary>Settings for commands about one workspace's setting values, named by options so the values can be
/// positional.</summary>
public class ValuesSettings : TrustingSettings
{
    [CommandOption("--id <id>")]
    [Description("The repo or orchestration ID (default: the repo this folder is)")]
    public string? Id { get; set; }

    [CommandOption("-b|--branch <branch>")]
    [Description("The workspace, by its branch or slot number (default: the slot this folder is in, else main)")]
    public string? Workspace { get; set; }

    public (string Id, string Workspace) Target(CliContext cli)
    {
        var (id, worktreeOf) = Id is { } named ? (named, null) : Here.Find(cli, Workspace);
        return (id, Workspace is { } word ? Here.WorkspaceName(cli, id, word) : worktreeOf ?? WorkspaceRecord.MainName);
    }
}

static class ValuesChange
{
    /// <summary>Keep the change on the workspace and, when it's up, bring it up again so its files have the new values,
    /// then say which apps to restart.</summary>
    public static int Apply(CliContext cli, ValuesSettings s, IReadOnlyDictionary<string, string> set, IReadOnlyCollection<string> unset)
    {
        var (id, workspace) = s.Target(cli);
        if (workspace == WorkspaceRecord.MainName)
            throw new InvalidOperationException("main runs with the definitions' own values; check a branch out in a slot to set your own: sprig up -b <branch> --set name=value");
        if (OrchestrationLoad.Load(cli, id, s, workspace) is not { } loaded) return 1;
        if (cli.Workspaces.Get(loaded.Id, workspace) is not { } record)
            throw new InvalidOperationException($"{loaded.Id} has no workspace '{workspace}' yet; give it values as it comes up: sprig up {(s.Id is null ? "" : $"{loaded.Id} ")}--set name=value");

        var before = cli.Workspaces.LastPlan(record.Orchestration, record.Name);
        record = cli.Workspaces.SetValues(record.Orchestration, record.Name, set, unset);
        var changed = string.Join(", ", set.Keys.Concat(unset));
        if (record.State != WorkspaceState.Up)
            return CliOutput.Ok(s.Json, $"{changed} kept on {record.Key}; it uses them next time it comes up", new { ok = true, values = s.UnmaskSecrets ? record.Values : Masked(record, cli) });

        try
        {
            cli.Workspaces.Up(loaded, record.Name, null);
        }
        catch (WorkspaceException e)
        {
            Console.Error.WriteLine($"error: kept, but {record.Key} couldn't take them: {e.Message}");
            if (e.Asks.Count > 0) cli.Ansi.MarkupLine("[dim]Give it the rest with sprig set, or bring it up with: sprig up --set name=value[/]");
            return 1;
        }
        var after = cli.Workspaces.LastPlan(record.Orchestration, record.Name)!;
        var restart = Changed(before, after);
        if (s.Json) { CliOutput.Json(new { ok = true, values = s.UnmaskSecrets ? record.Values : Masked(record, cli), restart }); return 0; }
        CliOutput.Success($"{changed} set on {record.Key}, and its files rewritten");
        if (restart.Count > 0)
            cli.Ansi.MarkupLine($"[dim]Restart what uses them: sprig start {Markup.Escape(string.Join(" ", restart))}{(s.Id is null && s.Workspace is null ? "" : $" (with {Markup.Escape(UpCommand.Words(record))} first)")}[/]");
        return 0;
    }

    /// <summary>Apps whose values differ between the two plans, by name.</summary>
    static IReadOnlyList<string> Changed(Plan? before, Plan after) => after.Apps
        .Where(a => before?.Apps.FirstOrDefault(b => b.Key == a.Key) is not { } old
                    || !old.Env.Select(e => (e.Key, e.File, e.Value.Value)).SequenceEqual(a.Env.Select(e => (e.Key, e.File, e.Value.Value))))
        .Select(a => a.Name)
        .ToArray();

    public static IReadOnlyDictionary<string, string> Masked(WorkspaceRecord record, CliContext cli)
        => SecretValues.Trimmed(record.Values, cli.Workspaces.LastPlan(record.Orchestration, record.Name)) ?? new Dictionary<string, string>();
}

[Description("Give settings values in a workspace, over the orchestration's and the repos' defaults. Kept for next time")]
public sealed class SetCommand(CliContext cli) : Command<SetCommand.Settings>
{
    public sealed class Settings : ValuesSettings
    {
        [CommandArgument(0, "<name=value>")]
        [Description("One or more settings to give a value")]
        public string[] Pairs { get; set; } = [];
    }

    protected override ValidationResult Validate(CommandContext context, Settings s)
        => SettingValues.Parse(s.Pairs, out _) is { } error ? ValidationResult.Error(error) : ValidationResult.Success();

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        SettingValues.Parse(s.Pairs, out var values);
        return ValuesChange.Apply(cli, s, values, []);
    }
}

[Description("Clear values set in a workspace, so the orchestration's or the repo's default applies again")]
public sealed class UnsetCommand(CliContext cli) : Command<UnsetCommand.Settings>
{
    public sealed class Settings : ValuesSettings
    {
        [CommandArgument(0, "<name>")]
        [Description("One or more settings to clear")]
        public string[] Names { get; set; } = [];
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
        => ValuesChange.Apply(cli, s, new Dictionary<string, string>(), s.Names);
}

[Description("List each setting of a workspace's repos, its value, and where the value comes from")]
public sealed class SettingsCommand(CliContext cli) : Command<ValuesSettings>
{
    protected override int Execute(CommandContext context, ValuesSettings s, CancellationToken cancellation)
    {
        var (id, workspace) = s.Target(cli);
        if (Read(cli, id, workspace, s) is not { } read) return 1;
        if (s.Json) { CliOutput.Json(read.Rows); return 0; }
        Write(cli, read.Loaded.Id, read.Rows,
            $"[bold]{Markup.Escape(read.Record?.SlotName ?? read.Loaded.Id)}[/] · [bold]{Markup.Escape(read.Record?.Label ?? workspace)}[/]{(read.Record is null ? " [dim](not made yet)[/]" : "")}");
        return 0;
    }

    internal sealed record Row(string Repo, string Name, string Type, string? Value, string From);

    /// <summary>Each setting of <paramref name="id"/>'s repos as <paramref name="workspace"/> has it. Null (after saying
    /// why) when the definitions can't be run.</summary>
    internal static (LoadedOrchestration Loaded, WorkspaceRecord? Record, Row[] Rows)? Read(CliContext cli, string id, string workspace, TrustingSettings s)
    {
        if (OrchestrationLoad.Load(cli, id, s, workspace) is not { } loaded) return null;
        var record = cli.Workspaces.Get(loaded.Id, workspace);
        var plan = Planner.Build(new PlanInput(loaded.Orchestration!, loaded.Repos, workspace, cli.Settings.Get(), new HashSet<int>(),
            Values: record?.Values));
        return (loaded, record, Rows(loaded, plan, record, s).ToArray());
    }

    internal static void Write(CliContext cli, string id, IReadOnlyList<Row> rows, string title)
    {
        if (rows.Count == 0)
        {
            cli.Ansi.MarkupLine($"[dim]{Markup.Escape(id)}'s repos have no settings.[/]");
            return;
        }
        var table = new Table().Border(TableBorder.Rounded).Title(title).AddColumns("Repo", "Setting", "Type", "Value", "From");
        foreach (var r in rows)
            table.AddRow(new Markup(Markup.Escape(r.Repo)), new Markup(Markup.Escape(r.Name)), new Markup($"[dim]{Markup.Escape(r.Type)}[/]"),
                new Markup(r.Value is null ? "[yellow]to ask[/]" : Markup.Escape(r.Value)), new Markup($"[dim]{Markup.Escape(r.From)}[/]"));
        cli.Ansi.Write(table);
        cli.Ansi.MarkupLine("[dim]Change one with: sprig set name=value. Clear it with: sprig unset name[/]");
    }

    static IEnumerable<Row> Rows(LoadedOrchestration loaded, Plan plan, WorkspaceRecord? record, GlobalSettings s)
    {
        foreach (var repo in loaded.Repos.Values)
            foreach (var setting in repo.Settings)
            {
                var from = record?.Values?.ContainsKey(setting.Name) == true ? "this workspace"
                    : loaded.Orchestration!.Bindings.Any(b => b.Setting == setting.Name) ? "the orchestration"
                    : loaded.Repos.Values.Where(r => r != repo && r.Exports.Any(e => e.Name == setting.Name)).Select(r => r.Name).FirstOrDefault() is { } exporter ? exporter
                    : setting.Default is not null ? "its default"
                    : "nothing yet";
                var value = plan.ToAsk.Any(a => a.Owner == repo.Name && a.Name == setting.Name) ? null : ValueOf(plan, repo.Name, setting);
                var shown = value is null ? null
                    : s.Json && !s.UnmaskSecrets ? SecretValues.Trim(value, SecretValues.IsSecret(plan, setting.Name))
                    : Shown.Value(plan, setting.Name, value, s);
                yield return new Row(repo.Name, setting.Name, setting.Type, shown, from);
            }
    }

    /// <summary>What the setting comes to, from an env var that's just the setting; else the default as written.</summary>
    static string ValueOf(Plan plan, string owner, SettingModel setting)
    {
        var whole = "{setting." + setting.Name + "}";
        var env = plan.Apps.Where(a => a.Owner == owner).SelectMany(a => a.Env).FirstOrDefault(e => e.Value.Template == whole);
        return env?.Value.Value ?? setting.Default ?? "";
    }
}
