using System.Text.Json;

namespace Kelvin.Core;

/// <summary>A complete setup Kelvin can switch to: fan settings, lighting and (on laptops) the fan mode.</summary>
public sealed class Profile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<FanSettings> Fans { get; set; } = [];
    public LightingSettings Lighting { get; set; } = new();
    /// <summary>Laptop fan mode id ("quiet", "balanced", "performance", "max"), or null to leave it alone.</summary>
    public string? FanMode { get; set; }
}

public sealed class ProfileAutomation
{
    public bool Enabled { get; set; } = true;
    public string DefaultProfileId { get; set; } = "balanced";

    public string? GameProfileId { get; set; } = "gaming";
    /// <summary>Treat a busy GPU (70%+ for 30 s) as a game, so it works without listing anything.</summary>
    public bool DetectGamesByGpu { get; set; } = true;
    /// <summary>Executable names, without ".exe", that always count as a game.</summary>
    public List<string> GameProcesses { get; set; } = [];

    public bool NightEnabled { get; set; }
    public string? NightProfileId { get; set; } = "night";
    public string NightStart { get; set; } = "23:00";
    public string NightEnd { get; set; } = "07:00";
}

public enum ProfileSource { Default, Night, Game, Manual }

public sealed record ProfileStatus(string? ActiveId, ProfileSource Source, string Reason, string? Game);

/// <summary>
/// Picks and applies profiles. Priority: a running game, then the night schedule, then the default.
/// A profile chosen by hand holds until the automatic situation changes (a game starts or ends, the
/// night window opens or closes), so it neither fights the user nor sticks forever.
/// </summary>
public sealed class ProfileEngine
{
    public static readonly TimeSpan GpuBusyFor = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan GpuIdleFor = TimeSpan.FromSeconds(60);
    public const double GpuBusyLoad = 70;
    public const double GpuIdleLoad = 30;

    private readonly SettingsStore _store;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<IReadOnlyCollection<string>> _processes;
    private readonly object _gate = new();
    private DateTimeOffset? _gpuBusySince, _gpuIdleSince;
    private bool _gpuGame;
    private string? _manualId;
    private string? _manualKey;

    /// <summary>Applies a laptop fan mode when a profile with one is activated.</summary>
    public Action<string>? ApplyFanMode { get; set; }
    /// <summary>Called after a profile is applied, e.g. to repaint lighting immediately.</summary>
    public Action? Applied { get; set; }

    public ProfileStatus Status { get; private set; } = new(null, ProfileSource.Default, "Starting", null);

    public ProfileEngine(SettingsStore store, Func<IReadOnlyCollection<string>> processes, Func<DateTimeOffset>? now = null)
    {
        _store = store;
        _processes = processes;
        _now = now ?? (() => DateTimeOffset.Now);
        EnsureDefaults(store);
        // Editing fans or lighting saves straight into the active profile.
        store.BeforeSave = s =>
        {
            var active = s.Profiles.FirstOrDefault(p => p.Id == s.ActiveProfileId);
            if (active is null) return;
            active.Fans = Copy(s.Fans);
            active.Lighting = Copy(s.Lighting);
        };
    }

    /// <summary>Creates Quiet, Balanced, Gaming and Night from the current setup, the first time.</summary>
    public static void EnsureDefaults(SettingsStore store)
    {
        if (store.Read(s => s.Profiles.Count) > 0) return;
        store.Mutate(s =>
        {
            Profile Make(string id, string name, string preset, double brightness, bool lightsOff, string fanMode)
            {
                var fans = Copy(s.Fans);
                foreach (var f in fans.Where(f => f.Mode == FanMode.Curve && preset != "keep"))
                {
                    f.Preset = preset;
                    f.Points = [.. Curves.Presets[preset]];
                }
                var lighting = Copy(s.Lighting);
                lighting.Brightness = Math.Round(lighting.Brightness * brightness, 2);
                if (lightsOff) lighting.Effect = LightingEffect.Off;
                return new Profile { Id = id, Name = name, Fans = fans, Lighting = lighting, FanMode = fanMode };
            }

            s.Profiles =
            [
                Make("quiet", "Quiet", "silent", 0.5, false, "quiet"),
                Make("balanced", "Balanced", "keep", 1, false, "balanced"),
                Make("gaming", "Gaming", "performance", 1, false, "performance"),
                Make("night", "Night", "silent", 1, true, "quiet"),
            ];
            s.ActiveProfileId = "balanced";
        });
    }

    /// <summary>Chooses a profile by hand. It holds until the automatic situation next changes.</summary>
    public void SetManual(string id)
    {
        lock (_gate)
        {
            if (!_store.Read(s => s.Profiles.Any(p => p.Id == id))) throw new KeyNotFoundException("That profile doesn't exist.");
            _manualId = id;
            _manualKey = AutoKey(Automatic(lastLoad: null));
            Status = Status with { ActiveId = id, Source = ProfileSource.Manual, Reason = "Chosen by you" };
            Activate(id);
        }
    }

    private sealed record AutoChoice(string Id, ProfileSource Source, string Reason, string? Game);

    private static string AutoKey(AutoChoice c) => $"{c.Source}:{c.Id}";

    /// <summary>Runs every few seconds: works out the right profile and switches if needed.</summary>
    public void Evaluate(IReadOnlyList<SensorReading> sensors)
    {
        lock (_gate)
        {
            var gpuLoad = sensors.FirstOrDefault(s => s.Role == SensorRole.Gpu && s.Kind == SensorKind.Load)?.Value;
            var auto = Automatic(gpuLoad);
            var automation = _store.Read(s => s.Automation);
            var active = _store.Read(s => s.ActiveProfileId);

            if (_manualId is not null && (!automation.Enabled || AutoKey(auto) == _manualKey))
            {
                Status = new(_manualId, ProfileSource.Manual, automation.Enabled
                    ? $"Chosen by you. Kelvin switches again when {NextChange(auto)}."
                    : "Chosen by you. Automatic switching is off.", null);
                if (active != _manualId) Activate(_manualId);
                return;
            }
            _manualId = null;

            if (!automation.Enabled)
            {
                Status = new(active, ProfileSource.Manual, "Automatic switching is off.", null);
                return;
            }

            Status = new(auto.Id, auto.Source, auto.Reason, auto.Game);
            if (active != auto.Id) Activate(auto.Id);
        }
    }

    private static string NextChange(AutoChoice auto) => auto.Source switch
    {
        ProfileSource.Game => "the game closes",
        ProfileSource.Night => "the night schedule ends",
        _ => "a game starts or the night schedule begins",
    };

    private AutoChoice Automatic(double? lastLoad)
    {
        var (a, profiles) = _store.Read(s => (s.Automation, s.Profiles.Select(p => p.Id).ToHashSet()));
        string Pick(string? id) => id is not null && profiles.Contains(id) ? id : a.DefaultProfileId;
        var now = _now();

        // Games: a listed executable, or a GPU that's been busy for a while.
        var game = MatchGame(a.GameProcesses);
        if (lastLoad is { } load && a.DetectGamesByGpu) UpdateGpu(load, now);
        if (game is not null && a.GameProfileId is not null)
            return new(Pick(a.GameProfileId), ProfileSource.Game, $"{game} is running", game);
        if (_gpuGame && a.DetectGamesByGpu && a.GameProfileId is not null)
            return new(Pick(a.GameProfileId), ProfileSource.Game, "Your graphics card is busy, so a game is probably running", null);

        if (a.NightEnabled && a.NightProfileId is not null && InWindow(now, a.NightStart, a.NightEnd))
            return new(Pick(a.NightProfileId), ProfileSource.Night, $"Night schedule ({a.NightStart}–{a.NightEnd})", null);

        return new(Pick(a.DefaultProfileId), ProfileSource.Default, "Default profile", null);
    }

    private void UpdateGpu(double load, DateTimeOffset now)
    {
        if (load >= GpuBusyLoad) { _gpuBusySince ??= now; _gpuIdleSince = null; }
        else if (load < GpuIdleLoad) { _gpuIdleSince ??= now; _gpuBusySince = null; }
        else { _gpuBusySince = null; _gpuIdleSince = null; }

        if (!_gpuGame && _gpuBusySince is { } b && now - b >= GpuBusyFor) _gpuGame = true;
        if (_gpuGame && _gpuIdleSince is { } i && now - i >= GpuIdleFor) _gpuGame = false;
    }

    private string? MatchGame(List<string> names)
    {
        if (names.Count == 0) return null;
        var running = _processes();
        foreach (var n in names)
        {
            var name = n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n;
            if (running.Contains(name, StringComparer.OrdinalIgnoreCase)) return name;
        }
        return null;
    }

    public static bool InWindow(DateTimeOffset now, string start, string end)
    {
        if (!TimeOnly.TryParse(start, out var s) || !TimeOnly.TryParse(end, out var e) || s == e) return false;
        var t = TimeOnly.FromDateTime(now.LocalDateTime);
        return s < e ? t >= s && t < e : t >= s || t < e; // windows may wrap past midnight
    }

    private void Activate(string id)
    {
        Profile? profile = null;
        _store.Mutate(s =>
        {
            profile = s.Profiles.FirstOrDefault(p => p.Id == id);
            if (profile is null) return;
            s.Fans = Copy(profile.Fans);
            s.Lighting = Copy(profile.Lighting);
            s.ActiveProfileId = id;
        });
        if (profile?.FanMode is { } mode)
        {
            try { ApplyFanMode?.Invoke(mode); } catch { /* a laptop mode failing must not block the profile */ }
        }
        Applied?.Invoke();
    }

    public static T Copy<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, SettingsStore.Json), SettingsStore.Json)!;
}
