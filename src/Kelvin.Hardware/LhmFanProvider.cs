using Kelvin.Core;
using LibreHardwareMonitor.Hardware;

namespace Kelvin.Hardware;

/// <summary>
/// Real sensors and fan control through LibreHardwareMonitor. Needs administrator rights and the
/// PawnIO driver to reach the motherboard's Super I/O chip (Nuvoton / ITE).
/// </summary>
public sealed class LhmFanProvider : IFanProvider
{
    private readonly Computer _computer = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMotherboardEnabled = true,
        IsControllerEnabled = true,
    };

    private readonly Dictionary<string, ISensor> _controls = new();

    // A graphics driver update takes away memory the library reads AMD sensors from (see AmdGpuGuard).
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RescanEvery = TimeSpan.FromSeconds(30), RescanFor = TimeSpan.FromMinutes(5);
    private readonly DateTime _started = DateTime.UtcNow;
    private readonly DateTime? _testResetAt = Environment.GetEnvironmentVariable("KELVIN_TEST_DRIVER_RESET") == "1" ? DateTime.UtcNow.AddSeconds(30) : null;
    private DateTime? _driverGoneAt;
    private DateTime _lastRescan = DateTime.UtcNow;
    private bool _resetReported;

    /// <summary>
    /// Called once, a few seconds after the graphics driver was found to have reset. The card's
    /// sensors can't be read again by this process, so the owner should restart it cleanly.
    /// </summary>
    public Action? GraphicsDriverReset { get; set; }

    /// <summary>True from the moment a graphics driver reset is noticed. The card's sensors are missing until a restart.</summary>
    public bool GraphicsDriverGone => _driverGoneAt is not null;

    public string Name => "LibreHardwareMonitor";
    public bool IsSimulated => false;
    public IReadOnlyList<FanReading> Fans { get; private set; } = [];
    public IReadOnlyList<SensorReading> Sensors { get; private set; } = [];

    public IReadOnlyList<HardwareInfo> Hardware =>
        _computer.Hardware
            .SelectMany(h => new[] { new HardwareInfo(h.HardwareType.ToString(), h.Name) }
                .Concat(h.SubHardware.Select(s => new HardwareInfo(s.HardwareType.ToString(), s.Name, h.Name))))
            .ToList();

    public void Open()
    {
        _computer.Open();
        Update();
    }

    public void Update()
    {
        var fans = new List<FanReading>();
        var sensors = new List<SensorReading>();
        _controls.Clear();

        foreach (var hw in _computer.Hardware)
        {
            // Reading an AMD card after its driver was updated or reset would crash the process,
            // and nothing can catch that. Leave the card out instead; its sensors go missing, and
            // any fan that follows them is handed back to the BIOS until Kelvin has restarted.
            if (_driverGoneAt is not null && hw.HardwareType == HardwareType.GpuAmd) continue;
            if (!AmdGpuGuard.SafeToUpdate(hw) || (_testResetAt <= DateTime.UtcNow && hw.HardwareType == HardwareType.GpuAmd))
            {
                _driverGoneAt = DateTime.UtcNow;
                continue;
            }

            hw.Update();
            foreach (var sub in hw.SubHardware) sub.Update();

            foreach (var h in new[] { hw }.Concat(hw.SubHardware))
            {
                var role = hw.HardwareType switch
                {
                    HardwareType.Cpu => SensorRole.Cpu,
                    HardwareType.GpuAmd or HardwareType.GpuNvidia or HardwareType.GpuIntel => SensorRole.Gpu,
                    HardwareType.Motherboard => SensorRole.Board,
                    _ => SensorRole.Other,
                };

                foreach (var s in h.Sensors)
                {
                    if (s.Value is not { } v) continue;
                    var id = s.Identifier.ToString();
                    switch (s.SensorType)
                    {
                        case SensorType.Temperature:
                            sensors.Add(new(id, s.Name, hw.Name, role, SensorKind.Temperature, Math.Round(v, 1), "°C", IsPrimary(role, s)));
                            break;
                        case SensorType.Load when s.Name is "CPU Total" or "GPU Core":
                            sensors.Add(new(id, s.Name, hw.Name, role, SensorKind.Load, Math.Round(v, 1), "%"));
                            break;
                    }
                }

                // LHM exposes a fan's speed (Fan, RPM) and its duty (Control, %) as separate sensors
                // that share an index on the same chip.
                var rpms = h.Sensors.Where(s => s.SensorType == SensorType.Fan).ToDictionary(s => s.Index);
                foreach (var control in h.Sensors.Where(s => s.SensorType == SensorType.Control))
                {
                    var id = control.Identifier.ToString();
                    _controls[id] = control;
                    rpms.TryGetValue(control.Index, out var rpm);
                    var name = rpm?.Name ?? control.Name;
                    fans.Add(new FanReading(id, name, rpm?.Value, control.Value, control.Control is not null,
                        name.Contains("pump", StringComparison.OrdinalIgnoreCase)));
                }
            }
        }

        Fans = fans;
        Sensors = SensorList.NormalizePrimary(sensors);

        // Give the new driver a moment to finish installing, then ask for the restart, once.
        if (_driverGoneAt is { } gone && !_resetReported && DateTime.UtcNow - gone >= SettleTime)
        {
            _resetReported = true;
            GraphicsDriverReset?.Invoke();
        }
        RescanForGraphicsCard();
    }

    /// <summary>
    /// The library looks for graphics cards once, when it opens. Right after a driver update (or a
    /// restart that follows one) the card may not be ready yet, so for the first few minutes, as
    /// long as no card has been found, look again every half minute.
    /// </summary>
    private void RescanForGraphicsCard()
    {
        var now = DateTime.UtcNow;
        if (_driverGoneAt is not null || now - _started > RescanFor || now - _lastRescan < RescanEvery) return;
        _lastRescan = now;
        if (_computer.Hardware.Any(h => h.HardwareType is HardwareType.GpuAmd or HardwareType.GpuNvidia or HardwareType.GpuIntel)) return;
        try
        {
            _computer.IsGpuEnabled = false;
            _computer.IsGpuEnabled = true;
        }
        catch (Exception) { /* looking again is a bonus; never let it stop the fan loop */ }
    }

    private static bool IsPrimary(SensorRole role, ISensor s) => role switch
    {
        SensorRole.Cpu => s.Name is "Core (Tctl/Tdie)" or "CPU Package" or "Core (Tctl)",
        SensorRole.Gpu => s.Name is "GPU Core",
        SensorRole.Board => s.Name is "Motherboard" or "System",
        _ => false,
    };

    public void SetFanPercent(string fanId, double percent)
    {
        if (_controls.TryGetValue(fanId, out var s) && s.Control is { } c)
            c.SetSoftware((float)Math.Clamp(percent, 0, 100));
    }

    public void RestoreDefault(string fanId)
    {
        if (_controls.TryGetValue(fanId, out var s)) s.Control?.SetDefault();
    }

    public void Dispose()
    {
        foreach (var s in _controls.Values) s.Control?.SetDefault();
        _computer.Close();
    }
}
