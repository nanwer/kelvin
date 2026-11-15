namespace Kelvin.Core;

public static class Curves
{
    public static readonly IReadOnlyDictionary<string, CurvePoint[]> Presets = new Dictionary<string, CurvePoint[]>
    {
        ["silent"] = [new(35, 20), new(55, 30), new(70, 50), new(80, 75), new(90, 100)],
        ["balanced"] = [new(35, 30), new(55, 40), new(70, 60), new(80, 85), new(88, 100)],
        ["performance"] = [new(35, 45), new(50, 55), new(65, 75), new(75, 100)],
    };

    /// <summary>Linear interpolation over sorted points, flat beyond the ends.</summary>
    public static double Evaluate(IReadOnlyList<CurvePoint> points, double temp)
    {
        if (points.Count == 0) return 100;
        var sorted = points.OrderBy(p => p.Temp).ToArray();
        if (temp <= sorted[0].Temp) return sorted[0].Percent;
        if (temp >= sorted[^1].Temp) return sorted[^1].Percent;
        for (var i = 1; i < sorted.Length; i++)
        {
            var (a, b) = (sorted[i - 1], sorted[i]);
            if (temp <= b.Temp)
            {
                var t = (temp - a.Temp) / Math.Max(b.Temp - a.Temp, 0.001);
                return a.Percent + (b.Percent - a.Percent) * t;
            }
        }
        return sorted[^1].Percent;
    }

    /// <summary>Clamp and sort user-supplied points so a bad request can't produce a dangerous curve.</summary>
    public static List<CurvePoint> Sanitize(IEnumerable<CurvePoint> points)
    {
        var list = points
            .Where(p => double.IsFinite(p.Temp) && double.IsFinite(p.Percent))
            .Select(p => new CurvePoint(Math.Clamp(Math.Round(p.Temp, 1), 20, 110), Math.Clamp(Math.Round(p.Percent), 0, 100)))
            .OrderBy(p => p.Temp)
            .ToList();
        if (list.Count < 2) throw new ArgumentException("A curve needs at least two points.");
        // Duty must never fall as temperature rises.
        for (var i = 1; i < list.Count; i++)
            if (list[i].Percent < list[i - 1].Percent) list[i] = list[i] with { Percent = list[i - 1].Percent };
        // The hot end always reaches full speed.
        if (list[^1].Percent < 100 && list[^1].Temp < 95) list.Add(new CurvePoint(95, 100));
        return list;
    }
}
