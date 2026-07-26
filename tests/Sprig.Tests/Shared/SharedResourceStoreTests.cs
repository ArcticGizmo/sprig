using System.Collections.Generic;
using System.Linq;
using Sprig.Core.Shared;

namespace Sprig.Tests.Shared;

/// <summary>
/// Shared resources are hand-authored JSON until extraction lands, so the store's job is to round-trip
/// them faithfully and to reject a typo rather than quietly ignore it — an ignored key in an overlay
/// means an override that silently never happens.
/// </summary>
public class SharedResourceStoreTests
{
    static SharedResourceDefinition Postgres() => new()
    {
        Name = "postgres-16",
        Capacity = 5,
        Values = new Dictionary<string, string> { ["database"] = "sprig_${sprig.workspace}" },
        Injects =
        [
            new ResourceInjection
            {
                Repo = "dotnet-api",
                Inputs = new Dictionary<string, string> { ["dbPort"] = "${sprig.shared.port}" },
            },
        ],
    };

    [Fact]
    public void Round_trips_through_the_store()
    {
        using var store = new TempStore();
        var resources = new SharedResourceStore(store.Paths);

        resources.Save(Postgres());
        var loaded = resources.Get("postgres-16");

        Assert.NotNull(loaded);
        Assert.Equal(5, loaded!.Capacity);
        Assert.True(loaded.Enabled);
        Assert.Equal("stop", loaded.WhenIdle);
        Assert.Equal("sprig_${sprig.workspace}", loaded.Values["database"]);
        Assert.Equal("dotnet-api", Assert.Single(loaded.Injects).Repo);
    }

    [Fact]
    public void Active_only_returns_the_enabled_ones()
    {
        using var store = new TempStore();
        var resources = new SharedResourceStore(store.Paths);

        resources.Save(Postgres());
        resources.Save(Postgres() with { Name = "redis-7", Enabled = false });

        Assert.Equal(["postgres-16", "redis-7"], resources.List().Select(r => r.Name));
        Assert.Equal(["postgres-16"], resources.Active().Select(r => r.Name));
    }

    // The lease ledger lives in the same directory. Listing it as a resource produced a blank row that
    // couldn't be shown, enabled or removed.
    [Fact]
    public void The_lease_ledger_is_not_a_resource()
    {
        using var store = new TempStore();
        var resources = new SharedResourceStore(store.Paths);
        resources.Save(Postgres());
        new SharedLeaseStore(store.Paths).Acquire(Postgres(), "a",
            [new SlotNamespace("dotnet-api", new Dictionary<string, string>())], ["a"]);

        Assert.Equal(["postgres-16"], resources.List().Select(r => r.Name));
    }

    [Fact]
    public void Listing_an_empty_store_is_fine()
    {
        using var store = new TempStore();
        Assert.Empty(new SharedResourceStore(store.Paths).List());
    }

    [Fact]
    public void Remove_takes_the_compose_fragment_with_it()
    {
        using var store = new TempStore();
        var resources = new SharedResourceStore(store.Paths);
        resources.Save(Postgres() with { Compose = "postgres-16.compose.yml" });
        var fragment = Path.Combine(store.Paths.SharedDir, "postgres-16.compose.yml");
        File.WriteAllText(fragment, "services: {}");

        resources.Remove("postgres-16");

        Assert.Null(resources.Get("postgres-16"));
        Assert.False(File.Exists(fragment));   // an orphaned fragment is just litter
    }

    [Fact]
    public void Remove_is_idempotent()
    {
        using var store = new TempStore();
        var resources = new SharedResourceStore(store.Paths);
        resources.Save(Postgres());

        resources.Remove("postgres-16");
        resources.Remove("postgres-16");

        Assert.Null(resources.Get("postgres-16"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("has spaces")]
    [InlineData("slash/es")]
    public void Rejects_a_name_that_cant_be_a_file(string name)
        => Assert.Contains("invalid name", string.Join("\n", SharedResourceStore.Validate(Postgres() with { Name = name })));

    [Fact]
    public void Rejects_a_capacity_below_one()
        => Assert.Contains("capacity must be at least 1",
            string.Join("\n", SharedResourceStore.Validate(Postgres() with { Capacity = 0 })));

    [Fact]
    public void Rejects_an_unknown_whenIdle()
        => Assert.Contains("whenIdle must be",
            string.Join("\n", SharedResourceStore.Validate(Postgres() with { WhenIdle = "linger" })));

    [Fact]
    public void Rejects_a_resource_that_injects_nowhere()
        => Assert.Contains("injects[] is empty",
            string.Join("\n", SharedResourceStore.Validate(Postgres() with { Injects = [] })));

    [Fact]
    public void Rejects_an_injection_that_changes_nothing()
        => Assert.Contains("changes nothing", string.Join("\n", SharedResourceStore.Validate(
            Postgres() with { Injects = [new ResourceInjection { Repo = "dotnet-api" }] })));

    [Fact]
    public void Rejects_two_injections_for_one_repo()
        => Assert.Contains("more than once", string.Join("\n", SharedResourceStore.Validate(
            Postgres() with { Injects = [Postgres().Injects[0], Postgres().Injects[0]] })));

    // A misspelled key in an overlay is an override that never fires — the failure mode this whole
    // design goes out of its way to make loud. It must not survive a save.
    [Fact]
    public void Rejects_an_unknown_key_rather_than_ignoring_it()
    {
        using var store = new TempStore();
        var resources = new SharedResourceStore(store.Paths);
        var file = resources.FilePath("typo");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, """
            { "schema": 1, "name": "typo", "capcity": 99,
              "injects": [ { "repo": "api", "inputs": { "dbPort": "5432" } } ] }
            """);

        var loaded = resources.Get("typo");

        Assert.NotNull(loaded);
        Assert.Equal(5, loaded!.Capacity);   // the misspelling set nothing — it fell into Unknown
        var issues = string.Join("\n", SharedResourceStore.Validate(loaded));
        Assert.Contains("unknown key 'capcity'", issues);
        Assert.Throws<SharedResourceException>(() => resources.Save(loaded));
    }
}
