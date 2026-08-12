using Sprig.Core.Compose;
using Sprig.Core.Docker;
using Sprig.Core.Env;
using Sprig.Core.Git;
using Sprig.Core.Init;
using Sprig.Core.Pools;
using Sprig.Core.Ports;
using Sprig.Core.Processes;
using Sprig.Core.Settings;
using Sprig.Core.Stacks;
using Sprig.Core.Store;
using Sprig.Core.Workspaces;

namespace Sprig.App;

/// <summary>
/// Composition root: wires the real <c>Sprig.Core</c> graph the UI drives. The UI holds only these
/// services and never re-implements logic. All calls are synchronous/blocking (git/docker shell-outs),
/// so ViewModels run them via <see cref="RunAsync"/> off the UI thread.
/// </summary>
public sealed class AppServices
{
    public ISprigPaths Paths { get; }
    public IGitService Git { get; }
    public IDockerService Docker { get; }
    public WorkspaceService Workspaces { get; }

    /// <summary>The pool lifecycle over the stack's <c>MaxSlots</c> ceiling: checkout / release / status.
    /// A thin query+allocation layer over the same <c>InstanceStore</c> the workspace list reads.</summary>
    public PoolService Pools { get; }
    public WorkspaceReconciler Reconciler { get; }
    public RepoRegistryStore Repos { get; }
    public StackStore Stacks { get; }
    public StackResolver StackResolver { get; }
    public InitInspector Init { get; }
    public ISettingsStore Settings { get; }
    public IPortStore Ports { get; }

    /// <summary>Builds (and removes) the guided tour's throwaway sample setup in <b>this</b> store.</summary>
    public Core.Demo.SampleSetup Sample { get; }

    /// <summary>
    /// True when this graph is serving the guided tour's throwaway sample rather than the user's real
    /// store. Read by <c>MainWindowViewModel</c> to show the tour banner — nothing else should branch
    /// on it (see docs/guided-tour-plan.md §7).
    /// </summary>
    public bool IsDemoStore { get; }

    /// <param name="root">Store root; null means this profile's real store.</param>
    /// <param name="isDemoStore">Declares this graph as the tour's sample. Stated by the caller rather
    /// than guessed from <paramref name="root"/>, so a test or a headless render can stand up a tour
    /// session in a temp directory.</param>
    public AppServices(string? root = null, bool isDemoStore = false)
    {
        Paths = new SprigPaths(root);
        IsDemoStore = isDemoStore;
        var runner = new ProcessRunner();
        Git = new GitService(runner);
        Docker = new DockerService(runner);
        Settings = new FileSettingsStore(Paths);
        var ports = new FilePortStore(Paths, Settings);
        Ports = ports;
        var instances = new InstanceStore(Paths);
        Workspaces = new WorkspaceService(Git, ports, instances, new EnvClobberService(),
            new ComposeGenerator(), Docker, Paths, new Core.Setup.SetupRunner(runner));
        Reconciler = new WorkspaceReconciler(Git, instances);
        Repos = new RepoRegistryStore(Paths);
        Stacks = new StackStore(Paths, Repos, instances);
        StackResolver = new StackResolver(Repos, Stacks, Git);
        Pools = new PoolService(Stacks, instances, StackResolver, Workspaces, Paths);
        Init = new InitInspector(Git);
        Sample = new Core.Demo.SampleSetup(Paths, runner, Repos, Stacks, StackResolver, Workspaces);
    }

    /// <summary>
    /// Raised after any repo/stack/workspace mutation, so state-driven surfaces (Home's rail, the
    /// setup guide) can refresh without polling. A lightweight in-process signal, not a store watch.
    /// </summary>
    public event Action? StoreChanged;

    /// <summary>Announce that the repo/stack/workspace stores changed.</summary>
    public void NotifyStoreChanged() => StoreChanged?.Invoke();

    /// <summary>Run a blocking Core call on a background thread (keeps the UI responsive).</summary>
    public static Task<T> RunAsync<T>(Func<T> work) => Task.Run(work);

    public static Task RunAsync(Action work) => Task.Run(work);
}
