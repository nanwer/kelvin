using System.Text.Json;

namespace Kelvin.Core;

/// <summary>How hard a part is working. Temperatures are only ever compared within the same band.</summary>
public enum LoadBand { Idle, Light, Medium, Heavy }

public enum HealthStatus { Learning, Ok, Warmer, Cooler, SettingsChanged }

/// <summary>Running totals for one load band on one day.</summary>
public sealed class BandStats
{
    public double Minutes { get; set; }
    /// <summary>Sum over samples of (part temperature − motherboard temperature), weighted by minutes.</summary>
    public double SumRise { get; set; }
    /// <summary>Sum of average fan duty, weighted by minutes. Used to skip comparisons after curve changes.</summary>
    public double SumDuty { get; set; }

    public double MeanRise => Minutes > 0 ? SumRise / Minutes : double.NaN;
    public double MeanDuty => Minutes > 0 ? SumDuty / Minutes : double.NaN;
}

public sealed class DayRecord
{
    public DateOnly Date { get; set; }
    /// <summary>"cpu" / "gpu" → band → stats.</summary>
    public Dictionary<string, Dictionary<LoadBand, BandStats>> Parts { get; set; } = new();
}

public sealed record BandComparison(LoadBand Band, double ThenRise, double NowRise, double Change, bool Compared, string? Skipped);

public sealed record TrendPoint(DateOnly Date, double Rise);

public sealed record PartHealth(
    string Key,
    string Name,
    HealthStatus Status,
    double? Change,
    string Headline,
    string Detail,
    int DaysCollected,
    int DaysNeeded,
    LoadBand? TrendBand,
    IReadOnlyList<TrendPoint> Trend,
    IReadOnlyList<BandComparison> Bands,
    bool RelativeToBoard);

public sealed record HealthReport(IReadOnlyList<PartHealth> Parts, DateTimeOffset GeneratedAt);

/// <summary>
/// Learns how hot the CPU and GPU run at each load level and spots slow changes over weeks,
/// such as dust build-up or drying thermal paste. Temperatures are stored relative to the
/// motherboard sensor, so a warmer room mostly cancels out. Only daily summaries are kept.
/// </summary>
public sealed class ThermalHistory
{
    public const int RecentDays = 7;        // "now": the last week
    public const int BaselineMinAge = 14;   // "then": at least two weeks old…
    public const int BaselineMaxAge = 45;   // …and at most about six weeks old
    public const double MinMinutes = 60;    // per band, per period, before comparing
    public const double FlagChange = 4;     // °C change worth telling the user about
    public const double MaxDutyShift = 12;  // % points: bigger means the user changed fan settings
    private const int KeepDays = 120;

    private static readonly JsonSerializerOptions Json = SettingsStore.Json;

    private readonly string? _path;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _gate = new();
    private List<DayRecord> _days = [];
    private DateTimeOffset _lastSave;

    public ThermalHistory(string? path, Func<DateTimeOffset>? now = null)
    {
        _path = path;
        _now = now ?? (() => DateTimeOffset.Now);
        _lastSave = _now();
        Load();
    }

    public static string PathNextTo(string settingsPath) =>
        Path.Combine(Path.GetDirectoryName(settingsPath)!, "thermal-history.json");

    public IReadOnlyList<DayRecord> Days { get { lock (_gate) return _days.ToList(); } }

    public static LoadBand BandFor(double loadPercent) => loadPercent switch
    {
        < 10 => LoadBand.Idle,
        < 35 => LoadBand.Light,
        < 70 => LoadBand.Medium,
        _ => LoadBand.Heavy,
    };

    public static string BandLabel(LoadBand b) => b switch
    {
        LoadBand.Idle => "idle",
        LoadBand.Light => "light load",
        LoadBand.Medium => "medium load",
        _ => "heavy load",
    };

    /// <summary>
    /// Adds one observation covering <paramref name="minutes"/> of time. Call it regularly (Kelvin
    /// does every 5 s). Skip it while fans are being measured or overridden, since those readings
    /// don't reflect normal cooling.
    /// </summary>
    public void Record(IReadOnlyList<SensorReading> sensors, IReadOnlyList<FanReading> fans, double minutes)
    {
        var board = sensors.FirstOrDefault(s => s.Primary && s.Role == SensorRole.Board && s.Kind == SensorKind.Temperature);
        var caseFans = fans.Where(f => f.Controllable && !f.IsGpuFan && !f.IsPump && f.Percent is not null && (f.Rpm ?? 0) > 0).ToList();
        var duty = caseFans.Count > 0 ? caseFans.Average(f => f.Percent!.Value) : 0;
        var today = DateOnly.FromDateTime(_now().LocalDateTime);

        lock (_gate)
        {
            var day = _days.LastOrDefault();
            if (day is null || day.Date != today) _days.Add(day = new DayRecord { Date = today });

            foreach (var (key, role) in new[] { ("cpu", SensorRole.Cpu), ("gpu", SensorRole.Gpu) })
            {
                var temp = sensors.FirstOrDefault(s => s.Primary && s.Role == role && s.Kind == SensorKind.Temperature);
                var load = sensors.FirstOrDefault(s => s.Role == role && s.Kind == SensorKind.Load);
                if (temp is null || load is null || !double.IsFinite(temp.Value) || !double.IsFinite(load.Value)) continue;

                var rise = temp.Value - (board?.Value ?? 0);
                if (!day.Parts.TryGetValue(key, out var bands)) day.Parts[key] = bands = new();
                var band = BandFor(load.Value);
                if (!bands.TryGetValue(band, out var stats)) bands[band] = stats = new BandStats();
                stats.Minutes += minutes;
                stats.SumRise += rise * minutes;
                stats.SumDuty += duty * minutes;
            }

            if (_now() - _lastSave > TimeSpan.FromMinutes(5)) SaveLocked();
        }
    }

    public HealthReport Analyze(IReadOnlyList<SensorReading> sensors)
    {
        List<DayRecord> days;
        lock (_gate) days = _days.ToList();
        var today = DateOnly.FromDateTime(_now().LocalDateTime);
        var hasBoard = sensors.Any(s => s.Primary && s.Role == SensorRole.Board && s.Kind == SensorKind.Temperature);

        var parts = new List<PartHealth>();
        foreach (var (key, role, name) in new[] { ("cpu", SensorRole.Cpu, "CPU"), ("gpu", SensorRole.Gpu, "Graphics card") })
        {
            var hardware = sensors.FirstOrDefault(s => s.Primary && s.Role == role)?.Hardware ?? name;
            parts.Add(AnalyzePart(key, name, hardware, days, today, hasBoard));
        }
        return new HealthReport(parts, _now());
    }

    private static PartHealth AnalyzePart(string key, string name, string hardware, List<DayRecord> days, DateOnly today, bool relative)
    {
        var withData = days.Where(d => d.Parts.TryGetValue(key, out var b) && b.Values.Sum(s => s.Minutes) >= 30).ToList();
        var daysCollected = withData.Count;
        var oldest = withData.FirstOrDefault()?.Date;
        var age = oldest is { } o ? today.DayNumber - o.DayNumber : 0;

        // Trend: daily average in the band with the most data overall, for the last ~6 weeks.
        var totals = new Dictionary<LoadBand, double>();
        foreach (var d in withData)
            foreach (var (band, s) in d.Parts[key])
                totals[band] = totals.GetValueOrDefault(band) + s.Minutes;
        LoadBand? trendBand = totals.Count > 0 ? totals.MaxBy(kv => kv.Value).Key : null;
        List<TrendPoint> TrendFor(LoadBand? band) => band is { } tb
            ? withData.Where(d => today.DayNumber - d.Date.DayNumber <= BaselineMaxAge && d.Parts[key].TryGetValue(tb, out var s) && s.Minutes >= 10)
                      .Select(d => new TrendPoint(d.Date, Math.Round(d.Parts[key][tb].MeanRise, 1))).ToList()
            : [];
        var trend = TrendFor(trendBand);

        if (age < BaselineMinAge)
        {
            var left = BaselineMinAge - age;
            return new PartHealth(key, name, HealthStatus.Learning, null,
                $"Learning how your {Lower(name)} normally runs",
                $"Kelvin compares this week with a few weeks ago, so it needs about {left} more day{(left == 1 ? "" : "s")} of normal use before the first check.",
                Math.Min(age, BaselineMinAge), BaselineMinAge, trendBand, trend, [], relative);
        }

        var baseline = Merge(days, key, d => today.DayNumber - d.Date.DayNumber is >= BaselineMinAge and <= BaselineMaxAge);
        var recent = Merge(days, key, d => today.DayNumber - d.Date.DayNumber < RecentDays);

        var comparisons = new List<BandComparison>();
        foreach (var band in Enum.GetValues<LoadBand>())
        {
            if (!baseline.TryGetValue(band, out var then) || !recent.TryGetValue(band, out var now)) continue;
            if (then.Minutes < MinMinutes || now.Minutes < MinMinutes)
            {
                comparisons.Add(new(band, Round(then.MeanRise), Round(now.MeanRise), 0, false, "not enough time at this load yet"));
                continue;
            }
            if (Math.Abs(now.MeanDuty - then.MeanDuty) > MaxDutyShift)
            {
                comparisons.Add(new(band, Round(then.MeanRise), Round(now.MeanRise), 0, false, "fan speeds changed, so this isn't a fair comparison"));
                continue;
            }
            comparisons.Add(new(band, Round(then.MeanRise), Round(now.MeanRise), Round(now.MeanRise - then.MeanRise), true, null));
        }

        var compared = comparisons.Where(c => c.Compared).ToList();
        var weeks = Math.Max(2, (int)Math.Round((today.DayNumber - (oldest?.DayNumber ?? today.DayNumber)) / 7.0 * 0.6));
        var against = relative ? " (measured against your motherboard temperature, so the room's temperature mostly cancels out)" : "";

        if (compared.Count == 0)
        {
            var changed = comparisons.Any(c => c.Skipped?.StartsWith("fan speeds") == true);
            return new PartHealth(key, name, changed ? HealthStatus.SettingsChanged : HealthStatus.Learning, null,
                changed ? "Re-learning after your fan changes" : $"Not enough comparable data for your {Lower(name)} yet",
                changed
                    ? "Your fan speeds changed since a few weeks ago, so old and new temperatures can't be compared fairly. Kelvin builds a new baseline over the next two weeks."
                    : "Kelvin needs about an hour at the same load in both periods. Use your PC normally and it'll fill in.",
                daysCollected, BaselineMinAge, trendBand, trend, comparisons, relative);
        }

        // The band with the biggest, best-supported change tells the story, and the chart shows it.
        var lead = compared.OrderByDescending(c => Math.Abs(c.Change)).First();
        trendBand = lead.Band;
        trend = TrendFor(trendBand);
        var at = BandLabel(lead.Band);
        var amount = $"{Math.Abs(lead.Change):0}°C";
        var fix = key == "cpu"
            ? "That usually means dust in the CPU cooler or case filters, or thermal paste drying out. Cleaning the filters and heatsink is the first thing to try; repasting is the next."
            : "That usually means dust in the graphics card's heatsink and fans, or clogged case filters. Blowing the dust out of the card and filters is the first thing to try.";

        if (lead.Change >= FlagChange)
            return new PartHealth(key, name, HealthStatus.Warmer, lead.Change,
                $"Your {Lower(name)} runs {amount} hotter than {weeks} weeks ago",
                $"At {at}, {hardware} now runs {amount} hotter than it did {weeks} weeks ago{against}. {fix}",
                daysCollected, BaselineMinAge, trendBand, trend, comparisons, relative);

        if (lead.Change <= -FlagChange)
            return new PartHealth(key, name, HealthStatus.Cooler, lead.Change,
                $"Your {Lower(name)} runs {amount} cooler than {weeks} weeks ago",
                $"At {at}, {hardware} now runs {amount} cooler than it did {weeks} weeks ago{against}. Whatever you changed is working.",
                daysCollected, BaselineMinAge, trendBand, trend, comparisons, relative);

        return new PartHealth(key, name, HealthStatus.Ok, lead.Change,
            $"Your {Lower(name)} cools as well as it did {weeks} weeks ago",
            $"At the same load, temperatures are within {FlagChange:0}°C of a few weeks ago{against}. Nothing to do.",
            daysCollected, BaselineMinAge, trendBand, trend, comparisons, relative);
    }

    private static Dictionary<LoadBand, BandStats> Merge(List<DayRecord> days, string key, Func<DayRecord, bool> include)
    {
        var merged = new Dictionary<LoadBand, BandStats>();
        foreach (var d in days.Where(include))
        {
            if (!d.Parts.TryGetValue(key, out var bands)) continue;
            foreach (var (band, s) in bands)
            {
                if (!merged.TryGetValue(band, out var m)) merged[band] = m = new BandStats();
                m.Minutes += s.Minutes;
                m.SumRise += s.SumRise;
                m.SumDuty += s.SumDuty;
            }
        }
        return merged;
    }

    private static double Round(double v) => double.IsFinite(v) ? Math.Round(v, 1) : 0;
    private static string Lower(string name) => name == "CPU" ? "CPU" : char.ToLowerInvariant(name[0]) + name[1..];

    /// <summary>Fills history with five weeks of plausible data in which the CPU slowly gets dustier.</summary>
    public void SeedDemo()
    {
        var rng = new Random(42);
        var today = DateOnly.FromDateTime(_now().LocalDateTime);
        var list = new List<DayRecord>();
        for (var age = 35; age >= 1; age--)
        {
            var day = new DayRecord { Date = today.AddDays(-age) };
            var dust = age > 21 ? 0 : (21 - age) / 21.0 * 7; // +7°C over the last three weeks
            foreach (var (key, baseRise, drift) in new[] { ("cpu", new[] { 9.0, 15, 26, 39 }, dust), ("gpu", new[] { 6.0, 12, 22, 31 }, 0.0) })
            {
                var bands = new Dictionary<LoadBand, BandStats>();
                double[] minutes = [300, 120, 60 + rng.Next(60), 45 + rng.Next(90)];
                for (var b = 0; b < 4; b++)
                {
                    var rise = baseRise[b] + drift * (0.6 + 0.4 * b / 3.0) + (rng.NextDouble() - 0.5) * 1.6;
                    bands[(LoadBand)b] = new BandStats { Minutes = minutes[b], SumRise = rise * minutes[b], SumDuty = (35 + 15 * b) * minutes[b] };
                }
                day.Parts[key] = bands;
            }
            list.Add(day);
        }
        lock (_gate) _days = list;
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        try { _days = JsonSerializer.Deserialize<List<DayRecord>>(File.ReadAllText(_path), Json) ?? []; }
        catch { _days = []; } // history is nice to have; never fail because of it
    }

    public void Save()
    {
        lock (_gate) SaveLocked();
    }

    private void SaveLocked()
    {
        _lastSave = _now();
        var cutoff = DateOnly.FromDateTime(_now().LocalDateTime).AddDays(-KeepDays);
        _days.RemoveAll(d => d.Date < cutoff);
        if (_path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_days, Json));
            File.Move(tmp, _path, overwrite: true);
        }
        catch { /* a failed save just loses a few minutes of history */ }
    }
}
