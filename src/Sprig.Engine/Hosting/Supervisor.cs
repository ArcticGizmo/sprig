using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Sprig.Engine.Store;

namespace Sprig.Engine.Hosting;

/// <summary>
/// Runs apps for the host: build (one at a time per repo), wait for dependencies, start in a job, log every line to a
/// file in the store, and call an app ready once its checks pass (a log line, a health url, or else its port). An app
/// with a restart policy starts again, with backoff, when it exits on its own. Stopping asks politely (Ctrl+C) and
/// kills only what's still running after <see cref="Grace"/>. Everything it starts dies with it, because each runner's
/// job closes when the host does.
/// </summary>
public sealed class Supervisor(ISprigPaths paths) : IAsyncDisposable
{
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(10);

    /// <summary>How long a held runner waits for its release: long enough for a first pull of every image, short
    /// enough that a CLI that died mid-up doesn't leave runners waiting for good.</summary>
    public static readonly TimeSpan HoldLimit = TimeSpan.FromMinutes(15);

    readonly ConcurrentDictionary<(string Workspace, string Name), Runner> _runners = new();
    readonly ConcurrentDictionary<string, SemaphoreSlim> _buildLocks = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, TaskCompletionSource> _holds = new();
    readonly Lock _startGate = new();

    public bool AnyAlive => _runners.Values.Any(r => r.Status.State.IsAlive() || r.Status.State == RunnerState.Stopping);

    public IReadOnlyList<RunnerStatus> List() => _runners.Values.Select(r => r.Status).OrderBy(s => s.Workspace).ThenBy(s => s.Name).ToArray();

    /// <summary>
    /// Have <paramref name="specs"/> going. A runner named in <paramref name="restart"/> starts afresh; any other starts
    /// only when it isn't already going or (for a job) finished, so starting one app doesn't bounce what it depends on.
    /// Returns at once; builds, waits and starts carry on in the background and show in <see cref="List"/>.
    /// </summary>
    /// <param name="hold">Build, then wait until a start for the same workspace without a hold releases them.</param>
    public IReadOnlyList<RunnerStatus> Start(IReadOnlyList<RunnerSpec> specs, IReadOnlyCollection<string> restart, bool hold = false)
    {
        lock (_startGate)
        {
            foreach (var workspace in specs.Select(s => s.Workspace).Distinct())
            {
                if (hold) _holds.GetOrAdd(workspace, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                else Release(workspace);
            }

            var started = new List<Runner>();
            foreach (var spec in specs)
            {
                var key = (spec.Workspace, spec.Name);
                Runner? previous = null;
                if (_runners.TryGetValue(key, out var existing))
                {
                    var keep = !restart.Contains(spec.Name) && (existing.Status.State.IsAlive() || existing.Status.State == RunnerState.Completed);
                    if (keep) continue;
                    _ = existing.StopAsync(Grace);
                    previous = existing;
                }
                var runner = new Runner(spec, HostProtocol.LogFile(paths, spec.Workspace, spec.Name),
                    spec.BuildKey is { } k ? _buildLocks.GetOrAdd(k, _ => new SemaphoreSlim(1, 1)) : null,
                    name => _runners.TryGetValue((spec.Workspace, name), out var r) ? r.Status : null,
                    previous,
                    () => _holds.TryGetValue(spec.Workspace, out var held) ? held.Task : Task.CompletedTask);
                _runners[key] = runner;
                started.Add(runner);
            }
            foreach (var runner in started) _ = Task.Run(runner.RunAsync);
            return started.Select(r => r.Status).ToArray();
        }
    }

    /// <summary>
    /// Stop one app, which stays listed as stopped; or, with no <paramref name="name"/>, the whole workspace, whose
    /// runners are forgotten once they've stopped (it's going down or away). Returns at once; each runner shows
    /// <see cref="RunnerState.Stopping"/> until it has.
    /// </summary>
    public bool Stop(string workspace, string? name)
    {
        var targets = _runners.Where(kv => kv.Key.Workspace == workspace && (name is null || kv.Key.Name == name)).ToArray();
        foreach (var (key, runner) in targets)
        {
            var stopping = runner.StopAsync(Grace);
            if (name is null) _ = stopping.ContinueWith(_ => _runners.TryRemove(new KeyValuePair<(string, string), Runner>(key, runner)));
        }
        // Only once they're all marked as stopping, or a held runner could wake and start.
        if (name is null) Release(workspace);
        return targets.Length > 0;
    }

    public async ValueTask DisposeAsync()
        => await Task.WhenAll(_runners.Values.Select(r => r.StopAsync(Grace)));

    void Release(string workspace)
    {
        if (_holds.TryRemove(workspace, out var held)) held.TrySetResult();
    }

    /// <param name="peer">Another runner of the same workspace, by name, for waiting on.</param>
    /// <param name="previous">The runner this one replaces; it has to be gone before this one builds, since a running
    /// app locks the files its build would overwrite.</param>
    /// <param name="held">Completes once the workspace's hold (if any) is released.</param>
    sealed class Runner(RunnerSpec spec, string logFile, SemaphoreSlim? buildLock, Func<string, RunnerStatus?> peer, Runner? previous,
        Func<Task> held)
    {
        // The shells sprig starts apps through, and the console host Windows gives each one; neither is the app.
        static readonly HashSet<string> Wrappers = new(StringComparer.OrdinalIgnoreCase) { "cmd", "conhost", "sh" };

        static readonly HttpClient Http = new(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = true,
            ConnectCallback = ConnectLoopbackAsync,
        })
        {
            Timeout = TimeSpan.FromSeconds(2),
        };

        /// <summary>
        /// Each loopback address in turn, briefly. An app may bind only one of them, and on Windows a refused connection
        /// takes about two seconds to fail, which would use up a whole health request on the wrong address.
        /// </summary>
        static async ValueTask<Stream> ConnectLoopbackAsync(SocketsHttpConnectionContext context, CancellationToken token)
        {
            var endpoint = context.DnsEndPoint;
            IPAddress[] addresses = endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                ? [IPAddress.Loopback, IPAddress.IPv6Loopback]
                : await Dns.GetHostAddressesAsync(endpoint.Host, token);
            foreach (var address in addresses)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromMilliseconds(500));
                    await socket.ConnectAsync(address, endpoint.Port, timeout.Token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (Exception e) when (e is SocketException or OperationCanceledException)
                {
                    socket.Dispose();
                    token.ThrowIfCancellationRequested();
                }
            }
            throw new HttpRequestException($"nothing answered on {endpoint.Host}:{endpoint.Port}");
        }

        readonly JobObject _job = new();
        readonly Lock _gate = new();
        readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _stopAsked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly Regex? _readyLog = spec.ReadyLog is { } pattern ? new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1)) : null;

        /// <summary>A line saying where the app serves: ASP.NET's "Now listening on: http://localhost:5080", Vite's
        /// "Local: http://localhost:5173/".</summary>
        static readonly Regex Serving = new(@"(listening|local:|running|serving|available)\D{0,40}?https?://(localhost|127\.0\.0\.1|0\.0\.0\.0|\[::1?\]):(?<port>\d{2,5})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

        // This run's own port has been announced, or another one has been reported in its place.
        bool _servesOwnPort;
        bool _otherPortSaid;
        StreamWriter? _log;
        DateOnly? _logDate;
        Process? _process;
        Task? _stopping;
        int _restarts;

        public RunnerStatus Status { get; private set; } = new(spec.Workspace, spec.Name, RunnerState.Building, null, spec.Port, null, logFile, DateTimeOffset.UtcNow);

        bool Stopping => _stopping is not null;

        public async Task RunAsync()
        {
            try
            {
                if (previous is not null) await previous._ended.Task;
                if (Stopping) return;
                Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
                lock (_gate)
                {
                    _log = new StreamWriter(new FileStream(logFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8) { AutoFlush = true };
                    _logDate = null;
                }

                if (spec.Build is { } build)
                {
                    Write($"sprig: building with {build}");
                    if (spec.BuildDoing is { } doing) lock (_gate) Status = Status with { Doing = doing };
                    if (buildLock is not null) await buildLock.WaitAsync();
                    int code;
                    try { code = await RunToEndAsync(build); }
                    finally { buildLock?.Release(); }
                    lock (_gate) Status = Status with { Doing = null };
                    if (Stopping) return;
                    if (code != 0)
                    {
                        Set(RunnerState.Failed, exit: code, error: $"the build exited with code {code}");
                        return;
                    }
                }

                var hold = held();
                if (!hold.IsCompleted)
                {
                    Set(RunnerState.Waiting);
                    Write("sprig: waiting for the workspace's infra");
                    var first = await Task.WhenAny(hold, _stopAsked.Task, Task.Delay(HoldLimit));
                    if (Stopping) return;
                    if (first != hold)
                    {
                        var why = $"the workspace's infra didn't come up within {HoldLimit.TotalMinutes:0} minutes";
                        Write($"sprig: {why}");
                        Set(RunnerState.Failed, error: why);
                        return;
                    }
                }

                if (await WaitForDependenciesAsync() is { } blocked)
                {
                    if (!Stopping) Set(RunnerState.Failed, error: blocked);
                    Write($"sprig: {blocked}");
                    return;
                }

                var inARow = 0;
                while (!Stopping)
                {
                    Write($"sprig: starting {spec.Command}");
                    var clock = Stopwatch.StartNew();
                    var logMatched = _readyLog is null ? null : new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    (_servesOwnPort, _otherPortSaid) = (false, false);
                    if (Launch(spec.Command, logMatched) is not { } process) return;
                    if (spec.Job) Set(RunnerState.Running, pid: process.Id);
                    else
                    {
                        Set(RunnerState.Starting, pid: process.Id);
                        _ = WatchReadyAsync(process, logMatched?.Task);
                    }

                    await process.WaitForExitAsync();
                    var exit = process.ExitCode;
                    Write($"sprig: exited with code {exit}");
                    if (Stopping) return;
                    if (spec.Job && exit == 0)
                    {
                        Set(RunnerState.Completed, exit: exit);
                        return;
                    }

                    if (clock.Elapsed >= RestartSpec.Settled) inARow = 0;
                    var again = spec.Restart is { } policy && inARow < policy.Max && (exit != 0 || (policy.Always && !spec.Job));
                    if (!again)
                    {
                        var after = inARow == 0 ? "" : $" after {inARow} {(spec.Job ? "retries" : "restarts")}";
                        if (!spec.Job) Set(RunnerState.Exited, exit: exit, error: after.Length > 0 ? $"exited with code {exit}{after}" : null);
                        else Set(RunnerState.Failed, exit: exit, error: $"exited with code {exit}{after}");
                        return;
                    }

                    inARow++;
                    _restarts++;
                    var wait = RestartSpec.Backoff(inARow);
                    var when = $"restarting in {wait.TotalSeconds:0}s ({inARow} of {spec.Restart!.Max})";
                    Write($"sprig: {when}");
                    Set(RunnerState.Restarting, exit: exit, error: $"exited with code {exit}; {when}");
                    // A child the last run left behind (a server outliving its cmd wrapper) would hold the port. Once a
                    // stop has begun the job is its to end (and close).
                    lock (_gate) if (!Stopping) _job.Terminate(process);
                    await Task.WhenAny(Task.Delay(wait), _stopAsked.Task);
                }
            }
            catch (Exception e)
            {
                Write($"sprig: {e.Message}");
                Set(RunnerState.Failed, error: e.Message);
            }
            finally
            {
                // Whatever it left behind (a child that outlived its parent) goes now, unless a stop is handling that.
                if (!Stopping)
                {
                    _job.Terminate(_process);
                    lock (_gate)
                    {
                        _log?.Dispose();
                        _log = null;
                    }
                    _ended.TrySetResult();
                }
            }
        }

        /// <summary>Stop it, politely first. Safe to call more than once; every caller gets the same stop.</summary>
        public Task StopAsync(TimeSpan grace)
        {
            lock (_gate)
            {
                if (_stopping is not null) return _stopping;
                var wasRunning = Status.State.IsAlive() && _process is { HasExited: false };
                // Marked before the stop request returns, so whoever asked sees Stopping on its next look, never a
                // stale Ready it would take for "already done".
                Status = Status with { State = RunnerState.Stopping, Since = DateTimeOffset.UtcNow };
                _stopAsked.TrySetResult();
                return _stopping = Task.Run(() => StopCoreAsync(grace, wasRunning));
            }
        }

        async Task StopCoreAsync(TimeSpan grace, bool wasRunning)
        {
            string? note = null;
            try
            {
                note = await AskToStopAsync(grace, wasRunning);
            }
            catch (Exception e)
            {
                // Nothing awaits this task's exceptions, so one left here would keep the runner Stopping forever.
                note = $"stopping went wrong ({e.GetType().Name}: {e.Message}), so it was ended";
                Write($"sprig: {note}\n{e}");
            }
            _job.Terminate(_process);
            Set(RunnerState.Stopped, force: true, error: note);
            _job.Dispose();
            lock (_gate)
            {
                _log?.Dispose();
                _log = null;
            }
            _ended.TrySetResult();
        }

        /// <summary>Ctrl+C, then wait for the app to go. Null when it went in time; otherwise what happened.</summary>
        async Task<string?> AskToStopAsync(TimeSpan grace, bool wasRunning)
        {
            string? note = null;
            if (wasRunning)
            {
                var clock = Stopwatch.StartNew();
                var asked = await ConsoleSignal.SendCtrlCAsync(_process!.Id);
                Write(asked ? "sprig: sent Ctrl+C" : "sprig: couldn't send Ctrl+C, so ending it");
                var answered = false;
                while (asked && clock.Elapsed < grace && AppStillRunning())
                {
                    // npm, vite and friends are .cmd batch files on Windows, and cmd answers Ctrl+C with "Terminate
                    // batch job (Y/N)?" and waits. Every shell in the tree shares this stdin, so one answer each will do.
                    if (!answered && clock.Elapsed > TimeSpan.FromSeconds(1) && OperatingSystem.IsWindows())
                    {
                        answered = true;
                        try
                        {
                            for (var i = 0; i < 4; i++) await _process.StandardInput.WriteLineAsync("Y");
                            await _process.StandardInput.FlushAsync();
                        }
                        catch (IOException) { /* nothing is reading any more */ }
                    }
                    await Task.Delay(150);
                }
                if (AppStillRunning())
                {
                    note = asked ? $"didn't stop within {grace.TotalSeconds:0}s of Ctrl+C, so it was ended" : "ended without Ctrl+C";
                    Write($"sprig: {note} (still running: {string.Join(", ", Remaining())})");
                }
                else Write($"sprig: stopped in {clock.Elapsed.TotalSeconds:0.0}s");
            }
            return note;
        }

        /// <summary>True while anything but a shell or console host is left in the job: those wait on a prompt
        /// ("Terminate batch job?") after Ctrl+C rather than exit, so they don't count as the app.</summary>
        bool AppStillRunning()
        {
            var ids = _job.ProcessIds();
            if (ids.Count == 0) return _process is { HasExited: false } && !OperatingSystem.IsWindows();
            foreach (var id in ids)
            {
                try
                {
                    using var p = Process.GetProcessById(id);
                    if (!Wrappers.Contains(p.ProcessName)) return true;
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException) { /* it exited meanwhile */ }
            }
            return false;
        }

        IEnumerable<string> Remaining()
        {
            foreach (var id in _job.ProcessIds())
            {
                string? name;
                try
                {
                    using var p = Process.GetProcessById(id);
                    name = p.ProcessName;
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException) { continue; }
                yield return $"{name} {id}";
            }
        }

        /// <summary>Null once everything it waits for is ready; otherwise why it can't start.</summary>
        async Task<string?> WaitForDependenciesAsync()
        {
            var names = spec.WaitFor ?? [];
            if (names.Count == 0) return null;
            Set(RunnerState.Waiting);
            Write($"sprig: waiting for {string.Join(", ", names)}");
            while (!Stopping)
            {
                var pending = false;
                foreach (var name in names)
                {
                    var other = peer(name);
                    if (other is null) return $"waits for {name}, which isn't running";
                    if (other.State.Satisfies()) continue;
                    if (other.State.IsSettled()) return $"waits for {name}, which {Describe(other)}";
                    pending = true;
                }
                if (!pending) return null;
                await Task.Delay(250);
            }
            return "stopped while waiting";
        }

        static string Describe(RunnerStatus other) => other.State switch
        {
            RunnerState.Exited or RunnerState.Failed when other.Exit is { } code => $"exited with code {code}",
            RunnerState.Failed => $"failed ({other.Error})",
            _ => $"is {other.State.ToString().ToLowerInvariant()}",
        };

        async Task<int> RunToEndAsync(CommandSpec command)
        {
            if (Launch(command) is not { } process) return -1;
            await process.WaitForExitAsync();
            _process = null;
            return process.ExitCode;
        }

        /// <summary>Null when a stop got in first. Starting under the gate means a stop either sees this process and
        /// signals it, or is seen here and nothing starts.</summary>
        /// <param name="logMatched">Completed by the first line of output that matches the ready pattern.</param>
        Process? Launch(CommandSpec command, TaskCompletionSource? logMatched = null)
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

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => Output(e.Data, logMatched);
            process.ErrorDataReceived += (_, e) => Output(e.Data, logMatched);
            lock (_gate)
            {
                if (Stopping) return null;
                process.Start();
                _job.Add(process);
                _process = process;
            }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return process;
        }

        void Output(string? line, TaskCompletionSource? logMatched)
        {
            if (line is null) return;
            Write(line);
            NoticePort(line);
            if (logMatched is null || logMatched.Task.IsCompleted) return;
            try
            {
                if (_readyLog!.IsMatch(AnsiText.Strip(line))) logMatched.TrySetResult();
            }
            catch (RegexMatchTimeoutException) { /* a pathological pattern; this line doesn't count */ }
        }

        /// <summary>
        /// An app that serves on a port of its own choosing (one fixed in its dev script, say) never opens the one sprig
        /// gave it, so it would sit at starting with no reason given. When it says it serves somewhere else, and hasn't
        /// said it serves on its own port too, that's said once, in its log and beside its state.
        /// </summary>
        void NoticePort(string line)
        {
            if (spec.Port is not { } port || _servesOwnPort) return;
            Match match;
            try { match = Serving.Match(AnsiText.Strip(line)); }
            catch (RegexMatchTimeoutException) { return; }
            if (!match.Success || !int.TryParse(match.Groups["port"].Value, out var seen)) return;
            if (seen == port)
            {
                _servesOwnPort = true;
                lock (_gate) if (Status.State == RunnerState.Starting && _otherPortSaid) Status = Status with { Error = null };
                return;
            }
            if (_otherPortSaid) return;
            _otherPortSaid = true;
            var why = $"it says it serves on {seen}, not the {port} sprig gave it";
            Write($"sprig: {why}, so it isn't ready until {port} opens. Check it takes its port from the value sprig writes for it");
            lock (_gate) if (Status.State == RunnerState.Starting) Status = Status with { Error = why };
        }

        /// <summary>
        /// Ready once every check it has passes: a matching log line, each health url answering with a success status.
        /// With neither, once its port accepts a connection; with no port either, straight away. A check that's still
        /// waiting when the process exits (or is replaced by a restart) leaves it as it is.
        /// </summary>
        async Task WatchReadyAsync(Process process, Task? logMatched)
        {
            var checks = new List<Task<bool>>();
            if (logMatched is not null) checks.Add(WaitForLogAsync(process, logMatched));
            foreach (var url in spec.HealthUrls ?? []) checks.Add(WaitForHealthAsync(process, url));
            if (checks.Count == 0 && spec.Port is { } port) checks.Add(WaitForPortAsync(process, port));

            var passed = await Task.WhenAll(checks);
            if (passed.Any(p => !p)) return;
            lock (_gate)
            {
                // Under the gate, so an exit noticed just before can't be overwritten by a late Ready.
                if (process.HasExited || !ReferenceEquals(_process, process)) return;
                if (checks.Count > 1) Write("sprig: ready");
                Set(RunnerState.Ready, pid: process.Id);
            }
        }

        async Task<bool> WaitForLogAsync(Process process, Task logMatched)
        {
            await Task.WhenAny(logMatched, process.WaitForExitAsync());
            if (!logMatched.IsCompleted) return false;
            Write($"sprig: ready: a line matched {spec.ReadyLog}");
            return true;
        }

        /// <summary>Connection errors are expected while the app starts, so only status codes are logged, and only
        /// when they change.</summary>
        async Task<bool> WaitForHealthAsync(Process process, string url)
        {
            int? last = null;
            while (!process.HasExited && !Stopping)
            {
                try
                {
                    using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                    var code = (int)response.StatusCode;
                    if (response.IsSuccessStatusCode)
                    {
                        Write($"sprig: ready: {url} answered {code}");
                        return true;
                    }
                    if (code != last) Write($"sprig: {url} answered {code}; waiting for a success status");
                    last = code;
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { }
                await Task.Delay(500);
            }
            return false;
        }

        /// <summary>Checked against the loopback addresses an app might bind.</summary>
        async Task<bool> WaitForPortAsync(Process process, int port)
        {
            while (!process.HasExited && !Stopping)
            {
                foreach (var host in new[] { "127.0.0.1", "::1" })
                {
                    try
                    {
                        using var client = new TcpClient(host.Contains(':') ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork);
                        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                        await client.ConnectAsync(host, port, timeout.Token);
                        Write($"sprig: ready on port {port}");
                        return true;
                    }
                    catch (Exception e) when (e is SocketException or OperationCanceledException) { }
                }
                await Task.Delay(250);
            }
            return false;
        }

        /// <param name="force">Only stopping may change the state once a stop has begun.</param>
        void Set(RunnerState state, int? pid = null, int? exit = null, string? error = null, bool force = false)
        {
            lock (_gate)
            {
                if (Stopping && !force) return;
                Status = Status with
                {
                    State = state, Pid = pid ?? Status.Pid, Exit = exit, Error = error, Since = DateTimeOffset.UtcNow, Restarts = _restarts,
                    Doing = state == RunnerState.Building ? Status.Doing : null,
                };
            }
        }

        void Write(string line)
        {
            var now = DateTime.Now;
            var today = DateOnly.FromDateTime(now);
            lock (_gate)
            {
                if (_log is null) return;
                try
                {
                    if (_logDate != today)
                    {
                        _log.WriteLine(LogLineFormat.DateMarker(today));
                        _logDate = today;
                    }
                    // Each line of a multi-line message gets the time, so readers never see an untimed line from sprig.
                    var prefix = LogLineFormat.Prefix(now);
                    foreach (var part in line.Split('\n')) _log.WriteLine(prefix + part.TrimEnd('\r'));
                }
                catch (ObjectDisposedException) { /* the runner ended mid-line */ }
            }
        }
    }
}
