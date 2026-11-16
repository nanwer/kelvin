using Kelvin.Core;

namespace Kelvin.Core.Tests;

public class AlertTests
{
    private DateTimeOffset _now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
    private readonly AlertMonitor _monitor;
    private readonly AlertSettings _settings = new()
    {
        Rules = [new AlertRule { Id = "cpu", Source = AlertSource.Cpu, Threshold = 85, SustainSeconds = 10 }],
    };
    private static readonly SafetyState Calm = new(false, null);

    public AlertTests() => _monitor = new AlertMonitor(() => _now);

    private static SensorReading[] Cpu(double t) =>
        [new("cpu", "CPU", "AMD Ryzen 7 5800X", SensorRole.Cpu, SensorKind.Temperature, t, "°C", true)];

    /// <summary>Feeds one reading per second for <paramref name="seconds"/> seconds.</summary>
    private void Run(double temp, int seconds, SafetyState? safety = null)
    {
        for (var i = 0; i < seconds; i++)
        {
            _monitor.Evaluate(_settings, Cpu(temp), safety ?? Calm, null);
            _now = _now.AddSeconds(1);
        }
    }

    private int Thresholds => _monitor.Recent.Count(e => e.Kind == "threshold");

    [Fact]
    public void A_short_spike_does_not_alert()
    {
        Run(90, 5);
        Run(60, 5);
        Assert.Equal(0, Thresholds);
    }

    [Fact]
    public void A_sustained_high_temperature_alerts_once()
    {
        Run(88, 60);
        Assert.Equal(1, Thresholds);
        var e = _monitor.Recent.Single();
        Assert.Equal("CPU is at 88°C", e.Title);
        Assert.Contains("cpu", _monitor.Firing);
    }

    [Fact]
    public void Hovering_around_the_limit_does_not_spam()
    {
        Run(88, 15);                       // fires
        for (var i = 0; i < 20; i++) { Run(84, 2); Run(86, 12); } // dips 1°C below, back up: not re-armed
        Assert.Equal(1, Thresholds);
    }

    [Fact]
    public void Re_arms_after_cooling_down_but_respects_the_cooldown()
    {
        Run(88, 15);           // fires
        Run(70, 60);           // cools well below: re-armed
        Run(88, 15);           // within 15 minutes: quiet
        Assert.Equal(1, Thresholds);
        Run(70, 15 * 60);      // wait out the cooldown
        Run(88, 15);
        Assert.Equal(2, Thresholds);
    }

    [Fact]
    public void Disabled_alerts_stay_quiet()
    {
        _settings.Enabled = false;
        Run(99, 60, new SafetyState(true, "CPU reached 99°C."));
        Assert.Empty(_monitor.Recent);
    }

    [Fact]
    public void Safety_override_notifies_once_per_episode()
    {
        Run(70, 3, new SafetyState(true, "CPU reached 91°C."));
        Run(70, 3);
        Run(70, 3, new SafetyState(true, "CPU reached 92°C."));
        Assert.Equal(2, _monitor.Recent.Count(e => e.Kind == "safety"));
    }

    [Fact]
    public void Event_ids_always_increase()
    {
        var a = _monitor.Test();
        var b = _monitor.Test();
        Assert.True(b.Id > a.Id);
    }
}
