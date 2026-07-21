namespace Sprig.Core.Substitution;

/// <summary>A simple <see cref="IVariableSource"/> backed by a dictionary of key → raw template.</summary>
public sealed class DictionaryVariableSource : IVariableSource
{
    readonly Dictionary<string, string> _values;

    public DictionaryVariableSource(IReadOnlyDictionary<string, string> values, IEqualityComparer<string>? comparer = null)
        => _values = new Dictionary<string, string>(values, comparer ?? StringComparer.Ordinal);

    public bool TryResolve(string key, out string rawValue) => _values.TryGetValue(key, out rawValue!);
}
