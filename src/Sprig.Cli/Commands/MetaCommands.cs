using System.ComponentModel;
using System.Diagnostics;
using Sprig.Engine.Workspaces;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Sprig.Cli.Commands;

[Description("Launch the sprig desktop app, or bring a window of it to this desktop. Name a workspace to open its page, in a window of its own unless one on this desktop shows it already")]
public sealed class OpenCommand(CliContext cli) : Command<WorkspaceSettings>
{
    // Detaches (shell execute, no wait) so the terminal is handed straight back.
    protected override int Execute(CommandContext context, WorkspaceSettings s, CancellationToken cancellation)
    {
        var exe = LocateGui() ?? throw new FileNotFoundException(
            "could not find the sprig app (sprig-gui). Install sprig via the installer so both " +
            "ship together, or build Sprig.App alongside the CLI.");

        // Nothing named opens sprig as it is, even from inside a workspace's folder.
        WorkspaceRecord? record = null;
        if (!string.IsNullOrEmpty(s.Orchestration) || !string.IsNullOrEmpty(s.Workspace))
        {
            var left = s.Resolve(cli);
            if (left.Count > 0) throw new InvalidOperationException($"{s.Orchestration} has no workspace '{left[0]}'; see them with: sprig workspaces {s.Orchestration}");
            record = PlaceTarget.Record(cli, s.Orchestration, s.Workspace)
                     ?? throw new InvalidOperationException($"{s.Orchestration} has no workspace '{s.Workspace}'; see them with: sprig workspaces {s.Orchestration}");
        }

        // Asked before launching: a launch with the app running hands over to it and exits.
        var running = Sprig.Engine.Store.AppInstance.IsRunning(cli.Paths);
        var start = new ProcessStartInfo(exe) { UseShellExecute = true };
        if (record is not null)
        {
            start.ArgumentList.Add("--workspace");
            start.ArgumentList.Add(record.Key);
        }
        Process.Start(start);
        if (s.Json) CliOutput.Json(new { ok = true, app = running ? "focused" : "launched", workspace = record?.Key });
        else AnsiConsole.MarkupLine(record is null ? "opening [bold]sprig[/]…" : $"opening [bold]sprig[/] on {PlaceTarget.Describe(record)}…");
        return 0;
    }

    // Installed, sprig-gui sits beside sprig. In a dev build each project has its own bin folder with the same
    // bin/<config>/<tfm> layout, so swapping the project segment finds it.
    static string? LocateGui()
    {
        var exeName = OperatingSystem.IsWindows() ? "sprig-gui.exe" : "sprig-gui";
        var baseDir = AppContext.BaseDirectory;

        foreach (var dir in new[] { baseDir, baseDir.Replace("Sprig.Cli", "Sprig.App") })
        {
            var candidate = Path.Combine(dir, exeName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}

[Description("Install a newer release in place (--check only reports)")]
public sealed class UpdateCommand : Command<UpdateCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandOption("--check")]
        [Description("Only report whether an update is available")]
        public bool Check { get; set; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation) => CliUpdater.Run(s.Check);
}

/// <summary>A Unicode tick, or an ASCII '+' where the console can't draw one.</summary>
static class Glyph
{
    public static string Check(IAnsiConsole console) => console.Profile.Capabilities.Unicode ? "✔" : "+";
}
