using System.Globalization;

namespace Sprig.Core.Substitution;

/// <summary>Small helpers for building an <see cref="IVariableSource"/> for a workspace.</summary>
public static class SprigScope
{
    /// <summary>A scope of <c>workspace</c> + named numeric <c>ports.&lt;name&gt;</c> values.</summary>
    public static IVariableSource ForWorkspace(string workspace, IReadOnlyDictionary<string, int> ports)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["workspace"] = workspace };
        foreach (var (name, port) in ports)
            values[$"ports.{name}"] = port.ToString(CultureInfo.InvariantCulture);
        return new DictionaryVariableSource(values);
    }

    /// <summary>A scope of <c>workspace</c> + arbitrary named string values.</summary>
    public static IVariableSource ForValues(string workspace, IReadOnlyDictionary<string, string> values)
    {
        var d = new Dictionary<string, string>(values, StringComparer.Ordinal) { ["workspace"] = workspace };
        return new DictionaryVariableSource(d);
    }
}
