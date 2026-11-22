namespace Kelvin.Core;

/// <summary>
/// The limits every setting must respect, applied in one place: each time settings are loaded from
/// disk and each time they are changed. The API routes check requests so they can explain what was
/// wrong; this is the backstop that makes the rules hold whatever wrote the value (a request the
/// route didn't anticipate, a hand-edited file, an older version). It never throws: a value that
/// can't be used is replaced with the safe default.
/// </summary>
public static class SettingsRules
{
    public const double MinCriticalTemp = 70, MaxCriticalCpuTemp = 95, MaxCriticalGpuTemp = 100;
    public const int MaxNameLength = 200, MaxEntries = 64, MaxCurvePoints = 32, MaxDismissed = 200;

    public static void Normalize(KelvinSettings s)
    {
        s.Safety = Safety(s.Safety);
        s.Fans = FanList(s.Fans);
        s.Lighting = Lighting(s.Lighting);

        s.Alerts ??= new();
        s.Alerts.Rules = (s.Alerts.Rules ?? []).Where(r => r is not null).Take(10).ToList();
        foreach (var r in s.Alerts.Rules)
        {
            r.Id = Text(r.Id, 32);
            r.Threshold = Math.Clamp(Finite(r.Threshold, 85), 40, 110);
            r.SustainSeconds = Math.Clamp(Finite(r.SustainSeconds, 10), 0, 600);
        }

        s.Profiles = (s.Profiles ?? []).Where(p => p is not null).ToList();
        foreach (var p in s.Profiles)
        {
            p.Id ??= "";
            p.Name = Text(p.Name, MaxNameLength);
            p.Fans = FanList(p.Fans);
            p.Lighting = Lighting(p.Lighting);
        }

        s.Automation ??= new();
        s.Automation.GameProcesses = Strings(s.Automation.GameProcesses, 50, 100);
        s.KnownLightingDevices = Strings(s.KnownLightingDevices, MaxEntries, MaxNameLength);
        s.DismissedRecommendations = Strings(s.DismissedRecommendations, MaxDismissed, 100);
        s.FanProfiles = (s.FanProfiles ?? []).Where(p => p is not null).ToList();

        s.Mqtt ??= new();
        s.Mqtt.Host = Text(s.Mqtt.Host, 255);
        s.Mqtt.Username = Text(s.Mqtt.Username, MaxNameLength);
        s.Mqtt.PasswordProtected ??= "";
        s.Mqtt.BaseTopic = Text(s.Mqtt.BaseTopic, 100);
        s.Mqtt.DiscoveryPrefix = Text(s.Mqtt.DiscoveryPrefix, 100);
        if (s.Mqtt.Port is < 1 or > 65535) s.Mqtt.Port = 1883;
    }

    /// <summary>
    /// The temperatures that force every fan to full speed. A value that isn't a number (NaN would
    /// make the comparison never true, switching the safety net off) falls back to the default.
    /// </summary>
    public static SafetySettings Safety(SafetySettings? s)
    {
        var defaults = new SafetySettings();
        s ??= defaults;
        s.CriticalCpuTemp = Math.Clamp(Finite(s.CriticalCpuTemp, defaults.CriticalCpuTemp), MinCriticalTemp, MaxCriticalCpuTemp);
        s.CriticalGpuTemp = Math.Clamp(Finite(s.CriticalGpuTemp, defaults.CriticalGpuTemp), MinCriticalTemp, MaxCriticalGpuTemp);
        return s;
    }

    public static FanSettings Fan(FanSettings f)
    {
        var defaults = new FanSettings();
        f.FanId = Text(f.FanId, MaxNameLength);
        f.SourceSensorId = Text(f.SourceSensorId, MaxNameLength);
        f.Preset = Text(f.Preset, 40);
        f.FixedPercent = Math.Clamp(Finite(f.FixedPercent, defaults.FixedPercent), 0, 100);
        f.MinPercent = Math.Clamp(Finite(f.MinPercent, defaults.MinPercent), 0, 60);
        f.SmoothSeconds = Math.Clamp(Finite(f.SmoothSeconds, defaults.SmoothSeconds), 0, 120);
        var points = (f.Points ?? []).Where(p => p is not null && double.IsFinite(p.Temp) && double.IsFinite(p.Percent)).Take(MaxCurvePoints).ToList();
        if (f.Mode == FanMode.Curve)
        {
            // A curve Kelvin can't use hands the fan back to the BIOS, which is always safe.
            try { points = Curves.Sanitize(points); }
            catch (ArgumentException) { f.Mode = FanMode.Bios; }
        }
        f.Points = points;
        return f;
    }

    public static LightingSettings Lighting(LightingSettings? l)
    {
        var defaults = new LightingSettings();
        l ??= defaults;
        l.Color = Hex(l.Color, defaults.Color);
        l.Brightness = Math.Clamp(Finite(l.Brightness, defaults.Brightness), 0, 1);
        l.Speed = Math.Clamp(Finite(l.Speed, defaults.Speed), 0, 1);
        l.TemperatureSensorId = l.TemperatureSensorId is { Length: > 0 and <= MaxNameLength } id ? id : SensorIds.Hottest;
        l.ExcludedDeviceIds = Strings(l.ExcludedDeviceIds, MaxEntries, MaxNameLength);

        var devices = new Dictionary<string, DeviceLighting>();
        foreach (var (key, d) in l.Devices ?? [])
        {
            if (d is null || key.Length > MaxNameLength || devices.Count >= MaxEntries) continue;
            Colors(d);
            var zones = new Dictionary<string, ColorOverride>();
            foreach (var (zone, o) in d.Zones ?? [])
            {
                if (o is null || zone.Length > MaxNameLength || zones.Count >= MaxEntries) continue;
                Colors(o);
                zones[zone] = o;
            }
            d.Zones = zones;
            devices[key] = d;
        }
        l.Devices = devices;
        return l;
    }

    private static List<FanSettings> FanList(List<FanSettings>? fans) =>
        (fans ?? []).Where(f => f is not null).Take(MaxEntries).Select(Fan).ToList();

    private static void Colors(ColorOverride o)
    {
        var defaults = new ColorOverride();
        o.Color = Hex(o.Color, defaults.Color);
        o.Color2 = Hex(o.Color2, defaults.Color2);
    }

    private static string Hex(string? value, string fallback)
    {
        try { return RgbColor.FromHex(value ?? "").Hex; }
        catch (Exception) { return fallback; }
    }

    private static double Finite(double value, double fallback) => double.IsFinite(value) ? value : fallback;

    private static string Text(string? value, int max) => value is null ? "" : value.Length > max ? value[..max] : value;

    private static List<string> Strings(List<string>? values, int maxCount, int maxLength) =>
        (values ?? []).Where(v => v is { Length: > 0 } && v.Length <= maxLength).Take(maxCount).ToList();
}
