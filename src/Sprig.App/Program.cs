using Avalonia;
using Sprig.App.Rendering;
using Velopack;

namespace Sprig.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Velopack install/update lifecycle hook — must run before anything else. No-op unless
        // launched with the special --veloapp-* hook args (i.e. during install/update).
        VelopackApp.Build().Run();

        // `sprig-gui render <dir>` dumps the main views to PNG (headless) for visual verification.
        if (args.Length > 0 && args[0] == "render")
            return HeadlessRenderer.RenderAll(args.Length > 1 ? args[1] : ".");

        // `sprig-gui check-update` runs the notify-only update check and prints the result — a
        // headless probe of the same path the UI uses on launch (honours SPRIG_UPDATE_FEED).
        if (args.Length > 0 && args[0] == "check-update")
        {
            var notice = Updates.UpdateChecker.CheckAsync().GetAwaiter().GetResult();
            Console.WriteLine(notice ?? "up to date (or not installed via Velopack)");
            return 0;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
