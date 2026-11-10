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

/// <summary>Either a repo's <c>sprig.cs</c> or an orchestration. Both can own containers.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(RepoModel), "repo")]
[JsonDerivedType(typeof(OrchestrationModel), "orchestration")]
public abstract record DefinitionModel(
    int Schema,
    string Name,
    string SdkVersion,
    SourceLocation Location,
    IReadOnlyList<ContainerModel> Containers);

/// <param name="Groups">Slices of the repo's apps to start on their own.</param>
/// <param name="WorktreeCopies">Files and folders git doesn't have that a worktree needs, copied from the repo's own
/// checkout.</param>
public sealed record RepoModel(
    int Schema,
    string Name,
    string SdkVersion,
    SourceLocation Location,
    IReadOnlyList<ContainerModel> Containers,
    IReadOnlyList<AppModel> Apps,
    IReadOnlyList<ComposeFileModel> ComposeFiles,
    IReadOnlyList<SettingModel> Settings,
    IReadOnlyList<ExportModel> Exports,
    IReadOnlyList<RepoGroupModel>? Groups = null,
    IReadOnlyList<WorktreeCopyModel>? WorktreeCopies = null)
    : DefinitionModel(Schema, Name, SdkVersion, Location, Containers);

/// <param name="Path">Relative to the repo's folder, with forward slashes.</param>
public sealed record WorktreeCopyModel(string Path, SourceLocation Location);

/// <param name="Apps">The repo's apps and jobs, by name.</param>
public sealed record RepoGroupModel(string Name, IReadOnlyList<string> Apps, SourceLocation Location);

public sealed record OrchestrationModel(
    int Schema,
    string Name,
    string SdkVersion,
    SourceLocation Location,
    IReadOnlyList<ContainerModel> Containers,
    IReadOnlyList<RepoRefModel> Repos,
    IReadOnlyList<BindingModel> Bindings,
    IReadOnlyList<GroupModel> Groups,
    IReadOnlyList<LinkModel>? Links = null)
    : DefinitionModel(Schema, Name, SdkVersion, Location, Containers);

/// <summary>A page worth opening, named for what it's for: where you'd actually start, rather than wherever the
/// endpoint's root lands.</summary>
/// <param name="Url">A template that resolves to an absolute http(s) url.</param>
public sealed record LinkModel(string Label, string Url, SourceLocation Location);

/// <param name="Env">The values it gets, each in the file it's written to.</param>
/// <param name="Job">A one-off command (such as migrations) that runs to completion, rather than an app that keeps running.</param>
/// <param name="ReadyLog">A regular expression; the app is ready once a line of its output matches.</param>
/// <param name="Restart">What to do when it exits without being asked to; null to leave it exited.</param>
/// <param name="Links">Quick links to the app's pages.</param>
/// <param name="Role">What it is (<c>web</c>, <c>api</c>, <c>worker</c>...), for how sprig draws it; null to leave it to sprig.</param>
public sealed record AppModel(
    string Name,
    string Kind,
    string Path,
    SourceLocation Location,
    IReadOnlyList<EndpointModel> Endpoints,
    IReadOnlyList<EnvModel> Env,
    string? Run,
    IReadOnlyList<string> WaitFor,
    bool Job,
    string? ReadyLog = null,
    RestartModel? Restart = null,
    IReadOnlyList<LinkModel>? Links = null,
    string? Role = null);

/// <param name="Policy"><c>onFailure</c> (a non-zero exit) or <c>always</c> (any exit; for a job, the same as
/// <c>onFailure</c>).</param>
/// <param name="Max">Restarts in a row before giving up. A run that lasted a minute starts the count again.</param>
public sealed record RestartModel(string Policy, int Max, SourceLocation Location);

/// <param name="Health">A command run inside the container that exits 0 once it's ready; null to only wait for it to
/// be running.</param>
/// <param name="Role">What it is (<c>database</c>, <c>queue</c>...), for how sprig draws it; null to leave it to sprig.</param>
public sealed record ContainerModel(
    string Name,
    string Image,
    SourceLocation Location,
    IReadOnlyList<EndpointModel> Endpoints,
    IReadOnlyList<EnvModel> Env,
    IReadOnlyList<VolumeModel> Volumes,
    string? Health,
    string? Role = null);

public sealed record VolumeModel(string Name, string Target);

/// <param name="TargetPort">The port inside a container; null for an app, which listens on the allocated port itself.</param>
/// <param name="Scheme">How a url to this endpoint starts (<c>http</c>, <c>amqp</c>, ...); null when it has no url.</param>
/// <param name="Health">An app's path on this endpoint (e.g. <c>/health</c>) that answers with a success status once
/// the app is ready.</param>
public sealed record EndpointModel(string Name, int? TargetPort, string? Pin, string? Scheme, SourceLocation Location, string? Health = null);

/// <summary>One value. An app's is written into <see cref="File"/>, relative to the app's path, never set on its
/// process; a container's goes in its compose definition and has no file.</summary>
/// <param name="Kind"><c>dotenv</c>, <c>appSettings</c> or <c>launchSettings</c>; null for a container's.</param>
/// <param name="Profile">The launch profile's name, for <c>launchSettings</c>.</param>
public sealed record EnvModel(string Key, string Value, string? File, SourceLocation Location, string? Kind = null, string? Profile = null);

/// <param name="Default">A template for the value to use when nothing else gives one; null to be asked for one.</param>
/// <param name="Role">What the value points at (<c>queue</c>, <c>identity</c>...), for how sprig draws it; null to
/// leave it to sprig.</param>
public sealed record SettingModel(string Name, string Type, SourceLocation Location, string? Default = null, string? Role = null);

public sealed record ExportModel(string Name, string Type, string Value, SourceLocation Location);

/// <summary>A compose file the repo already has. Services named here get their host ports allocated and mapped;
/// <see cref="Overrides"/> set other values by path.</summary>
public sealed record ComposeFileModel(
    string File,
    SourceLocation Location,
    IReadOnlyList<ComposeServiceModel> Services,
    IReadOnlyList<ComposeOverrideModel> Overrides);

/// <param name="Health">A command that exits 0 once the service is ready, replacing the file's own health check.</param>
/// <param name="Role">What it is (<c>database</c>, <c>queue</c>...), for how sprig draws it; null to leave it to sprig.</param>
public sealed record ComposeServiceModel(string Name, SourceLocation Location, IReadOnlyList<EndpointModel> Endpoints, string? Health, string? Role = null);

/// <param name="Path">A dotted path into the compose file, e.g. <c>services.postgres.container_name</c>.</param>
public sealed record ComposeOverrideModel(string Path, string Value, SourceLocation Location);

/// <param name="Path">The repo's folder (or later a git URL), relative to the orchestration file; for <see cref="Use"/>, the
/// entry's ID, which keys the repo the same way.</param>
/// <param name="Use">A repo from sprig's Repos list, by ID: its folder and definition both come from there.</param>
public sealed record RepoRefModel(string Path, SourceLocation Location, string? Use = null);

/// <summary>The orchestration's answer for a setting: a value it supplies, or a named repo's export.</summary>
public sealed record BindingModel(string Setting, string Value, SourceLocation Location);

public sealed record GroupModel(string Name, IReadOnlyList<GroupMemberModel> Members, SourceLocation Location);

/// <param name="Apps">Only these apps of the repo; null means all of them.</param>
public sealed record GroupMemberModel(string Repo, IReadOnlyList<string>? Apps);
