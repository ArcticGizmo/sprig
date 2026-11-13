using Sprig.App;

namespace Sprig.Tests.App;

public class BrowserTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe\" --single-argument %1", "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe")]
    [InlineData("\"C:\\Program Files\\Mozilla Firefox\\firefox.exe\" -osint -url \"%1\"", "C:\\Program Files\\Mozilla Firefox\\firefox.exe")]
    [InlineData("C:\\Tools\\browser.exe %1", "C:\\Tools\\browser.exe")]
    [InlineData("rundll32 url.dll", null)]
    public void The_program_is_read_from_a_shell_open_command(string command, string? exe) => Assert.Equal(exe, Browser.ExeOf(command));

    [Theory]
    [InlineData("C:\\x\\chrome.exe", "--new-window")]
    [InlineData("C:\\x\\msedge.exe", "--new-window")]
    [InlineData("C:\\x\\firefox.exe", "-new-window")]
    [InlineData("C:\\x\\iexplore.exe", null)]
    public void Each_browser_family_is_asked_for_a_new_window_its_own_way(string exe, string? flag) => Assert.Equal(flag, Browser.NewWindowFlag(exe));
}
