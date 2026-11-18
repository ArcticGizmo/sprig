using System.Diagnostics;
using Sprig.Engine.Hosting;
using Sprig.Engine.Live;
using Sprig.Engine.Workspaces;

namespace Sprig.Tests.Live;

/// <summary>Against a throwaway store's files and, for the host, one in this process with no apps.</summary>
public sealed class LiveStatusTests : IDisposable
{
    readonly TempStore _store = new();
    readonly List<object> _lines = [];
    static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    public void Dispose() => _store.Dispose();

    static WorkspaceRecord Record(string orchestration, string name) => new(orchestration, $@"C:\src\{orchestration}.yaml", name, $"feat-{name}", "stable",
        [], [], [], WorkspaceState.Up, Now, Now);

    async Task<T> Heard<T>(Func<T, bool> match)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(15))
        {
            lock (_lines)
                if (_lines.OfType<T>().FirstOrDefault(match) is { } found) return found;
            await Task.Delay(50);
        }
        lock (_lines) throw new TimeoutException($"never heard it; heard: {string.Join("\n", _lines.Select(WatchLines.Serialize))}");
    }

    [Fact]
    public async Task Saves_to_the_store_and_a_host_that_starts_and_ends_each_reach_a_watcher_of_the_workspaces_it_follows()
    {
        var store = new WorkspaceStore(_store.Paths);
        store.Save(Record("storefront", "1"));
        store.Save(Record("other", "1"));
        using var stop = new CancellationTokenSource();
        var run = new LiveStatus(_store.Paths, new HostClient(_store.Paths)).RunAsync(r => r.Orchestration == "storefront", usage: false,
            line => { lock (_lines) _lines.Add(line); }, stop.Token);

        var first = await Heard<WatchSnapshot>(_ => true);
        Assert.False(first.Host.Running);
        Assert.Equal(["storefront/1"], first.Workspaces.Select(w => w.Key));

        store.Save(Record("storefront", "1") with { Paused = new PausedRun([], [], Now) });
        await Heard<WatchWorkspace>(w => w.Key == "storefront/1" && w.Paused);
        store.Save(Record("storefront", "2"));
        await Heard<WatchWorkspace>(w => w.Key == "storefront/2");
        store.Delete("storefront", "1");
        await Heard<WatchWorkspaceRemoved>(w => w.Key == "storefront/1");

        // Its idle check runs every 5 s, so with nothing to run it ends a few seconds after starting.
        var host = new HostServer(_store.Paths, TimeSpan.FromSeconds(1)).RunAsync();
        await Heard<WatchSnapshot>(s => s.Host.Running && s.Host.Pid == Environment.ProcessId);
        Assert.Equal(0, await host.WaitAsync(TimeSpan.FromSeconds(20)));
        await Heard<WatchHost>(h => !h.Running);

        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        lock (_lines) Assert.DoesNotContain(_lines.OfType<WatchWorkspace>(), w => w.Orchestration == "other");
    }
}
