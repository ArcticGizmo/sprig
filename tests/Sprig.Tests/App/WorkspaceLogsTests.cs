using System.Text;
using Sprig.App.Logs;
using Sprig.Engine.Hosting;

namespace Sprig.Tests.App;

public sealed class WorkspaceLogsTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("sprig-workspacelogs-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    string File(string name) => Path.Combine(_dir, name);

    static void Append(string file, string text)
    {
        using var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        stream.Write(Encoding.UTF8.GetBytes(text));
    }

    static string Timed(string time, string text) => $"{time} {text}\n";

    static readonly string Today = LogLineFormat.DateMarker(new DateOnly(2026, 11, 4)) + "\n";

    [Fact]
    public async Task Lines_from_several_apps_are_merged_in_time_order_and_each_app_keeps_its_own()
    {
        Append(File("api.log"), Today + Timed("11:00:00.100", "api one") + Timed("11:00:00.300", "api two"));
        Append(File("web.log"), Today + Timed("11:00:00.200", "web one") + Timed("11:00:00.400", "fail: web broke"));
        var logs = new WorkspaceLogs();
        logs.SetApps([("shop/api", File("api.log")), ("shop/web", File("web.log"))]);

        var change = await logs.PollAsync();

        Assert.Equal(["api one", "web one", "api two", "fail: web broke"], logs.Merged.Select(l => l.Text));
        Assert.Equal(["api one", "api two"], logs.For("shop/api").Select(l => l.Text));
        Assert.Equal([1, 2], logs.For("shop/web").Select(l => l.Number));
        Assert.Equal(LineLevel.Error, logs.For("shop/web")[1].Level);
        Assert.Equal(4, change.Added.Count);
        Assert.False(change.Removed);
    }

    [Fact]
    public async Task A_line_older_than_the_newest_is_slotted_in_without_anything_being_removed()
    {
        Append(File("api.log"), Today + Timed("11:00:01.000", "api late"));
        Append(File("web.log"), Today);
        var logs = new WorkspaceLogs();
        logs.SetApps([("shop/api", File("api.log")), ("shop/web", File("web.log"))]);
        await logs.PollAsync();

        Append(File("web.log"), Timed("11:00:00.500", "web earlier"));
        var change = await logs.PollAsync();

        Assert.Equal(["web earlier", "api late"], logs.Merged.Select(l => l.Text));
        Assert.False(change.Removed);
    }

    [Fact]
    public async Task Untimed_lines_from_an_older_log_stay_in_file_order()
    {
        Append(File("old.log"), "first\nsecond\n");
        var logs = new WorkspaceLogs();
        logs.SetApps([("shop/old", File("old.log"))]);

        await logs.PollAsync();

        Assert.Equal(["first", "second"], logs.Merged.Select(l => l.Text));
        Assert.All(logs.Merged, l => Assert.False(l.Timed));
    }

    [Fact]
    public async Task An_app_started_afresh_loses_its_earlier_lines_everywhere()
    {
        Append(File("api.log"), Today + Timed("11:00:00.100", "first run, a long line to be sure the file shrinks"));
        Append(File("web.log"), Today + Timed("11:00:00.200", "web"));
        var logs = new WorkspaceLogs();
        logs.SetApps([("shop/api", File("api.log")), ("shop/web", File("web.log"))]);
        await logs.PollAsync();

        System.IO.File.WriteAllText(File("api.log"), Today + Timed("11:00:05.000", "second run"));
        var change = await logs.PollAsync();

        Assert.Equal(["second run"], logs.For("shop/api").Select(l => l.Text));
        Assert.Equal(["web", "second run"], logs.Merged.Select(l => l.Text));
        Assert.Contains("shop/api", change.Restarted);
        Assert.True(change.Removed);
        Assert.Equal(1, logs.ReadCount("shop/api"));
    }

    [Fact]
    public async Task Both_lists_are_capped_with_room_to_spare_but_line_numbers_keep_counting()
    {
        var text = new StringBuilder(Today);
        for (var i = 1; i <= 30; i++) text.Append(Timed($"11:00:{i:00}.000", $"line {i}"));
        Append(File("api.log"), text.ToString());
        var logs = new WorkspaceLogs(perAppCap: 20, mergedCap: 25);
        logs.SetApps([("shop/api", File("api.log"))]);

        var change = await logs.PollAsync();

        Assert.Equal(18, logs.For("shop/api").Count);
        Assert.Equal(30, logs.For("shop/api")[^1].Number);
        Assert.Equal(23, logs.Merged.Count);
        Assert.Contains("shop/api", change.TrimmedApps);
        Assert.Equal(30, logs.ReadCount("shop/api"));
    }

    [Fact]
    public async Task An_app_no_longer_followed_takes_its_lines_with_it()
    {
        Append(File("api.log"), Today + Timed("11:00:00.100", "api"));
        Append(File("web.log"), Today + Timed("11:00:00.200", "web"));
        var logs = new WorkspaceLogs();
        logs.SetApps([("shop/api", File("api.log")), ("shop/web", File("web.log"))]);
        await logs.PollAsync();

        logs.SetApps([("shop/web", File("web.log"))]);

        Assert.Equal(["web"], logs.Merged.Select(l => l.Text));
        Assert.Empty(logs.For("shop/api"));
        Assert.Equal(["shop/web"], logs.Apps);
    }
}
