using System.Globalization;

namespace Sprig.Core.Substitution;

/// <summary>
/// Builds the <see cref="IVariableSource"/> for a workspace: the <c>workspace</c> slug, each
/// allocated <c>ports.&lt;name&gt;</c>, optional stack-level <c>computed</c> variables (which may
/// reference other variables), and cross-repo <c>provides.&lt;repo&gt;.&lt;key&gt;</c> values.
/// </summary>
public static class SprigScope
{
    public static IVariableSource ForWorkspace(
        string workspace,
        IReadOnlyDictionary<string, int> ports,
        IReadOnlyDictionary<string, string>? computed = null,
        IReadOnlyDictionary<string, string>? provides = null)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["workspace"] = workspace,
        };

        foreach (var (name, port) in ports)
            values[$"ports.{name}"] = port.ToString(CultureInfo.InvariantCulture);

        // Stack-level computed vars are raw templates; the engine resolves their nested refs.
        if (computed is not null)
            foreach (var (key, raw) in computed)
                values[key] = raw;

        if (provides is not null)
            foreach (var (key, raw) in provides)
                values[$"provides.{key}"] = raw;

        return new DictionaryVariableSource(values);
    }
}
