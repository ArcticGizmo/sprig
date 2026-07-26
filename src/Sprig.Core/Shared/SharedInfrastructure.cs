using Sprig.Core.Docker;
using Sprig.Core.Store;

namespace Sprig.Core.Shared;

/// <summary>
/// The three shared-resource collaborators a workspace needs, bundled so the feature arrives as one
/// optional dependency rather than three. Absent means the feature is simply off, and every plan is the
/// plan sprig would have built before it existed.
/// </summary>
public sealed class SharedInfrastructure
{
    public SharedInfrastructure(ISprigPaths paths, IDockerService docker)
    {
        Resources = new SharedResourceStore(paths);
        Leases = new SharedLeaseStore(paths);
        Runner = new SharedResourceRunner(docker, Resources, Leases, paths);
    }

    public SharedInfrastructure(SharedResourceStore resources, SharedLeaseStore leases, SharedResourceRunner runner)
    {
        Resources = resources;
        Leases = leases;
        Runner = runner;
    }

    public SharedResourceStore Resources { get; }
    public SharedLeaseStore Leases { get; }
    public SharedResourceRunner Runner { get; }
}
