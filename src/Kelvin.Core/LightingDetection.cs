namespace Kelvin.Core;

public enum DetectionAction { Wait, Ok, Restart, Forget }

/// <summary>
/// OpenRGB sometimes stalls partway through finding devices, and then simply serves the ones it
/// found. Kelvin remembers which lighting devices it has seen before. When fewer show up after
/// things settle, it restarts OpenRGB once. If they still don't appear, they're probably
/// unplugged or switched off, so it stops expecting them (they come back when they reappear).
/// </summary>
public sealed class LightingDetectionWatch(TimeSpan? settle = null, int maxRestarts = 1)
{
    private readonly TimeSpan _settle = settle ?? TimeSpan.FromSeconds(60);
    private DateTimeOffset? _stableSince;
    private int _lastCount = -1;
    private int _restarts;

    public int Restarts => _restarts;

    /// <param name="known">Device names seen before (a name appears once per device).</param>
    /// <param name="current">Device names OpenRGB serves now.</param>
    /// <param name="connected">Whether Kelvin is talking to the OpenRGB it started.</param>
    public DetectionAction Check(IReadOnlyList<string> known, IReadOnlyList<string> current, bool connected, DateTimeOffset now)
    {
        if (!connected) { _stableSince = null; _lastCount = -1; return DetectionAction.Wait; }
        if (current.Count != _lastCount) { _lastCount = current.Count; _stableSince = now; }
        if (now - _stableSince < _settle) return DetectionAction.Wait;

        if (Missing(known, current).Count == 0) { _restarts = 0; return DetectionAction.Ok; }
        if (_restarts < maxRestarts)
        {
            _restarts++;
            _stableSince = null;
            _lastCount = -1;
            return DetectionAction.Restart;
        }
        _restarts = 0;
        return DetectionAction.Forget;
    }

    /// <summary>Names in <paramref name="known"/> not matched one-for-one in <paramref name="current"/>.</summary>
    public static List<string> Missing(IReadOnlyList<string> known, IReadOnlyList<string> current)
    {
        var left = current.GroupBy(n => n).ToDictionary(g => g.Key, g => g.Count());
        var missing = new List<string>();
        foreach (var name in known)
        {
            if (left.TryGetValue(name, out var n) && n > 0) left[name] = n - 1;
            else missing.Add(name);
        }
        return missing;
    }

    /// <summary>Everything in either list, keeping the larger count of each name.</summary>
    public static List<string> Merge(IReadOnlyList<string> known, IReadOnlyList<string> current) =>
        [.. known, .. Missing(current, known)];
}
