using System.Text;

namespace Sprig;

/// <summary>
/// A value that may not be known until sprig resolves the system: a literal, a reference to something another
/// part of the system produces (a port, a url, a setting), or a template mixing both. Definitions run before
/// any port is allocated, so they build these instead of strings.
/// </summary>
public abstract record Expr
{
    /// <summary>The template form the engine resolves: literal text with <c>{path}</c> placeholders, and literal
    /// braces doubled.</summary>
    public abstract string ToTemplate();

    public static implicit operator Expr(string literal) => new Literal(literal);
    public static implicit operator Expr(int literal) => new Literal(literal.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public sealed override string ToString() => ToTemplate();
}

public sealed record Literal(string Value) : Expr
{
    public override string ToTemplate() => Value.Replace("{", "{{").Replace("}", "}}");
}

/// <summary>A placeholder for a value resolved later, addressed by a dotted path such as
/// <c>catalog-api.http.port</c> or <c>setting.catalog-url</c>.</summary>
public record Ref(string Path) : Expr
{
    public override string ToTemplate() => "{" + Path + "}";
}

public sealed record Template(IReadOnlyList<Expr> Parts) : Expr
{
    public override string ToTemplate()
    {
        var sb = new StringBuilder();
        foreach (var part in Parts) sb.Append(part.ToTemplate());
        return sb.ToString();
    }
}
