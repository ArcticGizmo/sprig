using Sprig.App;
using Sprig.Cli;
using Sprig.Engine.Hosting;
using Sprig.Engine.Store;

namespace Sprig.Tests.App;

public class ResidentTrayTests
{
    static RunnerStatus Running(string workspace, string name)
        => new(workspace, name, RunnerState.Ready, 1, 8000, null, "a.log", DateTimeOffset.UnixEpoch);

    [Fact]
    public void Quit_is_its_own_message_and_never_read_as_asking_for_a_window()
    {
        Assert.False(SingleInstance.TryRead(AppInstance.QuitMessage, out _));
        Assert.NotEqual(AppInstance.QuitMessage, SingleInstance.Message(null));
    }

    [Fact]
    public void Sign_in_starts_this_copy_of_sprig_in_the_tray()
        => Assert.Equal("\"C:\\Users\\me\\AppData\\Local\\sprig\\current\\sprig-gui.exe\" --tray",
            SignInStart.CommandFor(@"C:\Users\me\AppData\Local\sprig\current\sprig-gui.exe"));

    [Fact]
    public void The_dev_icon_inverts_each_colour_and_keeps_the_transparent_edges_transparent()
    {
        // Premultiplied: a half-transparent pure green, then a fully transparent pixel.
        byte[] pixels = [0, 128, 0, 128, 0, 0, 0, 0];
        AppIcon.Invert(pixels, premultiplied: true);
        Assert.Equal(new byte[] { 128, 0, 128, 128, 0, 0, 0, 0 }, pixels);

        byte[] straight = [0, 200, 10, 255];
        AppIcon.Invert(straight, premultiplied: false);
        Assert.Equal(new byte[] { 255, 55, 245, 255 }, straight);
    }

    [Fact]
    public void Updating_asks_nothing_when_nothing_would_be_lost()
        => Assert.Null(CliUpdater.TakedownQuestion(app: false, alive: [], pause: true));

    [Fact]
    public void Updating_says_it_will_quit_sprig_and_what_happens_to_its_apps()
    {
        var alive = new[] { Running("storefront/1", "shop/api"), Running("storefront/1", "shop/web"), Running("storefront/2", "shop/api") };

        Assert.Contains("quits it", CliUpdater.TakedownQuestion(app: true, alive: [], pause: true));
        var paused = CliUpdater.TakedownQuestion(app: true, alive, pause: true)!;
        Assert.Contains("3 apps running in 2 workspaces", paused);
        Assert.Contains("pausing them", paused);
        Assert.Contains("stopping them", CliUpdater.TakedownQuestion(app: true, alive, pause: false));
        Assert.Contains("sprig host", CliUpdater.TakedownQuestion(app: false, alive, pause: false));
    }
}
