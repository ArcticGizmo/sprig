using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Sprig.Engine.Hosting;

/// <summary>
/// Sends Ctrl+C to everything in a runner's console, the way pressing it in a terminal would, so apps get to shut down
/// properly (ASP.NET drains requests, Vite closes its watchers). Each runner was started with a console of its own (no
/// window), so the signal reaches only that runner's processes.
/// <para>
/// Sending it means attaching to that console, and whatever is attached gets the Ctrl+C too. The host used to attach
/// itself and rely on a handler to survive, but now and then the signal ended the host anyway, and every app with it. So
/// the host never attaches: a short-lived copy of itself (<see cref="HelperArgument"/>) does it and exits, and only that
/// copy is ever at risk.
/// </para>
/// </summary>
public static class ConsoleSignal
{
    public const string HelperArgument = "--ctrl-c";

    /// <summary>The exe that runs <see cref="RunHelper"/> when given <see cref="HelperArgument"/>: the host sets its own
    /// path at startup. Without it, Ctrl+C can't be sent and stops fall back to ending the app.</summary>
    public static string? HelperPath { get; set; }

    /// <summary>
    /// Call once before starting any app. Windows passes an "ignore Ctrl+C" flag from parent to child (whatever started
    /// the host may have set it), and an app that inherits it can never be stopped gently, so the host clears it.
    /// </summary>
    public static void Prepare()
    {
        if (OperatingSystem.IsWindows()) SetConsoleCtrlHandler(IntPtr.Zero, false);
    }

    /// <summary>Ask the console that <paramref name="pid"/> runs in to stop. False when that couldn't be done (it has
    /// no console, it already exited, or this isn't Windows); the caller then has only the kill left.</summary>
    public static async Task<bool> SendCtrlCAsync(int pid)
    {
        if (!OperatingSystem.IsWindows() || HelperPath is not { } helper) return false;
        var psi = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add(HelperArgument);
        psi.ArgumentList.Add(pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var process = Process.Start(psi);
        if (process is null) return false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(); } catch (InvalidOperationException) { /* just exited */ }
            return false;
        }
        return process.ExitCode == 0;
    }

    /// <summary>
    /// The helper's whole job: leave its own console, join the app's, and send Ctrl+C to everything there. It ignores
    /// the Ctrl+C itself (it's in that console too), which is safe here because it starts nothing that could inherit
    /// the setting. Exit code 0 when sent.
    /// </summary>
    public static int RunHelper(string pidText)
    {
        if (!OperatingSystem.IsWindows() || !uint.TryParse(pidText, out var pid)) return 2;
        FreeConsole();
        if (!AttachConsole(pid)) return 3;
        if (!SetConsoleCtrlHandler(IntPtr.Zero, true)) return 4;
        return GenerateConsoleCtrlEvent(CtrlCEvent, 0) ? 0 : 5;
    }

    const uint CtrlCEvent = 0;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool FreeConsole();

    /// <summary>With a null handler: set (true) or clear (false) the "ignore Ctrl+C" flag, which children inherit.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);
}
