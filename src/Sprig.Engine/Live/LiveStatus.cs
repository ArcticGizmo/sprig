using System.Threading.Channels;
using Sprig.Engine.Hosting;
using Sprig.Engine.Planning;
using Sprig.Engine.Store;
using Sprig.Engine.Workspaces;

namespace Sprig.Engine.Live;

/// <summary>
/// What sprig's workspaces and apps are doing, as it changes: the host's runner feed and the store's workspace and plan
/// files, put together into <see cref="WatchLines"/>. Never starts the host, and writes nothing to the store.
/// </summary>
public sealed class LiveStatus(ISprigPaths paths, HostClient host)
{
    /// <summary>Saves that come this close together are read once: making a workspace ready saves its record and its
    /// plan in turn.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(100);

    sealed record Input(bool FromHost, HostResponse? Answer = null, bool Missed = false);

    /// <summary>
    /// Hand each line to <paramref name="emit"/>, a snapshot first, until <paramref name="cancellation"/> fires. The
    /// first waits until the host has been asked, so it never shows a running app as off.
    /// </summary>
    /// <param name="include">Which workspaces to follow; one made later that passes is followed from then.</param>
    public async Task RunAsync(Func<WorkspaceRecord, bool> include, bool usage, Action<object> emit, CancellationToken cancellation)
    {
        var inputs = Channel.CreateUnbounded<Input>(new UnboundedChannelOptions { SingleReader = true });
        var store = new WorkspaceStore(paths);
        using var watcher = new StoreWatcher(Path.Combine(paths.Root, "workspaces"), missed => inputs.Writer.TryWrite(new Input(false, Missed: missed)));
        var feed = Task.Run(async () =>
        {
            await foreach (var answer in host.SubscribeAnswersAsync(usage, cancellation: cancellation))
                inputs.Writer.TryWrite(new Input(true, answer));
        }, CancellationToken.None);

        var workspaces = Load(store, include) ?? [];
        HostResponse? answer = null;
        var asked = false;
        var diff = new WatchDiff();
        try
        {
            await foreach (var input in inputs.Reader.ReadAllAsync(cancellation))
            {
                if (input.FromHost)
                {
                    answer = input.Answer;
                    asked = true;
                }
                else if (Load(store, include) is { } loaded) workspaces = loaded;
                // Caught mid-save: read again shortly, since nothing else may come to prompt it.
                else _ = AgainAsync(inputs.Writer, cancellation);
                if (!asked) continue;
                foreach (var line in diff.Next(WatchLines.Build(workspaces, answer, usage), fresh: input.Missed)) emit(line);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        await feed;
    }

    /// <summary>Null when a file couldn't be read just then.</summary>
    static IReadOnlyList<(WorkspaceRecord, Plan?)>? Load(WorkspaceStore store, Func<WorkspaceRecord, bool> include)
    {
        try
        {
            return store.List().Where(include).Select(r => (r, store.GetPlan(r.Orchestration, r.Name))).ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    static async Task AgainAsync(ChannelWriter<Input> inputs, CancellationToken cancellation)
    {
        try { await Task.Delay(TimeSpan.FromMilliseconds(250), cancellation); }
        catch (OperationCanceledException) { return; }
        inputs.TryWrite(new Input(false));
    }

    /// <summary>
    /// Says when anything under the workspaces folder changes, once it has settled. Records and plans are saved by
    /// writing a temp file and renaming it, so a rename is a save. When the watcher's buffer overflows, changes were
    /// missed, and it says so. Until the folder exists (a new store), it looks for it every few seconds.
    /// </summary>
    sealed class StoreWatcher : IDisposable
    {
        readonly string _folder;
        readonly Action<bool> _changed;
        readonly Timer _settle;
        readonly Timer _lookForFolder;
        readonly Lock _gate = new();
        FileSystemWatcher? _watcher;
        bool _missed;
        bool _disposed;

        public StoreWatcher(string folder, Action<bool> changed)
        {
            _folder = folder;
            _changed = changed;
            _settle = new Timer(_ => Settled());
            _lookForFolder = new Timer(_ => Start());
            Start();
        }

        void Start()
        {
            lock (_gate)
            {
                if (_disposed || _watcher is not null) return;
                if (!Directory.Exists(_folder))
                {
                    _lookForFolder.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
                    return;
                }
                var watcher = new FileSystemWatcher(_folder)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                    InternalBufferSize = 64 * 1024,
                };
                watcher.Created += (_, _) => Touched();
                watcher.Changed += (_, _) => Touched();
                watcher.Deleted += (_, _) => Touched();
                watcher.Renamed += (_, _) => Touched();
                watcher.Error += (_, _) =>
                {
                    lock (_gate) _missed = true;
                    Touched();
                };
                watcher.EnableRaisingEvents = true;
                _watcher = watcher;
            }
            // Anything saved before the watcher started would otherwise go unseen.
            Touched();
        }

        void Touched()
        {
            lock (_gate)
                if (!_disposed) _settle.Change(Settle, Timeout.InfiniteTimeSpan);
        }

        void Settled()
        {
            bool missed;
            lock (_gate)
            {
                if (_disposed) return;
                missed = _missed;
                _missed = false;
            }
            _changed(missed);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _watcher?.Dispose();
            }
            _settle.Dispose();
            _lookForFolder.Dispose();
        }
    }
}
