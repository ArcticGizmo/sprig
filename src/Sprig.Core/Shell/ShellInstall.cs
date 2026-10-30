namespace Sprig.Core.Shell;

/// <summary>
/// Makes an installed sprig runnable as <c>sprig</c> from any terminal, and takes that away again on
/// uninstall. Run by the Velopack install/update/uninstall hooks, and by <c>sprig shell install</c> so a
/// change in PowerShell's script policy takes effect without waiting for the next update.
///
/// Two folders go on the user PATH, in this order: <c>bin</c>, holding the shell shims that let
/// <c>sprig cd</c> move the shell it was typed in (see <see cref="ShellShims"/>), then <c>current</c>, holding
/// <c>sprig.exe</c>. Shells search PATH folder by folder, so they reach a shim first. Programs that start
/// <c>sprig</c> without a shell (.NET's <c>Process.Start</c>, Node's <c>execFile</c>, Python's
/// <c>subprocess</c>) only look for an <c>.exe</c>, so they skip <c>bin</c> and still find <c>current</c>.
///
/// Everything here edits the <b>user</b> PATH (<c>HKCU\Environment</c>) only: the installer needs no
/// admin rights, so we never touch the machine PATH. .NET's <see cref="Environment.SetEnvironmentVariable(string, string, EnvironmentVariableTarget)"/>
/// with a User/Machine target also broadcasts <c>WM_SETTINGCHANGE</c>, so newly launched terminals
/// pick the change up — already-open ones won't, which is why the installer tells the user to open a
/// fresh terminal.
/// </summary>
public static class ShellInstall
{
    /// <summary>What <see cref="Ensure"/> did. <see cref="ShimError"/> is set when the shims couldn't be
    /// written, in which case only the exe's folder went on PATH.</summary>
    public sealed record Result(
        string ShimDirectory,
        IReadOnlyList<string> Shims,
        IReadOnlyList<ScriptPolicy.ShellPolicy> Policies,
        bool PathChanged,
        string? ShimError);

    /// <summary>True when <paramref name="cliDirectory"/> is a Velopack install's <c>current</c> folder, the only
    /// layout whose PATH entries this manages. A build run from its output folder is not one.</summary>
    public static bool IsInstalled(string cliDirectory)
    {
        var dir = Path.TrimEndingDirectorySeparator(cliDirectory);
        return string.Equals(Path.GetFileName(dir), "current", StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(Path.GetDirectoryName(dir)!, "Update.exe"));
    }

    // Beside "current" rather than inside it, so an update's swap of current never takes the shims with it.
    // It must be a folder of its own: cmd picks sprig.exe over sprig.cmd in the same folder.
    public static string ShimDirectory(string cliDirectory) =>
        Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(cliDirectory))!, "bin");

    /// <summary>Write the shims and put both folders on the user PATH, shims first (idempotent).</summary>
    public static Result Ensure(string cliDirectory)
    {
        var cli = Path.TrimEndingDirectorySeparator(cliDirectory);
        var shimDir = ShimDirectory(cli);
        var policies = ScriptPolicy.Ask();
        var entries = UserPath();
        IReadOnlyList<string> shims = [];
        string? error = null;
        string[] updated;
        try
        {
            shims = ShellShims.Write(shimDir, Path.Combine(cli, "sprig.exe"), ScriptPolicy.AllowLocalScripts(policies));
            updated = Register(entries, shimDir, cli);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Without shims sprig still runs; `sprig cd` opens a window instead of moving the shell.
            error = e.Message;
            updated = Register(Unregister(entries, shimDir), cli);
        }
        var changed = !updated.SequenceEqual(entries);
        if (changed)
            Environment.SetEnvironmentVariable("PATH", string.Join(';', updated), EnvironmentVariableTarget.User);
        return new Result(shimDir, shims, policies, changed, error);
    }

    /// <summary>Take both folders off the user PATH and delete the shims, leaving every other entry untouched.</summary>
    public static void Remove(string cliDirectory)
    {
        var cli = Path.TrimEndingDirectorySeparator(cliDirectory);
        var shimDir = ShimDirectory(cli);
        var entries = UserPath();
        var kept = Unregister(entries, shimDir, cli);
        if (kept.Length != entries.Length)
            Environment.SetEnvironmentVariable("PATH", string.Join(';', kept), EnvironmentVariableTarget.User);

        // Already gone, or held open by a shell running a shim: neither may fail the uninstall.
        try { Directory.Delete(shimDir, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary><paramref name="dirs"/> on PATH in the given order, where the first of them already sits (or at
    /// the end), so a user who moved our entries keeps that position.</summary>
    internal static string[] Register(string[] entries, params string[] dirs)
    {
        var at = Array.FindIndex(entries, e => dirs.Any(d => SamePath(e, d)));
        var rest = Unregister(entries, dirs).ToList();
        rest.InsertRange(at < 0 ? rest.Count : at, dirs);
        return [.. rest];
    }

    internal static string[] Unregister(string[] entries, params string[] dirs) =>
        entries.Where(e => !dirs.Any(d => SamePath(e, d))).ToArray();

    static string[] UserPath() =>
        Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) is { Length: > 0 } path
            ? path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

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
