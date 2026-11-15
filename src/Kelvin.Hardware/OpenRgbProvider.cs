using Kelvin.Core;
using OpenRGB.NET;

namespace Kelvin.Hardware;

/// <summary>
/// Drives lighting through OpenRGB's SDK server (OpenRGB must be running with "Start server" on).
/// Talking to it over its network protocol keeps Kelvin's code separate from OpenRGB's GPL code.
/// </summary>
public sealed class OpenRgbProvider(string host = "127.0.0.1", int port = 6742) : IRgbProvider
{
    private OpenRgbClient? _client;
    private Device[] _raw = [];
    private DateTime _nextRetry = DateTime.MinValue;
    private readonly object _gate = new();

    public string Name => "OpenRGB";
    public bool IsSimulated => false;
    public bool IsConnected => _client?.Connected == true;
    public string? Status { get; private set; }
    public IReadOnlyList<RgbDevice> Devices { get; private set; } = [];

    public void Open()
    {
        lock (_gate)
        {
            if (IsConnected || DateTime.UtcNow < _nextRetry) return;
            try
            {
                _client?.Dispose();
                _client = new OpenRgbClient(host, port, "Kelvin", true, 1000);
                LoadDevices();
                Status = null;
            }
            catch (Exception)
            {
                Status = $"Can't reach OpenRGB on port {port}.";
                Devices = [];
                _nextRetry = DateTime.UtcNow.AddSeconds(5);
            }
        }
    }

    private void LoadDevices()
    {
        _raw = _client!.GetAllControllerData();
        // Direct (custom) mode lets Kelvin set every LED itself.
        foreach (var d in _raw) _client.SetCustomMode(d.Index);
        Devices = _raw.Select(d => new RgbDevice(
            $"openrgb/{d.Index}/{d.Name}",
            d.Name,
            d.Vendor ?? "",
            KindName(d.Type),
            d.Leds.Length,
            d.Zones.Select(z => new RgbZone(z.Name, (int)z.LedCount)).ToList())).ToList();
    }

    public void Refresh()
    {
        lock (_gate)
        {
            if (!IsConnected) return;
            try
            {
                if (_client!.GetControllerCount() != _raw.Length) LoadDevices();
            }
            catch (Exception)
            {
                _client?.Dispose();
                _client = null;
            }
        }
    }

    public void SetColors(string deviceId, IReadOnlyList<RgbColor> colors)
    {
        lock (_gate)
        {
            if (!IsConnected) { Open(); return; }
            var device = _raw.FirstOrDefault(d => $"openrgb/{d.Index}/{d.Name}" == deviceId);
            if (device is null) return;
            var buffer = new Color[colors.Count];
            for (var i = 0; i < buffer.Length; i++) buffer[i] = new Color(colors[i].R, colors[i].G, colors[i].B);
            try { _client!.UpdateLeds(device.Index, buffer); }
            catch (Exception) { Status = "Lost connection to OpenRGB. Reconnecting."; _client?.Dispose(); _client = null; }
        }
    }

    private static string KindName(DeviceType t) => t switch
    {
        DeviceType.Motherboard => "Motherboard",
        DeviceType.Dram => "Memory",
        DeviceType.Gpu => "Graphics card",
        DeviceType.Cooler => "Cooler",
        DeviceType.Ledstrip => "LED strip",
        DeviceType.Keyboard => "Keyboard",
        DeviceType.Mouse => "Mouse",
        DeviceType.Mousemat => "Mousepad",
        DeviceType.Headset => "Headset",
        DeviceType.HeadsetStand => "Headset stand",
        DeviceType.Gamepad => "Controller",
        DeviceType.Speaker => "Speaker",
        _ => "Device",
    };

    public void Dispose() => _client?.Dispose();
}
