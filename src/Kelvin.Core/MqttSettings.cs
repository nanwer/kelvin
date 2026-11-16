namespace Kelvin.Core;

/// <summary>Connection to an MQTT broker, for Home Assistant and other smart-home tools.</summary>
public sealed class MqttSettings
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 1883;
    public bool UseTls { get; set; }
    public string Username { get; set; } = "";
    /// <summary>Encrypted with Windows DPAPI for the account the service runs as. Never sent to the UI.</summary>
    public string PasswordProtected { get; set; } = "";
    /// <summary>Topics are &lt;BaseTopic&gt;/&lt;device&gt;/…</summary>
    public string BaseTopic { get; set; } = "kelvin";
    /// <summary>Home Assistant's MQTT discovery prefix.</summary>
    public string DiscoveryPrefix { get; set; } = "homeassistant";
    /// <summary>Let Home Assistant switch profiles. Kelvin never takes direct fan-speed commands over MQTT.</summary>
    public bool AllowProfileControl { get; set; } = true;
}
