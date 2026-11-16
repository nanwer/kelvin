using Kelvin.Core;

namespace Kelvin.Core.Tests;

public class CurveTests
{
    [Fact]
    public void Evaluate_interpolates_and_is_flat_beyond_the_ends()
    {
        CurvePoint[] pts = [new(40, 20), new(80, 100)];
        Assert.Equal(20, Curves.Evaluate(pts, 30));
        Assert.Equal(60, Curves.Evaluate(pts, 60));
        Assert.Equal(100, Curves.Evaluate(pts, 95));
    }

    [Fact]
    public void Sanitize_never_lets_duty_fall_as_temperature_rises()
    {
        var clean = Curves.Sanitize([new(70, 30), new(40, 60), new(90, 100)]);
        Assert.Equal([40.0, 70.0, 90.0], clean.Select(p => p.Temp));
        Assert.Equal([60.0, 60.0, 100.0], clean.Select(p => p.Percent));
    }

    [Fact]
    public void Sanitize_adds_full_speed_at_the_hot_end()
    {
        var clean = Curves.Sanitize([new(30, 10), new(60, 40)]);
        Assert.Equal(new CurvePoint(95, 100), clean[^1]);
    }

    [Fact]
    public void Sanitize_rejects_too_few_points()
    {
        Assert.Throws<ArgumentException>(() => Curves.Sanitize([new(50, 50)]));
    }
}

public class SensorListTests
{
    [Fact]
    public void Keeps_one_main_temperature_per_component()
    {
        // Matches the real ROG STRIX B550-I, which reports two sensors named "Motherboard".
        var list = SensorList.NormalizePrimary(
        [
            new("b1", "Motherboard", "Board", SensorRole.Board, SensorKind.Temperature, 43, "°C", true),
            new("b2", "Motherboard", "Board", SensorRole.Board, SensorKind.Temperature, 43, "°C", true),
            new("g1", "GPU Memory", "GPU", SensorRole.Gpu, SensorKind.Temperature, 50, "°C"),
        ]);
        Assert.Equal(["b1"], list.Where(s => s.Primary && s.Role == SensorRole.Board).Select(s => s.Id));
        Assert.True(list.Single(s => s.Id == "g1").Primary); // fallback when none is marked
    }
}

public class FanEngineTests
{
    private sealed class FakeHardware : IFanProvider
    {
        public double CpuTemp = 50;
        public bool NoFans;
        public readonly Dictionary<string, double?> Duty = new() { ["fan"] = null };
        public string Name => "Fake";
        public bool IsSimulated => true;
        public IReadOnlyList<FanReading> Fans => NoFans ? [] : [new("fan", "CPU Fan", 1000, Duty["fan"], true, false)];
        public IReadOnlyList<SensorReading> Sensors =>
            [new("cpu", "CPU", "AMD Ryzen 7 5800X", SensorRole.Cpu, SensorKind.Temperature, CpuTemp, "°C", true)];
        public IReadOnlyList<HardwareInfo> Hardware => [];
        public void Open() { }
        public void Update() { }
        public void SetFanPercent(string fanId, double percent) => Duty[fanId] = percent;
        public void RestoreDefault(string fanId) => Duty[fanId] = null;
        public void Dispose() { }
    }

    private static (FakeHardware, FanEngine, SettingsStore) Setup()
    {
        var hw = new FakeHardware();
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), $"kelvin-test-{Guid.NewGuid():N}.json"));
        store.Mutate(s => s.Fans.Add(new FanSettings
        {
            FanId = "fan", Mode = FanMode.Fixed, FixedPercent = 30, MinPercent = 0, ResponseSeconds = 0.1,
        }));
        return (hw, new FanEngine(hw, store), store);
    }

    private static void Tick(FanEngine engine) =>
        typeof(FanEngine).GetMethod("Tick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(engine, [1.0]);

    [Fact]
    public void Critical_cpu_temperature_forces_full_speed()
    {
        var (hw, engine, _) = Setup();
        Tick(engine);
        Assert.Equal(30, hw.Duty["fan"]);

        hw.CpuTemp = 95;
        Tick(engine);
        Assert.Equal(100, hw.Duty["fan"]);
        Assert.True(engine.Safety.Override);
    }

    [Fact]
    public void No_override_when_there_are_no_fans_to_control()
    {
        // A laptop: hot CPU, but its fans belong to the laptop's own controller.
        var hw = new FakeHardware { CpuTemp = 95, NoFans = true };
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), $"kelvin-test-{Guid.NewGuid():N}.json"));
        var engine = new FanEngine(hw, store);
        Tick(engine);
        Assert.False(engine.Safety.Override);
    }

    [Fact]
    public void Override_message_counts_the_fans_it_controls()
    {
        var (hw, engine, _) = Setup();
        hw.CpuTemp = 95;
        Tick(engine);
        Assert.Equal("CPU reached 95°C. The fan Kelvin controls is at full speed until it cools down.", engine.Safety.Reason);
    }

    [Fact]
    public void Dispose_hands_fans_back_to_the_bios()
    {
        var (hw, engine, _) = Setup();
        Tick(engine);
        engine.Dispose();
        Assert.Null(hw.Duty["fan"]);
    }

    [Fact]
    public void Missing_source_sensor_releases_the_fan_instead_of_parking_it()
    {
        var (hw, engine, store) = Setup();
        Tick(engine);
        store.Mutate(s => { s.Fans[0].Mode = FanMode.Curve; s.Fans[0].SourceSensorId = "gone"; s.Fans[0].Points = [new(30, 0), new(90, 0)]; });
        Tick(engine);
        Assert.Null(hw.Duty["fan"]);
    }
}

public class AdvisorTests
{
    [Fact]
    public void Suggests_measuring_when_fans_are_unmeasured()
    {
        var recs = Advisor.Build([new("fan", "CPU Fan", 900, 50, true, false)], [], new KelvinSettings(), []);
        Assert.Contains(recs, r => r.Action == RecommendationAction.Calibrate);
    }

    [Fact]
    public void Case_fans_follow_the_hottest_sensor_and_respect_the_stall_point()
    {
        var settings = new KelvinSettings
        {
            FanProfiles = [new FanProfile { FanId = "case", Connected = true, MaxRpm = 1500, MinRpm = 400, StopPercent = 20 }],
        };
        SensorReading[] sensors =
        [
            new("cpu", "CPU", "AMD Ryzen 7 5800X", SensorRole.Cpu, SensorKind.Temperature, 50, "°C", true),
            new("gpu", "GPU", "RX 9070 XT", SensorRole.Gpu, SensorKind.Temperature, 45, "°C", true),
        ];
        var rec = Advisor.Build([new("case", "Chassis Fan", 900, 50, true, false)], sensors, settings, [])
            .Single(r => r.Id == "fan:case");
        Assert.Equal(SensorIds.Hottest, rec.Settings!.SourceSensorId);
        Assert.Equal(30, rec.Settings.MinPercent);
    }

    [Fact]
    public void Gpu_fans_are_left_to_the_card()
    {
        var settings = new KelvinSettings
        {
            FanProfiles = [new FanProfile { FanId = "cpu", Connected = true, MaxRpm = 1800, StopPercent = 10 }],
        };
        SensorReading[] sensors = [new("cpu", "CPU", "AMD Ryzen 7 5800X", SensorRole.Cpu, SensorKind.Temperature, 50, "°C", true)];
        FanReading[] fans = [new("cpu", "CPU Fan", 900, 50, true, false), new("gpu", "GPU Fan", 1500, 40, true, false)];

        var recs = Advisor.Build(fans, sensors, settings, []);

        Assert.DoesNotContain(recs, r => r.FanId == "gpu");
        // The unmeasured GPU fan alone must not trigger "Measure your fans".
        Assert.DoesNotContain(recs, r => r.Action == RecommendationAction.Calibrate);
    }

    [Fact]
    public void FanControl_is_never_offered_for_force_closing()
    {
        Assert.False(ConflictDetector.Find("fancontrol")!.CanClose);
        var recs = Advisor.Build([], [], new KelvinSettings(),
            [new Conflict("fancontrol", "FanControl", ConflictArea.Fans, "", "", false),
             new Conflict("signalrgb", "SignalRGB", ConflictArea.Lighting, "", "", true)]);
        Assert.Equal(RecommendationAction.None, recs.Single(r => r.Id == "conflict:fancontrol").Action);
        Assert.Equal(RecommendationAction.CloseApp, recs.Single(r => r.Id == "conflict:signalrgb").Action);
    }

    [Fact]
    public void Empty_headers_are_reported_not_configured()
    {
        var settings = new KelvinSettings { FanProfiles = [new FanProfile { FanId = "aio", Connected = false }] };
        var recs = Advisor.Build([new("aio", "AIO Pump", 0, 100, true, true)], [], settings, []);
        Assert.Contains(recs, r => r.Id == "empty:aio" && r.Action == RecommendationAction.None);
    }
}
