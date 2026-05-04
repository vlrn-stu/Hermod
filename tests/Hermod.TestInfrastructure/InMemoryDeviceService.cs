using Hermod.Core.Interfaces;
using Hermod.Core.Models;

namespace Hermod.TestInfrastructure;

/// <summary>
/// Unified in-memory <see cref="IDeviceService"/> fake for test use.
/// Supports both construction-time seeding and post-construction mutation
/// via the <see cref="Devices"/> dictionary, plus a <see cref="ThrowOnGetAll"/>
/// hook for exercising error paths in callers.
/// </summary>
public sealed class InMemoryDeviceService : IDeviceService
{
    /// <summary>Mutable dict keyed by device id. Tests may add/remove/replace entries directly.</summary>
    public Dictionary<string, Device> Devices { get; } = new();

    /// <summary>
    /// When set, device-read methods (<see cref="GetDevicesPageAsync"/>,
    /// <see cref="StreamAllDevicesAsync"/>) throw this exception instead
    /// of returning the current devices. Only applied to the get-all path;
    /// per-device queries are unaffected.
    /// </summary>
    public Exception? ThrowOnGetAll { get; set; }

    public event EventHandler<DeviceAvailabilityChangedEventArgs>? AvailabilityChanged;

    /// <summary>
    /// Test-facing raise hook for <see cref="AvailabilityChanged"/>. Mirrors the
    /// production contract: PreviousStatus/CurrentStatus cannot be equal here
    /// because the service only raises on real transitions.
    /// </summary>
    public void RaiseAvailability(string deviceId, DeviceStatus previous, DeviceStatus current, string? topic = null)
    {
        AvailabilityChanged?.Invoke(this, new DeviceAvailabilityChangedEventArgs
        {
            DeviceId = deviceId,
            PreviousStatus = previous,
            CurrentStatus = current,
            Device = Devices.TryGetValue(deviceId, out var d) ? d : null,
            Topic = topic ?? $"availability/{deviceId}",
        });
    }

    /// <summary>Empty construction for tests that build up state progressively.</summary>
    public InMemoryDeviceService() { }

    /// <summary>
    /// Bulk-seeded construction for tests that want a ready-to-read
    /// device set. Each device is indexed by its <c>Id</c>; duplicate
    /// ids overwrite in order.
    /// </summary>
    public InMemoryDeviceService(IEnumerable<Device> seed)
    {
        foreach (var d in seed)
        {
            Devices[d.Id] = d;
        }
    }

    public async IAsyncEnumerable<Device> StreamAllDevicesAsync(
        int pageSize = 500,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        if (ThrowOnGetAll is not null) throw ThrowOnGetAll;
        foreach (var d in Devices.Values.OrderBy(d => d.Id))
        {
            yield return d;
            await Task.Yield();
        }
    }

    public Task<DevicePage> GetDevicesPageAsync(int offset, int limit, string? filter = null, Protocol? protocol = null, CancellationToken cancellationToken = default)
    {
        if (ThrowOnGetAll is not null) throw ThrowOnGetAll;
        var safeOffset = Math.Max(0, offset);
        var safeLimit = Math.Clamp(limit, 1, 1000);
        IEnumerable<Device> rows = Devices.Values.OrderBy(d => d.Id);
        if (protocol.HasValue)
        {
            rows = rows.Where(d => d.Protocol == protocol.Value);
        }
        if (!string.IsNullOrWhiteSpace(filter))
        {
            var f = filter.Trim();
            rows = rows.Where(d =>
                d.Id.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                (d.Name?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false));
        }
        var list = rows.ToList();
        var page = list.Skip(safeOffset).Take(safeLimit).ToList();
        return Task.FromResult(new DevicePage(page, list.Count, safeOffset, safeLimit));
    }

    public Task<Device?> GetDeviceAsync(string id, CancellationToken cancellationToken = default)
        => Task.FromResult(Devices.TryGetValue(id, out var d) ? d : null);

    public Task<Device> AddOrUpdateDeviceAsync(Device device, CancellationToken cancellationToken = default)
    {
        Devices[device.Id] = device;
        return Task.FromResult(device);
    }

    public Task<bool> RemoveDeviceAsync(string id, CancellationToken cancellationToken = default)
        => Task.FromResult(Devices.Remove(id));

    public Task<bool> RenameDeviceAsync(string oldId, string newId, CancellationToken cancellationToken = default)
    {
        if (string.Equals(oldId, newId, StringComparison.Ordinal)) return Task.FromResult(true);
        if (Devices.ContainsKey(newId)) return Task.FromResult(false);
        if (!Devices.Remove(oldId, out var device)) return Task.FromResult(false);
        device.Id = newId;
        device.Name = newId;
        Devices[newId] = device;
        return Task.FromResult(true);
    }

    public Task<IEnumerable<Device>> GetDevicesByProtocolAsync(Protocol protocol, CancellationToken cancellationToken = default)
        => Task.FromResult<IEnumerable<Device>>(Devices.Values.Where(d => d.Protocol == protocol).ToList());

    public Task<DeviceCounts> GetCountsAsync(CancellationToken cancellationToken = default)
    {
        var all = Devices.Values.ToList();
        var byProto = all
            .Where(d => d.Protocol != Protocol.Unknown)
            .GroupBy(d => d.Protocol)
            .ToDictionary(g => g.Key, g => g.Count());
        return Task.FromResult(new DeviceCounts(
            all.Count,
            all.Count(d => d.Status == DeviceStatus.Online),
            byProto));
    }

    public Task UpdateDeviceStateAsync(string deviceId, Dictionary<string, object> state, CancellationToken cancellationToken = default)
    {
        // Mirror PostgresDeviceService: merge the delta into device.State and
        // stamp the live fields. A no-op here made tests that exercised the
        // merge path look correct even when state never actually updated.
        if (!Devices.TryGetValue(deviceId, out var device))
        {
            return Task.CompletedTask;
        }
        foreach (var kvp in state) device.State[kvp.Key] = kvp.Value;
        device.LastSeen = DateTime.UtcNow;
        device.Status = DeviceStatus.Online;
        return Task.CompletedTask;
    }

    public Task UpsertDeviceStateAsync(string deviceId, string name, Protocol protocol, Dictionary<string, object> state, CancellationToken cancellationToken = default)
    {
        if (!Devices.TryGetValue(deviceId, out var device))
        {
            device = new Device
            {
                Id = deviceId,
                Name = name,
                Protocol = protocol,
                Status = DeviceStatus.Online,
                CreatedAt = DateTime.UtcNow,
            };
            Devices[deviceId] = device;
        }
        device.LastSeen = DateTime.UtcNow;
        device.Status = DeviceStatus.Online;
        foreach (var kvp in state) device.State[kvp.Key] = kvp.Value;
        return Task.CompletedTask;
    }

    public Task UpdateDeviceStatusAsync(string deviceId, DeviceStatus status, CancellationToken cancellationToken = default)
    {
        if (Devices.TryGetValue(deviceId, out var device))
        {
            var previous = device.Status;
            device.Status = status;
            if (previous != status)
            {
                RaiseAvailability(deviceId, previous, status);
            }
        }
        return Task.CompletedTask;
    }
}
