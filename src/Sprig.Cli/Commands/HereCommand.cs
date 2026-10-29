using System.ComponentModel;
using Sprig.Core.Git;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Sprig.Cli.Commands;

// `sprig here` — the reverse of `sprig path`: given a directory (default: where you are), name the workspace,
// repo and module that hold it, plus what the workspace is for and its ports. Statuslines, prompts, hooks and
// session monitors call it on every refresh, so it stays cheap (record lookup + file reads, no git process)
// and its --json is a contract: `schema` is bumped only for a breaking change, and fields are only added.

[Description("Show which workspace, repo and module the current directory is in")]
public sealed class HereCommand(CliContext cli) : Command<HereCommand.Settings>
{
    public const int Schema = 1;

    public sealed class Settings : GlobalSettings
    {
        [CommandOption("--path <dir>")]
        [Description("Resolve this directory instead of the current one")]
        public string? Path { get; set; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        var dir = s.Path ?? Directory.GetCurrentDirectory();
        // Outside a workspace is an ordinary answer for a statusline, but still exit 1 so callers can branch
        // on it and stay quiet.
        var location = cli.Workspaces.Locate(dir) ?? throw new ArgumentException($"not in a sprig workspace: {dir}");
        var (record, repo, module) = location;
        var heads = record.Repos.Select(r => (r.Name, Head: WorktreeHead.Read(r.WorktreePath))).ToList();

        if (s.Json)
        {
            CliOutput.Json(new
            {
                schema = Schema,
                workspace = record.Workspace,
                map = record.Map,
                repo = repo.Name,
                module,
                label = record.Label,
                note = record.Note,
                claimed = record.Claimed,
                claimedAt = record.ClaimedAt,
                claimBranch = record.Branch,
                repos = record.Repos.Select((r, i) => new
                {
                    name = r.Name,
                    branch = heads[i].Head?.Branch,
                    commit = heads[i].Head?.Commit,
                    detached = heads[i].Head?.IsDetached,
                    path = r.WorktreePath,
                }),
                ports = record.Ports,
            });
            return 0;
        }

        var console = cli.Ansi;
        var here = heads.First(h => h.Name == repo.Name).Head;
        console.MarkupLine($"[bold]{Markup.Escape(record.Workspace)}[/]  " +
            CliFormat.Purpose(record, CliFormat.Head(here)).Replace("\n", "\n  "));
        console.MarkupLine($"  [dim]repo[/] {Markup.Escape(repo.Name)}" +
            (module is null ? "" : $"  [dim]module[/] {Markup.Escape(module)}") +
            $"  [dim]branch[/] {CliFormat.Head(here)}");
        if (record.Ports.Count > 0)
            console.MarkupLine($"  [dim]ports[/] {Markup.Escape(CliFormat.Ports(record.Ports))}");
        return 0;
    }
}
