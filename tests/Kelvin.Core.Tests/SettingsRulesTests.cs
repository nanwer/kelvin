using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using Kelvin.Core;
using Kelvin.Hardware;

namespace Kelvin.Core.Tests;

/// <summary>
/// Settings keep to the rules whatever wrote them: a request the routes didn't anticipate, a
/// hand-edited file, or another program on the PC.
/// </summary>
public class SettingsRulesTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"kelvin-rules-{Guid.NewGuid():N}", "settings.json");

    [Fact]
    public void A_safety_limit_that_is_not_a_number_falls_back_to_the_default()
    {
        var store = new SettingsStore(TempPath());
        store.Mutate(s => s.Safety = new SafetySettings { CriticalCpuTemp = double.NaN, CriticalGpuTemp = double.PositiveInfinity });
        Assert.Equal(90, store.Current.Safety.CriticalCpuTemp);
        Assert.Equal(95, store.Current.Safety.CriticalGpuTemp);
    }

    [Fact]
    public void A_change_with_a_bad_number_still_saves()
    {
        var path = TempPath();
        var store = new SettingsStore(path);
        // This used to throw after the change had already taken effect in memory.
        store.Mutate(s => s.Lighting.Brightness = double.NaN);
        Assert.Equal(0.8, new SettingsStore(path).Current.Lighting.Brightness);
    }

    [Fact]
    public void A_settings_file_gets_the_same_limits_as_the_api()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            {
              "safety": { "criticalCpuTemp": 1000000000, "criticalGpuTemp": 5 },
              "fans": [ null, { "fanId": "a", "mode": "fixed", "fixedPercent": 500, "minPercent": 99, "smoothSeconds": -4 },
                              { "fanId": "b", "mode": "curve", "points": [ { "temp": 50, "percent": 40 } ] } ],
              "lighting": { "color": "not a color", "brightness": 7, "excludedDeviceIds": null, "devices": { "x": null } },
              "profiles": [ null, { "id": "p", "name": "P", "fans": [ null ], "lighting": null } ],
              "dismissedRecommendations": null
            }
            """);
        var s = new SettingsStore(path).Current;

        Assert.Equal(95, s.Safety.CriticalCpuTemp);
        Assert.Equal(70, s.Safety.CriticalGpuTemp);
        Assert.Equal(2, s.Fans.Count);
        Assert.Equal((100, 60, 0), (s.Fans[0].FixedPercent, s.Fans[0].MinPercent, s.Fans[0].SmoothSeconds));
        Assert.Equal(FanMode.Bios, s.Fans[1].Mode);          // a one-point curve can't be used: the BIOS keeps the fan
        Assert.Equal("#ff8a3d", s.Lighting.Color);
        Assert.Equal(1, s.Lighting.Brightness);
        Assert.Empty(s.Lighting.ExcludedDeviceIds);
        Assert.Empty(s.Lighting.Devices);
        var profile = Assert.Single(s.Profiles);
        Assert.Empty(profile.Fans);
        Assert.NotNull(profile.Lighting);
        Assert.Empty(s.DismissedRecommendations);
    }

    [Fact]
    public void A_missing_list_in_a_change_becomes_an_empty_one()
    {
        var path = TempPath();
        var store = new SettingsStore(path);
        store.Mutate(s => s.Lighting = new LightingSettings { ExcludedDeviceIds = null!, Devices = null! });
        Assert.Empty(store.Current.Lighting.ExcludedDeviceIds);
        Assert.Empty(new SettingsStore(path).Current.Lighting.ExcludedDeviceIds);   // and it stays fixed after a restart
    }

    [Fact]
    public void Saving_never_reuses_a_file_that_was_waiting_under_the_old_temporary_name()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var planted = path + ".tmp";
        File.WriteAllText(planted, "planted");

        new SettingsStore(path).Mutate(s => s.Safety.CriticalCpuTemp = 80);

        Assert.Equal("planted", File.ReadAllText(planted));   // untouched, so it never became settings.json
        Assert.Contains("\"criticalCpuTemp\": 80", File.ReadAllText(path));
        Assert.Equal(2, Directory.GetFiles(Path.GetDirectoryName(path)!).Length);   // no temporary files left behind
    }

    [Fact]
    public void The_saved_password_does_not_follow_the_broker_to_a_new_address()
    {
        var m = new MqttSettings { Host = "ha.local", Port = 1883, UseTls = true, Username = "kelvin" };
        Assert.False(m.SendsPasswordElsewhere("HA.local", 1883, true, "kelvin"));
        Assert.True(m.SendsPasswordElsewhere("127.0.0.1", 1883, true, "kelvin"));
        Assert.True(m.SendsPasswordElsewhere("ha.local", 8883, true, "kelvin"));
        Assert.True(m.SendsPasswordElsewhere("ha.local", 1883, false, "kelvin"));   // same broker, but no longer encrypted
        Assert.True(m.SendsPasswordElsewhere("ha.local", 1883, true, "someone"));
        m.UseTls = false;
        Assert.False(m.SendsPasswordElsewhere("ha.local", 1883, true, "kelvin"));   // turning encryption on is fine
    }

    [Fact]
    public void The_settings_folder_is_readable_by_everyone_and_writable_only_by_the_system_and_administrators()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = Path.Combine(Path.GetTempPath(), $"kelvin-folder-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "openrgb"));
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{}");
        try
        {
            Assert.True(MachineFolder.Secure(dir));

            var security = new DirectoryInfo(dir).GetAccessControl();
            Assert.True(security.AreAccessRulesProtected);   // nothing is inherited from the parent any more
            var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
            var writers = rules.Where(r => r.AccessControlType == AccessControlType.Allow &&
                    (r.FileSystemRights & (FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete | FileSystemRights.ChangePermissions)) != 0)
                .Select(r => ((SecurityIdentifier)r.IdentityReference).Value).ToHashSet();
            Assert.Equal(new HashSet<string> { "S-1-5-18", "S-1-5-32-544" }, writers);
            Assert.Contains(rules, r => ((SecurityIdentifier)r.IdentityReference).Value == "S-1-5-32-545" && r.FileSystemRights.HasFlag(FileSystemRights.Read));

            // What was already inside takes the folder's permissions and nothing else.
            var file = new FileInfo(Path.Combine(dir, "settings.json")).GetAccessControl();
            Assert.All(file.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(), r => Assert.True(r.IsInherited));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* an administrator-only folder may not be ours to delete */ }
        }
    }

    [Fact]
    public void The_service_can_tell_which_program_is_calling()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var servicePort = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new TcpClient();
        client.Connect(IPAddress.Loopback, servicePort);
        using var accepted = listener.AcceptTcpClient();
        var callerPort = ((IPEndPoint)client.Client.LocalEndPoint!).Port;

        var verdict = LocalCaller.Check(callerPort, servicePort);
        Assert.Equal(Environment.ProcessId, verdict.ProcessId);   // it found this test process
        Assert.True(verdict.Allowed, verdict.Reason);             // an ordinary program run by the person at the PC

        // A port nothing is connected from: unknown callers are refused, not waved through.
        Assert.False(LocalCaller.Check(1, servicePort).Allowed);
    }
}

/// <summary>A graphics driver update must not be able to crash the service (see AmdGpuGuard).</summary>
public class AmdGpuGuardTests
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr VirtualAlloc(IntPtr address, nuint size, uint type, uint protect);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool VirtualFree(IntPtr address, nuint size, uint type);

    [Fact]
    public void The_sensor_library_still_has_the_fields_the_guard_reads()
    {
        // If this fails after updating LibreHardwareMonitorLib, the guard has silently switched
        // itself off: find the new names of the shared-memory fields in its AmdGpu class.
        Assert.True(AmdGpuGuard.Available);
    }

    [Fact]
    public void Memory_that_has_gone_away_is_reported_as_unreadable()
    {
        if (!OperatingSystem.IsWindows()) return;
        const int size = 3 * 4096;
        var memory = VirtualAlloc(IntPtr.Zero, size, 0x3000 /* commit and reserve */, 0x04 /* read-write */);
        Assert.NotEqual(IntPtr.Zero, memory);
        Assert.True(AmdGpuGuard.Readable(memory, size));
        Assert.False(AmdGpuGuard.Readable(memory, size + 1 << 20));   // runs past the end of the block

        VirtualFree(memory, 0, 0x8000 /* release */);
        Assert.False(AmdGpuGuard.Readable(memory, size));            // what a driver reset leaves behind
        Assert.False(AmdGpuGuard.Readable(IntPtr.Zero, 16));
    }
}
