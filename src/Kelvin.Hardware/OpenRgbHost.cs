using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;

namespace Kelvin.Hardware;

public enum OpenRgbState { NotInstalled, Starting, RunningManaged, RunningExternal, Failed }

/// <summary>
/// Runs OpenRGB for the user, invisibly. Kelvin's service starts it as a headless SDK server
/// (no window, nothing to click), with the administrator rights it needs to reach RAM and
/// motherboard lighting, restarts it if it crashes, and stops it with the service.
/// If the user already runs OpenRGB themselves, Kelvin just uses that one.
/// <para>The OpenRGB Kelvin starts can never outlive the service: Windows ends it with the service
/// even after a crash, and a copy left over from before that existed is closed on sight. A leftover
/// would hold its files open and block the next Kelvin update.</para>
/// </summary>
public sealed class OpenRgbHost(int port = 6742) : IDisposable
{
    private static readonly TimeSpan RestartBackoff = TimeSpan.FromSeconds(30);

    private readonly ChildJob _job = new();
    private Process? _process;
    private DateTime _lastStart = DateTime.MinValue;
    private readonly object _gate = new();

    public OpenRgbState State { get; private set; } = OpenRgbState.Starting;
    public string? ExecutablePath { get; private set; }

    /// <summary>Called every few seconds by the service. Cheap when everything is already fine.</summary>
    public void EnsureRunning()
    {
        lock (_gate)
        {
            if (_process is { HasExited: false })
            {
                State = Listening() ? OpenRgbState.RunningManaged : OpenRgbState.Starting;
                return;
            }

            ExecutablePath ??= FindExecutable();

            // Kelvin's own OpenRGB running without Kelvin having started it is a leftover from a
            // service that died. Nothing manages it, so close it and start a fresh one.
            var leftovers = CloseLeftovers();

            if (leftovers == 0 && Listening())
            {
                State = OpenRgbState.RunningExternal; // the user's own OpenRGB, with its server on
                return;
            }

            if (ExecutablePath is null)
            {
                State = OpenRgbState.NotInstalled;
                return;
            }

            if (leftovers == 0 && DateTime.UtcNow - _lastStart < RestartBackoff)
            {
                State = _process is null ? OpenRgbState.Starting : OpenRgbState.Failed;
                return;
            }

            Start();
        }
    }

    private void Start()
    {
        _lastStart = DateTime.UtcNow;
        // Keep OpenRGB's own settings next to Kelvin's, not in the system account's profile.
        var config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Kelvin", "openrgb");
        Directory.CreateDirectory(config);
        try
        {
            _process?.Dispose();
            _process = Process.Start(new ProcessStartInfo
            {
                FileName = ExecutablePath!,
                // --server without --gui runs OpenRGB headless: detect devices, serve the SDK.
                ArgumentList = { "--server", "--server-port", port.ToString(), "--config", config },
                WorkingDirectory = Path.GetDirectoryName(ExecutablePath!)!,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (_process is not null) _job.Add(_process);
            State = OpenRgbState.Starting;
        }
        catch (Exception)
        {
            _process = null;
            State = OpenRgbState.Failed;
        }
    }

    /// <summary>
    /// Whether something is listening on the SDK port. Reads the system's listener table instead of
    /// connecting: a bare connection makes OpenRGB log an error every time.
    /// </summary>
    private bool Listening()
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
                .Any(e => e.Port == port && (IPAddress.IsLoopback(e.Address) || e.Address.Equals(IPAddress.Any) || e.Address.Equals(IPAddress.IPv6Any)));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Closes every running copy of the OpenRGB that ships with Kelvin, other than the one this host
    /// started. Returns how many it closed. The user's own OpenRGB install is never touched.
    /// </summary>
    private int CloseLeftovers()
    {
        var executable = BundledPath;
        if (!File.Exists(executable)) return 0;
        var closed = 0;
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
        {
            try
            {
                if (_process is { HasExited: false } && p.Id == _process.Id) continue;
                if (!string.Equals(p.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase)) continue;
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
                closed++;
            }
            catch { /* already gone, or not ours to inspect */ }
            finally { p.Dispose(); }
        }
        return closed;
    }

    /// <summary>True when Kelvin started the OpenRGB that's running, so it may restart it.</summary>
    public bool IsManaged
    {
        get { lock (_gate) return _process is { HasExited: false }; }
    }

    /// <summary>Restarts the OpenRGB Kelvin started, e.g. when device detection stalled.</summary>
    public void Restart()
    {
        lock (_gate)
        {
            if (_process is not { HasExited: false }) return;
            try { _process.Kill(entireProcessTree: true); _process.WaitForExit(5000); } catch { }
            Start();
        }
    }

    /// <summary>The copy that ships with Kelvin: &lt;install&gt;\openrgb, next to &lt;install&gt;\service.</summary>
    public static string BundledPath { get; } =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "openrgb", "OpenRGB.exe"));

    /// <summary>Looks in Kelvin's own folder first, then the system-wide OpenRGB install folders.</summary>
    public static string? FindExecutable()
    {
        var env = Environment.GetEnvironmentVariable("KELVIN_OPENRGB");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;

        // Only folders that take an administrator to write to. Kelvin starts this program with
        // system rights, so it never looks in a user's own profile, where anyone could put a
        // file called OpenRGB.exe.
        string[] candidates =
        [
            BundledPath,
            // Otherwise a separate OpenRGB install.
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenRGB", "OpenRGB.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "OpenRGB", "OpenRGB.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_process is { HasExited: false })
            {
                try { _process.Kill(entireProcessTree: true); _process.WaitForExit(3000); } catch { }
            }
            _process?.Dispose();
            _process = null;
            _job.Dispose();
        }
    }
}
