namespace Kelvin.Core;

/// <summary>Which temperature an alert watches. Roles, not sensor ids, so rules survive hardware changes.</summary>
public enum AlertSource { Cpu, Gpu, Board, Hottest }

public sealed class AlertRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public bool Enabled { get; set; } = true;
    public AlertSource Source { get; set; } = AlertSource.Cpu;
    public double Threshold { get; set; } = 85;
    /// <summary>How long the temperature must stay at or above the threshold. Filters out spikes.</summary>
    public double SustainSeconds { get; set; } = 10;
}

public sealed class AlertSettings
{
    public bool Enabled { get; set; } = true;
    public List<AlertRule> Rules { get; set; } =
    [
        new() { Id = "cpu", Source = AlertSource.Cpu, Threshold = 85 },
        new() { Id = "gpu", Source = AlertSource.Gpu, Threshold = 85 },
    ];
    /// <summary>Notify when a safety limit forces every fan to full speed.</summary>
    public bool NotifyOnSafety { get; set; } = true;
    /// <summary>Notify when Health finds a part running hotter than a few weeks ago.</summary>
    public bool NotifyOnHealth { get; set; } = true;
}

public enum AlertSeverity { Info, Warning, Critical }

public sealed record AlertEvent(
    long Id,
    DateTimeOffset At,
    string Kind,
    AlertSeverity Severity,
    string Title,
    string Body,
    string? RuleId = null);

/// <summary>
/// Turns temperature readings into alert events. A rule fires once when its temperature stays at or
/// above the threshold for the sustain time, then stays quiet until the temperature drops
/// <see cref="Hysteresis"/> below the threshold, and never repeats within <see cref="Cooldown"/>.
/// The desktop app turns new events into Windows notifications.
/// </summary>
public sealed class AlertMonitor(Func<DateTimeOffset>? now = null)
{
    public const double Hysteresis = 3;
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(15);
    private const int KeepEvents = 50;

    private sealed class RuleState
    {
        public DateTimeOffset? AboveSince;
        public bool Firing;
        public DateTimeOffset LastNotified = DateTimeOffset.MinValue;
    }

    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.Now);
    private readonly object _gate = new();
    private readonly Dictionary<string, RuleState> _rules = new();
    private readonly List<AlertEvent> _events = [];
    private readonly HashSet<string> _healthNotified = [];
    private bool _safetyWasOn;
    // Ids keep increasing across restarts, so the app never mistakes a new event for one it has shown.
    private long _nextId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public IReadOnlyList<AlertEvent> Recent { get { lock (_gate) return _events.ToList(); } }

    public IReadOnlyList<string> Firing
    {
        get { lock (_gate) return _rules.Where(r => r.Value.Firing).Select(r => r.Key).ToList(); }
    }

    public static SensorReading? SensorFor(AlertSource source, IReadOnlyList<SensorReading> sensors) => source switch
    {
        AlertSource.Hottest => sensors.FirstOrDefault(s => s.Id == SensorIds.Hottest),
        _ => sensors.FirstOrDefault(s => s.Primary && s.Kind == SensorKind.Temperature && s.Role == source switch
        {
            AlertSource.Cpu => SensorRole.Cpu,
            AlertSource.Gpu => SensorRole.Gpu,
            _ => SensorRole.Board,
        }),
    };

    public static string SourceName(AlertSource s) => s switch
    {
        AlertSource.Cpu => "CPU",
        AlertSource.Gpu => "Graphics card",
        AlertSource.Board => "Motherboard",
        _ => "Hottest of CPU and GPU",
    };

    public void Evaluate(AlertSettings settings, IReadOnlyList<SensorReading> sensors, SafetyState safety, HealthReport? health)
    {
        var now = _now();
        lock (_gate)
        {
            // Rules the user removed shouldn't keep state around.
            foreach (var gone in _rules.Keys.Except(settings.Rules.Select(r => r.Id)).ToList()) _rules.Remove(gone);

            foreach (var rule in settings.Rules)
            {
                if (!_rules.TryGetValue(rule.Id, out var st)) _rules[rule.Id] = st = new RuleState();
                var reading = SensorFor(rule.Source, sensors);
                if (!settings.Enabled || !rule.Enabled || reading is null || !double.IsFinite(reading.Value))
                {
                    st.AboveSince = null;
                    st.Firing = false;
                    continue;
                }

                var value = reading.Value;
                if (value >= rule.Threshold)
                {
                    st.AboveSince ??= now;
                    var held = (now - st.AboveSince.Value).TotalSeconds >= rule.SustainSeconds;
                    if (held && !st.Firing)
                    {
                        st.Firing = true;
                        if (now - st.LastNotified >= Cooldown)
                        {
                            st.LastNotified = now;
                            var name = SourceName(rule.Source);
                            Add(now, "threshold", value >= rule.Threshold + 10 ? AlertSeverity.Critical : AlertSeverity.Warning,
                                $"{name} is at {value:0}°C",
                                rule.SustainSeconds >= 1
                                    ? $"It has been above your {rule.Threshold:0}°C alert for {rule.SustainSeconds:0} seconds."
                                    : $"It went above your {rule.Threshold:0}°C alert.",
                                rule.Id);
                        }
                    }
                }
                else
                {
                    st.AboveSince = null;
                    if (st.Firing && value <= rule.Threshold - Hysteresis) st.Firing = false;
                }
            }

            if (safety.Override && !_safetyWasOn && settings.Enabled && settings.NotifyOnSafety)
                Add(now, "safety", AlertSeverity.Critical, "Fans at full speed", safety.Reason ?? "A safety limit was reached.");
            _safetyWasOn = safety.Override;

            foreach (var part in health?.Parts ?? [])
            {
                var key = $"{part.Key}:{part.Headline}";
                if (part.Status != HealthStatus.Warmer || !settings.Enabled || !settings.NotifyOnHealth || !_healthNotified.Add(key)) continue;
                Add(now, "health", AlertSeverity.Warning, part.Headline, "Open Kelvin's Health page to see what changed and what to do.");
            }
        }
    }

    public AlertEvent Test()
    {
        lock (_gate) return Add(_now(), "test", AlertSeverity.Info, "Kelvin notifications are working",
            "You'll get one like this when a temperature stays above one of your alerts.");
    }

    private AlertEvent Add(DateTimeOffset at, string kind, AlertSeverity severity, string title, string body, string? ruleId = null)
    {
        var e = new AlertEvent(++_nextId, at, kind, severity, title, body, ruleId);
        _events.Add(e);
        if (_events.Count > KeepEvents) _events.RemoveAt(0);
        return e;
    }
}
