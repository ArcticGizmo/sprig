using Sprig.Engine.Hosting;

namespace Sprig.Engine.Store;

/// <summary>The desktop app's claim on a store, named here so the CLI can tell whether it's running without asking it.</summary>
public static class AppInstance
{
    public static string MutexName(ISprigPaths paths) => @"Local\sprig-gui-" + HostProtocol.StoreId(paths);

    /// <summary>Where a later launch tells the running app to show a window.</summary>
    public static string PipeName(ISprigPaths paths) => "sprig-gui-" + HostProtocol.StoreId(paths);

    public static bool IsRunning(ISprigPaths paths)
    {
        if (!Mutex.TryOpenExisting(MutexName(paths), out var mutex)) return false;
        mutex.Dispose();
        return true;
    }
}
