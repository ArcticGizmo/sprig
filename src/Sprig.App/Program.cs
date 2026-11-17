using Avalonia;
using Sprig.Engine.Shell;
using Velopack;

namespace Sprig.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Must run before anything else. No-op unless launched with Velopack's --veloapp-* hook args.
        var velopack = VelopackApp.Build();

        // The hooks keep the bundled CLI (sprig.exe) and its shims on the user PATH across install, update
        // and uninstall. They run from Velopack's "current" folder, where sprig.exe also lands, and must finish
        // inside Velopack's 15-30s budget.
        if (OperatingSystem.IsWindows())
        {
            velopack
                .OnAfterInstallFastCallback(_ => ShellInstall.Ensure(AppContext.BaseDirectory))
                .OnAfterUpdateFastCallback(_ => ShellInstall.Ensure(AppContext.BaseDirectory))
                .OnBeforeUninstallFastCallback(_ => ShellInstall.Remove(AppContext.BaseDirectory));
        }

        velopack.Run();

        if (args.Length > 1 && args[0] == "render-code-editor")
            return Rendering.HeadlessRenderer.RenderCodeEditor(args[1]);

        if (args.Length > 1 && args[0] == "render-code-editor-cases")
            return Rendering.HeadlessRenderer.RenderCodeEditorCases(args[1]);

        if (args.Length > 2 && args[0] == "render-orchestrations")
            return Rendering.HeadlessRenderer.RenderOrchestrations(args[1], args[2]);

        if (args.Length > 2 && args[0] == "render-workspaces")
            return Rendering.HeadlessRenderer.RenderWorkspaces(args[1], args[2]);

        if (args.Length > 1 && args[0] == "render-workspaces-sample")
            return Rendering.HeadlessRenderer.RenderWorkspacesSample(args[1]);

        // A headless probe of the same update check the UI runs on launch (honours SPRIG_UPDATE_FEED).
        if (args.Length > 0 && args[0] == "check-update")
        {
            var notice = Updates.UpdateChecker.CheckAsync().GetAwaiter().GetResult();
            Console.WriteLine(notice ?? "up to date (or not installed via Velopack)");
            return 0;
        }

        App.StartWorkspace = WorkspaceArg(args);
        using var instance = SingleInstance.Claim(new Sprig.Engine.Store.SprigPaths(null), App.StartWorkspace);
        if (instance is null) return 0;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    /// <summary>The key after <c>--workspace</c>, which <c>sprig open</c> passes to open a workspace's window.</summary>
    internal static string? WorkspaceArg(string[] args)
    {
        var at = Array.IndexOf(args, "--workspace");
        return at >= 0 && at + 1 < args.Length && !string.IsNullOrWhiteSpace(args[at + 1]) ? args[at + 1] : null;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
