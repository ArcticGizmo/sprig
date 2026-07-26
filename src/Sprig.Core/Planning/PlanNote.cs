namespace Sprig.Core.Planning;

/// <summary>
/// One recorded decision in a plan: which layer set what, and — when a later layer overrode an earlier
/// one — what it displaced. Notes are the answer to "why is this value what it is?", which is the whole
/// price of admission for a layered system with a machine-local layer in it.
/// </summary>
/// <param name="Layer">The layer that produced <paramref name="Value"/>.</param>
/// <param name="Target">A stable target key — see <see cref="PlanTargets"/>.</param>
/// <param name="Value">The value (or, before binding, the expression) this layer set.</param>
public sealed record PlanNote(PlanLayer Layer, string Target, string Value)
{
    /// <summary>The repo this note belongs to; null for a stack-wide note such as a port.</summary>
    public string? Repo { get; init; }

    /// <summary>What this note displaced, when a later layer overrode an earlier one.</summary>
    public string? Replaced { get; init; }

    /// <summary>Where the override came from — e.g. the shared resource's name. Null for the base layers.</summary>
    public string? Source { get; init; }

    /// <summary>The unresolved expression behind <see cref="Value"/>, when they differ.</summary>
    public string? Expression { get; init; }
}

/// <summary>
/// Builds the stable target keys used by <see cref="PlanNote.Target"/>. Keeping the format in one place
/// matters because M1's overlay ops address the same targets — a note and an override must agree on what
/// "the same thing" means, or conflict detection quietly stops working.
/// </summary>
public static class PlanTargets
{
    /// <summary>A repo input, e.g. <c>input:dbPort</c>.</summary>
    public static string Input(string name) => $"input:{name}";

    /// <summary>A key in one of the repo's env files, e.g. <c>env:.env#ConnectionStrings__Default</c>.</summary>
    public static string EnvKey(string file, string key) => $"env:{Normalize(file)}#{key}";

    /// <summary>A YAML path in one of the repo's compose files, e.g. <c>compose:docker-compose.yml#services.postgres.ports.0</c>.</summary>
    public static string ComposePath(string file, IEnumerable<string> path)
        => $"compose:{Normalize(file)}#{string.Join('.', path)}";

    /// <summary>A whole compose service, the unit suppression works in.</summary>
    public static string ComposeService(string file, string service)
        => $"compose:{Normalize(file)}#services.{service}";

    /// <summary>A stack port, e.g. <c>port:api_port</c>.</summary>
    public static string Port(string name) => $"port:{name}";

    // Compose/env files are declared repo-relative, but '\' and '/' both reach the same file on Windows.
    // Normalising here stops two spellings of one path from reading as two different targets.
    static string Normalize(string file) => file.Replace('\\', '/').TrimStart('.', '/');
}
