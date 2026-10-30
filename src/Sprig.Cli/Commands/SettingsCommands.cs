using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Sprig.Core.Settings;

namespace Sprig.Cli.Commands;

// `sprig settings <sub>` (alias `config`) — the port-allocation policy and worktree layout.

[Description("Show port range, restricted ports and worktree layout")]
public sealed class SettingsShowCommand(CliContext cli) : Command<GlobalSettings>
{
    protected override int Execute(CommandContext context, GlobalSettings s, CancellationToken cancellation)
    {
        var current = cli.Settings.Get();
        // Project just the port-allocation policy and layout — the app-internal fields (changelog flags,
        // completed guides, …) aren't the CLI's contract and would only leak.
        if (s.Json)
        {
            CliOutput.Json(new
            {
                portRangeStart = current.PortRangeStart,
                portRangeEndExclusive = current.PortRangeEndExclusive,
                restrictedPorts = current.RestrictedPorts,
                worktreeLayout = SettingsSetCommand.LayoutName(current.WorktreeLayout),
            });
            return 0;
        }
        cli.Ansi.MarkupLine($"[dim]port range[/]        {current.PortRangeStart}..{current.PortRangeEndExclusive} [dim](end exclusive)[/]");
        cli.Ansi.MarkupLine($"[dim]restricted ports[/]  {(current.RestrictedPorts.Count == 0 ? "[dim]-[/]" : Markup.Escape(string.Join(", ", current.RestrictedPorts)))}");
        cli.Ansi.MarkupLine($"[dim]worktree layout[/]   {SettingsSetCommand.LayoutName(current.WorktreeLayout)}" +
            (current.WorktreeLayout == WorktreeLayout.Claude ? " [dim](<repo>/.claude/worktrees/<ws>, shortcut at <repo>--<ws>)[/]" : " [dim](<repo>--<ws>)[/]"));
        return 0;
    }
}

[Description("Update the port range, restricted ports and worktree layout")]
public sealed class SettingsSetCommand(CliContext cli) : Command<SettingsSetCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandOption("--start <port>")]
        [Description("Port range start (inclusive)")]
        public string? Start { get; set; }

        [CommandOption("--end <port>")]
        [Description("Port range end (exclusive)")]
        public string? End { get; set; }

        [CommandOption("--restrict <ports>")]
        [Description("Add restricted ports (comma-separated or repeated)")]
        public string[] Restrict { get; set; } = [];

        [CommandOption("--unrestrict <ports>")]
        [Description("Remove restricted ports (comma-separated or repeated)")]
        public string[] Unrestrict { get; set; } = [];

        [CommandOption("--worktree-layout <layout>")]
        [Description("Where new workspaces put worktrees: sibling (<repo>--<ws>) or claude (<repo>/.claude/worktrees/<ws>)")]
        public string? WorktreeLayout { get; set; }
    }

    internal static string LayoutName(WorktreeLayout layout) => layout == Core.Settings.WorktreeLayout.Claude ? "claude" : "sibling";

    static WorktreeLayout ParseLayout(string text) => text.Trim().ToLowerInvariant() switch
    {
        "sibling" => Core.Settings.WorktreeLayout.Sibling,
        "claude" => Core.Settings.WorktreeLayout.Claude,
        _ => throw new ArgumentException($"--worktree-layout must be 'sibling' or 'claude', not '{text}'"),
    };

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        var current = cli.Settings.Get();
        var updated = current.Clone();
        if (s.Start is not null) updated.PortRangeStart = CliFormat.ParsePort(s.Start, "--start");
        if (s.End is not null) updated.PortRangeEndExclusive = CliFormat.ParsePort(s.End, "--end");
        var ports = new SortedSet<int>(updated.RestrictedPorts);
        foreach (var p in CliFormat.SplitList(s.Restrict)) ports.Add(CliFormat.ParsePort(p, "--restrict"));
        foreach (var p in CliFormat.SplitList(s.Unrestrict)) ports.Remove(CliFormat.ParsePort(p, "--unrestrict"));
        updated.RestrictedPorts = ports.ToList();
        if (s.WorktreeLayout is not null) updated.WorktreeLayout = ParseLayout(s.WorktreeLayout);

        cli.Settings.Save(updated); // validates the range and restricted ports
        return CliOutput.Ok(s.Json, "settings updated", new
        {
            ok = true,
            portRangeStart = updated.PortRangeStart,
            portRangeEndExclusive = updated.PortRangeEndExclusive,
            restrictedPorts = updated.RestrictedPorts,
            worktreeLayout = LayoutName(updated.WorktreeLayout),
        });
    }
}
