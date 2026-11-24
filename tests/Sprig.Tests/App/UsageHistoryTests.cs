using Sprig.App;
using Sprig.App.ViewModels;
using Sprig.Engine.Hosting;
using Sprig.Engine.Workspaces;

namespace Sprig.Tests.App;

public class UsageHistoryTests
{
    const long Mb = 1024 * 1024;
    static readonly DateTimeOffset At = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    sealed class Clock
    {
        public DateTime Now { get; set; } = At.UtcDateTime;
        public void Step(int steps = 1) => Now += UsageHistory.Step * steps;
    }

    static RunnerStatus Runner(string workspace, string name, double cpu, long memory)
        => new(workspace, name, RunnerState.Ready, 1, null, null, "x.log", At, Usage: new ResourceUsage(cpu, memory, 1));

    [Fact]
    public void Each_host_answer_lands_in_its_step_and_a_missed_step_stays_empty()
    {
        using var temp = new TempStore();
        var services = new AppServices(temp.Root);
        var clock = new Clock();
        var history = new UsageHistory(services.HostStatus, services.InfraUsage, () => clock.Now);

        services.HostStatus.Apply([Runner("shop/1", "web/api", 5, 100 * Mb)]);
        clock.Step();
        // Twice in one step: the later figure wins.
        services.HostStatus.Apply([Runner("shop/1", "web/api", 7, 110 * Mb)]);
        services.HostStatus.Apply([Runner("shop/1", "web/api", 8, 120 * Mb)]);
        clock.Step(2);
        services.HostStatus.Apply([Runner("shop/1", "web/api", 9, 130 * Mb)]);

        var trend = history.Of(UsageHistory.AppId("shop/1", "web/api"));
        Assert.Equal(UsageHistory.Length, trend.Cpu.Length);
        Assert.Equal([5, 8, null, 9], trend.Cpu[^4..]);
        Assert.Equal(130 * Mb, trend.Memory[^1]);
        Assert.Equal((9, 130.0 * Mb), (trend.PeakCpu, trend.PeakMemory));
        Assert.Same(UsageTrend.Empty, history.Of(UsageHistory.AppId("shop/1", "web/other")));
    }

    [Fact]
    public void A_workspace_adds_up_its_apps_and_its_containers_including_ones_that_have_stopped()
    {
        using var temp = new TempStore();
        var services = new AppServices(temp.Root);
        var clock = new Clock();
        var history = new UsageHistory(services.HostStatus, services.InfraUsage, () => clock.Now);
        var record = new WorkspaceRecord("shop", "shop.orchestration.yaml", "1", "feat", "stable", [], [], [], WorkspaceState.Up, At, At);
        var projects = WorkspaceService.InfraProjects(record).ToHashSet(StringComparer.Ordinal);

        services.HostStatus.Apply([Runner(record.Key, "web/api", 2, 100 * Mb), Runner(record.Key, "web/worker", 1, 50 * Mb), Runner("other/1", "web/api", 40, 900 * Mb)]);
        services.InfraUsage.Hold(new Dictionary<(string, string), ResourceUsage> { [(projects.First(), "pg")] = new(3, 200 * Mb) });
        clock.Step();
        // The worker stopped, and docker wasn't asked again.
        services.HostStatus.Apply([Runner(record.Key, "web/api", 4, 100 * Mb)]);

        var trend = history.OfWorkspace(record.Key, projects);
        Assert.Equal([6, 4], trend.Cpu[^2..]);
        Assert.Equal([350.0 * Mb, 100.0 * Mb], trend.Memory[^2..]);
    }

    [Fact]
    public void A_container_measured_every_few_steps_holds_its_figure_in_the_total_between_measures()
    {
        var app = new double?[UsageHistory.Length];
        var container = new double?[UsageHistory.Length];
        for (var i = 0; i < app.Length; i++) app[i] = 1;
        container[^7] = 10;
        container[^4] = 20;

        var total = UsageTrend.Sum([new UsageTrend(app, app), new UsageTrend(container, container)]);

        Assert.Equal([1, 11, 11, 11, 21, 1, 1, 1], total.Cpu[^8..]);
    }

    [Fact]
    public void A_series_with_nothing_for_the_whole_span_is_forgotten()
    {
        using var temp = new TempStore();
        var services = new AppServices(temp.Root);
        var clock = new Clock();
        var history = new UsageHistory(services.HostStatus, services.InfraUsage, () => clock.Now);
        var id = UsageHistory.AppId("shop/1", "web/api");

        services.HostStatus.Apply([Runner("shop/1", "web/api", 5, 100 * Mb)]);
        clock.Step(UsageHistory.Length - 1);
        Assert.Equal(5, history.Of(id).Cpu[0]);

        clock.Step();
        services.HostStatus.Apply([]);
        Assert.Same(UsageTrend.Empty, history.Of(id));
    }

    [Fact]
    public void Cpu_scales_from_nothing_to_its_peak_but_never_magnifies_an_idle_app()
    {
        Assert.Equal([0, 0.5, 1, null], UsageViewModel.ScaleCpu([0, 10, 20, null]));
        Assert.Equal([0.1, 0.3], UsageViewModel.ScaleCpu([0.1, 0.3]));
    }

    [Fact]
    public void Memory_scales_between_its_low_and_peak_with_a_span_of_at_least_a_tenth_of_the_peak()
    {
        Assert.Equal([0, 0.5, 1, null], UsageViewModel.ScaleMemory([100, 150, 200, null]));
        var steady = UsageViewModel.ScaleMemory([1000, 1001]);
        Assert.Equal(1, steady[1]!.Value, 6);
        Assert.InRange(steady[0]!.Value, 0.98, 0.995);
    }

    [Fact]
    public void Usage_rows_carry_their_workspace_s_trend_and_an_open_row_its_parts_trends()
    {
        using var temp = new TempStore();
        var services = new AppServices(temp.Root, () => At.UtcDateTime);
        var store = new WorkspaceStore(services.Paths);
        var record = new WorkspaceRecord("shop", "shop.orchestration.yaml", "1", "feat", "stable", [], [], [], WorkspaceState.Up, At, At);
        store.Save(record);
        services.HostStatus.Apply([Runner(record.Key, "web/api", 2, 100 * Mb)]);
        services.History.Fill(UsageHistory.AppId(record.Key, "web/api"), ago => new ResourceUsage(ago, 100 * Mb - ago * Mb, 1));
        var vm = new UsageViewModel(services) { IsActive = true };
        vm.PollAsync(usage: false);

        var row = Assert.Single(vm.Rows);
        Assert.Equal(1, row.MemoryTrend![^1]);
        Assert.StartsWith("Last 10 minutes: CPU peaked at", row.CpuTrendTip);
        Assert.Null(row.Parts.Single().CpuTrend);

        row.IsExpanded = true;
        Assert.Equal(UsageHistory.Length, row.Parts.Single().CpuTrend!.Count);
    }

    [Fact]
    public void Every_page_reads_a_workspace_s_containers_as_running_from_what_docker_last_said()
    {
        using var temp = new TempStore();
        var services = new AppServices(temp.Root);
        var record = new WorkspaceRecord("shop", "shop.orchestration.yaml", "1", "feat", "stable", [], [], [], WorkspaceState.Up, At, At);
        Assert.Equal("stopped", services.RunState(record));

        services.InfraUsage.Hold(new Dictionary<(string, string), ResourceUsage> { [(WorkspaceService.InfraProjects(record)[0], "pg")] = new(1, Mb) });

        Assert.Equal("running", services.RunState(record));
    }
}
