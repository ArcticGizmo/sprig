using System.Diagnostics;
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
/// the swap. If the desktop app is open it holds locks on those same files, so we refuse up front
/// rather than leave a half-applied install.
/// </remarks>
static class CliUpdater
{
    // Mirrors Sprig.App's UpdateChecker — keep the env var and default feed in step with it.
    const string FeedEnvVar = "SPRIG_UPDATE_FEED";
    const string DefaultFeedUrl = "https://github.com/ArcticGizmo/sprig";

    public static int Run(bool checkOnly)
    {
        // Establishes the VelopackLocator so UpdateManager can find the install. The install-lifecycle
        // hooks are the GUI's job (it's the packaged mainExe); the CLI never gets those args, so a bare
        // Run() here is a locator-only no-op. Scoped to the update path — other commands never call it.
        VelopackApp.Build().Run();

        var feed = Environment.GetEnvironmentVariable(FeedEnvVar);
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

        // The apply swaps files the running desktop app has open; refuse rather than fail mid-swap.
        if (DesktopAppRunning())
        {
            Console.Error.WriteLine("the sprig desktop app is open — close it first, then run 'sprig update' again");
            return 1;
        }

        Console.WriteLine($"updating v{current} -> v{target}…");
        manager.DownloadUpdatesAsync(update, DownloadProgress).GetAwaiter().GetResult();
        Console.WriteLine("\rdownloaded — applying update and exiting (files are swapped after this process quits)");
        manager.ApplyUpdatesAndExit(update.TargetFullRelease); // hands off to Update.exe and terminates us
        return 0; // unreachable — ApplyUpdatesAndExit does not return
    }

    static void DownloadProgress(int percent) => Console.Write($"\rdownloading… {percent,3}%");

    // The desktop app publishes as sprig-gui(.exe); a live instance locks the files the swap replaces.
    static bool DesktopAppRunning()
    {
        try { return Process.GetProcessesByName("sprig-gui").Length > 0; }
        catch { return false; } // never let a process-enumeration hiccup block an update
    }
}
