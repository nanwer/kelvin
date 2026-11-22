using Kelvin.Core;
using Kelvin.Hardware;

namespace Kelvin.Core.Tests;

/// <summary>The HP OMEN four-zone keyboard, against a stand-in for the laptop's BIOS.</summary>
public class LaptopKeyboardTests
{
    /// <summary>Answers the way the BIOS does, and remembers what it was told.</summary>
    private sealed class FakeBios(byte keyboardType = 1, byte hasBacklight = 1, byte zonesMinusOne = 3, byte backlight = 0x64)
    {
        public byte[] Table = MakeTable(zonesMinusOne);
        public byte Backlight = backlight;
        public int ColorWrites, BacklightWrites;
        public bool Refuse;

        private static byte[] MakeTable(byte zones)
        {
            var t = new byte[128];
            t[0] = zones;
            t[5] = 0xAB;       // something outside the colors, which must survive
            t[100] = 0xCD;
            return t;
        }

        public byte[]? Call(uint command, uint type, byte[] input, int outputSize) => (command, type) switch
        {
            (0x20008, 0x2B) => [keyboardType, 0, 0, 0],
            (0x20009, 0x01) => [hasBacklight, 0, 0, 0],
            (0x20009, 0x02) => (byte[])Table.Clone(),
            (0x20009, 0x03) => Write(input),
            (0x20009, 0x04) => [Backlight, 0, 0, 0],
            (0x20009, 0x05) => SetBacklight(input[0]),
            _ => null,
        };

        private byte[]? Write(byte[] input)
        {
            if (Refuse || input.Length != 128) return null;
            Table = (byte[])input.Clone();
            ColorWrites++;
            return [];
        }

        private byte[] SetBacklight(byte value) { Backlight = value; BacklightWrites++; return []; }
    }

    private static readonly RgbColor Red = new(255, 0, 0), Green = new(0, 255, 0), Blue = new(0, 0, 255), White = new(255, 255, 255);

    private static void WaitFor(Func<bool> done)
    {
        for (var i = 0; i < 100 && !done(); i++) Thread.Sleep(20);
        Assert.True(done());
    }

    [Fact]
    public void Colors_go_into_the_table_in_the_order_the_bios_numbers_the_zones()
    {
        var table = new byte[128];
        table[0] = 3; table[7] = 9;
        // Kelvin's order is left, middle, right, WASD. The BIOS's is right, middle, left, WASD, from byte 25.
        var next = HpOmenKeyboard.Patch(table, [Red, Green, Blue, White]);
        Assert.Equal([0, 0, 255, /* right */ 0, 255, 0, /* middle */ 255, 0, 0, /* left */ 255, 255, 255 /* WASD */], next[25..37]);
        Assert.Equal((3, 9), (next[0], next[7]));
        Assert.All(next[37..], b => Assert.Equal(0, b));
        Assert.Equal(0, table[25]);   // the original is left alone
    }

    [Fact]
    public void A_four_zone_keyboard_is_found_and_lit()
    {
        var bios = new FakeBios();
        using var keyboard = new HpOmenKeyboard(bios.Call);
        keyboard.Open();
        Assert.True(keyboard.IsConnected);
        var device = Assert.Single(keyboard.Devices);
        Assert.Equal((4, "Keyboard", "HP"), (device.LedCount, device.Kind, device.Vendor));
        Assert.Equal(["Left", "Middle", "Right", "WASD keys"], device.Zones.Select(z => z.Name));

        keyboard.SetColors(device.Id, [Red, Green, Blue, White]);
        WaitFor(() => bios.ColorWrites == 1);
        Assert.Equal([255, 0, 0], bios.Table[31..34]);        // left is the BIOS's zone 2
        Assert.Equal((0xAB, 0xCD), (bios.Table[5], bios.Table[100]));   // bytes nobody understands are kept
        WaitFor(() => bios.Backlight == 0xE4);                // it was off, so it is switched on to show them
    }

    [Fact]
    public void The_backlight_key_stays_in_charge_after_the_first_time()
    {
        var bios = new FakeBios();
        using var keyboard = new HpOmenKeyboard(bios.Call);
        keyboard.Open();
        keyboard.SetColors(HpOmenKeyboard.DeviceId, [Red, Red, Red, Red]);
        WaitFor(() => bios.BacklightWrites == 1);

        bios.Backlight = 0x64;   // the person switches it off with the keyboard's own key
        keyboard.SetColors(HpOmenKeyboard.DeviceId, [Blue, Blue, Blue, Blue]);
        WaitFor(() => bios.ColorWrites == 2);
        Thread.Sleep(100);
        Assert.Equal(0x64, bios.Backlight);
        Assert.Equal(1, bios.BacklightWrites);
    }

    [Fact]
    public void Turning_the_lights_off_never_switches_the_backlight_on()
    {
        var bios = new FakeBios();
        using var keyboard = new HpOmenKeyboard(bios.Call);
        keyboard.Open();
        keyboard.SetColors(HpOmenKeyboard.DeviceId, [RgbColor.Black, RgbColor.Black, RgbColor.Black, RgbColor.Black]);
        WaitFor(() => bios.ColorWrites == 1);
        Thread.Sleep(100);
        Assert.Equal(0, bios.BacklightWrites);
    }

    [Theory]
    [InlineData(3, 1, 3)]   // a per-key keyboard answers these calls but ignores them
    [InlineData(1, 0, 3)]   // no backlight
    [InlineData(0, 1, 0)]   // a single-color keyboard reports one zone
    public void Other_keyboards_are_left_alone(byte type, byte hasBacklight, byte zonesMinusOne)
    {
        var bios = new FakeBios(type, hasBacklight, zonesMinusOne);
        using var keyboard = new HpOmenKeyboard(bios.Call);
        keyboard.Open();
        Assert.False(keyboard.IsConnected);
        Assert.Empty(keyboard.Devices);
        keyboard.SetColors(HpOmenKeyboard.DeviceId, [Red, Red, Red, Red]);
        Thread.Sleep(100);
        Assert.Equal(0, bios.ColorWrites);
    }

    [Fact]
    public void A_pc_with_no_such_interface_is_asked_once()
    {
        var calls = 0;
        using var keyboard = new HpOmenKeyboard((_, _, _, _) => { calls++; return null; });
        keyboard.Open();
        var afterFirst = calls;
        keyboard.Open();
        keyboard.Open();
        Assert.False(keyboard.IsConnected);
        Assert.Equal(afterFirst, calls);
    }

    [Fact]
    public void A_bios_that_keeps_refusing_is_reported_not_hidden()
    {
        var bios = new FakeBios { Refuse = true };
        using var keyboard = new HpOmenKeyboard(bios.Call);
        keyboard.Open();
        for (var i = 0; i < 5; i++)
        {
            keyboard.SetColors(HpOmenKeyboard.DeviceId, [new RgbColor((byte)i, 0, 0), Red, Red, Red]);
            Thread.Sleep(260);
        }
        WaitFor(() => !keyboard.IsConnected);
        Assert.NotNull(keyboard.Status);
    }

    [Fact]
    public void A_keyboard_openrgb_already_shows_is_not_listed_twice()
    {
        var elsewhere = false;
        using var keyboard = new HpOmenKeyboard(new FakeBios().Call, () => elsewhere);
        keyboard.Open();
        Assert.Single(keyboard.Devices);
        elsewhere = true;
        Assert.Empty(keyboard.Devices);
    }

    [Fact]
    public void Backends_are_combined_and_each_device_goes_to_the_one_that_found_it()
    {
        var bios = new FakeBios();
        var simulated = new Kelvin.Core.Simulated.SimulatedRgbProvider();
        using var all = new CombinedRgbProvider(simulated, new HpOmenKeyboard(bios.Call), new HpOmenKeyboard((_, _, _, _) => null));
        all.Open();
        Assert.True(all.IsConnected);
        Assert.Equal(simulated.Devices.Count + 1, all.Devices.Count);
        Assert.Equal("Simulated + HP keyboard", all.Name);

        all.SetColors(HpOmenKeyboard.DeviceId, [Green, Green, Green, Green]);
        WaitFor(() => bios.ColorWrites == 1);
        all.SetColors("sim/rgb/board", [Green]);   // goes to the simulated backend, not the keyboard
        Thread.Sleep(100);
        Assert.Equal(1, bios.ColorWrites);
    }
}
