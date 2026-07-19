using Sprig.Core.Store;

namespace Sprig.Tests;

/// <summary>A central store rooted in a throwaway temp directory, deleted on dispose.</summary>
public sealed class TempStore : IDisposable
{
    public ISprigPaths Paths { get; }
    public string Root { get; }

    public TempStore()
    {
        Root = Path.Combine(Path.GetTempPath(), "sprig-test-" + Guid.NewGuid().ToString("N"));
        Paths = new SprigPaths(Root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }
}
