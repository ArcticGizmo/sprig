using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Sprig.App.Controls;

/// <summary>
/// Terminal-style completions for a path being typed: the entries of the folder typed so far whose names start with
/// what follows the last slash. Folders end in <c>/</c> so the next Tab descends into them. Paths are shown with
/// forward slashes whatever the OS.
/// </summary>
public static class PathCompletion
{
    public static string ToPosix(string path) => path.Replace('\\', '/');

    /// <summary><c>~</c> or a leading <c>~/</c> as the home folder, as a shell reads it, so a field can be typed from
    /// there; anything else as it is.</summary>
    /// <param name="home">The home folder; null for the user's own.</param>
    public static string ExpandHome(string path, string? home = null)
    {
        if (path != "~" && !path.StartsWith("~/", StringComparison.Ordinal)) return path;
        home = ToPosix(home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)).TrimEnd('/');
        return home + path[1..];
    }

    /// <param name="fileFilter">Which files to offer; folders are always offered. Null offers every file.</param>
    /// <param name="home">What <c>~</c> stands for; null for the user's own home folder. The suggestions keep the
    /// <c>~</c> as typed.</param>
    public static IReadOnlyList<string> Suggest(string text, Func<string, bool>? fileFilter = null, int max = 50, string? home = null)
    {
        var typed = ToPosix(text ?? "");
        if (typed == "~") return ["~/"];
        var slash = typed.LastIndexOf('/');
        var folderPart = slash >= 0 ? typed[..(slash + 1)] : "";
        var prefix = slash >= 0 ? typed[(slash + 1)..] : typed;

        var folder = folderPart.Length == 0 ? Directory.GetCurrentDirectory() : ExpandHome(folderPart, home);
        try
        {
            if (!Directory.Exists(folder)) return [];
            var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };

            var folders = Directory.EnumerateDirectories(folder, "*", options)
                .Select(Path.GetFileName)
                .Where(n => n is not null && n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !n.StartsWith('.'))
                .Select(n => folderPart + n + "/");
            var files = Directory.EnumerateFiles(folder, "*", options)
                .Select(Path.GetFileName)
                .Where(n => n is not null && n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && (fileFilter?.Invoke(n) ?? true))
                .Select(n => folderPart + n);

            return folders.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Concat(files.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                .Take(max)
                .Cast<string>()
                .ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }
}
