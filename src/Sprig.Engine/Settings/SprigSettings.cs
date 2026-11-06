namespace Sprig.Engine.Settings;

/// <summary>
/// User-configurable, machine-local settings (persisted to <c>settings.json</c> in the central store).
/// </summary>
public sealed class SprigSettings
{
    /// <summary>First port sprig may allocate (inclusive).</summary>
    public int PortRangeStart { get; set; } = DefaultRangeStart;

    /// <summary>One past the last port sprig may allocate (exclusive).</summary>
    public int PortRangeEndExclusive { get; set; } = DefaultRangeEndExclusive;

    /// <summary>
    /// Ports that are never allocated, even when they fall inside the range, e.g. ports something else on
    /// the machine already owns. Deduped and sorted on save.
    /// </summary>
    public List<int> RestrictedPorts { get; set; } = new();

    /// <summary>Show the "what's new" changelog window on the first launch after an update.</summary>
    public bool ShowChangelogOnUpdate { get; set; } = true;

    /// <summary>
    /// The app version that last ran here, used to pick the "what's new" entries. Null on a fresh install.
    /// </summary>
    public string? LastSeenVersion { get; set; }

    /// <summary>
    /// Where workspaces put their worktrees, as <c>&lt;folder&gt;/&lt;orchestration&gt;--&lt;workspace&gt;/&lt;repo&gt;</c>. Null puts that
    /// folder beside the orchestration's repos instead, which keeps the repos' layout relative to each other.
    /// </summary>
    public string? WorkspacesFolder { get; set; }

    public const int DefaultRangeStart = 8000;
    public const int DefaultRangeEndExclusive = 9000;

    /// <summary>A Dev build's own range, so its workspaces never take ports an installed sprig's workspaces use.</summary>
    public const int DevRangeStart = 9000;
    public const int DevRangeEndExclusive = 10000;

    /// <param name="dev">A Dev build (<see cref="Store.AppProfile.IsDev"/>), which defaults to the 9000s.</param>
    public static SprigSettings Defaults(bool dev) => dev
        ? new SprigSettings { PortRangeStart = DevRangeStart, PortRangeEndExclusive = DevRangeEndExclusive }
        : new SprigSettings();

    public const int MinPort = 1;
    public const int MaxPort = 65535;

    public SprigSettings Clone() => new()
    {
        PortRangeStart = PortRangeStart,
        PortRangeEndExclusive = PortRangeEndExclusive,
        RestrictedPorts = new List<int>(RestrictedPorts),
        ShowChangelogOnUpdate = ShowChangelogOnUpdate,
        LastSeenVersion = LastSeenVersion,
        WorkspacesFolder = WorkspacesFolder,
    };
}
