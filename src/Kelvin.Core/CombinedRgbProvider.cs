namespace Kelvin.Core;

/// <summary>
/// Several lighting backends presented as one, so the rest of Kelvin doesn't care where a device
/// comes from: OpenRGB for most hardware, plus built-in drivers for things OpenRGB can't reach
/// (a laptop keyboard lit through the BIOS, say). Each device is driven by the backend that found it.
/// One backend failing, or having nothing to offer, never affects the others.
/// </summary>
public sealed class CombinedRgbProvider(params IRgbProvider[] parts) : IRgbProvider
{
    public string Name
    {
        get
        {
            var live = parts.Where(p => p.IsConnected).Select(p => p.Name).Distinct().ToList();
            return live.Count > 0 ? string.Join(" + ", live) : parts[0].Name;
        }
    }

    public bool IsSimulated => parts.All(p => p.IsSimulated);
    public bool IsConnected => parts.Any(p => p.IsConnected);
    /// <summary>The main backend's reason for being unavailable, when nothing at all is connected.</summary>
    public string? Status => IsConnected ? null : parts[0].Status;

    public IReadOnlyList<RgbDevice> Devices => parts.Where(p => p.IsConnected).SelectMany(Safe).ToList();

    public void Open()
    {
        foreach (var p in parts) Try(p.Open);
    }

    /// <summary>Refreshes what is connected and keeps trying what isn't.</summary>
    public void Refresh()
    {
        foreach (var p in parts) Try(p.IsConnected ? p.Refresh : p.Open);
    }

    public void SetColors(string deviceId, IReadOnlyList<RgbColor> colors)
    {
        foreach (var p in parts)
        {
            if (!p.IsConnected || Safe(p).All(d => d.Id != deviceId)) continue;
            p.SetColors(deviceId, colors);
            return;
        }
    }

    public void Dispose()
    {
        foreach (var p in parts) Try(p.Dispose);
    }

    private static IReadOnlyList<RgbDevice> Safe(IRgbProvider p)
    {
        try { return p.Devices; } catch (Exception) { return []; }
    }

    private static void Try(Action action)
    {
        try { action(); } catch (Exception) { /* one backend's trouble stays its own */ }
    }
}
