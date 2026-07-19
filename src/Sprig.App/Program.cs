using Avalonia;
using Sprig.App.Rendering;

namespace Sprig.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // `sprig-gui render <dir>` dumps the main views to PNG (headless) for visual verification.
        if (args.Length > 0 && args[0] == "render")
            return HeadlessRenderer.RenderAll(args.Length > 1 ? args[1] : ".");

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
