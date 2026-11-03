using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Sprig.App.Authoring;

public sealed record HostCompletionItem(string Label, string Kind, string? Detail);

public sealed record HostDiagnostic(int Start, int End, int Line, string Severity, string Id, string Message);

public sealed record HostResponse(
    int Id,
    double Ms,
    IReadOnlyList<HostCompletionItem>? Items,
    IReadOnlyList<HostDiagnostic>? Diagnostics,
    double? WorkingSetMb,
    double? ManagedHeapMb,
    string? Error);

/// <summary>
/// Talks to sprig-langhost over stdin/stdout. If the host dies, pending requests fail, <see cref="Stopped"/> fires,
/// and the next request starts a fresh host, so the editor degrades to plain text instead of taking the app down.
/// </summary>
public sealed class LanguageHostClient : IDisposable
{
    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    readonly ConcurrentDictionary<int, TaskCompletionSource<HostResponse>> _pending = new();
    readonly object _gate = new();
    Process? _process;
    int _nextId;
    string? _lastError;

    /// <summary>Raised on a background thread when the host exits for any reason.</summary>
    public event Action<int>? Stopped;

    public bool IsRunning => _process is { HasExited: false };

    public int? ProcessId => IsRunning ? _process!.Id : null;

    public Task<HostResponse> CompleteAsync(string text, int position) => SendAsync("complete", text, position);

    public Task<HostResponse> DiagnoseAsync(string text) => SendAsync("diagnostics", text, 0);

    public Task<HostResponse> StatsAsync() => SendAsync("stats", null, 0);

    /// <summary>Kill the host on purpose, to show the editor survives it.</summary>
    public void Kill()
    {
        try { _process?.Kill(entireProcessTree: true); } catch { /* already gone */ }
    }

    async Task<HostResponse> SendAsync(string op, string? text, int position)
    {
        var process = EnsureStarted();
        var id = System.Threading.Interlocked.Increment(ref _nextId);
        var reply = new TaskCompletionSource<HostResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = reply;

        var line = JsonSerializer.Serialize(new { id, op, text, position }, Json);
        lock (_gate)
        {
            process.StandardInput.WriteLine(line);
            process.StandardInput.Flush();
        }
        return await reply.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    Process EnsureStarted()
    {
        lock (_gate)
        {
            if (_process is { HasExited: false }) return _process;

            var psi = new ProcessStartInfo(LocateHost())
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardInputEncoding = new UTF8Encoding(false),
            };
            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => OnLine(e.Data);
            process.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 } line) _lastError = line; };
            process.Exited += (_, _) => OnExited(process);
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;
            return process;
        }
    }

    void OnLine(string? line)
    {
        if (string.IsNullOrEmpty(line)) return;
        HostResponse? response;
        try { response = JsonSerializer.Deserialize<HostResponse>(line, Json); }
        catch (JsonException) { return; }
        if (response is not null && _pending.TryRemove(response.Id, out var waiter))
            waiter.TrySetResult(response);
    }

    void OnExited(Process process)
    {
        foreach (var id in _pending.Keys)
            if (_pending.TryRemove(id, out var waiter))
                waiter.TrySetException(new IOException("the language host stopped" + (_lastError is { } why ? $": {why}" : "")));
        int code;
        try { code = process.ExitCode; } catch { code = -1; }
        Stopped?.Invoke(code);
    }

    // Installed, sprig-langhost sits beside sprig-gui. In a dev build it's in its own project's bin folder. The
    // build also copies a bare sprig-langhost.exe stub (without its dll) into projects that reference it, so a
    // candidate only counts when its dll is there too.
    static string LocateHost()
    {
        var exe = OperatingSystem.IsWindows() ? "sprig-langhost.exe" : "sprig-langhost";
        foreach (var dir in CandidateDirectories())
        {
            var candidate = Path.Combine(dir, exe);
            if (File.Exists(candidate) && File.Exists(Path.Combine(dir, "sprig-langhost.dll"))) return candidate;
        }
        throw new FileNotFoundException("couldn't find sprig-langhost; build Sprig.LanguageHost");
    }

    static IEnumerable<string> CandidateDirectories()
    {
        var baseDir = AppContext.BaseDirectory;
        yield return baseDir;

        var repo = new DirectoryInfo(baseDir);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "sprig.slnx"))) repo = repo.Parent;
        if (repo is null) yield break;
        // Prefer the configuration this process was built in, so a Release app doesn't pick up a stale Debug host.
        var tfm = new DirectoryInfo(baseDir).Name;
        var config = new DirectoryInfo(baseDir).Parent?.Name ?? "Debug";
        foreach (var c in new[] { config, "Debug", "Release" })
            yield return Path.Combine(repo.FullName, "src", "Sprig.LanguageHost", "bin", c, tfm);
    }

    public void Dispose()
    {
        try { _process?.StandardInput.Close(); } catch { /* exiting */ }
        Kill();
        _process?.Dispose();
    }
}
