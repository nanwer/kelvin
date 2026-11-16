using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kelvin.Core;
using MQTTnet;

namespace Kelvin.Service;

/// <summary>What Kelvin shares with Home Assistant.</summary>
public sealed record HaState(
    double? CpuTemperature,
    double? GpuTemperature,
    double? BoardTemperature,
    double? CpuLoad,
    double? GpuLoad,
    IReadOnlyList<(string Name, double? Rpm)> Fans,
    IReadOnlyList<(string Id, string Name)> Profiles,
    string? ActiveProfileId,
    bool Automatic,
    bool SafetyOverride,
    bool AlertActive,
    string? FanMode);

/// <summary>
/// Publishes Kelvin to an MQTT broker using Home Assistant's discovery, so it appears as a device with
/// temperature and fan sensors, a profile picker and an "automatic profiles" switch. It only accepts
/// two kinds of commands: switch profile, and turn automatic switching on or off. It never takes
/// direct fan-speed commands, so a compromised broker can't stop your fans.
/// </summary>
public sealed class HomeAssistantBridge(
    Func<MqttSettings> settings,
    Func<HaState> state,
    Action<string> setProfile,
    Action<bool> setAutomatic,
    string machineName,
    string version) : IAsyncDisposable
{
    private static readonly TimeSpan PublishEvery = TimeSpan.FromSeconds(10);
    private readonly MqttClientFactory _factory = new();
    private IMqttClient? _client;
    private string _connectedWith = "";
    private string _discoveryKey = "";

    public string Node { get; } = Slug(machineName);
    public string Status { get; private set; } = "Off";
    public bool Connected => _client?.IsConnected == true;

    private string Base(MqttSettings s) => $"{s.BaseTopic.Trim('/')}/{Node}";

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var s = settings();
            try
            {
                if (!s.Enabled || string.IsNullOrWhiteSpace(s.Host))
                {
                    await DisconnectAsync();
                    Status = s.Enabled ? "Enter your broker's address." : "Off";
                }
                else
                {
                    var key = JsonSerializer.Serialize(s);
                    if (!Connected || key != _connectedWith) await ConnectAsync(s, key, ct);
                    await PublishAsync(s, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Status = $"Can't connect: {ex.Message}";
                await DisconnectAsync();
                await Task.Delay(TimeSpan.FromSeconds(20), ct).ContinueWith(_ => { });
                continue;
            }
            await Task.Delay(PublishEvery, ct).ContinueWith(_ => { });
        }
        await DisconnectAsync();
    }

    private async Task ConnectAsync(MqttSettings s, string key, CancellationToken ct)
    {
        await DisconnectAsync();
        Status = "Connecting…";
        var b = new MqttClientOptionsBuilder()
            .WithTcpServer(s.Host.Trim(), s.Port)
            .WithClientId($"kelvin-{Node}")
            .WithCleanSession()
            .WithWillTopic($"{Base(s)}/availability")
            .WithWillPayload("offline")
            .WithWillRetain();
        if (!string.IsNullOrEmpty(s.Username)) b = b.WithCredentials(s.Username, Unprotect(s.PasswordProtected));
        if (s.UseTls) b = b.WithTlsOptions(o => o.UseTls());

        var client = _factory.CreateMqttClient();
        client.ApplicationMessageReceivedAsync += e =>
        {
            HandleCommand(settings(), e.ApplicationMessage.Topic, e.ApplicationMessage.ConvertPayloadToString() ?? "");
            return Task.CompletedTask;
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(b.Build(), timeout.Token);
        _client = client;
        _connectedWith = key;
        _discoveryKey = "";

        if (s.AllowProfileControl)
        {
            await client.SubscribeAsync(_factory.CreateSubscribeOptionsBuilder()
                .WithTopicFilter($"{Base(s)}/profile/set")
                .WithTopicFilter($"{Base(s)}/automatic/set")
                .Build(), ct);
        }
        await Send($"{Base(s)}/availability", "online", retain: true, ct);
        Status = $"Connected to {s.Host}";
    }

    private async Task PublishAsync(MqttSettings s, CancellationToken ct)
    {
        var st = state();
        // Discovery is retained, so it's re-sent only when something it describes changes.
        var discovery = Discovery(s, st);
        var key = JsonSerializer.Serialize(discovery.Select(d => d.Payload.ToJsonString()));
        if (key != _discoveryKey)
        {
            foreach (var (topic, payload) in discovery) await Send(topic, payload.ToJsonString(), retain: true, ct);
            _discoveryKey = key;
        }
        await Send($"{Base(s)}/state", StatePayload(st).ToJsonString(), retain: false, ct);
    }

    public static JsonObject StatePayload(HaState st)
    {
        var o = new JsonObject
        {
            ["cpu_temperature"] = Round(st.CpuTemperature),
            ["gpu_temperature"] = Round(st.GpuTemperature),
            ["board_temperature"] = Round(st.BoardTemperature),
            ["cpu_load"] = Round(st.CpuLoad),
            ["gpu_load"] = Round(st.GpuLoad),
            ["profile"] = st.Profiles.FirstOrDefault(p => p.Id == st.ActiveProfileId).Name,
            ["automatic"] = st.Automatic ? "ON" : "OFF",
            ["safety_override"] = st.SafetyOverride ? "ON" : "OFF",
            ["alert"] = st.AlertActive ? "ON" : "OFF",
            ["fan_mode"] = st.FanMode,
        };
        foreach (var (name, rpm) in st.Fans) o[$"fan_{Slug(name)}"] = rpm is { } r ? Math.Round(r) : null;
        return o;
    }

    /// <summary>Home Assistant MQTT discovery messages: one retained config topic per entity.</summary>
    public List<(string Topic, JsonObject Payload)> Discovery(MqttSettings s, HaState st)
    {
        var device = new JsonObject
        {
            ["identifiers"] = new JsonArray($"kelvin_{Node}"),
            ["name"] = $"Kelvin ({machineName})",
            ["manufacturer"] = "Kelvin",
            ["model"] = "Fan and lighting controller",
            ["sw_version"] = version,
        };
        var stateTopic = $"{Base(s)}/state";
        var list = new List<(string, JsonObject)>();

        void Add(string component, string id, string name, Action<JsonObject> extra)
        {
            var o = new JsonObject
            {
                ["name"] = name,
                ["unique_id"] = $"kelvin_{Node}_{id}",
                ["object_id"] = $"kelvin_{Node}_{id}",
                ["availability_topic"] = $"{Base(s)}/availability",
                ["device"] = device.DeepClone(),
            };
            extra(o);
            list.Add(($"{s.DiscoveryPrefix.Trim('/')}/{component}/kelvin_{Node}/{id}/config", o));
        }

        void Temperature(string id, string name, double? value)
        {
            if (value is null) return;
            Add("sensor", id, name, o =>
            {
                o["state_topic"] = stateTopic;
                o["value_template"] = $"{{{{ value_json.{id} }}}}";
                o["device_class"] = "temperature";
                o["unit_of_measurement"] = "°C";
                o["state_class"] = "measurement";
            });
        }

        Temperature("cpu_temperature", "CPU temperature", st.CpuTemperature);
        Temperature("gpu_temperature", "GPU temperature", st.GpuTemperature);
        Temperature("board_temperature", "Motherboard temperature", st.BoardTemperature);
        foreach (var (id, name, value) in new[] { ("cpu_load", "CPU load", st.CpuLoad), ("gpu_load", "GPU load", st.GpuLoad) })
        {
            if (value is null) continue;
            Add("sensor", id, name, o =>
            {
                o["state_topic"] = stateTopic; o["value_template"] = $"{{{{ value_json.{id} }}}}";
                o["unit_of_measurement"] = "%"; o["state_class"] = "measurement"; o["icon"] = "mdi:gauge";
            });
        }
        foreach (var (fanName, _) in st.Fans)
        {
            var id = $"fan_{Slug(fanName)}";
            Add("sensor", id, fanName, o =>
            {
                o["state_topic"] = stateTopic; o["value_template"] = $"{{{{ value_json.{id} }}}}";
                o["unit_of_measurement"] = "RPM"; o["state_class"] = "measurement"; o["icon"] = "mdi:fan";
            });
        }

        if (s.AllowProfileControl)
        {
            Add("select", "profile", "Profile", o =>
            {
                o["state_topic"] = stateTopic; o["value_template"] = "{{ value_json.profile }}";
                o["command_topic"] = $"{Base(s)}/profile/set";
                o["options"] = new JsonArray(st.Profiles.Select(p => (JsonNode)JsonValue.Create(p.Name)!).ToArray());
                o["icon"] = "mdi:layers";
            });
            Add("switch", "automatic", "Automatic profiles", o =>
            {
                o["state_topic"] = stateTopic; o["value_template"] = "{{ value_json.automatic }}";
                o["command_topic"] = $"{Base(s)}/automatic/set";
                o["icon"] = "mdi:auto-mode";
            });
        }
        else
        {
            Add("sensor", "profile", "Profile", o =>
            {
                o["state_topic"] = stateTopic; o["value_template"] = "{{ value_json.profile }}"; o["icon"] = "mdi:layers";
            });
        }

        Add("binary_sensor", "safety_override", "Safety override", o =>
        {
            o["state_topic"] = stateTopic; o["value_template"] = "{{ value_json.safety_override }}"; o["device_class"] = "problem";
        });
        Add("binary_sensor", "alert", "Temperature alert", o =>
        {
            o["state_topic"] = stateTopic; o["value_template"] = "{{ value_json.alert }}"; o["device_class"] = "heat";
        });
        if (st.FanMode is not null)
        {
            Add("sensor", "fan_mode", "Laptop fan mode", o =>
            {
                o["state_topic"] = stateTopic; o["value_template"] = "{{ value_json.fan_mode }}"; o["icon"] = "mdi:fan-auto";
            });
        }
        return list;
    }

    /// <summary>Only two commands exist. Anything else, including unknown profiles, is ignored.</summary>
    public void HandleCommand(MqttSettings s, string topic, string payload)
    {
        if (!s.AllowProfileControl) return;
        payload = payload.Trim();
        if (topic == $"{Base(s)}/profile/set")
        {
            // Home Assistant sends the option label (the profile name); ids work too.
            var match = state().Profiles.FirstOrDefault(p =>
                p.Name.Equals(payload, StringComparison.OrdinalIgnoreCase) || p.Id.Equals(payload, StringComparison.OrdinalIgnoreCase));
            if (match.Id is not null) setProfile(match.Id);
        }
        else if (topic == $"{Base(s)}/automatic/set")
        {
            if (payload.Equals("ON", StringComparison.OrdinalIgnoreCase)) setAutomatic(true);
            else if (payload.Equals("OFF", StringComparison.OrdinalIgnoreCase)) setAutomatic(false);
        }
    }

    private async Task Send(string topic, string payload, bool retain, CancellationToken ct)
    {
        if (_client is not { IsConnected: true } c) return;
        await c.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload).WithRetainFlag(retain).Build(), ct);
    }

    private async Task DisconnectAsync()
    {
        var c = _client;
        _client = null;
        _connectedWith = "";
        if (c is null) return;
        try
        {
            if (c.IsConnected)
            {
                var s = settings();
                await c.PublishAsync(new MqttApplicationMessageBuilder().WithTopic($"{Base(s)}/availability").WithPayload("offline").WithRetainFlag().Build());
                await c.DisconnectAsync();
            }
        }
        catch { /* going away anyway */ }
        finally { c.Dispose(); }
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();

    // ---- helpers

    public static string Slug(string s)
    {
        var chars = s.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
        var slug = new string(chars).Trim('_');
        while (slug.Contains("__")) slug = slug.Replace("__", "_");
        return slug.Length == 0 ? "pc" : slug;
    }

    private static JsonNode? Round(double? v) => v is { } x && double.IsFinite(x) ? JsonValue.Create(Math.Round(x, 1)) : null;

    /// <summary>Encrypts the broker password for the account Kelvin runs as (Windows DPAPI).</summary>
    public static string Protect(string password) =>
        string.IsNullOrEmpty(password) ? "" :
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser));

    public static string Unprotect(string protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return "";
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), null, DataProtectionScope.CurrentUser)); }
        catch { return ""; } // e.g. settings copied from another PC: ask for the password again
    }
}
