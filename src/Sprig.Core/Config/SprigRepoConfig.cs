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

    /// <summary>Logical repo name (used as the stack's binding key for this repo).</summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// The values this repo needs to run, referenced as <c>${sprig.&lt;name&gt;}</c> in its env/compose
    /// templates. A repo is a pure consumer: the stack supplies these per-repo (see StackDefinition).
    /// </summary>
    public IReadOnlyList<InputDeclaration> Inputs { get; init; } = [];

    /// <summary>Which <c>.env.*</c> files to clobber and which keys to set (values are <c>${sprig...}</c> templates).</summary>
    public IReadOnlyList<EnvOverride> Env { get; init; } = [];

    /// <summary>Docker compose override declarations (path-based edits only), one per compose file.</summary>
    public IReadOnlyList<ComposeConfig> Compose { get; init; } = [];

    /// <summary>Captures any unrecognised top-level keys so the validator can reject them.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Unknown { get; init; } = new();
}

/// <summary>
/// A value the repo needs from the stack, referenced as <c>${sprig.&lt;name&gt;}</c>. The
/// <see cref="Example"/> shows the shape the stack should supply (e.g. <c>5000</c> or
/// <c>http://localhost:5000</c>) so the author knows what to bind.
/// </summary>
public sealed record InputDeclaration
{
    public string Name { get; init; } = "";
    public string? Example { get; init; }
    public string? Description { get; init; }

    /// <summary>
    /// Optional restriction on which host ports the stack port feeding this input may take, as a
    /// compact spec (e.g. <c>"8100-8103"</c> or <c>"8100,8101,8200"</c>; see <c>PortSetSpec</c>).
    /// Use it when a value is only valid for a fixed set of ports — e.g. an Auth0 front end whose
    /// callback URLs are pre-registered per port. sprig traces the input's binding to its stack
    /// port and only ever allocates from this set, so the set size caps how many instances can
    /// run at once. Null/blank means unrestricted (the whole settings range).
    /// </summary>
    public string? AllowedPorts { get; init; }
}

/// <summary>An override applied to a single <c>.env.*</c> file: the keys in <see cref="Set"/> are clobbered.</summary>
public sealed record EnvOverride
{
    public string File { get; init; } = "";

    /// <summary>
    /// Optional source file(s), relative to the repo root, to seed the worktree's copy of
    /// <see cref="File"/> from before sprig's override block is injected — e.g. a committed
    /// <c>.env.template</c>. When set, these replace the default seed (the target file's own
    /// content); multiple are concatenated in order. Missing files are skipped. Null/empty means
    /// the old behaviour: seed from the target file itself if it exists.
    /// </summary>
    public IReadOnlyList<string>? Templates { get; init; }

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
