using Sprig.Engine.Compose;
using Sprig.Engine.Hosting;
using Sprig.Engine.Processes;

namespace Sprig.Tests.Compose;

public class ContainerUsageTests
{
    /// <summary>Answers <c>docker ps</c> and <c>docker stats</c>, keeping what stats was asked about.</summary>
    sealed class FakeDocker(string ps, string stats, int statsExit = 0) : IProcessRunner
    {
        public IReadOnlyList<string>? StatsArgs { get; private set; }

        public ProcessResult Run(string executable, IReadOnlyList<string> arguments, string? workingDirectory = null,
            CancellationToken cancellationToken = default, Action<string>? onOutput = null)
        {
            if (arguments[0] == "ps") return new ProcessResult(executable, arguments, 0, ps, "");
            StatsArgs = arguments;
            return new ProcessResult(executable, arguments, statsExit, stats, statsExit == 0 ? "" : "Error: No such container");
        }
    }

    const string Ps = """
        3f2a9c1d7e44	sprig-storefront-feat-a-containers	shop-services-postgres
        9b8c7d6e5f40	sprig-storefront-feat-a-shop-web	cache
        a1b2c3d4e5f6	someone-elses-project	db
        """;

    const string Stats = """
        {"BlockIO":"0B / 0B","CPUPerc":"8.00%","Container":"3f2a9c1d7e44","ID":"3f2a9c1d7e44","MemPerc":"1.2%","MemUsage":"45.5MiB / 7.6GiB","Name":"x-postgres-1","NetIO":"1kB / 0B","PIDs":"7"}
        {"BlockIO":"0B / 0B","CPUPerc":"0.00%","Container":"9b8c7d6e5f40","ID":"9b8c7d6e5f40","MemPerc":"0.1%","MemUsage":"1.5GiB / 7.6GiB","Name":"my-cache","NetIO":"1kB / 0B","PIDs":"4"}
        """;

    [Fact]
    public void Each_container_is_known_by_its_project_and_service_and_only_the_projects_asked_for_are_measured()
    {
        var docker = new FakeDocker(Ps, Stats);

        var usage = ContainerUsage.Read(docker, new HashSet<string> { "sprig-storefront-feat-a-containers", "sprig-storefront-feat-a-shop-web" });

        Assert.Equal(["3f2a9c1d7e44", "9b8c7d6e5f40"], docker.StatsArgs!.Skip(4).Order());
        var postgres = usage[("sprig-storefront-feat-a-containers", "shop-services-postgres")];
        Assert.Equal(8.0 / Environment.ProcessorCount, postgres.Cpu!.Value, 6);
        Assert.Equal((long)(45.5 * 1024 * 1024), postgres.Memory);
        Assert.Equal(7, postgres.Processes);
        Assert.Equal(1536L * 1024 * 1024, usage[("sprig-storefront-feat-a-shop-web", "cache")].Memory);
    }

    [Fact]
    public void Nothing_running_asks_docker_nothing_more_and_a_stats_call_that_fails_gives_nothing()
    {
        var idle = new FakeDocker("", "");
        Assert.Empty(ContainerUsage.Read(idle));
        Assert.Null(idle.StatsArgs);

        Assert.Empty(ContainerUsage.Read(new FakeDocker(Ps, "", statsExit: 1)));
    }

    [Fact]
    public void Docker_s_share_of_one_core_becomes_a_share_of_the_machine()
    {
        var usage = ContainerUsage.ParseStats("""{"ID":"abc","CPUPerc":"200.00%","MemUsage":"512kB / 2GB","PIDs":"1"}""", processors: 8);

        Assert.Equal(25, usage["abc"].Cpu);
        Assert.Equal(512_000, usage["abc"].Memory);
    }

    [Theory]
    [InlineData("0B", 0L)]
    [InlineData("12.5KiB", 12800L)]
    [InlineData("45.3MiB ", 47500492L)]
    [InlineData("1.5GiB", 1610612736L)]
    [InlineData("3MB", 3000000L)]
    [InlineData("--", null)]
    public void Sizes_are_read_in_docker_s_units(string text, long? bytes) => Assert.Equal(bytes, ContainerUsage.Bytes(text));

    [Fact]
    public void Usage_reads_as_task_manager_would_put_it_and_adds_up()
    {
        var api = new ResourceUsage(3.24, 412L * 1024 * 1024, 3);
        var db = new ResourceUsage(null, 2L * 1024 * 1024 * 1024, 1);

        Assert.Equal("3.2% · 412 MB", api.Text);
        Assert.Equal("3.2% CPU · 412 MB · 3 processes", api.Line);
        Assert.Equal("–% · 2.0 GB", db.Text);
        Assert.Equal("0%", ResourceUsage.CpuText(0.01));
        Assert.Equal("37%", ResourceUsage.CpuText(36.6));

        var total = ResourceUsage.Sum([api, null, db])!;
        Assert.Equal(3.24, total.Cpu);
        Assert.Equal(api.Memory + db.Memory, total.Memory);
        Assert.Null(ResourceUsage.Sum([null, null]));
    }
}
