using Sprig.Core.Compose;
using Sprig.Core.Docker;
using Sprig.Core.Env;
using Sprig.Core.Git;
using Sprig.Core.Init;
using Sprig.Core.Ports;
using Sprig.Core.Processes;
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
    public WorkspaceReconciler Reconciler { get; }
    public RepoRegistryStore Repos { get; }
    public StackStore Stacks { get; }
    public StackResolver StackResolver { get; }
    public InitInspector Init { get; }

    public AppServices(string? root = null)
    {
        Paths = new SprigPaths(root);
        var runner = new ProcessRunner();
        Git = new GitService(runner);
        Docker = new DockerService(runner);
        var ports = new FilePortStore(Paths);
        var instances = new InstanceStore(Paths);
        Workspaces = new WorkspaceService(Git, ports, instances, new EnvClobberService(),
            new ComposeGenerator(), Docker, Paths);
        Reconciler = new WorkspaceReconciler(Git, instances);
        Repos = new RepoRegistryStore(Paths);
        Stacks = new StackStore(Paths, Repos);
        StackResolver = new StackResolver(Repos, Stacks, Git);
        Init = new InitInspector();
    }

    /// <summary>Run a blocking Core call on a background thread (keeps the UI responsive).</summary>
    public static Task<T> RunAsync<T>(Func<T> work) => Task.Run(work);

    public static Task RunAsync(Action work) => Task.Run(work);
}
