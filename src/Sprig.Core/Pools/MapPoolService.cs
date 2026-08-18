using Sprig.Core.Maps;
using Sprig.Core.Store;
using Sprig.Core.Workspaces;

namespace Sprig.Core.Pools;

/// <summary>
/// The pool view + lifecycle over a <b>map</b> (the Graph Turn counterpart to <see cref="PoolService"/>): the
/// emergent, bounded set of workspaces built from a map. There is no persisted pool object — the pool is
/// derived from the instance store (membership by <see cref="InstanceRecord.Map"/>), and checkout/release
/// mutate individual workspaces under a per-map lock so two concurrent checkouts can't grab the same workspace
/// or exceed the ceiling. A new checkout is <see cref="WorkspaceService.CreateFromMap"/> (parked, detached) then
/// a minimal claim; a reused checkout re-cuts the branch on an unclaimed member. Shares <see cref="CheckoutMode"/>
/// and <see cref="PoolException"/> with the stack pool.
/// </summary>
public sealed class MapPoolService(
    MapStore maps,
    InstanceStore instances,
    MapResolver resolver,
    WorkspaceService workspaces,
    ISprigPaths paths)
{
    /// <summary>The ceiling used when a map doesn't set its own <see cref="MapDefinition.MaxSlots"/> — pooling
    /// stays bounded ("no floating instances forever") even for a map that never configured a size.</summary>
    public const int DefaultMaxSlots = 5;

    /// <summary>The current state of a map's pool: its ceiling and the live workspaces in it, ordered by pool
    /// index. Throws if the map doesn't exist.</summary>
    public MapPoolStatus Status(string mapName)
    {
        var map = maps.Get(mapName) ?? throw new MapException($"unknown map '{mapName}'");
        return new MapPoolStatus(map.Name, Ceiling(map), Members(mapName));
    }

    /// <summary>Every currently-claimed workspace, optionally scoped to one map — the set <c>release</c>
    /// chooses from.</summary>
    public IReadOnlyList<InstanceRecord> ClaimedWorkspaces(string? mapName = null)
        => instances.LoadAll()
            .Where(i => i.Claimed && (mapName is null || string.Equals(i.Map, mapName, StringComparison.Ordinal)))
            .OrderBy(i => i.Workspace, StringComparer.Ordinal)
            .ToList();

    /// <summary>The ordered checklist a <see cref="Checkout"/> will work through, so the CLI can render every
    /// row before work starts. A new checkout mirrors <see cref="WorkspaceService.PlanCreateFromMap"/> plus the
    /// per-repo "cut branch" rows and an infra row; a reused checkout mirrors
    /// <see cref="WorkspaceService.PlanClaim"/>.</summary>
    public IReadOnlyList<WorkspaceStep> PlanCheckout(string mapName, string? existingWorkspace, CheckoutMode mode)
    {
        if (existingWorkspace is null)
        {
            var map = maps.Get(mapName) ?? throw new MapException($"unknown map '{mapName}'");
            var (_, repos) = resolver.Resolve(mapName, null);
            var placeholder = $"{mapName}-{NextIndex(mapName, Members(mapName), Ceiling(map))}";
            var steps = workspaces.PlanCreateFromMap(repos, placeholder).ToList();
            // A new workspace is created fresh at base, so its claim is minimal: just cut the branch per repo.
            foreach (var repo in repos)
                steps.Add(new WorkspaceStep(RefreshStepIds.Claim(repo.Name), $"Create branch — {repo.Name}"));
            steps.Add(new WorkspaceStep(RefreshStepIds.Infra, "Start infrastructure"));
            return steps;
        }

        var record = instances.TryLoad(existingWorkspace)
            ?? throw new PoolException($"'{existingWorkspace}' is not a workspace in the '{mapName}' pool");
        return workspaces.PlanClaim(record, mode == CheckoutMode.Fresh, resolver.Resolve(mapName, null).Repos);
    }

    /// <summary>Gather the "start from" picker options for a checkout: the default ref plus the ranked candidate
    /// refs to branch from. Inspects an existing workspace's worktrees when reusing, else the map's source
    /// repos. With <paramref name="fetch"/> true it fetches first (network) for freshness.</summary>
    public StartPointOptions StartPointsFor(string mapName, string? existingWorkspace, bool fetch)
    {
        IReadOnlyList<string> repoPaths;
        if (existingWorkspace is not null)
        {
            var rec = instances.TryLoad(existingWorkspace)
                ?? throw new PoolException($"'{existingWorkspace}' is not a workspace in the '{mapName}' pool");
            repoPaths = rec.Repos.Select(r => r.WorktreePath).ToList();
        }
        else
        {
            repoPaths = resolver.Resolve(mapName, null).Repos.Select(r => r.Root).ToList();
        }
        return workspaces.StartPoints(repoPaths, fetch);
    }

    /// <summary>Recent commits + current branch for the branch-graph view. Uses the first repo of the map (an
    /// existing workspace's first worktree when reusing, else the first source repo).</summary>
    public (IReadOnlyList<Git.GraphCommit> Commits, string? CurrentBranch) CommitGraphFor(
        string mapName, string? existingWorkspace, int limit)
    {
        string path;
        if (existingWorkspace is not null)
        {
            var rec = instances.TryLoad(existingWorkspace)
                ?? throw new PoolException($"'{existingWorkspace}' is not a workspace in the '{mapName}' pool");
            path = rec.Repos.Count > 0 ? rec.Repos[0].WorktreePath
                : throw new PoolException($"workspace '{existingWorkspace}' has no repos");
        }
        else
        {
            var repos = resolver.Resolve(mapName, null).Repos;
            path = repos.Count > 0 ? repos[0].Root : throw new MapException($"map '{mapName}' has no repos");
        }
        return workspaces.CommitGraphData(path, limit);
    }

    /// <summary>Check a proposed claim <paramref name="branch"/> against an existing pool workspace's repos
    /// without touching anything, so a UI/CLI can warn before committing to the checkout.</summary>
    public ClaimConflicts CheckCheckout(string existingWorkspace, string branch)
    {
        var record = instances.TryLoad(existingWorkspace)
            ?? throw new PoolException($"unknown workspace '{existingWorkspace}'");
        return workspaces.CheckClaim(record, branch);
    }

    /// <summary>
    /// Check out a workspace from the map's pool: cut the claim <paramref name="branch"/> across its repos and
    /// mark it claimed (with an optional recognition <paramref name="label"/>). When
    /// <paramref name="existingWorkspace"/> is named, that unclaimed workspace is reused and its branch cut per
    /// <paramref name="mode"/>; when null, a brand-new parked <c>&lt;map&gt;-&lt;n&gt;</c> workspace is
    /// materialised (only under the ceiling) and claimed minimally. The branch pre-flight is atomic across every
    /// repo; a name that already exists aborts before anything is cut. Runs under a per-map lock so the
    /// pick/allocate is atomic.
    /// </summary>
    public InstanceRecord Checkout(string mapName, string? existingWorkspace, string branch, string? label = null,
        CheckoutMode mode = CheckoutMode.Keep, bool force = false,
        IProgress<WorkspaceStepProgress>? progress = null, string? startPoint = null)
    {
        if (string.IsNullOrWhiteSpace(branch))
            throw new PoolException("a checkout needs a branch name");
        workspaces.EnsureValidBranchName(branch); // reject a bad name before materialising anything
        var map = maps.Get(mapName) ?? throw new MapException($"unknown map '{mapName}'");

        using var _ = Lock(mapName);
        var members = Members(mapName);
        var (mapDef, repos) = resolver.Resolve(mapName, null);

        if (existingWorkspace is not null)
        {
            var target = members.FirstOrDefault(m => string.Equals(m.Workspace, existingWorkspace, StringComparison.Ordinal))
                ?? throw new PoolException($"'{existingWorkspace}' is not a workspace in the '{mapName}' pool");
            if (target.Claimed)
                throw new PoolException($"workspace '{existingWorkspace}' is already claimed");

            workspaces.Claim(target.Workspace, branch, mode == CheckoutMode.Fresh, force, progress, repos, startPoint);
            return MarkClaimed(target.Workspace, label, target.WorkspaceIndex);
        }

        // Materialise a new workspace — only if there's room under the ceiling.
        var ceiling = Ceiling(map);
        if (members.Count >= ceiling)
            throw new PoolException(
                $"pool '{mapName}' is full ({members.Count}/{ceiling} in use) — release one first");

        // Pre-flight the branch against the source repos BEFORE materialising anything, so a name conflict
        // never leaves a half-created workspace behind.
        WorkspaceService.ThrowIfBlocked(
            workspaces.CheckClaimAcross(repos.Select(r => (r.Name, r.Root)), branch), branch);

        var index = NextIndex(mapName, members, ceiling);
        var name = $"{mapName}-{index}";
        workspaces.CreateFromMap(name, mapDef, repos, progress: progress, startPoint: startPoint); // parked, warm
        workspaces.CutBranchAndStart(name, branch, progress);   // minimal claim: cut branch at start point + start infra
        return MarkClaimed(name, label, index);
    }

    /// <summary>
    /// Release a claimed workspace back to the pool: stop its containers (<c>docker stop</c>) so it stops
    /// burning CPU/RAM, and flag it unclaimed. Release is not a teardown and <b>touches no git</b>. Returns the
    /// released record plus a <see cref="ReleaseReport"/> of any pending work — surfaced, never acted on.
    /// </summary>
    public (InstanceRecord Record, ReleaseReport Pending) Release(string workspace)
    {
        var record = instances.TryLoad(workspace)
            ?? throw new WorkspaceException($"unknown workspace '{workspace}'");
        if (record.Map is not { } mapName)
            throw new PoolException($"workspace '{workspace}' isn't part of a map pool");

        using var _ = Lock(mapName);
        var pending = workspaces.CollectPending(record); // report only — release never touches the working tree
        workspaces.TryStopContainers(workspace);         // free CPU/RAM; keep containers, volumes + disk intact
        var latest = instances.TryLoad(workspace) ?? record;
        var released = latest with { Claimed = false, LastUsedAt = DateTimeOffset.UtcNow };
        instances.Save(released);
        return (released, pending);
    }

    // A map's own ceiling, or the shared default when it never configured one.
    static int Ceiling(MapDefinition map) => map.MaxSlots ?? DefaultMaxSlots;

    // The workspaces that make up a map's pool: every instance tagged with the map, ordered by index.
    IReadOnlyList<InstanceRecord> Members(string mapName)
        => instances.LoadAll()
            .Where(i => string.Equals(i.Map, mapName, StringComparison.Ordinal))
            .OrderBy(i => i.WorkspaceIndex ?? int.MaxValue)
            .ThenBy(i => i.Workspace, StringComparer.Ordinal)
            .ToList();

    InstanceRecord MarkClaimed(string workspace, string? label, int? index)
    {
        var record = instances.TryLoad(workspace)
            ?? throw new WorkspaceException($"unknown workspace '{workspace}'");
        var claimed = record with
        {
            Claimed = true,
            Label = label,
            ClaimedAt = DateTimeOffset.UtcNow,
            WorkspaceIndex = index ?? record.WorkspaceIndex,
        };
        instances.Save(claimed);
        return claimed;
    }

    // The lowest free index in [1..ceiling] whose <map>-<n> name isn't taken. Guaranteed to exist because the
    // caller checked there's room under the ceiling.
    static int NextIndex(string mapName, IReadOnlyList<InstanceRecord> members, int ceiling)
    {
        var used = new HashSet<string>(members.Select(m => m.Workspace), StringComparer.Ordinal);
        for (var n = 1; n <= ceiling; n++)
            if (!used.Contains($"{mapName}-{n}"))
                return n;
        throw new PoolException($"pool '{mapName}' is full");
    }

    IDisposable Lock(string mapName)
        => new FileLock(Path.Combine(paths.Root, $"map-pool-{mapName}.lock"));

    /// <summary>A best-effort cross-process lock via an exclusively-opened lock file, so two checkouts can't
    /// race on the same map.</summary>
    sealed class FileLock : IDisposable
    {
        readonly FileStream _stream;

        public FileLock(string path, int timeoutMs = 10_000)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var start = Environment.TickCount64;
            while (true)
            {
                try
                {
                    _stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    return;
                }
                catch (IOException) when (Environment.TickCount64 - start < timeoutMs)
                {
                    Thread.Sleep(15);
                }
            }
        }

        public void Dispose() => _stream.Dispose();
    }
}

/// <summary>A map's pool at a moment: the ceiling, and every workspace currently in it. Map counterpart to
/// <see cref="PoolStatus"/>.</summary>
public sealed record MapPoolStatus(string Map, int MaxSlots, IReadOnlyList<InstanceRecord> Workspaces)
{
    /// <summary>Workspaces currently checked out.</summary>
    public int ClaimedCount => Workspaces.Count(w => w.Claimed);

    /// <summary>Unclaimed workspaces already materialised — free to take (reset per the checkout choice).</summary>
    public int FreeCount => Workspaces.Count(w => !w.Claimed);

    /// <summary>Workspaces whose last setup run failed — degraded, may not actually work.</summary>
    public int DegradedCount => Workspaces.Count(w => w.SetupFailed);

    /// <summary>Room to materialise a brand-new workspace under the ceiling.</summary>
    public int Headroom => Math.Max(0, MaxSlots - Workspaces.Count);

    /// <summary>No unclaimed workspace to reuse and no headroom to build one — checkout must wait for a
    /// release.</summary>
    public bool IsExhausted => FreeCount == 0 && Headroom == 0;
}
