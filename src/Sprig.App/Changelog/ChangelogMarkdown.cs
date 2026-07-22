using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Sprig.App.Changelog;

/// <summary>
/// Loads the embedded <c>CHANGELOG.md</c> and renders its (lightweight) markdown into a stacked column of
/// themed text. Shared by the About "What's new" viewer and the post-update window so the two read
/// identically. Handles just the subset the changelog uses: <c>## </c>/<c>### </c> headings,
/// <c>-</c>/<c>*</c> bullets, <c>&gt; </c> quotes, <c>---</c> rules, and inline emphasis/links.
/// </summary>
internal static class ChangelogMarkdown
{
    /// <summary>Reads the changelog embedded at build time (csproj: <c>Sprig.CHANGELOG.md</c>), or null.</summary>
    public static string? LoadEmbedded()
    {
        try
        {
            using var s = typeof(ChangelogMarkdown).Assembly.GetManifestResourceStream("Sprig.CHANGELOG.md");
            if (s is null) return null;
            using var reader = new StreamReader(s);
            return reader.ReadToEnd();
        }
        catch { return null; }
    }

    /// <summary>Appends one control per markdown line into <paramref name="page"/>.</summary>
    public static void Render(StackPanel page, IEnumerable<string> lines)
    {
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("## "))
                page.Children.Add(Text(StripInline(line[3..]), 15, FontWeight.SemiBold, "TitleBrush", top: 10, bottom: 2));
            else if (line.StartsWith("### "))
                page.Children.Add(Text(StripInline(line[4..]), 13, FontWeight.Bold, "FgBrush", top: 6, bottom: 4));
            else if (line.StartsWith("# ")) { /* the H1 title is redundant here */ }
            else if (line.StartsWith("- ") || line.StartsWith("* "))
                page.Children.Add(Text("•  " + StripInline(line[2..]), 13, FontWeight.Normal, "FgBrush", left: 4, bottom: 3));
            else if (line == "---")
                page.Children.Add(Separator());
            else if (line.StartsWith("> "))
                page.Children.Add(Text(StripInline(line[2..]), 13, FontWeight.Normal, "MutedBrush", left: 12, bottom: 6, italic: true));
            else if (line.Trim().Length > 0)
                page.Children.Add(Text(StripInline(line), 13, FontWeight.Normal, "FgBrush", bottom: 3));
        }
    }

    static TextBlock Text(string text, double size, FontWeight weight, string brushKey,
        double left = 0, double top = 0, double bottom = 0, bool italic = false)
        => new()
        {
            Text = text,
            FontSize = size,
            FontWeight = weight,
            FontStyle = italic ? FontStyle.Italic : FontStyle.Normal,
            Foreground = Brush(brushKey),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(left, top, 0, bottom),
        };

    static Border Separator() => new()
    {
        BorderBrush = Brush("BorderBrush"),
        BorderThickness = new Thickness(0, 1, 0, 0),
        Margin = new Thickness(0, 8, 0, 8),
    };

    static IBrush? Brush(string key)
        => Application.Current is { } app && app.TryFindResource(key, out var v) && v is IBrush b ? b : null;

    /// <summary>Strips inline markdown (bold/italic/code/links) down to its display text.</summary>
    public static string StripInline(string text)
    {
        text = Regex.Replace(text, @"\*\*(.*?)\*\*", "$1");
        text = Regex.Replace(text, @"__(.*?)__", "$1");
        text = Regex.Replace(text, @"\*(.*?)\*", "$1");
        text = Regex.Replace(text, @"_(.*?)_", "$1");
        text = Regex.Replace(text, @"`([^`]+)`", "$1");
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]+\)", "$1");
        return text;
    }
}
