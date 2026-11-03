using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Sprig.Engine.Hosting;

/// <summary>
/// A Windows Job Object that kills every process in it when it's closed or terminated. A runner's process goes in
/// straight after it starts, and the children it spawns later (<c>dotnet run</c>'s app, Vite's workers) join
/// automatically, so stopping a runner can't leave a grandchild holding its port. Elsewhere it falls back to killing the
/// process tree.
/// </summary>
public sealed class JobObject : IDisposable
{
    readonly IntPtr _handle;

    public JobObject()
    {
        if (!OperatingSystem.IsWindows()) return;
        _handle = CreateJobObject(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero) throw new InvalidOperationException($"couldn't create a job object (error {Marshal.GetLastWin32Error()})");

        var info = new ExtendedLimitInformation { Basic = new BasicLimitInformation { LimitFlags = KillOnJobClose } };
        if (!SetInformationJobObject(_handle, ExtendedLimitInformationClass, ref info, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
            throw new InvalidOperationException($"couldn't configure a job object (error {Marshal.GetLastWin32Error()})");
    }

    /// <summary>Put <paramref name="process"/> (and so its future children) in the job.</summary>
    public void Add(Process process)
    {
        if (_handle == IntPtr.Zero) return;
        if (!AssignProcessToJobObject(_handle, process.Handle))
            throw new InvalidOperationException($"couldn't put process {process.Id} in its job (error {Marshal.GetLastWin32Error()})");
    }

    /// <summary>End every process in the job.</summary>
    public void Terminate(Process? fallback)
    {
        if (_handle != IntPtr.Zero) TerminateJobObject(_handle, 1);
        else try { fallback?.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero) CloseHandle(_handle);
    }

    const int ExtendedLimitInformationClass = 9;
    const uint KillOnJobClose = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ExtendedLimitInformation
    {
        public BasicLimitInformation Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimitInformation info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);
}
