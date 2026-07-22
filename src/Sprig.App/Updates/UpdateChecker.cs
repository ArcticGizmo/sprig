using System;
using System.Threading.Tasks;
using Velopack;

namespace Sprig.App.Updates;

/// <summary>Outcome of an update check.</summary>
public enum UpdateAvailability
{
    /// <summary>No feed configured, or the app wasn't installed via Velopack (e.g. a dev build).</summary>
    NotApplicable,
    /// <summary>Already on the latest release.</summary>
    UpToDate,
    /// <summary>A newer release is available (see <see cref="UpdateCheckResult.AvailableVersion"/>).</summary>
    Available,
    /// <summary>The check failed (offline, bad feed, …). Never fatal.</summary>
    Failed,
}

/// <summary>
/// The result of an update check. Carries the Velopack handles needed to <b>apply</b> the update,
/// so the About page can download + install without re-running the check.
/// </summary>
public sealed class UpdateCheckResult
{
    public required UpdateAvailability Availability { get; init; }
    public string? CurrentVersion { get; init; }
    public string? AvailableVersion { get; init; }

    internal UpdateManager? Manager { get; init; }
    internal UpdateInfo? Info { get; init; }
}

/// <summary>
/// Checks a release feed for a newer version, and (from the About page) applies it. The launch-time
/// banner is notification-only; applying is an explicit, user-driven action.
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
    /// Full-detail check used by the About page. Never throws; returns <see cref="UpdateAvailability"/>
    /// plus the handles required to apply an available update.
    /// </summary>
    public static async Task<UpdateCheckResult> CheckDetailedAsync()
    {
        var feed = Environment.GetEnvironmentVariable(FeedEnvVar);
        if (string.IsNullOrWhiteSpace(feed))
            return new UpdateCheckResult { Availability = UpdateAvailability.NotApplicable };

        try
        {
            var manager = new UpdateManager(feed);
            if (!manager.IsInstalled)
                return new UpdateCheckResult { Availability = UpdateAvailability.NotApplicable };

            var current = manager.CurrentVersion?.ToString();
            var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (update is null)
                return new UpdateCheckResult { Availability = UpdateAvailability.UpToDate, CurrentVersion = current };

            return new UpdateCheckResult
            {
                Availability = UpdateAvailability.Available,
                CurrentVersion = current,
                AvailableVersion = update.TargetFullRelease.Version.ToString(),
                Manager = manager,
                Info = update,
            };
        }
        catch
        {
            return new UpdateCheckResult { Availability = UpdateAvailability.Failed };
        }
    }

    /// <summary>
    /// Downloads and installs the update described by <paramref name="result"/>, then restarts the
    /// app. Only valid when <see cref="UpdateCheckResult.Availability"/> is
    /// <see cref="UpdateAvailability.Available"/>. Does not return on success (the process restarts).
    /// </summary>
    public static async Task ApplyAsync(UpdateCheckResult result)
    {
        if (result is not { Availability: UpdateAvailability.Available, Manager: { } manager, Info: { } info })
            return;

        await manager.DownloadUpdatesAsync(info).ConfigureAwait(false);
        manager.ApplyUpdatesAndRestart(info.TargetFullRelease);
    }

    /// <summary>
    /// Returns a human-readable notice (e.g. "Update available: v0.2.0 — you have v0.1.0") when a
    /// newer release exists, or null when up to date / not applicable. Used by the launch banner and
    /// the <c>check-update</c> CLI probe.
    /// </summary>
    public static async Task<string?> CheckAsync()
    {
        var result = await CheckDetailedAsync().ConfigureAwait(false);
        return result.Availability == UpdateAvailability.Available
            ? $"Update available: v{result.AvailableVersion} — you have v{result.CurrentVersion}"
            : null;
    }
}
