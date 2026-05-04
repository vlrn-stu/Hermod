using LoRa2MQTT.Service.Models;

namespace LoRa2MQTT.Service.Adapters;

/// <summary>
/// Interface for LoRa adapter implementations.
/// </summary>
public interface ILoRaAdapter : IAsyncDisposable
{
    /// <summary>
    /// Event raised when a message is received from a LoRa device.
    /// </summary>
    event EventHandler<LoRaMessage>? MessageReceived;

    /// <summary>
    /// Gets a value indicating whether the adapter is connected.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Connects to the LoRa adapter.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the async operation.</returns>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Disconnects from the LoRa adapter.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the async operation.</returns>
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends data to a LoRa device.
    /// </summary>
    /// <param name="command">The command to send.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the async operation.</returns>
    Task SendAsync(LoRaCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts receiving messages from LoRa devices.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the async operation.</returns>
    Task StartReceivingAsync(CancellationToken cancellationToken = default);
}
