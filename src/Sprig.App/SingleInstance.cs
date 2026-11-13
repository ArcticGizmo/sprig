using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Sprig.Engine.Hosting;
using Sprig.Engine.Store;

namespace Sprig.App;

/// <summary>
/// One sprig window per store. With its window closed sprig can be sitting in the tray, and launching it again then
/// should bring that one back rather than start a second copy polling the same host.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    readonly Mutex _mutex;
    readonly string _pipe;
    readonly CancellationTokenSource _stop = new();

    /// <summary>Another launch asked for the window. Raised on a background thread.</summary>
    public static event Action? ShowRequested;

    SingleInstance(Mutex mutex, string pipe)
    {
        _mutex = mutex;
        _pipe = pipe;
        _ = ListenAsync();
    }

    /// <summary>This launch's claim on the store; null when another sprig already has it and has been asked to show
    /// itself, so this one should exit.</summary>
    public static SingleInstance? Claim(ISprigPaths paths)
    {
        var id = HostProtocol.StoreId(paths);
        var mutex = new Mutex(initiallyOwned: true, @"Local\sprig-gui-" + id, out var mine);
        if (mine) return new SingleInstance(mutex, "sprig-gui-" + id);
        mutex.Dispose();
        // One that holds the claim but won't answer is stuck; better a second window than none.
        return Signal("sprig-gui-" + id) ? null : new SingleInstance(new Mutex(), "sprig-gui-" + id + "-" + Environment.ProcessId);
    }

    static bool Signal(string pipe)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipe, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(2000);
            client.Write("show\n"u8);
            return true;
        }
        catch (Exception e) when (e is TimeoutException or IOException)
        {
            return false;
        }
    }

    async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipe, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_stop.Token);
                using var reader = new StreamReader(server);
                if (await reader.ReadLineAsync(_stop.Token) == "show") ShowRequested?.Invoke();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // A launch that gave up mid-message; wait for the next.
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { /* never owned: the fallback when the owner was stuck */ }
        _mutex.Dispose();
    }
}
