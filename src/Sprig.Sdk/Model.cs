using System.Text.Json.Serialization;

namespace Sprig.Model;

// The data a definition emits. The engine reads this, never the C#, so its shape is the contract between them.

/// <summary>Source-generated because file-based apps default to Native AOT settings, which switch reflection-based
/// JSON off. The engine reads the model with the same context.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RepoModel))]
public sealed partial class ModelJsonContext : JsonSerializerContext;

public sealed record SourceLocation(string File, int Line);

public sealed record RepoModel(
    int Schema,
    string Kind,
    string Name,
    string SdkVersion,
    SourceLocation Location,
    IReadOnlyList<AppModel> Apps,
    IReadOnlyList<ContainerModel> Containers,
    IReadOnlyList<ImportModel> Imports,
    IReadOnlyList<ExportModel> Exports);

public sealed record AppModel(
    string Name,
    string Kind,
    string Path,
    SourceLocation Location,
    IReadOnlyList<EndpointModel> Endpoints,
    IReadOnlyList<EnvModel> Env,
    string? Run,
    IReadOnlyList<string> WaitFor);

public sealed record ContainerModel(
    string Name,
    string Image,
    SourceLocation Location,
    IReadOnlyList<EndpointModel> Endpoints,
    IReadOnlyList<EnvModel> Env);

public sealed record EndpointModel(string Name, int? TargetPort, string? Pin, SourceLocation Location);

/// <summary>One env var. <see cref="File"/> is null for a process env var, else the env file it's written to.</summary>
public sealed record EnvModel(string Key, string Value, string? File, SourceLocation Location);

public sealed record ImportModel(string Name, string Type, SourceLocation Location);

public sealed record ExportModel(string Name, string Type, string Value, SourceLocation Location);
