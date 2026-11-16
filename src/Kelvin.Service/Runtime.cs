using Kelvin.Core;

namespace Kelvin.Service;

/// <summary>Owns the hardware and engines, runs their loops, and builds the snapshot the UI renders.</summary>
public sealed class Runtime(
    IFanProvider fanHw,
    IRgbProvider rgbHw,
    FanEngine fans,
    LightingEngine lighting,
    SettingsStore store,
    bool elevated,
    Kelvin.Hardware.OpenRgbHost? openRgb,
    ThermalHistory thermal,
    bool demo = false) : IHostedService
{
    private static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(5);
    private readonly CancellationTokenSource _cts = new();
    private HealthReport? _health;
    private DateTimeOffset _nextAnalysis = DateTimeOffset.MinValue;

    public AlertMonitor Alerts { get; } = new();
    private readonly List<Task> _loops = [];
    private IReadOnlyList<Conflict> _conflicts = [];
    private Task? _calibration;
    private bool _driverInstalled = true;

    public FanEngine Fans => fans;
    public LightingEngine Lighting => lighting;

    public Task StartAsync(CancellationToken ct)
    {
        fanHw.Open();
        rgbHw.Open();
        _loops.Add(fans.RunAsync(_cts.Token));
        _loops.Add(lighting.RunAsync(_cts.Token));
        _loops.Add(WatchAsync(_cts.Token));
        _loops.Add(AlertsAsync(_cts.Token));
        return Task.CompletedTask;
    }

    /// <summary>Checks alert rules every second, so "above 85°C for 10 s" means 10 s.</summary>
    private async Task AlertsAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try { Alerts.Evaluate(store.Current.Alerts, fans.Sensors, fans.Safety, _health); }
            catch (Exception) { /* a bad reading must never stop alerting for good */ }
        }
    }

    private async Task WatchAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(WatchInterval);
        do
        {
            // Thermal history: skip readings taken while fans are measured or forced to full speed.
            if (!fans.Calibration.Running && !fans.Safety.Override && fans.Sensors.Count > 0)
                thermal.Record(fans.Sensors, fans.Fans, WatchInterval.TotalMinutes);
            if (DateTimeOffset.Now >= _nextAnalysis)
            {
                _health = thermal.Analyze(fans.Sensors);
                _nextAnalysis = DateTimeOffset.Now.AddMinutes(1);
            }

            _conflicts = demo ? [] : ConflictDetector.Detect();
            _driverInstalled = fanHw.IsSimulated || FanDriverInstalled();
            openRgb?.EnsureRunning();
            if (!rgbHw.IsConnected) rgbHw.Open();
            else rgbHw.Refresh();
        } while (await timer.WaitForNextTickAsync(ct));
    }

    public static string Version { get; } =
        (typeof(Runtime).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .FirstOrDefault() as System.Reflection.AssemblyInformationalVersionAttribute)?.InformationalVersion.Split('+')[0] ?? "dev";

    public HardwareReportResult Report() => HardwareReport.Build(new HardwareReportInput(
        Version,
        System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        fanHw.IsSimulated,
        _driverInstalled,
        fanHw.Hardware,
        fans.Fans,
        store.Current.FanProfiles,
        fans.Sensors,
        rgbHw.IsConnected,
        rgbHw.Status,
        rgbHw.Devices,
        _conflicts));

    public sealed record CloseResult(string Name, bool Closed, string Message);

    /// <summary>
    /// Closes a conflicting app from the known list: stops its Windows services first (so they
    /// can't relaunch it), then ends its processes. Only apps marked CanClose are touched.
    /// </summary>
    public CloseResult? CloseConflict(string key)
    {
        var app = ConflictDetector.Find(key);
        if (app is null) return null;
        if (!app.CanClose) return new(app.Name, false, app.Fix);

        var problems = new List<string>();
        if (OperatingSystem.IsWindows() && app.ServiceHints.Length > 0)
        {
            foreach (var svc in System.ServiceProcess.ServiceController.GetServices())
            {
                using (svc)
                {
                    var match = app.ServiceHints.Any(h =>
                        svc.ServiceName.Contains(h, StringComparison.OrdinalIgnoreCase) ||
                        svc.DisplayName.Contains(h, StringComparison.OrdinalIgnoreCase));
                    if (!match || svc.Status != System.ServiceProcess.ServiceControllerStatus.Running) continue;
                    try
                    {
                        svc.Stop();
                        svc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                    }
                    catch (Exception) { problems.Add(svc.DisplayName); }
                }
            }
        }

        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            using (p)
            {
                if (!app.Processes.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase)) continue;
                try
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(5000);
                }
                catch (Exception) { problems.Add(p.ProcessName); }
            }
        }

        _conflicts = ConflictDetector.Detect();
        var stillRunning = _conflicts.Any(c => c.Key == key);
        if (stillRunning)
        {
            var why = elevated ? "" : " Kelvin's service isn't running with administrator rights, which this needs.";
            return new(app.Name, false, $"Couldn't fully close {app.Name}.{why} Close it from its tray icon instead.");
        }
        return new(app.Name, true, $"Closed {app.Name}. It starts again with Windows unless you turn that off.");
    }

    /// <summary>
    /// LibreHardwareMonitor reaches the motherboard's fan chip through the PawnIO kernel driver.
    /// Without it, CPU and GPU temperatures still work but motherboard fans can't be read or set.
    /// </summary>
    private static bool FanDriverInstalled()
    {
        if (!OperatingSystem.IsWindows()) return true;
        try
        {
            return System.ServiceProcess.ServiceController.GetDevices()
                .Any(d => d.ServiceName.Equals("PawnIO", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return true; // can't tell; don't show a warning we can't back up
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _cts.Cancel();
        try { await Task.WhenAll(_loops); } catch (OperationCanceledException) { }
        fans.Dispose(); // hands every fan back to the BIOS
        thermal.Save();
        rgbHw.Dispose();
        openRgb?.Dispose();
        fanHw.Dispose();
    }

    public bool StartCalibration()
    {
        if (_calibration is { IsCompleted: false }) return false;
        _calibration = Task.Run(async () =>
        {
            try { await fans.CalibrateAsync(_cts.Token); } catch { /* state carries the message */ }
        });
        return true;
    }

    private IReadOnlyList<Recommendation> Recommendations() =>
        Advisor.Build(fans.Fans, fans.Sensors, store.Current, _conflicts, _health);

    public bool Apply(string id)
    {
        var rec = Recommendations().FirstOrDefault(r => r.Id == id);
        if (rec is null) return false;
        if (rec.Action == RecommendationAction.Calibrate) return StartCalibration();
        if (rec.Action == RecommendationAction.CloseApp) return CloseConflict(id["conflict:".Length..])?.Closed == true;
        if (rec.Settings is not { } s) return false;
        store.Mutate(x => { x.Fans.RemoveAll(f => f.FanId == s.FanId); x.Fans.Add(s); });
        return true;
    }

    public int ApplyAll()
    {
        var recs = Recommendations().Where(r => r.Action == RecommendationAction.ApplyFan && r.Settings is not null).ToList();
        store.Mutate(x =>
        {
            foreach (var r in recs)
            {
                x.Fans.RemoveAll(f => f.FanId == r.FanId);
                x.Fans.Add(r.Settings!);
            }
        });
        return recs.Count;
    }

    public object Snapshot()
    {
        var settings = store.Current;
        var sensors = fans.Sensors;
        var fanSettings = settings.Fans.ToDictionary(f => f.FanId);
        var profiles = settings.FanProfiles.ToDictionary(p => p.FanId);
        var excluded = settings.Lighting.ExcludedDeviceIds;

        return new
        {
            system = new
            {
                version = Version,
                simulated = fanHw.IsSimulated,
                demo,
                elevated,
                fanBackend = fanHw.Name,
                rgbBackend = rgbHw.Name,
                rgbConnected = rgbHw.IsConnected,
                rgbStatus = rgbHw.Status,
                // notInstalled | starting | runningManaged | runningExternal | failed | null (simulated)
                openRgb = openRgb?.State,
                fanDriverInstalled = _driverInstalled,
            },
            safety = fans.Safety,
            safetySettings = settings.Safety,
            calibration = fans.Calibration,
            sensors,
            history = fans.History,
            fans = fans.Fans.Select(f => new
            {
                f.Id, f.Name, f.Rpm, f.Percent, f.Controllable, f.IsPump,
                settings = fanSettings.GetValueOrDefault(f.Id),
                profile = profiles.GetValueOrDefault(f.Id),
            }),
            devices = rgbHw.Devices.Select(d => new { d.Id, d.Name, d.Vendor, d.Kind, d.LedCount, d.Zones, included = !excluded.Contains(d.Id) }),
            lighting = settings.Lighting,
            preview = lighting.Preview,
            conflicts = _conflicts,
            health = _health,
            alertSettings = settings.Alerts,
            alerts = new { recent = Alerts.Recent.TakeLast(20).Reverse(), firing = Alerts.Firing },
            recommendations = Recommendations(),
            presets = Curves.Presets,
        };
    }
}
