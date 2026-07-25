namespace Sprig.Core.Stacks;

/// <summary>One repo input as a pin on the wiring canvas.</summary>
public sealed record WiringPin(string Input, BindingKind Kind, string? Port, bool Shared, bool UndeclaredPort)
{
    /// <summary>Bound to exactly one declared port — the case the canvas can draw a clean cable for.</summary>
    public bool HasPort => Port is not null;
}

/// <summary>A repo node: its name and the input pins it exposes.</summary>
public sealed record WiringRepoNode(string Repo, IReadOnlyList<WiringPin> Pins);

/// <summary>A port node on the central rail, and how many consumers it has.</summary>
public sealed record WiringPortNode(string Name, int ConsumerCount)
{
    public bool Shared => ConsumerCount >= 2;
    public bool Used => ConsumerCount >= 1;
}

/// <summary>A cable: a repo input wired to a stack port, tagged for how to draw it.</summary>
public sealed record WiringEdge(string Repo, string Input, string Port, bool Transform, bool Shared);

/// <summary>
/// The wiring of a stack as a graph the canvas can lay out: repos with input pins, a rail of ports,
/// and the cables between them. Pure and derived entirely from the stack's repos, ports, declared
/// inputs, and bindings — the same data the list view shows — so the canvas is a second view, not a
/// second source of truth. Positions are the view's job; this is just what connects to what.
/// </summary>
public sealed record WiringGraph(
    IReadOnlyList<WiringRepoNode> Repos,
    IReadOnlyList<WiringPortNode> Ports,
    IReadOnlyList<WiringEdge> Edges)
{
    public static WiringGraph Build(
        IReadOnlyList<string> repos,
        IReadOnlyList<string> ports,
        IReadOnlyDictionary<string, IReadOnlyList<string>> repoInputs,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> bindings)
    {
        var declared = new HashSet<string>(ports, StringComparer.Ordinal);
        var shared = BindingClassifier.SharedPorts(bindings, declared);

        var consumerCounts = ports.ToDictionary(p => p, _ => 0, StringComparer.Ordinal);
        var repoNodes = new List<WiringRepoNode>();
        var edges = new List<WiringEdge>();

        foreach (var repo in repos)
        {
            bindings.TryGetValue(repo, out var repoBindings);
            var inputs = repoInputs.TryGetValue(repo, out var names) ? names : [];
            var pins = new List<WiringPin>();

            foreach (var input in inputs)
            {
                var expr = repoBindings is not null && repoBindings.TryGetValue(input, out var e) ? e : null;
                var cls = BindingClassifier.Classify(expr, declared, shared);
                var referenced = PortExpressions.ReferencedPorts(expr);
                var declaredRefs = referenced.Where(declared.Contains).ToList();

                foreach (var port in declaredRefs)
                {
                    consumerCounts[port]++;
                    edges.Add(new WiringEdge(repo, input, port,
                        Transform: cls.Kind == BindingKind.Transform, Shared: shared.Contains(port)));
                }

                var single = declaredRefs.Count == 1 ? declaredRefs[0] : null;
                pins.Add(new WiringPin(input, cls.Kind, single, cls.Shared, cls.ReferencesUndeclaredPort));
            }

            repoNodes.Add(new WiringRepoNode(repo, pins));
        }

        var portNodes = ports.Select(p => new WiringPortNode(p, consumerCounts[p])).ToList();
        return new WiringGraph(repoNodes, portNodes, edges);
    }
}
