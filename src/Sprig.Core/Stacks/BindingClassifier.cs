namespace Sprig.Core.Stacks;

/// <summary>What kind of binding an expression is, for deciding what deserves the author's attention.</summary>
public enum BindingKind
{
    /// <summary>No expression yet — must be filled before the stack resolves.</summary>
    Unbound,
    /// <summary>Exactly one port token and nothing else (<c>${sprig.ports.x}</c>) — a plain mechanical mapping.</summary>
    Identity,
    /// <summary>A port token wrapped in other text (<c>http://localhost:${sprig.ports.x}</c>) — a transform.</summary>
    Transform,
    /// <summary>A constant with no port token (<c>http://localhost:4000</c>).</summary>
    Literal,
}

/// <summary>How one binding row is classified, and whether it can be folded out of the way.</summary>
public sealed record BindingClass(BindingKind Kind, bool Shared, bool ReferencesUndeclaredPort)
{
    /// <summary>A plain identity mapping to its own declared port — the mechanical 80% that can collapse.</summary>
    public bool IsCollapsible => Kind == BindingKind.Identity && !Shared && !ReferencesUndeclaredPort;

    /// <summary>Anything that warrants a look: unbound, a transform, a shared port, or a bare literal.</summary>
    public bool IsException => !IsCollapsible;
}

/// <summary>
/// Classifies stack bindings so the builder can collapse the mechanical mappings and surface the
/// decisions — transforms, shared ports, literals, and anything still unbound. Pure and stateless:
/// it reads only the expressions, the declared ports, and (for sharing) how many consumers each port
/// has. The builder re-runs it whenever a binding or port changes.
/// </summary>
public static class BindingClassifier
{
    /// <summary>Classify a single expression against the declared ports and the set of shared ports.</summary>
    public static BindingClass Classify(string? expression, ISet<string> declaredPorts, ISet<string> sharedPorts)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return new BindingClass(BindingKind.Unbound, Shared: false, ReferencesUndeclaredPort: false);

        var expr = expression.Trim();
        var ports = PortExpressions.ReferencedPorts(expr);

        var undeclared = ports.Any(p => !declaredPorts.Contains(p));
        var shared = ports.Any(sharedPorts.Contains);

        var kind = ports.Count switch
        {
            0 => BindingKind.Literal,
            1 when expr == $"${{sprig.ports.{ports[0]}}}" => BindingKind.Identity,
            _ => BindingKind.Transform,
        };

        return new BindingClass(kind, shared, undeclared);
    }

    /// <summary>
    /// Classify every binding across the builder at once, first working out which declared ports are
    /// shared (referenced by two or more <c>(repo, input)</c> consumers) so each row knows its context.
    /// </summary>
    public static IReadOnlyDictionary<(string Repo, string Input), BindingClass> ClassifyAll(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> bindings,
        IReadOnlyCollection<string> declaredPorts)
    {
        var declared = new HashSet<string>(declaredPorts, StringComparer.Ordinal);
        var shared = SharedPorts(bindings, declared);

        var result = new Dictionary<(string, string), BindingClass>();
        foreach (var (repo, inputs) in bindings)
            foreach (var (input, expr) in inputs)
                result[(repo, input)] = Classify(expr, declared, shared);
        return result;
    }

    /// <summary>Declared ports referenced by two or more distinct <c>(repo, input)</c> consumers.</summary>
    public static ISet<string> SharedPorts(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> bindings,
        ISet<string> declaredPorts)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (_, inputs) in bindings)
            foreach (var (_, expr) in inputs)
                foreach (var port in PortExpressions.ReferencedPorts(expr))
                    if (declaredPorts.Contains(port))
                        counts[port] = counts.GetValueOrDefault(port) + 1;

        return counts.Where(kv => kv.Value >= 2).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
    }
}
