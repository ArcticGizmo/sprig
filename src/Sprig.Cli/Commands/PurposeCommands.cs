using System.ComponentModel;
using Sprig.Core.Store;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Sprig.Cli.Commands;

// `sprig ws label` / `sprig ws note` — the purpose layer a claimer owns. Both can run at any time (claimed or
// not) and only ever rewrite the record: no git, no infra. With no text they print the current value, so a
// script can read one field without parsing `info`.

public sealed class PurposeSettings : WorkspaceSettings
{
    [CommandArgument(1, "[text]")]
    [Description("The new text (omit to show the current value)")]
    public string? Text { get; set; }

    [CommandOption("--clear")]
    [Description("Remove the current value")]
    public bool Clear { get; set; }
}

[Description("Show or set what a workspace is for (its headline in ls, info and the app)")]
public sealed class LabelCommand(CliContext cli) : Command<PurposeSettings>
{
    protected override int Execute(CommandContext context, PurposeSettings s, CancellationToken cancellation)
        => Purpose.Run(cli, s, "label", r => r.Label, cli.Workspaces.SetLabel);
}

[Description("Show or set a workspace's status note (shown beneath its label)")]
public sealed class NoteCommand(CliContext cli) : Command<PurposeSettings>
{
    protected override int Execute(CommandContext context, PurposeSettings s, CancellationToken cancellation)
        => Purpose.Run(cli, s, "note", r => r.Note, cli.Workspaces.SetNote);
}

static class Purpose
{
    public static int Run(CliContext cli, PurposeSettings s, string field,
        Func<InstanceRecord, string?> read, Func<string, string?, InstanceRecord> write)
    {
        if (s.Clear && s.Text is not null)
            throw new ArgumentException($"pass either new {field} text or --clear, not both");

        var workspace = WorkspacePrompt.Resolve(cli, s.Workspace, s.Json);
        if (workspace is null) return 0; // interactive cancel (ESC)

        if (!s.Clear && s.Text is null)
        {
            var record = cli.Workspaces.Get(workspace) ?? throw new ArgumentException($"unknown workspace '{workspace}'");
            var current = read(record);
            if (s.Json) CliOutput.Json(new Dictionary<string, object?> { ["workspace"] = workspace, [field] = current });
            else if (current is null) CliOutput.Muted($"'{workspace}' has no {field}");
            else Console.WriteLine(current);
            return 0;
        }

        var value = read(write(workspace, s.Clear ? null : s.Text));
        return CliOutput.Ok(s.Json,
            value is null ? $"cleared the {field} on '{workspace}'" : $"{field} for '{workspace}': {value}",
            new Dictionary<string, object?> { ["ok"] = true, ["workspace"] = workspace, [field] = value });
    }
}
