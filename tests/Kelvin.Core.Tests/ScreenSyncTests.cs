using Kelvin.Core;

namespace Kelvin.Core.Tests;

public class ScreenSyncTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    private static byte[] Grid(int w, int h, Func<int, int, (byte, byte, byte)> pixel)
    {
        var data = new byte[w * h * 3];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                (data[(y * w + x) * 3], data[(y * w + x) * 3 + 1], data[(y * w + x) * 3 + 2]) = pixel(x, y);
        return data;
    }

    [Fact]
    public void Left_and_right_of_the_screen_keep_their_own_colors()
    {
        var sync = new ScreenSync();
        Assert.True(sync.Submit(16, 9, Grid(16, 9, (x, _) => x < 8 ? ((byte)255, (byte)0, (byte)0) : ((byte)0, (byte)0, (byte)255)), Now));
        Assert.Equal(new RgbColor(255, 0, 0), sync.Across(0.1));
        Assert.Equal(new RgbColor(0, 0, 255), sync.Across(0.9));
        var whole = sync.Whole();
        Assert.InRange(whole.R, 120, 135);
        Assert.InRange(whole.B, 120, 135);
    }

    [Fact]
    public void A_little_color_beats_a_lot_of_grey()
    {
        var sync = new ScreenSync();
        // A grey page with a red banner across the top row.
        sync.Submit(16, 9, Grid(16, 9, (_, y) => y == 0 ? ((byte)255, (byte)0, (byte)0) : ((byte)128, (byte)128, (byte)128)), Now);
        var c = sync.Whole();
        Assert.True(c.R > c.G + 60, $"expected clearly red, got {c.Hex}");
    }

    [Fact]
    public void Colors_fade_toward_a_new_frame_instead_of_jumping()
    {
        var sync = new ScreenSync();
        sync.Submit(1, 1, [255, 0, 0], Now);
        Assert.Equal(new RgbColor(255, 0, 0), sync.Whole());   // the first frame shows at once
        sync.Submit(1, 1, [0, 0, 255], Now.AddMilliseconds(66));
        sync.Step(0.033);
        var mid = sync.Whole();
        Assert.InRange(mid.R, 150, 254);
        for (var i = 0; i < 60; i++) sync.Step(0.033);
        Assert.Equal(new RgbColor(0, 0, 255), sync.Whole());
    }

    [Fact]
    public void Stops_counting_as_live_three_seconds_after_the_last_frame()
    {
        var sync = new ScreenSync();
        Assert.False(sync.Receiving(Now));
        sync.Submit(1, 1, [10, 20, 30], Now);
        Assert.True(sync.Receiving(Now.AddSeconds(2)));
        Assert.False(sync.Receiving(Now.AddSeconds(4)));
    }

    [Theory]
    [InlineData(0, 9, 0)]
    [InlineData(16, 9, 10)]       // wrong length
    [InlineData(100, 100, 30000)] // too big
    public void Rejects_bad_frames(int w, int h, int bytes) =>
        Assert.False(new ScreenSync().Submit(w, h, new byte[bytes], Now));
}
