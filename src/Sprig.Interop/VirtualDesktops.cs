using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Sprig.Interop;

/// <summary>
/// Which Windows virtual desktop a window is on, through the documented <c>IVirtualDesktopManager</c>. The release
/// build is trimmed, which turns off the runtime's built-in COM, so the interface is source-generated.
/// </summary>
public static partial class VirtualDesktops
{
    static readonly Guid ManagerClass = new("aa509086-5ca9-4c25-8f95-589d3c07b48a");
    static readonly Guid ManagerInterface = new("a5cd92ff-29be-454c-8d04-d82879fb3f1b");
    const uint InProcServer = 1;

    static IVirtualDesktopManager? _manager;
    static bool _tried;

    /// <summary>Whether the top-level window is on the desktop being looked at; null where that can't be told (not
    /// Windows, or no handle), so the caller falls back to what it did before desktops.</summary>
    public static bool? IsOnCurrent(nint window)
    {
        if (window == 0 || Manager() is not { } manager) return null;
        try
        {
            return manager.IsWindowOnCurrentVirtualDesktop(window, out var on) == 0 ? on : null;
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
            return null;
        }
    }

    static IVirtualDesktopManager? Manager()
    {
        if (_tried) return _manager;
        _tried = true;
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            if (CoCreateInstance(ManagerClass, 0, InProcServer, ManagerInterface, out var pointer) != 0 || pointer == 0) return null;
            try
            {
                _manager = (IVirtualDesktopManager)new StrategyBasedComWrappers().GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.None);
            }
            finally
            {
                Marshal.Release(pointer);
            }
        }
        catch (Exception e) when (e is COMException or DllNotFoundException or EntryPointNotFoundException or InvalidCastException)
        {
            _manager = null;
        }
        return _manager;
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid classId, nint outer, uint context, in Guid interfaceId, out nint instance);
}

[GeneratedComInterface]
[Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b")]
internal partial interface IVirtualDesktopManager
{
    [PreserveSig]
    int IsWindowOnCurrentVirtualDesktop(nint window, [MarshalAs(UnmanagedType.Bool)] out bool onCurrentDesktop);

    [PreserveSig]
    int GetWindowDesktopId(nint window, out Guid desktopId);

    [PreserveSig]
    int MoveWindowToDesktop(nint window, in Guid desktopId);
}
