using Kelvin.Core;

namespace Kelvin.Core.Tests;

public class ThermalHealthTests
{
    private static readonly DateTimeOffset Today = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    private static SensorReading[] Sensors(double cpu, double cpuLoad, double board = 35) =>
    [
        new("cpu", "CPU", "AMD Ryzen 7 5800X", SensorRole.Cpu, SensorKind.Temperature, cpu, "°C", true),
        new("cpuload", "CPU total", "AMD Ryzen 7 5800X", SensorRole.Cpu, SensorKind.Load, cpuLoad, "%"),
        new("board", "Motherboard", "Board", SensorRole.Board, SensorKind.Temperature, board, "°C", true),
    ];

    private static FanReading[] Fans(double duty) => [new("f", "CPU Fan", 1000, duty, true, false)];

    /// <summary>Fills history day by day: each day gets two hours at medium load.</summary>
    private static ThermalHistory Build(Func<int, (double cpu, double board, double duty)> dayAt, int days = 35)
    {
        var clock = Today;
        var history = new ThermalHistory(null, () => clock);
        for (var age = days; age >= 0; age--)
        {
            clock = Today.AddDays(-age);
            var (cpu, board, duty) = dayAt(age);
            history.Record(Sensors(cpu, 50, board), Fans(duty), minutes: 120);
        }
        clock = Today;
        return history;
    }

    private static PartHealth Cpu(ThermalHistory h) => h.Analyze(Sensors(60, 50)).Parts.Single(p => p.Key == "cpu");

    [Fact]
    public void Flags_a_cpu_that_runs_hotter_at_the_same_load()
    {
        var h = Build(age => (age < 7 ? 72 : 65, 35, 50));
        var cpu = Cpu(h);
        Assert.Equal(HealthStatus.Warmer, cpu.Status);
        Assert.Equal(7, cpu.Change!.Value, precision: 1);
        Assert.Contains("hotter", cpu.Headline);
    }

    [Fact]
    public void A_warmer_room_is_not_mistaken_for_dust()
    {
        // CPU and motherboard both 6°C warmer: the room heated up, the cooler didn't get worse.
        var h = Build(age => age < 7 ? (71, 41, 50) : (65, 35, 50));
        Assert.Equal(HealthStatus.Ok, Cpu(h).Status);
    }

    [Fact]
    public void Changed_fan_settings_are_not_compared()
    {
        // Hotter, but the fans now spin much slower: the user chose quieter curves.
        var h = Build(age => age < 7 ? (72, 35, 30) : (65, 35, 60));
        Assert.Equal(HealthStatus.SettingsChanged, Cpu(h).Status);
    }

    [Fact]
    public void Says_it_is_still_learning_before_two_weeks()
    {
        var h = Build(_ => (65, 35, 50), days: 5);
        var cpu = Cpu(h);
        Assert.Equal(HealthStatus.Learning, cpu.Status);
        Assert.Equal(5, cpu.DaysCollected);
    }

    [Fact]
    public void Different_loads_are_never_mixed()
    {
        var clock = Today.AddDays(-30);
        var h = new ThermalHistory(null, () => clock);
        h.Record(Sensors(55, 50), Fans(50), 120);      // medium load, a month ago
        clock = Today;
        h.Record(Sensors(80, 95), Fans(50), 120);      // heavy load today: much hotter, but not comparable
        Assert.NotEqual(HealthStatus.Warmer, Cpu(h).Status);
    }

    [Fact]
    public void Demo_history_shows_a_dusty_cpu_and_a_healthy_gpu()
    {
        var h = new ThermalHistory(null, () => Today);
        h.SeedDemo();
        var report = h.Analyze(Sensors(60, 50));
        Assert.Equal(HealthStatus.Warmer, report.Parts.Single(p => p.Key == "cpu").Status);
        Assert.Equal(HealthStatus.Ok, report.Parts.Single(p => p.Key == "gpu").Status);
    }
}
