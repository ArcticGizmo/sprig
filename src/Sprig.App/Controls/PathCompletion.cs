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

    /// <param name="fileFilter">Which files to offer; folders are always offered. Null offers every file.</param>
    public static IReadOnlyList<string> Suggest(string text, Func<string, bool>? fileFilter = null, int max = 50)
    {
        var typed = ToPosix(text ?? "");
        var slash = typed.LastIndexOf('/');
        var folderPart = slash >= 0 ? typed[..(slash + 1)] : "";
        var prefix = slash >= 0 ? typed[(slash + 1)..] : typed;

        var folder = folderPart.Length == 0 ? Directory.GetCurrentDirectory() : folderPart;
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
