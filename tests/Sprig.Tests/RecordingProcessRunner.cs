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

    public ProcessResult Run(string executable, IReadOnlyList<string> arguments, string? workingDirectory = null,
        CancellationToken ct = default, Action<string>? onOutput = null)
    {
        Calls.Add(new Invocation(executable, arguments, workingDirectory));
        // Replay the canned output through the stream sink so streaming callers can be exercised.
        if (onOutput is not null)
            foreach (var line in (StdOut + StdErr).Split('\n', StringSplitOptions.RemoveEmptyEntries))
                onOutput(line.TrimEnd('\r'));
        return new ProcessResult(executable, arguments, ExitCode, StdOut, StdErr);
    }

    public Invocation Last => Calls[^1];
}
