using System.Runtime.InteropServices;
using System.Text;

namespace Sprig.Engine.Store;

/// <summary>
/// Expands Windows 8.3 short names (<c>C:\Users\ABCDEF~1\…</c>, common in <c>%TEMP%</c>) to the long form. Tools like
/// Vite refuse to serve a project whose root is spelled differently from the path they resolve, so every path sprig
/// hands to an app goes through here.
/// </summary>
public static class LongPath
{
    public static string Of(string path)
    {
        var full = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows() || !full.Contains('~')) return full;

        var buffer = new StringBuilder(1024);
        var length = GetLongPathNameW(full, buffer, (uint)buffer.Capacity);
        return length > 0 && length < buffer.Capacity ? buffer.ToString() : full;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetLongPathNameW(string shortPath, StringBuilder longPath, uint bufferLength);
}
