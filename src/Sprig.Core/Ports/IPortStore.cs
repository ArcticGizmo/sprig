using Sprig.Core.Settings;

namespace Sprig.Core.Ports;

/// <summary>Thrown when the configured port range is exhausted.</summary>
public sealed class PortAllocationException(string message) : Exception(message);

/// <summary>The allocation policy in force: the range sprig may use and the ports it must skip.</summary>
public sealed record PortPolicy(int RangeStart, int RangeEndExclusive, IReadOnlySet<int> Restricted)
{
    public static PortPolicy From(SprigSettings s)
        => new(s.PortRangeStart, s.PortRangeEndExclusive, new HashSet<int>(s.RestrictedPorts));
}

/// <summary>One allocated port: which workspace and named port hold it.</summary>
public sealed record PortLease(string Workspace, string Name, int Port);

/// <summary>
/// A request for one named port. <see cref="Allowed"/>, when set, restricts the port to that exact
/// set (drawn from regardless of the settings range, but still skipping restricted/in-use ports);
/// when null the port is allocated from the settings range as usual.
/// </summary>
public sealed record PortRequest(string Name, IReadOnlySet<int>? Allowed = null);

/// <summary>The status of a single port relative to the current policy and live leases.</summary>
public enum PortStatus
{
    /// <summary>In range, not restricted, not leased — sprig could allocate it.</summary>
    Available,
    /// <summary>Explicitly restricted — sprig will never allocate it.</summary>
    Restricted,
    /// <summary>Currently leased to a workspace (see <see cref="PortReport.HeldBy"/>).</summary>
    InUse,
    /// <summary>Outside the configured range — sprig doesn't manage it.</summary>
    OutOfRange,
}

/// <summary>The result of a single-port status query.</summary>
public sealed record PortReport(int Port, PortStatus Status, string? HeldBy);

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

    /// <summary>
    /// As <see cref="Acquire(string,IReadOnlyList{string})"/>, but each request may pin its port to
    /// a restricted set (see <see cref="PortRequest.Allowed"/>).
    /// </summary>
    /// <exception cref="PortAllocationException">A request's range/set cannot be satisfied.</exception>
    IReadOnlyDictionary<string, int> Acquire(string workspace, IReadOnlyList<PortRequest> requests);

    /// <summary>Release every port held by <paramref name="workspace"/> (idempotent).</summary>
    void Release(string workspace);

    /// <summary>Return the workspace's current lease, or <c>null</c> if it holds none.</summary>
    IReadOnlyDictionary<string, int>? Peek(string workspace);

    /// <summary>Every port currently leased, across all workspaces, ordered by port number.</summary>
    IReadOnlyList<PortLease> ListLeases();

    /// <summary>Classify a single port against the current policy and live leases.</summary>
    PortReport Describe(int port);
}
