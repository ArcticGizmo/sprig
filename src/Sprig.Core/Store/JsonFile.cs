using System.Text.Json;

namespace Sprig.Core.Store;

/// <summary>
/// Read/write JSON to disk with an atomic write (temp file + move), so an interrupted write
/// never leaves a half-written store on disk.
/// </summary>
internal static class JsonFile
{
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Read and deserialize; returns <c>default</c> if the file does not exist.</summary>
    public static T? Read<T>(string path)
    {
        if (!File.Exists(path)) return default;
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);
    }

    /// <summary>Serialize and write atomically (temp file in the same dir, then move over the target).</summary>
    public static void Write<T>(string path, T value)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $".{Path.GetFileName(path)}.tmp-{Guid.NewGuid():N}");
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));

        // Windows can transiently deny a rename-over-existing when an external handle (AV,
        // search indexer) briefly holds the destination. Retry with backoff, then clean up.
        try { MoveWithRetry(tmp, path); }
        finally { if (File.Exists(tmp)) TryDelete(tmp); }
    }

    static void MoveWithRetry(string tmp, string path)
    {
        const int attempts = 10;
        for (var i = 0; ; i++)
        {
            try { File.Move(tmp, path, overwrite: true); return; }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && i < attempts - 1)
            {
                Thread.Sleep(15 * (i + 1));
            }
        }
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* leftover temp is harmless */ }
    }
}
