using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sprig.Core.Maps;

/// <summary>
/// A <b>map</b> (the Graph Turn model): an open graph of repos you take slices of. It lists the repos in
/// play and stores only the <b>deviations</b> from automatic wiring — which provider satisfies a need when
/// several could (<see cref="Wiring"/>), and a manual fallback value for a need whose provider isn't in the
/// selection (<see cref="Defaults"/>). Everything else is derived from the repos' own provides/needs at
/// checkout. Multiple maps are first-class (different working styles, or unrelated projects). Lives in the
/// central store at <c>maps/&lt;name&gt;.json</c>, never inside a repo. See docs/graph-model-redesign.md.
/// </summary>
public sealed record MapDefinition
{
    /// <summary>Schema version. The map format starts fresh at 1.</summary>
    public int Schema { get; init; } = 1;

    /// <summary>Map name (the filename). Letters, digits, <c>. - _ +</c>.</summary>
    public string Name { get; init; } = "";

    /// <summary>The repos in the map — by local registry name, optionally carrying a git URL for portability.</summary>
    public IReadOnlyList<MapRepo> Repos { get; init; } = [];

    /// <summary>
    /// Disambiguation: <c>Wiring[repo][capability] = providerCapability</c>. Only needed when more than one
    /// selected repo provides a capability a repo needs — pick which one wins. Keyed by repo + capability
    /// (module granularity is added only if a real collision ever appears).
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Wiring { get; init; }
        = new Dictionary<string, IReadOnlyDictionary<string, string>>();

    /// <summary>
    /// Manual fallbacks: <c>Defaults[repo][capability][output] = literal</c>. Used for a need whose provider
    /// isn't in the current selection (e.g. a shared staging URL), so a partial checkout resolves instead of
    /// reporting a gap.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> Defaults { get; init; }
        = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>();

    /// <summary>Optional pool ceiling — the max warm workspaces of this map (replaces the stack's maxSlots).
    /// Null means pooling is unbounded/unconfigured for this map.</summary>
    public int? MaxSlots { get; init; }
}

/// <summary>
/// A repo in a map. JSON is a union: a bare string is a registry name (<c>"acme"</c>); an object carries a
/// name plus an optional git URL (<c>{ "name": "billing", "repo": "git@…" }</c>) so a shared map can
/// bootstrap the repo on a machine that hasn't registered it.
/// </summary>
[JsonConverter(typeof(MapRepoConverter))]
public sealed record MapRepo
{
    /// <summary>The repo's registry name (also the wiring/defaults key, and the clone target name).</summary>
    public string Name { get; init; } = "";

    /// <summary>Optional git URL. When set, checkout can clone + register the repo if it's not already known.
    /// Treated as the <b>canonical/upstream</b> source (see the M5 bootstrap).</summary>
    public string? Repo { get; init; }

    public static MapRepo Local(string name) => new() { Name = name };
}

/// <summary>Reads/writes a <see cref="MapRepo"/>: a JSON string is a name-only entry; an object carries
/// <c>name</c> + optional <c>repo</c> URL.</summary>
public sealed class MapRepoConverter : JsonConverter<MapRepo>
{
    public override MapRepo Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return new MapRepo { Name = reader.GetString() ?? "" };

            case JsonTokenType.StartObject:
                string name = "";
                string? repo = null;
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndObject)
                        return new MapRepo { Name = name, Repo = repo };
                    if (reader.TokenType != JsonTokenType.PropertyName)
                        throw new JsonException("malformed map repo entry");
                    var prop = reader.GetString();
                    reader.Read();
                    switch (prop?.ToLowerInvariant())
                    {
                        case "name": name = reader.GetString() ?? ""; break;
                        case "repo": repo = reader.GetString(); break;
                        default: reader.Skip(); break;
                    }
                }
                throw new JsonException("unterminated map repo entry");

            default:
                throw new JsonException("a map repo must be a name string or a { \"name\": … } object");
        }
    }

    public override void Write(Utf8JsonWriter writer, MapRepo value, JsonSerializerOptions options)
    {
        if (string.IsNullOrWhiteSpace(value.Repo))
        {
            writer.WriteStringValue(value.Name);
        }
        else
        {
            writer.WriteStartObject();
            writer.WriteString("name", value.Name);
            writer.WriteString("repo", value.Repo);
            writer.WriteEndObject();
        }
    }
}
