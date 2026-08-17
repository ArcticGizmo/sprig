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
    /// Inputs are declared once at the repo level and <b>shared across every module</b>.
    /// </summary>
    public IReadOnlyList<InputDeclaration> Inputs { get; init; } = [];

    /// <summary>
    /// The repo's modules (schema 3+). Each module is a slice of the repo (e.g. a monorepo
    /// subdirectory) with its own <c>.env</c> files, compose files and setup commands, sharing the
    /// repo-level <see cref="Inputs"/>. A schema-2 file has no modules; <see cref="SprigConfigMigration"/>
    /// lifts its flat <see cref="Env"/>/<see cref="Compose"/>/<see cref="Setup"/> into a single default
    /// module on load.
    /// </summary>
    public IReadOnlyList<ModuleDeclaration> Modules { get; init; } = [];

    /// <summary>
    /// <b>Map model (schema v1).</b> Capabilities this repo offers others (its own ports + values derived
    /// from them). Top-level entries are the single-app sugar — folded into the implicit <c>app</c> module by
    /// <see cref="EffectiveModules"/>. A monorepo declares provides per <see cref="ModuleDeclaration"/> instead.
    /// Empty for a stack-era config; the stack path ignores it. See docs/graph-model-redesign.md.
    /// </summary>
    public IReadOnlyList<ProvidedCapability> Provides { get; init; } = [];

    /// <summary><b>Map model (schema v1).</b> Capabilities this repo consumes from others (single-app sugar;
    /// folded into the implicit module). See <see cref="Provides"/>.</summary>
    public IReadOnlyList<Need> Needs { get; init; } = [];

    /// <summary>
    /// <b>Legacy (schema ≤ 2), load-only.</b> Which <c>.env.*</c> files to clobber and which keys to set.
    /// A schema-2 file carries these at the top level; migration moves them into a default module and
    /// nulls them, so a normalised schema-3 config never re-serialises them. New shape:
    /// <see cref="ModuleDeclaration.Env"/>. Null (not empty) when absent, so it is omitted on write.
    /// </summary>
    public IReadOnlyList<EnvOverride>? Env { get; init; }

    /// <summary><b>Legacy (schema ≤ 2), load-only.</b> Docker compose override declarations; see <see cref="Env"/>.
    /// New shape: <see cref="ModuleDeclaration.Compose"/>.</summary>
    public IReadOnlyList<ComposeConfig>? Compose { get; init; }

    /// <summary>
    /// <b>Legacy (schema ≤ 2), load-only.</b> Free-form post-create commands; see <see cref="Env"/>.
    /// New shape: <see cref="ModuleDeclaration.Setup"/>.
    /// </summary>
    public IReadOnlyList<string>? Setup { get; init; }

    /// <summary>Captures any unrecognised top-level keys so the validator can reject them.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Unknown { get; init; } = new();

    /// <summary>
    /// The modules to materialise, unifying the two shapes a config can be in. A normalised schema-3
    /// config carries only <see cref="Modules"/> (its top-level lists are empty). A config still in the
    /// legacy flat shape — one built directly (tests, the editor's pre-modules <c>Build</c>) rather than
    /// loaded through the migrating loader — surfaces its top-level <see cref="Env"/>/<see cref="Compose"/>/<see cref="Setup"/>
    /// as one implicit root module (name <c>"app"</c>, empty path), followed by any declared modules.
    /// Every consumer iterates this, so both shapes behave identically.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<ModuleDeclaration> EffectiveModules
    {
        get
        {
            var hasFlat = Env is { Count: > 0 } || Compose is { Count: > 0 } || Setup is { Count: > 0 }
                || Provides.Count > 0 || Needs.Count > 0;
            if (!hasFlat)
                return Modules;
            var list = new List<ModuleDeclaration>(Modules.Count + 1)
            {
                new()
                {
                    Name = SprigConfigMigration.DefaultModuleName, Path = "",
                    Env = Env ?? [], Compose = Compose ?? [], Setup = Setup ?? [],
                    Provides = Provides, Needs = Needs,
                },
            };
            list.AddRange(Modules);
            return list;
        }
    }
}

/// <summary>
/// A slice of a repo (schema 3+): its own <c>.env</c> files, compose files and setup commands, plus an
/// optional <see cref="Path"/> — the subdirectory the module lives in (a monorepo slice). A module's
/// env/compose file paths resolve under <see cref="Path"/>, and its <see cref="Setup"/> runs in
/// <c>&lt;worktree&gt;/&lt;path&gt;</c>. Inputs are not per-module — they are shared at the repo level.
/// </summary>
public sealed record ModuleDeclaration
{
    /// <summary>Module name — the tab label. Unique within the repo; identifier chars only.</summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// Optional working directory for the module, relative to the repo/worktree root (e.g.
    /// <c>apps/web</c>). Empty means the repo root (a single-slice repo). Env/compose file paths are
    /// resolved under it and setup runs in it.
    /// </summary>
    public string Path { get; init; } = "";

    /// <summary><b>Map model (schema v1).</b> Capabilities this module offers (its own ports + derived values).
    /// Empty for a stack-era config.</summary>
    public IReadOnlyList<ProvidedCapability> Provides { get; init; } = [];

    /// <summary><b>Map model (schema v1).</b> Capabilities this module consumes — wired to a sibling module
    /// (local, nearest-wins) or, if none, to the outer map. Empty for a stack-era config.</summary>
    public IReadOnlyList<Need> Needs { get; init; } = [];

    /// <summary>Which <c>.env.*</c> files (relative to <see cref="Path"/>) to clobber and which keys to set.</summary>
    public IReadOnlyList<EnvOverride> Env { get; init; } = [];

    /// <summary>Docker compose override declarations (relative to <see cref="Path"/>), one per compose file.</summary>
    public IReadOnlyList<ComposeConfig> Compose { get; init; } = [];

    /// <summary>
    /// Free-form commands run in order in <c>&lt;worktree&gt;/&lt;path&gt;</c> right after the worktree is
    /// created (e.g. <c>npm ci</c>). Each runs via the platform shell; a failing command warns but does
    /// not roll back the workspace. Values are literal (no <c>${sprig.*}</c>).
    /// </summary>
    public IReadOnlyList<string> Setup { get; init; } = [];
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
    /// Optional <b>fallback</b> source file(s), relative to the module path (as with <see cref="File"/>),
    /// used to seed the worktree's copy of <see cref="File"/> only when the target file itself is absent
    /// or empty in the source repo — e.g. a committed <c>.env.template</c> standing in for a gitignored
    /// <c>.env.local</c> that lives only on a developer's machine. The target file's own <b>real values
    /// always win when present</b>; the templates apply only in their absence, concatenated in order
    /// (missing/empty skipped). Null/empty: seed from the target file alone.
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
