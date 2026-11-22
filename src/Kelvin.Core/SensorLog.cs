using System.Text.Json;

namespace Kelvin.Core;

/// <summary>One line on the History page.</summary>
/// <param name="Kind">temperature, load, rpm or duty.</param>
/// <param name="Group">What it belongs to, e.g. the CPU's name, or "Fans".</param>
public sealed record LogSeries(string Id, string Name, string Group, string Kind, string Role, bool Primary, double?[] Values);

/// <summary>Readings over a stretch of time. <see cref="Times"/> are Unix seconds, one per value.</summary>
public sealed record LogView(string Range, int StepSeconds, long[] Times, IReadOnlyList<LogSeries> Series);

/// <summary>
/// Keeps recent readings of every temperature, load and fan for the History page: the last hour
/// at one reading every 5 seconds, and the last day at one a minute (each the average of that
/// minute). The day is saved to disk now and then, so it survives a restart or an update; the
/// hour is cheap to lose and stays in memory. Times are stored with the readings, so the time
/// Kelvin wasn't running shows up as a gap instead of being drawn over.
/// </summary>
public sealed class SensorLog
{
    public const int HourStep = 5, DayStep = 60;
    private const int HourSeconds = 3600, DaySeconds = 86400, MaxSeries = 80;
    private static readonly TimeSpan SaveEvery = TimeSpan.FromMinutes(10);

    private sealed class Meta
    {
        public string Name { get; set; } = "";
        public string Group { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Role { get; set; } = "";
        public bool Primary { get; set; }
    }

    private sealed class Tier
    {
        public List<long> Times { get; set; } = [];
        public Dictionary<string, List<double?>> Values { get; set; } = new();

        public void Add(long time, IReadOnlyDictionary<string, double> sample, int keepSeconds)
        {
            foreach (var id in sample.Keys)
                if (!Values.ContainsKey(id)) Values[id] = Enumerable.Repeat<double?>(null, Times.Count).ToList();
            Times.Add(time);
            foreach (var (id, list) in Values) list.Add(sample.TryGetValue(id, out var v) ? v : null);
            Trim(time - keepSeconds);
        }

        public void Trim(long oldest)
        {
            var drop = Times.FindIndex(t => t >= oldest);
            if (drop < 0) drop = Times.Count;
            if (drop == 0) return;
            Times.RemoveRange(0, drop);
            foreach (var list in Values.Values) list.RemoveRange(0, drop);
            // A series with nothing left (a device that was unplugged) is forgotten.
            foreach (var id in Values.Where(v => v.Value.All(x => x is null)).Select(v => v.Key).ToList()) Values.Remove(id);
        }
    }

    private sealed class Saved
    {
        public Dictionary<string, Meta> Series { get; set; } = new();
        public Tier Day { get; set; } = new();
    }

    private readonly string? _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, Meta> _meta = new();
    private readonly Tier _hour = new();
    private Tier _day = new();
    private readonly Dictionary<string, (double Sum, int Count)> _minute = new();
    private long _minuteStart;
    private DateTimeOffset _lastSave = DateTimeOffset.Now;

    public SensorLog(string? path)
    {
        _path = path;
        Load();
    }

    public static string PathNextTo(string settingsPath) =>
        Path.Combine(Path.GetDirectoryName(settingsPath)!, "sensor-history.json");

    /// <summary>Adds the current readings. Call it every few seconds.</summary>
    public void Record(IReadOnlyList<SensorReading> sensors, IReadOnlyList<FanReading> fans, DateTimeOffset now)
    {
        var sample = new Dictionary<string, double>();
        lock (_gate)
        {
            void Take(string id, double? value, int decimals, Func<Meta> describe)
            {
                if (value is not { } v || !double.IsFinite(v)) return;
                if (!_meta.ContainsKey(id))
                {
                    if (_meta.Count >= MaxSeries) return;
                    _meta[id] = describe();
                }
                sample[id] = Math.Round(v, decimals);
            }

            foreach (var s in sensors.Where(s => s.Role != SensorRole.Virtual && s.Kind is SensorKind.Temperature or SensorKind.Load))
            {
                var kind = s.Kind == SensorKind.Temperature ? "temperature" : "load";
                Take(s.Id, s.Value, 1, () => new Meta { Name = s.Name, Group = s.Hardware, Kind = kind, Role = s.Role.ToString().ToLowerInvariant(), Primary = s.Primary });
            }
            foreach (var f in fans)
            {
                Take($"{f.Id}|rpm", f.Rpm, 0, () => new Meta { Name = f.Name, Group = "Fans", Kind = "rpm", Role = "fan" });
                Take($"{f.Id}|duty", f.Percent, 0, () => new Meta { Name = f.Name, Group = "Fans", Kind = "duty", Role = "fan" });
            }

            var time = now.ToUnixTimeSeconds();
            _hour.Add(time, sample, HourSeconds);

            // The day keeps one value a minute: the average of everything seen in that minute.
            var minute = time - time % DayStep;
            if (minute != _minuteStart)
            {
                if (_minute.Count > 0)
                    _day.Add(_minuteStart, _minute.ToDictionary(m => m.Key, m => Math.Round(m.Value.Sum / m.Value.Count, 1)), DaySeconds);
                _minute.Clear();
                _minuteStart = minute;
            }
            foreach (var (id, v) in sample)
            {
                var (sum, count) = _minute.GetValueOrDefault(id);
                _minute[id] = (sum + v, count + 1);
            }

            foreach (var id in _meta.Keys.Where(id => !_hour.Values.ContainsKey(id) && !_day.Values.ContainsKey(id) && !_minute.ContainsKey(id)).ToList())
                _meta.Remove(id);

            if (now - _lastSave > SaveEvery) SaveLocked(now);
        }
    }

    /// <summary>The last hour (<paramref name="range"/> "hour") or the last day ("day").</summary>
    public LogView View(string range)
    {
        lock (_gate)
        {
            var day = range == "day";
            var tier = day ? _day : _hour;
            var series = tier.Values
                .Where(v => _meta.ContainsKey(v.Key))
                .Select(v => { var m = _meta[v.Key]; return new LogSeries(v.Key, m.Name, m.Group, m.Kind, m.Role, m.Primary, [.. v.Value]); })
                .ToList();
            return new LogView(day ? "day" : "hour", day ? DayStep : HourStep, [.. tier.Times], series);
        }
    }

    /// <summary>Fills both ranges with believable readings, for screenshots in demo mode.</summary>
    public void SeedDemo(IReadOnlyList<SensorReading> sensors, IReadOnlyList<FanReading> fans, DateTimeOffset now)
    {
        // A day of use: mostly idle, a long session in the evening and a couple of short ones.
        static double Load(double hoursAgo)
        {
            double Bump(double centre, double width) => 1 / (1 + Math.Pow((hoursAgo - centre) / width, 6));
            var busy = Math.Max(Bump(2.2, 1.1), Math.Max(Bump(9.5, 0.5) * 0.7, Bump(0.35, 0.12) * 0.85));
            return Math.Clamp(0.06 + busy * 0.8 + 0.015 * Math.Sin(hoursAgo * 5.3) + 0.01 * Math.Sin(hoursAgo * 19), 0, 1);
        }

        Dictionary<string, double> At(double hoursAgo, double lag)
        {
            var load = Load(hoursAgo + lag);
            var sample = new Dictionary<string, double>();
            foreach (var s in sensors.Where(s => s.Role != SensorRole.Virtual))
            {
                var wobble = 0.3 * Math.Sin(hoursAgo * 23 + s.Id.Length);
                sample[s.Id] = Math.Round(s.Kind == SensorKind.Load
                    ? Load(hoursAgo) * 100
                    : s.Role switch
                    {
                        SensorRole.Cpu => 41 + load * 33,
                        SensorRole.Gpu => 37 + load * 34 + (s.Primary ? 0 : 6),
                        _ => 32 + load * 6 + (s.Primary ? 0 : 12),
                    } + wobble, 1);
            }
            foreach (var f in fans.Where(f => (f.Rpm ?? 0) > 0))
            {
                var duty = 28 + load * 52;
                sample[$"{f.Id}|duty"] = Math.Round(duty);
                sample[$"{f.Id}|rpm"] = Math.Round((f.Name.Contains("CPU") ? 1600 : 1200) * (0.25 + 0.75 * duty / 100));
            }
            return sample;
        }

        lock (_gate)
        {
            Record(sensors, fans, now); // registers every series
            _hour.Times.Clear(); _hour.Values.Clear(); _day.Times.Clear(); _day.Values.Clear(); _minute.Clear();
            var end = now.ToUnixTimeSeconds();
            for (var t = end - DaySeconds + DayStep; t <= end; t += DayStep) _day.Add(t - t % DayStep, At((end - t) / 3600.0, 0.02), DaySeconds);
            for (var t = end - HourSeconds + HourStep; t <= end; t += HourStep) _hour.Add(t, At((end - t) / 3600.0, 0.004), HourSeconds);
            _minuteStart = end - end % DayStep;
        }
    }

    public void Save()
    {
        lock (_gate) SaveLocked(DateTimeOffset.Now);
    }

    private void SaveLocked(DateTimeOffset now)
    {
        _lastSave = now;
        if (_path is null) return;
        try
        {
            var saved = new Saved { Day = _day, Series = _meta.Where(m => _day.Values.ContainsKey(m.Key)).ToDictionary(m => m.Key, m => m.Value) };
            SafeFile.Replace(_path, JsonSerializer.Serialize(saved, Json));
        }
        catch (Exception) { /* history is a convenience; never let saving it disturb fan control */ }
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(_path), Json);
            if (saved?.Day?.Times is null || saved.Series is null || saved.Day.Values is null) return;
            // Only a file with the right shape is used: every series as long as the list of times.
            var count = saved.Day.Times.Count;
            if (count > DaySeconds / DayStep + 1 || saved.Series.Count > MaxSeries) return;
            var values = saved.Day.Values
                .Where(v => v.Value is not null && v.Value.Count == count && saved.Series.TryGetValue(v.Key, out var m) && m is not null)
                .ToDictionary(v => v.Key, v => v.Value);
            _day = new Tier { Times = saved.Day.Times, Values = values };
            foreach (var id in values.Keys)
            {
                var m = saved.Series[id];
                _meta[id] = new Meta { Name = Short(m.Name), Group = Short(m.Group), Kind = Short(m.Kind), Role = Short(m.Role), Primary = m.Primary };
            }
            _day.Trim(DateTimeOffset.Now.ToUnixTimeSeconds() - DaySeconds);
        }
        catch (Exception) { _day = new Tier(); _meta.Clear(); } // a bad file just means an empty chart
    }

    private static string Short(string? s) => s is null ? "" : s.Length > 200 ? s[..200] : s;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}
