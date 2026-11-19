using System.IO.Pipes;
using System.Text;
using Sprig.Engine.Hosting;

namespace Sprig.Engine.Store;

/// <summary>The desktop app's claim on a store, named here so the CLI can tell whether it's running without asking it.</summary>
public static class AppInstance
{
    public static string MutexName(ISprigPaths paths) => @"Local\sprig-gui-" + HostProtocol.StoreId(paths);

    /// <summary>Where a later launch tells the running app to show a window, or the CLI tells it to quit.</summary>
    public static string PipeName(ISprigPaths paths) => "sprig-gui-" + HostProtocol.StoreId(paths);

    /// <summary>Asks the running app to quit as its tray's Quit does, pausing or stopping every app first.</summary>
    public const string QuitMessage = "quit";

    public static bool IsRunning(ISprigPaths paths)
    {
        if (!Mutex.TryOpenExisting(MutexName(paths), out var mutex)) return false;
        mutex.Dispose();
        return true;
    }

    /// <summary>Say one line to the running app. False when none answered in time.</summary>
    public static bool Tell(string pipe, string message, TimeSpan? timeout = null)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipe, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect((int)(timeout ?? TimeSpan.FromSeconds(2)).TotalMilliseconds);
            client.Write(Encoding.UTF8.GetBytes(message + "\n"));
            return true;
        }
        catch (Exception e) when (e is TimeoutException or IOException)
        {
            return false;
        }
    }
}
