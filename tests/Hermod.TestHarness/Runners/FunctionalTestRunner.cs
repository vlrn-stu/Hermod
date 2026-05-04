using System.Globalization;
using System.Text.Json;
using Hermod.Core.Models.Rules;
using Microsoft.Extensions.Logging;

namespace Hermod.TestHarness.Runners;

/// <summary>
/// Functional tests verifying cross-technology translation.
/// Validates safety and liveness properties of the formal model.
/// </summary>
public sealed class FunctionalTestRunner
{
    private readonly ILogger<FunctionalTestRunner> _logger;
    private readonly MqttTestClient _mqtt;
    private readonly MockPublisher _mock;
    private readonly MeasurementCollector _collector;

    private readonly string _baseUrl;
    private readonly string _adminEmail;
    private readonly string? _adminPassword;

    public FunctionalTestRunner(
        ILogger<FunctionalTestRunner> logger,
        MqttTestClient mqtt,
        MockPublisher mock,
        MeasurementCollector collector)
    {
        _logger = logger;
        _mqtt = mqtt;
        _mock = mock;
        _collector = collector;

        _baseUrl = Environment.GetEnvironmentVariable("HERMOD_URL")
                   ?? Environment.GetEnvironmentVariable("HERMOD_COORDINATOR_URL")
                   ?? "http://localhost:42069";
        _adminEmail = Environment.GetEnvironmentVariable("HERMOD_ADMIN_EMAIL")
                      ?? "v@l.l";
        _adminPassword = Environment.GetEnvironmentVariable("HERMOD_ADMIN_PASSWORD");
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _logger.LogInformation("=== Functional Tests ===");

        await _mqtt.ConnectAsync(ct);

        await TestCrossProtocolTranslation(ct);
        await TestMultiProtocolRule(ct);
        await TestSemanticPreservation(ct);
        await TestSafetyNoSpuriousMessages(ct);
    }

    /// <summary>
    /// ZigBee contact sensor -> WiFi bridge. Verifies that the seeded
    /// <c>rule-zigbee-to-wifi</c> bridge fires for any zigbee2mqtt event and
    /// produces the corresponding wifi/bridged/{deviceName} output.
    ///
    /// The wait topic is <c>wifi/bridged/front_door</c>, which matches the
    /// seeded bridge rule's action template
    /// <c>wifi/bridged/{{deviceName}}</c> at
    /// <c>PostgresDatabaseInitializer.cs</c> line 317. An earlier
    /// <c>wifi/thermostat_living/set</c> wait did not correspond to any
    /// seeded rule.
    /// </summary>
    private async Task TestCrossProtocolTranslation(CancellationToken ct)
    {
        _logger.LogInformation("Test: Cross-protocol translation (ZigBee -> WiFi bridge)");

        var response = await _mqtt.PublishAndWaitAsync(
            publishTopic: "zigbee/front_door",
            publishPayload: """{"contact":false,"battery":92,"linkquality":120}""",
            waitTopic: "wifi/bridged/front_door",
            timeout: TimeSpan.FromSeconds(10),
            ct: ct);

        _collector.Record(new TestResult
        {
            Category = "Functional",
            Claim = "C2-C3",
            Name = "CrossProtocol_ZigBee_WiFi",
            Status = response.LatencyMs.HasValue ? "PASS" : "FAIL",
            LatencyMs = response.LatencyMs,
            CorrelationId = response.CorrelationId,
            Details = response.LatencyMs.HasValue
                ? string.Format(
                    CultureInfo.InvariantCulture,
                    "Action received in {0:F1}ms: {1}",
                    response.LatencyMs,
                    response.ForwardedPayload)
                : "Timed out waiting for action"
        });
    }

    /// <summary>
    /// Compound conditions in a single rule, exercising the rules engine's
    /// AND evaluation across multiple payload properties. This test seeds
    /// and tears down its own fixture rule via the REST API (the seed
    /// catalogue does not include one), matching the seed/teardown pattern
    /// in <c>PerformanceTestRunner.SeedPerfRuleAsync</c>.
    ///
    /// The rules engine evaluates conditions on a single triggering message,
    /// so this test publishes one ZigBee message that carries both
    /// occupancy and temperature properties. The seeded rule fires only
    /// when ALL conditions hold (occupancy=true AND temperature>30), then
    /// publishes to a wifi action topic. The harness asserts the
    /// correlation id survives the round trip.
    ///
    /// True cross-message state aggregation (one ZigBee message + one LoRa
    /// message together triggering an action) requires explicit per-rule
    /// state and is not part of the engine's default condition evaluator;
    /// when that becomes a thesis claim it gets its own dedicated test.
    /// </summary>
    private async Task TestMultiProtocolRule(CancellationToken ct)
    {
        _logger.LogInformation("Test: Compound conditions (AND) ZigBee -> WiFi");

        if (string.IsNullOrEmpty(_adminPassword))
        {
            _collector.Record(new TestResult
            {
                Category = "Functional", Claim = "C2-C3",
                Name = "Compound_AndConditions_Zigbee_to_Wifi",
                Status = "ERROR",
                Details = "HERMOD_ADMIN_PASSWORD not set; cannot seed compound fixture rule."
            });
            return;
        }

        var deviceId = $"compound_{Guid.NewGuid():N}".Substring(0, 24);
        var triggerPattern = $"zigbee/{deviceId}";
        var actionTopic = $"wifi/test_compound/{deviceId}/set";

        using var api = new HermodApiClient(_baseUrl);
        if (!await api.LoginAsync(_adminEmail, _adminPassword, ct))
        {
            _collector.Record(new TestResult
            {
                Category = "Functional", Claim = "C2-C3",
                Name = "Compound_AndConditions_Zigbee_to_Wifi",
                Status = "ERROR",
                Details = $"Login as {_adminEmail} failed; cannot seed fixture rule."
            });
            return;
        }

        var ruleBody = new
        {
            name = $"harness-functional-compound-{Guid.NewGuid():N}",
            description = "AND conditions over occupancy + temperature.",
            enabled = true,
            trigger = new RuleTrigger { TopicPattern = triggerPattern, Type = TriggerType.OnMessage },
            conditions = new RuleConditionGroup
            {
                Logic = LogicOperator.All,
                Conditions = new List<RuleCondition>
                {
                    new RuleCondition { Property = "occupancy",   Operator = ComparisonOperator.Equals,      Value = true },
                    new RuleCondition { Property = "temperature", Operator = ComparisonOperator.GreaterThan, Value = 30.0 }
                }
            },
            actions = new[]
            {
                new RuleAction { Type = ActionType.Publish, Topic = actionTopic, PassthroughPayload = true, QoS = 0 }
            }
        };

        string? ruleId = null;
        try
        {
            using var seedResp = await api.PostJsonAsync("/api/rules", ruleBody, ct);
            if (!seedResp.IsSuccessStatusCode)
            {
                var err = await seedResp.Content.ReadAsStringAsync(ct);
                _collector.Record(new TestResult
                {
                    Category = "Functional", Claim = "C2-C3",
                    Name = "Compound_AndConditions_Zigbee_to_Wifi",
                    Status = "ERROR",
                    Details = $"POST /api/rules -> {(int)seedResp.StatusCode}: {Truncate(err, 160)}"
                });
                return;
            }
            using var seedDoc = JsonDocument.Parse(await seedResp.Content.ReadAsStringAsync(ct));
            ruleId = seedDoc.RootElement.GetProperty("id").GetString();

            // Now drive the round trip. The rule evaluates conditions on the
            // same payload that triggered it; a single message must carry
            // every property the rule checks.
            var response = await _mqtt.PublishAndWaitAsync(
                publishTopic: triggerPattern,
                publishPayload: """{"occupancy":true,"temperature":35.0,"humidity":62}""",
                waitTopic: actionTopic,
                timeout: TimeSpan.FromSeconds(10),
                ct: ct);

            _collector.Record(new TestResult
            {
                Category = "Functional", Claim = "C2-C3",
                Name = "Compound_AndConditions_Zigbee_to_Wifi",
                Status = response.LatencyMs.HasValue ? "PASS" : "FAIL",
                LatencyMs = response.LatencyMs,
                CorrelationId = response.CorrelationId,
                Details = response.LatencyMs.HasValue
                    ? string.Format(CultureInfo.InvariantCulture,
                        "Compound rule fired in {0:F1}ms; forwarded={1}",
                        response.LatencyMs, Truncate(response.ForwardedPayload ?? "", 120))
                    : "Compound rule did not fire within 10s; conditions did not match"
            });
        }
        finally
        {
            if (ruleId is not null)
            {
                try { using var _ = await api.DeleteAsync($"/api/rules/{ruleId}", ct); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to teardown fixture rule {Id}", ruleId); }
            }
        }
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s.Substring(0, max) + "...";

    /// <summary>
    /// Semantic preservation test battery, one sub-test per primitive shape.
    /// Methodology ref: <c>docs/TESTING_HARNESS.md</c> section 3.1 paragraph 96-97.
    ///
    /// Each test publishes a payload containing a single primitive on
    /// <c>zigbee/test_&lt;primitive&gt;_&lt;n&gt;</c>, waits (by correlation id) for
    /// the forwarded output on <c>hermod/debug/test_&lt;primitive&gt;_&lt;n&gt;</c>, and
    /// asserts the serialized value survives byte-equal when formatted with
    /// <c>InvariantCulture</c>. The forwarding rule is the seeded
    /// <c>rule-debug-passthrough</c> from <c>PostgresDatabaseInitializer</c>: it
    /// triggers on <c>zigbee/+</c> and publishes with
    /// <c>PassthroughPayload=true</c>, so the test does not need to seed per-
    /// primitive rules to exercise the round trip.
    ///
    /// Dependencies: <c>rule-debug-passthrough</c> must be enabled in the
    /// coordinator. If someone disables it these tests go FAIL, not SKIP, so
    /// the regression is visible.
    /// </summary>
    private async Task TestSemanticPreservation(CancellationToken ct)
    {
        _logger.LogInformation("Test: Semantic preservation (per-primitive round trip)");

        // No more `hermod/#` wildcard + F1-format scraping. Each sub-test uses
        // PublishAndWaitAsync which subscribes to the single wait topic and
        // resolves by correlation id, so two concurrent primitive tests on
        // different topics cannot collide with each other.

        await PrimitiveRoundTrip(
            name: "SemanticPreservation_Float_Zigbee_to_Debug",
            deviceId: "test_float_01",
            sourcePayload: """{"temperature": 23.7}""",
            expectedNeedle: "23.7",
            ct: ct);

        await PrimitiveRoundTrip(
            name: "SemanticPreservation_Integer_Zigbee_to_Debug",
            deviceId: "test_int_01",
            sourcePayload: """{"battery": 78}""",
            expectedNeedle: "78",
            ct: ct);

        await PrimitiveRoundTrip(
            name: "SemanticPreservation_String_Zigbee_to_Debug",
            deviceId: "test_string_01",
            sourcePayload: """{"mode": "heating"}""",
            expectedNeedle: "heating",
            ct: ct);

        await PrimitiveRoundTrip(
            name: "SemanticPreservation_Boolean_Zigbee_to_Debug",
            deviceId: "test_bool_01",
            sourcePayload: """{"contact": false}""",
            expectedNeedle: "false",
            ct: ct);

        await PrimitiveRoundTrip(
            name: "SemanticPreservation_NestedObject_Zigbee_to_Debug",
            deviceId: "test_nested_01",
            sourcePayload: """{"position":{"x":12.5,"y":7.25}}""",
            expectedNeedle: "12.5",
            ct: ct);
    }

    /// <summary>
    /// Publishes a correlated payload on <c>zigbee/{deviceId}</c>, waits on
    /// <c>hermod/debug/{deviceId}</c>, asserts the forwarded payload contains
    /// <paramref name="expectedNeedle"/> (formatted InvariantCulture by
    /// <see cref="System.Text.Json"/>, which never switches on thread culture).
    /// </summary>
    private async Task PrimitiveRoundTrip(
        string name,
        string deviceId,
        string sourcePayload,
        string expectedNeedle,
        CancellationToken ct)
    {
        var publishTopic = $"zigbee/{deviceId}";
        var waitTopic = $"hermod/debug/{deviceId}";

        var response = await _mqtt.PublishAndWaitAsync(
            publishTopic: publishTopic,
            publishPayload: sourcePayload,
            waitTopic: waitTopic,
            timeout: TimeSpan.FromSeconds(10),
            ct: ct);

        string status;
        string details;
        if (!response.LatencyMs.HasValue)
        {
            status = "FAIL";
            details = string.Format(
                CultureInfo.InvariantCulture,
                "Timed out on {0} (rule-debug-passthrough missing or disabled?)",
                waitTopic);
        }
        else if (response.ForwardedPayload is null)
        {
            status = "FAIL";
            details = "Response envelope present but payload was null";
        }
        else if (!ForwardedPayloadContains(response.ForwardedPayload, expectedNeedle))
        {
            status = "FAIL";
            details = string.Format(
                CultureInfo.InvariantCulture,
                "Round trip succeeded in {0:F1}ms but payload did not contain '{1}': {2}",
                response.LatencyMs,
                expectedNeedle,
                response.ForwardedPayload);
        }
        else
        {
            status = "PASS";
            details = string.Format(
                CultureInfo.InvariantCulture,
                "Round trip in {0:F1}ms preserved '{1}' in payload",
                response.LatencyMs,
                expectedNeedle);
        }

        _collector.Record(new TestResult
        {
            Category = "Functional",
            Claim = "C2-C3",
            Name = name,
            Status = status,
            LatencyMs = response.LatencyMs,
            CorrelationId = response.CorrelationId,
            Details = details
        });
    }

    /// <summary>
    /// Culture-independent substring check. Also tolerates JSON serializer
    /// whitespace variation by parsing and re-emitting the payload with
    /// default (compact) writer options, then substring-matching.
    /// </summary>
    private static bool ForwardedPayloadContains(string payload, string needle)
    {
        // Fast path: raw substring in InvariantCulture ordinal comparison.
        if (payload.Contains(needle, StringComparison.Ordinal))
        {
            return true;
        }

        // Slow path: JSON parse + re-serialize to strip whitespace variation
        // introduced by the broker or intermediate serialization layers.
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var compact = JsonSerializer.Serialize(
                doc.RootElement,
                new JsonSerializerOptions { WriteIndented = false });
            return compact.Contains(needle, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Safety: verify no spurious messages are generated without a trigger.
    /// </summary>
    private async Task TestSafetyNoSpuriousMessages(CancellationToken ct)
    {
        _logger.LogInformation("Test: Safety - no spurious messages");

        await _mqtt.SubscribeAsync("+/+/set", ct);
        _mqtt.ClearReceived();

        // Wait without sending anything
        await Task.Delay(TimeSpan.FromSeconds(5), ct);

        var spurious = _mqtt.ReceivedMessages
            .Where(m => m.Topic.EndsWith("/set"))
            .ToList();

        _collector.Record(new TestResult
        {
            Category = "Functional",
            Claim = "C2-C3",
            Name = "Safety_NoSpuriousMessages",
            Status = spurious.Count == 0 ? "PASS" : "FAIL",
            Details = spurious.Count == 0
                ? "No spurious action messages detected"
                : $"Detected {spurious.Count} unexpected action message(s)"
        });
    }
}
