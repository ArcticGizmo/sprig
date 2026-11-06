using System.Runtime.CompilerServices;

namespace Sprig;

/// <summary>
/// Lets <c>$"http://localhost:{api.Port}"</c> build an <see cref="Expr"/> instead of a string, so a placeholder
/// survives into the template and the compiler still checks what goes in the holes.
/// </summary>
[InterpolatedStringHandler]
public ref struct ExprHandler
{
    readonly List<Expr> _parts;

    public ExprHandler(int literalLength, int formattedCount) => _parts = new List<Expr>(formattedCount * 2 + 1);

    public void AppendLiteral(string value)
    {
        if (value.Length > 0) _parts.Add(new Literal(value));
    }

    public void AppendFormatted(Expr value) => _parts.Add(value);

    public void AppendFormatted(string? value) => AppendLiteral(value ?? "");

    // C# prefers this exact-type generic over AppendFormatted(Expr) for any Expr subtype (PortRef, Setting<T>), so
    // it has to recognise placeholders itself or they'd be escaped into literal text.
    public void AppendFormatted<T>(T value)
    {
        if (value is Expr expr) _parts.Add(expr);
        else AppendLiteral(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "");
    }

    public readonly Expr ToExpr() => _parts.Count switch
    {
        0 => new Literal(""),
        1 => _parts[0],
        _ => new Template(_parts.ToArray()),
    };
}
