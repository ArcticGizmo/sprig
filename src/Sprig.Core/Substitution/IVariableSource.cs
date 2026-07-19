namespace Sprig.Core.Substitution;

/// <summary>
/// Supplies the raw value for a substitution key — the dotted path *after* the <c>sprig.</c>
/// prefix, e.g. <c>workspace</c>, <c>ports.api</c>, <c>provides.dotnet-api.baseUrl</c>.
/// The returned value is "raw": it may itself contain further <c>${sprig...}</c> references,
/// which the engine resolves recursively.
/// </summary>
public interface IVariableSource
{
    bool TryResolve(string key, out string rawValue);
}
