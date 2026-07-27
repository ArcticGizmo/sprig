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
using Sprig.Core.Workspaces;

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
                    // Detail panel for an existing stack (repos / ports / inputs summary).
                    stacks.Selected = stacks.Stacks.FirstOrDefault();
                    Capture(vm, Path.Combine(outDir, "main_stacks_detail.png"));

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

            // The guided tour, now coachmarks: each step spotlights its target over the real sample. An
            // unresolved anchor here is a failure, same as the spike and guides.
            var unresolved = RenderGuidedTour(outDir);

            // The coachmark spike: one frame per anchor case. Unlike every other capture here, an
            // unresolved anchor is treated as a failure rather than logged — a coachmark pointing at
            // nothing is exactly the bug this harness exists to catch, so it must break the build.
            unresolved += RenderCoachSpike(outDir);

            // Guide 1, driven the way a user would: register the sample repo via "Show me", then read what
            // it declares. Also renders the Learn list before and after, to show the completion tick.
            unresolved += RenderRegisterRepoGuide(outDir);

            // Guide 2, driven as a user would: open the stack builder, read the wiring, create the stack.
            unresolved += RenderWireStackGuide(outDir);

            // Guide 3: create a workspace from the stack, then see what sprig made.
            unresolved += RenderRunWorkspaceGuide(outDir);

            // Guide 4 (drift): break a worktree, then Repair it.
            unresolved += RenderRepairDriftGuide(outDir);

            // Row-level spotlight: one repo in the list highlighted, everything else dimmed.
            unresolved += RenderRowHighlight(outDir);

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

            // The create/teardown progress window, with a representative mix of step states so every
            // indicator (pending, running spinner, warning, done) is visible in one frame.
            RenderProgressModal(outDir);

            Console.WriteLine($"rendered to {Path.GetFullPath(outDir)}");

            if (unresolved > 0)
            {
                Console.Error.WriteLine(
                    $"{unresolved} coachmark anchor(s) did not resolve — see the messages above");
                return 1;
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"headless render failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Render the guided tour, now that it's coachmarks: each step over the real sample, dimming the page and
    /// ringing its target. Docker is pinned so the run is deterministic — the base script (no daemon) is
    /// walked in full, then the infra step is posed from a Docker-up run. Any step whose anchor doesn't
    /// resolve is a failure, like the guide render.
    /// </summary>
    static int RenderGuidedTour(string outDir)
    {
        var root = Path.Combine(Path.GetTempPath(), "sprig-render-tour-" + Guid.NewGuid().ToString("N"));
        var demo = new AppServices(root, isDemoStore: true);
        var unresolved = 0;
        try
        {
            demo.Sample.Build();

            // Base script, Docker off: walk every step, capturing the coach frame (callout + spotlight).
            var window = new MainWindow { DataContext = new MainWindowViewModel(demo, dockerIsRunning: () => false) };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var vm = (MainWindowViewModel)window.DataContext!;

            Pump(vm.StartTour());
            for (var step = 1; vm.Coach.IsActive; step++)
            {
                unresolved += CaptureCoachStep(window, vm, outDir, $"tour_stop{step}");
                Pump(vm.Coach.NextCommand.ExecuteAsync(null));
            }
            window.Close();

            // The optional Docker step, posed rather than executed (running it would pull an image): start a
            // Docker-up tour and step to the infra mark (second from last).
            var dwindow = new MainWindow { DataContext = new MainWindowViewModel(demo, dockerIsRunning: () => true) };
            dwindow.Show();
            Dispatcher.UIThread.RunJobs();
            var dvm = (MainWindowViewModel)dwindow.DataContext!;
            Pump(dvm.StartTour());
            // Step (which re-navigates each time) up to the infra mark, second from last. Stop there — Next on
            // it would Perform the container start, which we don't want in a render.
            while (dvm.Coach.Index < dvm.Coach.Count - 2)
                Pump(dvm.Coach.NextCommand.ExecuteAsync(null));
            unresolved += CaptureCoachStep(dwindow, dvm, outDir, "tour_stop_infra");
            dwindow.Close();

            // The build checklist the user watches on the way in.
            var progress = new OperationProgressViewModel("Building your sample setup");
            progress.Load(Sprig.Core.Demo.SampleSetup.PlanBuild());
            progress.Steps[0].State = WorkspaceStepState.Done;
            progress.Steps[1].State = WorkspaceStepState.Done;
            progress.Steps[2].State = WorkspaceStepState.Running;
            var pwindow = new OperationProgressWindow { DataContext = progress };
            pwindow.Show();
            Dispatcher.UIThread.RunJobs();
            pwindow.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "tour_building.png"));
            pwindow.Close();
        }
        catch (Exception ex)
        {
            unresolved++;
            Console.Error.WriteLine($"guided-tour render failed: {ex.Message}");
        }
        finally
        {
            try { demo.Sample.Destroy(); } catch { /* best-effort */ }
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
        return unresolved;
    }

    /// <summary>
    /// Render the coachmark spike: one frame per anchor case, over the tour's sample so the canvas has real
    /// repos to draw. Reports on stderr when a mark's anchor failed to resolve — which is the whole question
    /// the spike exists to answer, so it must be loud rather than a silently mispositioned callout.
    /// </summary>
    /// <returns>How many of the spike's anchors failed to resolve — non-zero fails the render.</returns>
    static int RenderCoachSpike(string outDir)
    {
        var root = Path.Combine(Path.GetTempPath(), "sprig-render-coach-" + Guid.NewGuid().ToString("N"));
        var demo = new AppServices(root, isDemoStore: true);
        var unresolved = 0;
        try
        {
            demo.Sample.Build();

            var window = new MainWindow { DataContext = new MainWindowViewModel(demo, dockerIsRunning: () => false) };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var vm = (MainWindowViewModel)window.DataContext!;
            var marks = vm.CoachSpikeMarks;
            Pump(vm.Coach.StartAsync(marks));

            for (var i = 0; i < marks.Count; i++)
            {
                // Two layout passes: the first realises whatever the precondition opened, the second lets the
                // overlay's deferred reposition run against it.
                Dispatcher.UIThread.RunJobs();
                Dispatcher.UIThread.RunJobs();

                if (vm.Coach.AnchorMissing)
                {
                    unresolved++;
                    Console.Error.WriteLine($"coach spike: anchor '{marks[i].Anchor}' did not resolve");
                }

                window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, $"coach_case{i + 1}.png"));
                Pump(vm.Coach.NextCommand.ExecuteAsync(null));
            }

            window.Close();
        }
        catch (Exception ex)
        {
            // A spike that can't run at all is also a failure — otherwise the gate silently passes.
            unresolved++;
            Console.Error.WriteLine($"coach spike render failed: {ex.Message}");
        }
        finally
        {
            try { demo.Sample.Destroy(); } catch { /* best-effort */ }
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }

        return unresolved;
    }

    /// <summary>
    /// Render guide 1 ("Register your first repo") the way a user experiences it: the Learn list, then each
    /// coachmark, driving the middle (waiting) step via "Show me" so the auto-advance path is exercised, then
    /// the Learn list again with its completion tick. Any step whose anchor doesn't resolve is a failure.
    /// </summary>
    static int RenderRegisterRepoGuide(string outDir)
    {
        var root = Path.Combine(Path.GetTempPath(), "sprig-render-guide-" + Guid.NewGuid().ToString("N"));
        var demo = new AppServices(root, isDemoStore: true);
        var unresolved = 0;
        try
        {
            var guide = Sprig.App.Coach.Guides.All[0];
            demo.Sample.BuildTo(Sprig.Core.Demo.SampleStage.RepoOnDisk);

            var window = new MainWindow { DataContext = new MainWindowViewModel(demo, dockerIsRunning: () => false) };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var vm = (MainWindowViewModel)window.DataContext!;

            // The Learn list, before starting.
            vm.CurrentPage = vm.Learn;
            SettleFrame(window, outDir, "guide1_learn");

            // Start the guide; the first (waiting) step highlights Add repo.
            Pump(vm.StartGuide(guide, () => MarkDemoGuideDone(demo, guide.Id)));
            unresolved += CaptureCoachStep(window, vm, outDir, "guide1_step1");

            // The user is stuck → "Show me" registers the repo, which fires StoreChanged and auto-advances.
            Pump(vm.Coach.ShowMeCommand.ExecuteAsync(null));
            unresolved += CaptureCoachStep(window, vm, outDir, "guide1_step2");

            // Read the declared inputs, then finish.
            Pump(vm.Coach.NextCommand.ExecuteAsync(null));
            unresolved += CaptureCoachStep(window, vm, outDir, "guide1_step3");
            Pump(vm.Coach.NextCommand.ExecuteAsync(null)); // Done → onFinished

            // The Learn list again, now ticked.
            vm.Learn.Refresh();
            vm.CurrentPage = vm.Learn;
            SettleFrame(window, outDir, "guide1_learn_done");

            window.Close();
        }
        catch (Exception ex)
        {
            unresolved++;
            Console.Error.WriteLine($"guide 1 render failed: {ex.Message}");
        }
        finally
        {
            try { demo.Sample.Destroy(); } catch { /* best-effort */ }
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
        return unresolved;
    }

    /// <summary>
    /// Render guide 4 ("Recover from drift"): the opening step breaks a worktree and reconciles so drift
    /// shows; the repair step (via "Show me") prunes it, which fires a store change and auto-advances.
    /// </summary>
    static int RenderRepairDriftGuide(string outDir)
    {
        var root = Path.Combine(Path.GetTempPath(), "sprig-render-guide4-" + Guid.NewGuid().ToString("N"));
        var demo = new AppServices(root, isDemoStore: true);
        var unresolved = 0;
        try
        {
            var guide = Sprig.App.Coach.Guides.All.Single(g => g.Id == Sprig.App.Coach.Guides.RepairDriftId);
            demo.Sample.Build();  // full Running sample

            var window = new MainWindow { DataContext = new MainWindowViewModel(demo, dockerIsRunning: () => false) };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var vm = (MainWindowViewModel)window.DataContext!;

            Pump(vm.StartGuide(guide, () => MarkDemoGuideDone(demo, guide.Id)));
            unresolved += CaptureCoachStep(window, vm, outDir, "guide4_step1");     // drift shown
            Pump(vm.Coach.NextCommand.ExecuteAsync(null));
            unresolved += CaptureCoachStep(window, vm, outDir, "guide4_step2");     // repair (waiting)

            // "Show me" repairs (prunes the stale registration), which fires a store change and advances.
            Pump(vm.Coach.ShowMeCommand.ExecuteAsync(null));
            unresolved += CaptureCoachStep(window, vm, outDir, "guide4_step3");     // recovered
            Pump(vm.Coach.NextCommand.ExecuteAsync(null)); // Done

            window.Close();
        }
        catch (Exception ex)
        {
            unresolved++;
            Console.Error.WriteLine($"guide 4 render failed: {ex.Message}");
        }
        finally
        {
            try { demo.Sample.Destroy(); } catch { /* best-effort */ }
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
        return unresolved;
    }

    /// <summary>
    /// Render guide 3 ("Create and run a workspace"): why → create (via "Show me", which does the real
    /// worktree work behind a progress checklist and auto-advances) → what got made → the handoff.
    /// </summary>
    static int RenderRunWorkspaceGuide(string outDir)
    {
        var root = Path.Combine(Path.GetTempPath(), "sprig-render-guide3-" + Guid.NewGuid().ToString("N"));
        var demo = new AppServices(root, isDemoStore: true);
        var unresolved = 0;
        try
        {
            var guide = Sprig.App.Coach.Guides.All.Single(g => g.Id == Sprig.App.Coach.Guides.RunWorkspaceId);
            demo.Sample.BuildTo(Sprig.Core.Demo.SampleStage.StackWired);

            var window = new MainWindow { DataContext = new MainWindowViewModel(demo, dockerIsRunning: () => false) };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var vm = (MainWindowViewModel)window.DataContext!;

            Pump(vm.StartGuide(guide, () => MarkDemoGuideDone(demo, guide.Id)));
            unresolved += CaptureCoachStep(window, vm, outDir, "guide3_step1");     // why a workspace
            Pump(vm.Coach.NextCommand.ExecuteAsync(null));
            unresolved += CaptureCoachStep(window, vm, outDir, "guide3_step2");     // create (waiting)

            // "Show me" creates the workspace — real worktrees, behind a progress window — then the store
            // change auto-advances to the "what got made" step.
            Pump(vm.Coach.ShowMeCommand.ExecuteAsync(null));
            unresolved += CaptureCoachStep(window, vm, outDir, "guide3_step3");     // what sprig made
            Pump(vm.Coach.NextCommand.ExecuteAsync(null));
            unresolved += CaptureCoachStep(window, vm, outDir, "guide3_step4");     // handoff
            Pump(vm.Coach.NextCommand.ExecuteAsync(null)); // Done

            window.Close();
        }
        catch (Exception ex)
        {
            unresolved++;
            Console.Error.WriteLine($"guide 3 render failed: {ex.Message}");
        }
        finally
        {
            try { demo.Sample.Destroy(); } catch { /* best-effort */ }
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
        return unresolved;
    }

    /// <summary>
    /// Render guide 2 ("Wire up a multi-repo stack") the way a user drives it: the builder-driving steps, then
    /// the create step via "Show me" (which saves the stack and auto-advances), then the handoff. Any step
    /// whose anchor doesn't resolve is a failure.
    /// </summary>
    static int RenderWireStackGuide(string outDir)
    {
        var root = Path.Combine(Path.GetTempPath(), "sprig-render-guide2-" + Guid.NewGuid().ToString("N"));
        var demo = new AppServices(root, isDemoStore: true);
        var unresolved = 0;
        try
        {
            var guide = Sprig.App.Coach.Guides.All.Single(g => g.Id == Sprig.App.Coach.Guides.WireStackId);
            demo.Sample.BuildTo(Sprig.Core.Demo.SampleStage.ReposRegistered);

            var window = new MainWindow { DataContext = new MainWindowViewModel(demo, dockerIsRunning: () => false) };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var vm = (MainWindowViewModel)window.DataContext!;

            Pump(vm.StartGuide(guide, () => MarkDemoGuideDone(demo, guide.Id)));
            unresolved += CaptureCoachStep(window, vm, outDir, "guide2_step1");     // why a stack
            Pump(vm.Coach.NextCommand.ExecuteAsync(null));
            unresolved += CaptureCoachStep(window, vm, outDir, "guide2_step2");     // builder + wiring
            Pump(vm.Coach.NextCommand.ExecuteAsync(null));
            unresolved += CaptureCoachStep(window, vm, outDir, "guide2_step3");     // own ports / sharing
            Pump(vm.Coach.NextCommand.ExecuteAsync(null));
            unresolved += CaptureCoachStep(window, vm, outDir, "guide2_step4");     // create (waiting)

            // "Show me" creates the stack, which fires StoreChanged and auto-advances to the handoff.
            Pump(vm.Coach.ShowMeCommand.ExecuteAsync(null));
            unresolved += CaptureCoachStep(window, vm, outDir, "guide2_step5");     // handoff
            Pump(vm.Coach.NextCommand.ExecuteAsync(null)); // Done

            window.Close();
        }
        catch (Exception ex)
        {
            unresolved++;
            Console.Error.WriteLine($"guide 2 render failed: {ex.Message}");
        }
        finally
        {
            try { demo.Sample.Destroy(); } catch { /* best-effort */ }
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
        return unresolved;
    }

    /// <summary>
    /// Prove the row-level spotlight: point a coachmark at one specific repo row (anchored from its data)
    /// with two repos registered, so the cut-out lands on exactly that row and everything else dims.
    /// </summary>
    static int RenderRowHighlight(string outDir)
    {
        var root = Path.Combine(Path.GetTempPath(), "sprig-render-row-" + Guid.NewGuid().ToString("N"));
        var demo = new AppServices(root, isDemoStore: true);
        var unresolved = 0;
        try
        {
            demo.Sample.BuildTo(Sprig.Core.Demo.SampleStage.ReposRegistered);

            var window = new MainWindow { DataContext = new MainWindowViewModel(demo, dockerIsRunning: () => false) };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var vm = (MainWindowViewModel)window.DataContext!;

            // Show the repo list and let its rows realise, then spotlight just sample-api.
            vm.CurrentPage = vm.Pages.OfType<ReposViewModel>().First();
            Dispatcher.UIThread.RunJobs();
            Dispatcher.UIThread.RunJobs();

            var mark = new CoachMark(
                Sprig.App.Coach.Anchors.RepoRow(Sprig.Core.Demo.SampleFixtures.ApiRepo),
                "This is a repo",
                "Each row is a repo sprig knows about. Everything else is dimmed so your eye goes straight to this one.")
            { Side = CoachSide.Right };

            Pump(vm.Coach.StartAsync([mark]));
            unresolved += CaptureCoachStep(window, vm, outDir, "row_highlight");

            window.Close();
        }
        catch (Exception ex)
        {
            unresolved++;
            Console.Error.WriteLine($"row-highlight render failed: {ex.Message}");
        }
        finally
        {
            try { demo.Sample.Destroy(); } catch { /* best-effort */ }
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
        return unresolved;
    }

    /// <summary>Let two layout passes run, capture the current coach frame, and report an unresolved anchor.</summary>
    static int CaptureCoachStep(MainWindow window, MainWindowViewModel vm, string outDir, string name)
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, name + ".png"));
        if (!vm.Coach.AnchorMissing) return 0;
        Console.Error.WriteLine($"{name}: anchor '{vm.Coach.Mark?.Anchor}' did not resolve");
        return 1;
    }

    static void SettleFrame(MainWindow window, string outDir, string name)
    {
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, name + ".png"));
    }

    /// <summary>Tick a guide complete in the demo store's own settings, so the render can show the tick.</summary>
    static void MarkDemoGuideDone(AppServices demo, string guideId)
    {
        var settings = demo.Settings.Get();
        if (!settings.CompletedGuides.Contains(guideId)) settings.CompletedGuides.Add(guideId);
        demo.Settings.Save(settings);
    }

    /// <summary>
    /// Drive an async view-model call to completion on the render thread. The awaited continuations are
    /// posted to the dispatcher, so blocking on the task would deadlock — the jobs have to be pumped.
    /// </summary>
    static void Pump(Task task)
    {
        while (!task.IsCompleted)
            Dispatcher.UIThread.RunJobs();
        task.GetAwaiter().GetResult();
    }

    /// <summary>Render the operation-progress checklist mid-run: ports/env/compose done, a soft setup
    /// warning, one step spinning, and later steps still pending.</summary>
    static void RenderProgressModal(string outDir)
    {
        var progress = new OperationProgressViewModel("Creating workspace 'feature-auth'");
        progress.Load(
        [
            new WorkspaceStep("ports", "Allocate ports"),
            new WorkspaceStep("vue:worktree", "Create worktree — vue"),
            new WorkspaceStep("vue:env", "Apply environment — vue"),
            new WorkspaceStep("vue:compose", "Generate compose — vue"),
            new WorkspaceStep("vue:setup", "Install dependencies — vue"),
            new WorkspaceStep("vue:setup:0", "npm ci") { SubStep = true },
            new WorkspaceStep("vue:setup:1", "npm run build") { SubStep = true },
            new WorkspaceStep("record", "Save workspace record"),
        ]);
        progress.AddStep("infra", "Start infrastructure");
        progress.Apply(new("ports", WorkspaceStepState.Done));
        progress.Apply(new("vue:worktree", WorkspaceStepState.Done));
        progress.Apply(new("vue:env", WorkspaceStepState.Done));
        progress.Apply(new("vue:compose", WorkspaceStepState.Done));
        progress.Apply(new("vue:setup", WorkspaceStepState.Running));
        progress.Apply(new("vue:setup:0", WorkspaceStepState.Done));
        progress.Apply(new("vue:setup:1", WorkspaceStepState.Running));
        // A few streamed output lines for the currently-running command.
        foreach (var line in new[]
                 {
                     "> vite build",
                     "vite v5.4.2 building for production...",
                     "transforming (247) src/components/App.vue",
                     "✓ 312 modules transformed.",
                     "rendering chunks...",
                 })
            progress.Apply(new("vue:setup:1", WorkspaceStepState.Running) { Output = line });

        var window = new OperationProgressWindow { DataContext = progress };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "operation_progress.png"));
        window.Close();
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
