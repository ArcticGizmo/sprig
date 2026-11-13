using System.Runtime.InteropServices;

namespace Sprig.Engine.Hosting;

/// <summary>The whole machine at one moment, for setting what sprig runs against everything else.</summary>
/// <param name="Cpu">Percent busy across every core since the last sample; null on the first, or where it can't tell.</param>
/// <param name="MemoryUsed">Bytes of RAM in use by everything; null where it can't tell.</param>
/// <param name="MemoryTotal">Bytes of RAM the machine has.</param>
public sealed record MachineSnapshot(double? Cpu, long? MemoryUsed, long MemoryTotal);

/// <summary>Samples the machine's CPU and memory. CPU is measured between one sample and the next, so one instance is
/// kept and sampled on a steady beat.</summary>
public sealed class MachineUsage
{
    (long Idle, long Busy)? _last;

    public MachineSnapshot Sample()
    {
        if (!OperatingSystem.IsWindows()) return new MachineSnapshot(null, null, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);

        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        var memory = GlobalMemoryStatusEx(ref status);
        double? cpu = null;
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            // Kernel time includes idle time, so busy is kernel and user without it.
            var now = (Idle: idle, Busy: kernel + user - idle);
            if (_last is { } last && (now.Idle - last.Idle) + (now.Busy - last.Busy) is > 0 and var span)
                cpu = Math.Clamp((now.Busy - last.Busy) * 100.0 / span, 0, 100);
            _last = now;
        }
        return memory
            ? new MachineSnapshot(cpu, (long)(status.TotalPhys - status.AvailPhys), (long)status.TotalPhys)
            : new MachineSnapshot(cpu, null, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
}
