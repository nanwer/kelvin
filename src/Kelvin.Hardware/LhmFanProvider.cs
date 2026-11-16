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
