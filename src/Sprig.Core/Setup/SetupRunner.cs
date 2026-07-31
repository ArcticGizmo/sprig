using Sprig.Core.Processes;

namespace Sprig.Core.Setup;

/// <summary>The captured outcome of one repo setup command (see <see cref="SetupRunner"/>).</summary>
public sealed record SetupOutcome(string Command, int ExitCode, string Output)
{
    public bool Success => ExitCode == 0;

    /// <summary>The module this command belonged to (schema 3+), for grouping in the UI/CLI. Null on
    /// records written before modules, and for the single default module of a migrated flat config.</summary>
    public string? Module { get; init; }
}

/// <summary>
/// Runs a repo's declared <c>setup</c> commands — the "install this project's dependencies" step —
/// in order, at the worktree root, via the platform shell (<c>cmd.exe /c</c> on Windows, else
/// <c>/bin/sh -c</c>) so free-form commands work. Deliberately never throws on a non-zero exit:
/// it returns one <see cref="SetupOutcome"/> per command and the caller decides what to do. sprig's
/// policy is to warn, not roll back, so a failed install leaves the worktree in place to fix by hand.
/// </summary>
public sealed class SetupRunner(IProcessRunner runner)
{
    /// <summary>Captured output is capped so a chatty install (npm, etc.) can't bloat the instance
    /// record; the tail is kept because that's where the error usually is.</summary>
    const int MaxOutputChars = 4000;

    /// <summary>Run each command in order at <paramref name="workingDirectory"/>. Stops at the first
    /// non-zero exit — a later step usually depends on an earlier one — and returns the outcomes
    /// gathered so far (the aborted/never-run commands are simply absent).</summary>
    public IReadOnlyList<SetupOutcome> Run(
        IReadOnlyList<string> commands, string workingDirectory, CancellationToken ct = default)
    {
        var outcomes = new List<SetupOutcome>();
        foreach (var command in commands)
        {
            if (string.IsNullOrWhiteSpace(command)) continue;
            var outcome = RunCommand(command, workingDirectory, ct: ct);
            outcomes.Add(outcome);
            if (!outcome.Success) break;   // a failed step poisons the rest — stop and report
        }
        return outcomes;
    }

    /// <summary>Run a single command at <paramref name="workingDirectory"/>, streaming each output line to
    /// <paramref name="onOutput"/> as it arrives (for a live view). Never throws — a non-zero exit or a
    /// shell that won't start is captured as a failed <see cref="SetupOutcome"/>.</summary>
    public SetupOutcome RunCommand(string command, string workingDirectory,
        Action<string>? onOutput = null, CancellationToken ct = default)
        => RunOne(command, workingDirectory, onOutput, ct);

    SetupOutcome RunOne(string command, string workingDirectory, Action<string>? onOutput, CancellationToken ct)
    {
        var (shell, args) = OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/c", command })
            : ("/bin/sh", new[] { "-c", command });

        // A shell that can't even start (missing cmd/sh) is captured as a failed outcome, not thrown,
        // so one broken command never aborts workspace creation.
        try
        {
            var r = runner.Run(shell, args, workingDirectory, ct, onOutput);
            return new SetupOutcome(command, r.ExitCode, Cap(Combine(r.StdOut, r.StdErr)));
        }
        catch (OperationCanceledException) { throw; }
        catch (ProcessException ex)
        {
            return new SetupOutcome(command, ex.Result.ExitCode, Cap(ex.Result.StdErr));
        }
        catch (Exception ex)
        {
            return new SetupOutcome(command, -1, ex.Message);
        }
    }

    static string Combine(string stdout, string stderr)
    {
        var o = stdout.TrimEnd();
        var e = stderr.TrimEnd();
        if (o.Length == 0) return e;
        if (e.Length == 0) return o;
        return $"{o}\n{e}";
    }

    static string Cap(string s)
        => s.Length <= MaxOutputChars ? s : "…" + s[^MaxOutputChars..];
}
