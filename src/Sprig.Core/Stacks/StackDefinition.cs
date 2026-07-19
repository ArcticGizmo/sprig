namespace Sprig.Core.Stacks;

/// <summary>
/// A stack: a named set of repos (by registry name) plus optional stack-level computed
/// variables. Lives in the central store (<c>stacks/&lt;name&gt;.json</c>), never inside a repo;
/// exportable to a file for sharing.
/// </summary>
public sealed record StackDefinition
{
    public int Schema { get; init; } = 1;
    public required string Name { get; init; }
    public IReadOnlyList<string> Repos { get; init; } = [];

    /// <summary>Stack-level computed variables (raw <c>${sprig...}</c> templates), available to every repo.</summary>
    public IReadOnlyDictionary<string, string> Vars { get; init; } = new Dictionary<string, string>();
}
