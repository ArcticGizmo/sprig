using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Sprig.App;
using Sprig.App.ViewModels;
using Sprig.Core.Maps;
using Sprig.Core.Store;

namespace Sprig.Tests.App;

/// <summary>
/// The Workspaces page reframed around pools: the list groups workspaces under their map's pool
/// (capacity, free/claimed/degraded), and Checkout/Release drive the lifecycle. Nothing here runs a real
/// checkout (git/docker — that's Core's <c>MapPoolService</c> tests); these pin what the grouped surface
/// shows and how the checkout overlay sets itself up before the user commits.
/// </summary>
public class PoolWorkspaceViewModelTests
{
    /// <summary>Register one repo (no git needed) and a map "app" with the given capacity.</summary>
    static void SeedMap(TempStore s, AppServices services, int maxSlots)
    {
        services.Repos.Add(MakeRepo(s.Root, "api"));
        services.Maps.Save(new MapDefinition { Name = "app", Repos = [MapRepo.Local("api")], MaxSlots = maxSlots });
    }

    /// <summary>A registered map repo: a directory with a provides-declaring .sprig.json (no git — these
    /// tests never run a real checkout, only the grouping + overlay setup).</summary>
    internal static string MakeRepo(string root, string name)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".sprig.json"),
            $$"""{ "schema":3, "name":"{{name}}", "modules":[ { "name":"main", "provides":[ { "capability":"{{name}}", "outputs":{ "port": { "port": true } } } ] } ] }""");
        return dir;
    }

    /// <summary>Write a pool workspace record straight into the store the VM reads (same paths as AppServices).</summary>
    static void SeedWorkspace(AppServices services, string name, int index, bool claimed,
        string? label = null, DateTimeOffset? lastUsed = null)
    {
        new InstanceStore(services.Paths).Save(new InstanceRecord
        {
            Workspace = name,
            Map = "app",
            WorkspaceIndex = index,
            Claimed = claimed,
            Label = label,
            LastUsedAt = lastUsed,
            Repos = [new InstanceRepo { Name = "api", SourcePath = "C:/api", WorktreePath = $"C:/api--{name}" }],
        });
    }

    static async Task<WorkspacesViewModel> LoadedVm(AppServices services)
    {
        var vm = new WorkspacesViewModel(services, new Navigator());
        await vm.RefreshCommand.ExecuteAsync(null);
        return vm;
    }

    [Fact]
    public async Task Each_map_is_a_pool_group_with_its_capacity_and_counts()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        SeedMap(s, services, maxSlots: 4);
        SeedWorkspace(services, "app-1", 1, claimed: true, label: "auth");
        SeedWorkspace(services, "app-2", 2, claimed: false);

        var vm = await LoadedVm(services);

        var pool = Assert.Single(vm.Pools);
        Assert.Equal("app", pool.Map);
        Assert.True(pool.IsPool);
        Assert.Equal("1/4 in use", pool.CapacitySummary);
        Assert.Equal(1, pool.ClaimedCount);
        Assert.Equal(1, pool.FreeCount);
        Assert.Equal(2, pool.Headroom);
        Assert.True(pool.CanCheckout);
        Assert.Contains("1 free", pool.StatusSummary);
    }

    [Fact]
    public async Task An_empty_pool_still_shows_so_it_can_be_checked_out()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        SeedMap(s, services, maxSlots: 3);

        var vm = await LoadedVm(services);

        Assert.True(vm.HasPools);
        Assert.False(vm.HasWorkspaces);
        var pool = Assert.Single(vm.Pools);
        Assert.True(pool.IsEmptyPool);
        Assert.Equal("0/3 in use", pool.CapacitySummary);
        Assert.True(pool.CanCheckout);
    }

    [Fact]
    public async Task A_full_pool_is_exhausted_and_cannot_be_checked_out()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        SeedMap(s, services, maxSlots: 1);
        SeedWorkspace(services, "app-1", 1, claimed: true, label: "busy");

        var vm = await LoadedVm(services);
        var pool = Assert.Single(vm.Pools);

        Assert.True(pool.IsExhausted);
        Assert.False(pool.CanCheckout);

        // Opening checkout on an exhausted pool is a no-op — the overlay never appears.
        vm.CheckoutCommand.Execute(pool);
        Assert.False(vm.IsCheckingOut);
    }

    [Fact]
    public async Task Checkout_defaults_to_reusing_the_least_recently_used_free_workspace()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        SeedMap(s, services, maxSlots: 4);
        SeedWorkspace(services, "app-1", 1, claimed: false, label: "old", lastUsed: DateTimeOffset.UtcNow.AddDays(-3));
        SeedWorkspace(services, "app-2", 2, claimed: false, label: "recent", lastUsed: DateTimeOffset.UtcNow.AddMinutes(-5));

        var vm = await LoadedVm(services);
        vm.CheckoutCommand.Execute(Assert.Single(vm.Pools));

        Assert.True(vm.IsCheckingOut);
        Assert.Equal("app", vm.CheckoutMap);
        Assert.False(vm.CheckoutNew);
        Assert.True(vm.CheckoutReuse);
        Assert.Equal("app-1", vm.CheckoutTarget?.Name);   // freed longest ago
        Assert.True(vm.ShowHandling);
        Assert.True(vm.ModeKeep);
    }

    [Fact]
    public async Task Reuse_offers_only_keep_and_fresh_handling()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        SeedMap(s, services, maxSlots: 2);
        SeedWorkspace(services, "app-1", 1, claimed: false);

        var vm = await LoadedVm(services);
        vm.CheckoutCommand.Execute(Assert.Single(vm.Pools));

        Assert.True(vm.ShowHandling);
        Assert.True(vm.ModeKeep);   // default
        vm.ModeKeep = false;
        vm.ModeFresh = true;
        Assert.True(vm.ModeFresh);
    }

    [Fact]
    public async Task An_empty_pool_checks_out_a_new_workspace_and_hides_handling()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        SeedMap(s, services, maxSlots: 2);

        var vm = await LoadedVm(services);
        vm.CheckoutCommand.Execute(Assert.Single(vm.Pools));

        Assert.True(vm.CheckoutNew);           // nothing free to reuse
        Assert.False(vm.CanReuseWorkspace);
        Assert.False(vm.ShowHandling);         // handling only applies to a reuse
    }

    [Fact]
    public async Task Checkout_needs_a_branch_and_keeps_the_overlay_open_without_one()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        SeedMap(s, services, maxSlots: 2);

        var vm = await LoadedVm(services);
        vm.CheckoutCommand.Execute(Assert.Single(vm.Pools));
        vm.CheckoutBranch = "   "; // whitespace-only is not a branch name

        await vm.ConfirmCheckoutCommand.ExecuteAsync(null);

        Assert.Equal("give this workspace a branch name", vm.CheckoutError);
        Assert.True(vm.IsCheckingOut);
    }

    [Fact]
    public async Task Release_is_available_only_for_a_claimed_workspace()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        SeedMap(s, services, maxSlots: 4);
        SeedWorkspace(services, "app-1", 1, claimed: true, label: "auth");
        SeedWorkspace(services, "app-2", 2, claimed: false);

        var vm = await LoadedVm(services);

        vm.Selected = vm.Workspaces.First(w => w.Name == "app-1");
        Assert.True(vm.ReleaseCommand.CanExecute(null));

        vm.Selected = vm.Workspaces.First(w => w.Name == "app-2");
        Assert.False(vm.ReleaseCommand.CanExecute(null));
    }

    [Fact]
    public async Task Claim_is_available_only_for_a_free_workspace()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        SeedMap(s, services, maxSlots: 4);
        SeedWorkspace(services, "app-1", 1, claimed: true, label: "auth");
        SeedWorkspace(services, "app-2", 2, claimed: false);

        var vm = await LoadedVm(services);

        vm.Selected = vm.Workspaces.First(w => w.Name == "app-2");
        Assert.True(vm.ClaimCommand.CanExecute(null));

        vm.Selected = vm.Workspaces.First(w => w.Name == "app-1");
        Assert.False(vm.ClaimCommand.CanExecute(null));
    }

    [Fact]
    public async Task Claim_opens_the_checkout_overlay_pre_targeted_at_the_selected_workspace()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        SeedMap(s, services, maxSlots: 4);
        SeedWorkspace(services, "app-1", 1, claimed: false, label: "old", lastUsed: DateTimeOffset.UtcNow.AddDays(-3));
        SeedWorkspace(services, "app-2", 2, claimed: false, label: "recent", lastUsed: DateTimeOffset.UtcNow.AddMinutes(-5));

        var vm = await LoadedVm(services);
        // Pick the workspace that is NOT the least-recently-used default, to prove Claim targets the pick.
        vm.Selected = vm.Workspaces.First(w => w.Name == "app-2");
        vm.ClaimCommand.Execute(null);

        Assert.True(vm.IsCheckingOut);
        Assert.Equal("app", vm.CheckoutMap);
        Assert.False(vm.CheckoutNew);
        Assert.True(vm.CheckoutReuse);
        Assert.Equal("app-2", vm.CheckoutTarget?.Name);   // the selected one, not the LRU default
        Assert.True(vm.ModeKeep);
    }
}
