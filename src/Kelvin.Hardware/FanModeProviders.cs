using System.ComponentModel;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Kelvin.Core;
using Microsoft.Win32.SafeHandles;

namespace Kelvin.Hardware;

/// <summary>
/// Finds the best way to change fan behaviour on a laptop. Vendor interfaces are only used after a
/// read-only probe succeeds; everything else falls back to Windows power modes, which every laptop has.
/// Returns null on desktops: their fans are controlled one by one instead.
/// </summary>
[SupportedOSPlatform("windows")]
public static class FanModeDetector
{
    public static IFanModeProvider? Detect()
    {
        if (!OperatingSystem.IsWindows() || !IsPortable()) return null;
        var (maker, model, board) = SystemInfo();

        if (maker.Contains("HP", StringComparison.OrdinalIgnoreCase) || maker.Contains("Hewlett", StringComparison.OrdinalIgnoreCase))
        {
            var omen = HpOmenBios.TryOpen(board);
            if (omen is not null)
            {
                IFanModeProvider primary = omen.SupportsThermalProfiles ? new HpOmenThermalModes(omen) : new WindowsPowerModes();
                return new CombinedFanModes(primary, omen.SupportsMaxFan ? new HpOmenMaxFan(omen) : null);
            }
        }
        if (maker.Contains("ASUS", StringComparison.OrdinalIgnoreCase) && AsusAcpiModes.TryOpen() is { } asus) return asus;
        if (maker.Contains("LENOVO", StringComparison.OrdinalIgnoreCase) && LenovoGameZoneModes.TryOpen() is { } lenovo) return lenovo;
        return new WindowsPowerModes();
    }

    /// <summary>Laptops, convertibles and tablets, by chassis type, or anything with a battery.</summary>
    private static bool IsPortable()
    {
        try
        {
            int[] portable = [8, 9, 10, 11, 12, 14, 18, 21, 30, 31, 32];
            using var enclosure = new ManagementObjectSearcher("SELECT ChassisTypes FROM Win32_SystemEnclosure");
            foreach (var o in enclosure.Get())
                if (o["ChassisTypes"] is ushort[] types && types.Any(t => portable.Contains(t))) return true;
            using var battery = new ManagementObjectSearcher("SELECT DeviceID FROM Win32_Battery");
            return battery.Get().Count > 0;
        }
        catch { return false; }
    }

    private static (string Maker, string Model, string Board) SystemInfo()
    {
        string Get(string query, string prop)
        {
            try
            {
                using var s = new ManagementObjectSearcher(query);
                return s.Get().Cast<ManagementBaseObject>().Select(o => o[prop]?.ToString()).FirstOrDefault(v => v is not null)?.Trim() ?? "";
            }
            catch { return ""; }
        }
        return (Get("SELECT Manufacturer FROM Win32_ComputerSystem", "Manufacturer"),
                Get("SELECT Model FROM Win32_ComputerSystem", "Model"),
                Get("SELECT Product FROM Win32_BaseBoard", "Product"));
    }
}

/// <summary>
/// Windows power mode (Settings → Power → Power mode). Works on every laptop: firmware and drivers
/// tie fan behaviour to it. Only applies while the "Balanced" power plan is active, which is the default.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPowerModes : IFanModeProvider
{
    private static readonly Guid BestEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    private static readonly Guid Balanced = Guid.Empty;
    private static readonly Guid BestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    [DllImport("powrprof.dll")] private static extern uint PowerGetEffectiveOverlayScheme(out Guid overlay);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveOverlayScheme(Guid overlay);

    public string Name => "Windows power modes";
    public bool Experimental => false;
    public IReadOnlyList<FanModeOption> Modes { get; } =
    [
        FanModeIds.Option(FanModeIds.Quiet) with { Description = "Windows' \"Best power efficiency\" mode: quieter and cooler." },
        FanModeIds.Option(FanModeIds.Balanced) with { Description = "Windows' \"Balanced\" mode." },
        FanModeIds.Option(FanModeIds.Performance) with { Description = "Windows' \"Best performance\" mode: fans ramp sooner." },
    ];

    public string? Current
    {
        get
        {
            if (PowerGetEffectiveOverlayScheme(out var g) != 0) return null;
            return g == BestEfficiency ? FanModeIds.Quiet : g == Balanced ? FanModeIds.Balanced : g == BestPerformance ? FanModeIds.Performance : null;
        }
    }

    public void Set(string id)
    {
        var g = id switch
        {
            FanModeIds.Quiet => BestEfficiency,
            FanModeIds.Balanced => Balanced,
            FanModeIds.Performance => BestPerformance,
            _ => throw new ArgumentException($"Unknown mode {id}"),
        };
        var rc = PowerSetActiveOverlayScheme(g);
        if (rc != 0) throw new Win32Exception((int)rc, "Windows didn't accept the power mode. Is the Balanced power plan active?");
    }
}

/// <summary>
/// HP's BIOS WMI interface (hpqBIntM), as used by OMEN Gaming Hub and documented by the Linux kernel's
/// hp-wmi driver. Commands go to the "gaming" command group 0x20008.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HpOmenBios
{
    private const uint GamingCommands = 0x20008;
    private const uint GetSystemDesignData = 0x28, SetPerformanceMode = 0x1A, GetMaxFan = 0x26, SetMaxFan = 0x27;

    // Boards where the Linux driver enables thermal profiles, and those that must use the old values.
    private static readonly HashSet<string> ThermalProfileBoards = new(StringComparer.OrdinalIgnoreCase)
    {
        "84DA", "84DB", "84DC", "8574", "8575", "860A", "87B5", "8572", "8573", "8600", "8601", "8602",
        "8605", "8606", "8607", "8746", "8747", "8749", "874A", "8603", "8604", "8748", "886B", "886C",
        "878A", "878B", "878C", "88C8", "88CB", "8786", "8787", "8788", "88D1", "88D2", "88F4", "88FD",
        "88F5", "88F6", "88F7", "88FE", "88FF", "8900", "8901", "8902", "8912", "8917", "8918", "8949",
        "894A", "89EB",
    };
    private static readonly HashSet<string> ForceV0Boards = new(StringComparer.OrdinalIgnoreCase)
    {
        "8607", "8746", "8747", "8749", "874A", "8748",
    };

    public bool SupportsThermalProfiles { get; private init; }
    public bool SupportsMaxFan { get; private init; }
    /// <summary>0: modes 0x00/0x01/0x02. 1: modes 0x30/0x31/0x50.</summary>
    public int ThermalPolicyVersion { get; private init; }

    public static HpOmenBios? TryOpen(string board)
    {
        try
        {
            var probe = new HpOmenBios();
            // Read-only probes. Anything unexpected means "not this interface".
            var maxFan = probe.Query(GetMaxFan, [0, 0, 0, 0], 4);
            var thermal = ThermalProfileBoards.Contains(board);
            var version = 0;
            if (thermal && !ForceV0Boards.Contains(board))
            {
                var design = probe.Query(GetSystemDesignData, new byte[8], 8);
                if (design is null) thermal = false;
                else version = design[3] >= 1 ? 1 : 0;
            }
            if (maxFan is null && !thermal) return null;
            return new HpOmenBios { SupportsMaxFan = maxFan is not null, SupportsThermalProfiles = thermal, ThermalPolicyVersion = version };
        }
        catch { return null; }
    }

    public bool? MaxFanOn() => Query(GetMaxFan, [0, 0, 0, 0], 4) is { } d ? d[0] != 0 : null;

    public void SetMaxFanOn(bool on)
    {
        if (Query(SetMaxFan, [(byte)(on ? 1 : 0)], 0) is null) throw new InvalidOperationException("The BIOS refused the Max fan setting.");
    }

    public void SetThermalMode(byte mode)
    {
        if (Query(SetPerformanceMode, [0, mode], 0) is null) throw new InvalidOperationException("The BIOS refused the fan mode.");
    }

    /// <summary>Calls hpqBIOSInt128. Returns the output bytes, or null if the BIOS reported an error.</summary>
    private byte[]? Query(uint commandType, byte[] input, int outputSize)
    {
        using var inClass = new ManagementClass(@"root\WMI", "hpqBDataIn", null);
        using var inData = inClass.CreateInstance();
        inData["Sign"] = new byte[] { 0x53, 0x45, 0x43, 0x55 }; // "SECU"
        inData["Command"] = GamingCommands;
        inData["CommandType"] = commandType;
        inData["Size"] = (uint)input.Length;
        inData["hpqBData"] = input;

        using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM hpqBIntM");
        using var bios = searcher.Get().Cast<ManagementObject>().FirstOrDefault()
            ?? throw new InvalidOperationException("No HP BIOS interface.");
        using var args = bios.GetMethodParameters("hpqBIOSInt128");
        args["InData"] = inData;
        using var result = bios.InvokeMethod("hpqBIOSInt128", args, null);
        if (result?["OutData"] is not ManagementBaseObject outData) return null;
        if (Convert.ToUInt32(outData["rwReturnCode"]) != 0) return null;
        var data = outData["Data"] as byte[] ?? [];
        return data.Take(Math.Max(outputSize, 0)).ToArray();
    }
}

[SupportedOSPlatform("windows")]
public sealed class HpOmenThermalModes(HpOmenBios bios) : IFanModeProvider
{
    public string Name => "HP Omen fan modes";
    public bool Experimental => true;
    public IReadOnlyList<FanModeOption> Modes { get; } =
        [FanModeIds.Option(FanModeIds.Quiet), FanModeIds.Option(FanModeIds.Balanced), FanModeIds.Option(FanModeIds.Performance)];
    // The BIOS can't report the mode (Linux reads it from the EC), so Kelvin remembers what it set.
    public string? Current { get; private set; }

    public void Set(string id)
    {
        var v1 = bios.ThermalPolicyVersion >= 1;
        byte mode = id switch
        {
            FanModeIds.Quiet => (byte)(v1 ? 0x50 : 0x02),       // "Cool"
            FanModeIds.Balanced => (byte)(v1 ? 0x30 : 0x00),    // "Default"
            FanModeIds.Performance => (byte)(v1 ? 0x31 : 0x01), // "Performance"
            _ => throw new ArgumentException($"Unknown mode {id}"),
        };
        bios.SetThermalMode(mode);
        Current = id;
    }
}

[SupportedOSPlatform("windows")]
public sealed class HpOmenMaxFan(HpOmenBios bios) : IFanModeProvider
{
    public string Name => "HP Max fan";
    public bool Experimental => true;
    public IReadOnlyList<FanModeOption> Modes { get; } = [FanModeIds.Option(FanModeIds.Max)];
    public string? Current => bios.MaxFanOn() == true ? FanModeIds.Max : null;

    /// <summary>"max" turns Max fan on; any other mode turns it off.</summary>
    public void Set(string id) => bios.SetMaxFanOn(id == FanModeIds.Max);
}

/// <summary>
/// ASUS ROG/TUF "throttle thermal policy" through the ATKACPI driver, the interface Armoury Crate uses
/// (device 0x00120075 in Linux's asus-wmi: 0 balanced, 1 turbo, 2 silent).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AsusAcpiModes : IFanModeProvider
{
    private const uint IoControl = 0x0022240C;
    private const uint Dsts = 0x53545344, Devs = 0x53564544; // "DSTS", "DEVS"
    private const uint ThermalPolicy = 0x00120075;
    private const uint PresenceBit = 0x00010000;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, uint inputSize, byte[] output, uint outputSize, out uint returned, IntPtr overlapped);

    public string Name => "ASUS fan modes";
    public bool Experimental => true;
    public IReadOnlyList<FanModeOption> Modes { get; } =
        [FanModeIds.Option(FanModeIds.Quiet), FanModeIds.Option(FanModeIds.Balanced), FanModeIds.Option(FanModeIds.Performance)];
    public string? Current { get; private set; }

    public static AsusAcpiModes? TryOpen()
    {
        try
        {
            var status = Call(Dsts, ThermalPolicy, 0);
            return status is { } s && (s & PresenceBit) != 0 ? new AsusAcpiModes() : null;
        }
        catch { return null; }
    }

    public void Set(string id)
    {
        uint value = id switch
        {
            FanModeIds.Quiet => 2,
            FanModeIds.Balanced => 0,
            FanModeIds.Performance => 1,
            _ => throw new ArgumentException($"Unknown mode {id}"),
        };
        if (Call(Devs, ThermalPolicy, value) is null) throw new InvalidOperationException("The ASUS firmware refused the fan mode.");
        Current = id;
    }

    private static uint? Call(uint method, uint device, uint value)
    {
        using var h = CreateFile(@"\\.\ATKACPI", 0xC0000000, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
        if (h.IsInvalid) return null;
        // [method][args length][device id][value]
        var input = new byte[16];
        BitConverter.GetBytes(method).CopyTo(input, 0);
        BitConverter.GetBytes(8u).CopyTo(input, 4);
        BitConverter.GetBytes(device).CopyTo(input, 8);
        BitConverter.GetBytes(value).CopyTo(input, 12);
        var output = new byte[16];
        return DeviceIoControl(h, IoControl, input, (uint)input.Length, output, (uint)output.Length, out _, IntPtr.Zero)
            ? BitConverter.ToUInt32(output, 0)
            : null;
    }
}

/// <summary>Lenovo Legion "smart fan mode" through the LENOVO_GAMEZONE_DATA WMI class (1 quiet, 2 balanced, 3 performance).</summary>
[SupportedOSPlatform("windows")]
public sealed class LenovoGameZoneModes : IFanModeProvider
{
    public string Name => "Lenovo Legion fan modes";
    public bool Experimental => true;
    public IReadOnlyList<FanModeOption> Modes { get; } =
        [FanModeIds.Option(FanModeIds.Quiet), FanModeIds.Option(FanModeIds.Balanced), FanModeIds.Option(FanModeIds.Performance)];

    public static LenovoGameZoneModes? TryOpen()
    {
        try { return Instance() is not null && Read() is not null ? new LenovoGameZoneModes() : null; }
        catch { return null; }
    }

    public string? Current => Read() switch { 1 => FanModeIds.Quiet, 2 => FanModeIds.Balanced, 3 => FanModeIds.Performance, _ => null };

    public void Set(string id)
    {
        uint value = id switch
        {
            FanModeIds.Quiet => 1,
            FanModeIds.Balanced => 2,
            FanModeIds.Performance => 3,
            _ => throw new ArgumentException($"Unknown mode {id}"),
        };
        using var gz = Instance() ?? throw new InvalidOperationException("No Lenovo GameZone interface.");
        using var args = gz.GetMethodParameters("SetSmartFanMode");
        args["Data"] = value;
        gz.InvokeMethod("SetSmartFanMode", args, null);
    }

    private static uint? Read()
    {
        using var gz = Instance();
        if (gz is null) return null;
        using var result = gz.InvokeMethod("GetSmartFanMode", null, null);
        return result?["Data"] is { } d ? Convert.ToUInt32(d) : null;
    }

    private static ManagementObject? Instance()
    {
        using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM LENOVO_GAMEZONE_DATA");
        return searcher.Get().Cast<ManagementObject>().FirstOrDefault();
    }
}
