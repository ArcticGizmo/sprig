using System.Globalization;
using Sprig.Core.Substitution;
using Sprig.Core.Workspaces;

namespace Sprig.Core.Stacks;

/// <summary>Thrown when a repo's declared input isn't bound by the stack.</summary>
public sealed class StackWiringException(string message) : Exception(message);

/// <summary>The resolved wiring for a workspace: allocated ports + each repo's resolved input values.</summary>
public sealed class WiredStack
{
    readonly Dictionary<string, IVariableSource> _scopes;

    public WiredStack(
        IReadOnlyDictionary<string, int> ports,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> inputs,
        Dictionary<string, IVariableSource> scopes)
    {
        Ports = ports;
        Inputs = inputs;
        _scopes = scopes;
    }

    /// <summary>Stack port name → allocated number.</summary>
    public IReadOnlyDictionary<string, int> Ports { get; }
    /// <summary>Repo name → (input name → resolved value).</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Inputs { get; }
    /// <summary>The scope a repo's env/compose templates resolve against.</summary>
    public IVariableSource ScopeFor(string repo) => _scopes[repo];
}

/// <summary>
/// Resolves a stack's per-repo bindings into per-repo input scopes. Data flows one way: the
/// stack's allocated ports feed the binding expressions, which fill each repo's declared inputs.
/// Every declared input must be bound or this throws (hard-fail, no partial resolution).
/// </summary>
public static class StackWiring
{
    public static WiredStack Resolve(
        string workspace,
        IReadOnlyDictionary<string, int> allocatedPorts,
        IReadOnlyList<ResolvedRepo> repos,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> bindings)
    {
        // The scope binding expressions resolve against: workspace + the stack's allocated ports.
        var portValues = new Dictionary<string, string>(StringComparer.Ordinal) { ["workspace"] = workspace };
        foreach (var (name, port) in allocatedPorts)
            portValues[$"ports.{name}"] = port.ToString(CultureInfo.InvariantCulture);
        var portScope = new DictionaryVariableSource(portValues);

        var scopes = new Dictionary<string, IVariableSource>(StringComparer.Ordinal);
        var resolvedInputs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);

        foreach (var repo in repos)
        {
            bindings.TryGetValue(repo.Name, out var repoBindings);
            var inputValues = new Dictionary<string, string>(StringComparer.Ordinal) { ["workspace"] = workspace };
            var display = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var input in repo.Config.Inputs)
            {
                if (repoBindings is null || !repoBindings.TryGetValue(input.Name, out var expr) || string.IsNullOrWhiteSpace(expr))
                    throw new StackWiringException(
                        $"repo '{repo.Name}' needs input '{input.Name}'" +
                        (input.Example is { } ex ? $" (e.g. {ex})" : "") +
                        " but the stack doesn't supply it");

                var value = SubstitutionEngine.Resolve(expr, portScope);
                inputValues[input.Name] = value;
                display[input.Name] = value;
            }

            scopes[repo.Name] = new DictionaryVariableSource(inputValues);
            resolvedInputs[repo.Name] = display;
        }

        return new WiredStack(allocatedPorts, resolvedInputs, scopes);
    }
}
