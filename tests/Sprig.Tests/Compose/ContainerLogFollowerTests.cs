using Sprig.Engine.Compose;

namespace Sprig.Tests.Compose;

public sealed class ContainerLogFollowerTests
{
    [Fact]
    public void A_timestamped_line_keeps_its_time_to_the_tenth_of_a_microsecond_and_loses_the_stamp()
    {
        var entry = ContainerLogFollower.Parse("2026-11-09T10:15:42.123456789Z LOG:  database system is ready to accept connections");

        Assert.Equal(new DateTimeOffset(2026, 11, 9, 10, 15, 42, TimeSpan.Zero).AddTicks(1234567), entry.Time);
        Assert.Equal("LOG:  database system is ready to accept connections", entry.Text);
    }

    [Fact]
    public void A_line_without_a_stamp_is_kept_whole()
    {
        var entry = ContainerLogFollower.Parse("no such service: pg");

        Assert.Null(entry.Time);
        Assert.Equal("no such service: pg", entry.Text);
    }

    [Fact]
    public void The_first_follow_starts_from_the_log_s_end_and_a_later_one_from_the_last_line_read()
    {
        var first = ContainerLogFollower.Arguments("sprig-shop-1", "postgres", null);
        Assert.Equal(["compose", "-p", "sprig-shop-1", "logs", "--follow", "--timestamps", "--no-color", "--no-log-prefix", "--tail", "1000", "postgres"], first);

        var again = ContainerLogFollower.Arguments("sprig-shop-1", "postgres", new DateTimeOffset(2026, 11, 9, 21, 15, 42, TimeSpan.FromHours(11)).AddTicks(1234567));
        Assert.Equal(["compose", "-p", "sprig-shop-1", "logs", "--follow", "--timestamps", "--no-color", "--no-log-prefix", "--since", "2026-11-09T10:15:42.1234567Z", "postgres"], again);
    }
}
