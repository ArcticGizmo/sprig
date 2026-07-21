namespace Sprig.Core.Stacks;

/// <summary>
/// A stack: the repos it composes, the named ports it owns, and — per repo — how each of that
/// repo's declared inputs is filled. The stack is the single source of every value; repos never
/// produce values. Lives in the central store (<c>stacks/&lt;name&gt;.json</c>), never inside a repo.
/// </summary>
public sealed record StackDefinition
{
    public int Schema { get; init; } = 1;
    public required string Name { get; init; }

    /// <summary>Repos in the stack, by registry name.</summary>
    public IReadOnlyList<string> Repos { get; init; } = [];

    /// <summary>Named ports the stack owns; each is allocated a real, non-colliding number per workspace.</summary>
    public IReadOnlyList<string> Ports { get; init; } = [];

    /// <summary>
    /// Per-repo input bindings: <c>Bindings[repo][input]</c> is an expression (a literal or a
    /// template over <c>${sprig.ports.&lt;name&gt;}</c> / <c>${sprig.workspace}</c>) that supplies
    /// that repo's input. Same-named inputs in different repos are bound independently.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Bindings { get; init; }
        = new Dictionary<string, IReadOnlyDictionary<string, string>>();
}
