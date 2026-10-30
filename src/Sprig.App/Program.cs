using Avalonia;
using Sprig.App.Rendering;
using Sprig.Core.Shell;
using Velopack;

namespace Sprig.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Velopack install/update lifecycle hook — must run before anything else. No-op unless
        // launched with the special --veloapp-* hook args (i.e. during install/update).
        var velopack = VelopackApp.Build();

        // The FastCallback hooks fire during install/update/uninstall to keep the bundled CLI
        // (sprig.exe) and its shell shims on the user PATH: add them on install, re-assert on update
        // (cheap insurance if they were ever lost), and take them off again on uninstall. Each must
        // finish within Velopack's 15-30s budget; the slowest step, asking PowerShell for its script
        // policy, is capped at 10s. The hooks run sprig-gui.exe from Velopack's "current" folder, which
        // is also where sprig.exe lands, so AppContext.BaseDirectory is the CLI's folder. The hooks (and
        // the PATH edit) are Windows-only; the guard both matches that and keeps the platform analyzer happy.
        if (OperatingSystem.IsWindows())
        {
            velopack
                .OnAfterInstallFastCallback(_ => ShellInstall.Ensure(AppContext.BaseDirectory))
                .OnAfterUpdateFastCallback(_ => ShellInstall.Ensure(AppContext.BaseDirectory))
                .OnBeforeUninstallFastCallback(_ => ShellInstall.Remove(AppContext.BaseDirectory));
        }

        velopack.Run();

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
