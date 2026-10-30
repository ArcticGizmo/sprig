using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Sprig.Core.Workspaces;

/// <summary>
/// Directory links that point one path at another. On Windows these are NTFS junctions, which need neither
/// admin rights nor Developer Mode (a directory symlink needs one of them); elsewhere a plain symlink. Created
/// through the filesystem API rather than <c>mklink</c>, so no shell is spawned per link.
/// </summary>
internal static class DirectoryLink
{
    /// <summary>Create a link at <paramref name="link"/> pointing at the existing directory <paramref name="target"/>.</summary>
    public static void Create(string link, string target)
    {
        var fullTarget = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, fullTarget);
            return;
        }

        // A junction is an empty directory carrying a mount-point reparse buffer.
        Directory.CreateDirectory(link);
        try
        {
            using var handle = CreateFileW(link, GenericWrite, 0, IntPtr.Zero, OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            var buffer = MountPointBuffer(fullTarget);
            if (!DeviceIoControl(handle, FsctlSetReparsePoint, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch
        {
            try { Directory.Delete(link); } catch { /* leave the empty directory rather than mask the real error */ }
            throw;
        }
    }

    /// <summary>True if <paramref name="path"/> itself is a link (junction or symlink), whether or not its target exists.</summary>
    public static bool IsLink(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 && Target(path) is not null; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>True if anything (link, directory or file) exists at <paramref name="path"/>. Unlike
    /// <see cref="Directory.Exists"/>, a link whose target is gone still counts.</summary>
    public static bool Occupied(string path)
    {
        try { File.GetAttributes(path); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return true; }
    }

    /// <summary>The absolute path a link points at, or null when <paramref name="path"/> is not a link.</summary>
    public static string? Target(string path)
    {
        var target = new DirectoryInfo(path).LinkTarget;
        if (target is null) return null;
        if (target.StartsWith(@"\??\", StringComparison.Ordinal)) target = target[4..];
        var parent = Path.GetDirectoryName(Path.GetFullPath(path))!;
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(target, parent));
    }

    /// <summary>True if <paramref name="link"/> is a link pointing at <paramref name="target"/>.</summary>
    public static bool PointsTo(string link, string target)
        => IsLink(link) && Target(link) is { } actual && WorktreeInspector.PathEquals(actual, target);

    /// <summary>
    /// Remove the link at <paramref name="path"/> without touching what it points at. Does nothing (and returns
    /// false) when <paramref name="path"/> is not a link, so a real directory that happens to sit there is never
    /// deleted by mistake.
    /// </summary>
    public static bool Remove(string path)
    {
        if (!IsLink(path)) return false;
        // Non-recursive on purpose: removing a junction's directory entry removes the link alone.
        if (OperatingSystem.IsWindows()) Directory.Delete(path);
        else File.Delete(path);
        return true;
    }

    static byte[] MountPointBuffer(string target)
    {
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + target);
        var print = Encoding.Unicode.GetBytes(target);
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write(IoReparseTagMountPoint);
        w.Write((ushort)(8 + substitute.Length + 2 + print.Length + 2)); // ReparseDataLength
        w.Write((ushort)0);                                             // Reserved
        w.Write((ushort)0);                                             // SubstituteNameOffset
        w.Write((ushort)substitute.Length);
        w.Write((ushort)(substitute.Length + 2));                       // PrintNameOffset
        w.Write((ushort)print.Length);
        w.Write(substitute);
        w.Write((ushort)0);
        w.Write(print);
        w.Write((ushort)0);
        return stream.ToArray();
    }

    const uint GenericWrite = 0x40000000;
    const uint OpenExisting = 3;
    const uint FileFlagBackupSemantics = 0x02000000;
    const uint FileFlagOpenReparsePoint = 0x00200000;
    const uint FsctlSetReparsePoint = 0x000900A4;
    const uint IoReparseTagMountPoint = 0xA0000003;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] inBuffer, int inSize,
        IntPtr outBuffer, int outSize, out int returned, IntPtr overlapped);
}
