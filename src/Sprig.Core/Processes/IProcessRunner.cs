namespace Sprig.Core.Processes;

/// <summary>
/// Runs external processes (git, docker) capturing stdout/stderr/exit code. Arguments are
/// passed as an array (never a shell string) so paths with spaces need no quoting gymnastics.
/// An interface so services can be unit-tested against a fake.
/// </summary>
public interface IProcessRunner
{
    ProcessResult Run(
        string executable,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default);
}
