using Sprig.Engine.Store;

namespace Sprig.Engine.Sprouts;

/// <summary>
/// The ports each sprout holds, in <c>ports.json</c>, keyed by sprout and then by endpoint. Every change happens under
/// a lock file, so two <c>sprig up</c>s at once can't hand out the same port.
/// </summary>
public sealed class PortLeases(ISprigPaths paths)
{
    string File => Path.Combine(paths.Root, "ports.json");

    public IReadOnlyDictionary<string, int> Get(string sprout)
    {
        using var _ = Lock();
        return Load().TryGetValue(sprout, out var mine) ? mine : new Dictionary<string, int>();
    }

    public IReadOnlyDictionary<string, Dictionary<string, int>> All()
    {
        using var _ = Lock();
        return Load();
    }

    /// <summary>
    /// Run <paramref name="decide"/> with this sprout's leases and every port other sprouts hold, all under the lock.
    /// When it returns leases, they replace this sprout's.
    /// </summary>
    public T Update<T>(string sprout, Func<IReadOnlyDictionary<string, int>, IReadOnlySet<int>, (T Result, IReadOnlyDictionary<string, int>? Leases)> decide)
    {
        using var _ = Lock();
        var data = Load();
        var mine = data.TryGetValue(sprout, out var held) ? held : new Dictionary<string, int>();
        var others = data.Where(kv => kv.Key != sprout).SelectMany(kv => kv.Value.Values).ToHashSet();

        var (result, leases) = decide(mine, others);
        if (leases is not null)
        {
            data[sprout] = new Dictionary<string, int>(leases);
            JsonFile.Write(File, data);
        }
        return result;
    }

    public void Release(string sprout)
    {
        using var _ = Lock();
        var data = Load();
        if (data.Remove(sprout)) JsonFile.Write(File, data);
    }

    Dictionary<string, Dictionary<string, int>> Load() => JsonFile.Read<Dictionary<string, Dictionary<string, int>>>(File) ?? [];

    IDisposable Lock() => new FileLock(File + ".lock");

    /// <summary>A cross-process lock: a file opened with no sharing.</summary>
    sealed class FileLock : IDisposable
    {
        readonly FileStream _stream;

        public FileLock(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var deadline = Environment.TickCount64 + 10_000;
            while (true)
            {
                try
                {
                    _stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    return;
                }
                catch (IOException) when (Environment.TickCount64 < deadline)
                {
                    Thread.Sleep(15);
                }
            }
        }

        public void Dispose() => _stream.Dispose();
    }
}
