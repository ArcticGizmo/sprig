using System.ComponentModel;
using Sprig.Core.Stacks;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Sprig.Cli.Commands;

// `sprig pool <sub>` — check out and manage the pooled workspaces built from a stack. M2 ships the
// read-only status view; checkout/release land in M3.

[Description("Show a stack's pool: its workspaces and how many of maxSlots are claimed")]
public sealed class PoolStatusCommand(CliContext cli) : Command<PoolStatusCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "<stack>")]
        [Description("Stack name")]
        public string Stack { get; set; } = "";
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        var status = cli.Pools.Status(s.Stack);
        if (s.Json) { CliOutput.Json(status); return 0; }

        var console = cli.Ansi;
        console.MarkupLine(
            $"[bold]{Markup.Escape(status.Stack)}[/] pool  " +
            $"[dim]{status.ClaimedCount}/{status.MaxSlots} claimed[/]" +
            (status.Workspaces.Count > 0
                ? $" [dim]({status.FreeCount} free, {status.Headroom} unbuilt)[/]"
                : ""));

        if (status.Workspaces.Count == 0)
        {
            console.MarkupLine("[dim]no workspaces yet — check one out with[/] [bold]sprig pool checkout " +
                $"{Markup.Escape(status.Stack)}[/] [dim](lands in M3)[/]");
            return 0;
        }

        var table = CliFormat.Table("WORKSPACE", "STATE", "LABEL", "LAST USED", "PORTS");
        foreach (var w in status.Workspaces)
            table.AddRow(
                Markup.Escape(w.Workspace),
                w.Claimed ? "[green]claimed[/]" : "[dim]free[/]",
                Markup.Escape(w.Label ?? "-"),
                Markup.Escape(w.LastUsedAt?.ToString("u") ?? "-"),
                Markup.Escape(CliFormat.Ports(w.Ports)));
        console.Write(table);
        return 0;
    }
}
