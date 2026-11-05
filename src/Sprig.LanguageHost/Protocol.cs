using System.Text.Json.Serialization;

namespace Sprig.LanguageHost;

/// <param name="Position">A caret offset; for <c>describe</c>, the index of an item in the last completion list.</param>
public sealed record Request(int Id, string Op, string? Text, int Position);

/// <param name="Suffix">Shown after the label but not inserted, e.g. <c>&lt;&gt;</c> on a generic method.</param>
/// <param name="Sort">Roslyn's own ordering, so the likeliest items come first as they do in an IDE.</param>
public sealed record CompletionItemDto(string Label, string Kind, string? Detail, string? Suffix = null, string? Sort = null);

public sealed record DiagnosticDto(int Start, int End, int Line, string Severity, string Id, string Message);

/// <summary>What an IDE shows on hover: the symbol's signature and its doc comment.</summary>
public sealed record HoverDto(int Start, int End, string Signature, string? Docs);

/// <param name="SpanStart">Where the word being completed starts, so typing on filters the list.</param>
/// <param name="Suggestion">The caret is where a new name is likely being typed (after <c>var</c>, say), so the list
/// should only open when asked for.</param>
public sealed record Response(
    int Id,
    double Ms,
    IReadOnlyList<CompletionItemDto>? Items = null,
    IReadOnlyList<DiagnosticDto>? Diagnostics = null,
    double? WorkingSetMb = null,
    double? ManagedHeapMb = null,
    string? Error = null,
    int? SpanStart = null,
    bool? Suggestion = null,
    HoverDto? Hover = null,
    string? Text = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Request))]
[JsonSerializable(typeof(Response))]
public sealed partial class ProtocolJson : JsonSerializerContext;
