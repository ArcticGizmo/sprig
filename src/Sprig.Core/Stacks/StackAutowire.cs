using System.Text;
using Sprig.Core.Config;

namespace Sprig.Core.Stacks;

/// <summary>One repo in the stack and the inputs it declares — the raw material for a proposal.</summary>
public sealed record AutowireRepo(string Repo, IReadOnlyList<InputDeclaration> Inputs);

/// <summary>
/// A proposed wiring for the current builder state: the full set of ports and the per-repo binding
/// expressions. Existing ports and any binding the user already typed are preserved verbatim; only
/// unbound inputs are filled in.
/// </summary>
public sealed record AutowireProposal(
    IReadOnlyList<string> Ports,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Bindings);

/// <summary>
/// Proposes stack wiring by convention, so the mechanical <c>input → port</c> mapping doesn't have to
/// be typed by hand. For each still-unbound input it picks a port name from the input (reusing an
/// existing port when the name matches, otherwise proposing a fresh, collision-free one) and binds it
/// — raw for a plain port, or wrapped as a <c>http://localhost:${port}</c> transform when the input
/// looks like a URL.
/// <para>
/// It is deliberately conservative: it never overwrites a binding the user already entered, and it
/// never assumes two repos <i>share</i> a port. Same-named inputs in different repos (a <c>port</c> or
/// <c>dbPort</c> each service owns) get distinct ports, because silently pointing them at one port
/// would collide at runtime. Sharing stays a deliberate action.
/// </para>
/// </summary>
public static class StackAutowire
{
    public static AutowireProposal Propose(
        IReadOnlyList<AutowireRepo> repos,
        IReadOnlyList<string> existingPorts,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> existingBindings)
    {
        // Ports we can bind to, and the running list we'll return. Seed with the user's ports plus any
        // a still-standing binding already references, so the form stays self-consistent.
        var ports = new List<string>();
        var portSet = new HashSet<string>(StringComparer.Ordinal);
        void AddPort(string name)
        {
            if (name.Length > 0 && portSet.Add(name)) ports.Add(name);
        }

        foreach (var p in existingPorts) AddPort(p);
        foreach (var repo in repos)
            if (existingBindings.TryGetValue(repo.Repo, out var b))
                foreach (var expr in b.Values)
                    foreach (var referenced in PortExpressions.ReferencedPorts(expr))
                        AddPort(referenced);

        // Ports that already exist may be reused by a name match; ports we mint during this run may
        // not (that would silently share them). Snapshot the pre-existing set to tell them apart.
        var reusable = new HashSet<string>(portSet, StringComparer.Ordinal);

        var bindings = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);

        foreach (var repo in repos)
        {
            existingBindings.TryGetValue(repo.Repo, out var existing);
            var rowBindings = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var input in repo.Inputs)
            {
                // Keep whatever the user already typed.
                if (existing is not null && existing.TryGetValue(input.Name, out var typed)
                    && !string.IsNullOrWhiteSpace(typed))
                {
                    rowBindings[input.Name] = typed;
                    continue;
                }

                var port = ChoosePort(input, reusable, portSet);
                AddPort(port);
                rowBindings[input.Name] = ExpressionFor(input, port);
            }

            bindings[repo.Repo] = rowBindings;
        }

        return new AutowireProposal(ports, bindings);
    }

    /// <summary>
    /// The port to bind an input to: an existing port whose name matches, otherwise a fresh name
    /// derived from the input and disambiguated so two different inputs never collide by accident.
    /// </summary>
    static string ChoosePort(InputDeclaration input, HashSet<string> reusable, HashSet<string> takenNames)
    {
        var canonical = CanonicalPort(input);
        if (reusable.Contains(canonical)) // reuse the user's existing port of this name
            return canonical;

        // Fresh name; guard against two proposed inputs landing on the same canonical name.
        var name = canonical;
        for (var i = 2; takenNames.Contains(name); i++)
            name = $"{canonical}_{i}";
        return name;
    }

    static string ExpressionFor(InputDeclaration input, string port)
    {
        var token = $"${{sprig.ports.{port}}}";
        if (!LooksLikeUrl(input)) return token;
        var scheme = (input.Example ?? "").TrimStart().StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? "https" : "http";
        return $"{scheme}://localhost:{token}";
    }

    /// <summary>A stack-port name derived from an input: snake-cased, with a <c>_port</c> suffix, and
    /// with a URL input's <c>url</c> tail swapped for <c>port</c> (so <c>apiUrl</c> → <c>api_port</c>).</summary>
    static string CanonicalPort(InputDeclaration input)
    {
        var snake = ToSnake(input.Name);
        if (LooksLikeUrl(input))
            snake = StripTail(StripTail(snake, "_url"), "url");
        return EnsurePortSuffix(snake);
    }

    static string EnsurePortSuffix(string snake)
    {
        if (snake.Length == 0) return "port";
        // Already ends in a "port" word (however it was separated) — don't tack on another one, so
        // an input already named "frontend-port" / "frontend_port" / "port" stays as-is.
        if (snake == "port"
            || snake.EndsWith("_port", StringComparison.Ordinal)
            || snake.EndsWith("-port", StringComparison.Ordinal))
            return snake;
        return snake + "_port";
    }

    static string StripTail(string s, string tail) =>
        s.EndsWith(tail, StringComparison.Ordinal) && s.Length > tail.Length ? s[..^tail.Length] : s;

    static bool LooksLikeUrl(InputDeclaration input) =>
        input.Name.EndsWith("url", StringComparison.OrdinalIgnoreCase)
        || (input.Example ?? "").TrimStart().StartsWith("http", StringComparison.OrdinalIgnoreCase);

    /// <summary>lowerCamel / PascalCase → snake_case; existing snake or lowercase passes through.</summary>
    static string ToSnake(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var ch = name[i];
            if (char.IsUpper(ch))
            {
                if (i > 0 && name[i - 1] != '_' && !char.IsUpper(name[i - 1]))
                    sb.Append('_');
                sb.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                sb.Append(ch);
            }
        }
        return sb.ToString();
    }
}
