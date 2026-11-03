using Sprig.Engine.Compose;
using Sprig.Engine.Definitions;
using Sprig.Engine.Git;
using Sprig.Engine.Hosting;
using Sprig.Engine.Processes;
using Sprig.Engine.Settings;
using Sprig.Engine.Sprouts;
using Sprig.Engine.Store;
using Sprig.Engine.Trellises;

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
    public DefinitionRunner Definitions { get; }
    public TrellisRegistry Trellises { get; }
    public TrellisLoader Loader { get; }
    public SproutService Sprouts { get; }
    public HostClient Host { get; }

    /// <param name="root">Store root; null means this profile's store.</param>
    public AppServices(string? root = null)
    {
        Paths = new SprigPaths(root);
        Settings = new FileSettingsStore(Paths);
        Trust = new FileTrustStore(Paths);
        Definitions = new DefinitionRunner(Trust, new DotnetRunHost(), Paths);
        Trellises = new TrellisRegistry(Paths);
        Loader = new TrellisLoader(Definitions);
        var processes = new ProcessRunner();
        Host = new HostClient(Paths);
        Sprouts = new SproutService(new SproutStore(Paths), new PortLeases(Paths), Settings,
            new GitClient(processes), new DockerCompose(processes), host: Host);
    }

    public static Task<T> RunAsync<T>(Func<T> work) => Task.Run(work);

    public static Task RunAsync(Action work) => Task.Run(work);
}
