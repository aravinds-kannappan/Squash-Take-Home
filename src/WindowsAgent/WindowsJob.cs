using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Squash.Agent;

// A Windows Job Object closes over child processes and kills them on service exit.
public sealed class WindowsJob : IDisposable
{
    readonly SafeFileHandle handle;
    WindowsJob(SafeFileHandle handle) => this.handle = handle;
    public static WindowsJob Attach(Process process)
    {
        var h = CreateJobObject(IntPtr.Zero, null);
        if (h.IsInvalid) { process.Kill(true); throw new Win32Exception(); }
        var info = new ExtendedLimit { Basic = new BasicLimit { LimitFlags = 0x2000 } };
        if (!SetInformationJobObject(h, 9, ref info, (uint)Marshal.SizeOf<ExtendedLimit>()) || !AssignProcessToJobObject(h, process.Handle))
        { h.Dispose(); try { process.Kill(true); } catch (InvalidOperationException) { } throw new Win32Exception(); }
        return new WindowsJob(h);
    }
    public void Dispose() => handle.Dispose();
    [StructLayout(LayoutKind.Sequential)] struct BasicLimit
    { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] struct ExtendedLimit
    { public BasicLimit Basic; public IoCounters Io; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimit info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
