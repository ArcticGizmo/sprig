using Sprig.Engine.Authoring;
using Sprig.Engine.Compose;
using Sprig.Engine.Definitions;
using Sprig.Engine.Git;
using Sprig.Engine.Hosting;
using Sprig.Engine.Processes;
using Sprig.Engine.Settings;
using Sprig.Engine.Workspaces;
using Sprig.Engine.Store;
using Sprig.Engine.Orchestrations;

namespace Sprig.App;

/// <summary>
/// Composition root: the engine services the UI drives. View-models hold these and never re-implement
/// engine logic; blocking calls go through <see cref="RunAsync{T}"/> so the UI thread stays free.
/// </summary>
public sealed class AppServices
{
    public ISprigPaths Paths { get; }
    public ISettingsStore Settings { get; }
    public ITrustStore Trust { get; }

    /// <summary>The trust store with the user's own edits approved, for asking whether a file needs approval.</summary>
    public TrustRules TrustRules { get; }
    public DefinitionRunner Definitions { get; }
    public OrchestrationRegistry Orchestrations { get; }
    public OrchestrationLoader Loader { get; }
    public StoredDefinitions Stored { get; }
    public OrchestrationAuthoring Authoring { get; }

    public WorkspaceService Workspaces { get; }
    public HostClient Host { get; }
    public HostStatus HostStatus { get; }
    public UiState Ui { get; }

    /// <param name="root">Store root; null means this profile's store.</param>
    public AppServices(string? root = null)
    {
        Paths = new SprigPaths(root);
        Settings = new FileSettingsStore(Paths, dev: root is null && AppProfile.IsDev);
        Trust = new FileTrustStore(Paths);
        var processes = new ProcessRunner();
        var git = new GitClient(processes);
        Definitions = new DefinitionRunner(Trust, Paths, git);
        Orchestrations = new OrchestrationRegistry(Paths);
        Stored = new StoredDefinitions(Paths);
        TrustRules = new TrustRules(Trust, Stored, git);
        Loader = new OrchestrationLoader(Definitions, Orchestrations, Stored);
        Authoring = new OrchestrationAuthoring(Stored, Orchestrations, Trust);
        Host = new HostClient(Paths);
        HostStatus = new HostStatus(Host);
        Ui = new UiState(Paths);
        Workspaces = new WorkspaceService(new WorkspaceStore(Paths), new PortLeases(Paths), Settings,
            git, new DockerCompose(processes), host: Host, loader: Loader);
    }

    public static Task<T> RunAsync<T>(Func<T> work) => Task.Run(work);

    public static Task RunAsync(Action work) => Task.Run(work);
}
