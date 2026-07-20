using System;
using System.Threading.Tasks;
using Velopack;

namespace Sprig.App.Updates;

/// <summary>
/// Checks a release feed for a newer version and reports it — <b>notification only</b>. It never
/// downloads or applies an update; that is a deliberate, user-driven action left for later.
/// </summary>
/// <remarks>
/// The feed is read from the <c>SPRIG_UPDATE_FEED</c> environment variable (a directory path or a
/// URL). If it is unset, or the app wasn't installed via Velopack (e.g. run from the build output),
/// the check is a no-op. Any failure is swallowed — a flaky feed must never block launch.
/// </remarks>
public static class UpdateChecker
{
    public const string FeedEnvVar = "SPRIG_UPDATE_FEED";

    /// <summary>
    /// Returns a human-readable notice (e.g. "Update available: v0.2.0 — you have v0.1.0") when a
    /// newer release exists, or null when up to date / not applicable.
    /// </summary>
    public static async Task<string?> CheckAsync()
    {
        var feed = Environment.GetEnvironmentVariable(FeedEnvVar);
        if (string.IsNullOrWhiteSpace(feed))
            return null;

        try
        {
            var manager = new UpdateManager(feed);
            if (!manager.IsInstalled)
                return null; // running uninstalled (dev build) — nothing to update

            var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (update is null)
                return null; // already on the latest

            var available = update.TargetFullRelease.Version;
            return $"Update available: v{available} — you have v{manager.CurrentVersion}";
        }
        catch
        {
            return null; // never let an update check break the app
        }
    }
}
