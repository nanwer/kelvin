using System.Text;

namespace Kelvin.Core;

public sealed record HardwareReportInput(
    string Version,
    string Os,
    bool Simulated,
    bool FanDriverInstalled,
    IReadOnlyList<HardwareInfo> Hardware,
    IReadOnlyList<FanReading> Fans,
    IReadOnlyList<FanProfile> Profiles,
    IReadOnlyList<SensorReading> Sensors,
    bool LightingConnected,
    string? LightingStatus,
    IReadOnlyList<RgbDevice> Devices,
    IReadOnlyList<Conflict> Conflicts);

public sealed record HardwareReportResult(string Title, string Body, string IssueUrl, bool BodyInUrl);

/// <summary>
/// Turns what Kelvin detected into a GitHub issue, so people can report boards and devices the
/// maintainers don't own. Only model names and readings go in: no serial numbers, device paths,
/// user names or file paths. The user sees the full text before anything is sent.
/// </summary>
public static class HardwareReport
{
    public const string RepositoryUrl = "https://github.com/nanwer/kelvin";
    private const int MaxUrlLength = 7500; // browsers and GitHub handle URLs up to about 8 KB

    public static HardwareReportResult Build(HardwareReportInput input)
    {
        var board = input.Hardware.FirstOrDefault(h => h.Kind == "Motherboard")?.Name ?? "Unknown board";
        var title = $"Hardware report: {board}";
        var b = new StringBuilder();

        b.AppendLine("**What works and what doesn't?**");
        b.AppendLine("<!-- e.g. \"Fans work, RAM lighting isn't detected\". Anything you tell us helps. -->");
        b.AppendLine();
        b.AppendLine("---");
        b.AppendLine();
        b.AppendLine("### System");
        b.AppendLine($"- Kelvin {input.Version}{(input.Simulated ? " (simulated hardware)" : "")}");
        b.AppendLine($"- {input.Os}");
        b.AppendLine($"- PawnIO driver: {(input.FanDriverInstalled ? "installed" : "missing")}");
        b.AppendLine();

        b.AppendLine("### Detected hardware");
        Table(b, ["Type", "Name"], input.Hardware.Select(h => new[] { h.Parent is null ? h.Kind : $"↳ {h.Kind}", h.Name }));

        b.AppendLine("### Fans");
        var profiles = input.Profiles.ToDictionary(p => p.FanId);
        Table(b, ["Fan", "RPM", "Duty", "Controllable", "Measured"], input.Fans.Select(f =>
        {
            var p = profiles.GetValueOrDefault(f.Id);
            var measured = p is null ? "not measured" : !p.Connected ? "nothing connected"
                : $"{p.MinRpm:0}–{p.MaxRpm:0} rpm{(p.StopPercent is { } s ? $", stalls ≤ {s:0}%" : "")}";
            return new[] { f.Name, f.Rpm is { } r ? $"{r:0}" : "–", f.Percent is { } d ? $"{d:0}%" : "–", f.Controllable ? "yes" : "no", measured };
        }));

        b.AppendLine("### Temperature sensors");
        Table(b, ["Part", "Sensor", "Value"], input.Sensors
            .Where(s => s.Kind == SensorKind.Temperature && s.Role != SensorRole.Virtual)
            .Select(s => new[] { s.Hardware, s.Name + (s.Primary ? " (main)" : ""), $"{s.Value:0.#} °C" }));

        b.AppendLine("### Lighting (OpenRGB)");
        if (!input.LightingConnected)
            b.AppendLine($"Not connected: {Clean(input.LightingStatus ?? "unknown")}").AppendLine();
        else
            Table(b, ["Vendor", "Device", "Type", "LEDs", "Zones"], input.Devices.Select(d => new[]
            {
                d.Vendor, d.Name, d.Kind, $"{d.LedCount}",
                string.Join(", ", d.Zones.Select(z => $"{z.Name} ({z.LedCount})")),
            }));

        if (input.Conflicts.Count > 0)
        {
            b.AppendLine("### Other RGB / fan software running");
            foreach (var c in input.Conflicts) b.AppendLine($"- {Clean(c.Name)}");
            b.AppendLine();
        }

        var body = b.ToString();
        var url = IssueUrl(title, body);
        if (url.Length <= MaxUrlLength) return new(title, body, url, true);

        // Too long for a link: the app copies the report and the issue asks for a paste.
        var shortBody = "**Kelvin copied your hardware report to the clipboard. Paste it here (Ctrl+V).**\n";
        return new(title, body, IssueUrl(title, shortBody), false);
    }

    private static string IssueUrl(string title, string body) =>
        $"{RepositoryUrl}/issues/new?labels=hardware-report&title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(body)}";

    private static void Table(StringBuilder b, string[] headers, IEnumerable<string[]> rows)
    {
        var list = rows.ToList();
        if (list.Count == 0) { b.AppendLine("_None detected._").AppendLine(); return; }
        b.AppendLine("| " + string.Join(" | ", headers) + " |");
        b.AppendLine("|" + string.Concat(headers.Select(_ => "---|")));
        foreach (var r in list) b.AppendLine("| " + string.Join(" | ", r.Select(Clean)) + " |");
        b.AppendLine();
    }

    /// <summary>Keeps device-reported text from breaking the Markdown table.</summary>
    private static string Clean(string s) =>
        s.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ").Replace("<", "&lt;").Trim();
}
