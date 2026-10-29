using System.Text.RegularExpressions;
using Sprig.Core.Store;

namespace Sprig.Core.Workspaces;

/// <summary>
/// Tag parsing, validation and filtering. Keys are <c>&lt;namespace&gt;.&lt;name&gt;</c> so callers can't collide
/// with each other (<c>agent.session</c>, <c>ci.run</c>); values are free strings. Sprig never reads a tag's
/// meaning — a tag that needs behaviour becomes a first-class field instead.
/// </summary>
public static partial class WorkspaceTags
{
    [GeneratedRegex("^[a-z0-9][a-z0-9-]*\\.[a-z0-9][a-z0-9-]*$")]
    private static partial Regex KeyPattern();

    public static bool IsValidKey(string key) => KeyPattern().IsMatch(key);

    /// <summary>Throw unless <paramref name="key"/> is a valid tag key.</summary>
    public static string EnsureValidKey(string key)
        => IsValidKey(key) ? key
            : throw new WorkspaceException(
                $"'{key}' is not a valid tag key — use <namespace>.<name>, lower case, letters, digits and '-' (e.g. agent.session)");

    /// <summary>Parse <c>key=value</c> (the value may be empty or contain further <c>=</c>).</summary>
    public static KeyValuePair<string, string> ParsePair(string text)
    {
        var eq = text.IndexOf('=');
        if (eq < 0) throw new WorkspaceException($"'{text}' is not a tag — write it as key=value");
        return new(EnsureValidKey(text[..eq].Trim()), text[(eq + 1)..]);
    }

    /// <summary>Parse several <c>key=value</c> pairs; a repeated key keeps the last value.</summary>
    public static IReadOnlyDictionary<string, string> ParsePairs(IEnumerable<string> pairs)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in pairs.Select(ParsePair)) tags[p.Key] = p.Value;
        return tags;
    }

    /// <summary>A filter: <c>key=value</c> requires that value, a bare <c>key</c> only that the tag is present.</summary>
    public sealed record Filter(string Key, string? Value)
    {
        public bool Matches(InstanceRecord record)
            => record.Tags.TryGetValue(Key, out var v) && (Value is null || v == Value);

        public static Filter Parse(string text)
        {
            var eq = text.IndexOf('=');
            return eq < 0
                ? new Filter(EnsureValidKey(text.Trim()), null)
                : new Filter(EnsureValidKey(text[..eq].Trim()), text[(eq + 1)..]);
        }
    }

    /// <summary>True when the record matches every filter (no filters match everything).</summary>
    public static bool MatchesAll(InstanceRecord record, IEnumerable<Filter> filters)
        => filters.All(f => f.Matches(record));
}
