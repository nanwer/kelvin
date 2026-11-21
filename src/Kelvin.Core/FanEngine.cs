namespace Kelvin.Core;

public sealed record SafetyState(bool Override, string? Reason);

public sealed record CalibrationState(bool Running, double Progress, string? Message);

/// <summary>
/// The fan control loop. Once per second it reads sensors, applies each fan's curve with smoothing,
/// and enforces safety: critical temperatures force every fan to 100%, and on exit every fan goes
/// back to BIOS control.
/// </summary>
public sealed class FanEngine : IDisposable
{
    private const int HistoryLength = 120;

    private readonly IFanProvider _hw;
    private readonly SettingsStore _store;
    private readonly object _gate = new();
    private readonly Dictionary<string, double> _applied = new();
    // The averaged temperature each fan follows, and which sensor it was averaged from.
    private readonly Dictionary<string, (string Sensor, double Temp)> _smoothed = new();

    /// <summary>
    /// However long the averaging, a fan never treats the temperature as more than this far below
    /// the live reading. A real, sustained rise therefore gets a response straight away.
    /// </summary>
    public const double SpikeHeadroom = 15;
    /// <summary>How fast the duty itself moves, so changes are heard as a glide, not a step.</summary>
    private const double RampUpSeconds = 2, RampDownSeconds = 4;
    private readonly HashSet<string> _controlled = new();
    private readonly Dictionary<string, Queue<double>> _history = new();
    private IReadOnlyList<SensorReading> _sensors = [];
    private IReadOnlyList<FanReading> _fans = [];
    private bool _calibrating;
    private bool _disposed;

    public FanEngine(IFanProvider hw, SettingsStore store)
    {
        _hw = hw;
        _store = store;
    }

    public SafetyState Safety { get; private set; } = new(false, null);
    public CalibrationState Calibration { get; private set; } = new(false, 0, null);

    public IReadOnlyList<SensorReading> Sensors { get { lock (_gate) return _sensors; } }
    public IReadOnlyList<FanReading> Fans { get { lock (_gate) return _fans; } }

    public IReadOnlyDictionary<string, double[]> History
    {
        get { lock (_gate) return _history.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()); }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        Tick(1);
        while (await timer.WaitForNextTickAsync(ct))
        {
            try { Tick(1); }
            catch (Exception) { RestoreAll(); }
        }
    }

    private void Tick(double dt)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _hw.Update();
            _fans = _hw.Fans;
            _sensors = WithVirtualSensors(_hw.Sensors);
            foreach (var s in _sensors.Where(s => s.Kind == SensorKind.Temperature && (s.Primary || s.Role == SensorRole.Virtual)))
            {
                if (!_history.TryGetValue(s.Id, out var q)) _history[s.Id] = q = new Queue<double>();
                q.Enqueue(Math.Round(s.Value, 1));
                while (q.Count > HistoryLength) q.Dequeue();
            }

            var critical = CheckCritical();
            Safety = new SafetyState(critical is not null, critical);

            if (_calibrating) return; // calibration drives the fans itself; safety is checked there too

            if (critical is not null)
            {
                foreach (var fan in _fans.Where(f => f.Controllable)) Apply(fan.Id, 100);
                return;
            }

            var settings = _store.Read(s => s.Fans.Select(f => f.Clone()).ToList());
            foreach (var fan in _fans.Where(f => f.Controllable))
            {
                var cfg = settings.FirstOrDefault(s => s.FanId == fan.Id);
                if (cfg is null || cfg.Mode == FanMode.Bios)
                {
                    Release(fan.Id);
                    continue;
                }

                double target;
                if (cfg.Mode == FanMode.Fixed)
                {
                    target = cfg.FixedPercent;
                }
                else
                {
                    var source = _sensors.FirstOrDefault(s => s.Id == cfg.SourceSensorId);
                    if (source is null || !double.IsFinite(source.Value))
                    {
                        // Losing the input sensor must never leave a fan parked at a low speed.
                        Release(fan.Id);
                        continue;
                    }
                    target = Curves.Evaluate(cfg.Points, Smooth(fan.Id, source, cfg.SmoothSeconds, dt));
                }

                var floor = Math.Max(cfg.MinPercent, fan.IsPump ? 50 : 0);
                target = Math.Clamp(Math.Max(target, floor), 0, 100);

                var current = _applied.TryGetValue(fan.Id, out var c) ? c : fan.Percent ?? target;
                // Ramp up twice as fast as down: react to heat quickly, settle quietly.
                var tau = target > current ? RampUpSeconds : RampDownSeconds;
                var next = current + (target - current) * (1 - Math.Exp(-dt / tau));
                if (Math.Abs(next - target) < 0.5) next = target;
                Apply(fan.Id, next);
            }
        }
    }

    /// <summary>
    /// The temperature a fan's curve is read at: the running average, but never more than
    /// <see cref="SpikeHeadroom"/> below the live reading. A short spike barely moves it; a real
    /// rise moves it at once and fully within a minute or so. Critical temperatures bypass all of
    /// this (see <see cref="CheckCritical"/>).
    /// </summary>
    private double Smooth(string fanId, SensorReading source, double seconds, double dt)
    {
        var raw = source.Value;
        if (seconds <= 0 || !_smoothed.TryGetValue(fanId, out var prev) || prev.Sensor != source.Id)
        {
            _smoothed[fanId] = (source.Id, raw);
            return raw;
        }
        var avg = prev.Temp + (raw - prev.Temp) * (1 - Math.Exp(-dt / seconds));
        _smoothed[fanId] = (source.Id, avg);
        return Math.Max(avg, raw - SpikeHeadroom);
    }

    private string? CheckCritical()
    {
        // The override exists to force fans Kelvin drives to full speed. With none (e.g. a laptop whose
        // fans are run by its own controller) it can't do anything, so it never claims to.
        var controllable = _fans.Count(f => f.Controllable);
        if (controllable == 0) return null;

        var safety = _store.Read(s => s.Safety);
        var fans = controllable == 1 ? "The fan Kelvin controls is" : $"All {controllable} fans Kelvin controls are";
        foreach (var s in _sensors.Where(s => s.Kind == SensorKind.Temperature && s.Primary))
        {
            if (s.Role == SensorRole.Cpu && s.Value >= safety.CriticalCpuTemp)
                return $"CPU reached {s.Value:0}°C. {fans} at full speed until it cools down.";
            if (s.Role == SensorRole.Gpu && s.Value >= safety.CriticalGpuTemp)
                return $"GPU reached {s.Value:0}°C. {fans} at full speed until it cools down.";
        }
        return null;
    }

    private static IReadOnlyList<SensorReading> WithVirtualSensors(IReadOnlyList<SensorReading> sensors)
    {
        var temps = sensors.Where(s => s.Kind == SensorKind.Temperature && s.Primary && s.Role is SensorRole.Cpu or SensorRole.Gpu).ToList();
        if (temps.Count == 0) return sensors;
        var hottest = temps.MaxBy(s => s.Value)!;
        return [.. sensors, new SensorReading(SensorIds.Hottest, "Hottest of CPU and GPU", "Kelvin", SensorRole.Virtual,
            SensorKind.Temperature, hottest.Value, "°C")];
    }

    private void Apply(string fanId, double percent)
    {
        percent = Math.Round(percent, 1);
        if (_applied.TryGetValue(fanId, out var prev) && Math.Abs(prev - percent) < 0.05 && _controlled.Contains(fanId)) return;
        _hw.SetFanPercent(fanId, percent);
        _applied[fanId] = percent;
        _controlled.Add(fanId);
    }

    private void Release(string fanId)
    {
        if (!_controlled.Remove(fanId)) return;
        _applied.Remove(fanId);
        _hw.RestoreDefault(fanId);
    }

    /// <summary>Gives every fan back to the BIOS. Called on shutdown and on any unexpected error.</summary>
    public void RestoreAll()
    {
        lock (_gate)
        {
            foreach (var id in _controlled.ToList())
            {
                try { _hw.RestoreDefault(id); } catch { /* keep going: every fan must be released */ }
            }
            _controlled.Clear();
            _applied.Clear();
        }
    }

    /// <summary>
    /// Steps each controllable fan through a range of duties and records the RPM, so the advisor knows
    /// each fan's real range and stall point. Aborts if the CPU or GPU gets hot.
    /// </summary>
    public async Task<IReadOnlyList<FanProfile>> CalibrateAsync(CancellationToken ct)
    {
        List<FanReading> fans;
        lock (_gate)
        {
            if (_calibrating) throw new InvalidOperationException("Calibration is already running.");
            _calibrating = true;
            // GPU fans are left to the card: stopping one mid-measurement isn't worth the risk.
            fans = _fans.Where(f => f.Controllable && !f.IsGpuFan).ToList();
        }

        double[] fanSteps = [100, 80, 60, 45, 30, 20, 10, 0];
        double[] pumpSteps = [100, 80, 60];
        var settle = TimeSpan.FromSeconds(_hw.IsSimulated ? 2.5 : 5);
        var results = fans.ToDictionary(f => f.Id, f => new FanProfile { FanId = f.Id });
        var abortTemp = 80.0;

        try
        {
            for (var i = 0; i < fanSteps.Length; i++)
            {
                Calibration = new CalibrationState(true, (double)i / fanSteps.Length, $"Measuring fans at {fanSteps[i]:0}%");
                lock (_gate)
                {
                    foreach (var fan in fans)
                    {
                        var steps = fan.IsPump ? pumpSteps : fanSteps;
                        if (i < steps.Length) Apply(fan.Id, steps[i]);
                    }
                }

                await Task.Delay(settle, ct);

                lock (_gate)
                {
                    var hot = _sensors.FirstOrDefault(s => s.Kind == SensorKind.Temperature && s.Primary && s.Value >= abortTemp);
                    if (hot is not null)
                        throw new InvalidOperationException($"Stopped: {hot.Hardware} reached {hot.Value:0}°C. Try again when the PC is idle.");

                    foreach (var fan in fans)
                    {
                        var steps = fan.IsPump ? pumpSteps : fanSteps;
                        if (i >= steps.Length) continue;
                        var rpm = _fans.FirstOrDefault(f => f.Id == fan.Id)?.Rpm ?? 0;
                        results[fan.Id].Points.Add(new CalibrationPoint(steps[i], Math.Round(rpm)));
                    }
                }
            }

            foreach (var p in results.Values)
            {
                var spinning = p.Points.Where(x => x.Rpm >= 150).ToList();
                p.Connected = spinning.Count > 0;
                p.MaxRpm = spinning.Count > 0 ? spinning.Max(x => x.Rpm) : 0;
                p.MinRpm = spinning.Count > 0 ? spinning.Min(x => x.Rpm) : 0;
                var stopped = p.Points.Where(x => x.Rpm < 150).ToList();
                p.StopPercent = p.Connected && stopped.Count > 0 ? stopped.Max(x => x.Percent) : null;
                p.MeasuredAt = DateTimeOffset.Now;
            }

            var profiles = results.Values.ToList();
            _store.Mutate(s =>
            {
                s.FanProfiles.RemoveAll(x => results.ContainsKey(x.FanId));
                s.FanProfiles.AddRange(profiles);
            });
            Calibration = new CalibrationState(false, 1, "Done. Every fan was measured.");
            return profiles;
        }
        catch (Exception ex)
        {
            Calibration = new CalibrationState(false, 0, ex is OperationCanceledException ? "Calibration was cancelled." : ex.Message);
            throw;
        }
        finally
        {
            lock (_gate)
            {
                _calibrating = false;
                // Next tick re-applies each fan's own settings; anything in BIOS mode is released.
                foreach (var fan in fans) _applied.Remove(fan.Id);
            }
        }
    }

    public void Dispose()
    {
        RestoreAll();
        lock (_gate) _disposed = true;
    }
}
