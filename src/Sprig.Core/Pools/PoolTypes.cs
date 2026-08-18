namespace Sprig.Core.Pools;

/// <summary>Thrown when a pool operation can't proceed (pool full, workspace not in the pool, etc.).</summary>
public sealed class PoolException(string message) : Exception(message);

/// <summary>How a workspace's warm state is handled when its claim branch is cut. Both modes cut the branch
/// at the same start point (default: base) and reset tracked files to it — they differ only in what happens
/// to the expensive local artifacts.</summary>
public enum CheckoutMode
{
    /// <summary>Keep the warm environment: installed deps (node_modules) and docker volumes stay as they are,
    /// no reinstall — the fast path. "Give me a clean main-based branch on top of what I already have."</summary>
    Keep,
    /// <summary>Fresh start: reinstall deps (setup) and wipe docker volumes for clean runtime data — a clean
    /// slate down to the environment.</summary>
    Fresh,
}
