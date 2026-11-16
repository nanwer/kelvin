using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Kelvin.Core;
using Kelvin.Service;
using MQTTnet;
using MQTTnet.Server;

namespace Kelvin.Core.Tests;

public class HomeAssistantTests
{
    private static HaState State(string active = "quiet") => new(
        61.2, 48.0, 35.5, 22, 7,
        [("CPU Fan", 1180), ("Chassis Fan", 940)],
        [("quiet", "Quiet"), ("gaming", "Gaming")],
        active, true, false, false, null);

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(100);
        Assert.True(condition(), $"Timed out waiting for: {what}");
    }

    [Fact]
    public async Task Talks_to_a_real_broker_and_only_accepts_profile_commands()
    {
        var port = FreePort();
        var factory = new MqttServerFactory();
        using var server = factory.CreateMqttServer(factory.CreateServerOptionsBuilder().WithDefaultEndpoint().WithDefaultEndpointPort(port).Build());
        var published = new ConcurrentDictionary<string, string>();
        server.InterceptingPublishAsync += e =>
        {
            published[e.ApplicationMessage.Topic] = e.ApplicationMessage.ConvertPayloadToString() ?? "";
            return Task.CompletedTask;
        };
        await server.StartAsync();

        var settings = new MqttSettings { Enabled = true, Host = "127.0.0.1", Port = port };
        var profileSet = new List<string>();
        var automatic = new List<bool>();
        var bridge = new HomeAssistantBridge(() => settings, () => State(), profileSet.Add, automatic.Add, "Test PC", "0.3.0");
        using var cts = new CancellationTokenSource();
        var run = bridge.RunAsync(cts.Token);

        var b = $"kelvin/{bridge.Node}";
        await Until(() => published.ContainsKey($"{b}/state"), "state");
        Assert.Equal("online", published[$"{b}/availability"]);

        // Discovery: a proper temperature sensor, a profile picker and the automatic switch.
        var cpu = JsonNode.Parse(published[$"homeassistant/sensor/kelvin_{bridge.Node}/cpu_temperature/config"])!;
        Assert.Equal("temperature", (string?)cpu["device_class"]);
        Assert.Equal("°C", (string?)cpu["unit_of_measurement"]);
        Assert.Equal("Kelvin (Test PC)", (string?)cpu["device"]!["name"]);
        var select = JsonNode.Parse(published[$"homeassistant/select/kelvin_{bridge.Node}/profile/config"])!;
        Assert.Equal(["Quiet", "Gaming"], select["options"]!.AsArray().Select(n => (string)n!));
        Assert.True(published.ContainsKey($"homeassistant/switch/kelvin_{bridge.Node}/automatic/config"));

        var state = JsonNode.Parse(published[$"{b}/state"])!;
        Assert.Equal(61.2, (double)state["cpu_temperature"]!);
        Assert.Equal("Quiet", (string?)state["profile"]);
        Assert.Equal(1180, (double)state["fan_cpu_fan"]!);

        async Task Send(string topic, string payload) =>
            await server.InjectApplicationMessage(new InjectedMqttApplicationMessage(
                new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload).Build()) { SenderClientId = "homeassistant" });

        await Send($"{b}/profile/set", "Gaming");
        await Until(() => profileSet.Count == 1, "profile command");
        Assert.Equal("gaming", profileSet[0]);

        await Send($"{b}/profile/set", "Not a profile");      // ignored
        await Send($"{b}/fan/set", "0");                        // there's no such command: ignored
        await Send($"{b}/automatic/set", "OFF");
        await Until(() => automatic.Count == 1, "automatic command");
        Assert.Equal([false], automatic);
        Assert.Equal(["gaming"], profileSet);

        cts.Cancel();
        await run;
        await Until(() => published.GetValueOrDefault($"{b}/availability") == "offline", "offline on stop");
        await server.StopAsync();
    }

    [Fact]
    public void Profile_control_can_be_turned_off()
    {
        var settings = new MqttSettings { AllowProfileControl = false };
        var calls = 0;
        var bridge = new HomeAssistantBridge(() => settings, () => State(), _ => calls++, _ => calls++, "PC", "0.3.0");
        bridge.HandleCommand(settings, $"kelvin/{bridge.Node}/profile/set", "Gaming");
        Assert.Equal(0, calls);
        Assert.DoesNotContain(bridge.Discovery(settings, State()), d => d.Topic.Contains("/select/"));
    }

    [Fact]
    public void Password_is_encrypted_at_rest()
    {
        var stored = HomeAssistantBridge.Protect("hunter2");
        Assert.DoesNotContain("hunter2", stored);
        Assert.Equal("hunter2", HomeAssistantBridge.Unprotect(stored));
        Assert.Equal("", HomeAssistantBridge.Unprotect("not-valid"));
    }

    [Theory]
    [InlineData("DESKTOP-ABC 12", "desktop_abc_12")]
    [InlineData("  ", "pc")]
    public void Machine_names_become_safe_topic_parts(string name, string slug) =>
        Assert.Equal(slug, HomeAssistantBridge.Slug(name));
}
