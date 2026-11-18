using System.ComponentModel;
using Sprig.Engine.Live;
using Sprig.Engine.Workspaces;
using Spectre.Console.Cli;

namespace Sprig.Cli.Commands;

[Description("Follow what workspaces and their apps are doing, as it changes. With --json, one line per change, for other tools")]
public sealed class WatchCommand(CliContext cli) : Command<WatchCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "[id]")]
        [Description("Only this repo's or orchestration's workspaces (default: every workspace)")]
        public string? Orchestration { get; set; }

        [CommandArgument(1, "[workspace]")]
        [Description("Only this workspace: its branch, slot number or folder name")]
        public string? Workspace { get; set; }

        [CommandOption("--usage")]
        [Description("Add each app's CPU and memory, every 2 s while it runs")]
        public bool Usage { get; set; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        var include = Scope(s);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        // A tool that runs watch keeps its stdin open; when it closes it, the tool has gone. With no stdin at all (a
        // terminal, or a parent that gave none) there's nothing to tell by, so only Ctrl+C or a kill ends it.
        if (Console.IsInputRedirected) _ = Task.Run(() => WaitForEndOfInput(stop), CancellationToken.None);
        var write = s.Json ? (Action<object>)(line => Console.Out.WriteLine(WatchLines.Serialize(line))) : new WatchText(Console.Out).Write;
        try
        {
            new LiveStatus(cli.Paths, cli.Host).RunAsync(include, s.Usage, line =>
            {
                try
                {
                    write(line);
                    Console.Out.Flush();
                }
                // Whoever was reading has gone.
                catch (IOException) { stop.Cancel(); }
            }, stop.Token).GetAwaiter().GetResult();
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
        return 0;
    }

    static void WaitForEndOfInput(CancellationTokenSource stop)
    {
        try
        {
            while (Console.In.ReadLine() is not null) { }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        try { stop.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Every workspace, or those the arguments name, including ones made after watch starts.</summary>
    Func<WorkspaceRecord, bool> Scope(Settings s)
    {
        if (string.IsNullOrEmpty(s.Orchestration))
        {
            if (!string.IsNullOrEmpty(s.Workspace)) throw new InvalidOperationException("name the repo or orchestration before the workspace");
            return _ => true;
        }
        var id = s.Orchestration;
        bool Mine(WorkspaceRecord r) => string.Equals(r.Orchestration, id, StringComparison.OrdinalIgnoreCase);
        if (!cli.Workspaces.List().Any(Mine) && cli.Orchestrations.Resolve(id) is null)
            throw new InvalidOperationException($"no repo or orchestration '{id}'");
        if (string.IsNullOrEmpty(s.Workspace)) return Mine;
        var found = cli.Workspaces.List().Where(Mine).FirstOrDefault(r => WorkspaceService.Names(r, s.Workspace))
            ?? throw new InvalidOperationException($"no workspace '{s.Workspace}' in {id}");
        return r => r.Key == found.Key;
    }
}

/// <summary>The lines as a person reads them: a list to start with, then one line per change, timed.</summary>
sealed class WatchText(TextWriter output)
{
    public void Write(object line)
    {
        var now = DateTime.Now.ToString("HH:mm:ss");
        switch (line)
        {
            case WatchSnapshot snapshot:
                if (!snapshot.Host.Running) output.WriteLine("The sprig host isn't running, so no apps are.");
                if (snapshot.Workspaces.Count == 0) output.WriteLine("No workspaces.");
                foreach (var w in snapshot.Workspaces)
                {
                    output.WriteLine($"{w.Key} ({w.Label})  {w.State}");
                    foreach (var app in (w.Apps ?? []).Where(a => a.State != WatchLines.Off)) output.WriteLine($"  {app.Name}  {Describe(app)}");
                }
                break;
            case WatchHost:
                output.WriteLine($"{now}  the sprig host has stopped");
                break;
            case WatchWorkspace w:
                output.WriteLine($"{now}  {w.Key} ({w.Label})  {w.State}");
                break;
            case WatchApp app:
                output.WriteLine($"{now}  {app.Workspace}  {app.Name}  {Describe(app)}");
                break;
            case WatchWorkspaceRemoved removed:
                output.WriteLine($"{now}  {removed.Key} removed");
                break;
            case WatchAppRemoved removed:
                output.WriteLine($"{now}  {removed.Workspace}  {removed.Name} removed");
                break;
        }
    }

    static string Describe(WatchApp app)
    {
        var text = app.State switch
        {
            "ready" when app.Port is { } port => $"ready on http://localhost:{port}",
            "building" when app.Doing is { } doing => $"building ({doing})",
            _ => app.State,
        };
        if (app.Error is { } error) text += $": {error}";
        if (app.Usage is { } usage) text += $"  [{Sprig.Engine.Hosting.ResourceUsage.CpuText(usage.Cpu)}, {Sprig.Engine.Hosting.ResourceUsage.MemoryText(usage.Memory)}]";
        return text;
    }
}
