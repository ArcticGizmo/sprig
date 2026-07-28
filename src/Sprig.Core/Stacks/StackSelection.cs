namespace Sprig.Core.Stacks;

/// <summary>
/// Works out what a <i>partial</i> workspace consists of: the subset of a stack's repos the user
/// kept, and which of the stack's named ports that leaves behind. A stack port exists to serve the
/// repos wired to it, so a port only referenced by deselected repos is <b>orphaned</b> — nothing in
/// the workspace can use it, and provisioning it would burn a real host port for nothing.
/// <para>
/// The rule is deliberately conservative: a port is orphaned only when at least one repo is
/// deselected <i>and</i> every binding that references it belongs to a deselected repo. A port no
/// repo references at all is kept, so a full selection always provisions exactly what it does today.
/// </para>
/// </summary>
public static class StackSelection
{
    /// <summary>
    /// Validate a chosen subset of <paramref name="stack"/>'s repos and return it in stack order
    /// (so a partial workspace materialises in the same order a full one does). <c>null</c> means
    /// "everything" — the no-deselection default; an empty-but-present selection is a mistake, not a
    /// shorthand for all, so it throws rather than quietly creating a full workspace.
    /// </summary>
    public static IReadOnlyList<string> Include(StackDefinition stack, IEnumerable<string>? selected)
    {
        if (selected is null) return stack.Repos;

        var chosen = selected.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).ToList();
        if (chosen.Count == 0)
            throw new StackException($"select at least one of stack '{stack.Name}''s repos");

        var known = new HashSet<string>(stack.Repos, StringComparer.Ordinal);
        var unknown = chosen.Where(r => !known.Contains(r)).Distinct(StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
            throw new StackException(
                $"stack '{stack.Name}' has no repo{(unknown.Count == 1 ? "" : "s")} " +
                $"{string.Join(", ", unknown.Select(r => $"'{r}'"))} " +
                $"(it has: {string.Join(", ", stack.Repos)})");

        var keep = new HashSet<string>(chosen, StringComparer.Ordinal);
        return stack.Repos.Where(keep.Contains).ToList();
    }

    /// <summary>
    /// The complement of <see cref="Include"/>: the stack repos left out of this workspace, in
    /// stack order. Empty for a full workspace.
    /// </summary>
    public static IReadOnlyList<string> Exclude(StackDefinition stack, IEnumerable<string>? selected)
    {
        var keep = new HashSet<string>(Include(stack, selected), StringComparer.Ordinal);
        return stack.Repos.Where(r => !keep.Contains(r)).ToList();
    }

    /// <summary>
    /// The stack ports orphaned by keeping only <paramref name="includedRepos"/> — declared by the
    /// stack, but referenced solely by the bindings of repos this workspace leaves out. These are
    /// the ports a partial workspace must <i>not</i> provision. In stack-declaration order.
    /// </summary>
    public static IReadOnlyList<string> OrphanedPorts(StackDefinition stack, IReadOnlyCollection<string> includedRepos)
        => OrphanedPorts(stack.Repos, stack.Ports, stack.Bindings, includedRepos);

    /// <inheritdoc cref="OrphanedPorts(StackDefinition, IReadOnlyCollection{string})"/>
    public static IReadOnlyList<string> OrphanedPorts(
        IReadOnlyList<string> stackRepos,
        IReadOnlyList<string> stackPorts,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> bindings,
        IReadOnlyCollection<string> includedRepos)
    {
        var keep = new HashSet<string>(includedRepos, StringComparer.Ordinal);
        // A full selection changes nothing — never drop a port the stack owns outright.
        if (stackRepos.All(keep.Contains)) return [];

        var wanted = ReferencedPorts(bindings, stackRepos.Where(keep.Contains));
        var dropped = ReferencedPorts(bindings, stackRepos.Where(r => !keep.Contains(r)));

        return stackPorts.Where(p => dropped.Contains(p) && !wanted.Contains(p)).ToList();
    }

    /// <summary>The ports a partial workspace still provisions: the stack's ports minus the orphans.</summary>
    public static IReadOnlyList<string> ProvisionedPorts(StackDefinition stack, IReadOnlyCollection<string> includedRepos)
    {
        var orphaned = new HashSet<string>(OrphanedPorts(stack, includedRepos), StringComparer.Ordinal);
        return orphaned.Count == 0 ? stack.Ports : stack.Ports.Where(p => !orphaned.Contains(p)).ToList();
    }

    /// <summary>Every stack port referenced by any binding of the given repos.</summary>
    static HashSet<string> ReferencedPorts(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> bindings,
        IEnumerable<string> repos)
    {
        var ports = new HashSet<string>(StringComparer.Ordinal);
        foreach (var repo in repos)
        {
            if (!bindings.TryGetValue(repo, out var repoBindings)) continue;
            foreach (var expr in repoBindings.Values)
                foreach (var port in PortExpressions.ReferencedPorts(expr))
                    ports.Add(port);
        }
        return ports;
    }
}
