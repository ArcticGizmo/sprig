using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Sprig.Engine.Store;

namespace Sprig.App;

/// <summary>
/// One sprig process per store, however many windows it has open. With every window closed sprig can be sitting in the
/// tray, and launching it again then should open a window in that one rather than start a second copy polling the same
/// host.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    readonly Mutex _mutex;
    readonly string _pipe;
    readonly CancellationTokenSource _stop = new();

    const string Show = "show";
    const string OpenPrefix = "open ";

    /// <summary>Another launch asked for a window, on a workspace by its key or null for any. Raised on a background
    /// thread.</summary>
    public static event Action<string?>? OpenRequested;

    SingleInstance(Mutex mutex, string pipe)
    {
        _mutex = mutex;
        _pipe = pipe;
        _ = ListenAsync();
    }

    /// <summary>This launch's claim on the store; null when another sprig already has it and has been asked for a
    /// window, so this one should exit.</summary>
    /// <param name="workspace">The workspace this launch was asked to open, by its key; null for none.</param>
    public static SingleInstance? Claim(ISprigPaths paths, string? workspace = null)
    {
        var pipe = AppInstance.PipeName(paths);
        var mutex = new Mutex(initiallyOwned: true, AppInstance.MutexName(paths), out var mine);
        if (mine) return new SingleInstance(mutex, pipe);
        mutex.Dispose();
        // One that holds the claim but won't answer is stuck; better a second window than none.
        return Signal(pipe, Message(workspace)) ? null : new SingleInstance(new Mutex(), pipe + "-" + Environment.ProcessId);
    }

    /// <summary>What a launch says to the sprig already running: one line, with any workspace key after it.</summary>
    internal static string Message(string? workspace)
        => string.IsNullOrWhiteSpace(workspace) ? Show : OpenPrefix + workspace.Trim();

    /// <summary>The workspace key a message asks for; null for any window; false when it isn't a message at all.</summary>
    internal static bool TryRead(string? line, out string? workspace)
    {
        workspace = null;
        if (line == Show) return true;
        if (line is null || !line.StartsWith(OpenPrefix, StringComparison.Ordinal)) return false;
        workspace = line[OpenPrefix.Length..].Trim();
        if (workspace.Length == 0) workspace = null;
        return true;
    }

    static bool Signal(string pipe, string message)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipe, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(2000);
            client.Write(System.Text.Encoding.UTF8.GetBytes(message + "\n"));
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
                if (TryRead(await reader.ReadLineAsync(_stop.Token), out var workspace)) OpenRequested?.Invoke(workspace);
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
