namespace Sprig.Engine.Store;

/// <summary>
/// Tells a development instance apart from an installed release, so the two never share a central store.
///
/// <c>SPRIG_DEV</c> wins if set; otherwise a Debug build is a dev instance and a Release build is not.
/// </summary>
public static class AppProfile
{
    /// <summary>True when this process is an isolated development instance.</summary>
    public static bool IsDev { get; } = ComputeIsDev();

    /// <summary>The store folder name under %LOCALAPPDATA%: <c>sprig</c> or <c>sprig (Dev)</c>.</summary>
    public static string DataFolderName => IsDev ? "sprig (Dev)" : "sprig";

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
