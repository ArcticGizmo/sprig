using Sprig.Core.Processes;

namespace Sprig.Tests;

/// <summary>Records invocations and returns a canned result — lets us assert exact CLI arg arrays.</summary>
public sealed class RecordingProcessRunner : IProcessRunner
{
    public record Invocation(string Executable, IReadOnlyList<string> Arguments, string? WorkingDirectory);

    public List<Invocation> Calls { get; } = [];
    public int ExitCode { get; set; }
    public string StdOut { get; set; } = "";
    public string StdErr { get; set; } = "";

    public ProcessResult Run(string executable, IReadOnlyList<string> arguments, string? workingDirectory = null, CancellationToken ct = default)
    {
        Calls.Add(new Invocation(executable, arguments, workingDirectory));
        return new ProcessResult(executable, arguments, ExitCode, StdOut, StdErr);
    }

    public Invocation Last => Calls[^1];
}
