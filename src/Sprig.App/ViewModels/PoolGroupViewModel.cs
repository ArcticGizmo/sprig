using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Sprig.App.ViewModels;

/// <summary>
/// One stack's pool in the Workspaces list: the stack's capacity ceiling and the live set of
/// workspaces standing under it. "Pool" is emergent — there's no pool object in the store, so this is
/// derived from <c>PoolService.Status</c> each refresh. A residual group (<see cref="IsPool"/> false)
/// gathers any ad-hoc workspaces that predate the pool model and belong to no stack; it has no capacity
/// and no checkout.
/// </summary>
public sealed class PoolGroupViewModel
{
    public string Stack { get; }

    /// <summary>The stack's <c>MaxSlots</c> — the ceiling on concurrent workspaces (0 for the residual group).</summary>
    public int MaxSlots { get; }

    /// <summary>False for the residual "(ad-hoc)" group — it isn't a real pool, so it shows no capacity/checkout.</summary>
    public bool IsPool { get; }

    public ObservableCollection<WorkspaceItemViewModel> Workspaces { get; } = [];

    public PoolGroupViewModel(string stack, int maxSlots, IEnumerable<WorkspaceItemViewModel> items, bool isPool = true)
    {
        Stack = stack;
        MaxSlots = maxSlots;
        IsPool = isPool;
        foreach (var item in items) Workspaces.Add(item);
    }

    public int Built => Workspaces.Count;
    public int ClaimedCount => Workspaces.Count(w => w.Claimed);
    public int FreeCount => Workspaces.Count(w => w.Free);
    public int DegradedCount => Workspaces.Count(w => w.SetupFailed);

    /// <summary>Workspaces that could still be built before hitting the cap.</summary>
    public int Headroom => Math.Max(0, MaxSlots - Built);

    /// <summary>No free workspace and no room to build one — checkout must wait for a release.</summary>
    public bool IsExhausted => IsPool && FreeCount == 0 && Headroom == 0;

    /// <summary>Checkout is possible when a free workspace can be reused or a new one built.</summary>
    public bool CanCheckout => IsPool && !IsExhausted;

    public bool HasWorkspaces => Workspaces.Count > 0;

    /// <summary>Primary capacity readout: "2/4 in use" for a pool; a plain count for the residual group.</summary>
    public string CapacitySummary => IsPool
        ? $"{ClaimedCount}/{MaxSlots} in use"
        : $"{Built} workspace{(Built == 1 ? "" : "s")}";

    /// <summary>Secondary line under the capacity: free / buildable / at-capacity, plus any degraded count.
    /// Empty for the residual group (it has no pool semantics to describe).</summary>
    public string StatusSummary
    {
        get
        {
            if (!IsPool) return "";
            var parts = new List<string>();
            if (FreeCount > 0) parts.Add($"{FreeCount} free");
            if (Headroom > 0) parts.Add($"{Headroom} can be built");
            if (parts.Count == 0) parts.Add("at capacity");
            if (DegradedCount > 0) parts.Add($"{DegradedCount} degraded");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Why checkout is unavailable, for the disabled button's tooltip (null when it's available).</summary>
    public string? CheckoutBlockedReason => IsExhausted ? "Pool full — release a workspace first." : null;

    /// <summary>Shown in place of the workspace rows when the pool has none built yet.</summary>
    public bool IsEmptyPool => IsPool && Built == 0;
}
