using System.ComponentModel;
using Sprig.Core.Shell;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Sprig.Cli.Commands;

// `sprig shell shims` — write the PATH shims that let `sprig cd` move the shell in place (see ShellShims).
// It only writes files: putting the folder on PATH, ahead of the folder holding sprig.exe, is the caller's job
// (the examples sandbox does it for one session).

[Description("Write the sprig.ps1 / sprig.cmd / sprig shims that let 'sprig cd' move your shell")]
public sealed class ShellShimsCommand(CliContext cli) : Command<ShellShimsCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandOption("--dir <dir>")]
        [Description("Folder to write them to (must not be the folder holding sprig.exe)")]
        public string Dir { get; set; } = "";

        [CommandOption("--exe <path>")]
        [Description("The sprig.exe the shims run (default: this one)")]
        public string? Exe { get; set; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(s.Dir)) throw new ArgumentException("shell shims needs --dir <folder>");
        var exe = Path.GetFullPath(s.Exe ?? Environment.ProcessPath
            ?? throw new InvalidOperationException("can't tell where sprig.exe is; pass --exe"));
        var dir = Path.GetFullPath(s.Dir);
        if (string.Equals(Path.TrimEndingDirectorySeparator(dir), Path.GetDirectoryName(exe), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("the shims need a folder of their own: cmd would pick sprig.exe over sprig.cmd beside it");

        var written = ShellShims.Write(dir, exe);
        if (s.Json) { CliOutput.Json(new { ok = true, dir, exe, files = written }); return 0; }
        CliOutput.Success($"wrote {written.Count} shims to {dir}");
        cli.Ansi.MarkupLine($"  [dim]put that folder on PATH ahead of {Markup.Escape(Path.GetDirectoryName(exe)!)} to use them[/]");
        return 0;
    }
}

// `sprig shell install` — what the installer does on install and update, on demand. Its use is after allowing
// PowerShell scripts: the installer leaves sprig.ps1 out where they're blocked, and would otherwise only look
// again at the next update. It reports each shell's policy so a person can see which one is in the way.
[Description("Rewrite the installed shims and PATH entries, adding sprig.ps1 if PowerShell now allows it")]
public sealed class ShellInstallCommand(CliContext cli) : Command<ShellInstallCommand.Settings>
{
    public sealed class Settings : GlobalSettings;

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("shell install is Windows-only");
        var cliDir = Path.GetDirectoryName(Environment.ProcessPath)
            ?? throw new InvalidOperationException("can't tell where sprig.exe is");
        if (!ShellInstall.IsInstalled(cliDir))
            throw new InvalidOperationException(
                "shell install looks after an installed sprig; for a build of your own, use 'sprig shell shims --dir <folder>'");

        var result = ShellInstall.Ensure(cliDir);
        var powerShell = result.Shims.Any(f => Path.GetFileName(f) == "sprig.ps1");

        if (s.Json)
        {
            CliOutput.Json(new
            {
                ok = result.ShimError is null,
                dir = result.ShimDirectory,
                files = result.Shims,
                powerShell,
                policies = result.Policies.Select(p => new { shell = p.Shell, policy = p.Policy, runsLocalScripts = p.RunsLocalScripts }),
                pathChanged = result.PathChanged,
                error = result.ShimError,
            });
            return result.ShimError is null ? 0 : 1;
        }

        if (result.ShimError is { } error)
        {
            cli.Ansi.MarkupLine($"[red]couldn't write the shims:[/] {Markup.Escape(error)}");
            cli.Ansi.MarkupLine("  [dim]sprig.exe is still on PATH; 'sprig cd' will open a window instead of moving your shell[/]");
            return 1;
        }

        CliOutput.Success($"shims ready in {result.ShimDirectory}");
        foreach (var p in result.Policies)
        {
            var mark = p.RunsLocalScripts ? $"[green]{Glyph.Check(cli.Ansi)}[/]" : "[yellow]![/]";
            var policy = p.Policy.Length > 0 ? p.Policy : "unknown (didn't answer in time)";
            cli.Ansi.MarkupLine($"  {mark} {Markup.Escape(p.Shell)}  [dim]{Markup.Escape(policy)}[/]");
        }

        if (powerShell)
            cli.Ansi.MarkupLine("  'sprig cd' moves PowerShell and cmd in place.");
        else
        {
            cli.Ansi.MarkupLine("  [yellow]sprig.ps1 left out:[/] 'sprig cd' moves cmd in place, but opens a window from PowerShell.");
            cli.Ansi.MarkupLine("  To change that, allow local scripts in each shell marked [yellow]![/], then run 'sprig shell install' again:");
            foreach (var shell in result.Policies.Where(p => !p.RunsLocalScripts)
                         .Select(p => Path.GetFileNameWithoutExtension(p.Shell)).Distinct(StringComparer.OrdinalIgnoreCase))
                cli.Ansi.MarkupLine($"    [bold]{Markup.Escape(shell)} -NoProfile -Command \"Set-ExecutionPolicy -Scope CurrentUser RemoteSigned\"[/]");
            cli.Ansi.MarkupLine("  [dim]A policy set by your organisation takes precedence, and Set-ExecutionPolicy will say so.[/]");
        }

        if (result.PathChanged)
            cli.Ansi.MarkupLine("  [dim]PATH changed: open a new terminal to pick it up[/]");
        return 0;
    }
}
