namespace Sprig.Core.Stacks;

/// <summary>
/// A stack: the repos it composes, the named ports it owns, and — per repo — how each of that
/// repo's declared inputs is filled. The stack is the single source of every value; repos never
/// produce values. Lives in the central store (<c>stacks/&lt;name&gt;.json</c>), never inside a repo.
/// </summary>
public sealed record StackDefinition
{
    /// <summary>
    /// Schema version. <c>2</c> added the explicit <see cref="Shares"/> list; a schema-1 file is
    /// migrated on load (its shares are back-filled from the bindings — see <c>StackMigration</c>).
    /// </summary>
    public int Schema { get; init; } = 2;
    public required string Name { get; init; }

    /// <summary>
    /// The most workspaces the stack's pool may hold at once — the ceiling that keeps a pool bounded
    /// ("no floating instances forever"). Concurrent-environment capacity is really a machine limit
    /// (RAM/ports/disk), so this is a sensible default the user can raise, not an intrinsic property of
    /// the repos. A file with no value keeps <see cref="DefaultMaxSlots"/>.
    /// </summary>
    public int MaxSlots { get; init; } = DefaultMaxSlots;

    public const int DefaultMaxSlots = 3;

    /// <summary>Repos in the stack, by registry name.</summary>
    public IReadOnlyList<string> Repos { get; init; } = [];

    /// <summary>Named ports the stack owns; each is allocated a real, non-colliding number per workspace.</summary>
    public IReadOnlyList<string> Ports { get; init; } = [];

    /// <summary>
    /// Optional per-repo setup commands the <b>stack</b> supplies, keyed by repo name. Folded in at
    /// resolution as an extra setup module that runs after the repo's own, so a repo with a thin (or
    /// name-only) <c>.sprig.json</c> can still be stood up entirely from the stack — the stack as a
    /// complete, self-contained block. Literal commands (no <c>${sprig.*}</c>), same as repo setup.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Setup { get; init; }
        = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>
    /// Per-repo input bindings: <c>Bindings[repo][input]</c> is an expression (a literal or a
    /// template over <c>${sprig.ports.&lt;name&gt;}</c> / <c>${sprig.workspace}</c>) that supplies
    /// that repo's input. Same-named inputs in different repos are bound independently. This stays
    /// the single source of truth for resolution (<c>StackWiring</c> reads only this).
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Bindings { get; init; }
        = new Dictionary<string, IReadOnlyDictionary<string, string>>();

    /// <summary>
    /// The stack ports two or more repos deliberately share, made explicit (schema 2). Each entry
    /// records a stack port and the <c>(repo, input)</c> consumers wired to it. It is an explicit,
    /// validated overlay on <see cref="Bindings"/> — it never feeds resolution, but it drives the
    /// builder's shared-port UI, port-rename propagation, and the wiring canvas. Kept honest by the
    /// store: every consumer's binding must reference <c>${sprig.ports.&lt;Port&gt;}</c>.
    /// </summary>
    public IReadOnlyList<SharedPort> Shares { get; init; } = [];

    /// <summary>
    /// Which repo "owns" (produces / serves on) each stack port, when the author has said so. Purely a
    /// <b>visualization overlay</b>, in the same spirit as <see cref="Shares"/>: it never feeds
    /// resolution and never changes what a value resolves to — repos stay pure consumers (see
    /// docs/pool-model-plan.md §4a). Its only job is to let the repo-graph view draw a directed
    /// <c>owner → consumer</c> dependency line for a port instead of a fan-out cable. Optional and
    /// additive: an absent list just means no ownership has been assigned yet (no migration needed).
    /// Kept honest by the store: each entry's port is declared and its repo is in the stack, and a
    /// port is owned at most once.
    /// </summary>
    public IReadOnlyList<PortOwner> Owners { get; init; } = [];
}

/// <summary>A stack port shared by more than one repo, and the consumers wired to it.</summary>
public sealed record SharedPort
{
    /// <summary>The name of the shared stack port (one of <see cref="StackDefinition.Ports"/>).</summary>
    public required string Port { get; init; }

    /// <summary>The <c>(repo, input)</c> pairs whose bindings reference this port.</summary>
    public IReadOnlyList<PortConsumer> Consumers { get; init; } = [];
}

/// <summary>One repo input that consumes a shared port.</summary>
public sealed record PortConsumer
{
    public required string Repo { get; init; }
    public required string Input { get; init; }
}

/// <summary>
/// Records that a stack port is produced by (served on by) a particular repo — a visualization-only
/// overlay that lets the repo-graph view draw a directed owner→consumer dependency line. See
/// <see cref="StackDefinition.Owners"/>; it never participates in resolution.
/// </summary>
public sealed record PortOwner
{
    /// <summary>The owned stack port (one of <see cref="StackDefinition.Ports"/>).</summary>
    public required string Port { get; init; }

    /// <summary>The repo that owns/produces the port (one of <see cref="StackDefinition.Repos"/>).</summary>
    public required string Repo { get; init; }
}
