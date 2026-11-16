using Kelvin.Core;

namespace Kelvin.Core.Tests;

public class LightingDetectionTests
{
    private static readonly string[] All = ["Corsair DDR4", "Corsair DDR4", "GPU", "Mouse", "Headset", "Keyboard", "Board"];
    private static readonly string[] Partial = ["Corsair DDR4", "Corsair DDR4", "GPU", "Mouse"];
    private DateTimeOffset _now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    private DetectionAction After(LightingDetectionWatch w, int seconds, string[] known, string[] current)
    {
        // The first thing the watch decides to do within that time, if anything.
        for (var i = 0; i <= seconds; i += 5)
        {
            var action = w.Check(known, current, true, _now);
            _now = _now.AddSeconds(5);
            if (action != DetectionAction.Wait) return action;
        }
        return DetectionAction.Wait;
    }

    [Fact]
    public void Waits_for_detection_to_settle_before_judging()
    {
        var w = new LightingDetectionWatch();
        Assert.Equal(DetectionAction.Wait, After(w, 55, All, Partial));
        Assert.Equal(DetectionAction.Restart, After(w, 10, All, Partial));
    }

    [Fact]
    public void Restarts_once_then_stops_expecting_devices_that_never_return()
    {
        var w = new LightingDetectionWatch();
        Assert.Equal(DetectionAction.Restart, After(w, 65, All, Partial));
        Assert.Equal(DetectionAction.Forget, After(w, 65, All, Partial));
    }

    [Fact]
    public void A_restart_that_finds_everything_is_ok()
    {
        var w = new LightingDetectionWatch();
        Assert.Equal(DetectionAction.Restart, After(w, 65, All, Partial));
        Assert.Equal(DetectionAction.Ok, After(w, 65, All, All));
        Assert.Equal(0, w.Restarts);
    }

    [Fact]
    public void Nothing_happens_while_disconnected()
    {
        var w = new LightingDetectionWatch();
        for (var i = 0; i < 100; i++) Assert.Equal(DetectionAction.Wait, w.Check(All, [], false, _now.AddSeconds(i * 5)));
    }

    [Fact]
    public void Counts_identical_devices_separately()
    {
        Assert.Equal(["Corsair DDR4"], LightingDetectionWatch.Missing(["Corsair DDR4", "Corsair DDR4"], ["Corsair DDR4"]));
        Assert.Equal(["A", "B", "A"], LightingDetectionWatch.Merge(["A", "B"], ["A", "A"]));
    }
}
