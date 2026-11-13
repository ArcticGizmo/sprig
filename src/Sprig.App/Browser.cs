using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Sprig.App;

/// <summary>Opening links in the user's browser. Best-effort throughout: a browser that won't open never takes sprig down.</summary>
public static class Browser
{
    public static void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch
        {
            // Nothing to do but leave it unopened.
        }
    }

    /// <summary>
    /// In a window of its own rather than a tab of the one already open. The shell can't ask for that, so the default
    /// browser is found and asked directly; one sprig doesn't know how to ask gets the link the usual way.
    /// </summary>
    public static void OpenInNewWindow(string url)
    {
        try
        {
            if (DefaultBrowser() is { } exe && NewWindowFlag(exe) is { } flag)
            {
                var start = new ProcessStartInfo(exe) { UseShellExecute = false };
                start.ArgumentList.Add(flag);
                start.ArgumentList.Add(url);
                Process.Start(start)?.Dispose();
                return;
            }
        }
        catch
        {
            // Fall back to the usual way below.
        }
        Open(url);
    }

    /// <summary>The flag each browser family takes, by its executable's name.</summary>
    public static string? NewWindowFlag(string exe) => Path.GetFileNameWithoutExtension(exe).ToLowerInvariant() switch
    {
        "chrome" or "msedge" or "brave" or "vivaldi" or "opera" or "chromium" => "--new-window",
        "firefox" or "librewolf" or "waterfox" => "-new-window",
        _ => null,
    };

    static string? DefaultBrowser()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var progId = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice", "ProgId", null) as string;
        if (string.IsNullOrEmpty(progId)) return null;
        var command = Registry.GetValue($@"HKEY_CLASSES_ROOT\{progId}\shell\open\command", "", null) as string;
        return command is null ? null : ExeOf(command);
    }

    /// <summary>The program in a shell open command, such as <c>"C:\...\chrome.exe" --single-argument %1</c>.</summary>
    public static string? ExeOf(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }
        var exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? command[..(exe + 4)] : null;
    }
}
