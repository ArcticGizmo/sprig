using System.IO.Pipes;
using System.Text.Json;
using Sprig.Engine.Store;

namespace Sprig.Engine.Hosting;

/// <summary>
/// The body of <c>sprig-host</c>: one per store (held by a named mutex), answering one JSON request per pipe connection.
/// It quits after <paramref name="idle"/> with no runners alive and no requests, or when asked to.
/// </summary>
public sealed class HostServer(ISprigPaths paths, TimeSpan idle)
{
    readonly Supervisor _supervisor = new(paths);
    readonly CancellationTokenSource _quit = new();

    // Later than _quit: subscribers hear the apps stop as the host ends, rather than losing the host while they still run.
    readonly CancellationTokenSource _ended = new();
    long _lastActivity = Environment.TickCount64;

    /// <summary>Changes that come this close together go to subscribers as one line: a stop, say, marks every runner of
    /// a workspace in turn.</summary>
    static readonly TimeSpan Gather = TimeSpan.FromMilliseconds(50);

    public static string Version => typeof(HostServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Returns 0 after a clean shutdown, 2 when another host already serves this store.</summary>
    public async Task<int> RunAsync()
    {
        using var single = new Mutex(initiallyOwned: true, HostProtocol.MutexName(paths), out var mine);
        if (!mine) return 2;

        ConsoleSignal.Prepare();
        _ = WatchIdleAsync();
        _ = SampleAsync();
        try
        {
            while (!_quit.IsCancellationRequested)
            {
                var pipe = new NamedPipeServerStream(HostProtocol.PipeName(paths), PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try { await pipe.WaitForConnectionAsync(_quit.Token); }
                catch (OperationCanceledException) { await pipe.DisposeAsync(); break; }
                _ = ServeAsync(pipe);
            }
        }
        finally
        {
            // Every app gets the same polite stop as `sprig stop`; anything left after that dies with its job.
            await _supervisor.DisposeAsync();
            _ended.Cancel();
        }
        return 0;
    }

    async Task ServeAsync(NamedPipeServerStream pipe)
    {
        await using var _ = pipe;
        // Only when a connection opens: a subscriber holding one open mustn't keep an idle host alive.
        Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);
        try
        {
            using var reader = new StreamReader(pipe, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            var line = await reader.ReadLineAsync();
            if (line is null) return;
            var request = JsonSerializer.Deserialize<HostRequest>(line, HostProtocol.Json);
            if (request?.Op == "subscribe")
            {
                await SubscribeAsync(reader, writer, request.Usage == true);
                return;
            }
            var response = Handle(request);
            await writer.WriteLineAsync(JsonSerializer.Serialize(response, HostProtocol.Json));
        }
        catch (Exception e) when (e is IOException or JsonException or OperationCanceledException)
        {
            // A client that went away or sent nonsense only loses its own answer.
        }
    }

    /// <summary>
    /// The runner list now, then again whenever it changes, until the client goes or the host has ended. A client that
    /// reads slowly gets the latest list when it catches up, not every one in between, so nothing piles up here.
    /// </summary>
    async Task SubscribeAsync(StreamReader reader, StreamWriter writer, bool usage)
    {
        using var over = CancellationTokenSource.CreateLinkedTokenSource(_ended.Token);
        // A subscriber sends nothing after asking, so anything read (end of stream, mostly) means it has gone. Without
        // this, a gone subscriber would only be noticed at the next change, which may be hours away.
        var gone = WatchForGoneAsync(reader, over);
        try
        {
            var seen = -1L;
            var samples = -1L;
            IReadOnlyList<RunnerStatus>? sent = null;
            while (true)
            {
                seen = await _supervisor.ChangedSinceAsync(seen, over.Token);
                await Task.Delay(Gather, over.Token);
                var now = _supervisor.List();
                if (!usage) now = now.Select(r => r with { Usage = null }).ToArray();
                var sampled = usage && _supervisor.Samples != samples;
                if (sent is not null && !sampled && now.SequenceEqual(sent)) continue;
                sent = now;
                samples = _supervisor.Samples;
                await writer.WriteLineAsync(JsonSerializer.Serialize(new HostResponse(true, Pid: Environment.ProcessId, Version: Version, Runners: now),
                    HostProtocol.Json));
            }
        }
        finally
        {
            over.Cancel();
            await gone;
        }
    }

    static async Task WatchForGoneAsync(StreamReader reader, CancellationTokenSource over)
    {
        try { await reader.ReadLineAsync(over.Token); }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
        try { over.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    HostResponse Handle(HostRequest? request)
    {
        try
        {
            return request?.Op switch
            {
                "ping" => new HostResponse(true, Pid: Environment.ProcessId, Version: Version, Runners: _supervisor.List()),
                "list" => new HostResponse(true, Runners: _supervisor.List()),
                "start" when request.Specs is { } specs => new HostResponse(true, Runners: _supervisor.Start(specs, request.Restart ?? [], request.Hold == true)),
                "stop" when request.Workspace is { } workspace => new HostResponse(_supervisor.Stop(workspace, request.Name), Runners: _supervisor.List(),
                    Error: null),
                "shutdown" => Shutdown(),
                _ => new HostResponse(false, $"unknown request '{request?.Op}'"),
            };
        }
        catch (Exception e)
        {
            return new HostResponse(false, e.Message);
        }
    }

    HostResponse Shutdown()
    {
        _quit.CancelAfter(TimeSpan.FromMilliseconds(100));
        return new HostResponse(true);
    }

    async Task SampleAsync()
    {
        using var beat = new PeriodicTimer(Supervisor.SampleEvery);
        try
        {
            while (await beat.WaitForNextTickAsync(_quit.Token)) _supervisor.Sample();
        }
        catch (OperationCanceledException) { }
    }

    async Task WatchIdleAsync()
    {
        while (!_quit.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(5), _quit.Token); }
            catch (OperationCanceledException) { return; }
            if (_supervisor.AnyAlive) Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);
            else if (Environment.TickCount64 - Interlocked.Read(ref _lastActivity) > idle.TotalMilliseconds) _quit.Cancel();
        }
    }
}
