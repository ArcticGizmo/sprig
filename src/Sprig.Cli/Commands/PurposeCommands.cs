using System.ComponentModel;
using Sprig.Core.Store;
using Sprig.Core.Workspaces;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Sprig.Cli.Commands;

// `sprig ws label` / `note` / `tag` / `untag` — the purpose layer a claimer owns. All can run at any time
// (claimed or not) and only ever rewrite the record: no git, no infra. With nothing to set they print the
// current value, so a script can read one field without parsing `info`.

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

[Description("Show or set a workspace's colour (#rrggbb), used to tint its VS Code title bar")]
public sealed class ColourCommand(CliContext cli) : Command<PurposeSettings>
{
    protected override int Execute(CommandContext context, PurposeSettings s, CancellationToken cancellation)
    {
        if (s.Clear && s.Text is not null)
            throw new ArgumentException("pass either a colour or --clear, not both");
        var workspace = WorkspacePrompt.Resolve(cli, s.Workspace, s.Json);
        if (workspace is null) return 0; // interactive cancel (ESC)

        var record = !s.Clear && s.Text is null
            ? cli.Workspaces.Get(workspace) ?? throw new ArgumentException($"unknown workspace '{workspace}'")
            : cli.Workspaces.SetColour(workspace, s.Clear ? null : s.Text);
        var colour = WorkspaceColour.For(record);
        var isDefault = record.Colour is null;

        if (s.Json) { CliOutput.Json(new { ok = true, workspace, colour, isDefault }); return 0; }
        cli.Ansi.MarkupLine($"[{colour}]██[/] {colour}{(isDefault ? " [dim](the slot's default)[/]" : "")}");
        return 0;
    }
}

[Description("Add or change tags on a workspace (key=value, repeatable); with none, list its tags")]
public sealed class TagCommand(CliContext cli) : Command<TagCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "<workspace>")]
        [Description("Workspace name")]
        public string Workspace { get; set; } = "";

        [CommandArgument(1, "[tags]")]
        [Description("Tags to set, as <namespace>.<name>=value")]
        public string[] Tags { get; set; } = [];
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        var record = s.Tags.Length == 0
            ? cli.Workspaces.Get(s.Workspace) ?? throw new ArgumentException($"unknown workspace '{s.Workspace}'")
            : cli.Workspaces.Tag(s.Workspace, WorkspaceTags.ParsePairs(s.Tags));
        return Purpose.ShowTags(s.Json, record);
    }
}

[Description("Remove tags from a workspace by key")]
public sealed class UntagCommand(CliContext cli) : Command<UntagCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "<workspace>")]
        [Description("Workspace name")]
        public string Workspace { get; set; } = "";

        [CommandArgument(1, "<keys>")]
        [Description("Tag keys to remove")]
        public string[] Keys { get; set; } = [];
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
        => Purpose.ShowTags(s.Json, cli.Workspaces.Tag(s.Workspace, new Dictionary<string, string>(), s.Keys));
}

static class Purpose
{
    public static int ShowTags(bool json, InstanceRecord record)
    {
        if (json) { CliOutput.Json(new { ok = true, workspace = record.Workspace, tags = record.Tags }); return 0; }
        if (record.Tags.Count == 0) { CliOutput.Muted($"'{record.Workspace}' has no tags"); return 0; }
        foreach (var (k, v) in record.Tags.OrderBy(t => t.Key, StringComparer.Ordinal))
            Console.WriteLine($"{k}={v}");
        return 0;
    }

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
