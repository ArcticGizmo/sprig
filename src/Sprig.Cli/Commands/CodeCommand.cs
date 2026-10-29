using System.ComponentModel;
using System.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Sprig.Cli.Commands;

// `sprig code` — open a workspace in VS Code as one window holding every repo's worktree, titled with what
// the workspace is for and tinted with its colour. The .code-workspace lives in the central store, so the
// repos never see it.

[Description("Open a workspace in VS Code, titled and coloured so its window stands out")]
public sealed class CodeCommand(CliContext cli) : Command<CodeCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandOption("--print")]
        [Description("Write the .code-workspace and print its path without opening VS Code")]
        public bool Print { get; set; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        var workspace = WorkspacePrompt.Resolve(cli, s.Workspace, s.Json);
        if (workspace is null) return 0; // interactive cancel (ESC)
        var record = cli.Workspaces.Get(workspace) ?? throw new ArgumentException($"unknown workspace '{workspace}'");
        var file = cli.Workspaces.WriteCodeWorkspace(record);

        if (s.Print) { Console.WriteLine(file); return 0; }
        // `code` is a .cmd shim on Windows, so it needs the shell to resolve it.
        Process.Start(new ProcessStartInfo("code", $"\"{file}\"") { UseShellExecute = true });
        return CliOutput.Ok(s.Json, $"opened '{workspace}' in VS Code", new { ok = true, workspace, file });
    }
}
