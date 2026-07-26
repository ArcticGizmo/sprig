using System.Diagnostics;
using System.Text;

namespace Sprig.Core.Processes;

/// <summary>Default <see cref="IProcessRunner"/> over <see cref="System.Diagnostics.Process"/>.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    public ProcessResult Run(
        string executable,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default,
        Action<string>? onOutput = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        // Line handlers run on threadpool threads; onOutput must be safe to call from there (it is —
        // the UI marshals via Progress<T>). stdout and stderr interleave in arrival order for the stream.
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { stdout.AppendLine(e.Data); onOutput?.Invoke(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { stderr.AppendLine(e.Data); onOutput?.Invoke(e.Data); } };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new ProcessException(new ProcessResult(
                executable, arguments, -1, "", $"could not start '{executable}': {ex.Message}"));
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            process.WaitForExitAsync(cancellationToken).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        // Ensure the async readers have flushed their final buffers.
        process.WaitForExit();

        return new ProcessResult(
            executable, arguments, process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* best effort */ }
    }
}
