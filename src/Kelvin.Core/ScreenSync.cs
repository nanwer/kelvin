namespace Kelvin.Core;

/// <summary>
/// Screen sync: lights follow what is on screen. The desktop app (which can see the screen; the
/// service cannot) sends a tiny grid of colors a few times a second. This turns each grid into
/// one color per screen column plus one for the whole screen, and fades toward it so lights
/// glide rather than flicker. Nothing is stored, and the grid is far too small to read.
/// </summary>
public sealed class ScreenSync
{
    public const int Columns = 16;
    public const int MaxCells = 64 * 64;
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(3);
    private const double FadeSeconds = 0.15;

    private readonly object _gate = new();
    // Columns, then the whole screen in the last slot.
    private readonly double[] _target = new double[(Columns + 1) * 3];
    private readonly double[] _current = new double[(Columns + 1) * 3];
    private DateTimeOffset _lastFrame = DateTimeOffset.MinValue;

    /// <summary>True while the desktop app is sending frames.</summary>
    public bool Receiving(DateTimeOffset now)
    {
        lock (_gate) return now - _lastFrame < StaleAfter;
    }

    /// <summary>Takes a grid of <paramref name="width"/> x <paramref name="height"/> RGB pixels, row by row.</summary>
    public bool Submit(int width, int height, ReadOnlySpan<byte> rgb, DateTimeOffset now)
    {
        if (width < 1 || height < 1 || (long)width * height > MaxCells || rgb.Length != width * height * 3) return false;
        Span<double> sums = stackalloc double[(Columns + 1) * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 3;
                double r = rgb[i], g = rgb[i + 1], b = rgb[i + 2];
                var max = Math.Max(r, Math.Max(g, b));
                var min = Math.Min(r, Math.Min(g, b));
                // Colorful pixels count for more, or everything averages out to a muddy grey.
                var weight = 0.05 + (max == 0 ? 0 : (max - min) / max) * (max / 255);
                Add(sums, x * Columns / width, r, g, b, weight);
                Add(sums, Columns, r, g, b, weight);
            }
        }

        lock (_gate)
        {
            for (var s = 0; s <= Columns; s++)
            {
                // A grid narrower than 16 leaves some columns empty: they take the whole-screen color.
                var from = sums[s * 4 + 3] > 0 ? s : Columns;
                for (var c = 0; c < 3; c++) _target[s * 3 + c] = sums[from * 4 + c] / sums[from * 4 + 3];
            }
            if (now - _lastFrame >= StaleAfter) _target.CopyTo(_current, 0);   // first frame: no fade from black
            _lastFrame = now;
        }
        return true;
    }

    private static void Add(Span<double> sums, int slot, double r, double g, double b, double weight)
    {
        sums[slot * 4] += r * weight;
        sums[slot * 4 + 1] += g * weight;
        sums[slot * 4 + 2] += b * weight;
        sums[slot * 4 + 3] += weight;
    }

    /// <summary>Moves the shown colors toward the latest frame. Call once per rendered frame.</summary>
    public void Step(double seconds)
    {
        var k = 1 - Math.Exp(-Math.Max(seconds, 0) / FadeSeconds);
        lock (_gate)
            for (var i = 0; i < _current.Length; i++) _current[i] += (_target[i] - _current[i]) * k;
    }

    /// <summary>The whole screen's color.</summary>
    public RgbColor Whole() => At(Columns);

    /// <summary>The color at a position across the screen, 0 (left) to 1 (right).</summary>
    public RgbColor Across(double position) => At(Math.Clamp((int)(position * Columns), 0, Columns - 1));

    private RgbColor At(int slot)
    {
        lock (_gate)
            return new((byte)Math.Round(_current[slot * 3]), (byte)Math.Round(_current[slot * 3 + 1]), (byte)Math.Round(_current[slot * 3 + 2]));
    }
}
