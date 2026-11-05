using System.Text.Json.Serialization;

namespace Sprig.Model;

// The data a definition emits. The engine reads this, never the C#, so its shape is the contract between them.

/// <summary>Source-generated because file-based apps default to Native AOT settings, which switch reflection-based
/// JSON off. The engine reads the model with the same context.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    AllowOutOfOrderMetadataProperties = true)]
[JsonSerializable(typeof(DefinitionModel))]
public sealed partial class ModelJsonContext : JsonSerializerContext;

public sealed record SourceLocation(string File, int Line);

/// <summary>Either a repo's <c>sprig.cs</c> or a trellis. Both can own containers.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(RepoModel), "repo")]
[JsonDerivedType(typeof(TrellisModel), "trellis")]
public abstract record DefinitionModel(
    int Schema,
    string Name,
    string SdkVersion,
    SourceLocation Location,
    IReadOnlyList<ContainerModel> Containers);

public sealed record RepoModel(
    int Schema,
    string Name,
    string SdkVersion,
    SourceLocation Location,
    IReadOnlyList<ContainerModel> Containers,
    IReadOnlyList<AppModel> Apps,
    IReadOnlyList<ComposeFileModel> ComposeFiles,
    IReadOnlyList<ImportModel> Imports,
    IReadOnlyList<ExportModel> Exports)
    : DefinitionModel(Schema, Name, SdkVersion, Location, Containers);

public sealed record TrellisModel(
    int Schema,
    string Name,
    string SdkVersion,
    SourceLocation Location,
    IReadOnlyList<ContainerModel> Containers,
    IReadOnlyList<RepoRefModel> Repos,
    IReadOnlyList<BindingModel> Bindings,
    IReadOnlyList<GroupModel> Groups,
    WorktreePolicyModel? Worktrees,
    string PortPolicy,
    IReadOnlyList<LinkModel>? Links = null)
    : DefinitionModel(Schema, Name, SdkVersion, Location, Containers);

/// <summary>A page worth opening, named for what it's for: where you'd actually start, rather than wherever the
/// endpoint's root lands.</summary>
/// <param name="Url">A template that resolves to an absolute http(s) url.</param>
public sealed record LinkModel(string Label, string Url, SourceLocation Location);

/// <param name="Outputs">Files that also receive the app's process env vars, for running it by hand or from an IDE.</param>
/// <param name="Job">A one-off command (such as migrations) that runs to completion, rather than an app that keeps running.</param>
/// <param name="ReadyLog">A regular expression; the app is ready once a line of its output matches.</param>
/// <param name="Restart">What to do when it exits without being asked to; null to leave it exited.</param>
/// <param name="Links">Quick links to the app's pages.</param>
public sealed record AppModel(
    string Name,
    string Kind,
    string Path,
    SourceLocation Location,
    IReadOnlyList<EndpointModel> Endpoints,
    IReadOnlyList<EnvModel> Env,
    string? Run,
    IReadOnlyList<string> WaitFor,
    IReadOnlyList<EnvOutputModel> Outputs,
    bool Job,
    string? ReadyLog = null,
    RestartModel? Restart = null,
    IReadOnlyList<LinkModel>? Links = null);

/// <param name="Policy"><c>onFailure</c> (a non-zero exit) or <c>always</c> (any exit; for a job, the same as
/// <c>onFailure</c>).</param>
/// <param name="Max">Restarts in a row before giving up. A run that lasted a minute starts the count again.</param>
public sealed record RestartModel(string Policy, int Max, SourceLocation Location);

/// <summary>A file, under the app's path, that gets the app's process env vars written into it.</summary>
/// <param name="Kind"><c>dotenv</c>, <c>launchSettings</c> or <c>appSettings</c>.</param>
/// <param name="File">Relative to the app's path.</param>
/// <param name="Profile">The launch profile's name, for <c>launchSettings</c>.</param>
public sealed record EnvOutputModel(string Kind, string File, string? Profile, SourceLocation Location);

/// <param name="Health">A command run inside the container that exits 0 once it's ready; null to only wait for it to
/// be running.</param>
public sealed record ContainerModel(
    string Name,
    string Image,
    SourceLocation Location,
    IReadOnlyList<EndpointModel> Endpoints,
    IReadOnlyList<EnvModel> Env,
    IReadOnlyList<VolumeModel> Volumes,
    string? Health);

public sealed record VolumeModel(string Name, string Target);

/// <param name="TargetPort">The port inside a container; null for an app, which listens on the allocated port itself.</param>
/// <param name="Scheme">How a url to this endpoint starts (<c>http</c>, <c>amqp</c>, ...); null when it has no url.</param>
/// <param name="Health">An app's path on this endpoint (e.g. <c>/health</c>) that answers with a success status once
/// the app is ready.</param>
public sealed record EndpointModel(string Name, int? TargetPort, string? Pin, string? Scheme, SourceLocation Location, string? Health = null);

/// <summary>One env var. <see cref="File"/> is null for a process env var, else the env file it's written to.</summary>
public sealed record EnvModel(string Key, string Value, string? File, SourceLocation Location);

public sealed record ImportModel(string Name, string Type, SourceLocation Location);

public sealed record ExportModel(string Name, string Type, string Value, SourceLocation Location);

/// <summary>A compose file the repo already has. Services named here get their host ports allocated and mapped;
/// <see cref="Overrides"/> set other values by path.</summary>
public sealed record ComposeFileModel(
    string File,
    SourceLocation Location,
    IReadOnlyList<ComposeServiceModel> Services,
    IReadOnlyList<ComposeOverrideModel> Overrides);

/// <param name="Health">A command that exits 0 once the service is ready, replacing the file's own health check.</param>
public sealed record ComposeServiceModel(string Name, SourceLocation Location, IReadOnlyList<EndpointModel> Endpoints, string? Health);

/// <param name="Path">A dotted path into the compose file, e.g. <c>services.postgres.container_name</c>.</param>
public sealed record ComposeOverrideModel(string Path, string Value, SourceLocation Location);

/// <param name="Path">The repo's folder (or later a git URL), relative to the trellis file; for <see cref="Use"/>, the
/// entry's ID, which keys the repo the same way.</param>
/// <param name="Use">A repo from sprig's Repos list, by ID: its folder and definition both come from there.</param>
public sealed record RepoRefModel(string Path, SourceLocation Location, string? Use = null);

/// <summary>The trellis's answer for an import: a value it supplies, or a named repo's export.</summary>
public sealed record BindingModel(string Import, string Value, SourceLocation Location);

public sealed record GroupModel(string Name, IReadOnlyList<GroupMemberModel> Members, SourceLocation Location);

/// <param name="Apps">Only these apps of the repo; null means all of them.</param>
public sealed record GroupMemberModel(string Repo, IReadOnlyList<string>? Apps);

public sealed record WorktreePolicyModel(string From);
