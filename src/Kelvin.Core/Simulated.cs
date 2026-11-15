namespace Kelvin.Core.Simulated;

/// <summary>
/// A believable stand-in for real hardware so the UI and engine can be developed without admin
/// rights or drivers. Modelled on an ITX build: ROG Strix B550-I, Ryzen 7 5800X, RX 9070 XT.
/// CPU temperature reacts to load and to the CPU fan; GPU and board temperatures react to case airflow.
/// </summary>
public sealed class SimulatedFanProvider : IFanProvider
{
    private sealed class SimFan(string id, string name, double maxRpm, double stopPercent, bool connected, bool isPump)
    {
        public string Id = id;
        public string Name = name;
        public double MaxRpm = maxRpm;
        public double StopPercent = stopPercent;
        public bool Connected = connected;
        public bool IsPump = isPump;
        public double? Software;
        public double Duty = 40;
        public double Rpm;
    }

    private readonly SimFan[] _fans =
    [
        new("sim/fan/cpu", "CPU Fan", 1600, 15, true, false),
        new("sim/fan/chassis", "Chassis Fan", 1200, 20, true, false),
        new("sim/fan/aio", "AIO Pump", 0, 0, false, true),
    ];

    private readonly Random _rng = new(5800);
    private DateTime _last = DateTime.UtcNow;
    private double _cpuLoad = 0.08, _gpuLoad = 0.03;
    private double _cpuTemp = 42, _gpuTemp = 38, _boardTemp = 33;
    private double _burst;

    public string Name => "Simulated";
    public bool IsSimulated => true;
    public IReadOnlyList<FanReading> Fans { get; private set; } = [];
    public IReadOnlyList<SensorReading> Sensors { get; private set; } = [];

    public void Open() => Update();

    public void Update()
    {
        var now = DateTime.UtcNow;
        var dt = Math.Clamp((now - _last).TotalSeconds, 0.05, 2);
        _last = now;

        // Load: a calm desktop with occasional bursts of work (a game, a compile, a render).
        if (_burst <= 0 && _rng.NextDouble() < 0.02 * dt) _burst = 20 + _rng.NextDouble() * 40;
        _burst -= dt;
        var cpuTarget = _burst > 0 ? 0.65 + _rng.NextDouble() * 0.3 : 0.05 + _rng.NextDouble() * 0.1;
        var gpuTarget = _burst > 0 ? 0.85 + _rng.NextDouble() * 0.15 : 0.02 + _rng.NextDouble() * 0.05;
        _cpuLoad += (cpuTarget - _cpuLoad) * Math.Min(1, dt * 1.5);
        _gpuLoad += (gpuTarget - _gpuLoad) * Math.Min(1, dt * 0.8);

        foreach (var f in _fans)
        {
            f.Duty = f.Software ?? BiosDuty(f);
            var target = !f.Connected || f.Duty < f.StopPercent ? 0 : f.MaxRpm * (0.25 + 0.75 * f.Duty / 100);
            f.Rpm += (target - f.Rpm) * Math.Min(1, dt * 1.8);
        }

        var cpuFan = _fans[0].Rpm / _fans[0].MaxRpm;
        var caseFan = _fans[1].Rpm / _fans[1].MaxRpm;
        // The 5800X runs hot and spiky; airflow pulls the target down.
        var cpuEq = 36 + _cpuLoad * 56 - cpuFan * 16 + (_boardTemp - 33) * 0.4;
        var gpuEq = 34 + _gpuLoad * 42 - caseFan * 9;
        var boardEq = 30 + (_cpuTemp - 40) * 0.12 + (_gpuTemp - 35) * 0.18 - caseFan * 4;
        _cpuTemp += (cpuEq - _cpuTemp) * Math.Min(1, dt / 2.5);
        _gpuTemp += (gpuEq - _gpuTemp) * Math.Min(1, dt / 9);
        _boardTemp += (boardEq - _boardTemp) * Math.Min(1, dt / 20);

        Fans = _fans.Select(f => new FanReading(f.Id, f.Name, f.Connected ? Math.Round(f.Rpm) : 0, Math.Round(f.Duty, 1), true, f.IsPump)).ToList();
        Sensors =
        [
            new("sim/cpu/tctl", "CPU (Tctl/Tdie)", "AMD Ryzen 7 5800X", SensorRole.Cpu, SensorKind.Temperature, Jitter(_cpuTemp, 0.6), "°C", Primary: true),
            new("sim/cpu/load", "CPU total", "AMD Ryzen 7 5800X", SensorRole.Cpu, SensorKind.Load, Math.Round(_cpuLoad * 100, 1), "%"),
            new("sim/gpu/core", "GPU core", "AMD Radeon RX 9070 XT", SensorRole.Gpu, SensorKind.Temperature, Jitter(_gpuTemp, 0.3), "°C", Primary: true),
            new("sim/gpu/hotspot", "GPU hotspot", "AMD Radeon RX 9070 XT", SensorRole.Gpu, SensorKind.Temperature, Jitter(_gpuTemp + 12 * _gpuLoad + 3, 0.4), "°C"),
            new("sim/gpu/load", "GPU core", "AMD Radeon RX 9070 XT", SensorRole.Gpu, SensorKind.Load, Math.Round(_gpuLoad * 100, 1), "%"),
            new("sim/board/system", "Motherboard", "ROG STRIX B550-I GAMING", SensorRole.Board, SensorKind.Temperature, Jitter(_boardTemp, 0.2), "°C", Primary: true),
            new("sim/board/chipset", "Chipset", "ROG STRIX B550-I GAMING", SensorRole.Board, SensorKind.Temperature, Jitter(_boardTemp + 14, 0.2), "°C"),
        ];
    }

    private static double BiosDuty(SimFan f) => f.IsPump ? 100 : 60;

    private double Jitter(double v, double amount) => Math.Round(v + (_rng.NextDouble() - 0.5) * amount, 1);

    public void SetFanPercent(string fanId, double percent)
    {
        var f = _fans.FirstOrDefault(x => x.Id == fanId) ?? throw new KeyNotFoundException(fanId);
        f.Software = Math.Clamp(percent, 0, 100);
    }

    public void RestoreDefault(string fanId)
    {
        var f = _fans.FirstOrDefault(x => x.Id == fanId);
        if (f is not null) f.Software = null;
    }

    public void Dispose() { }
}

public sealed class SimulatedRgbProvider : IRgbProvider
{
    public string Name => "Simulated";
    public bool IsSimulated => true;
    public bool IsConnected => true;
    public string? Status => null;

    public IReadOnlyList<RgbDevice> Devices { get; } =
    [
        new("sim/rgb/board", "ROG STRIX B550-I GAMING", "ASUS", "Motherboard", 8, [new("Aura mainboard", 8)]),
        new("sim/rgb/ram1", "Vengeance RGB Pro (slot 1)", "Corsair", "Memory", 10, [new("DRAM", 10)]),
        new("sim/rgb/ram2", "Vengeance RGB Pro (slot 2)", "Corsair", "Memory", 10, [new("DRAM", 10)]),
        new("sim/rgb/gpu", "Radeon RX 9070 XT", "Gigabyte", "Graphics card", 12, [new("Logo and side bar", 12)]),
        new("sim/rgb/kbd", "G213 Prodigy", "Logitech", "Keyboard", 5, [new("Zones", 5)]),
        new("sim/rgb/mouse", "Basilisk V3 Pro 35K", "Razer", "Mouse", 13, [new("Logo", 1), new("Scroll wheel", 1), new("Underglow", 11)]),
    ];

    public void Open() { }
    public void Refresh() { }
    public void SetColors(string deviceId, IReadOnlyList<RgbColor> colors) { }
    public void Dispose() { }
}
