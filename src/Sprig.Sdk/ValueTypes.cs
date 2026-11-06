namespace Sprig;

/// <summary>The kind of value an import or export carries. Checked when the engine joins repos, so a repo that
/// imports a <see cref="Url"/> can't be fed a connection string.</summary>
public abstract class ValueType
{
    private protected ValueType() { }

    internal static string NameOf<T>() where T : ValueType => typeof(T).Name.ToLowerInvariant();
}

public sealed class Url : ValueType;
public sealed class ConnectionString : ValueType;
public sealed class HostPort : ValueType;
public sealed class Secret : ValueType;
public sealed class Text : ValueType;

/// <summary>A value this repo asks the system for. The system binds it to an export of the same name.</summary>
public sealed record Import<T>(string Name) : Ref("import." + Name) where T : ValueType
{
    internal Action<Expr>? SetOrElse { get; init; }

    /// <summary>
    /// What to use when nothing else fills this import: no other repo exports it and no trellis binds it, as when the
    /// repo runs on its own. Usually something of the repo's own, such as <c>repo.Mailpit("mail").SmtpUrl</c>; infra
    /// declared only for this is left out of a sprout where something else fills the import.
    /// </summary>
    public Import<T> OrElse(Expr value)
    {
        (SetOrElse ?? throw new InvalidOperationException($"'{Name}' wasn't made by Repo.Import, so it can't have a fallback"))(value);
        return this;
    }

    /// <inheritdoc cref="OrElse(Expr)"/>
    public Import<T> OrElse([System.Runtime.CompilerServices.InterpolatedStringHandlerArgument] ExprHandler value) => OrElse(value.ToExpr());
}

/// <summary>A port sprig allocates for an app's endpoint.</summary>
public sealed record PortRef(string App, string Endpoint) : Ref($"{App}.{Endpoint}.port");
