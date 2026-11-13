using System.Text.RegularExpressions;

namespace Sprig.Engine.Store;

/// <summary>IDs end up in folder names, compose project names and command lines, so they keep to a safe alphabet.</summary>
public static partial class Ids
{
    /// <summary>Null when <paramref name="id"/> is usable, else why not.</summary>
    public static string? Check(string id, string what)
        => Rule().IsMatch(id) ? null
            : $"'{id}' can't be {what}: use letters, digits, '.', '_' and '-', starting with a letter or digit";

    /// <summary><paramref name="name"/> made into a usable ID, or <paramref name="fallback"/> when nothing is left.</summary>
    public static string Slug(string name, string fallback)
    {
        var slug = Regex.Replace(name.Trim(), "[^A-Za-z0-9._-]+", "-").Trim('-', '.', '_');
        return slug.Length == 0 ? fallback : slug.Length > 63 ? slug[..63] : slug;
    }

    /// <summary><paramref name="wanted"/>, or the first <c>wanted-2</c>, <c>wanted-3</c>… not in <paramref name="taken"/>.</summary>
    public static string Unused(string wanted, IReadOnlySet<string> taken)
    {
        var candidate = wanted;
        for (var n = 2; taken.Contains(candidate); n++) candidate = $"{wanted}-{n}";
        return candidate;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,62}$")]
    private static partial Regex Rule();
}
