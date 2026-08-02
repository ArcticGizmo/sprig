using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Sprig.App.ViewModels;
using Sprig.Core.Demo;

namespace Sprig.App.Coach;

/// <summary>
/// One guide: a small, self-contained lesson that teaches a single concept by hand-holding the user through
/// doing it, in the throwaway demo sandbox where nothing is at stake.
///
/// A guide names the sandbox <see cref="Stage"/> it starts from — the state just <i>before</i> the thing it
/// teaches, so the user performs that step themselves — and builds its steps from the live navigator and
/// services, so each step's wait predicate and "show me" act on the real sample.
/// </summary>
/// <param name="Id">Stable id, used to record completion in settings.</param>
/// <param name="Title">Short lesson name for the Learn list.</param>
/// <param name="Subtitle">One line on what it teaches.</param>
/// <param name="Duration">Rough time, e.g. "2 min".</param>
/// <param name="Stage">The sandbox state the guide starts from.</param>
/// <param name="Build">Builds the steps against the live navigator + services.</param>
public sealed record Guide(
    string Id,
    string Title,
    string Subtitle,
    string Duration,
    SampleStage Stage,
    System.Func<Navigator, AppServices, IReadOnlyList<CoachMark>> Build);

/// <summary>
/// The guide catalog — the ladder of lessons, each introducing one concept. Ordered easiest-first:
/// register a repo, split it into modules, wire a multi-repo stack, run a workspace, and recover from drift.
/// </summary>
public static class Guides
{
    public const string RegisterRepoId = "register-repo";
    public const string SplitModulesId = "split-modules";
    public const string WireStackId = "wire-stack";
    public const string RunWorkspaceId = "run-workspace";
    public const string RepairDriftId = "repair-drift";

    public static IReadOnlyList<Guide> All { get; } =
    [
        new(RegisterRepoId,
            "Register your first repo",
            "Tell sprig about a repo, and see what it declares.",
            "2 min",
            SampleStage.RepoOnDisk,
            RegisterRepoSteps),

        new(SplitModulesId,
            "Split a repo into modules",
            "One repo, many slices — each with its own env, compose and setup.",
            "2 min",
            SampleStage.ReposRegistered,
            SplitModulesSteps),

        new(WireStackId,
            "Wire up a multi-repo stack",
            "Compose two repos into one runnable stack.",
            "3 min",
            SampleStage.ReposRegistered,
            WireStackSteps),

        new(RunWorkspaceId,
            "Create and run a workspace",
            "Spin up a live, isolated copy of a stack.",
            "3 min",
            SampleStage.StackWired,
            RunWorkspaceSteps),

        new(RepairDriftId,
            "Recover from drift",
            "See how sprig detects and fixes a broken workspace.",
            "2 min",
            SampleStage.Running,
            RepairDriftSteps),
    ];

    /// <summary>Guide 1: point sprig at a repo on disk, then read what that repo declares.</summary>
    static IReadOnlyList<CoachMark> RegisterRepoSteps(Navigator nav, AppServices services)
    {
        var apiPath = Path.Combine(services.Sample.SampleReposDir, SampleFixtures.ApiRepo);
        bool ApiRegistered() => services.Repos.Get(SampleFixtures.ApiRepo) is not null;

        return
        [
            // Waiting: the user actually registers the repo. Prime the modal so their manual route is a
            // single Confirm, and hand them the same result via "Show me" if they get stuck.
            new(Anchors.ReposAdd,
                "Everything starts with a repo",
                "This is the Repos page — where you tell sprig about the code you want to isolate. Nothing's registered yet. Click Add repo (the sample-api folder is filled in for you) and confirm — or use Show me.")
            {
                Side = CoachSide.Below,
                Prepare = () => { nav.PrimeAddRepo(apiPath); return Task.CompletedTask; },
                Completed = ApiRegistered,
                ShowMe = () => nav.RegisterRepo(apiPath),
            },

            // Explanation: registration dropped us into the editor. Point at what the repo declares.
            new(Anchors.RepoInputs,
                "A repo declares what it needs",
                "These are inputs and they represent what needs to be defined for the repo to be created in isolation")
            {
                Side = CoachSide.Right,
                Prepare = () => { nav.EditRepo(SampleFixtures.ApiRepo); return Task.CompletedTask; },
            },

            new(Anchors.RepoModules,
                "And here's where those inputs get used",
                "For each .env, docker compose and setup command you can reference the previous inputs and they will be injected at run time.")
            {
                Side = CoachSide.Left,
                Prepare = () => { nav.EditRepo(SampleFixtures.ApiRepo); return Task.CompletedTask; },
            },
        ];
    }

    /// <summary>
    /// Guide 2: split a single repo into modules — the monorepo lesson. Starts at
    /// <see cref="SampleStage.ReposRegistered"/> (sample-api is registered, so its editor opens) and walks:
    /// what a module is → inputs stay shared → add a second module → what that module owns → the handoff.
    /// <para>Every step is an explanation that advances on Next, not a waiting step: adding a module is
    /// editor state, not a store change, and the coach only re-checks a wait on <c>StoreChanged</c>. So the
    /// module-driving steps put the editor into each state from their <c>Prepare</c> (idempotently), the same
    /// pattern the stack-builder guide uses for its UI-only transitions.</para>
    /// </summary>
    static IReadOnlyList<CoachMark> SplitModulesSteps(Navigator nav, AppServices services)
    {
        const string NewModule = "api";
        const string NewModulePath = "apps/api";

        return
        [
            new(Anchors.RepoModules,
                "One repo can have many slices",
                "When working with a monorepo you can scope .env, docker compose and setup commands per directory like apps/web, apps/api or apps/mobile.\n\nHere we have an example monorepo with one module defined. In the next steps we will add another.")
            {
                Side = CoachSide.Left,
                Prepare = () => { nav.PrepareRepoEditor(SampleFixtures.ApiRepo); return Task.CompletedTask; },
            },

            new(Anchors.RepoInputs,
                "Inputs stay shared",
                "Regardless of the number of modules, inputs remain shared across all of them.")
            {
                Side = CoachSide.Right,
                Prepare = () => { nav.PrepareRepoEditor(SampleFixtures.ApiRepo); return Task.CompletedTask; },
            },

            new(Anchors.RepoAddModule,
                "Add a second slice",
                "You can add another module (or more) from here.")
            {
                Side = CoachSide.Left,
                Prepare = () => { nav.PrepareRepoEditor(SampleFixtures.ApiRepo); return Task.CompletedTask; },
            },

            new(Anchors.RepoModules,
                "A module of its own",
                "Here is the new \"api\" module referencing apps/api. Each of the .env, docker compose and setup commands will resolve RELATIVE to this path.")
            {
                Side = CoachSide.Left,
                Prepare = () => { nav.AddModuleTo(SampleFixtures.ApiRepo, NewModule, NewModulePath); return Task.CompletedTask; },
            },

            new(null,
                "Wrapping up",
                "One registered repo, as many modules as you want, while maintaining a single shared set of inputs.\n\nThe example here is sandboxed so you can mess around with it interactively as much as you want. hit \"Exit\" top right to set up your own repos.")
            { Prepare = () => { nav.PrepareRepoEditor(SampleFixtures.ApiRepo); return Task.CompletedTask; } },
        ];
    }

    /// <summary>
    /// Guide 3: compose the two registered repos into one stack. Starts at <see cref="SampleStage.ReposRegistered"/>
    /// (both repos known, no stack), and walks: why a stack → open the builder → read the auto-wiring →
    /// create it. Only the final step waits on the user; the builder-driving steps prepare state and advance
    /// on Next, since opening a builder is UI state, not a store change.
    /// </summary>
    static IReadOnlyList<CoachMark> WireStackSteps(Navigator nav, AppServices services)
    {
        const string StackName = "web+api";
        // At this stage there are no stacks; the first one to appear is the one the user just built.
        bool StackCreated() => services.Stacks.List().Count > 0;

        return
        [
            new(Anchors.StackNew,
                "Two repos, nothing tying them together",
                "sample-api and sample-web are both registered, but on their own they don't know about each other. A stack composes repos into one runnable set and supplies the values each one needs. Let's build one.")
            {
                Side = CoachSide.Below,
                Prepare = () => { nav.ShowStacksFresh(); return Task.CompletedTask; },
            },

            new(Anchors.StackCanvas,
                "Both repos, wired by convention",
                "Here's the builder. Both repos sit on the right, the stack's ports on the left. Selecting the repos auto-wired every input to a port — each cable shows what feeds what. sample-api's port and dbPort, sample-web's port and apiUrl: all supplied by the stack.")
            {
                Side = CoachSide.Left,
                Prepare = () => { nav.PrepareStackBuilder(StackName); return Task.CompletedTask; },
            },

            new(Anchors.StackCanvas,
                "Each repo gets its own ports — unless you say otherwise",
                "Auto-wire gives every input a separate port, so two services never collide by accident. When you *do* want two repos to share one — the web app talking to the API's exact port — you drag one onto the other. Sharing is always a deliberate choice, never a surprise.")
            {
                Side = CoachSide.Left,
                Prepare = () => { nav.PrepareStackBuilder(StackName); return Task.CompletedTask; },
            },

            new(Anchors.StackCreate,
                "Save the wiring as a stack",
                "Rename it if you like, then Create stack to save this composition — or let me. Once it exists, any workspace can be built from it.")
            {
                Side = CoachSide.Left,
                Prepare = () => { nav.PrepareStackBuilder(StackName); return Task.CompletedTask; },
                Completed = StackCreated,
                ShowMe = () => nav.CreateStack(),
            },

            new(Anchors.Nav("Workspaces"),
                "That's a multi-repo stack",
                "Two repos, composed and wired, saved as one reusable set. The last step is to run it: create a workspace, and sprig builds an isolated, live copy of the whole stack. Leave the tour whenever you like — nothing here is yours.")
            { Side = CoachSide.Right },
        ];
    }

    /// <summary>
    /// Guide 4: turn a stack into a running, isolated workspace. Starts at <see cref="SampleStage.StackWired"/>
    /// (a stack exists, nothing running) and walks: why a workspace → create it → what sprig actually made →
    /// how you run and dispose of it. The create step waits on the user; creating a workspace is real work
    /// (a worktree per repo), so it runs behind the same progress checklist the app always uses.
    /// </summary>
    static IReadOnlyList<CoachMark> RunWorkspaceSteps(Navigator nav, AppServices services)
    {
        const string WorkspaceName = "feature-x";
        bool WorkspaceCreated() => services.Workspaces.List().Count > 0;

        return
        [
            new(Anchors.WorkspaceNew,
                "A stack is a plan; a workspace is the real thing",
                "You've got a stack, but nothing's running from it yet. A workspace is a live, isolated copy of the whole stack — its own git worktrees, its own allocated ports, its own docker infra. Let's spin one up.")
            {
                Side = CoachSide.Below,
                Prepare = () => { nav.ShowWorkspacesFresh(); return Task.CompletedTask; },
            },

            new(Anchors.WorkspaceCreate,
                "Create it",
                "The stack and a name are filled in. Click Create — sprig adds a worktree per repo on its own sprig/ branch, allocates ports just for this workspace, and writes each worktree's .env and compose with the resolved values. Or let me.")
            {
                Side = CoachSide.Left,
                Prepare = () => nav.PrepareNewWorkspace(WorkspaceName),
                Completed = WorkspaceCreated,
                ShowMe = () => nav.CreateWorkspace(),
            },

            new(Anchors.WorkspaceDetail,
                "Here's what sprig made",
                "Two worktrees on sprig/ branches, ports allocated for this workspace alone, and every ${sprig.*} value resolved into real numbers in the generated files. Your own repos never moved — this is a copy off to the side.")
            {
                Side = CoachSide.Left,
                Prepare = () => { nav.ShowFirstWorkspace(); return Task.CompletedTask; },
            },

            new(null,
                "That's the whole journey",
                "A repo declares what it needs, a stack supplies it, a workspace runs it — isolated, side by side with as many others as you like. Bring its infra up and down from here, and when you're done, delete it and everything it created is cleaned up.")
            { Prepare = () => { nav.ShowFirstWorkspace(); return Task.CompletedTask; } },
        ];
    }

    /// <summary>
    /// Guide 5 (the safety net): what happens when a workspace drifts from its record — a worktree deleted
    /// out from under sprig — and how Repair reconciles the two. Starts at <see cref="SampleStage.Running"/>,
    /// breaks a worktree in the opening step, and waits on the user to Repair. Teaches the actual behaviour:
    /// Repair prunes the stale registration (it does not resurrect deleted work), leaving a known state.
    /// </summary>
    static IReadOnlyList<CoachMark> RepairDriftSteps(Navigator nav, AppServices services)
    {
        // After the break, sample-api's worktree is MissingFolder (drift). After Repair prunes the stale
        // registration it becomes Gone — no longer drift. So "resolved" is the absence of drift, not health.
        bool DriftResolved() => services.Reconciler.Inspect(SampleSetup.WorkspaceName) is { HasDrift: false };

        return
        [
            new(Anchors.WorkspaceDetail,
                "Something went missing",
                "I've just deleted one of this workspace's worktrees behind your back — a stray cleanup, a folder gone. Your real repo is untouched, but the record now expects a worktree that isn't there. sprig checked, and flagged the mismatch: that's the drift marker.")
            {
                Side = CoachSide.Left,
                Prepare = async () =>
                {
                    nav.ShowFirstWorkspace();
                    services.Sample.BreakWorktree();
                    await nav.Reconcile();   // surface the drift so the detail shows it
                },
            },

            new(Anchors.WorkspaceRepair,
                "Let sprig reconcile it",
                "Click Repair. sprig lines the record back up with reality — here, it prunes the stale registration for the folder that's gone. Healthy worktrees are never touched, and your source repo is never involved. Or Show me.")
            {
                Side = CoachSide.Left,
                Prepare = () => { nav.ShowFirstWorkspace(); return Task.CompletedTask; },
                Completed = DriftResolved,
                ShowMe = () => nav.Repair(),
            },

            new(null,
                "Nothing here is unrecoverable",
                "That's the safety net. However a workspace gets into a half-state — a deleted worktree, an orphaned folder, a half-finished teardown — sprig can always detect the drift and bring record and reality back into line. Which is exactly why you can delete things freely.")
            { Prepare = () => { nav.ShowFirstWorkspace(); return Task.CompletedTask; } },
        ];
    }
}
