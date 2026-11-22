using System.Reflection;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace Kelvin.Hardware;

/// <summary>
/// Keeps a graphics driver update from crashing Kelvin.
///
/// For AMD cards, LibreHardwareMonitor reads sensors from a block of memory the Radeon driver
/// shares with it. When the driver is updated or resets, that memory goes away, and the library's
/// next read of it is a fault that .NET cannot catch: the whole service died (seen on 2026-11-21,
/// one second after a Radeon driver install registered its new driver).
///
/// So before each update of an AMD card, this asks Windows whether that memory is still there.
/// If it isn't, the caller skips the update and arranges a clean restart instead.
///
/// It reaches into the library's private fields to find the address. If a future version renames
/// them, the guard switches itself off (<see cref="Available"/> is false, and a test fails) and
/// behaviour is what it was before: no worse.
/// </summary>
public static class AmdGpuGuard
{
    private const string GpuType = "LibreHardwareMonitor.Hardware.Gpu.AmdGpu";
    private const string LogType = "LibreHardwareMonitor.Interop.AtiAdlxx+ADLPMLogData";

    private static readonly Type? Gpu = typeof(Computer).Assembly.GetType(GpuType);
    private static readonly FieldInfo? Started = Field(Gpu, "_pmLogStarted", typeof(bool));
    private static readonly FieldInfo? Output = Field(Gpu, "_adlPMLogStartOutput", null);
    private static readonly FieldInfo? Address = Field(Output?.FieldType, "pLoggingAddress", typeof(IntPtr));
    private static readonly int LogSize = SizeOf(typeof(Computer).Assembly.GetType(LogType));

    /// <summary>Whether the library still has the shape this guard was written for.</summary>
    public static bool Available => Started is not null && Output is not null && Address is not null && LogSize > 0;

    /// <summary>False when updating this hardware would read memory that is no longer there.</summary>
    public static bool SafeToUpdate(IHardware hardware)
    {
        if (!Available || !OperatingSystem.IsWindows() || hardware.GetType() != Gpu) return true;
        try
        {
            if (Started!.GetValue(hardware) is not true) return true;       // the library isn't using the shared memory
            var address = (IntPtr)Address!.GetValue(Output!.GetValue(hardware))!;
            return Readable(address, LogSize);
        }
        catch (Exception)
        {
            return true; // couldn't tell: behave as before
        }
    }

    /// <summary>Whether every byte in the range is committed memory this process may read.</summary>
    public static bool Readable(IntPtr address, int bytes)
    {
        if (address == IntPtr.Zero || bytes <= 0) return false;
        var at = (long)address;
        var end = at + bytes;
        while (at < end)
        {
            if (VirtualQuery((IntPtr)at, out var region, (nuint)Marshal.SizeOf<MemoryBasicInformation>()) == 0) return false;
            var readable = region.State == Committed && (region.Protect & (NoAccess | Guard)) == 0 && (region.Protect & AnyRead) != 0;
            if (!readable) return false;
            at = (long)region.BaseAddress + (long)region.RegionSize;
        }
        return true;
    }

    private static FieldInfo? Field(Type? type, string name, Type? expected)
    {
        var field = type?.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        return field is not null && (expected is null || field.FieldType == expected) ? field : null;
    }

    private static int SizeOf(Type? type)
    {
        try { return type is null ? 0 : Marshal.SizeOf(type); }
        catch (Exception) { return 0; }
    }

    private const uint Committed = 0x1000, NoAccess = 0x01, Guard = 0x100;
    // PAGE_READONLY, READWRITE, WRITECOPY and their EXECUTE_ forms.
    private const uint AnyRead = 0x02 | 0x04 | 0x08 | 0x20 | 0x40 | 0x80;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll")]
    private static extern nuint VirtualQuery(IntPtr address, out MemoryBasicInformation info, nuint length);
}
