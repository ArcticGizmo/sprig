using Sprig.Core.Docker;
using Sprig.Core.Store;
using Sprig.Core.Substitution;

namespace Sprig.Core.Shared;

/// <summary>
/// Runs the container behind a shared resource, and the attach/detach commands that carve a workspace's
/// namespace out of it.
///
/// <para>Two counters drive everything, and they are not the same counter. <b>Attached</b> (create → rm)
/// is what capacity limits and what owns your data. <b>Running</b> (up → down) is what starts and stops
/// the container — and it is <b>derived by asking docker</b>, not read from a record, so a crashed process
/// or a manual <c>docker compose down</c> can't leave a shared postgres running forever with a phantom
/// user.</para>
/// </summary>
public sealed class SharedResourceRunner(
    IDockerService docker,
    SharedResourceStore resources,
    SharedLeaseStore leases,
    ISprigPaths paths)
{
    /// <summary>How long attach/detach keeps retrying while a just-started service finishes coming up.</summary>
    public int ReadyTimeoutSeconds { get; init; } = 60;

    /// <summary>Sleep between attach retries; overridable so tests don't wait in real time.</summary>
    public Action<int> Delay { get; init; } = Thread.Sleep;

    public static string ProjectName(string resource) => $"sprig-shared-{resource}";

    /// <summary>The compose fragment for a resource, resolved against the shared store directory.</summary>
    public string? ComposePath(SharedResourceDefinition resource)
        => resource.Compose is { Length: > 0 } file
            ? Path.IsPathRooted(file) ? file : Path.Combine(paths.SharedDir, file)
            : null;

    /// <summary>True when this resource has containers of its own to manage.</summary>
    public bool IsManaged(SharedResourceDefinition resource)
        => ComposePath(resource) is { } path && File.Exists(path);

    /// <summary>Bring the resource up (waiting for it to be ready) if it isn't already.</summary>
    public void EnsureUp(SharedResourceDefinition resource)
    {
        if (!IsManaged(resource)) return;
        if (!docker.IsAvailable())
            throw new SharedResourceException(
                $"shared resource '{resource.Name}' needs docker — is Docker Desktop installed and running?");

        docker.Up([ComposePath(resource)!], paths.SharedDir, ProjectName(resource.Name), wait: true);
    }

    /// <summary>
    /// Stop the resource if nothing is using it any more. <paramref name="otherUsers"/> is how the caller
    /// answers "is anyone else actually running?" — <c>whenIdle: keep</c> trades idle memory for an
    /// instant next start and skips this entirely.
    /// </summary>
    /// <returns>True if the container was stopped.</returns>
    public bool StopIfIdle(SharedResourceDefinition resource, bool otherUsers)
    {
        if (!IsManaged(resource) || otherUsers) return false;
        if (resource.WhenIdle != "stop") return false;
        if (!docker.IsAvailable()) return false;

        // Volumes are always kept: this is one container with many tenants, and a lifecycle event for one
        // workspace must never destroy another's data.
        docker.Down([ComposePath(resource)!], paths.SharedDir, ProjectName(resource.Name), removeVolumes: false);
        return true;
    }

    /// <summary>True when the resource's own containers are up right now.</summary>
    public bool IsRunning(SharedResourceDefinition resource)
        => IsManaged(resource) && docker.IsAvailable()
           && docker.Ps([ComposePath(resource)!], paths.SharedDir, ProjectName(resource.Name))
                    .Any(c => c.State.Contains("running", StringComparison.OrdinalIgnoreCase)
                              || c.State.Contains("Up", StringComparison.Ordinal));

    /// <summary>Run the resource's <c>attach</c> commands for every namespace this slot owns.</summary>
    public void Attach(SharedResourceDefinition resource, SharedSlot slot)
        => RunForSlot(resource, slot, resource.Attach, "attach");

    /// <summary>
    /// Run the resource's <c>detach</c> commands. Best-effort: teardown must always run to completion, so
    /// a database that can't be dropped is reported rather than allowed to strand the workspace.
    /// </summary>
    public IReadOnlyList<string> Detach(SharedResourceDefinition resource, SharedSlot slot)
    {
        var problems = new List<string>();
        try { RunForSlot(resource, slot, resource.Detach, "detach"); }
        catch (SharedResourceException ex) { problems.Add(ex.Message); }
        return problems;
    }

    void RunForSlot(SharedResourceDefinition resource, SharedSlot slot,
        IReadOnlyList<string> commands, string phase)
    {
        if (commands.Count == 0 || !IsManaged(resource)) return;

        var service = resource.ExecService
            ?? throw new SharedResourceException(
                $"shared resource '{resource.Name}' has {phase} commands but no execService — sprig " +
                "needs to know which container to run them in.");

        EnsureUp(resource);

        foreach (var ns in slot.Namespaces)
        {
            var scope = NamespaceScope(slot, ns);
            foreach (var template in commands)
                RunWithRetry(resource, service, SubstitutionEngine.Resolve(template, scope), phase);
        }
    }

    /// <summary>
    /// Run one command, retrying while the service finishes starting. <c>--wait</c> gets us to "running or
    /// healthy", which for a database without a declared healthcheck is not the same as "accepting
    /// connections" — so the first <c>CREATE DATABASE</c> can legitimately lose a race it will win a
    /// second later. Retrying is cheaper and more robust than demanding every fragment declare a probe.
    /// </summary>
    void RunWithRetry(SharedResourceDefinition resource, string service, string command, string phase)
    {
        var deadline = Environment.TickCount64 + ReadyTimeoutSeconds * 1000L;
        var attempt = 0;
        while (true)
        {
            var (success, output) = docker.Exec([ComposePath(resource)!], paths.SharedDir,
                ProjectName(resource.Name), service, command);
            if (success) return;

            if (Environment.TickCount64 >= deadline)
                throw new SharedResourceException(
                    $"shared resource '{resource.Name}' failed to {phase} after {ReadyTimeoutSeconds}s.\n" +
                    $"  command: {command}\n" +
                    $"  output:  {output.Trim()}");

            Delay(Math.Min(2000, 200 * ++attempt));
        }
    }

    /// <summary>What an attach/detach command resolves against: this namespace's values plus the slot number.</summary>
    static IVariableSource NamespaceScope(SharedSlot slot, SlotNamespace ns)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["workspace"] = slot.Workspace,
            ["repo"] = ns.Repo,
            ["slot"] = slot.Slot.ToString(),
        };
        foreach (var (key, value) in ns.Values)
        {
            values[$"shared.{key}"] = value;
            values[$"shared.{slot.Resource}.{key}"] = value;
        }
        return new DictionaryVariableSource(values);
    }

    /// <summary>The resource a slot belongs to, or null if its definition has since been deleted.</summary>
    public SharedResourceDefinition? Definition(string name) => resources.Get(name);

    /// <summary>Every slot held on a resource other than <paramref name="workspace"/>'s.</summary>
    public IReadOnlyList<SharedSlot> OtherHolders(string resource, string workspace)
        => [.. leases.List(resource).Where(s => s.Workspace != workspace)];
}
