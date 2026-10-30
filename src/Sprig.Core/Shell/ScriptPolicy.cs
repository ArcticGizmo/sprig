using System.Diagnostics;

namespace Sprig.Core.Shell;

/// <summary>
/// Whether every installed PowerShell will run a local, unsigned script such as the <c>sprig.ps1</c> shim.
/// A blocked <c>.ps1</c> on PATH breaks <c>sprig</c> in that shell entirely, so it is only written when this
/// says yes. Each shell is asked for its effective policy rather than the policy being worked out here:
/// Windows PowerShell and PowerShell 7 keep separate settings, layered across Group Policy, the registry and
/// <c>powershell.config.json</c>, and only the shell itself applies that precedence reliably.
/// </summary>
public static class ScriptPolicy
{
    // A script sprig wrote carries no mark of the web, so RemoteSigned runs it.
    static readonly string[] Runnable = ["Unrestricted", "RemoteSigned", "Bypass"];

    internal const string ProcessScopeVariable = "PSExecutionPolicyPreference";

    // Velopack gives an install hook 15-30s in all; the shells are asked in parallel against this one deadline.
    static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    /// <summary>One shell's effective policy; empty when it couldn't be asked in time.</summary>
    public sealed record ShellPolicy(string Shell, string Policy)
    {
        public bool RunsLocalScripts => Runnable.Contains(Policy, StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<ShellPolicy> Ask()
    {
        var shells = InstalledShells();
        var asked = shells.Select(EffectivePolicyAsync).ToArray();
        Task.WhenAll(asked).Wait(Deadline);
        return shells.Select((s, i) => new ShellPolicy(s, asked[i].IsCompletedSuccessfully ? asked[i].Result : "")).ToList();
    }

    public static bool AllowLocalScripts(IReadOnlyList<ShellPolicy> policies) =>
        policies.Count > 0 && policies.All(p => p.RunsLocalScripts);

    internal static IReadOnlyList<string> InstalledShells()
    {
        var found = new List<string>();
        var windows = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (File.Exists(windows)) found.Add(windows);

        // PowerShell 7 can be installed several times over (stable, preview, the Store), each with its own policy.
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var dirs = path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var programFiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell");
        if (Directory.Exists(programFiles)) dirs.AddRange(Directory.GetDirectories(programFiles));
        foreach (var dir in dirs)
        {
            string pwsh;
            try { pwsh = Path.GetFullPath(Path.Combine(dir, "pwsh.exe")); }
            catch { continue; } // a malformed PATH entry must never fail the install
            if (File.Exists(pwsh) && !found.Contains(pwsh, StringComparer.OrdinalIgnoreCase)) found.Add(pwsh);
        }
        return found;
    }

    static async Task<string> EffectivePolicyAsync(string shell)
    {
        try
        {
            var info = new ProcessStartInfo(shell)
            {
                ArgumentList = { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "Get-ExecutionPolicy" },
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            // A PowerShell started with -ExecutionPolicy hands that policy to its children through this variable.
            // Run from such a shell (an agent, a VS Code task, CI), the answer would describe that one session,
            // not the terminals the shim has to work in.
            info.Environment.Remove(ProcessScopeVariable);
            using var process = Process.Start(info);
            if (process is null) return "";
            using var timeout = new CancellationTokenSource(Deadline);
            try
            {
                var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                return process.ExitCode == 0 ? output.Trim() : "";
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                return "";
            }
        }
        catch
        {
            return ""; // treated as blocked: leaving the .ps1 out only costs the in-place cd
        }
    }
}
