using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Kelvin.Hardware;

/// <summary>
/// Ties child processes to this one: Windows ends them the moment this process goes away, however
/// it goes (a clean stop, a crash, or being killed). Without it a crash would leave OpenRGB running
/// with nobody in charge of it, holding its files open so an update can't replace them.
/// </summary>
public sealed class ChildJob : IDisposable
{
    private IntPtr _job;

    public ChildJob()
    {
        if (!OperatingSystem.IsWindows()) return;
        _job = CreateJobObjectW(IntPtr.Zero, null);
        if (_job == IntPtr.Zero) return;
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = KillOnJobClose;
        if (!SetInformationJobObject(_job, ExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            CloseHandle(_job);
            _job = IntPtr.Zero;
        }
    }

    /// <summary>False when the process couldn't be tied to this one; it still runs, just untied.</summary>
    public bool Add(Process process)
    {
        if (_job == IntPtr.Zero) return false;
        try { return AssignProcessToJobObject(_job, process.Handle); }
        catch { return false; }
    }

    public void Dispose()
    {
        if (_job == IntPtr.Zero) return;
        CloseHandle(_job); // ends every process still in the job
        _job = IntPtr.Zero;
    }

    private const uint KillOnJobClose = 0x2000;
    private const int ExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
