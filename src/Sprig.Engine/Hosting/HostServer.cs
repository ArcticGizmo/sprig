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
    long _lastActivity = Environment.TickCount64;

    public static string Version => typeof(HostServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Returns 0 after a clean shutdown, 2 when another host already serves this store.</summary>
    public async Task<int> RunAsync()
    {
        using var single = new Mutex(initiallyOwned: true, HostProtocol.MutexName(paths), out var mine);
        if (!mine) return 2;

        ConsoleSignal.Prepare();
        _ = WatchIdleAsync();
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
        }
        return 0;
    }

    async Task ServeAsync(NamedPipeServerStream pipe)
    {
        await using var _ = pipe;
        Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);
        try
        {
            using var reader = new StreamReader(pipe, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            var line = await reader.ReadLineAsync();
            if (line is null) return;
            var response = Handle(JsonSerializer.Deserialize<HostRequest>(line, HostProtocol.Json));
            await writer.WriteLineAsync(JsonSerializer.Serialize(response, HostProtocol.Json));
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            // A client that went away or sent nonsense only loses its own answer.
        }
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
                "stop" when request.Sprout is { } sprout => new HostResponse(_supervisor.Stop(sprout, request.Name), Runners: _supervisor.List(),
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
