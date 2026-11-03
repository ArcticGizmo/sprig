using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Sprig.Engine.Store;

namespace Sprig.Engine.Hosting;

/// <summary>
/// Runs apps for the host: build (one at a time per repo), start in a job, log every line to a file in the store, and
/// call an app ready once its port accepts connections. Everything it starts dies with it, because each runner's job
/// closes when the host does.
/// </summary>
public sealed class Supervisor(ISprigPaths paths) : IDisposable
{
    readonly ConcurrentDictionary<(string Sprout, string Name), Runner> _runners = new();
    readonly ConcurrentDictionary<string, SemaphoreSlim> _buildLocks = new(StringComparer.OrdinalIgnoreCase);

    public bool AnyAlive => _runners.Values.Any(r => r.Alive);

    public IReadOnlyList<RunnerStatus> List() => _runners.Values.Select(r => r.Status).OrderBy(s => s.Sprout).ThenBy(s => s.Name).ToArray();

    /// <summary>Start a runner, replacing one of the same name. Returns at once; the build and start carry on in the
    /// background and show in <see cref="List"/>.</summary>
    public RunnerStatus Start(RunnerSpec spec)
    {
        var key = (spec.Sprout, spec.Name);
        if (_runners.TryRemove(key, out var old)) old.Stop();
        var runner = new Runner(spec, HostProtocol.LogFile(paths, spec.Sprout, spec.Name),
            spec.BuildKey is { } k ? _buildLocks.GetOrAdd(k, _ => new SemaphoreSlim(1, 1)) : null);
        _runners[key] = runner;
        _ = Task.Run(runner.RunAsync);
        return runner.Status;
    }

    /// <summary>Stop one app, which stays listed as stopped; or, with no <paramref name="name"/>, the whole sprout,
    /// whose runners are then forgotten (it's going down or away).</summary>
    public bool Stop(string sprout, string? name)
    {
        var stopped = false;
        foreach (var (key, runner) in _runners.Where(kv => kv.Key.Sprout == sprout && (name is null || kv.Key.Name == name)).ToArray())
        {
            runner.Stop();
            if (name is null) _runners.TryRemove(key, out _);
            stopped = true;
        }
        return stopped;
    }

    public void Dispose()
    {
        foreach (var runner in _runners.Values) runner.Stop();
    }

    sealed class Runner(RunnerSpec spec, string logFile, SemaphoreSlim? buildLock)
    {
        readonly JobObject _job = new();
        readonly Lock _gate = new();
        StreamWriter? _log;
        Process? _process;
        volatile bool _stopping;

        public RunnerStatus Status { get; private set; } = new(spec.Sprout, spec.Name, RunnerState.Building, null, spec.Port, null, logFile, DateTimeOffset.UtcNow);

        public bool Alive => Status.State is RunnerState.Building or RunnerState.Starting or RunnerState.Ready;

        public async Task RunAsync()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
                _log = new StreamWriter(new FileStream(logFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8) { AutoFlush = true };

                if (spec.Build is { } build)
                {
                    Write($"sprig: building with {build}");
                    if (buildLock is not null) await buildLock.WaitAsync();
                    int code;
                    try { code = await RunToEndAsync(build); }
                    finally { buildLock?.Release(); }
                    if (_stopping) return;
                    if (code != 0)
                    {
                        Set(RunnerState.Failed, exit: code, error: $"the build exited with code {code}");
                        return;
                    }
                }

                Write($"sprig: starting {spec.Command}");
                var process = Launch(spec.Command);
                _process = process;
                Set(RunnerState.Starting, pid: process.Id);
                if (spec.Port is { } port) _ = WatchReadyAsync(process, port);
                else Set(RunnerState.Ready, pid: process.Id);

                await process.WaitForExitAsync();
                var exit = process.ExitCode;
                Write($"sprig: exited with code {exit}");
                if (!_stopping) Set(RunnerState.Exited, exit: exit);
            }
            catch (Exception e)
            {
                Write($"sprig: {e.Message}");
                Set(RunnerState.Failed, error: e.Message);
            }
            finally
            {
                lock (_gate)
                {
                    _log?.Dispose();
                    _log = null;
                }
            }
        }

        public void Stop()
        {
            _stopping = true;
            _job.Terminate(_process);
            Set(RunnerState.Stopped, exit: null);
            _job.Dispose();
        }

        async Task<int> RunToEndAsync(CommandSpec command)
        {
            var process = Launch(command);
            await process.WaitForExitAsync();
            return process.ExitCode;
        }

        Process Launch(CommandSpec command)
        {
            var psi = new ProcessStartInfo(command.File)
            {
                WorkingDirectory = spec.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // An open stdin that never sends anything: some dev servers quit when theirs reaches end of file.
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var arg in command.Args) psi.ArgumentList.Add(arg);
            foreach (var (key, value) in spec.Env) psi.Environment[key] = value;

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) Write(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Write(e.Data); };
            process.Start();
            _job.Add(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return process;
        }

        /// <summary>Ready once the port accepts a connection, checked against the loopback addresses an app might bind.</summary>
        async Task WatchReadyAsync(Process process, int port)
        {
            while (!process.HasExited && !_stopping)
            {
                foreach (var host in new[] { "127.0.0.1", "::1" })
                {
                    try
                    {
                        using var client = new TcpClient(host.Contains(':') ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork);
                        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                        await client.ConnectAsync(host, port, timeout.Token);
                        Write($"sprig: ready on port {port}");
                        Set(RunnerState.Ready, pid: process.Id);
                        return;
                    }
                    catch (Exception e) when (e is SocketException or OperationCanceledException) { }
                }
                await Task.Delay(250);
            }
        }

        void Set(RunnerState state, int? pid = null, int? exit = null, string? error = null)
        {
            lock (_gate)
            {
                // Once stopped, a late exit or readiness report mustn't bring it back.
                if (Status.State == RunnerState.Stopped && state != RunnerState.Stopped) return;
                Status = Status with { State = state, Pid = pid ?? Status.Pid, Exit = exit, Error = error, Since = DateTimeOffset.UtcNow };
            }
        }

        void Write(string line)
        {
            lock (_gate)
            {
                try { _log?.WriteLine(line); } catch (ObjectDisposedException) { /* the runner ended mid-line */ }
            }
        }
    }
}
