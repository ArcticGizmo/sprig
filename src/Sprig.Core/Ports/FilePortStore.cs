using Sprig.Core.Settings;
using Sprig.Core.Store;

namespace Sprig.Core.Ports;

/// <summary>
/// File-backed <see cref="IPortStore"/>. All state lives in one JSON file
/// (<see cref="ISprigPaths.PortsFile"/>); every mutation takes a cross-process file lock and
/// does an atomic read-modify-write, so concurrent <c>create</c>s cannot double-allocate.
/// </summary>
/// <remarks>
/// The allocation <see cref="PortPolicy"/> (range + restricted ports) is read on every operation,
/// so edits made in Settings take effect for the next allocation without restarting the app.
/// </remarks>
public sealed class FilePortStore : IPortStore
{
    static readonly IReadOnlySet<int> NoRestrictions = new HashSet<int>();

    readonly ISprigPaths _paths;
    readonly Func<PortPolicy> _policy;

    /// <summary>Fixed-range store (used by tests and as a simple default).</summary>
    public FilePortStore(ISprigPaths paths, int rangeStart = SprigSettings.DefaultRangeStart,
        int rangeEndExclusive = SprigSettings.DefaultRangeEndExclusive)
    {
        if (rangeEndExclusive <= rangeStart)
            throw new ArgumentException("port range end must be greater than start");
        _paths = paths;
        var policy = new PortPolicy(rangeStart, rangeEndExclusive, NoRestrictions);
        _policy = () => policy;
    }

    /// <summary>Live store: reads the range + restricted ports from settings on each call.</summary>
    public FilePortStore(ISprigPaths paths, ISettingsStore settings)
    {
        _paths = paths;
        _policy = () => PortPolicy.From(settings.Get());
    }

    public IReadOnlyDictionary<string, int> Acquire(string workspace, IReadOnlyList<string> portNames)
    {
        var policy = CurrentPolicy();
        using var _ = Lock();
        var data = Load();

        var mine = data.Leases.TryGetValue(workspace, out var existing)
            ? new Dictionary<string, int>(existing)
            : new Dictionary<string, int>();

        // Ports in use by *other* workspaces are off-limits.
        var used = new HashSet<int>(
            data.Leases.Where(kv => kv.Key != workspace).SelectMany(kv => kv.Value.Values));
        // Plus any this workspace already holds (including names not requested this call).
        foreach (var p in mine.Values) used.Add(p);

        var result = new Dictionary<string, int>();
        foreach (var name in portNames)
        {
            if (mine.TryGetValue(name, out var already))
            {
                result[name] = already; // deterministic: reuse existing
                continue;
            }
            var port = NextFree(used, policy);
            mine[name] = port;
            used.Add(port);
            result[name] = port;
        }

        data.Leases[workspace] = mine;
        Save(data);
        return result;
    }

    public void Release(string workspace)
    {
        using var _ = Lock();
        var data = Load();
        if (data.Leases.Remove(workspace)) Save(data);
    }

    public IReadOnlyDictionary<string, int>? Peek(string workspace)
    {
        using var _ = Lock();
        var data = Load();
        return data.Leases.TryGetValue(workspace, out var mine)
            ? new Dictionary<string, int>(mine)
            : null;
    }

    public IReadOnlyList<PortLease> ListLeases()
    {
        using var _ = Lock();
        var data = Load();
        return data.Leases
            .SelectMany(ws => ws.Value.Select(kv => new PortLease(ws.Key, kv.Key, kv.Value)))
            .OrderBy(l => l.Port)
            .ToList();
    }

    public PortReport Describe(int port)
    {
        var policy = CurrentPolicy();
        if (port < policy.RangeStart || port >= policy.RangeEndExclusive)
            return new PortReport(port, PortStatus.OutOfRange, null);

        // A live lease wins over the restricted flag — it's genuinely occupied right now.
        var held = FindLease(port);
        if (held is not null)
            return new PortReport(port, PortStatus.InUse, $"{held.Workspace} / {held.Name}");

        if (policy.Restricted.Contains(port))
            return new PortReport(port, PortStatus.Restricted, null);

        return new PortReport(port, PortStatus.Available, null);
    }

    PortLease? FindLease(int port)
    {
        using var _ = Lock();
        var data = Load();
        foreach (var (ws, map) in data.Leases)
            foreach (var (name, p) in map)
                if (p == port)
                    return new PortLease(ws, name, port);
        return null;
    }

    PortPolicy CurrentPolicy()
    {
        var policy = _policy();
        if (policy.RangeEndExclusive <= policy.RangeStart)
            throw new PortAllocationException(
                "the configured port range is invalid (end must be greater than start) — fix it in Settings");
        return policy;
    }

    static int NextFree(HashSet<int> used, PortPolicy policy)
    {
        for (var p = policy.RangeStart; p < policy.RangeEndExclusive; p++)
            if (!used.Contains(p) && !policy.Restricted.Contains(p))
                return p;
        throw new PortAllocationException(
            $"port range {policy.RangeStart}-{policy.RangeEndExclusive - 1} is exhausted " +
            $"({used.Count} in use, {policy.Restricted.Count} restricted)");
    }

    PortStoreData Load()
        => JsonFile.Read<PortStoreData>(_paths.PortsFile) ?? new PortStoreData();

    void Save(PortStoreData data) => JsonFile.Write(_paths.PortsFile, data);

    IDisposable Lock() => new FileLock(_paths.PortsFile + ".lock");

    sealed class PortStoreData
    {
        // workspace -> (port name -> port number)
        public Dictionary<string, Dictionary<string, int>> Leases { get; init; } = new();
    }

    /// <summary>A best-effort cross-process lock via an exclusively-opened lock file.</summary>
    sealed class FileLock : IDisposable
    {
        readonly FileStream _stream;

        public FileLock(string path, int timeoutMs = 10_000)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var start = Environment.TickCount64;
            while (true)
            {
                try
                {
                    _stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    return;
                }
                catch (IOException) when (Environment.TickCount64 - start < timeoutMs)
                {
                    Thread.Sleep(15);
                }
            }
        }

        public void Dispose() => _stream.Dispose();
    }
}
