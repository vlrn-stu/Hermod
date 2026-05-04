using Hermod.Core.Models;

namespace Hermod.Core.Interfaces;

/// <summary>
/// Abstraction over the coordinator's MQTT client. Implementations are
/// expected to auto-reconnect and to re-subscribe on reconnect so subscribers
/// do not need to resubscribe after a transient outage.
/// </summary>
public interface IMqttService
{
    /// <summary>Latest observed connection state. Updated by <see cref="ConnectionStateChanged"/>.</summary>
    bool IsConnected { get; }

    /// <summary>Fires for every inbound message on a subscribed topic. Handlers run on the broker thread; offload slow work.</summary>
    event EventHandler<MqttMessage>? MessageReceived;

    /// <summary>Fires on every connect/disconnect transition (reconnect bursts included).</summary>
    event EventHandler<bool>? ConnectionStateChanged;

    /// <summary>Opens the MQTT connection and attaches the configured subscriptions.</summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Closes the MQTT connection and suppresses further reconnect attempts.</summary>
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Subscribes to <paramref name="topic"/> (MQTT <c>+</c>/<c>#</c> wildcards accepted).</summary>
    Task SubscribeAsync(string topic, CancellationToken cancellationToken = default);

    /// <summary>Unsubscribes from a previously-subscribed <paramref name="topic"/>.</summary>
    Task UnsubscribeAsync(string topic, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes <paramref name="payload"/> to <paramref name="topic"/>. MQTT QoS semantics:
    /// 0 fire-and-forget, 1 at-least-once, 2 exactly-once. <paramref name="retain"/>=true
    /// asks the broker to keep the last message as initial state for new subscribers.
    /// </summary>
    Task PublishAsync(string topic, string payload, bool retain = false, int qos = 0, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a snapshot of the in-memory bounded ring buffer of recent messages
    /// (chronological order, oldest first). Intended for the dashboard feed; not a
    /// persistent log.
    /// </summary>
    IReadOnlyList<MqttMessage> GetMessageHistory();
}
