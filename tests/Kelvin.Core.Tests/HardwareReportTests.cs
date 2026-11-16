using Kelvin.Core;

namespace Kelvin.Core.Tests;

public class HardwareReportTests
{
    private static HardwareReportInput Input(IReadOnlyList<RgbDevice>? devices = null) => new(
        "0.2.0", "Microsoft Windows 10.0.26300", false, true,
        [new("Motherboard", "ROG STRIX B550-I GAMING"), new("SuperIO", "Nuvoton NCT6798D", "ROG STRIX B550-I GAMING")],
        [new("f1", "CPU Fan", 1200, 50, true, false)],
        [new FanProfile { FanId = "f1", Connected = true, MinRpm = 450, MaxRpm = 1800, StopPercent = 10 }],
        [new("t", "Core (Tctl/Tdie)", "AMD Ryzen 7 5800X", SensorRole.Cpu, SensorKind.Temperature, 61.4, "°C", true)],
        true, null,
        devices ?? [new("d", "G213 Prodigy", "Logitech", "Keyboard", 5, [new("Zones", 5)])],
        []);

    [Fact]
    public void Includes_the_board_chip_fans_and_devices()
    {
        var r = HardwareReport.Build(Input());
        Assert.Equal("Hardware report: ROG STRIX B550-I GAMING", r.Title);
        Assert.Contains("Nuvoton NCT6798D", r.Body);
        Assert.Contains("450–1800 rpm, stalls ≤ 10%", r.Body);
        Assert.Contains("| Logitech | G213 Prodigy | Keyboard | 5 | Zones (5) |", r.Body);
        Assert.True(r.BodyInUrl);
        Assert.StartsWith(HardwareReport.RepositoryUrl + "/issues/new?", r.IssueUrl);
    }

    [Fact]
    public void Device_names_cannot_break_the_markdown()
    {
        var r = HardwareReport.Build(Input([new("d", "Evil | <script>", "X", "Mouse", 1, [])]));
        Assert.Contains("Evil \\| &lt;script>", r.Body);
    }

    [Fact]
    public void Very_long_reports_go_through_the_clipboard_instead_of_the_link()
    {
        var many = Enumerable.Range(0, 200).Select(i => new RgbDevice($"d{i}", $"Light strip number {i}", "Vendor", "LED strip", 60, [new("Main", 60)])).ToList();
        var r = HardwareReport.Build(Input(many));
        Assert.False(r.BodyInUrl);
        Assert.True(r.IssueUrl.Length < 7500);
        Assert.Contains("Light strip number 199", r.Body); // the full text is still there to copy
    }
}
