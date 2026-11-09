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
/// screen capture. Invoked via <c>sprig-gui render-code-editor &lt;file.png&gt;</c> and the other render-* switches in
/// Program.cs.
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

            var text = Sprig.App.Authoring.SampleDefinition.Text
                .Replace(".Http(\"http\")\n    .WaitFor(pg);", ".Htp(\"http\")\n    .WaitFor(pg);")
                .Replace("repo.Export<Url>", "#warning check the export name before committing\nrepo.Export<Url>")
                // An http url, which has to keep the string colour rather than be drawn as a link.
                .Replace("// The catalog owns stock levels.", "var search = repo.Setting<Url>(\"search-url\", \"http://localhost:7700\");\n\n// The catalog owns stock levels.");
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

    /// <summary>The code editor's colouring in the orders a page can give it a file: opened while hidden (as the Repos
    /// page does), a second file into a showing editor, and a file after closing and opening again. Each should be
    /// coloured.</summary>
    public static int RenderCodeEditorCases(string outFile)
    {
        try
        {
            AppBuilder.Configure<App>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .WithInterFont()
                .SetupWithoutStarting();

            var editor = new Sprig.App.Controls.CodeEditor();
            var holder = new Avalonia.Controls.ContentControl { Content = editor, IsVisible = false };
            var window = new Avalonia.Controls.Window
            {
                Width = 900, Height = 420, Content = holder,
                Background = Avalonia.Media.Brush.Parse("#1F1F2A"),
            };
            window.Show();
            Pump(TimeSpan.FromMilliseconds(200));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);

            editor.Text = Sprig.App.Authoring.SampleDefinition.Text;
            holder.IsVisible = true;
            Pump(TimeSpan.FromMilliseconds(800));
            window.CaptureRenderedFrame()?.Save(Sibling(outFile, "opened-hidden"));

            editor.Text = Sprig.App.Authoring.SampleDefinition.Text.Replace("shop-services", "shop-web");
            Pump(TimeSpan.FromMilliseconds(800));
            window.CaptureRenderedFrame()?.Save(Sibling(outFile, "second-file"));

            holder.IsVisible = false;
            Pump(TimeSpan.FromMilliseconds(200));
            editor.Text = Sprig.App.Authoring.SampleDefinition.Text.Replace("shop-services", "shop-mobile");
            holder.IsVisible = true;
            Pump(TimeSpan.FromMilliseconds(800));
            window.CaptureRenderedFrame()?.Save(Sibling(outFile, "reopened"));

            // A Dev build drafts definitions against the local SDK project, so their first line is a Windows path.
            editor.Text = Sprig.App.Authoring.SampleDefinition.Text.Replace(
                "#:package Sprig.Sdk@0.1.0-exp", @"#:project C:\Users\someone\src\sprig\src\Sprig.Sdk\Sprig.Sdk.csproj");
            Pump(TimeSpan.FromMilliseconds(800));
            window.CaptureRenderedFrame()?.Save(Sibling(outFile, "project-path"));

            // Leaving a page and coming back takes the editor out of the window and puts it back.
            window.Content = null;
            Pump(TimeSpan.FromMilliseconds(200));
            editor.Text = Sprig.App.Authoring.SampleDefinition.Text.Replace("shop-services", "shop-docs");
            window.Content = holder;
            Pump(TimeSpan.FromMilliseconds(800));
            window.CaptureRenderedFrame()?.Save(Sibling(outFile, "reattached"));

            window.Close();
            Console.WriteLine($"rendered {Sibling(outFile, "opened-hidden")}, -second-file and -reopened");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"headless render failed: {ex}");
            return 1;
        }
    }

    /// <summary>The Orchestrations page planning <paramref name="orchestrationFile"/>, in a throwaway store where the orchestration and
    /// its repos' definitions are approved up front (this is a dev tool, not a user path).</summary>
    public static int RenderOrchestrations(string outFile, string orchestrationFile)
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
            var root = Path.GetDirectoryName(Path.GetFullPath(orchestrationFile))!;
            foreach (var file in Directory.EnumerateFiles(root, "*.orchestration.cs")
                         .Concat(Directory.EnumerateDirectories(root).Select(d => Path.Combine(d, "sprig.cs")).Where(File.Exists)))
                services.Trust.Approve(file, Sprig.Engine.Definitions.DefinitionHash.Of(File.ReadAllText(file)));
            services.Orchestrations.Add("storefront", orchestrationFile);
            // One repo of each kind in the Repos list: its file in the repo, and a copy of one kept in sprig.
            if (Path.Combine(root, "shop-web") is var web && File.Exists(Path.Combine(web, "sprig.cs")))
                services.Authoring.AddRepo(web, "shop-web");
            if (Path.Combine(root, "shop-services") is var services_ && File.Exists(Path.Combine(services_, "sprig.cs")))
            {
                services.Authoring.AddRepo(services_, "shop-services");
                services.Authoring.CopyIntoSprig("shop-services", "shop-services-local");
            }

            var vm = new MainWindowViewModel(services, checkForUpdates: false);
            vm.NavigateCommand.Execute(vm.Orchestrations);
            var window = new MainWindow { DataContext = vm, Width = 1440, Height = 1000 };
            window.Show();

            var clock = Stopwatch.StartNew();
            Pump(TimeSpan.FromMilliseconds(300));
            while ((vm.Orchestrations.IsBusy || vm.Orchestrations.Plan is null) && !vm.Orchestrations.HasLoadProblems && clock.Elapsed < TimeSpan.FromSeconds(60))
                Pump(TimeSpan.FromMilliseconds(100));
            Pump(TimeSpan.FromMilliseconds(500));

            Save(window, outFile);
            var planned = vm.Orchestrations.Plan is not null;
            // A failed definition's output is the only clue to why the render didn't plan.
            if (vm.Orchestrations.HasLoadProblems && services.Definitions.Evaluate(orchestrationFile) is Sprig.Engine.Definitions.Failed failed)
                Console.Error.WriteLine($"the orchestration {failed.Reason}:\n{failed.Output}");

            // One app picked, with one group shown: its wires stand out, and what the group doesn't start dims.
            if (vm.Orchestrations.Graph?.Boxes.SelectMany(b => b.Items).FirstOrDefault(i => i.Name == "web") is { } picked)
            {
                vm.Orchestrations.SelectAppCommand.Execute(picked);
                if (vm.Orchestrations.GroupChips.FirstOrDefault(c => c.Name == "web") is { } group) vm.Orchestrations.ShowGroupCommand.Execute(group);
                Pump(TimeSpan.FromMilliseconds(300));
                Save(window, Sibling(outFile, "picked"));
                vm.Orchestrations.ShowGroupCommand.Execute(vm.Orchestrations.GroupChips.FirstOrDefault());
            }

            foreach (var tab in new[] { OrchestrationTab.Values, OrchestrationTab.Ports })
            {
                vm.Orchestrations.Tab = tab;
                Pump(TimeSpan.FromMilliseconds(300));
                Save(window, Sibling(outFile, tab.ToString().ToLowerInvariant()));
            }
            vm.Orchestrations.Tab = OrchestrationTab.Overview;

            // The Repos page, on the repo kept in sprig.
            vm.NavigateCommand.Execute(vm.Repos);
            vm.Repos.Show("shop-services-local");
            clock.Restart();
            Pump(TimeSpan.FromMilliseconds(200));
            while (vm.Repos.Check.EndsWith('…') && clock.Elapsed < TimeSpan.FromSeconds(60)) Pump(TimeSpan.FromMilliseconds(100));
            Pump(TimeSpan.FromMilliseconds(300));
            Save(window, Sibling(outFile, "repo"));
            // Its Write to the repo dialog, which says why it can't: the folder has its own sprig.cs already.
            vm.Repos.OpenDeriveCommand.Execute(null);
            Pump(TimeSpan.FromMilliseconds(300));
            Save(window, Sibling(outFile, "repo-write"));
            vm.Repos.CancelDeriveCommand.Execute(null);
            // + Add's two cards, then the in-sprig form over a folder that has its own sprig.cs.
            vm.Repos.OpenChooseCommand.Execute(null);
            Pump(TimeSpan.FromMilliseconds(300));
            Save(window, Sibling(outFile, "repo-add"));
            vm.Repos.OpenAddInSprigCommand.Execute(null);
            vm.Repos.AddFolder = Sprig.App.Controls.PathCompletion.ToPosix(Path.Combine(root, "shop-web"));
            Pump(TimeSpan.FromMilliseconds(300));
            Save(window, Sibling(outFile, "repo-add-sprig"));
            vm.Repos.CancelAddCommand.Execute(null);
            // Start a workspace from a repo: the new-workspace form, with the repo chosen and the workspace's own choices.
            vm.Repos.Show("shop-services");
            clock.Restart();
            while (!vm.Repos.HasModel && clock.Elapsed < TimeSpan.FromSeconds(60)) Pump(TimeSpan.FromMilliseconds(100));
            Save(window, Sibling(outFile, "repo-ready"));
            vm.Repos.StartCommand.Execute(null);
            clock.Restart();
            while (!vm.Workspaces.HasNewSettings && clock.Elapsed < TimeSpan.FromSeconds(30)) Pump(TimeSpan.FromMilliseconds(100));
            Pump(TimeSpan.FromMilliseconds(300));
            Save(window, Sibling(outFile, "new-workspace"));
            vm.Workspaces.CancelAddCommand.Execute(null);

            // shop-web run in place with one value of its own, so its page lists a workspace and the workspace's Settings
            // dialog has something to show. It has no infra, so this needs no docker.
            if (services.Orchestrations.Resolve("shop-web") is { } webEntry)
            {
                var loaded = services.Loader.Load(webEntry);
                services.Workspaces.Up(loaded, "local", null, new Sprig.Engine.Workspaces.WorkspaceOptions(InPlace: true),
                    values: new Dictionary<string, string> { ["catalog-url"] = "https://catalog.example.com" });
                vm.NavigateCommand.Execute(vm.Repos);
                vm.Repos.Show("shop-web");
                clock.Restart();
                while (!vm.Repos.HasModel && clock.Elapsed < TimeSpan.FromSeconds(60)) Pump(TimeSpan.FromMilliseconds(100));
                Pump(TimeSpan.FromMilliseconds(300));
                Save(window, Sibling(outFile, "repo-workspaces"));
                vm.Repos.OpenWorkspaceRowCommand.Execute(vm.Repos.WorkspacesOfIt.First());
                Pump(TimeSpan.FromMilliseconds(300));
                Wait(vm.Workspaces.OpenSettingsCommand.ExecuteAsync(null));
                Pump(TimeSpan.FromMilliseconds(300));
                Save(window, Sibling(outFile, "workspace-settings"));
                vm.Workspaces.CancelSettingsCommand.Execute(null);
                services.Workspaces.Remove("shop-web", "local", new Sprig.Engine.Workspaces.RemoveOptions());
            }

            // An orchestration whose repos have settings none of them offers: they're asked for, so it can still start.
            if (Path.Combine(root, "shop-mobile") is var mobile && Directory.Exists(mobile))
            {
                var trial = services.Authoring.CreateOrchestration("trial", [Path.Combine(root, "shop-web"), mobile]);
                vm.NavigateCommand.Execute(vm.Orchestrations);
                vm.Orchestrations.Show(trial.Id);
                clock.Restart();
                Pump(TimeSpan.FromMilliseconds(300));
                while ((vm.Orchestrations.IsBusy || vm.Orchestrations.Plan is null) && clock.Elapsed < TimeSpan.FromSeconds(60)) Pump(TimeSpan.FromMilliseconds(100));
                Pump(TimeSpan.FromMilliseconds(300));
                Save(window, Sibling(outFile, "settings"));
                services.Authoring.RemoveOrchestration(trial.Id);
            }
            vm.NavigateCommand.Execute(vm.Orchestrations);
            vm.Orchestrations.Show("storefront");
            Pump(TimeSpan.FromMilliseconds(300));

            vm.Orchestrations.OpenAddCommand.Execute(null);
            vm.Orchestrations.NewPath = Sprig.App.Controls.PathCompletion.ToPosix(Path.GetFullPath(orchestrationFile));
            Pump(TimeSpan.FromMilliseconds(400));
            var addFile = Sibling(outFile, "add");
            Save(window, addFile);

            // The same file, added again once its approval is revoked, shows what a not-yet-approved orchestration looks like.
            services.Trust.Revoke(orchestrationFile);
            Wait(vm.Orchestrations.AddCommand.ExecuteAsync(null));
            Pump(TimeSpan.FromMilliseconds(400));
            var unapprovedFile = Sibling(outFile, "unapproved");
            Save(window, unapprovedFile);
            window.Close();

            Console.WriteLine($"rendered {Path.GetFullPath(outFile)} (plan: {planned}, problems: {vm.Orchestrations.LoadProblems.Count}, took {clock.Elapsed.TotalSeconds:0.0}s), {Path.GetFullPath(addFile)} and {Path.GetFullPath(unapprovedFile)}");
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
    /// The Workspaces page over the current store (<c>SPRIG_STORE</c>), read only, with its first workspace selected, its apps'
    /// states from the host and the first app's log open; and the progress window part way through a sample up, failing
    /// on a compose step, so both states of a row show.
    /// </summary>
    public static int RenderWorkspaces(string pageFile, string progressFile)
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
            vm.NavigateCommand.Execute(vm.Workspaces);
            vm.Workspaces.Show(vm.Workspaces.Workspaces.FirstOrDefault()?.Record.Key);
            var window = new MainWindow { DataContext = vm, Width = 1440, Height = 1400 };
            window.Show();
            Pump(TimeSpan.FromMilliseconds(500));

            // Timers don't tick here, so poll as the window's and the page's timers would, then open a log that has something in it.
            Wait(services.HostStatus.PollAsync());
            Wait(vm.Workspaces.PollAsync());
            if ((vm.Workspaces.Apps.FirstOrDefault(a => a.IsAlive && !a.IsJob) ?? vm.Workspaces.Apps.FirstOrDefault()) is { } app)
            {
                vm.Workspaces.OpenApp(app);
                Wait(vm.Workspaces.Logs.PollAsync());
            }
            Pump(TimeSpan.FromMilliseconds(600));
            Save(window, pageFile);
            window.Close();

            var progress = new OperationProgressViewModel("Starting storefront / feat-x");
            Sprig.Engine.Workspaces.WorkspaceEvent[] sample =
            [
                new("Plan", Sprig.Engine.Workspaces.StepState.Running),
                new("Plan", Sprig.Engine.Workspaces.StepState.Done, "4 apps, 1 containers, 1 compose files"),
                new("Worktree shop-services", Sprig.Engine.Workspaces.StepState.Running),
                new("Worktree shop-services", Sprig.Engine.Workspaces.StepState.Done, "../storefront--feat-x/shop-services on feat-x (new, from main)"),
                new("Ports", Sprig.Engine.Workspaces.StepState.Running),
                new("Ports", Sprig.Engine.Workspaces.StepState.Done, "catalog-api.http 8000, postgres.tcp 8002"),
                new("Start sprig-storefront-feat-x", Sprig.Engine.Workspaces.StepState.Running),
                new("Start sprig-storefront-feat-x", Sprig.Engine.Workspaces.StepState.Output, "Container sprig-storefront-feat-x-storefront-mail-1 Waiting"),
                new("Start sprig-storefront-feat-x", Sprig.Engine.Workspaces.StepState.Done, "running; healthy: mail"),
                new("Start sprig-storefront-feat-x-shop-services", Sprig.Engine.Workspaces.StepState.Running),
                new("Start sprig-storefront-feat-x-shop-services", Sprig.Engine.Workspaces.StepState.Output, "Container sprig-storefront-feat-x-shop-services-postgres-1 Starting"),
                new("Start sprig-storefront-feat-x-shop-services", Sprig.Engine.Workspaces.StepState.Output, "Error response from daemon: port is already allocated"),
            ];
            foreach (var e in sample) progress.Apply(e);
            progress.Finish("command failed (exit 1): docker compose up (a sample failure, rendered for review)", ProgressState.Error);
            progress.Steps[^1].State = ProgressState.Error;
            var dialog = new OperationProgressWindow { DataContext = progress };
            dialog.Show();
            Pump(TimeSpan.FromMilliseconds(400));
            Save(dialog, progressFile);
            dialog.Close();

            Console.WriteLine($"rendered {Path.GetFullPath(pageFile)} ({vm.Workspaces.Workspaces.Count} workspaces) and {Path.GetFullPath(progressFile)}");
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
