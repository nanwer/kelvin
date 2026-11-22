using System.Diagnostics;

namespace Kelvin.Core;

public enum ConflictArea { Lighting, Fans, Both }

public sealed record Conflict(string Key, string Name, ConflictArea Area, string Impact, string Fix, bool CanClose);

/// <summary>Another app that fights Kelvin over the same devices, and how to recognise it.</summary>
public sealed record ConflictApp(
    string Key,
    string Name,
    ConflictArea Area,
    string[] Processes,
    string[] ServiceHints,
    string Impact,
    string Fix,
    bool CanClose);

/// <summary>
/// Finds other RGB and fan software that fights over the same devices. Two apps writing to one
/// controller is the main reason lighting flickers, resets, or stops responding.
/// </summary>
public static class ConflictDetector
{
    private const string Returns = "It starts again with Windows unless you turn that off in its settings or in Task Manager → Startup apps.";

    public static readonly IReadOnlyList<ConflictApp> Apps =
    [
        new("razer", "Razer Synapse / Chroma", ConflictArea.Lighting,
            ["RazerAppEngine", "Razer Synapse", "RazerCentralService", "Razer Synapse Service", "RzChromaStreamServer", "RzSDKServer", "RzChromaConnectServer", "razer_elevation_service"],
            ["Razer"],
            "Takes exclusive control of Razer devices, and Chroma Connect can override other brands' lighting.",
            $"Kelvin can close it for you. Mouse settings saved to onboard memory keep working. {Returns}", true),
        new("signalrgb", "SignalRGB", ConflictArea.Lighting,
            ["SignalRgb", "SignalRgbService", "SignalRgbLauncher"],
            ["SignalRGB", "SignalRgb"],
            "Drives every device it supports, including ASUS Aura and Corsair RAM, and overwrites Kelvin's colors.",
            $"Kelvin can close it and its background service for you. {Returns}", true),
        // Never force-closed: FanControl puts the fan chip into manual mode, and only FanControl's own
        // exit puts it back. Killing it would leave fans stuck at a fixed speed until a restart.
        new("fancontrol", "FanControl", ConflictArea.Fans,
            ["FanControl"],
            [],
            "Writes to the same fan headers. Two fan controllers fighting cause speeds to jump.",
            "Right-click FanControl in the tray and choose Exit. Closing it that way hands your fans back to the BIOS; force-closing it could leave them stuck at a fixed speed, so Kelvin won't.", false),
        new("armoury", "Armoury Crate / Aura", ConflictArea.Lighting,
            ["ArmouryCrate", "ArmouryCrate.Service", "ArmourySocketServer", "LightingService", "AuraService"],
            ["ARMOURY CRATE", "LightingService", "AuraService"],
            "Controls ASUS Aura lighting on your board.",
            $"Kelvin can close it and its lighting service for you. Fan settings saved in your BIOS aren't affected. {Returns}", true),
        new("icue", "Corsair iCUE", ConflictArea.Lighting,
            ["iCUE", "Corsair.Service", "CorsairService"],
            ["Corsair", "iCUE"],
            "Controls Corsair RAM lighting and Corsair fan hubs.",
            $"Kelvin can close it for you. Corsair fan hubs fall back to their built-in settings. {Returns}", true),
        new("lghub", "Logitech G HUB", ConflictArea.Lighting,
            ["lghub", "lghub_agent"],
            [],
            "Controls Logitech keyboard and mouse lighting.",
            $"Kelvin can close it for you. Settings saved to the device's onboard memory keep working. {Returns}", true),
        new("gigabyte", "Gigabyte Control Center", ConflictArea.Lighting,
            ["GCC", "GigabyteControlCenter", "RGBFusion", "AorusEngine"],
            [],
            "Controls Gigabyte graphics card and board lighting.",
            $"Kelvin can close it for you. The graphics card goes back to its own fan settings. {Returns}", true),
    ];

    public static ConflictApp? Find(string key) => Apps.FirstOrDefault(a => a.Key == key);

    /// <summary>
    /// Process names too short to mean one program. "GCC" is Gigabyte Control Center and also the
    /// GNU compiler, so it only counts when the program runs from a folder with this in its path.
    /// </summary>
    private static readonly Dictionary<string, string> NeedsFolder = new(StringComparer.OrdinalIgnoreCase) { ["GCC"] = "GIGABYTE" };

    /// <summary>Whether a running process is one of this app's, by name and, where the name is ambiguous, by where it runs from.</summary>
    public static bool Matches(ConflictApp app, Process process)
    {
        string name;
        try { name = process.ProcessName; }
        catch { return false; }
        if (!app.Processes.Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
        if (!NeedsFolder.TryGetValue(name, out var folder)) return true;
        try { return process.MainModule?.FileName.Contains(folder, StringComparison.OrdinalIgnoreCase) == true; }
        catch { return false; } // can't see where it runs from: don't guess
    }

    public static IReadOnlyList<Conflict> Detect()
    {
        Process[] running;
        try { running = Process.GetProcesses(); }
        catch { return []; }
        try
        {
            return Apps
                .Where(a => running.Any(p => Matches(a, p)))
                .Select(a => new Conflict(a.Key, a.Name, a.Area, a.Impact, a.Fix, a.CanClose))
                .ToList();
        }
        finally
        {
            foreach (var p in running) p.Dispose();
        }
    }
}
