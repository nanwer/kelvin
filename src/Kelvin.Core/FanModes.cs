namespace Kelvin.Core;

public sealed record FanModeOption(string Id, string Label, string Description);

/// <summary>
/// Whole-system fan modes, for PCs (mostly laptops) whose fans Kelvin can't drive one by one.
/// Implementations: Windows power modes (any laptop), HP Omen, ASUS, Lenovo Legion, Simulated.
/// Standard ids: "quiet", "balanced", "performance", and optionally "max".
/// </summary>
public interface IFanModeProvider
{
    /// <summary>Shown to the user, e.g. "HP Omen fan modes".</summary>
    string Name { get; }
    /// <summary>Not yet confirmed on real hardware by anyone. The UI says so.</summary>
    bool Experimental { get; }
    IReadOnlyList<FanModeOption> Modes { get; }
    /// <summary>The mode now in effect, if the hardware can report it.</summary>
    string? Current { get; }
    void Set(string id);
}

public static class FanModeIds
{
    public const string Quiet = "quiet", Balanced = "balanced", Performance = "performance", Max = "max";

    public static FanModeOption Option(string id) => id switch
    {
        Quiet => new(id, "Quiet", "Slower fans and lower power for a quieter laptop."),
        Balanced => new(id, "Balanced", "The laptop's normal behaviour."),
        Performance => new(id, "Performance", "Higher power limits and fans that ramp sooner."),
        Max => new(id, "Max fan", "Every fan at full speed, regardless of temperature."),
        _ => new(id, id, ""),
    };
}

/// <summary>Picks the first provider that works, so a laptop gets its vendor's modes when available.</summary>
public sealed class CombinedFanModes(IFanModeProvider primary, IFanModeProvider? extra) : IFanModeProvider
{
    // e.g. Windows power modes for quiet/balanced/performance, plus HP's "Max fan".
    public string Name => extra is null ? primary.Name : $"{primary.Name} + {extra.Name}";
    public bool Experimental => primary.Experimental || (extra?.Experimental ?? false);
    public IReadOnlyList<FanModeOption> Modes => [.. primary.Modes, .. extra?.Modes.Where(m => primary.Modes.All(p => p.Id != m.Id)) ?? []];
    public string? Current => extra?.Current == FanModeIds.Max ? FanModeIds.Max : primary.Current;

    public void Set(string id)
    {
        if (extra is not null && extra.Modes.Any(m => m.Id == id) && primary.Modes.All(m => m.Id != id))
        {
            extra.Set(id);
            return;
        }
        // Leaving "Max fan" for a normal mode switches max off first.
        if (extra?.Current == FanModeIds.Max) extra.Set(FanModeIds.Balanced);
        primary.Set(id);
    }
}

public sealed class SimulatedFanModes : IFanModeProvider
{
    public string Name => "Simulated laptop";
    public bool Experimental => false;
    public IReadOnlyList<FanModeOption> Modes { get; } =
        [FanModeIds.Option(FanModeIds.Quiet), FanModeIds.Option(FanModeIds.Balanced), FanModeIds.Option(FanModeIds.Performance), FanModeIds.Option(FanModeIds.Max)];
    public string? Current { get; private set; } = FanModeIds.Balanced;
    public void Set(string id) => Current = Modes.Any(m => m.Id == id) ? id : throw new ArgumentException($"Unknown mode {id}");
}
