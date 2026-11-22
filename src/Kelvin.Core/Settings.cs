using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kelvin.Core;

public enum FanMode { Bios, Fixed, Curve, Target }

public sealed record CurvePoint(double Temp, double Percent);

public sealed class FanSettings
{
    public string FanId { get; set; } = "";
    public FanMode Mode { get; set; } = FanMode.Bios;
    public double FixedPercent { get; set; } = 50;
    public string SourceSensorId { get; set; } = "";
    public List<CurvePoint> Points { get; set; } = [];
    /// <summary>
    /// Target mode: the temperature to keep the followed sensor under, with the fan as slow as
    /// that allows (see <see cref="TargetControl"/>).
    /// </summary>
    public double TargetTemp { get; set; } = 70;
    /// <summary>Never drive the fan below this, so it can't stall.</summary>
    public double MinPercent { get; set; } = 20;
    /// <summary>
    /// How long a temperature has to stay up before the fan fully follows it. Modern CPUs jump
    /// 20°C or more for a few seconds at a time; the fan follows the average over about this long,
    /// so it doesn't rev at every spike. 0 follows the raw reading.
    /// </summary>
    public double SmoothSeconds { get; set; } = 30;
    public string Preset { get; set; } = "custom";

    public FanSettings Clone() => new()
    {
        FanId = FanId, Mode = Mode, FixedPercent = FixedPercent, SourceSensorId = SourceSensorId,
        Points = [.. Points], TargetTemp = TargetTemp, MinPercent = MinPercent, SmoothSeconds = SmoothSeconds, Preset = Preset,
    };
}

public enum LightingEffect { Off, Static, Breathing, Spectrum, Temperature, Screen }

/// <summary>How one device or zone is colored: by the global effect, or on its own.</summary>
public enum ColorMode { Follow, Solid, Gradient, Off }

public class ColorOverride
{
    public ColorMode Mode { get; set; } = ColorMode.Follow;
    public string Color { get; set; } = "#ff8a3d";
    /// <summary>End color for gradients, which run from the first LED to the last.</summary>
    public string Color2 { get; set; } = "#3d7bff";
}

public sealed class DeviceLighting : ColorOverride
{
    /// <summary>Per-zone colors by zone name. A zone's own setting beats the device's.</summary>
    public Dictionary<string, ColorOverride> Zones { get; set; } = new();
}

public sealed class LightingSettings
{
    /// <summary>Per-device colors by device id. Devices not listed follow the effect.</summary>
    public Dictionary<string, DeviceLighting> Devices { get; set; } = new();

    public LightingEffect Effect { get; set; } = LightingEffect.Static;
    public string Color { get; set; } = "#ff8a3d";
    public double Brightness { get; set; } = 0.8;
    public double Speed { get; set; } = 0.4;
    public string TemperatureSensorId { get; set; } = SensorIds.Hottest;
    /// <summary>Screen sync: spread the screen left to right along each device, instead of one color.</summary>
    public bool ScreenSpread { get; set; }
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
    public AlertSettings Alerts { get; set; } = new();
    public List<Profile> Profiles { get; set; } = [];
    public string? ActiveProfileId { get; set; }
    public ProfileAutomation Automation { get; set; } = new();
    /// <summary>A profile the user picked by hand, kept across restarts until the situation changes.</summary>
    public ManualProfileChoice? ManualProfile { get; set; }
    /// <summary>Lighting devices OpenRGB has found before, so Kelvin notices when detection comes up short.</summary>
    public List<string> KnownLightingDevices { get; set; } = [];
    /// <summary>Laptop fan mode Kelvin last set, so it can be restored after a restart.</summary>
    public string? FanMode { get; set; }
    public MqttSettings Mqtt { get; set; } = new();
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
            {
                var loaded = JsonSerializer.Deserialize<KelvinSettings>(File.ReadAllText(_path), Json) ?? new();
                // The file gets the same limits as the API: whatever wrote it, a value the API
                // would refuse (a safety limit out of range, a broken list) never takes effect.
                SettingsRules.Normalize(loaded);
                return loaded;
            }
        }
        catch (Exception)
        {
            // A corrupt file must never stop the app. Keep it aside for bug reports and start clean.
            try { File.Copy(_path, _path + ".corrupt", overwrite: true); }
            catch (Exception) { /* keeping a copy is a courtesy; starting is what matters */ }
        }
        return new();
    }

    /// <summary>Runs inside every save, after the change (used to keep the active profile in sync).</summary>
    public Action<KelvinSettings>? BeforeSave { get; set; }

    public void Mutate(Action<KelvinSettings> change)
    {
        lock (_gate)
        {
            change(Current);
            // Every change is brought inside the rules before anything reads or saves it, so a
            // value that slipped past a route (NaN, a null list) can neither take effect nor
            // make the save fail and leave memory and disk out of step.
            SettingsRules.Normalize(Current);
            BeforeSave?.Invoke(Current);
            SafeFile.Replace(_path, JsonSerializer.Serialize(Current, Json));
        }
    }

    public T Read<T>(Func<KelvinSettings, T> read)
    {
        lock (_gate) return read(Current);
    }
}
