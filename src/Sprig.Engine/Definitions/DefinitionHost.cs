using System.Diagnostics;
using System.Text;

namespace Sprig.Engine.Definitions;

public sealed record HostResult(int ExitCode, string Output, bool TimedOut);

/// <summary>Runs a definition as its own process. An interface so the runner's trust and cache logic can be tested
/// without compiling anything.</summary>
public interface IDefinitionHost
{
    HostResult Run(string definitionPath, string outputFile, TimeSpan timeout);
}

/// <summary>
/// <c>dotnet run sprig.cs -- emit --out &lt;file&gt;</c>, in the definition's folder, with an environment cut down to
/// what dotnet and NuGet need. Tokens and keys in the user's environment never reach definition code.
/// </summary>
public sealed class DotnetRunHost : IDefinitionHost
{
    /// <summary>Variables the .NET SDK, NuGet and Windows need to build and run. Everything else is dropped.</summary>
    static readonly string[] Allowed =
    [
        "PATH", "PATHEXT", "SystemRoot", "SystemDrive", "windir", "ComSpec", "OS",
        "TEMP", "TMP", "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "HOME",
        "LOCALAPPDATA", "APPDATA", "ProgramData", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432",
        "CommonProgramFiles", "CommonProgramFiles(x86)", "CommonProgramW6432",
        "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE",
        "DOTNET_ROOT", "DOTNET_ROOT(x86)", "NUGET_PACKAGES", "NUGET_HTTP_CACHE_PATH",
    ];

    public HostResult Run(string definitionPath, string outputFile, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(definitionPath))!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in new[] { "run", Path.GetFullPath(definitionPath), "--", "emit", "--out", outputFile })
            psi.ArgumentList.Add(arg);

        psi.Environment.Clear();
        foreach (var name in Allowed)
            if (Environment.GetEnvironmentVariable(name) is { } value)
                psi.Environment[name] = value;
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";

        using var process = new Process { StartInfo = psi };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit(timeout))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            process.WaitForExit();
            return new HostResult(-1, output.ToString(), TimedOut: true);
        }
        process.WaitForExit();
        return new HostResult(process.ExitCode, output.ToString(), TimedOut: false);
    }
}
