using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sprig.Core.Config;

/// <summary>
/// The per-repo configuration, committed as <c>.sprig.json</c> inside each repo.
/// It is the ONLY file sprig ever adds to a source repo's tracked tree, and it declares
/// only that repo's own isolation surface (see docs/implementation-plan.md §2).
/// </summary>
public sealed record SprigRepoConfig
{
    /// <summary>Config schema version. Only <see cref="SprigConfigLoader.SupportedSchema"/> is understood.</summary>
    public int Schema { get; init; } = SprigConfigLoader.SupportedSchema;

    /// <summary>Logical repo name (used in stack wiring and <c>provides</c> namespacing).</summary>
    public string Name { get; init; } = "";

    /// <summary>Named ports this repo needs; each is allocated a real, non-colliding number per workspace.</summary>
    public IReadOnlyList<PortDeclaration> Ports { get; init; } = [];

    /// <summary>Which <c>.env.*</c> files to clobber and which keys to set (values are <c>${sprig...}</c> templates).</summary>
    public IReadOnlyList<EnvOverride> Env { get; init; } = [];

    /// <summary>Optional docker compose override declaration (path-based edits only).</summary>
    public ComposeConfig? Compose { get; init; }

    /// <summary>Derived values this repo publishes for other repos in the same stack to consume.</summary>
    public IReadOnlyDictionary<string, string> Provides { get; init; } = new Dictionary<string, string>();

    /// <summary>Captures any unrecognised top-level keys so the validator can reject them.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Unknown { get; init; } = new();
}

/// <summary>A named port the repo needs; referenced as <c>${sprig.ports.&lt;name&gt;}</c>.</summary>
public sealed record PortDeclaration
{
    public string Name { get; init; } = "";
    public string? Description { get; init; }
}

/// <summary>An override applied to a single <c>.env.*</c> file: the keys in <see cref="Set"/> are clobbered.</summary>
public sealed record EnvOverride
{
    public string File { get; init; } = "";
    public IReadOnlyDictionary<string, string> Set { get; init; } = new Dictionary<string, string>();
}

/// <summary>Points at the repo's compose file and the path-based value overrides to apply.</summary>
public sealed record ComposeConfig
{
    public string File { get; init; } = "";
    public IReadOnlyList<ComposeOverride> Overrides { get; init; } = [];
}

/// <summary>Replaces the value at a YAML <see cref="Path"/> with a resolved <see cref="Template"/>.</summary>
public sealed record ComposeOverride
{
    /// <summary>YAML path segments, e.g. <c>["services","postgres","ports","0"]</c>.</summary>
    public IReadOnlyList<string> Path { get; init; } = [];
    public string Template { get; init; } = "";
}
