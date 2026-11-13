using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Sprig.Engine.Hosting;

namespace Sprig.Engine.Compose;

/// <summary>
/// One compose service's log, read as it grows through <c>docker compose logs --follow</c>. The follow ends when the
/// container stops, so <see cref="EnsureRunning"/> starts it again from just after the last line it saw, which leaves
/// no gap and no repeats across a container's restart. The process sits in a Job Object, so it can't outlive the app.
/// </summary>
public sealed class ContainerLogFollower(string projectName, string service, int tail = ContainerLogFollower.Tail) : ILogSource, IDisposable
{
    public const int Tail = 1000;

    readonly ConcurrentQueue<LogEntry> _lines = new();
    readonly object _gate = new();
    Process? _process;
    JobObject? _job;
    DateTimeOffset? _last;
    bool _disposed;

    /// <summary>For people: the command that shows the same log.</summary>
    public string File => $"docker compose -p {projectName} logs {service}";

    public int Generation => 0;

    public bool IsRunning
    {
        get
        {
            lock (_gate) return _process is { HasExited: false };
        }
    }

    public IReadOnlyList<LogEntry> ReadNew()
    {
        var entries = new List<LogEntry>();
        while (entries.Count < 2_000 && _lines.TryDequeue(out var entry)) entries.Add(entry);
        return entries;
    }

    /// <summary>Start following, or start again after the container stopped; does nothing while it's following.</summary>
    public void EnsureRunning()
    {
        lock (_gate)
        {
            if (_disposed || _process is { HasExited: false }) return;
            Close();
            var psi = new ProcessStartInfo("docker")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var arg in Arguments(projectName, service, _last, tail)) psi.ArgumentList.Add(arg);
            var process = new Process { StartInfo = psi };
            process.OutputDataReceived += (_, e) => Take(e.Data);
            process.ErrorDataReceived += (_, e) => Take(e.Data);
            try
            {
                process.Start();
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                process.Dispose();
                _lines.Enqueue(new LogEntry(DateTimeOffset.Now, $"sprig: couldn't follow the log: {e.Message}"));
                return;
            }
            _job = new JobObject();
            try { _job.Add(process); }
            catch (InvalidOperationException) { /* already exited */ }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;
        }
    }

    void Take(string? raw)
    {
        if (raw is null) return;
        var entry = Parse(raw);
        lock (_gate)
        {
            if (entry.Time is { } time)
            {
                // A follow started again from the last line's time can send that line again.
                if (_last is { } last && time <= last) return;
                _last = time;
            }
        }
        _lines.Enqueue(entry);
    }

    /// <param name="since">The last line already read: only what came after it. Null for the end of the log so far.</param>
    public static IReadOnlyList<string> Arguments(string projectName, string service, DateTimeOffset? since, int tail = Tail)
        => ["compose", "-p", projectName, "logs", "--follow", "--timestamps", "--no-color", "--no-log-prefix",
            .. since is { } s ? new[] { "--since", s.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture) }
                : ["--tail", tail.ToString(CultureInfo.InvariantCulture)],
            service];

    /// <summary>A line as <c>--timestamps</c> writes it: an RFC 3339 time with up to nine decimals, a space, then the
    /// container's own line. Anything else (docker's own messages) is kept whole, without a time.</summary>
    public static LogEntry Parse(string raw)
    {
        var space = raw.IndexOf(' ');
        var stamp = space > 0 ? raw[..space] : raw;
        if (stamp.Length < 20 || stamp[4] != '-' || stamp[10] != 'T') return new LogEntry(null, raw);
        // .NET reads at most seven decimals; docker writes nine.
        var dot = stamp.IndexOf('.');
        var zone = stamp.IndexOfAny(['Z', '+'], 19);
        if (zone < 0) zone = stamp.LastIndexOf('-') > 18 ? stamp.LastIndexOf('-') : stamp.Length;
        if (dot > 0 && dot < zone && zone - dot - 1 > 7) stamp = stamp[..(dot + 8)] + stamp[zone..];
        return DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
            ? new LogEntry(time, space > 0 ? raw[(space + 1)..] : "")
            : new LogEntry(null, raw);
    }

    void Close()
    {
        _job?.Terminate(_process);
        _job?.Dispose();
        _job = null;
        _process?.Dispose();
        _process = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            Close();
        }
    }
}
