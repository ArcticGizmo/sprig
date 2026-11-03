using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using AvaloniaEdit;
using Sprig.App.ViewModels;
using Sprig.App.Views;

namespace Sprig.App.Rendering;

/// <summary>
/// Renders app views to PNG on a headless Skia platform, so the UI can be checked without a display or any
/// screen capture. Invoked via <c>sprig-gui render-editor &lt;file.png&gt;</c>.
/// </summary>
internal static class HeadlessRenderer
{
    /// <summary>The editor spike after the GUI's "add env var" edit, with a typo so the problems list has a row.</summary>
    public static int RenderEditor(string outFile)
    {
        var store = Path.Combine(Path.GetTempPath(), "sprig-render-" + Guid.NewGuid().ToString("N"));
        try
        {
            AppBuilder.Configure<App>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .WithInterFont()
                .SetupWithoutStarting();

            var vm = new MainWindowViewModel(new AppServices(store), checkForUpdates: false);
            vm.NavigateCommand.Execute(vm.EditorSpike);
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 820 };
            window.Show();
            Pump(TimeSpan.FromMilliseconds(200));

            var editor = Find<TextEditor>(window) ?? throw new InvalidOperationException("editor not found");
            var addEnv = Find<Avalonia.Controls.Button>(window, "AddEnvButton")!;
            addEnv.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            var typo = editor.Text.IndexOf(".Http(\"http\")", StringComparison.Ordinal);
            editor.Document.Replace(typo, ".Http".Length, ".Htp");

            // The debounce timer doesn't tick on the headless platform, so ask for diagnostics directly and pump
            // until the answer from the host process has been applied.
            var view = Find<EditorSpikeView>(window)!;
            var pending = view.RunDiagnostics();
            var clock = Stopwatch.StartNew();
            while (!pending.IsCompleted && clock.Elapsed < TimeSpan.FromSeconds(20)) Pump(TimeSpan.FromMilliseconds(50));
            vm.EditorSpike.RefreshMemoryCommand.Execute(null);
            Pump(TimeSpan.FromMilliseconds(800));

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
            window.CaptureRenderedFrame()?.Save(outFile);
            window.Close();
            vm.EditorSpike.Host.Dispose();
            Console.WriteLine($"rendered {Path.GetFullPath(outFile)} (diagnostics: {vm.EditorSpike.Diagnostics.Count}, {vm.EditorSpike.DiagnosticsTiming})");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"headless render failed: {ex}");
            return 1;
        }
        finally
        {
            try { Directory.Delete(store, recursive: true); } catch { /* temp store */ }
        }
    }

    static void Pump(TimeSpan duration)
    {
        var until = DateTime.UtcNow + duration;
        do
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(15);
        } while (DateTime.UtcNow < until);
    }

    static T? Find<T>(Avalonia.Visual root, string? name = null) where T : Avalonia.Controls.Control
    {
        foreach (var visual in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root))
            if (visual is T match && (name is null || match.Name == name)) return match;
        return null;
    }
}
