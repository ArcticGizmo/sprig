using Sprig.Engine.Settings;
using Sprig.Engine.Store;

namespace Sprig.App;

/// <summary>
/// Composition root: the engine services the UI drives. View-models hold these and never re-implement
/// engine logic; blocking calls go through <see cref="RunAsync{T}"/> so the UI thread stays free.
/// </summary>
public sealed class AppServices
{
    public ISprigPaths Paths { get; }
    public ISettingsStore Settings { get; }

    /// <param name="root">Store root; null means this profile's store.</param>
    public AppServices(string? root = null)
    {
        Paths = new SprigPaths(root);
        Settings = new FileSettingsStore(Paths);
    }

    public static Task<T> RunAsync<T>(Func<T> work) => Task.Run(work);

    public static Task RunAsync(Action work) => Task.Run(work);
}
