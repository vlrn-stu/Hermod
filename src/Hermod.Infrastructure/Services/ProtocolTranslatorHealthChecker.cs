using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Hermod.Core.Configuration;
using Hermod.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hermod.Infrastructure.Services;

/// <summary>
/// Probes each configured protocol translator's liveness endpoint and
/// returns a uniform <see cref="TranslatorHealth"/> snapshot.
/// </summary>
public sealed class ProtocolTranslatorHealthChecker : IProtocolTranslatorHealth
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<HermodSettings> _settings;
    private readonly ILogger<ProtocolTranslatorHealthChecker> _logger;

    /// <summary>
    /// Creates a checker wired to the shared <see cref="IHttpClientFactory"/>
    /// so sockets are pooled across probe batches.
    /// </summary>
    public ProtocolTranslatorHealthChecker(
        IHttpClientFactory httpClientFactory,
        IOptions<HermodSettings> settings,
        ILogger<ProtocolTranslatorHealthChecker> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClientFactory = httpClientFactory;
        _settings = settings;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<TranslatorHealth>> CheckAllAsync(CancellationToken cancellationToken = default)
    {
        var t = _settings.Value.ProtocolTranslators;

        var tasks = new[]
        {
            ProbeAsync("Zigbee2MQTT", t.Zigbee2Mqtt, cancellationToken),
            ProbeAsync("LoRa2MQTT", t.Lora2Mqtt, cancellationToken),
            ProbeAsync("BLE2MQTT", t.Ble2Mqtt, cancellationToken),
            ProbeAsync("WiFi2MQTT", t.Wifi2Mqtt, cancellationToken),
        };

        return await Task.WhenAll(tasks);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Liveness probe must never throw into the caller; any failure becomes a Reachable=false health row.")]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "HttpClient returned by IHttpClientFactory is owned by the factory and must not be disposed.")]
    private async Task<TranslatorHealth> ProbeAsync(string name, TranslatorSettings settings, CancellationToken cancellationToken)
    {
        var configured = !string.IsNullOrEmpty(settings.Url) && settings.Enabled;
        if (!configured)
        {
            return new TranslatorHealth(name, settings.Url, Configured: false, Reachable: false, Error: null);
        }

        var url = settings.Url!;

        // mqtt:// / mqtts:// / tcp:// / tls:// URLs: probe with a TCP
        // connect — the translator is an MQTT bridge that exposes no
        // HTTP surface, but a successful TCP handshake with the broker
        // it talks to is a faithful liveness check. (mqtts:// is the
        // mTLS variant the prod overlay uses for ble2mqtt + wifi2mqtt.)
        if (url.StartsWith("mqtt://",  StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("mqtts://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("tcp://",   StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("tls://",   StringComparison.OrdinalIgnoreCase))
        {
            return await ProbeTcpAsync(name, url, cancellationToken);
        }

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return new TranslatorHealth(name, url, Configured: true, Reachable: false,
                Error: "Unsupported URL scheme");
        }

        var probeUrl = url.TrimEnd('/') + settings.HealthEndpoint;
        if (!Uri.TryCreate(probeUrl, UriKind.Absolute, out var probeUri))
        {
            return new TranslatorHealth(name, url, Configured: true, Reachable: false,
                Error: "Invalid URL");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(ProbeTimeout);

        try
        {
            var client = _httpClientFactory.CreateClient();
            using var response = await client.GetAsync(probeUri, cts.Token);

            // Treat any response from the server (2xx/3xx/4xx) as proof
            // of life — the translator's HTTP server is up. Only 5xx and
            // below-transport failures mean "down". Zigbee2MQTT in
            // particular returns 404 on unknown paths (no /health/live)
            // but the server is perfectly healthy; marking that red
            // would be a false negative.
            var statusCode = (int)response.StatusCode;
            var reachable = statusCode < 500;
            var error = reachable
                ? (response.IsSuccessStatusCode
                    ? null
                    : string.Create(CultureInfo.InvariantCulture, $"HTTP {statusCode} (server alive, probe path has no health endpoint)"))
                : string.Create(CultureInfo.InvariantCulture, $"HTTP {statusCode}");
            return new TranslatorHealth(name, url, Configured: true, Reachable: reachable, Error: error);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return new TranslatorHealth(name, url, Configured: true, Reachable: false, Error: "Timeout");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Translator {Name} probe failed at {Url}", name, probeUrl);
            return new TranslatorHealth(name, url, Configured: true, Reachable: false, Error: ex.GetType().Name);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Same justification as HTTP probe path: probe must not throw.")]
    private async Task<TranslatorHealth> ProbeTcpAsync(string name, string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return new TranslatorHealth(name, url, Configured: true, Reachable: false, Error: "Invalid URL");
        }

        var host = uri.Host;
        // Default to 8883 for mqtts/tls (mTLS), 1883 for plain mqtt/tcp.
        var defaultPort = uri.Scheme.Equals("mqtts", StringComparison.OrdinalIgnoreCase)
                       || uri.Scheme.Equals("tls",   StringComparison.OrdinalIgnoreCase)
                       ? 8883 : 1883;
        var port = uri.Port > 0 ? uri.Port : defaultPort;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(ProbeTimeout);

        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            await tcp.ConnectAsync(host, port, cts.Token);
            return new TranslatorHealth(name, url, Configured: true, Reachable: tcp.Connected, Error: null);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return new TranslatorHealth(name, url, Configured: true, Reachable: false, Error: "Timeout");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Translator {Name} TCP probe failed at {Host}:{Port}", name, host, port);
            return new TranslatorHealth(name, url, Configured: true, Reachable: false, Error: ex.GetType().Name);
        }
    }
}
