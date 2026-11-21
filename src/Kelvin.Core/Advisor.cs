namespace Kelvin.Core;

public enum Severity { Info, Suggestion, Warning }

public enum RecommendationAction { None, Calibrate, ApplyFan, CloseApp }

public sealed record Recommendation(
    string Id,
    Severity Severity,
    string Title,
    string Detail,
    RecommendationAction Action,
    string? FanId = null,
    FanSettings? Settings = null);

/// <summary>
/// Looks at the fans that are actually installed (what they're plugged into, their measured RPM
/// range and stall point) and the sensors on this machine, and suggests settings with reasons.
/// Rules are deliberately simple and explained in plain language so users can trust them.
/// </summary>
public static class Advisor
{
    public static IReadOnlyList<Recommendation> Build(
        IReadOnlyList<FanReading> fans,
        IReadOnlyList<SensorReading> sensors,
        KelvinSettings settings,
        IReadOnlyList<Conflict> conflicts,
        HealthReport? health = null)
    {
        var recs = new List<Recommendation>();

        foreach (var part in health?.Parts ?? [])
        {
            if (part.Status == HealthStatus.Warmer)
                recs.Add(new($"health:{part.Key}", Severity.Warning, part.Headline, part.Detail, RecommendationAction.None));
            else if (part.Status == HealthStatus.Cooler)
                recs.Add(new($"health:{part.Key}", Severity.Info, part.Headline, part.Detail, RecommendationAction.None));
        }

        foreach (var c in conflicts)
            recs.Add(new($"conflict:{c.Key}", Severity.Warning, $"{c.Name} is running", $"{c.Impact} {c.Fix}",
                c.CanClose ? RecommendationAction.CloseApp : RecommendationAction.None));

        // GPU fans are managed well by the card's own firmware, so Kelvin doesn't suggest changes.
        var controllable = fans.Where(f => f.Controllable && !f.IsGpuFan).ToList();
        var profiles = settings.FanProfiles.ToDictionary(p => p.FanId);

        if (controllable.Count > 0 && controllable.Any(f => !profiles.ContainsKey(f.Id)))
        {
            recs.Add(new("calibrate", Severity.Suggestion, "Measure your fans",
                "Kelvin runs each fan from full speed down to stopped, about 30 seconds in total, to learn its real range " +
                "and the point where it stalls. Then it can suggest a curve for each one. Best done while the PC is idle.",
                RecommendationAction.Calibrate));
        }

        var cpu = sensors.FirstOrDefault(s => s.Primary && s.Role == SensorRole.Cpu && s.Kind == SensorKind.Temperature);
        var gpu = sensors.FirstOrDefault(s => s.Primary && s.Role == SensorRole.Gpu && s.Kind == SensorKind.Temperature);
        var isRyzen = cpu?.Hardware.Contains("Ryzen", StringComparison.OrdinalIgnoreCase) == true;

        foreach (var fan in controllable)
        {
            if (!profiles.TryGetValue(fan.Id, out var profile)) continue;

            if (!profile.Connected)
            {
                recs.Add(new($"empty:{fan.Id}", Severity.Info, $"Nothing is plugged into {fan.Name}",
                    "It didn't spin at any speed during measurement, so Kelvin leaves this header on BIOS control.",
                    RecommendationAction.None, fan.Id));
                continue;
            }

            var rec = ForFan(fan, profile, cpu, gpu, isRyzen);
            if (rec is null) continue;

            var current = settings.Fans.FirstOrDefault(f => f.FanId == fan.Id);
            if (current is not null && Matches(current, rec.Settings!)) continue;
            recs.Add(rec);
        }

        return recs.Where(r => !settings.DismissedRecommendations.Contains(r.Id)).ToList();
    }

    private static Recommendation? ForFan(FanReading fan, FanProfile profile, SensorReading? cpu, SensorReading? gpu, bool isRyzen)
    {
        var why = new List<string>();
        var s = new FanSettings { FanId = fan.Id, Mode = FanMode.Curve };

        if (fan.IsPump)
        {
            s.Mode = FanMode.Fixed;
            s.FixedPercent = 70;
            s.Preset = "fixed";
            s.MinPercent = 50;
            return new($"fan:{fan.Id}", Severity.Suggestion, $"Run {fan.Name} at a steady 70%",
                "Pumps cool best at a constant speed. Ramping a pump with temperature adds noise for very little extra cooling.",
                RecommendationAction.ApplyFan, fan.Id, s);
        }

        var isCpuFan = fan.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase);
        if (isCpuFan && cpu is not null)
        {
            s.SourceSensorId = cpu.Id;
            why.Add("It cools the CPU, so it follows the CPU temperature.");
        }
        else if (gpu is not null)
        {
            s.SourceSensorId = SensorIds.Hottest;
            why.Add("Your graphics card dumps its heat into the case, so case fans follow whichever of the CPU or GPU is hotter.");
        }
        else if (cpu is not null)
        {
            s.SourceSensorId = cpu.Id;
            why.Add("It follows the CPU temperature, the main heat source Kelvin can see.");
        }
        else return null;

        s.Preset = "balanced";
        if (profile.MaxRpm >= 2200)
        {
            s.Preset = "silent";
            why.Add($"This fan reaches {profile.MaxRpm:0} RPM, which is loud at high duty. A quieter curve still cools well.");
        }
        else if (profile.MaxRpm > 0 && profile.MaxRpm <= 1300)
        {
            s.Preset = "performance";
            why.Add($"It tops out at {profile.MaxRpm:0} RPM, so it stays quiet even when working hard. A stronger curve costs you little noise.");
        }
        s.Points = [.. Curves.Presets[s.Preset]];

        if (profile.StopPercent is { } stop)
        {
            // Fans need more duty to start than to keep turning; leave a margin above the stall point.
            s.MinPercent = Math.Min(stop + 10, 40);
            why.Add($"It stalls at {stop:0}%, so Kelvin never drives it below {s.MinPercent:0}%.");
        }
        else
        {
            s.MinPercent = 0;
        }

        if (isRyzen && s.SourceSensorId == cpu?.Id)
        {
            s.SmoothSeconds = 45;
            why.Add("Ryzen CPUs jump 20°C or more for a few seconds at a time. Following the average over 45 seconds stops the fan revving at every spike.");
        }

        var label = s.Preset switch { "silent" => "a quiet curve", "performance" => "a stronger curve", _ => "a balanced curve" };
        return new($"fan:{fan.Id}", Severity.Suggestion, $"Give {fan.Name} {label}", string.Join(" ", why),
            RecommendationAction.ApplyFan, fan.Id, s);
    }

    private static bool Matches(FanSettings a, FanSettings b) =>
        a.Mode == b.Mode && a.Preset == b.Preset && a.SourceSensorId == b.SourceSensorId &&
        Math.Abs(a.MinPercent - b.MinPercent) < 0.5 && Math.Abs(a.FixedPercent - b.FixedPercent) < 0.5;
}
