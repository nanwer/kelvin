using System.Management;
using Kelvin.Core;

namespace Kelvin.Hardware;

/// <summary>
/// The four-zone keyboard backlight on HP OMEN (and some Victus) laptops, set through HP's BIOS WMI
/// interface, the same one the fan modes use. OpenRGB only reaches this keyboard on models without
/// a number pad, so Kelvin drives it itself.
///
/// The protocol is the one HP's own software uses, as worked out by OmenMon, OpenRGB and the
/// hp-omen-linux-module project, which all agree: command group 0x20009, a 128-byte color table
/// whose first byte is the number of zones minus one, with the zones' colors as R, G, B from byte
/// 25, in the order right, middle, left, WASD.
///
/// It is used only after three read-only checks pass: the keyboard is not a per-key one (those
/// answer these calls but ignore them), the BIOS says there is a backlight, and the color table
/// reports four zones (a single-color keyboard reports one). Experimental until confirmed on real
/// machines: nobody has yet reported what each OMEN model answers.
/// </summary>
public sealed class HpOmenKeyboard : IRgbProvider
{
    public const string DeviceId = "hp-omen/keyboard";
    public const int TableSize = 128, FirstZone = 25, ZoneCount = 4;
    private const uint Gaming = 0x20008, Keyboard = 0x20009;
    private const uint GetKeyboardType = 0x2B, HasBacklight = 0x01, GetColors = 0x02, SetColors_ = 0x03, GetBacklight = 0x04, SetBacklight = 0x05;
    private const byte PerKey = 0x03, BacklightOff = 0x64, BacklightOn = 0xE4;
    /// <summary>
    /// Every change is a call into the BIOS, so animated effects are sent at most this often. Five
    /// a second keeps Breathing and Screen sync fluid enough without leaning on the firmware.
    /// </summary>
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(200);

    // Kelvin lists the zones as they sit on the keyboard, left to right, then the WASD keys.
    // The BIOS numbers them right (0), middle (1), left (2), WASD (3).
    private static readonly int[] BiosZone = [2, 1, 0, 3];
    private static readonly RgbDevice Device = new(DeviceId, "OMEN Keyboard", "HP", "Keyboard", ZoneCount,
        [new("Left", 1), new("Middle", 1), new("Right", 1), new("WASD keys", 1)]);

    private readonly object _gate = new();
    private readonly Func<uint, uint, byte[], int, byte[]?> _bios;
    private byte[]? _table;          // the color table as last read or written
    private RgbColor[]? _pending;    // the newest colors waiting to be sent
    private bool _probed, _connected, _litOnce;
    private int _failures;
    private Thread? _worker;
    private volatile bool _stop;

    private readonly Func<bool>? _drivenElsewhere;

    /// <param name="drivenElsewhere">
    /// True while another backend (OpenRGB, on the models it supports) is already showing this
    /// keyboard. Kelvin then stays out of it, so the keyboard isn't listed and driven twice.
    /// </param>
    public HpOmenKeyboard(Func<bool>? drivenElsewhere = null) : this(HpBios.Call, drivenElsewhere) { }

    /// <summary>For tests: a stand-in for the BIOS (command, type, input, output size) → output or null.</summary>
    public HpOmenKeyboard(Func<uint, uint, byte[], int, byte[]?> bios, Func<bool>? drivenElsewhere = null)
    {
        _bios = bios;
        _drivenElsewhere = drivenElsewhere;
    }

    public string Name => "HP keyboard";
    public bool IsSimulated => false;
    public bool IsConnected => _connected;
    public string? Status { get; private set; }
    public IReadOnlyList<RgbDevice> Devices => _connected && _drivenElsewhere?.Invoke() != true ? [Device] : [];

    /// <summary>Looks for the keyboard, once. A PC without one is never asked again.</summary>
    public void Open()
    {
        lock (_gate)
        {
            if (_probed) return;
            _probed = true;
            try
            {
                // The type call is allowed to fail (then it isn't a per-key keyboard as far as HP's own check goes).
                if (_bios(Gaming, GetKeyboardType, new byte[4], 4) is { Length: > 0 } type && type[0] == PerKey) return;
                if (_bios(Keyboard, HasBacklight, new byte[4], 4) is not { Length: > 0 } has || (has[0] & 1) == 0) return;
                if (_bios(Keyboard, GetColors, new byte[4], TableSize) is not { Length: TableSize } table || table[0] != ZoneCount - 1) return;
                _table = table;
                _connected = true;
                _worker = new Thread(Work) { IsBackground = true, Name = "HP keyboard lighting" };
                _worker.Start();
            }
            catch (Exception) { /* not an HP laptop, or not this interface */ }
        }
    }

    public void Refresh() { }

    public void SetColors(string deviceId, IReadOnlyList<RgbColor> colors)
    {
        if (deviceId != DeviceId || colors.Count < ZoneCount) return;
        // Only remembered here: the BIOS call happens on the worker, so a slow firmware never
        // holds up the lighting of other devices. If several arrive in between, the newest wins.
        lock (_gate) { _pending = [.. colors.Take(ZoneCount)]; Monitor.Pulse(_gate); }
    }

    private void Work()
    {
        while (!_stop)
        {
            RgbColor[] colors;
            lock (_gate)
            {
                while (_pending is null && !_stop) Monitor.Wait(_gate, 1000);
                if (_stop) return;
                colors = _pending!;
                _pending = null;
            }
            try
            {
                Send(colors);
                _failures = 0;
            }
            catch (Exception)
            {
                // A keyboard that keeps refusing is dropped, so the app says so instead of pretending.
                if (++_failures >= 5) { _connected = false; Status = "The laptop's BIOS stopped accepting keyboard colors."; return; }
            }
            Thread.Sleep(MinInterval);
        }
    }

    private void Send(RgbColor[] colors)
    {
        var table = Patch(_table!, colors);
        if (_bios(Keyboard, SetColors_, table, 0) is null) throw new InvalidOperationException("The BIOS refused the keyboard colors.");
        _table = table;

        // Colors don't show while the backlight is switched off. Kelvin switches it on the first
        // time it has something to show, and only then: after that the keyboard's own backlight
        // key stays in charge, so switching it off there stays off.
        if (!_litOnce && colors.Any(c => c != RgbColor.Black))
        {
            _litOnce = true;
            if (_bios(Keyboard, GetBacklight, new byte[4], 4) is { Length: > 0 } state && state[0] == BacklightOff)
                _bios(Keyboard, SetBacklight, [BacklightOn, 0, 0, 0], 0);
        }
    }

    /// <summary>
    /// The color table with these four colors in it (left, middle, right, WASD). Everything else in
    /// the table is kept as the BIOS gave it: nobody knows what the other bytes mean.
    /// </summary>
    public static byte[] Patch(byte[] table, IReadOnlyList<RgbColor> colors)
    {
        var next = (byte[])table.Clone();
        for (var i = 0; i < ZoneCount; i++)
        {
            var at = FirstZone + BiosZone[i] * 3;
            (next[at], next[at + 1], next[at + 2]) = (colors[i].R, colors[i].G, colors[i].B);
        }
        return next;
    }

    public void Dispose()
    {
        _stop = true;
        lock (_gate) Monitor.PulseAll(_gate);
    }
}

/// <summary>One call into HP's BIOS WMI interface (class hpqBIntM in root\WMI).</summary>
internal static class HpBios
{
    private static bool? _isHp;

    /// <summary>
    /// Returns the output bytes, or null when the BIOS reports an error or this isn't an HP PC.
    /// The method called depends on how much output is expected (hpqBIOSInt0, 4 or 128), as HP's
    /// own software does it.
    /// </summary>
    public static byte[]? Call(uint command, uint commandType, byte[] input, int outputSize)
    {
        if (!OperatingSystem.IsWindows() || !IsHp()) return null;
        var method = $"hpqBIOSInt{outputSize}";
        using var inClass = new ManagementClass(@"root\WMI", "hpqBDataIn", null);
        using var inData = inClass.CreateInstance();
        inData["Sign"] = new byte[] { 0x53, 0x45, 0x43, 0x55 }; // "SECU"
        inData["Command"] = command;
        inData["CommandType"] = commandType;
        inData["Size"] = (uint)input.Length;
        inData["hpqBData"] = input;

        using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM hpqBIntM");
        using var bios = searcher.Get().Cast<ManagementObject>().FirstOrDefault();
        if (bios is null) return null;
        using var args = bios.GetMethodParameters(method);
        args["InData"] = inData;
        using var result = bios.InvokeMethod(method, args, null);
        if (result?["OutData"] is not ManagementBaseObject outData) return null;
        if (Convert.ToUInt32(outData["rwReturnCode"]) != 0) return null;
        if (outputSize == 0) return [];
        return (outData["Data"] as byte[] ?? []).Take(outputSize).ToArray();
    }

    /// <summary>Asked once: other makers' PCs have no such interface, and looking for it each time is slow.</summary>
    private static bool IsHp()
    {
        if (_isHp is { } known) return known;
        try
        {
            if (!OperatingSystem.IsWindows()) return false;
            using var s = new ManagementObjectSearcher("SELECT Manufacturer FROM Win32_ComputerSystem");
            var maker = s.Get().Cast<ManagementBaseObject>().Select(o => o["Manufacturer"]?.ToString()).FirstOrDefault() ?? "";
            _isHp = maker.Contains("HP", StringComparison.OrdinalIgnoreCase) || maker.Contains("Hewlett", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { _isHp = false; }
        return _isHp.Value;
    }
}
