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
    readonly Lock _gate = new();
    bool _disposed;

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

    /// <summary>The processes in the job now; empty when it can't tell (not Windows).</summary>
    public IReadOnlyList<int> ProcessIds()
    {
        if (_handle == IntPtr.Zero) return [];
        const int capacity = 512;
        var size = 8 + IntPtr.Size * capacity;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryInformationJobObject(_handle, BasicProcessIdListClass, buffer, (uint)size, IntPtr.Zero)) return [];
            var count = Marshal.ReadInt32(buffer, 4);
            var ids = new int[count];
            for (var i = 0; i < count; i++) ids[i] = (int)Marshal.ReadIntPtr(buffer, 8 + i * IntPtr.Size);
            return ids;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// CPU time used by every process that has ever been in the job (so a child that exits doesn't make the total drop),
    /// and the working set of those in it now. Null when it can't tell (not Windows).
    /// </summary>
    public (TimeSpan Cpu, long Memory, int Processes)? Usage()
    {
        // Measured from another thread than the one that disposes, and a closed handle's number can be handed out again.
        lock (_gate)
        {
            if (_handle == IntPtr.Zero || _disposed) return null;
            if (!QueryInformationJobObject(_handle, BasicAccountingInformationClass, out var accounting, (uint)Marshal.SizeOf<BasicAccountingInformation>(), IntPtr.Zero))
                return null;
            var ids = ProcessIds();
            long memory = 0;
            foreach (var id in ids) memory += WorkingSetOf(id);
            return (TimeSpan.FromTicks(accounting.TotalUserTime + accounting.TotalKernelTime), memory, ids.Count);
        }
    }

    /// <summary>Zero for one that has gone, or that can't be looked at.</summary>
    static long WorkingSetOf(int id)
    {
        var process = OpenProcess(QueryLimitedInformation, false, (uint)id);
        if (process == IntPtr.Zero) return 0;
        try
        {
            var counters = new MemoryCounters { Size = (uint)Marshal.SizeOf<MemoryCounters>() };
            return GetProcessMemoryInfo(process, ref counters, counters.Size) ? (long)counters.WorkingSetSize : 0;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>End every process in the job.</summary>
    public void Terminate(Process? fallback)
    {
        if (_handle != IntPtr.Zero) TerminateJobObject(_handle, 1);
        else try { fallback?.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_handle == IntPtr.Zero || _disposed) return;
            _disposed = true;
            CloseHandle(_handle);
        }
    }

    const int ExtendedLimitInformationClass = 9;
    const int BasicAccountingInformationClass = 1;
    const int BasicProcessIdListClass = 3;
    const uint KillOnJobClose = 0x2000;
    const uint QueryLimitedInformation = 0x1000;

    /// <summary>Times are in 100 ns ticks, the same unit as <see cref="TimeSpan"/>'s.</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct BasicAccountingInformation
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryCounters
    {
        public uint Size;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage;
        public UIntPtr QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage;
        public UIntPtr QuotaNonPagedPoolUsage;
        public UIntPtr PagefileUsage;
        public UIntPtr PeakPagefileUsage;
    }

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
    static extern bool QueryInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool QueryInformationJobObject(IntPtr job, int infoClass, out BasicAccountingInformation info, uint length, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo", SetLastError = true)]
    static extern bool GetProcessMemoryInfo(IntPtr process, ref MemoryCounters counters, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);
}
