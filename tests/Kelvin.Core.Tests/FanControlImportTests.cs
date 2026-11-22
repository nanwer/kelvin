using Kelvin.Core;

namespace Kelvin.Core.Tests;

/// <summary>
/// Importing from FanControl. The JSON here follows real configuration files (versions 81 to 284);
/// the ids are the ones FanControl and Kelvin use for the same Nuvoton chip and Radeon card.
/// </summary>
public class FanControlImportTests
{
    private static readonly FanReading[] Fans =
    [
        new("/lpc/nct6798d/0/control/0", "Chassis Fan", 700, 40, true, false),
        new("/lpc/nct6798d/0/control/1", "CPU Fan", 900, 40, true, false),
        new("/lpc/nct6798d/0/control/4", "AIO Pump", 0, 100, true, true),
        new("/gpu-amd/0/control/0", "GPU Fan", 0, 0, true, false),
    ];

    private static readonly SensorReading[] Sensors =
    [
        new("/amdcpu/0/temperature/2", "Core (Tctl/Tdie)", "Ryzen", SensorRole.Cpu, SensorKind.Temperature, 50, "°C", true),
        new("/gpu-amd/0/temperature/0", "GPU Core", "Radeon", SensorRole.Gpu, SensorKind.Temperature, 45, "°C", true),
        new("/gpu-amd/0/temperature/1", "GPU Memory", "Radeon", SensorRole.Gpu, SensorKind.Temperature, 50, "°C"),
        new("/lpc/nct6798d/0/temperature/2", "Motherboard", "Board", SensorRole.Board, SensorKind.Temperature, 35, "°C", true),
        new(SensorIds.Hottest, "Hottest of CPU and GPU", "Kelvin", SensorRole.Virtual, SensorKind.Temperature, 50, "°C"),
    ];

    private static ImportPreview Read(string json) => FanControlImport.Read(json, Fans, Sensors);
    private static ImportedFan Fan(ImportPreview p, string label) => p.Fans.Single(f => f.Label == label);

    // The layout current versions write: curves are referred to by name, sensors by id.
    private const string Current = """
        {
          "__VERSION__": "273",
          "FanControl": {
            "Controls": [
              { "Identifier": "/lpc/nct6798d/control/0", "NickName": "Chassis Fan", "Enable": true, "ManualControl": false, "SelectedFanCurve": { "Name": "graph" }, "MinimumPercent": 25, "SelectedOffset": 0 },
              { "Identifier": "/lpc/nct6798d/control/1", "NickName": "CPU Fan", "Enable": true, "ManualControl": false, "SelectedFanCurve": { "Name": "auto" }, "MinimumPercent": 0 },
              { "Identifier": "/lpc/nct6798d/control/4", "NickName": "AIO Pump", "Enable": false, "ManualControl": false, "SelectedFanCurve": null },
              { "Identifier": "ADLX/AMD Radeon RX 9070 XT/2560/control", "NickName": "Radeon", "Enable": true, "ManualControl": false, "SelectedFanCurve": { "Name": "gpu" } },
              { "Identifier": "/lpc/it8689e/control/2", "NickName": "Elsewhere", "Enable": true, "SelectedFanCurve": { "Name": "graph" } }
            ],
            "FanCurves": [
              { "Name": "graph", "CommandMode": 0, "SelectedTempSource": { "Identifier": "/amdcpu/0/temperature/2" },
                "Points": ["70,60", "40.5,30.25", "85,80"], "MaximumCommand": 100 },
              { "Name": "auto", "CommandMode": 0, "SelectedTempSource": { "Identifier": "/amdcpu/0/temperature/2" },
                "Step": 2, "Deadband": 3, "SelectedResponseTime": 2, "IdleTemperature": 50, "MinFanSpeed": 50, "MaxFanSpeed": 90, "LoadTemperature": 65 },
              { "Name": "gpu", "SelectedTempSource": { "Identifier": "ADLX/AMD Radeon RX 9070 XT/2560/temp/Memory" },
                "MinimumTemperature": 40, "MaximumTemperature": 80, "MinimumFanSpeed": 20, "MaximumFanSpeed": 100 }
            ]
          }
        }
        """;

    [Fact]
    public void A_graph_curve_comes_over_as_a_curve_on_the_same_fan_and_sensor()
    {
        var fan = Fan(Read(Current), "Chassis Fan");
        Assert.Equal("/lpc/nct6798d/0/control/0", fan.FanId);            // the ids differ by the chip's index
        var s = fan.Settings!;
        Assert.Equal(FanMode.Curve, s.Mode);
        Assert.Equal("/amdcpu/0/temperature/2", s.SourceSensorId);
        Assert.Equal(25, s.MinPercent);
        // Points are "temperature,percent", in any order; Kelvin sorts them and makes the hot end reach 100%.
        Assert.Equal([new(40.5, 30), new(70, 60), new(85, 80), new(95, 100)], s.Points);
        Assert.True(fan.Suggested);
        Assert.Contains("always reach 100%", fan.Note);
    }

    [Fact]
    public void An_auto_curve_becomes_target_mode()
    {
        var s = Fan(Read(Current), "CPU Fan").Settings!;
        Assert.Equal(FanMode.Target, s.Mode);
        Assert.Equal(65, s.TargetTemp);
        Assert.Equal(50, s.MinPercent);
        Assert.Equal("/amdcpu/0/temperature/2", s.SourceSensorId);
    }

    [Fact]
    public void A_graphics_card_fan_is_matched_but_not_ticked()
    {
        var fan = Fan(Read(Current), "Radeon");
        Assert.Equal("/gpu-amd/0/control/0", fan.FanId);
        Assert.False(fan.Suggested);
        Assert.Equal("/gpu-amd/0/temperature/1", fan.Settings!.SourceSensorId);   // ".../temp/Memory" is "GPU Memory"
        Assert.Equal([new(40, 20), new(80, 100)], fan.Settings.Points);
    }

    [Fact]
    public void Fans_that_cannot_come_over_say_why()
    {
        var p = Read(Current);
        Assert.Null(Fan(p, "AIO Pump").Settings);
        Assert.Contains("wasn't controlling", Fan(p, "AIO Pump").Summary);
        Assert.Null(Fan(p, "Elsewhere").FanId);
        Assert.Equal(3, p.Importable);
        Assert.Equal("273", p.Version);
    }

    [Fact]
    public void Older_files_that_embed_whole_curves_read_the_same()
    {
        // Before version 235: the section is "Main", and a fan carries a full copy of its curve.
        var p = Read("""
            { "Main": {
                "Controls": [
                  { "Identifier": "/lpc/nct6798d/control/1", "Name": "Fan #2", "NickName": "CPU", "Enable": true, "ManualControl": false,
                    "SelectedFanCurve": { "Name": "old", "Points": ["1,1"], "SelectedTempSource": { "Identifier": "x" } } },
                  { "Identifier": "/lpc/nct6798d/control/0", "Name": "Fan #1", "NickName": "Case", "Enable": true, "ManualControl": true, "ManualControlValue": 45.0 }
                ],
                "FanCurves": [
                  { "Name": "old", "SelectedTempSource": { "Identifier": "/amdcpu/0/temperature/2", "IsHidden": false, "Name": "Core", "NickName": "Core" },
                    "IdleTemperature": 50, "LoadTemperature": 65, "IdleFanSpeed": 38, "LoadFanSpeed": 57, "SelectedResponseTime": 15 }
                ] } }
            """);
        var cpu = Fan(p, "CPU").Settings!;
        Assert.Equal([new(50, 38), new(65, 57), new(95, 100)], cpu.Points);   // the top-level curve wins over the embedded copy
        var fixedFan = Fan(p, "Case").Settings!;
        Assert.Equal((FanMode.Fixed, 45.0), (fixedFan.Mode, fixedFan.FixedPercent));
    }

    [Fact]
    public void A_mix_of_cpu_and_gpu_curves_follows_the_hotter_of_the_two()
    {
        var p = Read("""
            { "FanControl": {
                "Controls": [
                  { "Identifier": "/lpc/nct6798d/control/0", "NickName": "Case", "Enable": true, "SelectedFanCurve": { "Name": "mix" } },
                  { "Identifier": "/lpc/nct6798d/control/1", "NickName": "Copy", "Enable": true, "SelectedFanCurve": { "Name": "sync" } },
                  { "Identifier": "/lpc/nct6798d/control/4", "NickName": "Pump", "Enable": true, "SelectedFanCurve": { "Name": "flat" } }
                ],
                "FanCurves": [
                  { "Name": "c", "SelectedTempSource": { "Identifier": "/amdcpu/0/temperature/2" }, "Points": ["40,20", "80,100"] },
                  { "Name": "g", "SelectedTempSource": { "Identifier": "NVApiWrapper/0-GA102-A/sensor/0" }, "Points": ["40,40", "60,50", "80,60"] },
                  { "Name": "mix", "SelectedFanCurves": [ { "Name": "c" }, { "Name": "g" } ], "SelectedMixFunction": 0 },
                  { "Name": "sync", "SelectedControl": { "Identifier": "/lpc/nct6798d/control/0" }, "SelectedOffset": 0, "Proportional": false },
                  { "Name": "flat", "Percent": 70 }
                ] } }
            """);
        var mix = Fan(p, "Case").Settings!;
        Assert.Equal(SensorIds.Hottest, mix.SourceSensorId);
        // The higher curve at each temperature (95°C is where Kelvin took the graphics curve up to full speed).
        Assert.Equal([new(40, 40), new(60, 60), new(80, 100), new(95, 100)], mix.Points);
        var copy = Fan(p, "Copy");
        Assert.Equal("/lpc/nct6798d/0/control/1", copy.Settings!.FanId);
        Assert.Equal(mix.Points, copy.Settings.Points);
        Assert.Equal((FanMode.Fixed, 70.0), (Fan(p, "Pump").Settings!.Mode, Fan(p, "Pump").Settings!.FixedPercent));
    }

    [Fact]
    public void A_custom_sensor_is_followed_through_to_a_real_one()
    {
        var p = Read("""
            { "FanControl": {
                "Controls": [
                  { "Identifier": "/lpc/nct6798d/control/0", "NickName": "A", "Enable": true, "SelectedFanCurve": { "Name": "a" } },
                  { "Identifier": "/lpc/nct6798d/control/1", "NickName": "B", "Enable": true, "SelectedFanCurve": { "Name": "b" } }
                ],
                "FanCurves": [
                  { "Name": "a", "SelectedTempSource": { "Identifier": "Mix/Max (CPU, GPU)" }, "Points": ["40,20", "80,100"] },
                  { "Name": "b", "SelectedTempSource": { "Identifier": "Time/Average" }, "Points": ["40,20", "80,100"] }
                ],
                "CustomSensors": [
                  { "Identifier": "Mix/Max (CPU, GPU)", "SelectedMixFunction": 1, "SelectedSensors": [ { "Identifier": "/amdcpu/0/temperature/2" }, { "Identifier": "/gpu-amd/5/temperature/0" } ] },
                  { "Identifier": "Time/Average", "SelectedTempSource": { "Identifier": "/amdcpu/0/temperature/2" }, "SelectedTime": 60 }
                ] } }
            """);
        Assert.Equal(SensorIds.Hottest, Fan(p, "A").Settings!.SourceSensorId);
        var b = Fan(p, "B").Settings!;
        Assert.Equal(("/amdcpu/0/temperature/2", 60.0), (b.SourceSensorId, b.SmoothSeconds));   // a time average becomes "reaction to spikes"
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("""{ "FanControl": { "Controls": [] } }""")]
    [InlineData("""{ "Main": "nope" }""")]
    public void A_file_that_is_not_a_configuration_is_refused_politely(string json) =>
        Assert.Throws<FormatException>(() => Read(json));

    [Fact]
    public void Nonsense_inside_a_configuration_never_produces_unsafe_settings()
    {
        var p = Read("""
            { "FanControl": {
                "Controls": [
                  { "Identifier": "/lpc/nct6798d/control/0", "NickName": "A", "Enable": true, "MinimumPercent": 1e300, "SelectedOffset": -500, "SelectedFanCurve": { "Name": "a" } },
                  { "Identifier": "/lpc/nct6798d/control/1", "NickName": "B", "Enable": true, "SelectedFanCurve": { "Name": "b" } },
                  { "Identifier": "/lpc/nct6798d/control/4", "NickName": "C", "Enable": true, "SelectedFanCurve": { "Name": "c" } },
                  { "Identifier": 7, "NickName": null, "Enable": "yes" }, 12, null
                ],
                "FanCurves": [
                  { "Name": "a", "SelectedTempSource": { "Identifier": "/amdcpu/0/temperature/2" }, "Points": ["90,10", "bad", "30,100", 5] },
                  { "Name": "b", "SelectedTempSource": { "Identifier": "/amdcpu/0/temperature/2" }, "MinFanSpeed": -5, "LoadTemperature": 9999 },
                  { "Name": "c", "CommandMode": 1, "Points": ["40,600", "80,1500"] }
                ] } }
            """);
        var a = Fan(p, "A").Settings!;
        Assert.Equal(60, a.MinPercent);
        // Speed never falls as it gets hotter, and the hot end reaches full speed, whatever the file said.
        Assert.True(a.Points.Zip(a.Points.Skip(1)).All(pair => pair.Second.Percent >= pair.First.Percent));
        Assert.Equal(100, a.Points[^1].Percent);
        Assert.Equal(SettingsRules.MaxTargetTemp, Fan(p, "B").Settings!.TargetTemp);
        Assert.Null(Fan(p, "C").Settings);                                  // RPM curves can't be brought over
        Assert.Contains("RPM", Fan(p, "C").Summary);
    }

    /// <summary>
    /// Reads every real configuration in a folder (set KELVIN_FANCONTROL_SAMPLES): none may fail to
    /// parse, and whatever comes out must be within Kelvin's rules. Skipped when the folder isn't set,
    /// since other people's configuration files don't belong in this repository.
    /// </summary>
    [Fact]
    public void Real_configuration_files_all_read()
    {
        var folder = Environment.GetEnvironmentVariable("KELVIN_FANCONTROL_SAMPLES");
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        var files = Directory.GetFiles(folder, "*.json");
        Assert.NotEmpty(files);
        var total = 0;
        foreach (var file in files)
        {
            var json = File.ReadAllText(file);
            // Give each file the fans and sensors it names, as if read on the PC it came from.
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var main = doc.RootElement.TryGetProperty("FanControl", out var m) ? m : doc.RootElement.GetProperty("Main");
            var fans = main.GetProperty("Controls").EnumerateArray()
                .Select(c => new FanReading(c.GetProperty("Identifier").GetString()!, "Fan", 800, 40, true, false)).ToList();
            var preview = FanControlImport.Read(json, fans, Sensors, Path.GetFileName(file));
            Assert.Equal(fans.Count, preview.Fans.Count);
            foreach (var f in preview.Fans.Where(f => f.Settings is not null))
            {
                total++;
                var s = f.Settings!;
                Assert.InRange(s.MinPercent, 0, 60);
                Assert.InRange(s.TargetTemp, SettingsRules.MinTargetTemp, SettingsRules.MaxTargetTemp);
                if (s.Mode == FanMode.Curve) Assert.Equal(100, s.Points[^1].Percent);
            }
        }
        Assert.True(total > 20, $"only {total} fans came over from {files.Length} files");
    }
}
