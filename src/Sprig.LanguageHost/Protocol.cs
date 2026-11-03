using System.Text.Json.Serialization;

namespace Sprig.LanguageHost;

public sealed record Request(int Id, string Op, string? Text, int Position);

public sealed record CompletionItemDto(string Label, string Kind, string? Detail);

public sealed record DiagnosticDto(int Start, int End, int Line, string Severity, string Id, string Message);

public sealed record Response(
    int Id,
    double Ms,
    IReadOnlyList<CompletionItemDto>? Items = null,
    IReadOnlyList<DiagnosticDto>? Diagnostics = null,
    double? WorkingSetMb = null,
    double? ManagedHeapMb = null,
    string? Error = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Request))]
[JsonSerializable(typeof(Response))]
public sealed partial class ProtocolJson : JsonSerializerContext;
