using System.Text.Json;
using System.Text.Json.Serialization;
using Sprig.Core.Config;

namespace Sprig.Core.Shared;

/// <summary>
/// A machine-local pooled docker resource — one container serving several workspaces, each with its own
/// namespace (a database, a vhost, a bucket) instead of its own port.
///
/// <para>It is <b>not</b> a producer repos or stacks reference. It is an <b>overlay</b>: it declares how it
/// injects itself into a plan, and sprig applies that as a transform after the stack has produced every
/// value. Nothing about it appears in <c>.sprig.json</c> or in a stack definition, which is what keeps one
/// person's resource optimisation off everybody else's tracked files.</para>
///
/// <para>Lives at <c>%LOCALAPPDATA%\sprig\shared\&lt;name&gt;.json</c>. Never exported by default.</para>
/// </summary>
public sealed record SharedResourceDefinition
{
    /// <summary>Config schema version. Only <see cref="SharedResourceStore.SupportedSchema"/> is understood.</summary>
    public int Schema { get; init; } = SharedResourceStore.SupportedSchema;

    /// <summary>Resource name — also the file name and the docker project suffix (<c>sprig-shared-&lt;name&gt;</c>).</summary>
    public string Name { get; init; } = "";

    /// <summary>Turn the overlay off without deleting it. Workspaces already built with it are unaffected.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>How many workspaces may be attached at once. Each attached workspace holds one slot. (M3.)</summary>
    public int Capacity { get; init; } = 5;

    /// <summary>What to do with the container when no attached workspace is running — <c>stop</c> or <c>keep</c>. (M3.)</summary>
    public string WhenIdle { get; init; } = "stop";

    /// <summary>The compose fragment that stands this resource up, relative to the shared store dir. (M3.)</summary>
    public string? Compose { get; init; }

    /// <summary>Which host port this resource may take, as a <c>PortSetSpec</c> (e.g. <c>"5432"</c>). (M3.)</summary>
    public string? AllowedPorts { get; init; }

    /// <summary>
    /// The values this resource publishes, referenced from its own injections as
    /// <c>${sprig.shared.&lt;key&gt;}</c>. Values may reference each other, plus
    /// <c>${sprig.workspace}</c> and <c>${sprig.repo}</c> — that is how one of them becomes
    /// per-workspace (<c>"database": "sprig_${sprig.workspace}"</c>).
    /// </summary>
    public IReadOnlyDictionary<string, string> Values { get; init; } = new Dictionary<string, string>();

    /// <summary>Commands run inside the container when a workspace attaches (e.g. <c>CREATE DATABASE</c>). (M3.)</summary>
    public IReadOnlyList<string> Attach { get; init; } = [];

    /// <summary>Commands run when a workspace detaches (e.g. <c>DROP DATABASE</c>). (M3.)</summary>
    public IReadOnlyList<string> Detach { get; init; } = [];

    /// <summary>How this resource rewrites a plan, one entry per repo it applies to.</summary>
    public IReadOnlyList<ResourceInjection> Injects { get; init; } = [];

    /// <summary>Captures unrecognised top-level keys so the validator can reject a typo rather than ignore it.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Unknown { get; init; } = new();
}

/// <summary>
/// What this resource changes for one repo. <see cref="Repo"/> doubles as applies-to: any stack containing
/// that repo gets the injection, so extraction is a one-time act rather than per-stack wiring.
/// </summary>
public sealed record ResourceInjection
{
    /// <summary>The repo (by registry name) this injection targets.</summary>
    public string Repo { get; init; } = "";

    /// <summary>
    /// Input name → replacement expression. <b>The preferred layer</b>: when the repo already declares an
    /// input carrying the value, override it here rather than reaching into the repo's env templates.
    /// The input must be declared — an overlay never introduces one.
    /// </summary>
    public IReadOnlyDictionary<string, string> Inputs { get; init; } = new Dictionary<string, string>();

    /// <summary>Env-key overrides, for values no declared input carries (a database name inside a connection string).</summary>
    public IReadOnlyList<InjectedEnv> Env { get; init; } = [];

    /// <summary>Compose path overrides — point a container at the shared host/port.</summary>
    public IReadOnlyList<InjectedCompose> Compose { get; init; } = [];

    /// <summary>Services this resource provides, so the repo's own copy isn't started. (Applied in M2.)</summary>
    public IReadOnlyList<InjectedSuppress> Suppress { get; init; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement> Unknown { get; init; } = new();
}

/// <summary>Overrides keys in one of the repo's <c>.env.*</c> files.</summary>
public sealed record InjectedEnv
{
    /// <summary>The env file, as the repo declares it (e.g. <c>.env</c>).</summary>
    public string File { get; init; } = "";

    /// <summary>KEY → replacement template. May reference <c>${sprig.shared.*}</c> and the repo's own inputs.</summary>
    public IReadOnlyDictionary<string, string> Set { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Allow creating the file entry or a key the repo doesn't already set. Off by default: a target that
    /// doesn't exist is nearly always a rename the overlay hasn't caught up with, and silently adding a
    /// key would turn that into a connection error three layers away (R1).
    /// </summary>
    public bool Add { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> Unknown { get; init; } = new();
}

/// <summary>Overrides YAML paths in one of the repo's compose files.</summary>
public sealed record InjectedCompose
{
    public string File { get; init; } = "";

    /// <summary>Path-based value replacements, the same shape the repo's own compose overrides use.</summary>
    public IReadOnlyList<ComposeOverride> Overrides { get; init; } = [];

    /// <summary>Allow adding an override for a path the repo doesn't already override. See <see cref="InjectedEnv.Add"/>.</summary>
    public bool Add { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> Unknown { get; init; } = new();
}

/// <summary>Services in one compose file that this resource replaces, so they aren't started per workspace.</summary>
public sealed record InjectedSuppress
{
    public string File { get; init; } = "";
    public IReadOnlyList<string> Services { get; init; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement> Unknown { get; init; } = new();
}

/// <summary>A compose service a plan will not generate, and the resource that took responsibility for it.</summary>
public sealed record ComposeSuppression(string File, string Service, string Resource);
