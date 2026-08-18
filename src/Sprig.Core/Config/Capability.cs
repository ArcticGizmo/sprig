using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sprig.Core.Config;

// The map-model surface of a repo config ("The Graph Turn"). A repo — and each module within it —
// declares what it PROVIDES to others and what it NEEDS from them. This replaced the stack-era
// pure-consumer `inputs`, which have been removed. See docs/graph-model-redesign.md.

/// <summary>
/// A capability a repo/module offers others: a named contract (<see cref="Capability"/>) that comes in
/// several <b>shapes</b>. The one real resource a repo owns is a <b>port</b> (auto-allocated per
/// workspace) — <see cref="Ports"/> — and every <see cref="Shapes"/> entry is a string <i>derived</i>
/// from those ports (a url, a connString). Both are addressed uniformly as
/// <c>${sprig.&lt;capability&gt;.&lt;output&gt;}</c>, where <c>&lt;output&gt;</c> is a port or a shape name.
/// Name the <i>service</i>, not the socket: <c>vite-server.port</c> reads as a hierarchy; <c>vite-port.port</c>
/// stutters. Every capability always exposes its <c>port</c>; shapes are optional extras.
/// </summary>
public sealed record ProvidedCapability
{
    /// <summary>The contract name others match against (identifier chars only; no dots — dots delimit the
    /// substitution path). Unique across a repo's provides. Name the service (<c>vite-server</c>), not one
    /// of its shapes.</summary>
    public string Capability { get; init; } = "";

    /// <summary>The real resources this capability owns: auto-allocated host ports, keyed by output name
    /// (conventionally the single fixed <c>port</c>). Referenced as <c>${sprig.&lt;capability&gt;.&lt;portName&gt;}</c>.</summary>
    public IReadOnlyDictionary<string, PortSpec> Ports { get; init; } = new Dictionary<string, PortSpec>();

    /// <summary>Derived string shapes built over this capability's ports (a url, a connection string),
    /// keyed by output name → template. Referenced as <c>${sprig.&lt;capability&gt;.&lt;shapeName&gt;}</c>.</summary>
    public IReadOnlyDictionary<string, string> Shapes { get; init; } = new Dictionary<string, string>();

    /// <summary>Every output name this capability exposes — its ports then its derived shapes. The two share
    /// one namespace, so a name is a port or a shape, never both.</summary>
    [JsonIgnore]
    public IEnumerable<string> OutputNames => Ports.Keys.Concat(Shapes.Keys);
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
/// A single provided <b>port</b> — an auto-allocated host port, optionally pinned to an
/// <see cref="Allowed"/> set. Serialised as <c>true</c> (any host port) or <c>{ "allowed": "8100-8103" }</c>
/// (constrained) by <see cref="PortSpecConverter"/>.
/// </summary>
[JsonConverter(typeof(PortSpecConverter))]
public sealed record PortSpec
{
    /// <summary>An optional restriction on which host ports this may take, as a compact <c>PortSetSpec</c>
    /// (e.g. <c>"8100-8103"</c>). Null/empty = the whole settings range.</summary>
    public string? Allowed { get; init; }

    /// <summary>Any host port in the settings range.</summary>
    public static PortSpec Any { get; } = new();

    /// <summary>A port pinned to <paramref name="allowed"/> (blank/whitespace collapses to <see cref="Any"/>).</summary>
    public static PortSpec Constrained(string? allowed) =>
        string.IsNullOrWhiteSpace(allowed) ? Any : new PortSpec { Allowed = allowed };
}

/// <summary>Reads/writes a <see cref="PortSpec"/>: <c>true</c> (or a bare <c>{}</c>) is any host port; a JSON
/// string or <c>{ "allowed": "…" }</c> object pins it to a set.</summary>
public sealed class PortSpecConverter : JsonConverter<PortSpec>
{
    public override PortSpec Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.True:
            case JsonTokenType.False:
            case JsonTokenType.Null:
                return PortSpec.Any;

            case JsonTokenType.String:
                return PortSpec.Constrained(reader.GetString());

            case JsonTokenType.StartObject:
                string? allowed = null;
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndObject)
                        return PortSpec.Constrained(allowed);
                    if (reader.TokenType != JsonTokenType.PropertyName)
                        throw new JsonException("malformed port object");
                    var prop = reader.GetString();
                    reader.Read();
                    switch (prop?.ToLowerInvariant())
                    {
                        case "allowed": allowed = reader.GetString(); break;
                        default: reader.Skip(); break;   // tolerate unknown keys; the validator judges
                    }
                }
                throw new JsonException("unterminated port object");

            default:
                throw new JsonException("a port must be `true` or a { \"allowed\": \"…\" } object");
        }
    }

    public override void Write(Utf8JsonWriter writer, PortSpec value, JsonSerializerOptions options)
    {
        if (string.IsNullOrWhiteSpace(value.Allowed))
        {
            writer.WriteBooleanValue(true);
        }
        else
        {
            writer.WriteStartObject();
            writer.WriteString("allowed", value.Allowed);
            writer.WriteEndObject();
        }
    }
}
