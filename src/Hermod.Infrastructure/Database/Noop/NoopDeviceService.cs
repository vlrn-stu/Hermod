using Hermod.Core.Interfaces;
using Hermod.Core.Models;

namespace Hermod.Infrastructure.Database.Noop;

/// <summary>
/// Pass-through device service used when <c>Hermod:Storage:Mode</c> is
/// <c>Noop</c>. Writes succeed silently; reads return empty. Lets the
/// thesis measure "coordinator CPU only" throughput by eliminating every
/// Postgres round-trip on the hot path.
///
/// The <see cref="AvailabilityChanged"/> event exists because the interface
/// requires it, but without a device registry no transition is ever raised.
/// </summary>
internal sealed class NoopDeviceService : IDeviceService
{
    public event EventHandler<DeviceAvailabilityChangedEventArgs>? AvailabilityChanged
    {
        add { /* no-op: never fires */ }
        remove { /* no-op */ }
    }

    public Task<DevicePage> GetDevicesPageAsync(int offset, int limit, string? filter = null, Protocol? protocol = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new DevicePage(Array.Empty<Device>(), 0, Math.Max(0, offset), Math.Clamp(limit, 1, 1000)));

#pragma warning disable CS1998 // async without awaits — yield break is the whole point
    public async IAsyncEnumerable<Device> StreamAllDevicesAsync(
        int pageSize = 500,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        yield break;
    }
#pragma warning restore CS1998

    public Task<Device?> GetDeviceAsync(string id, CancellationToken cancellationToken = default)
        => Task.FromResult<Device?>(null);

    public Task<DeviceCounts> GetCountsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new DeviceCounts(0, 0, new Dictionary<Protocol, int>()));

    public Task<Device> AddOrUpdateDeviceAsync(Device device, CancellationToken cancellationToken = default)
        => Task.FromResult(device);

    public Task<bool> RemoveDeviceAsync(string id, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task<bool> RenameDeviceAsync(string oldId, string newId, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task<IEnumerable<Device>> GetDevicesByProtocolAsync(Protocol protocol, CancellationToken cancellationToken = default)
        => Task.FromResult<IEnumerable<Device>>(Array.Empty<Device>());

    public Task UpdateDeviceStateAsync(string deviceId, Dictionary<string, object> state, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task UpsertDeviceStateAsync(string deviceId, string name, Protocol protocol, Dictionary<string, object> state, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task UpdateDeviceStatusAsync(string deviceId, DeviceStatus status, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
