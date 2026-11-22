using System.Globalization;
using System.Text.Json;

namespace Kelvin.Core;

/// <summary>One fan in a FanControl configuration, and what Kelvin would do with it.</summary>
/// <param name="FanId">The matching fan in Kelvin, or null when there isn't one on this PC.</param>
/// <param name="Settings">What importing would save, or null when it can't be brought over.</param>
/// <param name="Suggested">Whether the import ticks this fan by default.</param>
public sealed record ImportedFan(string Label, string? FanId, string? FanName, FanSettings? Settings, string Summary, string? Note, bool Suggested);

public sealed record ImportPreview(string? File, string? Version, IReadOnlyList<ImportedFan> Fans, IReadOnlyList<string> Notes)
{
    public int Importable => Fans.Count(f => f.Settings is not null);
}

/// <summary>
/// Reads a configuration file from FanControl (Rem0o's fan control app) and turns its fans and
/// curves into Kelvin's settings. Nothing is saved here: the result is a preview the person
/// reviews first.
///
/// FanControl has no published file format. This follows what real configuration files from
/// versions 81 to 284 contain: curve types are told apart by which properties they have, and
/// older files embed whole curves where newer ones only name them, so everything is looked up
/// by name. The file is untrusted input: every field is optional, sizes are capped, and the
/// result still goes through <see cref="SettingsRules"/> before it is saved.
/// </summary>
public static class FanControlImport
{
    public const int MaxFileBytes = 2 * 1024 * 1024;
    private const int MaxItems = 64;

    public static ImportPreview Read(string json, IReadOnlyList<FanReading> fans, IReadOnlyList<SensorReading> sensors, string? file = null)
    {
        if (json.Length > MaxFileBytes) throw new FormatException("That file is too large to be a FanControl configuration.");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException) { throw new FormatException("That file isn't a FanControl configuration (it isn't valid JSON)."); }

        using (doc)
        {
            var root = doc.RootElement;
            // "FanControl" since about version 262, "Main" before that.
            var main = Obj(root, "FanControl") ?? Obj(root, "Main");
            var controls = Arr(main, "Controls");
            if (main is null || controls.Count == 0)
                throw new FormatException("That file doesn't look like a FanControl configuration: it has no fans in it.");

            var curves = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var c in Arr(main, "FanCurves").Take(MaxItems))
                if (Str(c, "Name") is { Length: > 0 } name) curves.TryAdd(name, c);
            var custom = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var c in Arr(main, "CustomSensors").Take(MaxItems))
                if (Str(c, "Identifier") is { Length: > 0 } id) custom.TryAdd(id, c);

            var notes = new List<string>();
            if (Obj(root, "MainWindow") is { } window && Bool(window, "Fahrenheit") == true)
                notes.Add("FanControl was showing Fahrenheit. Check the imported temperatures before you rely on them.");

            var context = new Context(fans, sensors, curves, custom);
            var result = new List<ImportedFan>();
            var byControl = new Dictionary<string, ImportedFan>(StringComparer.Ordinal);
            var synced = new List<(int Index, string Source)>();

            foreach (var control in controls.Take(MaxItems))
            {
                var identifier = Str(control, "Identifier") ?? "";
                var label = Clip(Str(control, "NickName") ?? Str(control, "Name") ?? identifier);
                var fan = context.MatchFan(identifier, label);
                var imported = Convert(control, label, fan, context, out var syncSource);
                if (syncSource is not null) synced.Add((result.Count, syncSource));
                result.Add(imported);
                if (identifier.Length > 0) byControl.TryAdd(identifier, imported);
            }

            // A fan set to copy another fan gets that fan's settings.
            foreach (var (index, source) in synced)
            {
                var me = result[index];
                if (me.FanId is null || !byControl.TryGetValue(source, out var other) || other.Settings is null) continue;
                var copy = other.Settings.Clone();
                copy.FanId = me.FanId;
                result[index] = me with { Settings = copy, Summary = $"Same as {other.Label}: {Lower(other.Summary)}", Note = null, Suggested = !IsGpu(context, me.FanId) };
            }

            return new ImportPreview(file, Clip(Str(root, "__VERSION__")), result, notes);
        }
    }

    private static ImportedFan Convert(JsonElement control, string label, FanReading? fan, Context ctx, out string? syncSource)
    {
        syncSource = null;
        ImportedFan Skip(string summary, string? note = null) => new(label, fan?.Id, fan?.Name, null, summary, note, false);

        if (fan is null) return Skip("Not found on this PC", "Kelvin can't see a fan that matches this one.");
        if (!fan.Controllable) return Skip("Read only in Kelvin", "Kelvin can read this fan's speed but can't set it.");
        if (Bool(control, "Enable") != true) return Skip("FanControl wasn't controlling it", "Stays on BIOS control.");

        var settings = new FanSettings { FanId = fan.Id, Preset = "custom" };
        settings.MinPercent = Math.Clamp(Num(control, "MinimumPercent") ?? 0, 0, 60);
        // Graphics card fans are left to the card unless the person chooses otherwise.
        var suggested = !fan.IsGpuFan;
        ImportedFan Done(string summary, string? note = null) =>
            new(label, fan.Id, fan.Name, SettingsRules.Fan(settings), summary, fan.IsGpuFan ? Join("Kelvin normally leaves graphics card fans to the card. Tick this only if you want Kelvin to drive it.", note) : note, suggested);

        if (Bool(control, "ManualControl") == true)
        {
            settings.Mode = FanMode.Fixed;
            settings.FixedPercent = Math.Clamp(Num(control, "ManualControlValue") ?? 50, 0, 100);
            return Done($"Fixed at {settings.FixedPercent:0}%");
        }

        var curveName = Obj(control, "SelectedFanCurve") is { } selected ? Str(selected, "Name") : null;
        if (curveName is null || !ctx.Curves.TryGetValue(curveName, out var curve))
            return Skip("No curve was chosen in FanControl", "Stays on BIOS control.");
        if (Num(curve, "CommandMode") is { } mode && mode != 0)
            return Skip($"\"{Clip(curveName)}\" sets a speed in RPM", "Kelvin sets fan speed as a percentage, so this one can't be brought over.");

        var offset = Num(control, "SelectedOffset") ?? 0;
        var made = FromCurve(curve, curveName, ctx, settings, offset, depth: 0, out syncSource);
        if (made.Summary is null) return Skip(made.Problem!, made.Note);
        return Done(made.Summary, made.Note);
    }

    private readonly record struct Made(string? Summary, string? Note, string? Problem);

    /// <summary>Fills <paramref name="s"/> from one FanControl curve. The curve's type is whichever set of properties it has.</summary>
    private static Made FromCurve(JsonElement curve, string name, Context ctx, FanSettings s, double offset, int depth, out string? syncSource)
    {
        syncSource = null;
        name = Clip(name);
        static Made Problem(string what, string? note = null) => new(null, note, what);

        if (Has(curve, "Percent"))
        {
            s.Mode = FanMode.Fixed;
            s.FixedPercent = Math.Clamp((Num(curve, "Percent") ?? 50) + offset, 0, 100);
            return new($"Fixed at {s.FixedPercent:0}%", null, null);
        }

        if (Has(curve, "SelectedControl"))
        {
            syncSource = Obj(curve, "SelectedControl") is { } other ? Str(other, "Identifier") : null;
            return Problem($"\"{name}\" copies another fan", "The fan it copies couldn't be brought over, so this one can't either.");
        }

        if (Has(curve, "SelectedFanCurves")) return Mix(curve, name, ctx, s, offset, depth);

        var source = ctx.MatchSensor(Obj(curve, "SelectedTempSource") is { } src ? Str(src, "Identifier") : null, depth: 0, out var smooth, out var sourceNote);
        if (source is null) return Problem($"\"{name}\" follows a temperature Kelvin can't see", sourceNote);
        s.SourceSensorId = source.Id;
        if (smooth is { } seconds) s.SmoothSeconds = Math.Clamp(seconds, 0, 120);
        var follows = Describe(source);

        // Auto: FanControl works the speed out itself to hold a temperature. That is Kelvin's target mode.
        if (Has(curve, "MinFanSpeed") && Has(curve, "LoadTemperature"))
        {
            s.Mode = FanMode.Target;
            s.TargetTemp = Math.Clamp(Num(curve, "LoadTemperature") ?? 70, SettingsRules.MinTargetTemp, SettingsRules.MaxTargetTemp);
            s.MinPercent = Math.Clamp(Math.Max(s.MinPercent, Num(curve, "MinFanSpeed") ?? 0), 0, 60);
            return new($"Target: keeps {follows} under {s.TargetTemp:0}°C, never slower than {s.MinPercent:0}%",
                Join("FanControl's Auto curve becomes Kelvin's Target mode, which does the same job.", sourceNote), null);
        }

        List<CurvePoint> points;
        string kind;
        string? note = sourceNote;
        if (Has(curve, "Points"))
        {
            points = [];
            foreach (var p in Arr(curve, "Points").Take(SettingsRules.MaxCurvePoints))
            {
                // "temperature,percent" with a dot for decimals, whatever the PC's language.
                var parts = (p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "").Split(',');
                if (parts.Length == 2 && Parse(parts[0]) is { } t && Parse(parts[1]) is { } pct) points.Add(new(t, pct));
            }
            kind = "Curve";
        }
        else if (Has(curve, "MinimumFanSpeed"))
        {
            points = Two(Num(curve, "MinimumTemperature"), Num(curve, "MinimumFanSpeed"), Num(curve, "MaximumTemperature"), Num(curve, "MaximumFanSpeed"));
            kind = "Straight-line curve";
        }
        else if (Has(curve, "IdleFanSpeed"))
        {
            // Trigger (called Target in old versions) jumps between two speeds. A ramp between the
            // same two points is the nearest thing Kelvin has, and it is gentler on the ears.
            points = Two(Num(curve, "IdleTemperature"), Num(curve, "IdleFanSpeed"), Num(curve, "LoadTemperature"), Num(curve, "LoadFanSpeed"));
            kind = "Curve";
            note = Join("FanControl switched between two speeds here. Kelvin ramps smoothly between the same two points instead.", note);
        }
        else return Problem($"\"{name}\" is a kind of curve Kelvin doesn't know");

        if (offset != 0) points = points.Select(p => p with { Percent = Math.Clamp(p.Percent + offset, 0, 100) }).ToList();
        try { s.Points = Curves.Sanitize(points); }
        catch (ArgumentException) { return Problem($"\"{name}\" has no usable points"); }
        s.Mode = FanMode.Curve;
        var hottest = s.Points[^1];
        if (points.Count > 0 && points.Max(p => p.Percent) < 100 && hottest.Percent >= 100)
            note = Join(note, "Kelvin's curves always reach 100% at the hot end, so a point was added there.");
        return new($"{kind} following {follows}, {s.Points[0].Percent:0}% at {s.Points[0].Temp:0}°C up to {hottest.Percent:0}% at {hottest.Temp:0}°C", note, null);
    }

    /// <summary>
    /// A Mix curve takes the highest (or another combination) of several curves. Kelvin has one
    /// curve per fan, so only the common case comes over: the highest of curves that follow the
    /// CPU and the graphics card becomes one curve that follows the hotter of the two.
    /// </summary>
    private static Made Mix(JsonElement curve, string name, Context ctx, FanSettings s, double offset, int depth)
    {
        Made No(string why) => new(null, why, $"\"{name}\" combines several curves");
        if (depth > 0) return No("Kelvin has one curve per fan. Set this fan up by hand.");
        if ((Num(curve, "SelectedMixFunction") ?? 0) != 0) return No("Kelvin can only bring over a mix that takes the highest of its curves. Set this fan up by hand.");

        var parts = new List<FanSettings>();
        foreach (var reference in Arr(curve, "SelectedFanCurves").Take(8))
        {
            if (Str(reference, "Name") is not { } partName || !ctx.Curves.TryGetValue(partName, out var part)) return No("One of its curves is missing from the file.");
            var ps = new FanSettings();
            var made = FromCurve(part, partName, ctx, ps, 0, depth + 1, out _);
            if (made.Summary is null || ps.Mode != FanMode.Curve) return No("Kelvin can only combine ordinary curves. Set this fan up by hand.");
            parts.Add(ps);
        }
        var roles = parts.Select(p => ctx.Sensors.FirstOrDefault(x => x.Id == p.SourceSensorId)?.Role).ToList();
        if (parts.Count < 2 || roles.Any(r => r is not (SensorRole.Cpu or SensorRole.Gpu)) || !ctx.Sensors.Any(x => x.Id == SensorIds.Hottest))
            return No("Kelvin can combine curves that follow the CPU and the graphics card. Set this fan up by hand.");

        // The highest of the curves at every temperature any of them mentions.
        var temps = parts.SelectMany(p => p.Points.Select(x => x.Temp)).Distinct().OrderBy(t => t).Take(SettingsRules.MaxCurvePoints);
        var points = temps.Select(t => new CurvePoint(t, Math.Clamp(parts.Max(p => Curves.Evaluate(p.Points, t)) + offset, 0, 100))).ToList();
        try { s.Points = Curves.Sanitize(points); }
        catch (ArgumentException) { return No("Its curves have no usable points."); }
        s.Mode = FanMode.Curve;
        s.SourceSensorId = SensorIds.Hottest;
        return new($"Curve following the hotter of CPU and GPU, {s.Points[0].Percent:0}% at {s.Points[0].Temp:0}°C up to {s.Points[^1].Percent:0}% at {s.Points[^1].Temp:0}°C",
            "FanControl took the highest of several curves. Kelvin uses one curve that follows whichever of the CPU and graphics card is hotter, which is close but not identical.", null);
    }

    private sealed class Context(IReadOnlyList<FanReading> fans, IReadOnlyList<SensorReading> sensors,
        Dictionary<string, JsonElement> curves, Dictionary<string, JsonElement> custom)
    {
        public IReadOnlyList<FanReading> Fans => fans;
        public IReadOnlyList<SensorReading> Sensors => sensors;
        public Dictionary<string, JsonElement> Curves => curves;

        public FanReading? MatchFan(string identifier, string label)
        {
            var exact = fans.FirstOrDefault(f => f.Id == identifier) ?? fans.FirstOrDefault(f => Normal(f.Id) == Normal(identifier));
            if (exact is not null) return exact;
            // Graphics cards are named differently by every library; one card with one fan control is unambiguous.
            if (IsGraphics(identifier) && identifier.Contains("control", StringComparison.OrdinalIgnoreCase))
            {
                var gpu = fans.Where(f => f.IsGpuFan || f.Id.StartsWith("/gpu-", StringComparison.OrdinalIgnoreCase)).ToList();
                if (gpu.Count == 1) return gpu[0];
            }
            var byName = fans.Where(f => f.Name.Equals(label, StringComparison.OrdinalIgnoreCase)).ToList();
            return byName.Count == 1 ? byName[0] : null;
        }

        /// <summary>The Kelvin sensor for a FanControl temperature source, following its custom sensors through.</summary>
        public SensorReading? MatchSensor(string? identifier, int depth, out double? smoothSeconds, out string? note)
        {
            smoothSeconds = null;
            note = null;
            if (string.IsNullOrEmpty(identifier) || depth > 4) return null;
            var temps = sensors.Where(s => s.Kind == SensorKind.Temperature).ToList();

            if (custom.TryGetValue(identifier, out var made))
            {
                if (Has(made, "SelectedSensors"))
                {
                    // Sensor mix: 0 average, 1 highest, 2 lowest. Highest of CPU and GPU is Kelvin's "hottest".
                    var parts = Arr(made, "SelectedSensors").Take(8)
                        .Select(p => MatchSensor(Str(p, "Identifier"), depth + 1, out _, out _)).ToList();
                    var hottest = temps.FirstOrDefault(s => s.Id == SensorIds.Hottest);
                    if (Num(made, "SelectedMixFunction") == 1 && hottest is not null && parts.Count >= 2 &&
                        parts.All(p => p?.Role is SensorRole.Cpu or SensorRole.Gpu))
                        return hottest;
                    note = "It followed a mix of temperatures in FanControl. Kelvin follows the first of them.";
                    return parts.FirstOrDefault(p => p is not null);
                }
                var inner = MatchSensor(Obj(made, "SelectedTempSource") is { } src ? Str(src, "Identifier") : null, depth + 1, out smoothSeconds, out note);
                if (Num(made, "SelectedTime") is { } time) smoothSeconds = time;   // a time average is Kelvin's "reaction to spikes"
                if (Num(made, "Offset") is { } off && off != 0) note = Join(note, $"FanControl added {off:0.#}°C to this temperature. Kelvin follows the real reading.");
                return inner;
            }

            var exact = temps.FirstOrDefault(s => s.Id == identifier) ?? temps.FirstOrDefault(s => Normal(s.Id) == Normal(identifier));
            if (exact is not null) return exact;

            SensorReading? Primary(SensorRole role) => temps.FirstOrDefault(s => s.Role == role && s.Primary) ?? temps.FirstOrDefault(s => s.Role == role);
            if (IsGraphics(identifier))
            {
                // e.g. "ADLX/AMD Radeon RX 9070 XT/2560/temp/Memory": match the last word against sensor names.
                var last = Letters(identifier[(identifier.LastIndexOf('/') + 1)..]);
                var named = last.Length >= 3 ? temps.FirstOrDefault(s => s.Role == SensorRole.Gpu && Letters(s.Name).EndsWith(last, StringComparison.OrdinalIgnoreCase)) : null;
                return named ?? Primary(SensorRole.Gpu);
            }
            if (identifier.Contains("cpu", StringComparison.OrdinalIgnoreCase)) return Primary(SensorRole.Cpu);
            return null;
        }
    }

    private static bool IsGpu(Context ctx, string fanId) => ctx.Fans.FirstOrDefault(f => f.Id == fanId)?.IsGpuFan == true;

    private static bool IsGraphics(string id) =>
        id.StartsWith("/gpu-", StringComparison.OrdinalIgnoreCase) || id.StartsWith("ADLX", StringComparison.OrdinalIgnoreCase) ||
        id.StartsWith("NVApiWrapper", StringComparison.OrdinalIgnoreCase) || id.StartsWith("IntelCtl", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Motherboard ids differ by one part: Kelvin's library says /lpc/nct6798d/0/control/1 where
    /// FanControl says /lpc/nct6798d/control/1. Dropping the chip's index makes them comparable.
    /// </summary>
    private static string Normal(string id)
    {
        var parts = id.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 5 && parts[0].Equals("lpc", StringComparison.OrdinalIgnoreCase) && int.TryParse(parts[2], out _))
            parts = [parts[0], parts[1], parts[3], parts[4]];
        return "/" + string.Join('/', parts).ToLowerInvariant();
    }

    private static string Describe(SensorReading s) => s.Role switch
    {
        SensorRole.Virtual => "the hotter of CPU and GPU",
        SensorRole.Cpu => s.Primary ? "the CPU" : $"the CPU ({s.Name})",
        SensorRole.Gpu => s.Primary ? "the graphics card" : $"the graphics card ({s.Name})",
        _ => s.Name,
    };

    private static List<CurvePoint> Two(double? t0, double? p0, double? t1, double? p1) =>
        t0 is null || p0 is null || t1 is null || p1 is null ? [] : [new(t0.Value, p0.Value), new(t1.Value, p1.Value)];

    private static string Letters(string s) => new(s.Where(char.IsLetterOrDigit).ToArray());
    private static string Lower(string s) => s.Length > 0 ? char.ToLowerInvariant(s[0]) + s[1..] : s;
    private static string? Join(string? a, string? b) => a is null ? b : b is null ? a : $"{a} {b}";
    private static string Clip(string? s) => s is null ? "" : s.Length > 80 ? s[..80] : s;
    private static double? Parse(string s) =>
        double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;

    private static bool Has(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null;
    private static JsonElement? Obj(JsonElement? e, string name) =>
        e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;
    private static List<JsonElement> Arr(JsonElement? e, string name) =>
        e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind is JsonValueKind.Object or JsonValueKind.String).ToList() : [];
    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static double? Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;
    private static bool? Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    // ---- Finding FanControl's own file

    /// <summary>
    /// The configuration FanControl is using, if FanControl is installed where its installer puts
    /// it. Only that fixed folder is looked in: Kelvin's service runs with full rights, so it never
    /// opens a path that came from a file or a request. A portable copy is imported by choosing
    /// the file in the app instead.
    /// </summary>
    public static (string Name, string Json)? FindInstalled()
    {
        foreach (var root in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
        {
            try
            {
                var folder = Path.Combine(Environment.GetFolderPath(root), "FanControl", "Configurations");
                if (!Directory.Exists(folder)) continue;
                var name = "userConfig.json";
                var cache = Path.Combine(folder, "CACHE");
                if (ReadSmall(cache) is { } cached)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(cached);
                        // Only ever a file name inside this folder, whatever the file says.
                        if (Str(doc.RootElement, "CurrentConfigFileName") is { Length: > 0 } current) name = Path.GetFileName(current);
                    }
                    catch (JsonException) { /* fall back to the default name */ }
                }
                if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) name = "userConfig.json";
                var json = ReadSmall(Path.Combine(folder, name)) ?? ReadSmall(Path.Combine(folder, name = "userConfig.json"));
                if (json is not null) return (name, json);
            }
            catch (Exception) { /* not there, or not readable: nothing to offer */ }
        }
        return null;
    }

    private static string? ReadSmall(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > MaxFileBytes || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return null;
        return File.ReadAllText(path);
    }
}
