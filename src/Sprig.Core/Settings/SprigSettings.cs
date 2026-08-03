namespace Sprig.Core.Settings;

/// <summary>
/// User-configurable, machine-local settings (persisted to <c>%LOCALAPPDATA%\sprig\settings.json</c>).
/// Currently just the port-allocation policy; kept as a single object so more can be added later.
/// </summary>
public sealed class SprigSettings
{
    /// <summary>First port sprig may allocate to a workspace (inclusive).</summary>
    public int PortRangeStart { get; set; } = DefaultRangeStart;

    /// <summary>One past the last port sprig may allocate (exclusive).</summary>
    public int PortRangeEndExclusive { get; set; } = DefaultRangeEndExclusive;

    /// <summary>
    /// Ports that are never allocated, even when they fall inside the range — e.g. ports something
    /// else on the machine already owns. Deduped and sorted on save.
    /// </summary>
    public List<int> RestrictedPorts { get; set; } = new();

    /// <summary>Show the "what's new" changelog window on the first launch after an update.</summary>
    public bool ShowChangelogOnUpdate { get; set; } = true;

    /// <summary>
    /// The app version that last ran here — used to pick the "what's new" entries. Null on a fresh
    /// install (nothing to diff against). Updated to the running version once the check has run.
    /// </summary>
    public string? LastSeenVersion { get; set; }

    /// <summary>
    /// Ids of the guided lessons the user has finished, so the Learn list can tick them off. Recorded
    /// against the <b>real</b> store, not the throwaway demo store a guide runs in.
    /// </summary>
    public List<string> CompletedGuides { get; set; } = new();

    /// <summary>
    /// The available version the user last dismissed from the update banner. The banner stays hidden while
    /// the feed keeps offering this same version, and reappears once a different (newer) release shows up.
    /// Null means nothing has been dismissed.
    /// </summary>
    public string? DismissedUpdateVersion { get; set; }

    public const int DefaultRangeStart = 8000;
    public const int DefaultRangeEndExclusive = 9000;

    public const int MinPort = 1;
    public const int MaxPort = 65535;

    public SprigSettings Clone() => new()
    {
        PortRangeStart = PortRangeStart,
        PortRangeEndExclusive = PortRangeEndExclusive,
        RestrictedPorts = new List<int>(RestrictedPorts),
        ShowChangelogOnUpdate = ShowChangelogOnUpdate,
        LastSeenVersion = LastSeenVersion,
        CompletedGuides = new List<string>(CompletedGuides),
        DismissedUpdateVersion = DismissedUpdateVersion,
    };
}
