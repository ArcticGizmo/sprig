using Sprig.Core.Store;

namespace Sprig.Core.Shared;

/// <summary>Thrown when a shared resource has no slot left for another workspace.</summary>
public sealed class SharedCapacityException(string message) : Exception(message);

/// <summary>One namespace a slot owns — a database, a vhost, a bucket — for one repo in the workspace.</summary>
public sealed record SlotNamespace(string Repo, IReadOnlyDictionary<string, string> Values)
{
    /// <summary>The value the resource's attach/detach commands revolve around, for messages and listings.</summary>
    public string Label => Values.TryGetValue("database", out var db) ? db
        : Values.Count > 0 ? Values.First().Value
        : Repo;
}

/// <summary>A workspace's hold on a shared resource: a numbered slot plus the namespaces it owns.</summary>
public sealed record SharedSlot(string Resource, int Slot, string Workspace,
    IReadOnlyList<SlotNamespace> Namespaces, DateTimeOffset AttachedAt);

/// <summary>
/// The lease ledger for shared resources: which workspace holds which slot, and what it owns there.
///
/// <para>A slot is held from <c>create</c> to <c>rm</c>, not from <c>up</c> to <c>down</c>. It owns the
/// workspace's data, so a stopped workspace keeps its database exactly as it keeps its worktree — the
/// price being that a stopped workspace still counts against capacity. That trade is deliberate: losing a
/// stopped workspace's database would be a far worse surprise than being told a pool is full.</para>
///
/// <para>Locked with the same exclusive-file pattern the port store uses, so two creates racing for the
/// last slot produce one winner and one ordinary "full" message rather than a duplicated slot.</para>
/// </summary>
public sealed class SharedLeaseStore(ISprigPaths paths)
{
    string LedgerPath => Path.Combine(paths.SharedDir, "leases.json");

    /// <summary>Take the lowest free slot on <paramref name="resource"/> for this workspace.</summary>
    /// <param name="known">
    /// Every workspace that still exists. Leases held by anything not in this set are reclaimed before
    /// declaring the resource full — a phantom slot eating capacity is the most irritating possible
    /// version of this bug, so the "full" path heals it rather than reporting it.
    /// </param>
    /// <exception cref="SharedCapacityException">Every slot is held by a workspace that still exists.</exception>
    public SharedSlot Acquire(SharedResourceDefinition resource, string workspace,
        IReadOnlyList<SlotNamespace> namespaces, IReadOnlyCollection<string> known)
    {
        using var _ = Lock();
        var data = Load();
        var leases = data.Leases.TryGetValue(resource.Name, out var existing) ? existing : [];

        // Idempotent: re-acquiring for a workspace that already holds a slot returns the one it has.
        if (leases.FirstOrDefault(l => l.Workspace == workspace) is { } held)
            return ToSlot(resource.Name, held);

        leases.RemoveAll(l => !known.Contains(l.Workspace));

        if (leases.Count >= resource.Capacity)
            throw new SharedCapacityException(Full(resource, leases));

        var taken = leases.Select(l => l.Slot).ToHashSet();
        var slot = Enumerable.Range(1, resource.Capacity).First(i => !taken.Contains(i));

        var lease = new LeaseRecord
        {
            Slot = slot,
            Workspace = workspace,
            AttachedAt = DateTimeOffset.UtcNow,
            Namespaces = [.. namespaces.Select(n => new NamespaceRecord
            {
                Repo = n.Repo,
                Values = new Dictionary<string, string>(n.Values),
            })],
        };
        leases.Add(lease);
        data.Leases[resource.Name] = leases;
        Save(data);

        return ToSlot(resource.Name, lease);
    }

    /// <summary>Release this workspace's slot; returns what it held, or null if it held nothing.</summary>
    public SharedSlot? Release(string resource, string workspace)
    {
        using var _ = Lock();
        var data = Load();
        if (!data.Leases.TryGetValue(resource, out var leases)) return null;

        var lease = leases.FirstOrDefault(l => l.Workspace == workspace);
        if (lease is null) return null;

        leases.Remove(lease);
        if (leases.Count == 0) data.Leases.Remove(resource);
        Save(data);
        return ToSlot(resource, lease);
    }

    /// <summary>Every slot held on a resource, oldest first.</summary>
    public IReadOnlyList<SharedSlot> List(string resource)
    {
        var data = Load();
        if (!data.Leases.TryGetValue(resource, out var leases)) return [];
        return [.. leases.OrderBy(l => l.AttachedAt).Select(l => ToSlot(resource, l))];
    }

    /// <summary>Every slot on every resource, for doctor and the Shared page.</summary>
    public IReadOnlyList<SharedSlot> ListAll()
        => [.. Load().Leases.SelectMany(kv => kv.Value.Select(l => ToSlot(kv.Key, l)))
                .OrderBy(s => s.Resource, StringComparer.Ordinal).ThenBy(s => s.Slot)];

    /// <summary>What this workspace holds on a resource, if anything.</summary>
    public SharedSlot? Peek(string resource, string workspace)
        => List(resource).FirstOrDefault(s => s.Workspace == workspace);

    /// <summary>Drop every lease whose workspace no longer exists; returns what was reclaimed.</summary>
    public IReadOnlyList<SharedSlot> Reclaim(IReadOnlyCollection<string> known)
    {
        using var _ = Lock();
        var data = Load();
        var dropped = new List<SharedSlot>();

        foreach (var (resource, leases) in data.Leases.ToList())
        {
            foreach (var lease in leases.Where(l => !known.Contains(l.Workspace)).ToList())
            {
                leases.Remove(lease);
                dropped.Add(ToSlot(resource, lease));
            }
            if (leases.Count == 0) data.Leases.Remove(resource);
        }

        if (dropped.Count > 0) Save(data);
        return dropped;
    }

    /// <summary>
    /// The "full" message. Holders are listed <b>oldest first</b> so the one you've forgotten about is the
    /// one you read first, and it teaches the model in a line rather than linking to it — the surprise
    /// isn't that there's a limit, it's that a stopped workspace still counts.
    /// </summary>
    static string Full(SharedResourceDefinition resource, List<LeaseRecord> leases)
    {
        var lines = leases
            .OrderBy(l => l.AttachedAt)
            .Select(l => $"  {l.Workspace,-18} {Describe(l),-24} attached {Age(l.AttachedAt)}");

        return $"""
            {resource.Name} is full — {leases.Count} of {resource.Capacity} slots attached.

            {string.Join('\n', lines)}

              A slot is a database, not a container — stopped workspaces still hold theirs.

              free one:     sprig rm {leases.OrderBy(l => l.AttachedAt).First().Workspace}
              raise it:     Shared → {resource.Name} → capacity
              skip pooling: sprig create <name> --no-shared
            """;
    }

    static string Describe(LeaseRecord lease)
        => lease.Namespaces.Count == 0 ? "-"
            : string.Join(", ", lease.Namespaces.Select(n =>
                n.Values.TryGetValue("database", out var db) ? db : n.Repo));

    static string Age(DateTimeOffset at)
    {
        var span = DateTimeOffset.UtcNow - at;
        return span.TotalDays >= 1 ? $"{(int)span.TotalDays}d ago"
            : span.TotalHours >= 1 ? $"{(int)span.TotalHours}h ago"
            : $"{Math.Max(1, (int)span.TotalMinutes)}m ago";
    }

    static SharedSlot ToSlot(string resource, LeaseRecord lease)
        => new(resource, lease.Slot, lease.Workspace,
            [.. lease.Namespaces.Select(n => new SlotNamespace(n.Repo, n.Values))],
            lease.AttachedAt);

    LedgerData Load() => JsonFile.Read<LedgerData>(LedgerPath) ?? new LedgerData();
    void Save(LedgerData data) => JsonFile.Write(LedgerPath, data);
    IDisposable Lock() => new FileLock(LedgerPath + ".lock");

    sealed class LedgerData
    {
        // resource name -> the slots held on it
        public Dictionary<string, List<LeaseRecord>> Leases { get; init; } = new();
    }

    sealed class LeaseRecord
    {
        public int Slot { get; init; }
        public string Workspace { get; init; } = "";
        public DateTimeOffset AttachedAt { get; init; }
        public List<NamespaceRecord> Namespaces { get; init; } = [];
    }

    sealed class NamespaceRecord
    {
        public string Repo { get; init; } = "";
        public Dictionary<string, string> Values { get; init; } = new();
    }

    /// <summary>A best-effort cross-process lock via an exclusively-opened lock file (as the port store does).</summary>
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
