using System.Diagnostics;

namespace Kelvin.Core;

/// <summary>
/// Renders the chosen effect for every included device and pushes frames to the RGB provider.
/// Static colors are sent once; animated effects run at 30 fps. Only changed frames are sent.
/// </summary>
public sealed class LightingEngine
{
    private const int PreviewLeds = 24;

    // Temperature effect: a cool-to-hot scale, like a blackbody running from blue sky to ember.
    private static readonly (double Temp, RgbColor Color)[] ThermalScale =
    [
        (35, new RgbColor(0x3d, 0x7b, 0xff)),
        (50, new RgbColor(0x9e, 0xc8, 0xff)),
        (62, new RgbColor(0xff, 0xe2, 0xb8)),
        (72, new RgbColor(0xff, 0x9a, 0x3d)),
        (85, new RgbColor(0xff, 0x2e, 0x1f)),
    ];

    private readonly IRgbProvider _rgb;
    private readonly SettingsStore _store;
    private readonly Func<IReadOnlyList<SensorReading>> _sensors;
    private readonly Dictionary<string, RgbColor[]> _frames = new();
    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastRender;

    /// <summary>Colors from the screen, fed by the desktop app.</summary>
    public ScreenSync Screen { get; } = new();

    public LightingEngine(IRgbProvider rgb, SettingsStore store, Func<IReadOnlyList<SensorReading>> sensors)
    {
        _rgb = rgb;
        _store = store;
        _sensors = sensors;
    }

    /// <summary>Downsampled current colors per device, for the UI preview.</summary>
    public IReadOnlyDictionary<string, string[]> Preview
    {
        get
        {
            lock (_gate)
            {
                return _frames.ToDictionary(kv => kv.Key, kv =>
                {
                    var f = kv.Value;
                    if (f.Length <= PreviewLeds) return f.Select(c => c.Hex).ToArray();
                    return Enumerable.Range(0, PreviewLeds).Select(i => f[i * f.Length / PreviewLeds].Hex).ToArray();
                });
            }
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(33));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try { Render(); }
            catch (Exception) { /* a device unplugged mid-frame must not kill the loop */ }
        }
    }

    public void Invalidate()
    {
        lock (_gate) _frames.Clear();
    }

    private void Render()
    {
        if (!_rgb.IsConnected) return;
        var cfg = _store.Read(s => s.Lighting);
        var t = _clock.Elapsed.TotalSeconds;
        var baseColor = SafeColor(cfg.Color);
        var brightness = Math.Clamp(cfg.Brightness, 0, 1);
        // Screen sync needs the desktop app running. Without it, show the plain color.
        var screen = cfg.Effect == LightingEffect.Screen && Screen.Receiving(DateTimeOffset.Now);
        if (screen) Screen.Step(t - _lastRender);
        _lastRender = t;

        foreach (var device in _rgb.Devices)
        {
            // Excluded devices are left entirely alone, so other software can drive them.
            if (cfg.ExcludedDeviceIds.Contains(device.Id))
            {
                lock (_gate) _frames.Remove(device.Id);
                continue;
            }

            var frame = new RgbColor[device.LedCount];
            for (var i = 0; i < frame.Length; i++)
            {
                frame[i] = cfg.Effect switch
                {
                    LightingEffect.Off => RgbColor.Black,
                    LightingEffect.Static => baseColor.Scale(brightness),
                    LightingEffect.Breathing => baseColor.Scale(brightness * (0.12 + 0.88 * (0.5 - 0.5 * Math.Cos(2 * Math.PI * t * (0.1 + cfg.Speed * 0.5))))),
                    LightingEffect.Spectrum => RgbColor.FromHsv(t * (10 + cfg.Speed * 80) + i * 360.0 / Math.Max(frame.Length, 1) * 0.5, 1, brightness),
                    LightingEffect.Temperature => Thermal(cfg.TemperatureSensorId).Scale(brightness),
                    LightingEffect.Screen when !screen => baseColor.Scale(brightness),
                    LightingEffect.Screen => (cfg.ScreenSpread && frame.Length > 1
                        ? Screen.Across((i + 0.5) / frame.Length) : Screen.Whole()).Scale(brightness),
                    _ => RgbColor.Black,
                };
            }

            // Per-device colors, then per-zone colors on top (a zone's own setting wins).
            if (cfg.Devices.TryGetValue(device.Id, out var own))
            {
                Paint(frame, 0, frame.Length, own, brightness);
                var start = 0;
                foreach (var zone in device.Zones)
                {
                    if (own.Zones.TryGetValue(zone.Name, out var zoneOwn))
                        Paint(frame, start, Math.Min(zone.LedCount, frame.Length - start), zoneOwn, brightness);
                    start += zone.LedCount;
                    if (start >= frame.Length) break;
                }
            }

            lock (_gate)
            {
                if (_frames.TryGetValue(device.Id, out var prev) && prev.SequenceEqual(frame)) continue;
                _frames[device.Id] = frame;
            }
            _rgb.SetColors(device.Id, frame);
        }
    }

    /// <summary>
    /// Applies one device's or zone's own colors to a run of LEDs. LEDs are ordered zone by zone,
    /// so a zone is a contiguous run. Gradients go from the run's first LED to its last.
    /// </summary>
    public static void Paint(RgbColor[] frame, int start, int count, ColorOverride own, double brightness)
    {
        if (own.Mode == ColorMode.Follow || count <= 0) return;
        var a = SafeColor(own.Color);
        var b = SafeColor(own.Color2);
        for (var i = 0; i < count; i++)
        {
            frame[start + i] = own.Mode switch
            {
                ColorMode.Solid => a.Scale(brightness),
                ColorMode.Gradient => RgbColor.Lerp(a, b, count == 1 ? 0 : (double)i / (count - 1)).Scale(brightness),
                _ => RgbColor.Black,
            };
        }
    }

    private RgbColor Thermal(string sensorId)
    {
        var temp = _sensors().FirstOrDefault(s => s.Id == sensorId)?.Value ?? 40;
        return ThermalColor(temp);
    }

    public static RgbColor ThermalColor(double temp)
    {
        if (temp <= ThermalScale[0].Temp) return ThermalScale[0].Color;
        for (var i = 1; i < ThermalScale.Length; i++)
        {
            var (a, b) = (ThermalScale[i - 1], ThermalScale[i]);
            if (temp <= b.Temp) return RgbColor.Lerp(a.Color, b.Color, (temp - a.Temp) / (b.Temp - a.Temp));
        }
        return ThermalScale[^1].Color;
    }

    private static RgbColor SafeColor(string hex)
    {
        try { return RgbColor.FromHex(hex); } catch { return new RgbColor(255, 255, 255); }
    }
}
