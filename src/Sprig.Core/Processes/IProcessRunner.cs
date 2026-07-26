namespace Sprig.Core.Processes;

/// <summary>
/// Runs external processes (git, docker) capturing stdout/stderr/exit code. Arguments are
/// passed as an array (never a shell string) so paths with spaces need no quoting gymnastics.
/// An interface so services can be unit-tested against a fake.
/// </summary>
public interface IProcessRunner
{
    /// <param name="onOutput">Optional sink called with each stdout/stderr line as it arrives, for
    /// callers that want to stream live output (e.g. a setup install). The full output is still
    /// captured and returned in the <see cref="ProcessResult"/> regardless.</param>
    ProcessResult Run(
        string executable,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default,
        Action<string>? onOutput = null);
}
