using System.Linq;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Sprig.App.ViewModels;
using Sprig.App.Views;

namespace Sprig.App.Rendering;

/// <summary>
/// Renders the app's views to PNG on a headless Skia platform, so the UI can be eyeballed without a
/// display. Invoked via <c>sprig-gui render &lt;dir&gt;</c>. Best-effort — capture problems are logged,
/// never fatal.
/// </summary>
internal static class HeadlessRenderer
{
    public static int RenderAll(string outDir)
    {
        Directory.CreateDirectory(outDir);
        try
        {
            AppBuilder.Configure<App>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .WithInterFont()
                .SetupWithoutStarting();

            var services = new AppServices();
            var vm = new MainWindowViewModel(services);

            foreach (var page in vm.Pages)
            {
                vm.CurrentPage = page;
                // Select the first repo so the config panel renders populated.
                if (page is ReposViewModel repos)
                    repos.Selected = repos.Repos.FirstOrDefault();
                Capture(vm, Path.Combine(outDir, $"main_{page.Title.ToLowerInvariant()}.png"));
            }

            Console.WriteLine($"rendered to {Path.GetFullPath(outDir)}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"headless render failed: {ex.Message}");
            return 1;
        }
    }

    static void Capture(MainWindowViewModel vm, string path)
    {
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        frame?.Save(path);
        window.Close();
    }
}
