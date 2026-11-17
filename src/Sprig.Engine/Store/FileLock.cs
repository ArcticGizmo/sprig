namespace Sprig.Engine.Store;

/// <summary>A cross-process lock: a file opened with no sharing, waited for up to ten seconds.</summary>
internal sealed class FileLock : IDisposable
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
