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
    /// <summary>The name given to the implicit module a single-app (flat) config is surfaced as by
    /// <see cref="EffectiveModules"/>.</summary>
    public const string DefaultModuleName = "app";

    /// <summary>Config schema version. Only <see cref="SprigConfigLoader.SupportedSchema"/> is understood.</summary>
    public int Schema { get; init; } = SprigConfigLoader.SupportedSchema;

    /// <summary>Logical repo name (its registry/map key).</summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// The repo's modules. Each module is a slice of the repo (e.g. a monorepo subdirectory) with its own
    /// <c>.env</c> files, compose files, setup commands and provides/needs. A single-app repo may omit
    /// <see cref="Modules"/> and use the top-level sugar instead; <see cref="EffectiveModules"/> surfaces
    /// that as one implicit <c>app</c> module.
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
    /// <b>Single-app sugar.</b> Which <c>.env.*</c> files to clobber and which keys to set, written at the
    /// top level by a single-slice repo instead of inside a module. <see cref="EffectiveModules"/> folds it
    /// into the implicit <c>app</c> module; a monorepo uses <see cref="ModuleDeclaration.Env"/> per module.
    /// Null (not empty) when absent, so it is omitted on write.
    /// </summary>
    public IReadOnlyList<EnvOverride>? Env { get; init; }

    /// <summary><b>Single-app sugar.</b> Top-level docker compose override declarations; see <see cref="Env"/>.
    /// Per-module shape: <see cref="ModuleDeclaration.Compose"/>.</summary>
    public IReadOnlyList<ComposeConfig>? Compose { get; init; }

    /// <summary>
    /// <b>Single-app sugar.</b> Top-level free-form post-create commands; see <see cref="Env"/>.
    /// Per-module shape: <see cref="ModuleDeclaration.Setup"/>.
    /// </summary>
    public IReadOnlyList<string>? Setup { get; init; }

    /// <summary>Captures any unrecognised top-level keys so the validator can reject them.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Unknown { get; init; } = new();

    /// <summary>
    /// The modules to materialise, unifying the two shapes a config can be in. A module-shaped config
    /// carries only <see cref="Modules"/>. A single-app config that uses the top-level sugar surfaces its
    /// <see cref="Env"/>/<see cref="Compose"/>/<see cref="Setup"/>/<see cref="Provides"/>/<see cref="Needs"/>
    /// as one implicit root module (name <see cref="DefaultModuleName"/>, empty path), followed by any
    /// declared modules. Every consumer iterates this, so both shapes behave identically.
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
                    Name = DefaultModuleName, Path = "",
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
/// A slice of a repo (schema 3+): its own <c>.env</c> files, compose files, setup commands and
/// provides/needs, plus an optional <see cref="Path"/> — the subdirectory the module lives in (a monorepo
/// slice). A module's env/compose file paths resolve under <see cref="Path"/>, and its <see cref="Setup"/>
/// runs in <c>&lt;worktree&gt;/&lt;path&gt;</c>.
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
