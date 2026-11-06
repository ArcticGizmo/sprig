using System.ComponentModel;
using Sprig.Engine.Authoring;
using Sprig.Engine.Definitions;
using Sprig.Engine.Store;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Sprig.Cli.Commands;

static class RepoList
{
    public static StoredDefinitions Stored(CliContext cli) => new(cli.Paths);

    public static OrchestrationAuthoring Authoring(CliContext cli) => new(Stored(cli), cli.Orchestrations, cli.Trust);

    public static object Payload(RepoEntry e) => new { ok = true, id = e.Id, folder = e.Folder, inRepo = e.InRepo, definition = e.DefinitionFile };

    public static object Row(RepoEntry e) => new { id = e.Id, folder = e.Folder, inRepo = e.InRepo, definition = e.DefinitionFile };

    public static string Kind(RepoEntry e) => e.InRepo ? "in repo" : "in sprig";
}

[Description("Put a repo folder on the Repos list, so orchestrations can use it with UseRepo(\"id\")")]
public sealed class RepoAddCommand(CliContext cli) : Command<RepoAddCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "<folder>")]
        [Description("The repo's folder. Its own sprig.cs is used if it has one; otherwise sprig drafts one and keeps it")]
        public string Folder { get; set; } = "";

        [CommandOption("--id <id>")]
        [Description("The ID orchestrations use for it (default: the folder's name, with -2, -3… when taken)")]
        public string? Id { get; set; }

        [CommandOption("--in-sprig")]
        [Description("Keep its definition in sprig: a copy of the folder's sprig.cs if it has one, else a draft")]
        public bool InSprig { get; set; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        var folder = LongPath.Of(s.Folder);
        var stored = RepoList.Stored(cli);

        // Adding the same folder again is a no-op, so a setup script can run twice.
        if (!s.InSprig && File.Exists(Path.Combine(folder, StoredDefinitions.RepoFileName))
            && stored.InRepoFor(folder) is { } existing && (s.Id is null || s.Id == existing.Id))
            return CliOutput.Ok(s.Json, $"'{existing.Id}' is already on the list, using {existing.DefinitionFile}", RepoList.Payload(existing));

        var entry = RepoList.Authoring(cli).AddRepo(folder, s.Id, s.InSprig ? false : null);
        var how = entry.InRepo ? $"using {entry.DefinitionFile}" : $"with its definition kept in sprig at {entry.DefinitionFile}";
        return CliOutput.Ok(s.Json, $"added '{entry.Id}' ({RepoList.Kind(entry)}), {how}", RepoList.Payload(entry));
    }
}

[Description("Copy an in-repo entry's sprig.cs into sprig as a new ID, to try changes without touching the repo")]
public sealed class RepoCopyCommand(CliContext cli) : Command<RepoCopyCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "<id>")]
        [Description("The in-repo entry to copy. It and the orchestrations using it are left as they are")]
        public string Id { get; set; } = "";

        [CommandOption("--id <new-id>")]
        [Description("The new entry's ID (default: the folder's name with -sprig after it)")]
        public string? NewId { get; set; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        var stored = RepoList.Stored(cli);
        var source = stored.Repo(s.Id) ?? throw new InvalidOperationException($"'{s.Id}' isn't on the Repos list");
        var authoring = RepoList.Authoring(cli);

        // An existing copy is kept rather than refreshed, so re-running never discards edits made in sprig.
        if (s.NewId is { } wanted && stored.Repo(wanted) is { InRepo: false } kept && SameFolder(kept.Folder, source.Folder))
            return CliOutput.Ok(s.Json, $"'{kept.Id}' is already kept in sprig at {kept.DefinitionFile}", RepoList.Payload(kept));

        var copy = authoring.CopyIntoSprig(s.Id, s.NewId ?? authoring.SuggestId(source, inRepo: false));
        return CliOutput.Ok(s.Json, $"copied '{s.Id}' into sprig as '{copy.Id}', at {copy.DefinitionFile}", RepoList.Payload(copy));
    }

    static bool SameFolder(string a, string b) => string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

[Description("List the repos on the Repos list")]
public sealed class RepoLsCommand(CliContext cli) : Command<GlobalSettings>
{
    protected override int Execute(CommandContext context, GlobalSettings s, CancellationToken cancellation)
    {
        var entries = RepoList.Stored(cli).Repos();
        if (s.Json) { CliOutput.Json(entries.Select(RepoList.Row)); return 0; }
        if (entries.Count == 0)
        {
            cli.Ansi.MarkupLine("[dim]No repos on the list. Add one with: sprig repo add <folder>[/]");
            return 0;
        }
        var table = new Table().Border(TableBorder.Rounded).AddColumns("ID", "Kind", "Folder", "Definition");
        foreach (var e in entries)
            table.AddRow(Markup.Escape(e.Id), RepoList.Kind(e),
                Directory.Exists(e.Folder) ? Markup.Escape(e.Folder) : $"[red]{Markup.Escape(e.Folder)} (missing)[/]",
                File.Exists(e.DefinitionFile) ? Markup.Escape(e.DefinitionFile) : $"[red]{Markup.Escape(e.DefinitionFile)} (missing)[/]");
        cli.Ansi.Write(table);
        return 0;
    }
}

[Description("Take a repo off the Repos list (the folder and its own sprig.cs are left alone)")]
public sealed class RepoRmCommand(CliContext cli) : Command<RepoRmCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "<id>")]
        public string Id { get; set; } = "";

        [CommandOption("--force")]
        [Description("Remove it even though orchestrations use it (they stop planning until it's back)")]
        public bool Force { get; set; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        if (RepoList.Stored(cli).Repo(s.Id) is null) throw new InvalidOperationException($"'{s.Id}' isn't on the Repos list");
        var authoring = RepoList.Authoring(cli);
        var usedBy = authoring.UsedBy(s.Id);
        if (usedBy.Count > 0 && !s.Force)
            throw new InvalidOperationException($"'{s.Id}' is used by {string.Join(", ", usedBy)}. Pass --force to remove it anyway");
        authoring.DeleteRepo(s.Id);
        return CliOutput.Ok(s.Json, $"took '{s.Id}' off the list", new { ok = true, id = s.Id, usedBy });
    }
}
