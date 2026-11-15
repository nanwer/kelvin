namespace Kelvin.Core;

/// <summary>
/// Reads temperatures and fan speeds, and sets fan duty. Implementations: LibreHardwareMonitor
/// (real hardware) and Simulated (development). Contributors add new hardware by implementing this.
/// </summary>
public interface IFanProvider : IDisposable
{
    string Name { get; }
    bool IsSimulated { get; }
    void Open();
    void Update();
    IReadOnlyList<FanReading> Fans { get; }
    IReadOnlyList<SensorReading> Sensors { get; }
    void SetFanPercent(string fanId, double percent);
    /// <summary>Hand the fan back to the BIOS / firmware curve.</summary>
    void RestoreDefault(string fanId);
}

/// <summary>Drives RGB devices. Implementations: OpenRGB SDK (real hardware) and Simulated.</summary>
public interface IRgbProvider : IDisposable
{
    string Name { get; }
    bool IsSimulated { get; }
    bool IsConnected { get; }
    /// <summary>Human-readable reason when not connected, e.g. "OpenRGB isn't running".</summary>
    string? Status { get; }
    void Open();
    /// <summary>Picks up devices found since connecting (detection can take a while).</summary>
    void Refresh();
    IReadOnlyList<RgbDevice> Devices { get; }
    void SetColors(string deviceId, IReadOnlyList<RgbColor> colors);
}
