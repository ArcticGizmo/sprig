using System.Text;
using Sprig.Cli.Commands;
using Sprig.Engine.Hosting;
using Spectre.Console;

namespace Sprig.Tests.Hosting;

public sealed class LogTailTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("sprig-logtail-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    string File(string name = "app.log") => Path.Combine(_dir, name);

    static void Append(string file, byte[] bytes)
    {
        using var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        stream.Write(bytes);
    }

    static void Append(string file, string text) => Append(file, Encoding.UTF8.GetBytes(text));

    static string[] Texts(LogTail tail) => tail.ReadNew().Select(e => e.Text).ToArray();

    [Fact]
    public void Only_whole_lines_are_read_and_a_line_still_being_written_waits_for_its_end()
    {
        var file = File();
        var tail = new LogTail(file);
        Assert.Empty(tail.ReadNew());

        // The host's StreamWriter starts the file with a byte order mark.
        Append(file, [0xEF, 0xBB, 0xBF]);
        Append(file, "one\r\ntwo\nthr");
        Assert.Equal(["one", "two"], Texts(tail));
        Assert.Empty(tail.ReadNew());

        Append(file, "ee\n");
        Assert.Equal(["three"], Texts(tail));
    }

    [Fact]
    public void Lines_the_host_wrote_carry_their_time_and_the_date_from_the_marker_above_them()
    {
        var file = File();
        var tail = new LogTail(file);
        Append(file, $"{LogLineFormat.DateMarker(new DateOnly(2026, 11, 4))}\n11:42:03.118 sprig: starting npm run dev\n");
        Append(file, $"11:42:04.002 12:00:00.000 an app line that starts with its own time\n{LogLineFormat.DateMarker(new DateOnly(2026, 11, 5))}\n00:00:01.500 after midnight\n");

        var entries = tail.ReadNew();

        Assert.Equal(["sprig: starting npm run dev", "12:00:00.000 an app line that starts with its own time", "after midnight"], entries.Select(e => e.Text));
        Assert.Equal(new DateTime(2026, 11, 4, 11, 42, 3, 118), entries[0].Time!.Value.DateTime);
        Assert.Equal(new DateTime(2026, 11, 5, 0, 0, 1, 500), entries[2].Time!.Value.DateTime);
    }

    [Fact]
    public void A_log_from_before_lines_carried_a_time_reads_as_untimed_lines()
    {
        var file = File();
        Append(file, "plain old line\n");

        var entry = Assert.Single(new LogTail(file).ReadNew());

        Assert.Null(entry.Time);
        Assert.Equal("plain old line", entry.Text);
    }

    [Fact]
    public void The_time_prefix_round_trips_through_the_parser()
    {
        var at = new DateTime(2026, 11, 4, 9, 5, 7, 42);
        var parser = new LogLineParser();

        Assert.Null(parser.Parse(LogLineFormat.DateMarker(DateOnly.FromDateTime(at))));
        var entry = parser.Parse(LogLineFormat.Prefix(at) + "hello");

        Assert.Equal("09:05:07.042 ", LogLineFormat.Prefix(at));
        Assert.Equal(at, entry!.Time!.Value.DateTime);
        Assert.Equal("hello", entry.Text);
    }

    [Fact]
    public void A_capped_read_leaves_the_rest_for_the_next()
    {
        var file = File();
        var tail = new LogTail(file) { MaxLinesPerRead = 2 };
        Append(file, "a\nb\nc\nd\ne");

        Assert.Equal(["a", "b"], Texts(tail));
        Assert.Equal(["c", "d"], Texts(tail));
        Assert.Empty(tail.ReadNew());
        Append(file, "\n");
        Assert.Equal(["e"], Texts(tail));
    }

    [Theory]
    [InlineData("fail: Microsoft.AspNetCore.Server.Kestrel[13]", LineLevel.Error)]
    [InlineData("crit: Microsoft.AspNetCore.Hosting.Diagnostics[6]", LineLevel.Error)]
    [InlineData("Unhandled exception. System.InvalidOperationException: boom", LineLevel.Error)]
    [InlineData("   at Payments.Api.Program.Main(String[] args) in Program.cs:line 31", LineLevel.Error)]
    [InlineData("Program.cs(12,5): error CS1002: ; expected", LineLevel.Error)]
    [InlineData("npm ERR! code ELIFECYCLE", LineLevel.Error)]
    [InlineData("Error: listen EADDRINUSE: address already in use :::5170", LineLevel.Error)]
    [InlineData("TypeError: Cannot read properties of undefined (reading 'id')", LineLevel.Error)]
    [InlineData("[vite] error while updating dependencies", LineLevel.Error)]
    [InlineData("2026-11-04 11:42:03 [ERROR] could not reach the broker", LineLevel.Error)]
    [InlineData("time=11:42 level=error msg=boom", LineLevel.Error)]
    [InlineData("sprig: exited with code 1 after 4.1s", LineLevel.Error)]
    [InlineData("warn: Npgsql: connection refused, retry 1 of 3", LineLevel.Warning)]
    [InlineData("Program.cs(3,1): warning CS8019: unnecessary using", LineLevel.Warning)]
    [InlineData("npm WARN deprecated inflight@1.0.6", LineLevel.Warning)]
    [InlineData("[WARN] slow query: 1.2s", LineLevel.Warning)]
    [InlineData("sprig: exited with code 134; restarting in 4s (3 of 5)", LineLevel.Error)]
    [InlineData("sprig: restarting in 4s", LineLevel.Warning)]
    [InlineData("sprig: exited with code 0", LineLevel.Sprig)]
    [InlineData("sprig: ready on port 8000", LineLevel.Sprig)]
    [InlineData("info: Microsoft.Hosting.Lifetime[14]", LineLevel.Info)]
    [InlineData("GET /products/mug 200 in 1.9ms", LineLevel.Info)]
    [InlineData("  VITE v6.0.3  ready in 412 ms", LineLevel.Info)]
    public void Each_line_gets_a_level_from_what_common_loggers_write(string text, LineLevel expected)
        => Assert.Equal(expected, LogLevels.Of(text));

    [Fact]
    public void Red_text_counts_as_an_error_when_nothing_else_says_so()
    {
        var line = "\x1B[31m✘ build failed\x1B[39m";

        Assert.Equal(LineLevel.Error, LogLevels.Of(AnsiText.Strip(line), AnsiText.Runs(line)));
        Assert.Equal(LineLevel.Info, LogLevels.Of("\x1B[32mok\x1B[39m", AnsiText.Runs("\x1B[32mok\x1B[39m")));
    }

    [Fact]
    public void A_character_split_across_two_reads_comes_out_whole()
    {
        var file = File();
        var tail = new LogTail(file);
        var bytes = Encoding.UTF8.GetBytes("café ✓\n");

        Append(file, bytes[..^3]);
        Assert.Empty(tail.ReadNew());
        Append(file, bytes[^3..]);

        Assert.Equal(["café ✓"], Texts(tail));
    }

    [Fact]
    public void A_log_started_afresh_is_read_from_the_top()
    {
        var file = File();
        var tail = new LogTail(file);
        Append(file, "a long first run of the app\n");
        tail.ReadNew();

        System.IO.File.WriteAllText(file, "new\n");

        Assert.Equal(["new"], Texts(tail));
    }

    [Fact]
    public void Colour_codes_and_window_titles_are_stripped()
    {
        Assert.Equal("  VITE v6 ready in 312 ms", AnsiText.Strip("\x1B[32m\x1B[1m  VITE\x1B[22m v6\x1B[39m ready in \x1B[1m312\x1B[22m ms"));
        Assert.Equal("done", AnsiText.Strip("\u001B]0;title\u0007done"));
        Assert.Equal("plain", AnsiText.Strip("plain"));
    }

    [Fact]
    public void Colour_codes_become_styled_runs_and_everything_else_is_dropped()
    {
        var runs = AnsiText.Runs("\u001B[32m\u001B[1mVITE\u001B[22m v6\u001B[39m ready \u001B[2min\u001B[0m \u001B[38;5;196mred\u001B[38;2;1;2;3m rgb\u001B[44m\u001B[K!");

        Assert.Equal(["VITE", " v6", " ready ", "in", " ", "red", " rgb", "!"], runs.Select(r => r.Text));
        Assert.Equal(new AnsiRun("VITE", 0x4ADE80, Bold: true), runs[0]);
        Assert.Equal(new AnsiRun(" v6", 0x4ADE80), runs[1]);
        Assert.Equal(new AnsiRun(" ready "), runs[2]);
        Assert.True(runs[3].Dim);
        Assert.Equal(new AnsiRun(" "), runs[4]);
        Assert.Equal(0xFF0000, runs[5].Foreground);
        Assert.Equal(0x010203, runs[6].Foreground);
        Assert.Equal(0x010203, runs[7].Foreground);
        Assert.Equal([new AnsiRun("plain")], AnsiText.Runs("plain"));
        Assert.Empty(AnsiText.Runs(""));
    }

    [Fact]
    public void A_restarted_log_bumps_its_generation()
    {
        var file = File();
        var tail = new LogTail(file);
        Append(file, "first run, quite a long line\n");
        tail.ReadNew();
        Assert.Equal(0, tail.Generation);

        System.IO.File.WriteAllText(file, "second\n");
        tail.ReadNew();

        Assert.Equal(1, tail.Generation);
    }

    [Fact]
    public void Followed_logs_carry_short_unique_labels_and_sprig_lines_lose_nothing()
    {
        var labels = LogFollower.Labels(["shop-services/catalog-api", "shop-web/web", "shop-mobile/web"]);
        Assert.Equal("catalog-api", labels["shop-services/catalog-api"]);
        Assert.Equal("shop-web/web", labels["shop-web/web"]);
        Assert.Equal("shop-mobile/web", labels["shop-mobile/web"]);

        var output = new StringWriter();
        var ansi = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(output),
            // Spectre turns ANSI back on when it sees a CI such as GitHub Actions, whatever is asked for above.
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false },
        });
        var follower = new LogFollower(ansi, output, colour: false);
        follower.Add("api", File("api.log"));
        follower.Add("web-app", File("web.log"));
        Append(File("api.log"), "sprig: ready on port 8000\n");
        Append(File("web.log"), "\x1B[32mVITE ready\x1B[39m [1/2]\n");

        Assert.True(follower.Poll());
        Assert.False(follower.Poll());

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["api     | sprig: ready on port 8000", "web-app | VITE ready [1/2]"], lines);
    }
}
