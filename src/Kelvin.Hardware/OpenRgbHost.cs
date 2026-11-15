using System.Diagnostics;
using System.Net.Sockets;

namespace Kelvin.Hardware;

public enum OpenRgbState { NotInstalled, Starting, RunningManaged, RunningExternal, Failed }

/// <summary>
/// Runs OpenRGB for the user, invisibly. Kelvin's service starts it as a headless SDK server
/// (no window, nothing to click), with the administrator rights it needs to reach RAM and
/// motherboard lighting, restarts it if it crashes, and stops it with the service.
/// If the user already runs OpenRGB themselves, Kelvin just uses that one.
/// </summary>
public sealed class OpenRgbHost(int port = 6742) : IDisposable
{
    private static readonly TimeSpan RestartBackoff = TimeSpan.FromSeconds(30);

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

            if (Listening())
            {
                State = OpenRgbState.RunningExternal; // the user's own OpenRGB, with its server on
                return;
            }

            ExecutablePath ??= FindExecutable();
            if (ExecutablePath is null)
            {
                State = OpenRgbState.NotInstalled;
                return;
            }

            if (DateTime.UtcNow - _lastStart < RestartBackoff)
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
            State = OpenRgbState.Starting;
        }
        catch (Exception)
        {
            _process = null;
            State = OpenRgbState.Failed;
        }
    }

    private bool Listening()
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync("127.0.0.1", port).Wait(200) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Looks in the usual install folders, including per-user installs for any user.</summary>
    public static string? FindExecutable()
    {
        var env = Environment.GetEnvironmentVariable("KELVIN_OPENRGB");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;

        var candidates = new List<string>
        {
            // The copy that ships with Kelvin: <install>\openrgb, next to <install>\service.
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "openrgb", "OpenRGB.exe")),
            // Otherwise a separate OpenRGB install.
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenRGB", "OpenRGB.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "OpenRGB", "OpenRGB.exe"),
        };
        try
        {
            // The service runs as LocalSystem, so per-user installs live under other profiles.
            var users = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "Users");
            foreach (var profile in Directory.EnumerateDirectories(users))
            {
                candidates.Add(Path.Combine(profile, "AppData", "Local", "Programs", "OpenRGB", "OpenRGB.exe"));
                candidates.Add(Path.Combine(profile, "AppData", "Local", "OpenRGB", "OpenRGB.exe"));
            }
        }
        catch { /* profiles folder unreadable: skip */ }

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
        }
    }
}
