using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Sprig.App;
using Sprig.App.ViewModels;
using Sprig.Core.Stacks;
using Sprig.Core.Store;

namespace Sprig.Tests.App;

/// <summary>
/// The Workspaces page reframed around pools: the list groups workspaces under their stack's pool
/// (capacity, free/claimed/degraded), and Checkout/Release drive the lifecycle. Nothing here runs a real
/// checkout (git/docker — that's Core's <c>PoolService</c> tests); these pin what the grouped surface
/// shows and how the checkout overlay sets itself up before the user commits.
/// </summary>
public class PoolWorkspaceViewModelTests
{
    /// <summary>Register one repo and a stack "app" with the given capacity.</summary>
    static void SeedStack(TempStore s, AppServices services, int maxSlots)
    {
        services.Repos.Add(ManagementViewModelTests.MakeRepoWithInputs(s.Root, "api", ("port", "5000")));
        services.Stacks.Save(new StackDefinition { Name = "app", Repos = ["api"], MaxSlots = maxSlots });
    }

    /// <summary>Write a pool workspace record straight into the store the VM reads (same paths as AppServices).</summary>
    static void SeedWorkspace(AppServices services, string name, int index, bool claimed,
        string? label = null, DateTimeOffset? lastUsed = null)
    {
        new InstanceStore(services.Paths).Save(new InstanceRecord
        {
            Workspace = name,
            Stack = "app",
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
    public async Task Each_stack_is_a_pool_group_with_its_capacity_and_counts()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        SeedStack(s, services, maxSlots: 4);
        SeedWorkspace(services, "app-1", 1, claimed: true, label: "auth");
        SeedWorkspace(services, "app-2", 2, claimed: false);

        var vm = await LoadedVm(services);

        var pool = Assert.Single(vm.Pools);
        Assert.Equal("app", pool.Stack);
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
        SeedStack(s, services, maxSlots: 3);

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
        SeedStack(s, services, maxSlots: 1);
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
        SeedStack(s, services, maxSlots: 4);
        SeedWorkspace(services, "app-1", 1, claimed: false, label: "old", lastUsed: DateTimeOffset.UtcNow.AddDays(-3));
        SeedWorkspace(services, "app-2", 2, claimed: false, label: "recent", lastUsed: DateTimeOffset.UtcNow.AddMinutes(-5));

        var vm = await LoadedVm(services);
        vm.CheckoutCommand.Execute(Assert.Single(vm.Pools));

        Assert.True(vm.IsCheckingOut);
        Assert.Equal("app", vm.CheckoutStack);
        Assert.False(vm.CheckoutNew);
        Assert.True(vm.CheckoutReuse);
        Assert.Equal("app-1", vm.CheckoutTarget?.Name);   // freed longest ago
        Assert.True(vm.ShowHandling);
        Assert.True(vm.ModeAsIs);
        Assert.False(vm.ShowRefreshRepos);
    }

    [Fact]
    public async Task Picking_refresh_reveals_the_targets_repos_to_choose_from()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        SeedStack(s, services, maxSlots: 2);
        SeedWorkspace(services, "app-1", 1, claimed: false);

        var vm = await LoadedVm(services);
        vm.CheckoutCommand.Execute(Assert.Single(vm.Pools));

        vm.ModeAsIs = false;
        vm.ModeRefresh = true;

        Assert.True(vm.ShowRefreshRepos);
        Assert.Equal(["api"], vm.CheckoutRefreshRepos.Select(r => r.Name));
    }

    [Fact]
    public async Task An_empty_pool_checks_out_a_new_workspace_and_hides_handling()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        SeedStack(s, services, maxSlots: 2);

        var vm = await LoadedVm(services);
        vm.CheckoutCommand.Execute(Assert.Single(vm.Pools));

        Assert.True(vm.CheckoutNew);           // nothing free to reuse
        Assert.False(vm.CanReuseWorkspace);
        Assert.False(vm.ShowHandling);         // handling only applies to a reuse
    }

    [Fact]
    public async Task Checkout_needs_a_label_and_keeps_the_overlay_open_without_one()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        SeedStack(s, services, maxSlots: 2);

        var vm = await LoadedVm(services);
        vm.CheckoutCommand.Execute(Assert.Single(vm.Pools));
        vm.CheckoutLabel = "   ";

        await vm.ConfirmCheckoutCommand.ExecuteAsync(null);

        Assert.Equal("give this checkout a label", vm.CheckoutError);
        Assert.True(vm.IsCheckingOut);
    }

    [Fact]
    public async Task Release_is_available_only_for_a_claimed_workspace()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        SeedStack(s, services, maxSlots: 4);
        SeedWorkspace(services, "app-1", 1, claimed: true, label: "auth");
        SeedWorkspace(services, "app-2", 2, claimed: false);

        var vm = await LoadedVm(services);

        vm.Selected = vm.Workspaces.First(w => w.Name == "app-1");
        Assert.True(vm.ReleaseCommand.CanExecute(null));

        vm.Selected = vm.Workspaces.First(w => w.Name == "app-2");
        Assert.False(vm.ReleaseCommand.CanExecute(null));
    }
}
