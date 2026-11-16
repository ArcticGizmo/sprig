using Sprig.Engine.Hosting;

namespace Sprig.Tests.Hosting;

public class ProcessTableTests
{
    [Fact]
    public void A_proc_stat_line_gives_parent_cpu_time_and_resident_memory_even_with_brackets_in_the_name()
    {
        // pid (comm) state ppid pgrp session tty tpgid flags minflt cminflt majflt cmajflt utime stime cutime cstime
        // priority nice threads itrealvalue starttime vsize rss ...
        const string stat = "4242 (node (vite) worker) S 4200 4200 4200 0 -1 4194560 1000 0 0 0 250 50 0 0 20 0 11 0 123456 1048576000 25600 18446744073709551615";

        var entry = ProcessTable.ParseStat(stat, pageSize: 4096)!;

        Assert.Equal((4242, 4200), (entry.Pid, entry.Parent));
        Assert.Equal(TimeSpan.FromSeconds(3), entry.Cpu);
        Assert.Equal(25600L * 4096, entry.Memory);
        Assert.Null(ProcessTable.ParseStat("not a stat line", 4096));
    }

    [Theory]
    [InlineData("12:34.56", 12 * 60 + 34.56)]
    [InlineData("0:00.03", 0.03)]
    [InlineData("01:02:03", 3600 + 120 + 3)]
    [InlineData("2-01:00:00", 2 * 86400 + 3600)]
    public void Ps_cpu_time_reads_as_macos_and_procps_write_it(string text, double seconds)
        => Assert.Equal(seconds, ProcessTable.ParseCpuTime(text)!.Value.TotalSeconds, 6);

    [Fact]
    public void A_ps_line_gives_memory_in_kib()
    {
        var entry = ProcessTable.ParsePsLine("  501   1 20480   0:01.50")!;

        Assert.Equal((501, 1, 20480L * 1024), (entry.Pid, entry.Parent, entry.Memory));
        Assert.Equal(1.5, entry.Cpu.TotalSeconds, 6);
        Assert.Null(ProcessTable.ParsePsLine("PID PPID RSS TIME"));
    }

    [Fact]
    public void A_tree_adds_up_the_root_and_every_descendant_and_nothing_else()
    {
        ProcessEntry P(int pid, int parent, int seconds, long memory) => new(pid, parent, TimeSpan.FromSeconds(seconds), memory);
        var table = new[]
        {
            P(1, 0, 100, 1000), P(10, 1, 1, 10), P(11, 10, 2, 20), P(12, 11, 3, 30), P(13, 10, 4, 40), P(20, 1, 50, 500),
        };

        Assert.Equal((TimeSpan.FromSeconds(10), 100L, 4), ProcessTable.Tree(table, 10));
        Assert.Equal((TimeSpan.FromSeconds(3), 30L, 1), ProcessTable.Tree(table, 12));
        Assert.Null(ProcessTable.Tree(table, 99));
    }

    [Fact]
    public void Off_windows_the_live_table_has_this_process_in_it()
    {
        var table = ProcessTable.Take();
        if (OperatingSystem.IsWindows())
        {
            Assert.Null(table);
            return;
        }
        Assert.NotNull(ProcessTable.Tree(table!, Environment.ProcessId));
    }
}
