namespace Sprig.Engine.Store;

/// <summary>
/// Tells a development instance apart from an installed release, so the two never share a central store.
///
/// <c>SPRIG_DEV</c> wins if set; otherwise a Debug build is a dev instance and a Release build is not.
/// The experimental model's store is kept apart from the released app's (<c>sprig</c>), whose maps and
/// workspaces this build can't read and must never overwrite.
/// </summary>
public static class AppProfile
{
    /// <summary>True when this process is an isolated development instance.</summary>
    public static bool IsDev { get; } = ComputeIsDev();

    /// <summary>The store folder name under %LOCALAPPDATA%: <c>sprig-exp</c> or <c>sprig-exp (Dev)</c>.</summary>
    public static string DataFolderName => IsDev ? "sprig-exp (Dev)" : "sprig-exp";

    /// <summary>Suffix for user-facing labels (e.g. the window title): <c>""</c> or <c>" (Dev)"</c>.</summary>
    public static string DisplaySuffix => IsDev ? " (Dev)" : "";

    static bool ComputeIsDev()
    {
        // "0"/"false" forces release, any other value forces dev.
        var env = Environment.GetEnvironmentVariable("SPRIG_DEV");
        if (!string.IsNullOrEmpty(env))
            return !(env == "0" || env.Equals("false", StringComparison.OrdinalIgnoreCase));
#if DEBUG
        return true;
#else
        return false;
#endif
    }
}
