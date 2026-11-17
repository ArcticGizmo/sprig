using System.ComponentModel;
using System.Diagnostics;

namespace Sprig.Engine.Shell;

/// <summary>A new terminal window open in a folder, for <c>sprig cd</c> when it can't move the shell it was typed in, and
/// for the app's Open in terminal.</summary>
public static class TerminalWindow
{
    public static readonly HashSet<string> KnownShells = new(StringComparer.OrdinalIgnoreCase)
        { "powershell.exe", "pwsh.exe", "cmd.exe", "bash.exe", "sh.exe", "zsh.exe", "nu.exe", "fish.exe" };

    public static bool IsPowerShell(string path) => Path.GetFileName(path).ToLowerInvariant() is "powershell.exe" or "pwsh.exe";

    /// <summary>
    /// Open a window in <paramref name="dir"/>: on Windows the best shell first (<paramref name="preferredShell"/>, else
    /// pwsh, PowerShell, cmd), trying the next when one won't start; on macOS Terminal; elsewhere the first terminal
    /// emulator found. Returns what opened, by name.
    /// </summary>
    /// <param name="title">Under Windows Terminal, the window's title, so several can be told apart.</param>
    public static string Open(string dir, string? preferredShell = null, string? title = null)
    {
        if (OperatingSystem.IsMacOS())
        {
            Process.Start(new ProcessStartInfo("open") { ArgumentList = { "-a", "Terminal", dir }, UseShellExecute = false });
            return "Terminal";
        }
        if (!OperatingSystem.IsWindows())
        {
            foreach (var emulator in new[] { "x-terminal-emulator", "gnome-terminal", "konsole", "xterm" })
            {
                if (Which(emulator) is null) continue;
                Process.Start(new ProcessStartInfo(emulator) { WorkingDirectory = dir, UseShellExecute = false });
                return emulator;
            }
            throw new InvalidOperationException("no terminal emulator was found to open");
        }

        Win32Exception? last = null;
        foreach (var shell in Candidates(preferredShell))
        {
            try
            {
                Start(shell, dir, title);
                return Path.GetFileNameWithoutExtension(shell);
            }
            catch (Win32Exception e) { last = e; }
        }
        throw (Exception?)last ?? new InvalidOperationException("no shell could be opened");
    }

    static void Start(string shell, string dir, string? title)
    {
        // Inside Windows Terminal (or with it installed, from the app), a new Windows Terminal window rather than a bare
        // console. Only it gets the title: a plain console's shell prompt would overwrite one anyway.
        if (Environment.GetEnvironmentVariable("WT_SESSION") is { Length: > 0 } || Which("wt.exe") is not null)
        {
            try
            {
                var wt = new ProcessStartInfo("wt.exe") { UseShellExecute = true };
                foreach (var arg in new[] { "-w", "new", "-d", dir }) wt.ArgumentList.Add(arg);
                if (title is not null)
                {
                    // `;` separates commands on wt's command line.
                    wt.ArgumentList.Add("--title");
                    wt.ArgumentList.Add(title.Replace(";", "\\;"));
                    wt.ArgumentList.Add("--suppressApplicationTitle");
                }
                wt.ArgumentList.Add(shell);
                Process.Start(wt);
                return;
            }
            catch (Win32Exception) { /* no wt after all: a plain console below */ }
        }
        Process.Start(new ProcessStartInfo(shell) { UseShellExecute = true, WorkingDirectory = dir });
    }

    static IEnumerable<string> Candidates(string? preferred)
    {
        if (preferred is not null) yield return preferred;
        if (Which("pwsh.exe") is { } pwsh) yield return pwsh;
        if (Which("powershell.exe") is { } powershell) yield return powershell;
        yield return Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
    }

    /// <summary>The full path of <paramref name="exe"/> on PATH, or null.</summary>
    public static string? Which(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var full = Path.Combine(dir.Trim(), exe);
            if (File.Exists(full)) return full;
        }
        return null;
    }
}
