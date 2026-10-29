using System.Globalization;
using System.Text.RegularExpressions;
using Sprig.Core.Store;

namespace Sprig.Core.Workspaces;

/// <summary>
/// A workspace's colour, used to tint its VS Code title bar so open windows can be told apart at a glance.
/// It belongs to the slot, not the claim: the same workspace keeps the same colour across claims, which is
/// easier to learn. Unless set explicitly it is picked from a fixed palette by a stable hash of the name.
/// </summary>
public static partial class WorkspaceColour
{
    // Mid-tone, mutually distinct hues that read clearly behind either light or dark title text.
    static readonly string[] Palette =
        ["#2563eb", "#16a34a", "#d97706", "#9333ea", "#dc2626", "#0891b2", "#c026d3", "#65a30d", "#ea580c", "#4f46e5"];

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexPattern();

    public static bool IsValid(string colour) => HexPattern().IsMatch(colour);

    /// <summary>The colour to show: the one set on the record, else the slot's palette colour.</summary>
    public static string For(InstanceRecord record) => record.Colour ?? Default(record.Workspace);

    /// <summary>The palette colour for a slot name. FNV-1a rather than <see cref="string.GetHashCode()"/>,
    /// which is randomised per process and would give a slot a different colour every run.</summary>
    public static string Default(string workspace)
    {
        var hash = 2166136261u;
        foreach (var c in workspace) hash = (hash ^ c) * 16777619u;
        return Palette[hash % (uint)Palette.Length];
    }

    /// <summary>Black or white, whichever reads better on <paramref name="background"/> (WCAG relative luminance).</summary>
    public static string Foreground(string background)
    {
        double Channel(int offset)
        {
            var v = int.Parse(background.AsSpan(offset, 2), NumberStyles.HexNumber) / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
        var luminance = 0.2126 * Channel(1) + 0.7152 * Channel(3) + 0.0722 * Channel(5);
        return luminance > 0.179 ? "#000000" : "#ffffff";
    }
}
