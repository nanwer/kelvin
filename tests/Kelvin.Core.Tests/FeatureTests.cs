using Kelvin.Core;

namespace Kelvin.Core.Tests;

/// <summary>Target mode: hold a temperature with the slowest fan that manages it.</summary>
public class TargetControlTests
{
    // A CPU and its cooler: more load is hotter, more fan is cooler, and it takes a while to show.
    private static (double Temp, List<double> Duties) Run(double load, double target, double floor = 20, int seconds = 600, double startDuty = 60)
    {
        var control = new TargetControl();
        double temp = 45, duty = startDuty;
        var duties = new List<double>();
        for (var i = 0; i < seconds; i++)
        {
            var settles = 36 + load * 56 - duty * 0.16;
            temp += (settles - temp) / 8;
            duty = control.Step(temp, target, floor, duty, 1);
            duties.Add(duty);
        }
        return (temp, duties);
    }

    [Fact]
    public void It_settles_just_under_the_target_without_hunting()
    {
        var (temp, duties) = Run(load: 0.6, target: 62);
        Assert.InRange(temp, 59, 62);
        var last = duties.TakeLast(120).ToList();
        Assert.InRange(last.Average(), 50, 70);            // 60% is what holds 60°C in this model
        Assert.True(last.Max() - last.Min() < 2, "the fan should be steady, not rising and falling");
    }

    [Fact]
    public void A_cool_pc_gets_the_slowest_speed_allowed()
    {
        var (_, duties) = Run(load: 0.05, target: 70, floor: 25);
        Assert.Equal(25, duties[^1]);
    }

    [Fact]
    public void A_target_the_cooler_cannot_hold_means_full_speed_and_says_so()
    {
        var control = new TargetControl();
        double temp = 45, duty = 30;
        for (var i = 0; i < 400; i++)
        {
            temp += (36 + 56 - duty * 0.16 - temp) / 8;   // full load: 76°C even at full speed
            duty = control.Step(temp, 62, 20, duty, 1);
        }
        Assert.Equal(100, duty);
        Assert.True(control.CannotHold);
    }

    [Fact]
    public void It_comes_back_down_after_a_long_spell_at_full_speed()
    {
        var control = new TargetControl();
        double duty = 30;
        for (var i = 0; i < 600; i++) duty = control.Step(80, 62, 20, duty, 1);
        Assert.Equal(100, duty);
        var seconds = 0;
        while (duty > 20 && seconds < 600) { duty = control.Step(45, 62, 20, duty, 1); seconds++; }
        Assert.True(seconds < 120, $"took {seconds}s to slow down once cool");
    }

    [Fact]
    public void The_engine_drives_a_fan_in_target_mode_and_reports_it()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kelvin-target-{Guid.NewGuid():N}", "settings.json");
        var store = new SettingsStore(path);
        var hw = new Kelvin.Core.Simulated.SimulatedFanProvider();
        hw.Open();
        store.Mutate(s => s.Fans.Add(new FanSettings { FanId = "sim/fan/cpu", Mode = FanMode.Target, SourceSensorId = "sim/cpu/tctl", TargetTemp = 300, MinPercent = 25, SmoothSeconds = 0 }));
        Assert.Equal(SettingsRules.MaxTargetTemp, store.Current.Fans[0].TargetTemp);   // out of range is brought back in

        using var engine = new FanEngine(hw, store);
        var tick = typeof(FanEngine).GetMethod("Tick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        for (var i = 0; i < 60; i++) tick.Invoke(engine, [1.0]);

        var status = engine.Targets["sim/fan/cpu"];
        Assert.Equal(85, status.Target);
        Assert.False(status.CannotHold);
        // Far below an 85°C target, the fan sits at its floor.
        Assert.Equal(25, hw.Fans.First(f => f.Id == "sim/fan/cpu").Percent);

        // Leaving target mode stops reporting it.
        store.Mutate(s => s.Fans[0].Mode = FanMode.Bios);
        tick.Invoke(engine, [1.0]);
        Assert.Empty(engine.Targets);
    }
}

public class SensorLogTests
{
    private static readonly SensorReading Cpu = new("cpu/t", "Core", "Ryzen", SensorRole.Cpu, SensorKind.Temperature, 50, "°C", true);
    private static readonly FanReading Fan = new("fan/1", "CPU Fan", 900, 40, true, false);

    [Fact]
    public void The_hour_keeps_every_reading_and_the_day_keeps_a_minute_average()
    {
        var log = new SensorLog(null);
        var start = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000 - 1_800_000_000 % 60);
        for (var i = 0; i < 36; i++)                                      // three minutes, every 5 s
            log.Record([Cpu with { Value = 50 + i / 12 * 10 }], [Fan], start.AddSeconds(i * 5));
        log.Record([Cpu], [Fan], start.AddSeconds(180));                  // the next minute closes the third

        var hour = log.View("hour");
        Assert.Equal(37, hour.Times.Length);
        Assert.Equal(5, hour.StepSeconds);
        Assert.Equal(["cpu/t", "fan/1|rpm", "fan/1|duty"], hour.Series.Select(s => s.Id));

        var day = log.View("day");
        Assert.Equal(3, day.Times.Length);
        Assert.Equal([50, 60, 70], day.Series.Single(s => s.Id == "cpu/t").Values);
        Assert.Equal("rpm", day.Series.Single(s => s.Id == "fan/1|rpm").Kind);
    }

    [Fact]
    public void A_sensor_that_appears_later_has_gaps_before_it_and_old_readings_fall_off()
    {
        var log = new SensorLog(null);
        var start = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        log.Record([Cpu], [], start);
        log.Record([Cpu], [Fan], start.AddSeconds(5));
        var rpm = log.View("hour").Series.Single(s => s.Id == "fan/1|rpm");
        Assert.Equal([null, 900], rpm.Values);

        log.Record([Cpu], [Fan], start.AddHours(2));   // Kelvin was off for two hours
        Assert.Single(log.View("hour").Times);         // the old hour is gone, not stretched across the gap
    }

    [Fact]
    public void The_day_survives_a_restart_and_a_damaged_file_is_ignored()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kelvin-log-{Guid.NewGuid():N}", "sensor-history.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var log = new SensorLog(path);
        var now = DateTimeOffset.Now;
        var minute = now.AddSeconds(-(now.ToUnixTimeSeconds() % 60));
        for (var i = 0; i < 4; i++) log.Record([Cpu], [Fan], minute.AddMinutes(i - 4));
        log.Save();

        var again = new SensorLog(path).View("day");
        Assert.Equal(3, again.Times.Length);
        Assert.Equal("Core", again.Series.Single(s => s.Id == "cpu/t").Name);

        File.WriteAllText(path, """{ "series": { "x": null }, "day": { "times": [1,2,3], "values": { "x": [1] } } }""");
        Assert.Empty(new SensorLog(path).View("day").Series);
        File.WriteAllText(path, "not json");
        Assert.Empty(new SensorLog(path).View("day").Times);
    }
}
