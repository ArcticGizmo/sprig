using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Sprig.App.Controls;
using Sprig.App.ViewModels;
using Sprig.App.Views;
using Sprig.Core.Stacks;
using Sprig.Core.Store;

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
                // Check the repos so the stack variable editor shows auto-detected vars.
                if (page is StacksViewModel stacks)
                {
                    // Detail panel for an existing stack.
                    stacks.Selected = stacks.Stacks.FirstOrDefault();
                    Capture(vm, Path.Combine(outDir, "main_stacks_detail.png"));

                    // The same stack as the full-width wiring diagram (list column collapsed).
                    if (stacks.Selected is not null)
                    {
                        stacks.ShowDiagram = true;
                        Capture(vm, Path.Combine(outDir, "main_stacks_diagram.png"));
                        stacks.ShowDiagram = false;
                    }

                    // Edit an existing stack (when nothing depends on it).
                    if (stacks.EditSelectedCommand.CanExecute(null))
                    {
                        stacks.EditSelectedCommand.Execute(null);
                        Capture(vm, Path.Combine(outDir, "main_stacks_edit.png"));
                        stacks.CancelCreateCommand.Execute(null);
                    }

                    // The New-stack builder (canvas is the only surface; auto-wire so it has cables).
                    stacks.NewStackCommand.Execute(null);
                    stacks.NewName = "web+api";
                    foreach (var c in stacks.RepoChoices) c.IsSelected = true;
                    stacks.AutoWireCommand.Execute(null);
                    Capture(vm, Path.Combine(outDir, "main_stacks_builder_diagram.png"));
                }
                // Populate the Settings port-checker so the snapshot shows a status result.
                if (page is SettingsViewModel settings)
                    settings.CheckText = "8080";
                Capture(vm, Path.Combine(outDir, $"main_{page.Title.ToLowerInvariant()}.png"));

                // Also capture the repos edit form when a repo is available.
                if (page is ReposViewModel { Selected: not null } editable && editable.BeginEditCommand.CanExecute(null))
                {
                    editable.BeginEditCommand.Execute(null);
                    Capture(vm, Path.Combine(outDir, "main_repos_edit.png"));
                    editable.CancelEditCommand.Execute(null);
                }

                // Capture the add-repo modal in both git states (a real repo path vs a non-repo folder).
                if (page is ReposViewModel { Repos.Count: > 0 } adder)
                {
                    adder.OpenAddCommand.Execute(null);
                    adder.NewPath = adder.Repos[0].Path;      // a real git repo → green highlight
                    Capture(vm, Path.Combine(outDir, "main_repos_add_git.png"));
                    adder.NewPath = outDir;                    // not a git repo → red warning
                    Capture(vm, Path.Combine(outDir, "main_repos_add_nogit.png"));
                    adder.CancelAddCommand.Execute(null);
                }
            }

            // Home's journey rail + next-best-action depend on store state, so render the two
            // states the live store can't show at once: first-run (empty) and running.
            RenderHomeStates(outDir);

            // The stack wiring diagram (patchbay), from a fixed sample so it renders the same
            // regardless of what's in the live store — and so drawing it is exercised on real Skia.
            RenderWiringSample(outDir);

            // The guided setup strip, active over Home.
            var guideVm = new MainWindowViewModel(services);
            guideVm.CurrentPage = guideVm.Pages[0];
            guideVm.Guide.Start();
            Capture(guideVm, Path.Combine(outDir, "main_guide.png"));

            // The "what's new" changelog window (the post-update popup / About viewer).
            var markdown = Changelog.ChangelogMarkdown.LoadEmbedded();
            var sections = markdown is null
                ? new List<Sprig.Core.Changelog.ChangelogSection>()
                : Sprig.Core.Changelog.ChangelogParser.Parse(markdown).ToList();
            var changelog = new ChangelogWindow("What's new in sprig", "Recent releases", sections);
            changelog.Show();
            Dispatcher.UIThread.RunJobs();
            changelog.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "changelog.png"));
            changelog.Close();

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

    /// <summary>
    /// Renders Home in the states the live store can't show at once. Builds the view-model with a
    /// synthetic <see cref="SetupState"/> and never activates it, so <c>OnActivated</c> can't
    /// overwrite the state with real-store data.
    /// </summary>
    static void RenderHomeStates(string outDir)
    {
        var services = new AppServices();

        HomeViewModel Home(SetupState state, IReadOnlyList<InstanceRecord> recent)
        {
            var vm = new HomeViewModel(services, new Navigator()) { State = state };
            foreach (var r in recent) vm.Recent.Add(new WorkspaceItemViewModel(r));
            return vm;
        }

        CaptureControl(new HomeView { DataContext = Home(new SetupState(0, 0, 0), []) },
            Path.Combine(outDir, "home_empty.png"));

        IReadOnlyList<InstanceRecord> running =
        [
            Rec("feature-auth", "web+api", "infra up", ("frontend_port", 5173), ("api_port", 5080)),
            Rec("hotfix-invoices", "web+api", "created", ("frontend_port", 5174), ("api_port", 5081)),
        ];
        CaptureControl(new HomeView { DataContext = Home(new SetupState(2, 1, 2), running) },
            Path.Combine(outDir, "home_running.png"));
    }

    static InstanceRecord Rec(string workspace, string stack, string status, params (string Name, int Port)[] ports)
        => new()
        {
            Workspace = workspace,
            Stack = stack,
            LastStatus = status,
            CreatedAt = DateTimeOffset.UtcNow,
            Ports = ports.ToDictionary(p => p.Name, p => p.Port),
        };

    /// <summary>Render the patchbay for a fixed multi-repo sample (two shared ports + a transform).</summary>
    static void RenderWiringSample(string outDir)
    {
        string[] repos = ["sprig-example-vue", "dotnet-api", "worker"];
        string[] ports = ["frontend_port", "api_port", "postgres_port", "queue_port"];
        var inputs = new Dictionary<string, IReadOnlyList<string>>
        {
            ["sprig-example-vue"] = ["frontend", "apiUrl"],
            ["dotnet-api"] = ["port", "dbPort"],
            ["worker"] = ["dbPort", "queuePort", "dbAddr"],
        };
        var bindings = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["sprig-example-vue"] = new Dictionary<string, string>
            {
                ["frontend"] = "${sprig.ports.frontend_port}",
                ["apiUrl"] = "http://localhost:${sprig.ports.api_port}",
            },
            ["dotnet-api"] = new Dictionary<string, string>
            {
                ["port"] = "${sprig.ports.api_port}",       // shares api_port with vue.apiUrl
                ["dbPort"] = "${sprig.ports.postgres_port}",
            },
            ["worker"] = new Dictionary<string, string>
            {
                ["dbPort"] = "${sprig.ports.postgres_port}", // shares postgres_port with dotnet-api.dbPort
                ["queuePort"] = "${sprig.ports.queue_port}",
                ["dbAddr"] = "${sprig.ports.postgres_port}:${sprig.ports.queue_port}", // fan-in: two ports → one node
            },
        };

        var graph = WiringGraph.Build(repos, ports, inputs, bindings);
        CaptureControl(new WiringCanvas { Graph = graph }, Path.Combine(outDir, "stacks_wiring_diagram.png"));
    }

    static void CaptureControl(Control content, string path)
    {
        var window = new Avalonia.Controls.Window
        {
            Width = 1100,
            Height = 760,
            Background = new SolidColorBrush(Color.Parse("#181820")),
            Content = content,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        frame?.Save(path);
        window.Close();
    }
}
