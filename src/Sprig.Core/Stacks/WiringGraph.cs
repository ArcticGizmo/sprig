namespace Sprig.Core.Stacks;

/// <summary>
/// One repo input as a pin on the wiring canvas. Carries the raw expression and the sources it
/// references so the canvas can draw its cables — and, for the D1 "one expression per input" rule,
/// show its current value inline for direct editing.
/// </summary>
public sealed record WiringPin(
    string Input,
    BindingKind Kind,
    string? Port,
    bool Shared,
    bool UndeclaredPort,
    IReadOnlyList<string> Ports,
    bool UsesWorkspace,
    bool NeedsTransform,
    string? Expression)
{
    /// <summary>Bound to exactly one declared port — the case the canvas can draw a clean cable for.</summary>
    public bool HasPort => Port is not null;

    /// <summary>Referenced sources: declared ports plus the workspace source, if any.</summary>
    public int SourceCount => Ports.Count + (UsesWorkspace ? 1 : 0);

    /// <summary>A pure literal typed inline — a value with no port or workspace source behind it.</summary>
    public bool IsLiteral => Expression is { Length: > 0 } && SourceCount == 0 && !UndeclaredPort;
}

/// <summary>A repo node: its name and the input pins it exposes.</summary>
public sealed record WiringRepoNode(string Repo, IReadOnlyList<WiringPin> Pins);

/// <summary>A port node on the central rail, and how many consumers it has.</summary>
public sealed record WiringPortNode(string Name, int ConsumerCount)
{
    public bool Shared => ConsumerCount >= 2;
    public bool Used => ConsumerCount >= 1;
}

/// <summary>
/// The built-in <c>${sprig.workspace}</c> source on the left rail, and how many inputs consume it.
/// Like a port it fans out to many inputs, but it is a fixed named string, not an allocated number.
/// </summary>
public sealed record WiringWorkspaceNode(int ConsumerCount)
{
    public bool Used => ConsumerCount >= 1;
    public bool Shared => ConsumerCount >= 2;
}

/// <summary>
/// A transform node in the centre column: the shaping step for one <c>(repo, input)</c> whose value
/// is more than a bare source pass-through — a source wrapped in text (<c>http://localhost:${…}</c>)
/// or a combination of sources. It owns the input's expression and lists the sources feeding it, so
/// it is the natural fan-in point once an input can draw from more than one port.
/// </summary>
public sealed record WiringTransformNode(
    string Repo,
    string Input,
    string Expression,
    IReadOnlyList<string> Ports,
    bool UsesWorkspace);

/// <summary>A cable: a repo input wired to a stack port, tagged for how to draw it.</summary>
public sealed record WiringEdge(string Repo, string Input, string Port, bool Transform, bool Shared);

/// <summary>
/// The wiring of a stack as a graph the canvas can lay out: repos with input pins, a rail of ports
/// (plus the workspace source), the centre-column transform nodes, and the cables between them. Pure
/// and derived entirely from the stack's repos, ports, declared inputs, and bindings — the same data
/// the list view shows — so the canvas is a second view, not a second source of truth. Positions are
/// the view's job; this is just what connects to what.
/// </summary>
public sealed record WiringGraph(
    IReadOnlyList<WiringRepoNode> Repos,
    IReadOnlyList<WiringPortNode> Ports,
    IReadOnlyList<WiringEdge> Edges,
    IReadOnlyList<WiringTransformNode> TransformNodes,
    WiringWorkspaceNode Workspace)
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
        var transformNodes = new List<WiringTransformNode>();
        var workspaceConsumers = 0;

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
                var usesWorkspace = PortExpressions.ReferencesWorkspace(expr);
                if (usesWorkspace) workspaceConsumers++;

                foreach (var port in declaredRefs)
                {
                    consumerCounts[port]++;
                    edges.Add(new WiringEdge(repo, input, port,
                        Transform: cls.Kind == BindingKind.Transform, Shared: shared.Contains(port)));
                }

                // A transform node is needed whenever the value shapes or combines its sources —
                // i.e. anything that isn't a bare single-source pass-through (identity port / raw
                // workspace) and isn't a pure inline literal.
                var needsTransform = (declaredRefs.Count > 0 || usesWorkspace)
                                     && !PortExpressions.IsBareSourceReference(expr);
                if (needsTransform)
                    transformNodes.Add(new WiringTransformNode(repo, input, expr!.Trim(), declaredRefs, usesWorkspace));

                var single = declaredRefs.Count == 1 ? declaredRefs[0] : null;
                pins.Add(new WiringPin(input, cls.Kind, single, cls.Shared, cls.ReferencesUndeclaredPort,
                    declaredRefs, usesWorkspace, needsTransform, expr));
            }

            repoNodes.Add(new WiringRepoNode(repo, pins));
        }

        var portNodes = ports.Select(p => new WiringPortNode(p, consumerCounts[p])).ToList();
        return new WiringGraph(repoNodes, portNodes, edges, transformNodes, new WiringWorkspaceNode(workspaceConsumers));
    }
}
