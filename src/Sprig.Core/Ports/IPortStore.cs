namespace Sprig.Core.Ports;

/// <summary>Thrown when the configured port range is exhausted.</summary>
public sealed class PortAllocationException(string message) : Exception(message);

/// <summary>
/// Allocates real, non-colliding host ports to workspaces from a central store.
/// Allocation is <b>deterministic per instance</b> (a workspace re-acquiring the same named
/// ports gets the same numbers), <b>non-colliding across live leases</b>, and reclaimed on
/// <see cref="Release"/>.
/// </summary>
public interface IPortStore
{
    /// <summary>
    /// Acquire (or reuse) a port for each requested name for <paramref name="workspace"/>.
    /// Names already leased to this workspace keep their number; new names get fresh ports.
    /// </summary>
    /// <exception cref="PortAllocationException">The range cannot satisfy the request.</exception>
    IReadOnlyDictionary<string, int> Acquire(string workspace, IReadOnlyList<string> portNames);

    /// <summary>Release every port held by <paramref name="workspace"/> (idempotent).</summary>
    void Release(string workspace);

    /// <summary>Return the workspace's current lease, or <c>null</c> if it holds none.</summary>
    IReadOnlyDictionary<string, int>? Peek(string workspace);
}
