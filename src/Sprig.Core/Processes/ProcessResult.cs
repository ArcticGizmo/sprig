using System.Text;

namespace Sprig.Core.Processes;

/// <summary>The captured outcome of running an external process.</summary>
public sealed record ProcessResult(
    string Executable,
    IReadOnlyList<string> Arguments,
    int ExitCode,
    string StdOut,
    string StdErr)
{
    public bool Success => ExitCode == 0;

    /// <summary>Throw a legible <see cref="ProcessException"/> if the process exited non-zero.</summary>
    public ProcessResult EnsureSuccess()
    {
        if (Success) return this;
        throw new ProcessException(this);
    }

    internal string CommandLine => $"{Executable} {string.Join(' ', Arguments)}";
}

/// <summary>Thrown when a process exits non-zero and the caller asked for success.</summary>
public sealed class ProcessException(ProcessResult result) : Exception(Build(result))
{
    public ProcessResult Result { get; } = result;

    static string Build(ProcessResult r)
    {
        var sb = new StringBuilder();
        sb.Append($"command failed (exit {r.ExitCode}): {r.CommandLine}");
        if (!string.IsNullOrWhiteSpace(r.StdErr))
            sb.Append($"\n{r.StdErr.TrimEnd()}");
        return sb.ToString();
    }
}
