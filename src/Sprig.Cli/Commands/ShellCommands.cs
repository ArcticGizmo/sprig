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
