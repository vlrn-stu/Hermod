using Hermod.Core.Models;

namespace Hermod.Core.Interfaces;

/// <summary>Aggregate device counts surfaced by <see cref="IDeviceService.GetCountsAsync"/>.</summary>
/// <param name="Total">Total registered devices.</param>
/// <param name="Online">Devices currently reporting <see cref="DeviceStatus.Online"/>.</param>
/// <param name="ByProtocol">Per-protocol breakdown; <see cref="Protocol.Unknown"/> is excluded.</param>
public sealed record DeviceCounts(
    int Total,
    int Online,
    IReadOnlyDictionary<Protocol, int> ByProtocol);

/// <summary>Paginated device slice returned by
/// <see cref="IDeviceService.GetDevicesPageAsync"/>.</summary>
/// <param name="Items">Devices in this page, in stable id order.</param>
/// <param name="Total">Total matching rows after the filter (unpaged).</param>
/// <param name="Offset">Offset of the first item in this page.</param>
/// <param name="Limit">The effective page size (after server-side clamp).</param>
public sealed record DevicePage(
    IReadOnlyList<Device> Items,
    int Total,
    int Offset,
    int Limit);

/// <summary>
/// Device registry and availability source. Callers mutate device state and
/// status here; the implementation surfaces availability transitions through
/// <see cref="AvailabilityChanged"/> so subscribers (notably the rules engine)
/// can react to online/offline edges without polling.
/// </summary>
public interface IDeviceService
{
    /// <summary>
    /// Fires when a device's <see cref="DeviceStatus"/> transitions. Edge-triggered:
    /// setting the same status twice produces no event.
    /// </summary>
    event EventHandler<DeviceAvailabilityChangedEventArgs>? AvailabilityChanged;

    /// <summary>Paginated device listing with optional filters. Stable <c>last_seen</c>-descending, id-stable order.</summary>
    /// <param name="offset">Zero-based offset.</param>
    /// <param name="limit">Max rows to return. Implementations clamp to <c>1000</c>.</param>
    /// <param name="filter">
    /// Optional free-text filter applied to <c>id</c> and <c>name</c>.
    /// Case-insensitive. Null or empty = no filter.
    /// </param>
    /// <param name="protocol">Optional protocol filter. Null = all protocols.</param>
    /// <param name="cancellationToken">Token to abort the call.</param>
    Task<DevicePage> GetDevicesPageAsync(
        int offset,
        int limit,
        string? filter = null,
        Protocol? protocol = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Bounded-memory stream of every device, for bulk consumers (backup,
    /// export) that legitimately need the full set. Internally pages
    /// through the store; the client only holds one page in memory at a
    /// time, never the full 220 k rows.
    /// </summary>
    /// <param name="pageSize">Page size used internally. Clamped to
    /// 1..1000 by the implementation.</param>
    /// <param name="cancellationToken">Token to abort the call.</param>
    IAsyncEnumerable<Device> StreamAllDevicesAsync(
        int pageSize = 500,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the device with the given <paramref name="id"/>, or null if none exists.</summary>
    Task<Device?> GetDeviceAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Server-side aggregation for the stats dashboard. Returns the
    /// totals the dashboard actually needs without shipping every device
    /// row across the wire on every poll. <c>Protocol.Unknown</c> is
    /// excluded from <c>ByProtocol</c> to match the live stats widget.
    /// </summary>
    Task<DeviceCounts> GetCountsAsync(CancellationToken cancellationToken = default);

    /// <summary>Upsert by <see cref="Device.Id"/>. Returns the stored device.</summary>
    Task<Device> AddOrUpdateDeviceAsync(Device device, CancellationToken cancellationToken = default);

    /// <summary>Returns false if no device with <paramref name="id"/> existed.</summary>
    Task<bool> RemoveDeviceAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Renames a device by updating its primary id (also bumps <see cref="Device.Name"/>
    /// to the new id). Returns false if the source id was not found, or if the
    /// destination id is already taken.
    /// </summary>
    Task<bool> RenameDeviceAsync(string oldId, string newId, CancellationToken cancellationToken = default);

    /// <summary>Returns every registered device on a given <paramref name="protocol"/>.</summary>
    Task<IEnumerable<Device>> GetDevicesByProtocolAsync(Protocol protocol, CancellationToken cancellationToken = default);

    /// <summary>
    /// Merges <paramref name="state"/> into the device's JSONB state column (delta, not
    /// replace), bumps <c>LastSeen</c>, and force-sets status to <see cref="DeviceStatus.Online"/>
    /// since receiving state implies the device is alive.
    /// </summary>
    Task UpdateDeviceStateAsync(string deviceId, Dictionary<string, object> state, CancellationToken cancellationToken = default);

    /// <summary>
    /// Single-statement UPSERT-with-JSONB-merge used on the MQTT telemetry
    /// hot path. Creates the device row with the given identity on first
    /// sight, merges <paramref name="state"/> into <c>devices.state</c>
    /// server-side on subsequent calls, and always bumps <c>LastSeen</c>.
    /// No prior read: replaces the read-modify-write pattern that turned
    /// every inbound message into two round-trips.
    /// </summary>
    Task UpsertDeviceStateAsync(
        string deviceId,
        string name,
        Protocol protocol,
        Dictionary<string, object> state,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Transitions the device's status. <c>LastSeen</c> is bumped only on
    /// <see cref="DeviceStatus.Online"/> transitions so time-since-last-contact stays
    /// accurate across Offline flaps. Raises <see cref="AvailabilityChanged"/> if the
    /// status actually changed.
    /// </summary>
    Task UpdateDeviceStatusAsync(string deviceId, DeviceStatus status, CancellationToken cancellationToken = default);
}
