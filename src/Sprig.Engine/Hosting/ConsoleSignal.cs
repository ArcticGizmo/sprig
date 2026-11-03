using System.Runtime.InteropServices;

namespace Sprig.Engine.Hosting;

/// <summary>
/// Sends Ctrl+C to everything in a runner's console, the way pressing it in a terminal would, so apps get to shut down
/// properly (ASP.NET drains requests, Vite closes its watchers). Each runner was started with a console of its own (no
/// window), so the signal reaches only that runner's processes.
/// </summary>
public static class ConsoleSignal
{
    // A console is per process, so attaching to one runner's console has to wait for the last to finish.
    static readonly Lock Gate = new();
    static readonly HandlerRoutine Ignore = _ => true;
    static bool _prepared;

    /// <summary>
    /// Call once before starting any app. Windows passes an "ignore Ctrl+C" flag from parent to child (whatever started
    /// the host may have set it), and an app that inherits it can never be stopped gently, so the host clears it.
    /// </summary>
    public static void Prepare()
    {
        if (!OperatingSystem.IsWindows()) return;
        lock (Gate)
        {
            if (_prepared) return;
            SetConsoleCtrlHandler(IntPtr.Zero, false);
            _prepared = true;
        }
    }

    /// <summary>Ask the console that <paramref name="pid"/> runs in to stop. False when that couldn't be done (it has
    /// no console, it already exited, or this isn't Windows); the caller then has only the kill left.</summary>
    public static bool SendCtrlC(int pid)
    {
        if (!OperatingSystem.IsWindows()) return false;
        Prepare();
        lock (Gate)
        {
            FreeConsole();
            if (!AttachConsole((uint)pid)) return false;
            try
            {
                // The host is in the console while it signals, so it gets the Ctrl+C too. A handler keeps it alive (the
                // null "ignore" flag would, but children inherit that and could never be stopped gently). It's added
                // again on every attach, because one added while attached to an earlier console doesn't protect the
                // host in the next. None is ever removed: Ctrl+C arrives on a thread of its own, possibly late, and a
                // moment with no handler would let a late one end the host.
                if (!SetConsoleCtrlHandler(Ignore, true)) return false;
                return GenerateConsoleCtrlEvent(CtrlCEvent, 0);
            }
            finally
            {
                FreeConsole();
            }
        }
    }

    const uint CtrlCEvent = 0;

    delegate bool HandlerRoutine(uint ctrlType);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetConsoleCtrlHandler(HandlerRoutine handler, bool add);

    /// <summary>With a null handler: set (true) or clear (false) the inherited "ignore Ctrl+C" flag.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);
}
