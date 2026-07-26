using System.Collections.Generic;
using System.Linq;
using Sprig.Core.Shared;

namespace Sprig.Tests.Shared;

/// <summary>
/// A slot is held from create to rm, so it owns the workspace's data — a stopped workspace keeps its
/// database exactly as it keeps its worktree, at the cost of still counting against capacity. These tests
/// pin that trade, and the self-healing that stops a phantom slot from making it painful.
/// </summary>
public class SharedLeaseStoreTests
{
    static SharedResourceDefinition Postgres(int capacity = 2) => new()
    {
        Name = "postgres-16",
        Capacity = capacity,
        Values = new Dictionary<string, string> { ["database"] = "sprig_${sprig.workspace}" },
        Injects = [new ResourceInjection { Repo = "api", Inputs = new Dictionary<string, string> { ["dbPort"] = "5432" } }],
    };

    static IReadOnlyList<SlotNamespace> Ns(string workspace) =>
        [new SlotNamespace("api", new Dictionary<string, string> { ["database"] = $"sprig_{workspace}" })];

    [Fact]
    public void Slots_are_handed_out_from_the_lowest_free_number()
    {
        using var store = new TempStore();
        var leases = new SharedLeaseStore(store.Paths);
        var res = Postgres(capacity: 3);

        var a = leases.Acquire(res, "a", Ns("a"), ["a"]);
        var b = leases.Acquire(res, "b", Ns("b"), ["a", "b"]);
        leases.Release("postgres-16", "a");
        var c = leases.Acquire(res, "c", Ns("c"), ["b", "c"]);

        Assert.Equal(1, a.Slot);
        Assert.Equal(2, b.Slot);
        Assert.Equal(1, c.Slot);   // a's slot came back round
    }

    [Fact]
    public void Acquiring_twice_returns_the_slot_already_held()
    {
        using var store = new TempStore();
        var leases = new SharedLeaseStore(store.Paths);

        var first = leases.Acquire(Postgres(), "a", Ns("a"), ["a"]);
        var again = leases.Acquire(Postgres(), "a", Ns("a"), ["a"]);

        Assert.Equal(first.Slot, again.Slot);
        Assert.Single(leases.List("postgres-16"));
    }

    [Fact]
    public void The_namespace_survives_a_round_trip_through_the_ledger()
    {
        using var store = new TempStore();
        var leases = new SharedLeaseStore(store.Paths);
        leases.Acquire(Postgres(), "feature-x", Ns("feature-x"), ["feature-x"]);

        var reloaded = new SharedLeaseStore(store.Paths).Peek("postgres-16", "feature-x");

        Assert.NotNull(reloaded);
        var ns = Assert.Single(reloaded!.Namespaces);
        Assert.Equal("api", ns.Repo);
        Assert.Equal("sprig_feature-x", ns.Values["database"]);
        Assert.Equal("sprig_feature-x", ns.Label);
    }

    [Fact]
    public void A_full_resource_says_who_is_holding_it_oldest_first_and_how_to_get_out()
    {
        using var store = new TempStore();
        var leases = new SharedLeaseStore(store.Paths);
        var res = Postgres(capacity: 2);
        leases.Acquire(res, "old-migration", Ns("old-migration"), ["old-migration"]);
        leases.Acquire(res, "feature-x", Ns("feature-x"), ["old-migration", "feature-x"]);

        var ex = Assert.Throws<SharedCapacityException>(() =>
            leases.Acquire(res, "review-pr", Ns("review-pr"), ["old-migration", "feature-x", "review-pr"]));

        Assert.Contains("postgres-16 is full — 2 of 2 slots attached", ex.Message);
        // The one you've forgotten about is the one you read first.
        Assert.True(ex.Message.IndexOf("old-migration") < ex.Message.IndexOf("feature-x"));
        // Teach the model in a line rather than linking to it — the surprise isn't the limit, it's that a
        // stopped workspace still counts.
        Assert.Contains("A slot is a database, not a container", ex.Message);
        Assert.Contains("sprig rm old-migration", ex.Message);
        Assert.Contains("--no-shared", ex.Message);
    }

    // A phantom slot eating capacity is the most irritating possible version of this bug, so the path that
    // would report "full" heals it first.
    [Fact]
    public void A_slot_held_by_a_workspace_that_no_longer_exists_is_reclaimed_rather_than_reported()
    {
        using var store = new TempStore();
        var leases = new SharedLeaseStore(store.Paths);
        var res = Postgres(capacity: 1);
        leases.Acquire(res, "deleted-by-hand", Ns("deleted-by-hand"), ["deleted-by-hand"]);

        // The workspace is gone from the instance store, so it isn't in `known`.
        var slot = leases.Acquire(res, "feature-x", Ns("feature-x"), ["feature-x"]);

        Assert.Equal(1, slot.Slot);
        Assert.Equal(["feature-x"], leases.List("postgres-16").Select(s => s.Workspace));
    }

    [Fact]
    public void Reclaim_reports_what_it_dropped()
    {
        using var store = new TempStore();
        var leases = new SharedLeaseStore(store.Paths);
        var res = Postgres(capacity: 3);
        leases.Acquire(res, "alive", Ns("alive"), ["alive"]);
        leases.Acquire(res, "ghost", Ns("ghost"), ["alive", "ghost"]);

        var dropped = leases.Reclaim(["alive"]);

        Assert.Equal("ghost", Assert.Single(dropped).Workspace);
        Assert.Equal(["alive"], leases.List("postgres-16").Select(s => s.Workspace));
    }

    [Fact]
    public void Releasing_something_that_holds_nothing_is_harmless()
    {
        using var store = new TempStore();
        var leases = new SharedLeaseStore(store.Paths);

        Assert.Null(leases.Release("postgres-16", "nobody"));
        Assert.Empty(leases.ListAll());
    }
}
