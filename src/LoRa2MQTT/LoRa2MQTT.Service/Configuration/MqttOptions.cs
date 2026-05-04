namespace LoRa2MQTT.Service.Configuration;

/// <summary>
/// Configuration options for the MQTT client.
/// </summary>
public sealed class MqttOptions
{
    /// <summary>
    /// Configuration section name in appsettings.json.
    /// </summary>
    public const string SectionName = "Mqtt";

    /// <summary>
    /// Gets or sets the MQTT broker host.
    /// </summary>
    public string Host { get; set; } = "localhost";

    /// <summary>
    /// Gets or sets the MQTT broker port.
    /// </summary>
    public int Port { get; set; } = 1883;

    /// <summary>
    /// Gets or sets the MQTT client ID.
    /// </summary>
    public string ClientId { get; set; } = "lora2mqtt";

    /// <summary>
    /// Gets or sets the base topic for publishing messages.
    /// </summary>
    public string BaseTopic { get; set; } = "lora";

    /// <summary>
    /// Gets or sets the username for MQTT authentication.
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// Gets or sets the password for MQTT authentication.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Gets or sets the keep-alive interval in seconds.
    /// </summary>
    public int KeepAliveSeconds { get; set; } = 60;

    /// <summary>
    /// Gets or sets whether to use clean session.
    /// </summary>
    public bool CleanSession { get; set; } = true;

    /// <summary>
    /// TLS configuration. Applied when <see cref="MqttTlsOptions.UseTls"/> is true.
    /// </summary>
    public MqttTlsOptions Tls { get; set; } = new();
}

/// <summary>
/// TLS configuration for the MQTT client.
/// </summary>
public sealed class MqttTlsOptions
{
    /// <summary>Enable TLS for the MQTT broker connection.</summary>
    public bool UseTls { get; set; }
    /// <summary>Skip cert chain validation (test-only).</summary>
    public bool AllowUntrustedCertificates { get; set; }
    /// <summary>Ignore X.509 chain errors (test-only).</summary>
    public bool IgnoreCertificateChainErrors { get; set; }
    /// <summary>Ignore CRL/OCSP revocation lookup failures.</summary>
    public bool IgnoreCertificateRevocationErrors { get; set; }
    /// <summary>PEM-encoded CA bundle used to verify the broker certificate.</summary>
    public string? CaBundlePath { get; set; }
    /// <summary>PEM-encoded client cert presented for mTLS.</summary>
    public string? ClientCertificatePath { get; set; }
    /// <summary>PEM-encoded client private key paired with <see cref="ClientCertificatePath"/>.</summary>
    public string? ClientKeyPath { get; set; }
}
