using System;
using System.Runtime.InteropServices;
using Sprig.Engine.Hosting;
using Sprig.Engine.Store;

namespace Sprig.App;

/// <summary>
/// A dev build run from a terminal (<c>dotnet run --project src/Sprig.App</c>) is a windowed app with no console, so
/// Ctrl+C there never reaches it, and the terminal sits waiting on a window that may be hidden in the tray. Joining the
/// terminal's console lets Ctrl+C quit it the way the tray's Quit does: every app stopped, then the host, then sprig.
/// <para>
/// Only a dev build joins: an installed one opened by <c>sprig open</c> would otherwise be quit by a Ctrl+C meant for
/// whatever that terminal ran next. The host and the apps run in consoles of their own, so the Ctrl+C reaches none of
/// them.
/// </para>
/// </summary>
static class TerminalStop
{
    const uint AttachParentProcess = unchecked((uint)-1);

    static PosixSignalRegistration? _registration;

    public static void Listen(Action quit)
    {
        if (!OperatingSystem.IsWindows() || !AppProfile.IsDev || !AttachConsole(AttachParentProcess)) return;
        // Whatever started the app may have told its children to ignore Ctrl+C, which would keep the handler from ever
        // hearing it.
        ConsoleSignal.Prepare();
        var stopping = false;
        _registration = PosixSignalRegistration.Create(PosixSignal.SIGINT, context =>
        {
            context.Cancel = true;
            if (stopping) return;
            stopping = true;
            Say("sprig: stopping every app, then quitting…");
            quit();
        });
    }

    static void Say(string line)
    {
        try { Console.Error.WriteLine(line); }
        catch (System.IO.IOException) { }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AttachConsole(uint processId);
}
