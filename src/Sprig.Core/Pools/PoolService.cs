using Sprig.Core.Stacks;
using Sprig.Core.Store;

namespace Sprig.Core.Pools;

/// <summary>
/// The pool view over a stack: the emergent set of workspaces built from it, bounded by the stack's
/// <see cref="StackDefinition.MaxSlots"/>. There is no persisted pool object (see
/// docs/pool-model-plan.md §1a) — the pool is <i>derived</i> from the instance store, so this is a thin
/// query/allocation layer, not a store. Checkout/release land in M3; M2 just exposes the status view.
/// </summary>
public sealed class PoolService(StackStore stacks, InstanceStore instances)
{
    /// <summary>The current state of a stack's pool: its ceiling and the live workspaces in it, ordered
    /// by pool index. Throws if the stack doesn't exist.</summary>
    public PoolStatus Status(string stackName)
    {
        var stack = stacks.Get(stackName)
            ?? throw new StackException($"unknown stack '{stackName}'");
        var workspaces = instances.LoadAll()
            .Where(i => string.Equals(i.Stack, stackName, StringComparison.Ordinal))
            .OrderBy(i => i.WorkspaceIndex ?? int.MaxValue)
            .ThenBy(i => i.Workspace, StringComparer.Ordinal)
            .ToList();
        return new PoolStatus(stack.Name, stack.MaxSlots, workspaces);
    }
}

/// <summary>A stack's pool at a moment: the ceiling, and every workspace currently in it.</summary>
public sealed record PoolStatus(string Stack, int MaxSlots, IReadOnlyList<InstanceRecord> Workspaces)
{
    /// <summary>Workspaces currently checked out.</summary>
    public int ClaimedCount => Workspaces.Count(w => w.Claimed);

    /// <summary>Unclaimed workspaces already materialised — free to take (reset per the checkout choice).</summary>
    public int FreeCount => Workspaces.Count(w => !w.Claimed);

    /// <summary>Room to materialise a brand-new workspace under the ceiling.</summary>
    public int Headroom => Math.Max(0, MaxSlots - Workspaces.Count);

    /// <summary>No unclaimed workspace to reuse and no headroom to build one — checkout must wait for a
    /// release. This is the cap doing its job ("no floating instances forever").</summary>
    public bool IsExhausted => FreeCount == 0 && Headroom == 0;
}
