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

    [Fact]
    public void Only_whole_lines_are_read_and_a_line_still_being_written_waits_for_its_end()
    {
        var file = File();
        var tail = new LogTail(file);
        Assert.Empty(tail.ReadNew());

        // The host's StreamWriter starts the file with a byte order mark.
        Append(file, [0xEF, 0xBB, 0xBF]);
        Append(file, "one\r\ntwo\nthr");
        Assert.Equal(["one", "two"], tail.ReadNew());
        Assert.Empty(tail.ReadNew());

        Append(file, "ee\n");
        Assert.Equal(["three"], tail.ReadNew());
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

        Assert.Equal(["café ✓"], tail.ReadNew());
    }

    [Fact]
    public void A_log_started_afresh_is_read_from_the_top()
    {
        var file = File();
        var tail = new LogTail(file);
        Append(file, "a long first run of the app\n");
        tail.ReadNew();

        System.IO.File.WriteAllText(file, "new\n");

        Assert.Equal(["new"], tail.ReadNew());
    }

    [Fact]
    public void Colour_codes_and_window_titles_are_stripped()
    {
        Assert.Equal("  VITE v6 ready in 312 ms", AnsiText.Strip("\x1B[32m\x1B[1m  VITE\x1B[22m v6\x1B[39m ready in \x1B[1m312\x1B[22m ms"));
        Assert.Equal("done", AnsiText.Strip("\u001B]0;title\u0007done"));
        Assert.Equal("plain", AnsiText.Strip("plain"));
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
