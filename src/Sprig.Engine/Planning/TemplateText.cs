using System.Text;

namespace Sprig.Engine.Planning;

public abstract record TemplatePart;

public sealed record TextPart(string Text) : TemplatePart;

public sealed record RefPart(string Path) : TemplatePart;

/// <summary>The template form definitions emit: literal text with <c>{path}</c> placeholders and doubled braces for a
/// literal brace.</summary>
public static class TemplateText
{
    public static IReadOnlyList<TemplatePart> Parse(string template)
    {
        var parts = new List<TemplatePart>();
        var text = new StringBuilder();
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c == '{' && i + 1 < template.Length && template[i + 1] == '{') { text.Append('{'); i++; continue; }
            if (c == '}' && i + 1 < template.Length && template[i + 1] == '}') { text.Append('}'); i++; continue; }
            if (c == '}') throw new FormatException($"unmatched '}}' at position {i} in \"{template}\"");
            if (c != '{') { text.Append(c); continue; }

            var end = template.IndexOf('}', i + 1);
            if (end < 0) throw new FormatException($"unclosed '{{' at position {i} in \"{template}\"");
            if (text.Length > 0) { parts.Add(new TextPart(text.ToString())); text.Clear(); }
            parts.Add(new RefPart(template[(i + 1)..end]));
            i = end;
        }
        if (text.Length > 0) parts.Add(new TextPart(text.ToString()));
        return parts;
    }
}
