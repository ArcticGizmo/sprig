using Sprig.App;
using Sprig.App.ViewModels;
using Sprig.Engine.Hosting;
using Sprig.Engine.Workspaces;

namespace Sprig.Tests.App;

public class UsageViewModelTests
{
    const long Mb = 1024 * 1024;
    static readonly DateTimeOffset Now = new(2026, 11, 13, 12, 0, 0, TimeSpan.FromHours(11));

    static UsageViewModel Make(TempStore temp, out AppServices services)
    {
        services = new AppServices(temp.Root);
        var store = new WorkspaceStore(services.Paths);
        foreach (var name in new[] { "feat-a", "feat-b", "idle" })
            store.Save(new WorkspaceRecord("storefront", @"C:\tb\storefront.orchestration.yaml", name, name, "stable", [], [], [], WorkspaceState.Up, Now, Now));
        store.Save(new WorkspaceRecord("storefront", @"C:\tb\storefront.orchestration.yaml", "old", "old", "stable", [], [], [], WorkspaceState.Down, Now, Now));

        services.HostStatus.Apply([
            Runner("storefront/feat-a", "shop/api", new ResourceUsage(2, 300 * Mb, 1)),
            Runner("storefront/feat-a", "shop/web", new ResourceUsage(8, 200 * Mb, 4)),
            Runner("storefront/feat-b", "shop/api", new ResourceUsage(20, 900 * Mb, 1)),
            Runner("storefront/feat-b", "shop/migrate", null),
        ]);
        var featA = store.Get("storefront", "feat-a")!;
        services.InfraUsage.Hold(new Dictionary<(string, string), ResourceUsage>
        {
            [(WorkspaceService.InfraProjects(featA)[0], "shop-postgres")] = new(1, 800 * Mb, 9),
        });

        var vm = new UsageViewModel(services);
        vm.HoldMachine(new MachineSnapshot(50, 16_384 * Mb, 32_768 * Mb));
        vm.IsActive = true;
        vm.PollAsync(usage: false);
        return vm;
    }

    static RunnerStatus Runner(string workspace, string name, ResourceUsage? usage)
        => new(workspace, name, usage is null ? RunnerState.Completed : RunnerState.Ready, 1, null, null, "x.log", Now, Usage: usage);

    [Fact]
    public void Workspaces_using_anything_are_listed_heaviest_first_with_their_apps_and_containers_together()
    {
        using var temp = new TempStore();
        var vm = Make(temp, out _);

        Assert.Equal(["storefront--feat-a", "storefront--feat-b"], vm.Rows.Select(r => r.Name));
        var featA = vm.Rows[0];
        Assert.Equal(("11%", "1.3 GB", "2 running", "1 running"), (featA.Cpu, featA.Memory, featA.Apps, featA.Infra));
        Assert.Equal(UsageViewModel.RowBarWidth, featA.Bar);
        // Nothing of feat-b's job is running, so it isn't counted.
        Assert.Equal(("1 running", "—"), (vm.Rows[1].Apps, vm.Rows[1].Infra));
        Assert.Equal(["shop-postgres", "api", "web"], featA.Parts.Select(p => p.Name));
        Assert.Equal(["container", "app · shop", "app · shop"], featA.Parts.Select(p => p.Kind));
        Assert.Equal("1 more is up with nothing of its own running.", vm.IdleNote);
        Assert.Equal("MEMORY ▼", vm.MemoryHeading);
    }

    [Fact]
    public void A_heading_sorts_by_it_and_again_the_other_way_and_an_open_row_stays_open()
    {
        using var temp = new TempStore();
        var vm = Make(temp, out var services);
        vm.Rows.Single(r => r.Name == "storefront--feat-a").IsExpanded = true;

        vm.SortCommand.Execute(UsageSort.Cpu);
        Assert.Equal(["storefront--feat-b", "storefront--feat-a"], vm.Rows.Select(r => r.Name));
        Assert.Equal(["web", "api", "shop-postgres"], vm.Rows[1].Parts.Select(p => p.Name));
        Assert.Equal(("CPU ▼", "MEMORY"), (vm.CpuHeading, vm.MemoryHeading));

        vm.SortCommand.Execute(UsageSort.Cpu);
        Assert.Equal(["storefront--feat-a", "storefront--feat-b"], vm.Rows.Select(r => r.Name));
        Assert.Equal("CPU ▲", vm.CpuHeading);

        vm.SortCommand.Execute(UsageSort.Workspace);
        Assert.Equal("WORKSPACE ▲", vm.WorkspaceHeading);

        services.HostStatus.Apply(services.HostStatus.Runners!.Append(Runner("storefront/idle", "shop/api", new ResourceUsage(0, 50 * Mb, 1))).ToArray());
        Assert.Equal(["storefront--feat-a", "storefront--feat-b", "storefront--idle"], vm.Rows.Select(r => r.Name));
        Assert.True(vm.Rows[0].IsExpanded);
        Assert.Equal("", vm.IdleNote);
    }

    [Fact]
    public void The_machine_s_bars_draw_what_sprig_runs_inside_what_s_in_use()
    {
        using var temp = new TempStore();
        var vm = Make(temp, out _);

        // 2.2 GB of sprig's, inside 16 GB used of 32.
        Assert.Equal(UsageViewModel.MachineBarWidth * 2200 / 32_768, vm.MemorySprigWidth, 6);
        Assert.Equal(UsageViewModel.MachineBarWidth * (16_384 - 2200) / 32_768, vm.MemoryOtherWidth, 6);
        Assert.Equal("16.0 GB of 32.0 GB in use · 2.1 GB of it what sprig runs", vm.MemoryLine);
        Assert.Equal("50% busy · 31% of it what sprig runs", vm.CpuLine);
        Assert.Equal(UsageViewModel.MachineBarWidth * 0.31, vm.CpuSprigWidth, 6);
    }

    [Fact]
    public void The_machine_reads_its_memory_and_then_how_busy_it_has_been()
    {
        var machine = new MachineUsage();
        var first = machine.Sample();
        Thread.Sleep(200);
        var second = machine.Sample();

        Assert.True(first.MemoryTotal > 0);
        if (!OperatingSystem.IsWindows()) return;
        Assert.Null(first.Cpu);
        Assert.InRange(second.Cpu!.Value, 0, 100);
        Assert.InRange(second.MemoryUsed!.Value, 1, second.MemoryTotal);
    }
}
