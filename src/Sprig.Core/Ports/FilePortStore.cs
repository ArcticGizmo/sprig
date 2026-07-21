using Sprig.Core.Store;

namespace Sprig.Core.Ports;

/// <summary>
/// File-backed <see cref="IPortStore"/>. All state lives in one JSON file
/// (<see cref="ISprigPaths.PortsFile"/>); every mutation takes a cross-process file lock and
/// does an atomic read-modify-write, so concurrent <c>create</c>s cannot double-allocate.
/// </summary>
public sealed class FilePortStore : IPortStore
{
    readonly ISprigPaths _paths;
    readonly int _rangeStart;
    readonly int _rangeEndExclusive;

    public FilePortStore(ISprigPaths paths, int rangeStart = 20000, int rangeEndExclusive = 30000)
    {
        if (rangeEndExclusive <= rangeStart)
            throw new ArgumentException("port range end must be greater than start");
        _paths = paths;
        _rangeStart = rangeStart;
        _rangeEndExclusive = rangeEndExclusive;
    }

    public IReadOnlyDictionary<string, int> Acquire(string workspace, IReadOnlyList<string> portNames)
    {
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
            var port = NextFree(used);
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

    int NextFree(HashSet<int> used)
    {
        for (var p = _rangeStart; p < _rangeEndExclusive; p++)
            if (!used.Contains(p))
                return p;
        throw new PortAllocationException(
            $"port range {_rangeStart}-{_rangeEndExclusive - 1} is exhausted ({used.Count} in use)");
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
