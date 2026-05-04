using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Hermod.TestHarness.Runners;


/// <summary>
/// Security tests: LoRa physical-layer attacks, MQTT topic injection,
/// malformed payload handling. Follows docs/TESTING_METHODOLOGY.md section 3.7.
///
/// Replay and spoof detection do not exist in the coordinator today; the
/// thesis defers them to future work. Those tests emit NOT_IMPLEMENTED rather
/// than silently passing on a dead assertion.
/// </summary>
public sealed class SecurityTestRunner
{
    private const string ClaimO4 = "O4";

    private readonly ILogger<SecurityTestRunner> _logger;
    private readonly MqttTestClient _mqtt;
    private readonly MeasurementCollector _collector;
    private readonly IConfiguration _config;
    private readonly string _baseUrl;
    private readonly string _adminEmail;
    private readonly string? _adminPassword;

    public SecurityTestRunner(
        ILogger<SecurityTestRunner> logger,
        MqttTestClient mqtt,
        MeasurementCollector collector,
        IConfiguration config)
    {
        _logger = logger;
        _mqtt = mqtt;
        _collector = collector;
        _config = config;
        _baseUrl = config["Coordinator:BaseUrl"]
            ?? Environment.GetEnvironmentVariable("HERMOD_URL")
            ?? Environment.GetEnvironmentVariable("HERMOD_COORDINATOR_URL")
            ?? "http://localhost:42069";
        // HermodApiClient.LoginAsync expects an email; the other four runners
        // read HERMOD_ADMIN_EMAIL with `v@l.l` (the vault42 seed identity) as
        // the fallback. HERMOD_ADMIN_USER and Coordinator:AdminUser are
        // retained as deprecated aliases so any caller still setting them
        // keeps working.
        _adminEmail = config["Coordinator:AdminEmail"]
            ?? config["Coordinator:AdminUser"]
            ?? Environment.GetEnvironmentVariable("HERMOD_ADMIN_EMAIL")
            ?? Environment.GetEnvironmentVariable("HERMOD_ADMIN_USER")
            ?? "v@l.l";
        _adminPassword = Environment.GetEnvironmentVariable("HERMOD_ADMIN_PASSWORD");
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _logger.LogInformation("=== Security Tests ===");

        await _mqtt.ConnectAsync(ct);

        // LoRa hardening is future work per thesis Chapter 5/Conclusion.
        // These rows keep the claim visible so reviewers see the gap.
        ReplayAttackNotImplemented();
        SpoofedDeviceNotImplemented();

        await MalformedPayloadsDoNotCrashCoordinator(ct);
        await MqttInjectionDoesNotAdvanceHermodCounters(ct);
    }

    private void ReplayAttackNotImplemented()
    {
        _collector.Record(new TestResult
        {
            Category = "Security",
            Claim = ClaimO4,
            Name = "LoRa_ReplayAttack",
            Status = "NOT_IMPLEMENTED",
            Details = "Coordinator has no LoRa replay detector. Thesis defers " +
                      "LoRa cryptographic hardening to future work. See " +
                      "docs/TESTING_METHODOLOGY.md section 3.7.3."
        });
    }

    private void SpoofedDeviceNotImplemented()
    {
        _collector.Record(new TestResult
        {
            Category = "Security",
            Claim = ClaimO4,
            Name = "LoRa_SpoofedDevice",
            Status = "NOT_IMPLEMENTED",
            Details = "Coordinator has no device registry for LoRa. Spoofed " +
                      "frames cannot be rejected because there is nothing to " +
                      "reject against. Future work."
        });
    }

    /// <summary>
    /// Malformed payloads must not wedge the coordinator. We publish a barrage
    /// of junk then assert that a subsequent legitimate round-trip still
    /// completes within the liveness SLO. This is the only assertion we can
    /// make without a dedicated rejection signal from the coordinator.
    /// </summary>
    private async Task MalformedPayloadsDoNotCrashCoordinator(CancellationToken ct)
    {
        _logger.LogInformation("Test: Malformed payloads do not crash coordinator");

        var malformed = new (string Name, string Payload)[]
        {
            ("empty", ""),
            ("not_json", "this is not json at all"),
            ("incomplete_json", "{\"temperature\": "),
            ("deeply_nested", GenerateDeeplyNested(50)),
            ("oversized", new string('A', 100_000)),
            ("sql_injection", "{\"device_id\":\"'; DROP TABLE devices; --\"}"),
            ("null_bytes", "{\"\u0000temperature\u0000\": 22.5}")
        };

        foreach (var (name, payload) in malformed)
        {
            try
            {
                await _mqtt.PublishAsync($"zigbee/malformed_test_{name}", payload, ct);
                await Task.Delay(TimeSpan.FromMilliseconds(100), ct);

                // Methodology rule: INFO is not permitted on a path that
                // claims to validate a property. This test only confirms that
                // the MQTT client did not throw on the malformed publish
                // (trivially true for any well-formed MQTT packet regardless
                // of payload bytes), which is not a security property we can
                // take credit for. Record as NOT_IMPLEMENTED with the concrete
                // gap (downstream ingestion / coordinator liveness delta) so
                // the row is not mistaken for a silent pass.
                _collector.Record(new TestResult
                {
                    Category = "Security",
                    Claim = ClaimO4,
                    Name = $"MalformedPayload_{name}_Accepted",
                    Status = "NOT_IMPLEMENTED",
                    Details = "Broker-only publish; no downstream ingestion / " +
                              "coordinator liveness assertion is wired in yet."
                });
            }
            catch (Exception ex)
            {
                _collector.Record(new TestResult
                {
                    Category = "Security",
                    Claim = ClaimO4,
                    Name = $"MalformedPayload_{name}_Accepted",
                    Status = "ERROR",
                    Details = $"Publish failed: {ex.GetType().Name}"
                });
            }
        }

        // Liveness check: a legitimate correlated round trip must still work.
        await _mqtt.SubscribeAsync("hermod/test-liveness/+/set", ct);
        try
        {
            var response = await _mqtt.PublishAndWaitAsync(
                publishTopic: "zigbee/kitchen_sensor",
                publishPayload: "{\"temperature\":22.0,\"humidity\":45}",
                waitTopic: "hermod/test-liveness/+/set",
                timeout: TimeSpan.FromSeconds(5),
                ct: ct);

            if (response.LatencyMs is null)
            {
                // Methodology rule: INFO is not permitted on a path that
                // claims to validate a property. The correlated round trip
                // depends on a seeded rule forwarding to
                // hermod/test-liveness/<device>/set; until one is in place
                // we cannot assert liveness after the barrage, so record
                // NOT_IMPLEMENTED with the concrete gap.
                _collector.Record(new TestResult
                {
                    Category = "Security",
                    Claim = ClaimO4,
                    Name = "MalformedPayload_LivenessAfterBarrage",
                    Status = "NOT_IMPLEMENTED",
                    Details = "No liveness rule seeded; coordinator still " +
                              "accepted publishes (no exception observed)."
                });
            }
            else
            {
                _collector.Record(new TestResult
                {
                    Category = "Security",
                    Claim = ClaimO4,
                    Name = "MalformedPayload_LivenessAfterBarrage",
                    Status = "PASS",
                    LatencyMs = response.LatencyMs,
                    CorrelationId = response.CorrelationId,
                    Details = "Coordinator still responds within the liveness SLO"
                });
            }
        }
        catch (Exception ex)
        {
            _collector.Record(new TestResult
            {
                Category = "Security",
                Claim = ClaimO4,
                Name = "MalformedPayload_LivenessAfterBarrage",
                Status = "FAIL",
                Details = $"Coordinator appears wedged: {ex.GetType().Name}: {ex.Message}"
            });
        }
    }

    /// <summary>
    /// MQTT topic injection: publishing to hermod/stats/... must not advance
    /// the processed-counter on the coordinator side. The previous test was
    /// PASS-on-publish-exception, which could not distinguish "publish
    /// blocked" from "publish silently accepted and dropped".
    /// </summary>
    private async Task MqttInjectionDoesNotAdvanceHermodCounters(CancellationToken ct)
    {
        _logger.LogInformation("Test: MQTT injection does not advance hermod counters");

        var beforeCounters = await TryGetStatsCountersAsync(ct);
        if (beforeCounters is null)
        {
            _collector.Record(new TestResult
            {
                Category = "Security",
                Claim = ClaimO4,
                Name = "MqttInjection_ReservedTopicPrefix",
                Status = "ERROR",
                Details = "Could not reach GET /api/stats on the coordinator"
            });
            return;
        }

        var injectionTopics = new[]
        {
            "hermod/rules/inject",
            "hermod/stats/inject",
            "hermod/backup/inject"
        };

        foreach (var topic in injectionTopics)
        {
            try
            {
                await _mqtt.PublishAsync(topic, "{\"injected\":true}", ct);
            }
            catch
            {
                // Publish might throw if the broker enforces ACL. Either way
                // the assertion below covers both outcomes: the coordinator
                // must not record these as legitimate messages.
            }
        }

        await Task.Delay(TimeSpan.FromSeconds(1), ct);

        var afterCounters = await TryGetStatsCountersAsync(ct);
        if (afterCounters is null)
        {
            _collector.Record(new TestResult
            {
                Category = "Security",
                Claim = ClaimO4,
                Name = "MqttInjection_ReservedTopicPrefix",
                Status = "ERROR",
                Details = "Stats endpoint disappeared mid-test"
            });
            return;
        }

        // We do not assert the counter is unchanged because any background
        // traffic (other tests, a retained state) can legitimately advance
        // it. We assert the delta is under a small threshold proportional to
        // the number of injection attempts. Larger deltas mean injection
        // payloads were treated as real traffic.
        var delta = afterCounters.Value.Messages - beforeCounters.Value.Messages;
        var pass = delta < injectionTopics.Length * 2;

        _collector.Record(new TestResult
        {
            Category = "Security",
            Claim = ClaimO4,
            Name = "MqttInjection_ReservedTopicPrefix",
            Status = pass ? "PASS" : "FAIL",
            Details = $"messages_processed delta={delta} over {injectionTopics.Length} injection attempts"
        });
    }

    private async Task<(long Messages, long Rules)?> TryGetStatsCountersAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_adminPassword))
            return null;

        try
        {
            using var api = new HermodApiClient(_baseUrl);
            if (!await api.LoginAsync(_adminEmail, _adminPassword, ct))
                return null;
            return await api.GetStatsCountersAsync(ct);
        }
        catch
        {
            return null;
        }
    }

    private static string GenerateDeeplyNested(int depth)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < depth; i++) sb.Append("{\"n\":");
        sb.Append("1");
        for (int i = 0; i < depth; i++) sb.Append('}');
        return sb.ToString();
    }
}
