using Sprig.Core.Stacks;
using Sprig.Core.Store;
using Sprig.Core.Workspaces;

namespace Sprig.Core.Pools;

/// <summary>Thrown when a pool operation can't proceed (pool full, workspace not in the pool, etc.).</summary>
public sealed class PoolException(string message) : Exception(message);

/// <summary>How a workspace's warm state is handled when its claim branch is cut. Both modes cut the branch
/// at the same start point (default: base) and reset tracked files to it — they differ only in what happens
/// to the expensive local artifacts.</summary>
public enum CheckoutMode
{
    /// <summary>Keep the warm environment: installed deps (node_modules) and docker volumes stay as they are,
    /// no reinstall — the fast path. "Give me a clean main-based branch on top of what I already have."</summary>
    Keep,
    /// <summary>Fresh start: reinstall deps (setup) and wipe docker volumes for clean runtime data — a clean
    /// slate down to the environment.</summary>
    Fresh,
}

/// <summary>
/// The pool view + lifecycle over a stack: the emergent, bounded set of workspaces built from it. There
/// is no persisted pool object (docs/pool-model-plan.md §1a) — the pool is derived from the instance
/// store, and checkout/release mutate individual workspaces under a per-stack lock so two concurrent
/// checkouts can't grab the same workspace or exceed the cap.
/// </summary>
public sealed class PoolService(
    StackStore stacks,
    InstanceStore instances,
    StackResolver resolver,
    WorkspaceService workspaces,
    ISprigPaths paths)
{
    /// <summary>The current state of a stack's pool: its ceiling and the live workspaces in it, ordered
    /// by pool index. Throws if the stack doesn't exist.</summary>
    public PoolStatus Status(string stackName)
    {
        var stack = stacks.Get(stackName)
            ?? throw new StackException($"unknown stack '{stackName}'");
        return new PoolStatus(stack.Name, stack.MaxSlots, Members(stackName));
    }

    /// <summary>Every currently-claimed workspace, optionally scoped to one stack — the set
    /// <c>release</c> chooses from.</summary>
    public IReadOnlyList<InstanceRecord> ClaimedWorkspaces(string? stackName = null)
        => instances.LoadAll()
            .Where(i => i.Claimed && (stackName is null || string.Equals(i.Stack, stackName, StringComparison.Ordinal)))
            .OrderBy(i => i.Workspace, StringComparer.Ordinal)
            .ToList();

    /// <summary>The ordered checklist a <see cref="Checkout"/> will work through, so the CLI can render every
    /// row before work starts. Step ids match what <see cref="Checkout"/> reports: a new checkout mirrors
    /// <see cref="WorkspaceService.PlanCreate"/> plus the per-repo "cut branch" rows and an infra row; a
    /// reused checkout mirrors <see cref="WorkspaceService.PlanClaim"/>.</summary>
    public IReadOnlyList<WorkspaceStep> PlanCheckout(string stackName, string? existingWorkspace, CheckoutMode mode)
    {
        if (existingWorkspace is null)
        {
            var stack = stacks.Get(stackName) ?? throw new StackException($"unknown stack '{stackName}'");
            var resolved = resolver.Resolve(stackName, null);
            var placeholder = $"{stackName}-{NextIndex(stackName, Members(stackName), stack.MaxSlots)}";
            var steps = workspaces.PlanCreate(resolved, placeholder).ToList();
            // A new slot is created fresh at base, so its claim is minimal: just cut the branch per repo.
            foreach (var repo in resolved.Repos)
                steps.Add(new WorkspaceStep(RefreshStepIds.Claim(repo.Name), $"Create branch — {repo.Name}"));
            steps.Add(new WorkspaceStep(RefreshStepIds.Infra, "Start infrastructure"));
            return steps;
        }

        var record = instances.TryLoad(existingWorkspace)
            ?? throw new PoolException($"'{existingWorkspace}' is not a workspace in the '{stackName}' pool");
        return workspaces.PlanClaim(record, mode == CheckoutMode.Fresh, resolver.Resolve(stackName, null).Repos);
    }

    /// <summary>Fetch and gather the "start from" picker options for a checkout: the default ref (the
    /// upstream-preferring base a null start point resolves to) plus the ranked candidate refs to branch
    /// from. Inspects an existing workspace's worktrees when reusing, else the stack's source repos. Touches
    /// the network (fetch), so callers should run it off the UI thread.</summary>
    public StartPointOptions StartPointsFor(string stackName, string? existingWorkspace)
    {
        IReadOnlyList<string> paths;
        if (existingWorkspace is not null)
        {
            var rec = instances.TryLoad(existingWorkspace)
                ?? throw new PoolException($"'{existingWorkspace}' is not a workspace in the '{stackName}' pool");
            paths = rec.Repos.Select(r => r.WorktreePath).ToList();
        }
        else
        {
            paths = resolver.Resolve(stackName, null).Repos.Select(r => r.Root).ToList();
        }
        return workspaces.StartPoints(paths);
    }

    /// <summary>Check a proposed claim <paramref name="branch"/> against an existing pool workspace's repos
    /// without touching anything, so a UI/CLI can warn before committing to the checkout. For a brand-new
    /// workspace (<paramref name="existingWorkspace"/> null) there's nothing to conflict with yet — returns
    /// no conflicts.</summary>
    public ClaimConflicts CheckCheckout(string existingWorkspace, string branch)
    {
        var record = instances.TryLoad(existingWorkspace)
            ?? throw new PoolException($"unknown workspace '{existingWorkspace}'");
        return workspaces.CheckClaim(record, branch);
    }

    /// <summary>
    /// Check out a workspace from the stack's pool: cut the claim <paramref name="branch"/> across its repos
    /// and mark it claimed (with an optional recognition <paramref name="label"/>). When
    /// <paramref name="existingWorkspace"/> is named, that unclaimed slot is reused and its branch cut per
    /// <paramref name="mode"/> (keep / fresh); when null, a brand-new parked <c>&lt;stack&gt;-&lt;n&gt;</c>
    /// slot is materialised (only if the pool has room under <c>maxSlots</c>) and claimed minimally — it's
    /// already fresh at base. The branch pre-flight is atomic across every repo (see
    /// <see cref="WorkspaceService.Claim"/>); a name that already exists aborts before anything is cut. Runs
    /// under a per-stack lock so the pick/allocate is atomic.
    /// </summary>
    public InstanceRecord Checkout(string stackName, string? existingWorkspace, string branch, string? label = null,
        CheckoutMode mode = CheckoutMode.Keep, bool force = false,
        IProgress<WorkspaceStepProgress>? progress = null, string? startPoint = null)
    {
        if (string.IsNullOrWhiteSpace(branch))
            throw new PoolException("a checkout needs a branch name");
        workspaces.EnsureValidBranchName(branch); // reject a bad name before materialising anything
        var stack = stacks.Get(stackName) ?? throw new StackException($"unknown stack '{stackName}'");

        using var _ = Lock(stackName);
        var members = Members(stackName);
        var resolved = resolver.Resolve(stackName, null);

        if (existingWorkspace is not null)
        {
            var target = members.FirstOrDefault(m => string.Equals(m.Workspace, existingWorkspace, StringComparison.Ordinal))
                ?? throw new PoolException($"'{existingWorkspace}' is not a workspace in the '{stackName}' pool");
            if (target.Claimed)
                throw new PoolException($"workspace '{existingWorkspace}' is already claimed");

            workspaces.Claim(target.Workspace, branch, mode == CheckoutMode.Fresh, force, progress, resolved.Repos, startPoint);
            return MarkClaimed(target.Workspace, label, target.WorkspaceIndex);
        }

        // Materialise a new workspace — only if there's room under the ceiling.
        if (members.Count >= stack.MaxSlots)
            throw new PoolException(
                $"pool '{stackName}' is full ({members.Count}/{stack.MaxSlots} in use) — release one first");

        // Pre-flight the branch against the source repos BEFORE materialising anything, so a name conflict
        // never leaves a half-created slot behind.
        WorkspaceService.ThrowIfBlocked(
            workspaces.CheckClaimAcross(resolved.Repos.Select(r => (r.Name, r.Root)), branch), branch);

        var index = NextIndex(stackName, members, stack.MaxSlots);
        var name = $"{stackName}-{index}";
        workspaces.Create(resolved, name, progress, startPoint); // parked slot at the chosen start point, warm
        workspaces.CutBranchAndStart(name, branch, progress);    // minimal claim: cut branch at start point + start infra
        return MarkClaimed(name, label, index);
    }

    /// <summary>
    /// Release a claimed workspace back to the pool: stop its containers (<c>docker stop</c>) so it stops
    /// burning CPU/RAM, and flag it unclaimed. Release is not a teardown and <b>touches no git</b> — nothing
    /// is removed, detached, or reset: the worktree stays on its claim branch, and the containers, networks,
    /// volumes and node_modules stay (halted, not deleted), so a later checkout is fast. The branch/label are
    /// kept as "last used" hints. Returns the released record plus a <see cref="ReleaseReport"/> of any
    /// pending work (uncommitted changes / unpushed commits) — <b>surfaced, never acted on</b> — so the user
    /// knows what's at stake before a later fresh checkout resets the slot. Idempotent-ish: releasing an
    /// already-free workspace just re-stamps it.
    /// </summary>
    public (InstanceRecord Record, ReleaseReport Pending) Release(string workspace)
    {
        var record = instances.TryLoad(workspace)
            ?? throw new WorkspaceException($"unknown workspace '{workspace}'");
        if (record.Stack is not { } stackName)
            throw new PoolException($"workspace '{workspace}' isn't part of a pool");

        using var _ = Lock(stackName);
        var pending = workspaces.CollectPending(record); // report only — release never touches the working tree
        workspaces.TryStopContainers(workspace);         // free CPU/RAM; keep containers, volumes + disk intact
        var latest = instances.TryLoad(workspace) ?? record;
        var released = latest with { Claimed = false, LastUsedAt = DateTimeOffset.UtcNow };
        instances.Save(released);
        return (released, pending);
    }

    // The workspaces that make up a stack's pool: every instance tagged with the stack, ordered by index.
    IReadOnlyList<InstanceRecord> Members(string stackName)
        => instances.LoadAll()
            .Where(i => string.Equals(i.Stack, stackName, StringComparison.Ordinal))
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

    // The lowest free index in [1..maxSlots] whose <stack>-<n> name isn't taken. Guaranteed to exist
    // because the caller checked there's room under the ceiling.
    static int NextIndex(string stackName, IReadOnlyList<InstanceRecord> members, int maxSlots)
    {
        var used = new HashSet<string>(members.Select(m => m.Workspace), StringComparer.Ordinal);
        for (var n = 1; n <= maxSlots; n++)
            if (!used.Contains($"{stackName}-{n}"))
                return n;
        throw new PoolException($"pool '{stackName}' is full");
    }

    IDisposable Lock(string stackName)
        => new FileLock(Path.Combine(paths.Root, $"pool-{stackName}.lock"));

    /// <summary>A best-effort cross-process lock via an exclusively-opened lock file (same shape as the
    /// port store's), so two <c>pool checkout</c>s can't race on the same stack.</summary>
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

/// <summary>A stack's pool at a moment: the ceiling, and every workspace currently in it.</summary>
public sealed record PoolStatus(string Stack, int MaxSlots, IReadOnlyList<InstanceRecord> Workspaces)
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
    /// release. This is the cap doing its job ("no floating instances forever").</summary>
    public bool IsExhausted => FreeCount == 0 && Headroom == 0;
}
