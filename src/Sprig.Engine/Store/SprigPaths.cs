namespace Sprig.Engine.Store;

/// <summary>
/// The machine-local central store layout. An interface so tests can point it at a temp dir and the engine
/// stays OS-agnostic.
/// </summary>
public interface ISprigPaths
{
    /// <summary>Root of the central store.</summary>
    string Root { get; }
    /// <summary>The user-settings file.</summary>
    string SettingsFile { get; }
}

/// <summary>Filesystem implementation of <see cref="ISprigPaths"/>.</summary>
public sealed class SprigPaths : ISprigPaths
{
    /// <param name="root">Store root. Null means <c>SPRIG_STORE</c> when it is set, so the CLI and the app can
    /// both be pointed at a throwaway store, else this profile's store.</param>
    public SprigPaths(string? root = null)
        => Root = root ?? EnvironmentRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppProfile.DataFolderName);

    /// <summary>The store root named by the <c>SPRIG_STORE</c> environment variable, or null when it is unset.</summary>
    public static string? EnvironmentRoot
        => Environment.GetEnvironmentVariable("SPRIG_STORE") is { } root && !string.IsNullOrWhiteSpace(root) ? root : null;

    public string Root { get; }
    public string SettingsFile => Path.Combine(Root, "settings.json");
}
