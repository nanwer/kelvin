using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kelvin.Core;

public enum FanMode { Bios, Fixed, Curve }

public sealed record CurvePoint(double Temp, double Percent);

public sealed class FanSettings
{
    public string FanId { get; set; } = "";
    public FanMode Mode { get; set; } = FanMode.Bios;
    public double FixedPercent { get; set; } = 50;
    public string SourceSensorId { get; set; } = "";
    public List<CurvePoint> Points { get; set; } = [];
    /// <summary>Never drive the fan below this, so it can't stall.</summary>
    public double MinPercent { get; set; } = 20;
    /// <summary>Smoothing time constant. Higher = calmer fans when temps spike briefly.</summary>
    public double ResponseSeconds { get; set; } = 6;
    public string Preset { get; set; } = "custom";

    public FanSettings Clone() => new()
    {
        FanId = FanId, Mode = Mode, FixedPercent = FixedPercent, SourceSensorId = SourceSensorId,
        Points = [.. Points], MinPercent = MinPercent, ResponseSeconds = ResponseSeconds, Preset = Preset,
    };
}

public enum LightingEffect { Off, Static, Breathing, Spectrum, Temperature }

public sealed class LightingSettings
{
    public LightingEffect Effect { get; set; } = LightingEffect.Static;
    public string Color { get; set; } = "#ff8a3d";
    public double Brightness { get; set; } = 0.8;
    public double Speed { get; set; } = 0.4;
    public string TemperatureSensorId { get; set; } = SensorIds.Hottest;
    public List<string> ExcludedDeviceIds { get; set; } = [];
}

public sealed class SafetySettings
{
    public double CriticalCpuTemp { get; set; } = 90;
    public double CriticalGpuTemp { get; set; } = 95;
}

public sealed record CalibrationPoint(double Percent, double Rpm);

public sealed class FanProfile
{
    public string FanId { get; set; } = "";
    public bool Connected { get; set; }
    public double MaxRpm { get; set; }
    public double MinRpm { get; set; }
    /// <summary>Highest duty where the fan stops spinning, or null if it never stops.</summary>
    public double? StopPercent { get; set; }
    public List<CalibrationPoint> Points { get; set; } = [];
    public DateTimeOffset MeasuredAt { get; set; }
}

public sealed class KelvinSettings
{
    public List<FanSettings> Fans { get; set; } = [];
    public LightingSettings Lighting { get; set; } = new();
    public SafetySettings Safety { get; set; } = new();
    public List<FanProfile> FanProfiles { get; set; } = [];
    public List<string> DismissedRecommendations { get; set; } = [];
}

public static class SensorIds
{
    /// <summary>Virtual sensor: the hotter of the CPU and GPU. Ideal for case fans in small builds.</summary>
    public const string Hottest = "virtual/hottest";
}

/// <summary>Loads and saves settings as JSON. Writes are atomic so a crash can't leave a half-written file.</summary>
public sealed class SettingsStore
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _path;
    private readonly object _gate = new();

    public SettingsStore(string path)
    {
        _path = path;
        Current = Load();
    }

    public KelvinSettings Current { get; private set; }

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kelvin", "settings.json");

    /// <summary>Settings for the installed Windows service, which has no user profile of its own.</summary>
    public static string MachinePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Kelvin", "settings.json");

    private KelvinSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<KelvinSettings>(File.ReadAllText(_path), Json) ?? new();
        }
        catch (Exception)
        {
            // A corrupt file must never stop the app. Keep it aside for bug reports and start clean.
            File.Copy(_path, _path + ".corrupt", overwrite: true);
        }
        return new();
    }

    public void Mutate(Action<KelvinSettings> change)
    {
        lock (_gate)
        {
            change(Current);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Json));
            File.Move(tmp, _path, overwrite: true);
        }
    }

    public T Read<T>(Func<KelvinSettings, T> read)
    {
        lock (_gate) return read(Current);
    }
}
