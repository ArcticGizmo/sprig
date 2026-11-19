using System.Diagnostics;
using Sprig.Engine.Hosting;
using Sprig.Engine.Store;
using Spectre.Console;
using Velopack;
using Velopack.Sources;

namespace Sprig.Cli;

/// <summary>
/// Drives an in-place self-update from the terminal (<c>sprig update</c>). The GUI has its own
/// update path (About → Download &amp; install) via <c>Sprig.App/Updates/UpdateChecker.cs</c>; this
/// is the CLI equivalent, sharing the same feed but applying without launching the app.
/// </summary>
/// <remarks>
/// Applying swaps the whole <c>current</c> folder — where <c>sprig.exe</c> itself lives — so the
/// running process must exit before the swap. <see cref="UpdateManager.ApplyUpdatesAndExit"/> hands
/// off to <c>Update.exe</c> (which sits outside <c>current</c>), waits for our PID to die, then does
/// the swap. The desktop app and the host run from that folder too and hold its files open, and sprig
/// usually sits in the tray, so the update offers to quit them first, as the tray's Quit would.
/// </remarks>
static class CliUpdater
{
    // Mirrors Sprig.App's UpdateChecker — keep the env var and default feed in step with it.
    const string FeedEnvVar = "SPRIG_UPDATE_FEED";
    const string DefaultFeedUrl = "https://github.com/ArcticGizmo/sprig";

    public static int Run(CliContext cli, bool checkOnly, bool yes, bool json)
    {
        // Establishes the VelopackLocator so UpdateManager can find the install. The install-lifecycle
        // hooks are the GUI's job (it's the packaged mainExe); the CLI never gets those args, so a bare
        // Run() here is a locator-only no-op. Scoped to the update path — other commands never call it.
        VelopackApp.Build().Run();

        var feed = Environment.GetEnvironmentVariable(FeedEnvVar);
        var feedUrl = string.IsNullOrWhiteSpace(feed) ? DefaultFeedUrl : feed;
        var manager = string.IsNullOrWhiteSpace(feed)
            ? new UpdateManager(new GithubSource(DefaultFeedUrl, accessToken: null, prerelease: false))
            : new UpdateManager(feed);

        // Not installed via the installer (e.g. a dev build run from bin) — nothing Velopack can update.
        if (!manager.IsInstalled)
        {
            Console.WriteLine("not installed via the sprig installer — nothing to update");
            return 0;
        }

        var current = manager.CurrentVersion?.ToString() ?? "?";
        Console.WriteLine($"sprig v{current} (installed)");
        Console.WriteLine($"checking for updates from {feedUrl}…");
        Console.WriteLine("(this can take a few seconds while we query the release feed)");
        var update = manager.CheckForUpdatesAsync().GetAwaiter().GetResult();
        if (update is null)
        {
            Console.WriteLine($"up to date (v{current})");
            return 0;
        }

        var target = update.TargetFullRelease.Version.ToString();
        if (checkOnly)
        {
            Console.WriteLine($"update available: v{target} — you have v{current} (run 'sprig update' to install)");
            return 0;
        }

        // Asked before the download, so a no costs nothing; the apps keep running while it downloads.
        var app = AppInstance.IsRunning(cli.Paths);
        var alive = Alive(cli.Host);
        var pause = cli.Settings.Get().PauseOnQuit;
        if (TakedownQuestion(app, alive, pause) is { } question && !yes)
        {
            if (!cli.Interactive(json))
                throw new InvalidOperationException($"{question} Pass --yes to go ahead.");
            if (!cli.Ansi.Prompt(new ConfirmationPrompt(Markup.Escape(question + " Update now?")) { DefaultValue = false }))
            {
                Console.WriteLine("not updated");
                return 1;
            }
        }

        Console.WriteLine($"updating v{current} -> v{target}…");
        manager.DownloadUpdatesAsync(update, DownloadProgress).GetAwaiter().GetResult();
        Console.WriteLine();

        TakeDown(cli, app, pause);
        if (Holding() is { Count: > 0 } left)
            throw new InvalidOperationException(
                $"sprig is still running from this install ({string.Join(", ", left)}), perhaps for another store; quit it, then run 'sprig update' again");

        Console.WriteLine("applying update and exiting (files are swapped after this process quits)");
        // Hands off to Update.exe and terminates us. The app comes back in the tray if it was there.
        if (app) manager.ApplyUpdatesAndRestart(update.TargetFullRelease, ["--tray"]);
        else manager.ApplyUpdatesAndExit(update.TargetFullRelease);
        return 0; // unreachable — neither returns
    }

    /// <summary>
    /// What updating would take down, as a question to put first; null when there's nothing to lose. A host with
    /// nothing running goes without asking.
    /// </summary>
    internal static string? TakedownQuestion(bool app, IReadOnlyList<RunnerStatus> alive, bool pause)
    {
        var workspaces = alive.Select(r => r.Workspace).Distinct().Count();
        var going = alive.Count == 0 ? null
            : $"{alive.Count} app{(alive.Count == 1 ? "" : "s")} running in {workspaces} workspace{(workspaces == 1 ? "" : "s")}";
        var fate = pause ? "pausing them so each workspace can be resumed" : "stopping them";
        return (app, going) switch
        {
            (false, null) => null,
            (true, null) => "sprig is open (in the tray or a window). Updating quits it, then starts it again in the tray.",
            (true, _) => $"sprig is open with {going}. Updating quits it, {fate}, then starts it again in the tray.",
            (false, _) => $"sprig has {going}. Updating stops the sprig host, {fate}.",
        };
    }

    static IReadOnlyList<RunnerStatus> Alive(HostClient host)
        => host.TrySend(new HostRequest("list"))?.Runners?.Where(r => r.State.IsAlive() || r.State == RunnerState.Stopping).ToArray() ?? [];

    /// <summary>Quit the app as its tray would, then end any host it leaves, each waited for.</summary>
    static void TakeDown(CliContext cli, bool app, bool pause)
    {
        var limit = Supervisor.Grace + TimeSpan.FromSeconds(30);
        if (app && AppInstance.Tell(AppInstance.PipeName(cli.Paths), AppInstance.QuitMessage))
        {
            Console.WriteLine("quitting sprig…");
            WaitUntil(() => !AppInstance.IsRunning(cli.Paths), limit);
        }
        if (!cli.Host.Running) return;
        if (pause && cli.Host.TrySend(new HostRequest("list"))?.Runners is { Count: > 0 } runners)
        {
            // A pause not saved shouldn't hold up the update; the apps stop either way.
            try { cli.Workspaces.RememberRunning(runners); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        Console.WriteLine("stopping the sprig host…");
        cli.Host.StopAllAndShutdown();
        WaitUntil(() => !cli.Host.Running, limit);
    }

    static void WaitUntil(Func<bool> done, TimeSpan limit)
    {
        var clock = Stopwatch.StartNew();
        while (!done() && clock.Elapsed < limit) Thread.Sleep(200);
    }

    static void DownloadProgress(int percent) => Console.Write($"\rdownloading… {percent,3}%");

    /// <summary>The app or host processes still running from this install, which hold the files the swap replaces.
    /// Only those this install started count: a dev build runs from elsewhere and is no obstacle.</summary>
    static List<string> Holding()
    {
        var folder = Path.GetFullPath(AppContext.BaseDirectory);
        var found = new List<string>();
        foreach (var name in new[] { "sprig-gui", "sprig-host" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        if (process.MainModule?.FileName is { } file && file.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                            found.Add($"{name} pid {process.Id}");
                    }
                    // Never let a process we can't look at block an update.
                    catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
                }
            }
        }
        return found;
    }
}
