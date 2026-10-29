using System.ComponentModel;
using System.Diagnostics;
using Sprig.Core.Workspaces;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Sprig.Cli.Commands;

// `sprig ws urls` / `sprig ws browse` — where to go in a workspace. The URLs come from each capability's
// declared `open` output; probing is opt-in and one-shot, never a poll.

[Description("List a workspace's entry-point URLs (--probe checks each one is up)")]
public sealed class UrlsCommand(CliContext cli) : Command<UrlsCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandOption("--probe")]
        [Description("GET each declared probe once and report up/down")]
        public bool Probe { get; set; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        var workspace = WorkspacePrompt.Resolve(cli, s.Workspace, s.Json);
        if (workspace is null) return 0; // interactive cancel (ESC)
        var record = cli.Workspaces.Get(workspace) ?? throw new ArgumentException($"unknown workspace '{workspace}'");
        var urls = cli.Workspaces.Urls(record);
        var states = s.Probe ? UrlProbes.Run(urls, cancellation) : null;

        if (s.Json)
        {
            CliOutput.Json(urls.Select(u => new
            {
                repo = u.Repo, module = u.Module, capability = u.Capability, url = u.Url, probe = u.Probe,
                state = states is not null && states.TryGetValue(u, out var st) ? st.ToString().ToLowerInvariant() : null,
            }));
            return 0;
        }

        if (urls.Count == 0)
        {
            CliOutput.Muted($"'{workspace}' declares no entry points — add \"open\" to a provided capability in .sprig.json");
            return 0;
        }

        var table = s.Probe ? CliFormat.Table("CAPABILITY", "URL", "STATE") : CliFormat.Table("CAPABILITY", "URL");
        foreach (var u in urls)
        {
            if (!s.Probe) { table.AddRow(Markup.Escape(u.Capability), Markup.Escape(u.Url)); continue; }
            table.AddRow(Markup.Escape(u.Capability), Markup.Escape(u.Url), states!.TryGetValue(u, out var st)
                ? st == ProbeState.Up ? "[green]up[/]" : "[red]down[/]"
                : "[dim]no probe[/]");
        }
        cli.Ansi.Write(table);
        return 0;
    }
}

[Description("Open a workspace's entry-point URL in the browser")]
public sealed class BrowseCommand(CliContext cli) : Command<BrowseCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandArgument(1, "[capability]")]
        [Description("Which capability's URL (optional when there is only one)")]
        public string? Capability { get; set; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        var workspace = WorkspacePrompt.Resolve(cli, s.Workspace, s.Json);
        if (workspace is null) return 0; // interactive cancel (ESC)
        var record = cli.Workspaces.Get(workspace) ?? throw new ArgumentException($"unknown workspace '{workspace}'");
        var urls = cli.Workspaces.Urls(record);
        if (urls.Count == 0)
            throw new ArgumentException($"'{workspace}' declares no entry points — add \"open\" to a provided capability in .sprig.json");

        WorkspaceUrl? target;
        if (s.Capability is { } cap)
            target = urls.FirstOrDefault(u => u.Capability == cap)
                ?? throw new ArgumentException($"'{workspace}' has no entry point '{cap}' (it has: {string.Join(", ", urls.Select(u => u.Capability))})");
        else if (urls.Count == 1)
            target = urls[0];
        else if (!s.Json && Interactivity.TerminalAttached)
        {
            var pick = Term.SelectOne(Term.Create(), "Open which [green]entry point[/]? [grey](esc cancels)[/]",
                urls.Select(u => u.Capability));
            if (pick is null) { cli.Ansi.MarkupLine("[yellow]cancelled[/]"); return 0; }
            target = urls.First(u => u.Capability == pick);
        }
        else
            throw new ArgumentException($"'{workspace}' has several entry points — name one: {string.Join(", ", urls.Select(u => u.Capability))}");

        Process.Start(new ProcessStartInfo(target.Url) { UseShellExecute = true });
        return CliOutput.Ok(s.Json, $"opened {target.Url}",
            new { ok = true, workspace, capability = target.Capability, url = target.Url });
    }
}

static class UrlProbes
{
    // Probe every URL that declares one, in parallel, so a slow or dead service costs one timeout, not one each.
    public static IReadOnlyDictionary<WorkspaceUrl, ProbeState> Run(IReadOnlyList<WorkspaceUrl> urls, CancellationToken cancel)
    {
        var probed = urls.Where(u => u.Probe is not null).ToList();
        var results = Task.WhenAll(probed.Select(u => WorkspaceService.ProbeAsync(u.Probe!, cancel: cancel))).GetAwaiter().GetResult();
        return probed.Zip(results).ToDictionary(p => p.First, p => p.Second);
    }
}
