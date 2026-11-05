using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
internal static partial class HeadlessRenderer
{
    /// <summary>The definition editor with a typo (an error) and an unused import's variable shadowed (a warning), so
    /// both kinds of squiggle and the problems strip show.</summary>
    public static int RenderCodeEditor(string outFile)
    {
        var host = new Sprig.App.Authoring.LanguageHostClient();
        try
        {
            AppBuilder.Configure<App>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .WithInterFont()
                .SetupWithoutStarting();

            var text = EditorSpikeViewModel.SampleText
                .Replace(".Http(\"http\")\n    .Env(\"ConnectionStrings", ".Htp(\"http\")\n    .Env(\"ConnectionStrings")
                .Replace("repo.Export<Url>", "#warning check the export name before committing\nrepo.Export<Url>");
            var editor = new Sprig.App.Controls.CodeEditor { Host = host, Text = text };
            var window = new Avalonia.Controls.Window
            {
                Width = 1000, Height = 640, Content = editor,
                Background = Avalonia.Media.Brush.Parse("#1F1F2A"),
            };
            window.Show();
            Pump(TimeSpan.FromMilliseconds(200));

            var pending = editor.DiagnoseAsync();
            var clock = Stopwatch.StartNew();
            while (!pending.IsCompleted && clock.Elapsed < TimeSpan.FromSeconds(30)) Pump(TimeSpan.FromMilliseconds(50));
            Pump(TimeSpan.FromMilliseconds(300));

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
            window.CaptureRenderedFrame()?.Save(outFile);
            window.Close();
            Console.WriteLine($"rendered {Path.GetFullPath(outFile)}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"headless render failed: {ex}");
            return 1;
        }
        finally
        {
            host.Dispose();
        }
    }

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

    /// <summary>The Trellises page planning <paramref name="trellisFile"/>, in a throwaway store where the trellis and
    /// its repos' definitions are approved up front (this is a dev tool, not a user path).</summary>
    public static int RenderTrellises(string outFile, string trellisFile)
    {
        var store = Path.Combine(Path.GetTempPath(), "sprig-render-" + Guid.NewGuid().ToString("N"));
        try
        {
            AppBuilder.Configure<App>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .WithInterFont()
                .SetupWithoutStarting();

            var services = new AppServices(store);
            var root = Path.GetDirectoryName(Path.GetFullPath(trellisFile))!;
            foreach (var file in Directory.EnumerateFiles(root, "*.trellis.cs")
                         .Concat(Directory.EnumerateDirectories(root).Select(d => Path.Combine(d, "sprig.cs")).Where(File.Exists)))
                services.Trust.Approve(file, Sprig.Engine.Definitions.DefinitionHash.Of(File.ReadAllText(file)));
            services.Trellises.Add("storefront", trellisFile);
            // One repo of each kind in the Repos list: its file in the repo, and in sprig.
            if (Path.Combine(root, "shop-web") is var web && File.Exists(Path.Combine(web, "sprig.cs")))
                services.Authoring.AddRepo(web, "shop-web");
            if (Path.Combine(root, "shop-services") is var services_ && Directory.Exists(services_))
            {
                services.Authoring.AddRepo(services_, "shop-services-local");
                services.Authoring.KeepInSprig("shop-services-local");
            }

            var vm = new MainWindowViewModel(services, checkForUpdates: false);
            vm.NavigateCommand.Execute(vm.Trellises);
            var window = new MainWindow { DataContext = vm, Width = 1440, Height = 1000 };
            window.Show();

            var clock = Stopwatch.StartNew();
            Pump(TimeSpan.FromMilliseconds(300));
            while ((vm.Trellises.IsBusy || vm.Trellises.Plan is null) && !vm.Trellises.HasLoadProblems && clock.Elapsed < TimeSpan.FromSeconds(60))
                Pump(TimeSpan.FromMilliseconds(100));
            Pump(TimeSpan.FromMilliseconds(500));

            Save(window, outFile);
            var planned = vm.Trellises.Plan is not null;

            if (vm.Trellises.RepoEntries.FirstOrDefault(r => !r.InRepo) is { } written)
            {
                vm.Trellises.SelectedRepo = written;
                clock.Restart();
                Pump(TimeSpan.FromMilliseconds(200));
                while (vm.Trellises.RepoCheck.EndsWith('…') && clock.Elapsed < TimeSpan.FromSeconds(60)) Pump(TimeSpan.FromMilliseconds(100));
                Pump(TimeSpan.FromMilliseconds(300));
                Save(window, Sibling(outFile, "repo"));
                vm.Trellises.Selected = vm.Trellises.Trellises.FirstOrDefault();
                Pump(TimeSpan.FromMilliseconds(300));
            }

            vm.Trellises.OpenAddCommand.Execute(null);
            vm.Trellises.NewPath = Sprig.App.Controls.PathCompletion.ToPosix(Path.GetFullPath(trellisFile));
            Pump(TimeSpan.FromMilliseconds(400));
            var addFile = Sibling(outFile, "add");
            Save(window, addFile);

            // The same file, added again once its approval is revoked, shows what a not-yet-approved trellis looks like.
            services.Trust.Revoke(trellisFile);
            Wait(vm.Trellises.AddCommand.ExecuteAsync(null));
            Pump(TimeSpan.FromMilliseconds(400));
            var unapprovedFile = Sibling(outFile, "unapproved");
            Save(window, unapprovedFile);
            window.Close();

            Console.WriteLine($"rendered {Path.GetFullPath(outFile)} (plan: {planned}, problems: {vm.Trellises.LoadProblems.Count}, took {clock.Elapsed.TotalSeconds:0.0}s), {Path.GetFullPath(addFile)} and {Path.GetFullPath(unapprovedFile)}");
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

    /// <summary>
    /// The Sprouts page over the current store (<c>SPRIG_STORE</c>), read only, with its first sprout selected, its apps'
    /// states from the host and the first app's log open; and the progress window part way through a sample up, failing
    /// on a compose step, so both states of a row show.
    /// </summary>
    public static int RenderSprouts(string pageFile, string progressFile)
    {
        try
        {
            AppBuilder.Configure<App>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .WithInterFont()
                .SetupWithoutStarting();

            var services = new AppServices();
            var vm = new MainWindowViewModel(services, checkForUpdates: false);
            vm.NavigateCommand.Execute(vm.Sprouts);
            vm.Sprouts.Show(vm.Sprouts.Sprouts.FirstOrDefault()?.Record.Key);
            var window = new MainWindow { DataContext = vm, Width = 1440, Height = 1400 };
            window.Show();
            Pump(TimeSpan.FromMilliseconds(500));

            // Timers don't tick here, so poll as the window's and the page's timers would, then open a log that has something in it.
            Wait(services.HostStatus.PollAsync());
            Wait(vm.Sprouts.PollAsync());
            if ((vm.Sprouts.Apps.FirstOrDefault(a => a.IsAlive && !a.IsJob) ?? vm.Sprouts.Apps.FirstOrDefault()) is { } app)
            {
                vm.Sprouts.OpenApp(app);
                Wait(vm.Sprouts.Logs.PollAsync());
            }
            Pump(TimeSpan.FromMilliseconds(600));
            Save(window, pageFile);
            window.Close();

            var progress = new OperationProgressViewModel("Sprouting storefront / feat-x");
            Sprig.Engine.Sprouts.SproutEvent[] sample =
            [
                new("Plan", Sprig.Engine.Sprouts.StepState.Running),
                new("Plan", Sprig.Engine.Sprouts.StepState.Done, "4 apps, 1 containers, 1 compose files"),
                new("Worktree shop-services", Sprig.Engine.Sprouts.StepState.Running),
                new("Worktree shop-services", Sprig.Engine.Sprouts.StepState.Done, "../storefront--feat-x/shop-services on feat-x (new, from main)"),
                new("Ports", Sprig.Engine.Sprouts.StepState.Running),
                new("Ports", Sprig.Engine.Sprouts.StepState.Done, "catalog-api.http 8000, postgres.tcp 8002"),
                new("Start sprig-storefront-feat-x", Sprig.Engine.Sprouts.StepState.Running),
                new("Start sprig-storefront-feat-x", Sprig.Engine.Sprouts.StepState.Output, "Container sprig-storefront-feat-x-storefront-mail-1 Waiting"),
                new("Start sprig-storefront-feat-x", Sprig.Engine.Sprouts.StepState.Done, "running; healthy: mail"),
                new("Start sprig-storefront-feat-x-shop-services", Sprig.Engine.Sprouts.StepState.Running),
                new("Start sprig-storefront-feat-x-shop-services", Sprig.Engine.Sprouts.StepState.Output, "Container sprig-storefront-feat-x-shop-services-postgres-1 Starting"),
                new("Start sprig-storefront-feat-x-shop-services", Sprig.Engine.Sprouts.StepState.Output, "Error response from daemon: port is already allocated"),
            ];
            foreach (var e in sample) progress.Apply(e);
            progress.Finish("command failed (exit 1): docker compose up (a sample failure, rendered for review)", ProgressState.Error);
            progress.Steps[^1].State = ProgressState.Error;
            var dialog = new OperationProgressWindow { DataContext = progress };
            dialog.Show();
            Pump(TimeSpan.FromMilliseconds(400));
            Save(dialog, progressFile);
            dialog.Close();

            Console.WriteLine($"rendered {Path.GetFullPath(pageFile)} ({vm.Sprouts.Sprouts.Count} sprouts) and {Path.GetFullPath(progressFile)}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"headless render failed: {ex}");
            return 1;
        }
    }

    static void Wait(System.Threading.Tasks.Task task)
    {
        var clock = Stopwatch.StartNew();
        while (!task.IsCompleted && clock.Elapsed < TimeSpan.FromSeconds(20)) Pump(TimeSpan.FromMilliseconds(50));
    }

    static string Sibling(string file, string suffix)
        => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file))!, $"{Path.GetFileNameWithoutExtension(file)}-{suffix}{Path.GetExtension(file)}");

    static void Save(Avalonia.Controls.TopLevel window, string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        window.CaptureRenderedFrame()?.Save(file);
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
