using System;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Sprig.Engine.Store;

namespace Sprig.App;

/// <summary>
/// Starting sprig in the tray when the user signs in to Windows, so opening it, from Perch or anywhere, is a window
/// coming forward rather than a cold start. Windows' own per-user Run entry is the setting rather than a copy in sprig's
/// settings, so it reads the same as Task Manager's Startup apps, which can switch it off too.
/// </summary>
public static class SignInStart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Where Task Manager records an entry switched off without removing it: a first byte of 2 is on, odd is
    /// off.</summary>
    const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    /// <summary>The flag that starts sprig with no window, in the tray.</summary>
    public const string TrayFlag = "--tray";

    /// <summary>A dev build has an entry of its own, so turning one on never turns off the other.</summary>
    static string EntryName => "sprig" + AppProfile.DisplaySuffix;

    public static bool Supported => OperatingSystem.IsWindows() && Environment.ProcessPath is not null;

    public static bool Enabled => OperatingSystem.IsWindows() && Read() is not null && !SwitchedOff();

    public static void Set(bool on)
    {
        if (!OperatingSystem.IsWindows()) return;
        using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (on && Command() is { } command) key.SetValue(EntryName, command);
            else key.DeleteValue(EntryName, throwOnMissingValue: false);
        }
        // Turned on here is on, whatever Task Manager last said.
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
        approved?.DeleteValue(EntryName, throwOnMissingValue: false);
    }

    /// <summary>Point an entry that's on at this copy of sprig, as after an install moved.</summary>
    public static void Refresh()
    {
        if (!OperatingSystem.IsWindows() || Read() is not { } current || Command() is not { } command || current == command) return;
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(EntryName, command);
    }

    /// <summary>The entry for <paramref name="exe"/> run with <see cref="TrayFlag"/>.</summary>
    internal static string CommandFor(string exe) => $"\"{exe}\" {TrayFlag}";

    static string? Command() => Environment.ProcessPath is { } exe ? CommandFor(exe) : null;

    [SupportedOSPlatform("windows")]
    static string? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(EntryName) as string;
    }

    [SupportedOSPlatform("windows")]
    static bool SwitchedOff()
    {
        using var key = Registry.CurrentUser.OpenSubKey(ApprovedKey);
        return key?.GetValue(EntryName) is byte[] { Length: > 0 } state && (state[0] & 1) == 1;
    }
}
