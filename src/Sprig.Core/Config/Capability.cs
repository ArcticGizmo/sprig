using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sprig.Core.Config;

// The map-model surface of a repo config ("The Graph Turn"). A repo — and each module within it —
// declares what it PROVIDES to others and what it NEEDS from them. This replaced the stack-era
// pure-consumer `inputs`, which have been removed. See docs/graph-model-redesign.md.

/// <summary>
/// A capability a repo/module offers others: a named contract (<see cref="Capability"/>) plus its
/// <see cref="Outputs"/> — the values consumers can reference as <c>${sprig.&lt;capability&gt;.&lt;output&gt;}</c>.
/// The only real "type" of an output is a port (auto-allocated per workspace); everything else is a
/// string template derived from ports. <see cref="Type"/> is a non-binding hint (e.g. <c>http</c>,
/// <c>postgres</c>) used only for UI grouping/validation affordances.
/// </summary>
public sealed record ProvidedCapability
{
    /// <summary>The contract name others match against (identifier chars only; no dots — dots delimit the
    /// substitution path). Unique across a repo's provides.</summary>
    public string Capability { get; init; } = "";

    /// <summary>Optional, non-binding hint about the capability's shape (e.g. <c>http</c>, <c>postgres</c>).</summary>
    public string? Type { get; init; }

    /// <summary>Named outputs, keyed by output name. Each is either an allocated port or a derived string.</summary>
    public IReadOnlyDictionary<string, OutputSpec> Outputs { get; init; } = new Dictionary<string, OutputSpec>();
}

/// <summary>
/// A capability a repo/module consumes. Resolved at checkout against a provider — a sibling module in
/// the same repo first (local wiring, nearest-wins), then any provider in the selected map. Referenced
/// in templates as <c>${sprig.&lt;capability&gt;.&lt;output&gt;}</c>, or via the <see cref="As"/> alias when set.
/// </summary>
public sealed record Need
{
    /// <summary>The capability name to wire in (identifier chars only).</summary>
    public string Capability { get; init; } = "";

    /// <summary>Optional local alias — reference the wired provider's outputs as
    /// <c>${sprig.&lt;as&gt;.&lt;output&gt;}</c> instead of by capability name. Defaults to the capability name.</summary>
    public string? As { get; init; }

    /// <summary>The name under which this need is referenced in templates: <see cref="As"/> if set, else <see cref="Capability"/>.</summary>
    [JsonIgnore]
    public string Alias => string.IsNullOrWhiteSpace(As) ? Capability : As!;
}

/// <summary>
/// A single provided output: either an allocated <b>port</b> (<c>{ "port": true, "allowed": "8100-8103" }</c>)
/// or a derived <b>string template</b> (<c>"http://localhost:${sprig.api.port}"</c>). The JSON is a union —
/// an object for a port, a bare string for a template — handled by <see cref="OutputSpecConverter"/>.
/// </summary>
[JsonConverter(typeof(OutputSpecConverter))]
public sealed record OutputSpec
{
    /// <summary>True when this output is an auto-allocated host port.</summary>
    public bool IsPort { get; init; }

    /// <summary>For a port output, an optional restriction on which host ports it may take, as a compact
    /// <c>PortSetSpec</c> (e.g. <c>"8100-8103"</c>). Null = the whole settings range.</summary>
    public string? Allowed { get; init; }

    /// <summary>For a derived output, the string template (may reference sibling outputs / <c>${sprig.workspace}</c>).</summary>
    public string? Template { get; init; }

    public static OutputSpec Port(string? allowed = null) => new() { IsPort = true, Allowed = allowed };
    public static OutputSpec Derived(string template) => new() { Template = template };
}

/// <summary>Reads/writes an <see cref="OutputSpec"/>: an object <c>{ "port": true, "allowed"?: "…" }</c> is a
/// port; a JSON string is a derived template.</summary>
public sealed class OutputSpecConverter : JsonConverter<OutputSpec>
{
    public override OutputSpec Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return OutputSpec.Derived(reader.GetString() ?? "");

            case JsonTokenType.StartObject:
                var isPort = false;
                string? allowed = null;
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndObject)
                        return new OutputSpec { IsPort = isPort, Allowed = allowed };
                    if (reader.TokenType != JsonTokenType.PropertyName)
                        throw new JsonException("malformed output object");
                    var prop = reader.GetString();
                    reader.Read();
                    switch (prop?.ToLowerInvariant())
                    {
                        case "port": isPort = reader.TokenType == JsonTokenType.True; break;
                        case "allowed": allowed = reader.GetString(); break;
                        default: reader.Skip(); break;   // tolerate unknown keys; the validator judges
                    }
                }
                throw new JsonException("unterminated output object");

            default:
                throw new JsonException("an output must be a string template or a { \"port\": true } object");
        }
    }

    public override void Write(Utf8JsonWriter writer, OutputSpec value, JsonSerializerOptions options)
    {
        if (value.IsPort)
        {
            writer.WriteStartObject();
            writer.WriteBoolean("port", true);
            if (!string.IsNullOrWhiteSpace(value.Allowed))
                writer.WriteString("allowed", value.Allowed);
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteStringValue(value.Template ?? "");
        }
    }
}
