using System.ComponentModel;
using Sprig.Engine.Definitions;
using Sprig.Engine.Workspaces;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Sprig.Cli.Commands;

static class LinkTarget
{
    public const string WorkspaceHelp = "Its workspace: a branch, slot number or folder name (default: main, the repo where it's checked out)";

    /// <summary>The target's ID as its workspaces are kept under, its definition, and its workspace.</summary>
    internal static (string Id, string Path, string Workspace) Find(CliContext cli, string word, string? workspace)
    {
        var path = cli.Orchestrations.Resolve(word)
            ?? throw new InvalidOperationException($"no orchestration or repo named '{word}'");
        var id = new StoredDefinitions(cli.Paths).RepoIdOf(path) ?? cli.Orchestrations.IdFor(path);
        if (workspace is null or WorkspaceRecord.MainName) return (id, path, WorkspaceRecord.MainName);
        var found = cli.Workspaces.Find(id, workspace) ?? throw new InvalidOperationException($"{id} has no workspace '{workspace}'");
        return (id, path, found.Name);
    }
}

[Description("Have a workspace take its inputs from another workspace's outputs of the same name, over the repos' defaults. Kept for next time")]
public sealed class LinkCommand(CliContext cli) : Command<LinkCommand.Settings>
{
    public sealed class Settings : ValuesSettings
    {
        [CommandArgument(0, "<target>")]
        [Description("The repo or orchestration ID to take inputs from")]
        public string To { get; set; } = "";

        [CommandArgument(1, "[target-workspace]")]
        [Description(LinkTarget.WorkspaceHelp)]
        public string? TargetWorkspace { get; set; }

        [CommandOption("--input <name=output>")]
        [Description("Point an input at an output of another name. Repeat for more")]
        public string[] Inputs { get; set; } = [];
    }

    protected override ValidationResult Validate(CommandContext context, Settings s)
        => SettingValues.Parse(s.Inputs, out _) is { } error ? ValidationResult.Error(error) : ValidationResult.Success();

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        SettingValues.Parse(s.Inputs, out var inputs);
        var (id, path, workspace) = LinkTarget.Find(cli, s.To, s.TargetWorkspace);
        var link = new WorkspaceLink(id, workspace, path, inputs.Count > 0 ? inputs : null);
        var label = cli.Workspaces.LinkLabel(link);
        return ValuesChange.Change(cli, s, $"check it out first: sprig checkout{{0}} -b <branch>",
            record => cli.Workspaces.Link(record.Orchestration, record.Name, link),
            record => $"{record.SlotName} linked to {label}; it takes its outputs next time it starts",
            record => $"{record.SlotName} linked to {label}, and its files rewritten",
            record => new { links = record.Links });
    }
}

[Description("Stop a workspace taking inputs from another workspace (or from every one), so the repos' defaults apply again")]
public sealed class UnlinkCommand(CliContext cli) : Command<UnlinkCommand.Settings>
{
    public sealed class Settings : ValuesSettings
    {
        [CommandArgument(0, "[target]")]
        [Description("The repo or orchestration ID it's linked to (default: every workspace it's linked to)")]
        public string? To { get; set; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        // A workspace has one link per repo or orchestration, so the ID is enough, and one that's no longer on the
        // Repos list can still be unlinked by the ID the link has.
        var id = s.To is { } word && cli.Orchestrations.Resolve(word) is { } path
            ? new StoredDefinitions(cli.Paths).RepoIdOf(path) ?? cli.Orchestrations.IdFor(path)
            : s.To;
        var label = id ?? "everything";
        return ValuesChange.Change(cli, s, "it has no links to take away",
            record => cli.Workspaces.Unlink(record.Orchestration, record.Name, id),
            record => $"{record.SlotName} unlinked from {label}; its defaults apply next time it starts",
            record => $"{record.SlotName} unlinked from {label}, and its files rewritten",
            record => new { links = record.Links ?? [] });
    }
}
