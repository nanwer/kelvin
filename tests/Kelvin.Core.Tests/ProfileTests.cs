using Kelvin.Core;

namespace Kelvin.Core.Tests;

public class ProfileTests
{
    private DateTimeOffset _now = new(2026, 11, 16, 15, 0, 0, TimeSpan.Zero);
    private readonly HashSet<string> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly SettingsStore _store;
    private readonly ProfileEngine _engine;
    private readonly List<string> _fanModes = [];

    public ProfileTests()
    {
        _store = new SettingsStore(Path.Combine(Path.GetTempPath(), $"kelvin-prof-{Guid.NewGuid():N}.json"));
        _store.Mutate(s =>
        {
            s.Fans = [new FanSettings { FanId = "cpu", Mode = FanMode.Curve, Preset = "balanced", Points = [.. Curves.Presets["balanced"]] }];
            s.Lighting = new LightingSettings { Effect = LightingEffect.Static, Brightness = 0.8 };
        });
        _engine = new ProfileEngine(_store, () => _running, () => _now) { ApplyFanMode = _fanModes.Add };
    }

    private static SensorReading[] Gpu(double load) =>
        [new("gpuload", "GPU core", "RTX 2080", SensorRole.Gpu, SensorKind.Load, load, "%")];

    /// <summary>Evaluates once a second for <paramref name="seconds"/> seconds.</summary>
    private void Run(int seconds, double gpuLoad = 5)
    {
        for (var i = 0; i < seconds; i++) { _engine.Evaluate(Gpu(gpuLoad)); _now = _now.AddSeconds(1); }
    }

    private string Active => _store.Read(s => s.ActiveProfileId)!;

    [Fact]
    public void Creates_four_profiles_from_the_current_setup()
    {
        var p = _store.Read(s => s.Profiles.ToDictionary(x => x.Id));
        Assert.Equal(["quiet", "balanced", "gaming", "night"], p.Keys);
        Assert.Equal("silent", p["quiet"].Fans[0].Preset);
        Assert.Equal("performance", p["gaming"].Fans[0].Preset);
        Assert.Equal(LightingEffect.Off, p["night"].Lighting.Effect);
        Assert.Equal(0.4, p["quiet"].Lighting.Brightness);
    }

    [Fact]
    public void A_listed_game_switches_to_gaming_and_back()
    {
        _store.Mutate(s => s.Automation.GameProcesses = ["eldenring.exe"]);
        Run(2);
        Assert.Equal("balanced", Active);
        _running.Add("eldenring");
        Run(2);
        Assert.Equal("gaming", Active);
        Assert.Equal("eldenring is running", _engine.Status.Reason);
        Assert.Contains("performance", _fanModes);
        _running.Clear();
        Run(2);
        Assert.Equal("balanced", Active);
    }

    [Fact]
    public void A_busy_gpu_counts_as_a_game_only_after_30_seconds_and_releases_after_a_minute_idle()
    {
        Run(29, gpuLoad: 95);
        Assert.Equal("balanced", Active);
        Run(2, gpuLoad: 95);
        Assert.Equal("gaming", Active);
        Run(30, gpuLoad: 50);   // in between: stays
        Run(59, gpuLoad: 10);
        Assert.Equal("gaming", Active);
        Run(2, gpuLoad: 10);
        Assert.Equal("balanced", Active);
    }

    [Fact]
    public void Night_window_wraps_past_midnight()
    {
        Assert.True(ProfileEngine.InWindow(At(23, 30), "23:00", "07:00"));
        Assert.True(ProfileEngine.InWindow(At(3, 0), "23:00", "07:00"));
        Assert.False(ProfileEngine.InWindow(At(12, 0), "23:00", "07:00"));
        Assert.True(ProfileEngine.InWindow(At(13, 0), "12:00", "14:00"));
    }

    private static DateTimeOffset At(int h, int m) => new DateTimeOffset(2026, 11, 16, h, m, 0, TimeSpan.Zero).ToLocalTime().Date.AddHours(h).AddMinutes(m);

    [Fact]
    public void A_game_beats_the_night_schedule()
    {
        _store.Mutate(s => { s.Automation.NightEnabled = true; s.Automation.NightStart = "00:00"; s.Automation.NightEnd = "23:59"; s.Automation.GameProcesses = ["game"]; });
        Run(1);
        Assert.Equal("night", Active);
        _running.Add("game");
        Run(1);
        Assert.Equal("gaming", Active);
    }

    [Fact]
    public void A_manual_choice_holds_until_the_situation_changes()
    {
        _store.Mutate(s => s.Automation.GameProcesses = ["game"]);
        _engine.SetManual("quiet");
        Run(10);
        Assert.Equal("quiet", Active);              // automation doesn't fight the user
        Assert.Equal(ProfileSource.Manual, _engine.Status.Source);
        _running.Add("game");
        Run(1);
        Assert.Equal("gaming", Active);             // but a new situation takes over
        _running.Clear();
        Run(1);
        Assert.Equal("balanced", Active);           // and the manual choice is gone, not stuck
    }

    [Fact]
    public void Editing_a_built_in_profile_makes_a_custom_copy_and_leaves_the_original()
    {
        Run(1);
        Assert.Equal("balanced", Active);
        var before = _store.Read(s => s.Profiles.Single(p => p.Id == "balanced").Fans[0].MinPercent);

        _store.Mutate(s => s.Fans[0].MinPercent = 37);

        var custom = _store.Read(s => s.Profiles.Single(p => p.BasedOn == "balanced"));
        Assert.Equal("Balanced (custom)", custom.Name);
        Assert.Equal(37, custom.Fans[0].MinPercent);
        Assert.Equal(custom.Id, Active);
        Assert.Equal(before, _store.Read(s => s.Profiles.Single(p => p.Id == "balanced").Fans[0].MinPercent));
        // It takes over where Balanced was used, so automatic switching doesn't undo the change.
        Assert.Equal(custom.Id, _store.Read(s => s.Automation.DefaultProfileId));
        Run(5);
        Assert.Equal(custom.Id, Active);
        Assert.Equal(37, _store.Read(s => s.Fans[0].MinPercent));
    }

    [Fact]
    public void Later_edits_go_into_the_same_custom_profile()
    {
        Run(1);
        _store.Mutate(s => s.Fans[0].MinPercent = 37);
        _store.Mutate(s => s.Lighting.Brightness = 0.33);
        _store.Mutate(s => s.Fans[0].MinPercent = 41);
        var customs = _store.Read(s => s.Profiles.Where(p => p.BasedOn is not null).ToList());
        Assert.Single(customs);
        Assert.Equal(41, customs[0].Fans[0].MinPercent);
        Assert.Equal(0.33, customs[0].Lighting.Brightness);
        Assert.Equal(5, _store.Read(s => s.Profiles.Count));
    }

    [Fact]
    public void Editing_a_built_in_again_reuses_its_custom_copy()
    {
        Run(1);
        _store.Mutate(s => s.Fans[0].MinPercent = 37);
        _engine.SetManual("balanced");                          // back to the untouched original
        Assert.NotEqual(37, _store.Read(s => s.Fans[0].MinPercent));
        _store.Mutate(s => s.Fans[0].MinPercent = 44);
        var custom = _store.Read(s => s.Profiles.Single(p => p.BasedOn == "balanced"));
        Assert.Equal(44, custom.Fans[0].MinPercent);
        Assert.Equal(custom.Id, Active);
        Assert.Equal(ProfileSource.Manual, _engine.Status.Source);
    }

    [Fact]
    public void Editing_a_profile_you_made_saves_into_it()
    {
        _store.Mutate(s => s.Profiles.Add(new Profile { Id = "mine", Name = "Mine", Fans = ProfileEngine.Copy(s.Fans), Lighting = ProfileEngine.Copy(s.Lighting) }));
        _engine.SetManual("mine");
        _store.Mutate(s => s.Fans[0].MinPercent = 37);
        Assert.Equal(37, _store.Read(s => s.Profiles.Single(p => p.Id == "mine").Fans[0].MinPercent));
        Assert.DoesNotContain(_store.Read(s => s.Profiles), p => p.BasedOn is not null);
    }

    [Fact]
    public void Turning_automation_off_keeps_the_current_profile()
    {
        _store.Mutate(s => { s.Automation.Enabled = false; s.Automation.GameProcesses = ["game"]; });
        _engine.SetManual("quiet");
        _running.Add("game");
        Run(5);
        Assert.Equal("quiet", Active);
    }

    [Fact]
    public void A_manual_choice_survives_a_restart()
    {
        _engine.SetManual("quiet");
        Run(2);
        var restarted = new ProfileEngine(_store, () => _running, () => _now);
        restarted.Evaluate(Gpu(5));
        Assert.Equal("quiet", Active);
        Assert.Equal(ProfileSource.Manual, restarted.Status.Source);
    }

    [Fact]
    public void A_manual_choice_is_forgotten_once_the_situation_changes()
    {
        _store.Mutate(s => s.Automation.GameProcesses = ["game"]);
        _engine.SetManual("quiet");
        _running.Add("game");
        Run(1);
        Assert.Null(_store.Read(s => s.ManualProfile));
        _running.Clear();
        var restarted = new ProfileEngine(_store, () => _running, () => _now);
        restarted.Evaluate(Gpu(5));
        Assert.Equal("balanced", Active);
    }
}
