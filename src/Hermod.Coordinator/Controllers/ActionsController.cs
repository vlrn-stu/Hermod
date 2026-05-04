using System.Text.Json;
using System.Text.RegularExpressions;
using Hermod.Core.Interfaces;
using Hermod.Core.Models;
using Hermod.Core.Mqtt;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Hermod.Coordinator.Controllers;

/// <summary>Admin-only MQTT publish and device command surface.</summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
[Authorize(Roles = "admin")]
public sealed class ActionsController : ControllerBase
{
    // Device identifiers must be ASCII-safe. Anything outside this set can
    // escape into sibling MQTT topic prefixes.
    private static readonly Regex DeviceIdPattern = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);

    private readonly IMqttService _mqttService;
    private readonly ILogger<ActionsController> _logger;

    /// <summary>Creates the controller with an MQTT service and logger.</summary>
    /// <param name="mqttService">MQTT broker client used to publish messages.</param>
    /// <param name="logger">Logger for audit of manual actions.</param>
    public ActionsController(IMqttService mqttService, ILogger<ActionsController> logger)
    {
        ArgumentNullException.ThrowIfNull(mqttService);
        ArgumentNullException.ThrowIfNull(logger);
        _mqttService = mqttService;
        _logger = logger;
    }

    private string ActorName => User?.Identity?.Name ?? "unknown";

    /// <summary>Publish a message to an MQTT topic.</summary>
    /// <param name="request">Topic, payload, retain flag and QoS.</param>
    /// <param name="cancellationToken">Token to abort the publish.</param>
    /// <returns>200 on success, 400 on missing topic, 503 if broker is offline.</returns>
    [HttpPost("publish")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> Publish(
        [FromBody] PublishRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrEmpty(request.Topic))
        {
            return BadRequest(new { message = "Topic is required" });
        }

        if (BrokerUnavailable(out var brokerError)) return brokerError!;

        var payload = SerializePayload(request.Payload);
        await _mqttService.PublishAsync(request.Topic, payload, request.Retain, request.Qos, cancellationToken);

        _logger.LogInformation("Published message to {Topic} via API by {Actor}", request.Topic, ActorName);
        return Ok(new { message = "Message published", topic = request.Topic });
    }

    /// <summary>Send a command to a device.</summary>
    /// <param name="deviceId">Device identifier (ASCII word characters, 1-64 chars).</param>
    /// <param name="protocol">Protocol name (Zigbee, LoRa, etc.) from <see cref="Protocol"/>.</param>
    /// <param name="command">Command body, serialized as JSON unless already a string.</param>
    /// <param name="cancellationToken">Token to abort the publish.</param>
    /// <returns>200 on success, 400 on invalid input, 503 if broker is offline.</returns>
    [HttpPost("devices/{deviceId}/command")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> SendDeviceCommand(
        string deviceId,
        [FromQuery] string? protocol,
        [FromBody] object command,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(protocol))
        {
            return BadRequest(new { message = "protocol query parameter is required" });
        }

        if (!Enum.TryParse<Protocol>(protocol, ignoreCase: true, out var parsed) || parsed == Protocol.Unknown)
        {
            return BadRequest(new { message = $"Unknown protocol '{protocol}'" });
        }

        if (!DeviceIdPattern.IsMatch(deviceId))
        {
            return BadRequest(new { message = "Invalid deviceId. Allowed: [A-Za-z0-9_-]{1,64}" });
        }

        if (BrokerUnavailable(out var brokerError)) return brokerError!;

        // Zigbee takes the z2m base_topic ("zigbee"), not ToTopicPrefix's
        // "zigbee2mqtt" — the coordinator subscribes to "zigbee/#" and z2m
        // is configured with base_topic: zigbee in every overlay, so a
        // command published to "zigbee2mqtt/..." vanishes into a topic no
        // one listens on. Non-Zigbee protocols use their protocol prefix.
        var topic = parsed == Protocol.Zigbee
            ? Zigbee2MqttTopics.DeviceSet(deviceId)
            : $"{parsed.ToTopicPrefix()}/{deviceId}/set";
        var payload = SerializePayload(command);
        await _mqttService.PublishAsync(topic, payload, cancellationToken: cancellationToken);

        _logger.LogInformation("Sent command to device {DeviceId} on topic {Topic} by {Actor}", deviceId, topic, ActorName);
        return Ok(new { message = "Command sent", topic, deviceId });
    }

    /// <summary>Trigger a rule manually by firing its first action.</summary>
    /// <param name="ruleId">Identifier of the rule to trigger.</param>
    /// <param name="rulesService">Rules service used to load the rule definition.</param>
    /// <param name="request">Optional payload override for the action.</param>
    /// <param name="cancellationToken">Token to abort the publish.</param>
    /// <returns>200 on success, 404 if rule missing, 400 if rule has no actionable topic, 503 if broker offline.</returns>
    [HttpPost("rules/{ruleId}/trigger")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> TriggerRule(
        string ruleId,
        [FromServices] IRulesService rulesService,
        [FromBody] TriggerRuleRequest? request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rulesService);
        var rule = await rulesService.GetRuleAsync(ruleId, cancellationToken);
        if (rule is null)
        {
            return NotFound(new { message = $"Rule '{ruleId}' not found" });
        }

        if (BrokerUnavailable(out var brokerError)) return brokerError!;

        var firstAction = rule.Actions.FirstOrDefault();
        if (firstAction is null)
        {
            return BadRequest(new { message = "Rule has no actions defined" });
        }

        if (string.IsNullOrEmpty(firstAction.Topic))
        {
            return BadRequest(new { message = "Rule action has no target topic" });
        }

        var payload = request?.Payload is not null
            ? SerializePayload(request.Payload)
            : JsonSerializer.Serialize(firstAction.Payload ?? new Dictionary<string, object>());

        // Respect the rule-author's retain + QoS on the manual trigger
        // path so "trigger this rule" behaves the same as a normal fire.
        var qos = Math.Clamp(firstAction.QoS, 0, 2);
        await _mqttService.PublishAsync(firstAction.Topic, payload, firstAction.Retain, qos, cancellationToken);

        _logger.LogInformation("Manually triggered rule {RuleId} by {Actor}", ruleId, ActorName);
        return Ok(new
        {
            message = "Rule triggered",
            ruleId,
            targetTopic = firstAction.Topic
        });
    }

    private bool BrokerUnavailable(out ActionResult? error)
    {
        if (_mqttService.IsConnected)
        {
            error = null;
            return false;
        }
        error = StatusCode(503, new { message = "MQTT broker not connected" });
        return true;
    }

    private static string SerializePayload(object? payload) =>
        payload is string str ? str : JsonSerializer.Serialize(payload);
}

/// <summary>Body for <see cref="ActionsController.Publish"/>.</summary>
public sealed class PublishRequest
{
    /// <summary>MQTT topic to publish to.</summary>
    public string Topic { get; set; } = string.Empty;

    /// <summary>Payload. Strings pass through; other objects are JSON-serialized.</summary>
    public object? Payload { get; set; }

    /// <summary>Retain flag forwarded to the broker.</summary>
    public bool Retain { get; set; }

    /// <summary>MQTT quality-of-service level (0-2).</summary>
    public int Qos { get; set; }
}

/// <summary>Body for <see cref="ActionsController.TriggerRule"/> with optional payload override.</summary>
public sealed class TriggerRuleRequest
{
    /// <summary>Payload override for the action; null uses the rule's default payload.</summary>
    public object? Payload { get; set; }
}
