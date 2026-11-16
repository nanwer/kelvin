using Kelvin.Core;

namespace Kelvin.Core.Tests;

public class LightingTests
{
    private sealed class FakeRgb(RgbDevice device) : IRgbProvider
    {
        public readonly Dictionary<string, RgbColor[]> Sent = new();
        public string Name => "Fake";
        public bool IsSimulated => true;
        public bool IsConnected => true;
        public string? Status => null;
        public IReadOnlyList<RgbDevice> Devices => [device];
        public void Open() { }
        public void Refresh() { }
        public void SetColors(string deviceId, IReadOnlyList<RgbColor> colors) => Sent[deviceId] = colors.ToArray();
        public void Dispose() { }
    }

    // A mouse: 1 logo LED, 1 scroll-wheel LED, 4 underglow LEDs.
    private static readonly RgbDevice Mouse = new("mouse", "Mouse", "Razer", "Mouse", 6,
        [new("Logo", 1), new("Scroll wheel", 1), new("Underglow", 4)]);

    private static RgbColor[] Render(LightingSettings lighting)
    {
        var rgb = new FakeRgb(Mouse);
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), $"kelvin-light-{Guid.NewGuid():N}.json"));
        store.Mutate(s => s.Lighting = lighting);
        var engine = new LightingEngine(rgb, store, () => []);
        typeof(LightingEngine).GetMethod("Render", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(engine, null);
        return rgb.Sent["mouse"];
    }

    [Fact]
    public void A_zone_color_only_touches_that_zone()
    {
        var frame = Render(new LightingSettings
        {
            Effect = LightingEffect.Static, Color = "#ffffff", Brightness = 1,
            Devices = { ["mouse"] = new DeviceLighting { Zones = { ["Scroll wheel"] = new ColorOverride { Mode = ColorMode.Solid, Color = "#ff0000" } } } },
        });
        Assert.Equal(new RgbColor(255, 255, 255), frame[0]); // logo follows the effect
        Assert.Equal(new RgbColor(255, 0, 0), frame[1]);     // scroll wheel is red
        Assert.All(frame[2..], c => Assert.Equal(new RgbColor(255, 255, 255), c));
    }

    [Fact]
    public void A_zone_beats_its_device_and_gradients_span_the_zone()
    {
        var frame = Render(new LightingSettings
        {
            Effect = LightingEffect.Off, Brightness = 1,
            Devices =
            {
                ["mouse"] = new DeviceLighting
                {
                    Mode = ColorMode.Solid, Color = "#00ff00",
                    Zones = { ["Underglow"] = new ColorOverride { Mode = ColorMode.Gradient, Color = "#000000", Color2 = "#ffffff" } },
                },
            },
        });
        Assert.Equal(new RgbColor(0, 255, 0), frame[0]);         // device color, even with the effect off
        Assert.Equal(new RgbColor(0, 0, 0), frame[2]);           // gradient starts at color 1…
        Assert.Equal(new RgbColor(255, 255, 255), frame[5]);     // …and ends at color 2
        Assert.True(frame[3].R is > 0 and < 255);
    }

    [Fact]
    public void Off_turns_just_that_device_dark()
    {
        var frame = Render(new LightingSettings
        {
            Effect = LightingEffect.Static, Color = "#ffffff", Brightness = 1,
            Devices = { ["mouse"] = new DeviceLighting { Mode = ColorMode.Off } },
        });
        Assert.All(frame, c => Assert.Equal(RgbColor.Black, c));
    }
}
