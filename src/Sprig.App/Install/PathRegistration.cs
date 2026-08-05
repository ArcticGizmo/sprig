namespace Sprig.App.Install;

/// <summary>
/// Puts the directory that holds <c>sprig.exe</c> on the user's PATH (and takes it off again on
/// uninstall), so the bundled CLI is runnable as <c>sprig</c> from any terminal. Driven from the
/// Velopack install/update/uninstall hooks in <see cref="Program"/>.
///
/// Everything here edits the <b>user</b> PATH (<c>HKCU\Environment</c>) only: the installer needs no
/// admin rights, so we never touch the machine PATH. .NET's <see cref="Environment.SetEnvironmentVariable(string, string, EnvironmentVariableTarget)"/>
/// with a User/Machine target also broadcasts <c>WM_SETTINGCHANGE</c>, so newly launched terminals
/// pick the change up — already-open ones won't, which is why the installer tells the user to open a
/// fresh terminal.
/// </summary>
internal static class PathRegistration
{
    // The Velopack install/update hooks run the main exe from the "current" content directory — the
    // stable path Velopack keeps across updates by swapping its contents in place — and that's exactly
    // where sprig.exe lands too. So AppContext.BaseDirectory is both "where sprig.exe is" and a location
    // that survives updates: adding it once on install is enough.
    static string CliDirectory => AppContext.BaseDirectory.TrimEnd('\\', '/');

    /// <summary>Add the CLI directory to the user PATH if it isn't already there (idempotent).</summary>
    public static void Ensure()
    {
        if (!OperatingSystem.IsWindows()) return;

        var dir = CliDirectory;
        var entries = Split(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User));
        if (entries.Any(e => SamePath(e, dir))) return; // already present

        var updated = string.Join(';', entries.Append(dir));
        Environment.SetEnvironmentVariable("PATH", updated, EnvironmentVariableTarget.User);
    }

    /// <summary>Remove the CLI directory from the user PATH, leaving every other entry untouched.</summary>
    public static void Remove()
    {
        if (!OperatingSystem.IsWindows()) return;

        var dir = CliDirectory;
        var entries = Split(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User));
        var kept = entries.Where(e => !SamePath(e, dir)).ToArray();
        if (kept.Length == entries.Length) return; // nothing of ours was on PATH

        Environment.SetEnvironmentVariable("PATH", string.Join(';', kept), EnvironmentVariableTarget.User);
    }

    static string[] Split(string? path) =>
        string.IsNullOrEmpty(path)
            ? []
            : path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // Compare two PATH entries as directories: normalise to a full path and compare case-insensitively,
    // since Windows paths are. A malformed existing entry must never crash the install hook, so fall
    // back to a plain string compare if it won't normalise.
    static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd('\\', '/'),
                Path.GetFullPath(b).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
    }
}
