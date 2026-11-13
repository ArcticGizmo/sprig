using System.Diagnostics;
using Sprig.Engine.Hosting;
using Sprig.Engine.Workspaces;
using Spectre.Console;

namespace Sprig.Cli.Commands;

/// <summary>
/// <c>sprig up --run</c> after the infra is up: start the workspace's apps, follow their logs as one stream, and stop the
/// apps on Ctrl+C. The infra stays up, since it holds data and is slow to bring back. A second Ctrl+C leaves at once;
/// the host has the stop requests by then and finishes them on its own.
/// </summary>
sealed class AppFollow(CliContext cli, WorkspaceRecord record, IReadOnlyList<RunnerSpec> specs)
{
    public int Run()
    {
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            if (stop.IsCancellationRequested) return;
            e.Cancel = true;
            stop.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            return Run(stop.Token, Console.Out, colour: !Console.IsOutputRedirected);
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    /// <param name="stopRequested">Ctrl+C: stop the apps and return.</param>
    /// <param name="output">Where the apps' lines go; the same place <see cref="CliContext.Ansi"/> writes.</param>
    internal int Run(CancellationToken stopRequested, TextWriter output, bool colour)
    {
        StartCommand.Send(cli, specs, []);
        var follower = new LogFollower(cli.Ansi, output, colour);
        var labels = LogFollower.Labels(specs.Select(x => x.Name));
        foreach (var spec in specs) follower.Add(labels[spec.Name], HostProtocol.LogFile(cli.Paths, record.Key, spec.Name));

        var names = UpCommand.Words(record);
        cli.Ansi.MarkupLine($"[dim]Following {specs.Count} apps. Ctrl+C stops them; the infra keeps running (sprig down {Markup.Escape(names)} stops it).[/]");

        if (FollowUntilStoppedOrDone(follower, stopRequested) is { } done) return done;
        return StopApps(follower);
    }

    /// <summary>Null when Ctrl+C was pressed; otherwise every app ended on its own, and this is the exit code.</summary>
    int? FollowUntilStoppedOrDone(LogFollower follower, CancellationToken stop)
    {
        var watched = specs.Select(x => x.Name).ToHashSet();
        var lastLook = Stopwatch.StartNew();
        while (!stop.IsCancellationRequested)
        {
            follower.Poll();
            if (lastLook.Elapsed > TimeSpan.FromSeconds(1))
            {
                lastLook.Restart();
                var mine = (cli.Host.TrySend(new HostRequest("list"))?.Runners ?? [])
                    .Where(r => r.Workspace == record.Key && watched.Contains(r.Name)).ToArray();
                if (!mine.Any(r => r.State.IsAlive() || r.State == RunnerState.Stopping))
                {
                    follower.Poll();
                    var failed = mine.Where(r => r.State is RunnerState.Failed or RunnerState.Exited).ToArray();
                    if (failed.Length > 0)
                    {
                        cli.Ansi.MarkupLine($"[red]Every app has ended[/]; these didn't succeed: {Markup.Escape(string.Join(", ", failed.Select(r => r.Name)))}");
                        return 1;
                    }
                    // Gone from the list too: a whole-workspace stop forgets its runners.
                    var stoppedElsewhere = mine.Length < watched.Count || mine.Any(r => r.State == RunnerState.Stopped);
                    cli.Ansi.MarkupLine(stoppedElsewhere ? "[dim]The apps were stopped.[/]" : "[dim]Every app has finished.[/]");
                    return 0;
                }
            }
            stop.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(150));
        }
        return null;
    }

    int StopApps(LogFollower follower)
    {
        follower.Poll();
        cli.Ansi.WriteLine();
        cli.Ansi.MarkupLine("[yellow]Stopping the apps…[/] [dim](Ctrl+C again to leave them stopping in the background)[/]");
        var stopping = Task.Run(() => cli.Host.StopAndWait(record.Key));
        while (!stopping.Wait(TimeSpan.FromMilliseconds(150))) follower.Poll();
        follower.Poll();

        foreach (var r in stopping.Result.OrderBy(r => r.Name))
        {
            if (r.Error is { } why) cli.Ansi.MarkupLine($"[yellow]{Markup.Escape(r.Name)}[/]  {Markup.Escape(why)}");
            else cli.Ansi.MarkupLine($"[green]{Glyph.Check(cli.Ansi)}[/] {Markup.Escape(r.Name)} stopped");
        }
        cli.Ansi.MarkupLine($"[dim]The infra is still up. Stop it with: sprig down {Markup.Escape(UpCommand.Words(record))}[/]");
        return 0;
    }
}
