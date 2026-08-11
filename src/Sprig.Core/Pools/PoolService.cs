using Sprig.Core.Stacks;
using Sprig.Core.Store;
using Sprig.Core.Workspaces;

namespace Sprig.Core.Pools;

/// <summary>Thrown when a pool operation can't proceed (pool full, workspace not in the pool, etc.).</summary>
public sealed class PoolException(string message) : Exception(message);

/// <summary>How an existing (unclaimed) workspace is handled when it's checked out again. New workspaces
/// are always materialised clean, so the mode only applies to reuse.</summary>
public enum CheckoutMode
{
    /// <summary>Resume exactly as it was left — no git change, deps and volumes kept. Just start infra.</summary>
    AsIs,
    /// <summary>Resync every repo to its base branch and wipe docker volumes (clean runtime data). Keeps
    /// installed deps.</summary>
    Fresh,
    /// <summary>Resync only the named repos to base; the rest (and the volumes) stay as they are.</summary>
    Refresh,
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

    /// <summary>The ordered checklist a <see cref="Checkout"/> will work through for a given decision,
    /// so the CLI can render every row before work starts. Step ids match what <see cref="Checkout"/>
    /// reports: a new checkout mirrors <see cref="WorkspaceService.PlanCreate"/> plus an infra row; a
    /// reused fresh/refresh mirrors <see cref="WorkspaceService.PlanRefresh"/>; an as-is is just infra.</summary>
    public IReadOnlyList<WorkspaceStep> PlanCheckout(string stackName, string? existingWorkspace,
        CheckoutMode mode, IReadOnlyList<string>? refreshRepos)
    {
        if (existingWorkspace is null)
        {
            var stack = stacks.Get(stackName) ?? throw new StackException($"unknown stack '{stackName}'");
            var resolved = resolver.Resolve(stackName, null);
            var placeholder = $"{stackName}-{NextIndex(stackName, Members(stackName), stack.MaxSlots)}";
            var steps = workspaces.PlanCreate(resolved, placeholder).ToList();
            steps.Add(new WorkspaceStep(RefreshStepIds.Infra, "Start infrastructure"));
            return steps;
        }

        var record = instances.TryLoad(existingWorkspace)
            ?? throw new PoolException($"'{existingWorkspace}' is not a workspace in the '{stackName}' pool");
        return mode switch
        {
            CheckoutMode.Fresh => workspaces.PlanRefresh(record, null, resolver.Resolve(stackName, null).Repos),
            CheckoutMode.Refresh => workspaces.PlanRefresh(record, refreshRepos, resolver.Resolve(stackName, null).Repos),
            _ => [new WorkspaceStep(RefreshStepIds.Infra, "Start infrastructure")],
        };
    }

    /// <summary>
    /// Check out a workspace from the stack's pool and mark it claimed with <paramref name="label"/>.
    /// When <paramref name="existingWorkspace"/> is named, that unclaimed workspace is reused and handled
    /// per <paramref name="mode"/> (<paramref name="refreshRepos"/> scopes <see cref="CheckoutMode.Refresh"/>);
    /// when null, a brand-new <c>&lt;stack&gt;-&lt;n&gt;</c> is materialised clean — but only if the pool has
    /// room under its <c>maxSlots</c> ceiling, else it fails (the cap doing its job). Runs under a
    /// per-stack lock so the pick/allocate is atomic.
    /// </summary>
    public InstanceRecord Checkout(string stackName, string? existingWorkspace, string label,
        CheckoutMode mode = CheckoutMode.AsIs, IReadOnlyList<string>? refreshRepos = null, bool force = false,
        IProgress<WorkspaceStepProgress>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(label))
            throw new PoolException("a checkout needs a label");
        var stack = stacks.Get(stackName) ?? throw new StackException($"unknown stack '{stackName}'");

        using var _ = Lock(stackName);
        var members = Members(stackName);

        if (existingWorkspace is not null)
        {
            var target = members.FirstOrDefault(m => string.Equals(m.Workspace, existingWorkspace, StringComparison.Ordinal))
                ?? throw new PoolException($"'{existingWorkspace}' is not a workspace in the '{stackName}' pool");
            if (target.Claimed)
                throw new PoolException($"workspace '{existingWorkspace}' is already claimed");

            // A fresh/refresh reuse resolves the stack so the refresh honours the stack's overlay (e.g.
            // stack-carried setup); as-is touches no config, so it needs no resolve.
            var resolvedRepos = mode == CheckoutMode.AsIs ? null : resolver.Resolve(stackName, null).Repos;
            ApplyHandling(target.Workspace, mode, refreshRepos, force, progress, resolvedRepos);
            return MarkClaimed(target.Workspace, label, target.WorkspaceIndex);
        }

        // Materialise a new workspace — only if there's room under the ceiling.
        if (members.Count >= stack.MaxSlots)
            throw new PoolException(
                $"pool '{stackName}' is full ({members.Count}/{stack.MaxSlots} in use) — release one first");

        var index = NextIndex(stackName, members, stack.MaxSlots);
        var name = $"{stackName}-{index}";
        var resolved = resolver.Resolve(stackName, null);
        workspaces.Create(resolved, name, progress); // fresh worktrees + env + compose + setup
        progress?.Report(new WorkspaceStepProgress(RefreshStepIds.Infra, WorkspaceStepState.Running));
        workspaces.TryStartInfra(name);
        progress?.Report(new WorkspaceStepProgress(RefreshStepIds.Infra, WorkspaceStepState.Done));
        return MarkClaimed(name, label, index);
    }

    /// <summary>
    /// Release a claimed workspace back to the pool: stop its infra (<c>docker down</c>, volumes kept) so
    /// it stops burning CPU/RAM, and flag it unclaimed. <b>Nothing is removed from disk</b> — worktrees,
    /// branches, node_modules and volumes stay, so a later <c>as-is</c> checkout resumes instantly. The
    /// label is kept as a "last used" hint. Idempotent-ish: releasing an already-free workspace just
    /// re-stamps it.
    /// </summary>
    public InstanceRecord Release(string workspace)
    {
        var record = instances.TryLoad(workspace)
            ?? throw new WorkspaceException($"unknown workspace '{workspace}'");
        if (record.Stack is not { } stackName)
            throw new PoolException($"workspace '{workspace}' isn't part of a pool");

        using var _ = Lock(stackName);
        workspaces.TryStopInfra(workspace, removeVolumes: false); // free CPU/RAM; keep volumes + disk
        var latest = instances.TryLoad(workspace) ?? record;
        var released = latest with { Claimed = false, LastUsedAt = DateTimeOffset.UtcNow };
        instances.Save(released);
        return released;
    }

    // The workspaces that make up a stack's pool: every instance tagged with the stack, ordered by index.
    IReadOnlyList<InstanceRecord> Members(string stackName)
        => instances.LoadAll()
            .Where(i => string.Equals(i.Stack, stackName, StringComparison.Ordinal))
            .OrderBy(i => i.WorkspaceIndex ?? int.MaxValue)
            .ThenBy(i => i.Workspace, StringComparer.Ordinal)
            .ToList();

    void ApplyHandling(string workspace, CheckoutMode mode, IReadOnlyList<string>? refreshRepos, bool force,
        IProgress<WorkspaceStepProgress>? progress, IReadOnlyList<ResolvedRepo>? resolvedRepos)
    {
        switch (mode)
        {
            case CheckoutMode.AsIs:
                progress?.Report(new WorkspaceStepProgress(RefreshStepIds.Infra, WorkspaceStepState.Running));
                workspaces.TryStartInfra(workspace);
                progress?.Report(new WorkspaceStepProgress(RefreshStepIds.Infra, WorkspaceStepState.Done));
                break;
            case CheckoutMode.Fresh:
                workspaces.RefreshToBase(workspace, onlyRepos: null, force, removeVolumes: true, progress, resolvedRepos);
                break;
            case CheckoutMode.Refresh:
                workspaces.RefreshToBase(workspace, refreshRepos, force, removeVolumes: false, progress, resolvedRepos);
                break;
        }
    }

    InstanceRecord MarkClaimed(string workspace, string label, int? index)
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
