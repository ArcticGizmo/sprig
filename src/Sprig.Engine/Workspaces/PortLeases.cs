using Sprig.Engine.Store;

namespace Sprig.Engine.Workspaces;

/// <summary>
/// The ports each workspace holds, in <c>ports.json</c>, keyed by workspace and then by endpoint. Every change happens under
/// a lock file, so two <c>sprig up</c>s at once can't hand out the same port.
/// </summary>
public sealed class PortLeases(ISprigPaths paths)
{
    string File => Path.Combine(paths.Root, "ports.json");

    public IReadOnlyDictionary<string, int> Get(string workspace)
    {
        using var _ = Lock();
        return Load().TryGetValue(workspace, out var mine) ? mine : new Dictionary<string, int>();
    }

    public IReadOnlyDictionary<string, Dictionary<string, int>> All()
    {
        using var _ = Lock();
        return Load();
    }

    /// <summary>
    /// Run <paramref name="decide"/> with this workspace's leases and every port other workspaces hold, all under the lock.
    /// When it returns leases, they replace this workspace's.
    /// </summary>
    public T Update<T>(string workspace, Func<IReadOnlyDictionary<string, int>, IReadOnlySet<int>, (T Result, IReadOnlyDictionary<string, int>? Leases)> decide)
    {
        using var _ = Lock();
        var data = Load();
        var mine = data.TryGetValue(workspace, out var held) ? held : new Dictionary<string, int>();
        var others = data.Where(kv => kv.Key != workspace).SelectMany(kv => kv.Value.Values).ToHashSet();

        var (result, leases) = decide(mine, others);
        if (leases is not null)
        {
            data[workspace] = new Dictionary<string, int>(leases);
            JsonFile.Write(File, data);
        }
        return result;
    }

    public void Release(string workspace)
    {
        using var _ = Lock();
        var data = Load();
        if (data.Remove(workspace)) JsonFile.Write(File, data);
    }

    /// <summary>The old app kept a <c>ports.json</c> of another shape in the same store folder. One that can't be read
    /// counts as holding nothing, and is replaced at the next change; ports in use are still found by asking what's
    /// listening.</summary>
    Dictionary<string, Dictionary<string, int>> Load()
    {
        try { return JsonFile.Read<Dictionary<string, Dictionary<string, int>>>(File) ?? []; }
        catch (System.Text.Json.JsonException) { return []; }
    }

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
