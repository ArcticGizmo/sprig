using System.ComponentModel;
using System.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Sprig.Cli.Commands;

[Description("Launch the sprig desktop app")]
public sealed class OpenCommand : Command<GlobalSettings>
{
    // Detaches (shell execute, no wait) so the terminal is handed straight back.
    protected override int Execute(CommandContext context, GlobalSettings s, CancellationToken cancellation)
    {
        var exe = LocateGui() ?? throw new FileNotFoundException(
            "could not find the sprig app (sprig-gui). Install sprig via the installer so both " +
            "ship together, or build Sprig.App alongside the CLI.");

        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        AnsiConsole.MarkupLine("opening [bold]sprig[/]…");
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
