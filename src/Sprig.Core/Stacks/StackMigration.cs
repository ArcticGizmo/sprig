namespace Sprig.Core.Stacks;

/// <summary>
/// Brings a persisted <see cref="StackDefinition"/> up to the current schema on load. Sharing became
/// explicit in schema 2; a schema-1 file has no <see cref="StackDefinition.Shares"/>, so it is
/// back-filled from the bindings — any stack port referenced by two or more <c>(repo, input)</c>
/// consumers becomes a <see cref="SharedPort"/>. Schema-2 files are trusted as-is (their shares were
/// written explicitly by the builder and validated by the store), so migration never re-infers over
/// them. The upgrade is in-memory; it persists the next time the stack is saved.
/// </summary>
public static class StackMigration
{
    public static StackDefinition Normalize(StackDefinition def)
    {
        if (def.Schema >= 2) return def;
        return def with { Schema = 2, Shares = DeriveShares(def) };
    }

    /// <summary>
    /// The shared ports implied by the bindings: each stack port that two or more distinct
    /// <c>(repo, input)</c> bindings reference, with those consumers in a stable order.
    /// </summary>
    public static IReadOnlyList<SharedPort> DeriveShares(StackDefinition def)
    {
        var declared = new HashSet<string>(def.Ports, StringComparer.Ordinal);
        var byPort = new Dictionary<string, List<PortConsumer>>(StringComparer.Ordinal);

        foreach (var repo in def.Repos)
        {
            if (!def.Bindings.TryGetValue(repo, out var inputs)) continue;
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
