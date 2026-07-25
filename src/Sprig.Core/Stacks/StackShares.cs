namespace Sprig.Core.Stacks;

/// <summary>
/// Derives the explicit <see cref="SharedPort"/> list from a set of bindings: any declared stack
/// port referenced by two or more <c>(repo, input)</c> consumers. Used both to migrate older stacks
/// and to compute what the builder persists on save, so the stored <see cref="StackDefinition.Shares"/>
/// always matches the bindings that feed resolution.
/// </summary>
public static class StackShares
{
    public static IReadOnlyList<SharedPort> Derive(
        IReadOnlyList<string> repos,
        IReadOnlyList<string> ports,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> bindings)
    {
        var declared = new HashSet<string>(ports, StringComparer.Ordinal);
        var byPort = new Dictionary<string, List<PortConsumer>>(StringComparer.Ordinal);

        foreach (var repo in repos)
        {
            if (!bindings.TryGetValue(repo, out var inputs)) continue;
            foreach (var (input, expr) in inputs.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                foreach (var port in PortExpressions.ReferencedPorts(expr))
                {
                    if (!declared.Contains(port)) continue;
                    if (!byPort.TryGetValue(port, out var list))
                        byPort[port] = list = [];
                    list.Add(new PortConsumer { Repo = repo, Input = input });
                }
        }

        return byPort
            .Where(kv => kv.Value.Count >= 2)
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new SharedPort { Port = kv.Key, Consumers = kv.Value })
            .ToList();
    }
}
