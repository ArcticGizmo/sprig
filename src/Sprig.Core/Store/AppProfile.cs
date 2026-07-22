namespace Sprig.Core.Store;

/// <summary>
/// Tells a development instance apart from an installed release, so the two never share a central
/// store (a dev run must not clobber the store your real, installed sprig depends on).
///
/// The decision (mirrors perch's AppProfile): the <c>SPRIG_DEV</c> environment variable wins if set,
/// otherwise the build configuration decides — a Debug build (F5 / <c>dotnet run</c> / <c>dotnet test</c>)
/// is a dev instance, a Release build (what the installer ships) is not. Computed once at startup.
/// </summary>
public static class AppProfile
{
    /// <summary>True when this process is an isolated development instance.</summary>
    public static bool IsDev { get; } = ComputeIsDev();

    /// <summary>The store folder name under %LOCALAPPDATA% for this profile — <c>sprig</c> or <c>sprig (Dev)</c>.</summary>
    public static string DataFolderName => IsDev ? "sprig (Dev)" : "sprig";

    /// <summary>Suffix for user-facing labels (e.g. the window title) — <c>""</c> or <c>" (Dev)"</c>.</summary>
    public static string DisplaySuffix => IsDev ? " (Dev)" : "";

    static bool ComputeIsDev()
    {
        // SPRIG_DEV overrides the build default: "0"/"false" forces release, any other value forces dev.
        // (Lets you point a Debug build at the real store, or force a Release build into a dev profile.)
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
