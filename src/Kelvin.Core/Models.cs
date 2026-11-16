namespace Kelvin.Core;

public enum SensorKind { Temperature, Load, Power }

/// <summary>What a sensor belongs to, so the engine can reason about it without parsing names.</summary>
public enum SensorRole { Cpu, Gpu, Board, Other, Virtual }

public sealed record SensorReading(
    string Id,
    string Name,
    string Hardware,
    SensorRole Role,
    SensorKind Kind,
    double Value,
    string Unit,
    bool Primary = false);

public sealed record FanReading(
    string Id,
    string Name,
    double? Rpm,
    double? Percent,
    bool Controllable,
    bool IsPump)
{
    /// <summary>
    /// A fan on the graphics card. The card's own firmware manages it well (including stopping it
    /// at idle), so Kelvin leaves it alone unless the user sets it up by hand.
    /// </summary>
    public bool IsGpuFan => Name.Contains("GPU", StringComparison.OrdinalIgnoreCase);
}

public static class SensorList
{
    /// <summary>
    /// Exactly one main temperature per component. Boards often report several sensors with the same
    /// name (e.g. two called "Motherboard"); only the first stays main. A component with none marked
    /// gets its first temperature as main.
    /// </summary>
    public static List<SensorReading> NormalizePrimary(IEnumerable<SensorReading> sensors)
    {
        var list = sensors.ToList();
        var seen = new HashSet<SensorRole>();
        for (var i = 0; i < list.Count; i++)
        {
            if (!list[i].Primary) continue;
            if (!seen.Add(list[i].Role)) list[i] = list[i] with { Primary = false };
        }
        foreach (var role in new[] { SensorRole.Cpu, SensorRole.Gpu, SensorRole.Board })
        {
            if (seen.Contains(role)) continue;
            var first = list.FindIndex(s => s.Role == role && s.Kind == SensorKind.Temperature);
            if (first >= 0) list[first] = list[first] with { Primary = true };
        }
        return list;
    }
}

public sealed record RgbColor(byte R, byte G, byte B)
{
    public static readonly RgbColor Black = new(0, 0, 0);

    public string Hex => $"#{R:x2}{G:x2}{B:x2}";

    public static RgbColor FromHex(string hex)
    {
        var h = hex.Trim().TrimStart('#');
        if (h.Length != 6) throw new FormatException("Colors must be #rrggbb.");
        return new(Convert.ToByte(h[..2], 16), Convert.ToByte(h[2..4], 16), Convert.ToByte(h[4..], 16));
    }

    public RgbColor Scale(double f)
    {
        f = Math.Clamp(f, 0, 1);
        return new((byte)Math.Round(R * f), (byte)Math.Round(G * f), (byte)Math.Round(B * f));
    }

    public static RgbColor Lerp(RgbColor a, RgbColor b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new((byte)Math.Round(a.R + (b.R - a.R) * t),
                   (byte)Math.Round(a.G + (b.G - a.G) * t),
                   (byte)Math.Round(a.B + (b.B - a.B) * t));
    }

    public static RgbColor FromHsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return new((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}

public sealed record HardwareInfo(string Kind, string Name, string? Parent = null);

public sealed record RgbZone(string Name, int LedCount);

public sealed record RgbDevice(
    string Id,
    string Name,
    string Vendor,
    string Kind,
    int LedCount,
    IReadOnlyList<RgbZone> Zones);
