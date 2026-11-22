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

    /// <summary>
    /// Whether these connection details would send the saved password somewhere it wasn't entered
    /// for: another address or port, another account, or the same broker without encryption.
    /// The password belongs to one broker, so a change like that must come with a new password.
    /// </summary>
    public bool SendsPasswordElsewhere(string host, int port, bool useTls, string username) =>
        !string.Equals(Host, host, StringComparison.OrdinalIgnoreCase) || Port != port
        || (UseTls && !useTls) || !string.Equals(Username, username, StringComparison.Ordinal);
}
