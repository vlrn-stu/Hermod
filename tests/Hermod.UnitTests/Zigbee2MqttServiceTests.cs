using System.Text.Json;
using Hermod.Core.Interfaces;
using Hermod.Core.Models;
using Hermod.Core.Mqtt;
using Hermod.Infrastructure.Zigbee;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hermod.UnitTests;

/// <summary>
/// Pins the snapshot-swap behaviour of
/// <see cref="Zigbee2MqttService.ProcessBridgeDevices"/>. A previous
/// implementation cleared both device maps then repopulated, exposing
/// a null-during-update window. The current implementation builds fresh
/// maps off to the side, then swaps via Volatile.Write.
/// </summary>
public class Zigbee2MqttServiceTests
{
    private const string DevicesTopic = Zigbee2MqttTopics.Bridge.Devices;

    private static Zigbee2MqttService Build() =>
        new(new FakeMqttService(), NullLogger<Zigbee2MqttService>.Instance);

    private static string SerializeDevices(params (string friendly, string ieee)[] items)
    {
        var list = items.Select(x => new Dictionary<string, object?>
        {
            ["friendly_name"] = x.friendly,
            ["ieee_address"] = x.ieee,
        }).ToList();
        return JsonSerializer.Serialize(list);
    }

    [Fact]
    public void ProcessBridgeDevices_EmptyList_ClearsDeviceMaps()
    {
        var sut = Build();
        sut.ProcessMessage(new MqttMessage
        {
            Topic = DevicesTopic,
            Payload = SerializeDevices(
                ("lamp_kitchen", "0x001122334455"),
                ("motion_lr", "0x001122334456"))
        });
        Assert.Equal(2, sut.Devices.Count);

        sut.ProcessMessage(new MqttMessage
        {
            Topic = DevicesTopic,
            Payload = "[]"
        });
        Assert.Empty(sut.Devices);
        Assert.Null(sut.GetDevice("lamp_kitchen"));
        Assert.Null(sut.GetDeviceByIeee("0x001122334455"));
    }

    [Fact]
    public void ProcessBridgeDevices_SinglePayload_PopulatesBothMaps()
    {
        var sut = Build();
        sut.ProcessMessage(new MqttMessage
        {
            Topic = DevicesTopic,
            Payload = SerializeDevices(
                ("lamp_kitchen", "0x00aabbccdd"),
                ("motion_lr", "0x00aabbccee"))
        });

        Assert.Equal(2, sut.Devices.Count);
        Assert.NotNull(sut.GetDevice("lamp_kitchen"));
        Assert.NotNull(sut.GetDevice("motion_lr"));
        Assert.NotNull(sut.GetDeviceByIeee("0x00aabbccdd"));
        Assert.NotNull(sut.GetDeviceByIeee("0x00aabbccee"));
    }

    [Fact]
    public void ProcessBridgeDevices_ReplacementPayload_ReplacesOldDevices()
    {
        var sut = Build();
        sut.ProcessMessage(new MqttMessage
        {
            Topic = DevicesTopic,
            Payload = SerializeDevices(("old_device", "0x1"))
        });
        Assert.NotNull(sut.GetDevice("old_device"));

        sut.ProcessMessage(new MqttMessage
        {
            Topic = DevicesTopic,
            Payload = SerializeDevices(("new_device", "0x2"))
        });

        Assert.Null(sut.GetDevice("old_device"));
        Assert.Null(sut.GetDeviceByIeee("0x1"));
        Assert.NotNull(sut.GetDevice("new_device"));
        Assert.NotNull(sut.GetDeviceByIeee("0x2"));
        Assert.Single(sut.Devices);
    }

    [Fact]
    public void ProcessBridgeDevices_DeviceWithoutFriendlyName_IsSkippedForNameMap()
    {
        // Defensive: a device missing friendly_name still lands in the
        // IEEE map but is invisible to GetDevice(name).
        var sut = Build();
        var payload = JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object?> { ["ieee_address"] = "0xdeadbeef" }
        });
        sut.ProcessMessage(new MqttMessage { Topic = DevicesTopic, Payload = payload });

        Assert.NotNull(sut.GetDeviceByIeee("0xdeadbeef"));
        // `Devices` is projected from the name map, so an IEEE-only
        // device is invisible there. This mirrors the prior behaviour
        // before the snapshot-swap refactor.
        Assert.Empty(sut.Devices);
    }

    [Fact]
    public void ProcessBridgeDevices_GarbledJson_LeavesPreviousStateIntact()
    {
        // If a bad payload arrives, the regression here is that the
        // maps must NOT be cleared. The old implementation's Clear()
        // happened BEFORE the foreach, so any exception in the loop
        // would have left the maps partially or fully empty. The
        // snapshot-swap path never touches the live field until the
        // new map is fully built, so exceptions mid-build leave the
        // previous state exactly as it was.
        var sut = Build();
        sut.ProcessMessage(new MqttMessage
        {
            Topic = DevicesTopic,
            Payload = SerializeDevices(("surviving_device", "0xabc"))
        });
        Assert.NotNull(sut.GetDevice("surviving_device"));

        sut.ProcessMessage(new MqttMessage
        {
            Topic = DevicesTopic,
            Payload = "this is not valid json {{{"
        });

        // Previous state must still be visible.
        Assert.NotNull(sut.GetDevice("surviving_device"));
        Assert.Single(sut.Devices);
    }

    [Fact]
    public void ProcessBridgeDevices_FiresDevicesUpdatedWithReadOnlyView()
    {
        var sut = Build();
        IReadOnlyList<Zigbee2MqttDevice>? captured = null;
        sut.DevicesUpdated += (_, devices) => captured = devices;

        sut.ProcessMessage(new MqttMessage
        {
            Topic = DevicesTopic,
            Payload = SerializeDevices(("a", "0x1"), ("b", "0x2"))
        });

        Assert.NotNull(captured);
        Assert.Equal(2, captured.Count);
        // The event should hand out a READ-ONLY view so subscribers
        // cannot mutate our internal list. `List<T>.AsReadOnly()` returns
        // a `ReadOnlyCollection<T>` wrapper whose `IsReadOnly` is true.
        Assert.True(((ICollection<Zigbee2MqttDevice>)captured).IsReadOnly);
    }

    private const string GroupsTopic = Zigbee2MqttTopics.Bridge.Groups;

    private static string SerializeGroups(params (int id, string friendly)[] items)
    {
        var list = items.Select(x => new Dictionary<string, object?>
        {
            ["id"] = x.id,
            ["friendly_name"] = x.friendly,
        }).ToList();
        return JsonSerializer.Serialize(list);
    }

    [Fact]
    public void ProcessBridgeGroups_ReplacementPayload_SwapsAtomicallyAndReplacesOldGroups()
    {
        // Pins the groups counterpart of the devices snapshot-swap rule.
        // A previous implementation refreshed `_groups` via
        // Clear()+foreach, exposing an empty-map window. The snapshot-swap
        // fix builds a fresh dict off to the side, so a reader observes
        // either the full old map or the full new map.
        var sut = Build();

        sut.ProcessMessage(new MqttMessage
        {
            Topic = GroupsTopic,
            Payload = SerializeGroups((1, "bedroom_lights"), (2, "kitchen_lights"))
        });
        Assert.Equal(2, sut.Groups.Count);
        Assert.Contains(sut.Groups, g => g.FriendlyName == "bedroom_lights");
        Assert.Contains(sut.Groups, g => g.FriendlyName == "kitchen_lights");

        // Replacement: a group rename (group 1 becomes "bedroom_all") plus
        // removal of the kitchen group. The new payload must fully replace
        // the old state, not merge with it.
        sut.ProcessMessage(new MqttMessage
        {
            Topic = GroupsTopic,
            Payload = SerializeGroups((1, "bedroom_all"))
        });
        Assert.Single(sut.Groups);
        Assert.DoesNotContain(sut.Groups, g => g.FriendlyName == "bedroom_lights");
        Assert.DoesNotContain(sut.Groups, g => g.FriendlyName == "kitchen_lights");
        Assert.Contains(sut.Groups, g => g.FriendlyName == "bedroom_all");
    }

    [Fact]
    public void ProcessBridgeGroups_GarbledJson_LeavesPreviousStateIntact()
    {
        // Symmetric to the devices test: a bad payload must not wipe the
        // previous group state. Under a Clear+foreach implementation a
        // JsonException mid-deserialize would have left the dict cleared.
        // The snapshot-swap path never touches the live field until
        // deserialization and the new-map build both succeed.
        var sut = Build();
        sut.ProcessMessage(new MqttMessage
        {
            Topic = GroupsTopic,
            Payload = SerializeGroups((7, "surviving_group"))
        });
        Assert.Single(sut.Groups);

        sut.ProcessMessage(new MqttMessage
        {
            Topic = GroupsTopic,
            Payload = "this is not valid json {{{"
        });

        Assert.Single(sut.Groups);
        Assert.Contains(sut.Groups, g => g.FriendlyName == "surviving_group");
    }

    [Fact]
    public void ProcessBridgeGroups_FiresGroupsUpdatedWithReadOnlyView()
    {
        // Pins the read-only event contract. A previous implementation
        // of `GroupsUpdated?.Invoke(this, groups)` handed out the raw
        // `List<Zigbee2MqttGroup>` the subscriber could cast back to
        // `List<T>` and mutate. The current invoke uses `.AsReadOnly()`
        // so the subscriber's `IsReadOnly` check is true.
        var sut = Build();
        IReadOnlyList<Zigbee2MqttGroup>? captured = null;
        sut.GroupsUpdated += (_, groups) => captured = groups;

        sut.ProcessMessage(new MqttMessage
        {
            Topic = GroupsTopic,
            Payload = SerializeGroups((1, "g_one"), (2, "g_two"))
        });

        Assert.NotNull(captured);
        Assert.Equal(2, captured.Count);
        Assert.True(((ICollection<Zigbee2MqttGroup>)captured).IsReadOnly);
    }

    [Fact]
    public void GetDeviceState_UnknownDevice_ReturnsNull()
    {
        var sut = Build();
        Assert.Null(sut.GetDeviceState("never_seen"));
    }

    [Fact]
    public void GetDeviceState_ReturnsDefensiveCopy_MutatingCallerDoesNotAffectStoredState()
    {
        // Send a state update for a device, then fetch the dict, mutate
        // the fetched dict, and fetch again. The second fetch must NOT
        // carry the caller's mutation. A previous implementation of
        // GetDeviceState handed out the live reference.
        var sut = Build();
        sut.ProcessMessage(new MqttMessage
        {
            Topic = "zigbee/lamp_test",
            Payload = "{\"state\":\"ON\",\"brightness\":128}"
        });

        var first = sut.GetDeviceState("lamp_test");
        Assert.NotNull(first);
        Assert.Equal("ON", first["state"].ToString());

        // Mutate the returned dict. If the fix is in place this must not
        // bleed back into the service's internal state.
        first["brightness"] = 999;
        first["poisoned_key"] = "mallory";

        var second = sut.GetDeviceState("lamp_test");
        Assert.NotNull(second);
        Assert.DoesNotContain("poisoned_key", second);
        // The brightness from the second read should be the original 128
        // (json number, could be JsonElement or long depending on
        // deserializer), NOT the 999 that the caller set on the first
        // fetched copy.
        Assert.NotEqual("999", second["brightness"].ToString());
    }

    [Fact]
    public void GetDeviceState_SecondCall_ReturnsFreshCopyNotSameInstance()
    {
        // Defensive copy means every GetDeviceState call produces a new
        // dictionary instance. Two back-to-back calls must return two
        // different references even though the underlying stored state
        // has not changed.
        var sut = Build();
        sut.ProcessMessage(new MqttMessage
        {
            Topic = "zigbee/sensor_test",
            Payload = "{\"temp\":21}"
        });

        var a = sut.GetDeviceState("sensor_test");
        var b = sut.GetDeviceState("sensor_test");

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotSame(a, b);
    }

    [Fact]
    public void ProcessDeviceState_ImmutableMerge_PreservesOldKeysInLiveReader()
    {
        // Stronger regression guard: a reader that grabbed a snapshot
        // before a merge-style state update must NOT see the new keys
        // bleed into the old dict. The immutable merge path builds a
        // NEW dictionary on every update, so the old reference is
        // frozen from the reader's perspective.
        var sut = Build();
        sut.ProcessMessage(new MqttMessage
        {
            Topic = "zigbee/fridge_test",
            Payload = "{\"temp\":4}"
        });

        var beforeUpdate = sut.GetDeviceState("fridge_test");
        Assert.NotNull(beforeUpdate);
        Assert.Single(beforeUpdate!);

        // Second update adds a new key via merge.
        sut.ProcessMessage(new MqttMessage
        {
            Topic = "zigbee/fridge_test",
            Payload = "{\"humidity\":55}"
        });

        // The stale reference from before the update must still have
        // the single key; the immutable merge rebuild means the writer
        // never touched it. (A mutable-merge implementation would have
        // the merged humidity key bleed into the old dict.)
        Assert.Single(beforeUpdate);
        Assert.DoesNotContain("humidity", beforeUpdate);

        // A fresh read sees the merged state.
        var afterUpdate = sut.GetDeviceState("fridge_test");
        Assert.NotNull(afterUpdate);
        Assert.Equal(2, afterUpdate.Count);
        Assert.Contains("temp", afterUpdate);
        Assert.Contains("humidity", afterUpdate);
    }

    [Fact]
    public void GetDevice_IsCaseInsensitive()
    {
        // Pin the StringComparer.OrdinalIgnoreCase on the new Dictionary
        // so a friendly_name key lookup works with any casing (Z2M is
        // inconsistent between device announcements and command topics).
        var sut = Build();
        sut.ProcessMessage(new MqttMessage
        {
            Topic = DevicesTopic,
            Payload = SerializeDevices(("Kitchen_Lamp", "0x1"))
        });

        Assert.NotNull(sut.GetDevice("Kitchen_Lamp"));
        Assert.NotNull(sut.GetDevice("kitchen_lamp"));
        Assert.NotNull(sut.GetDevice("KITCHEN_LAMP"));
    }

    private sealed class FakeMqttService : IMqttService
    {
        public bool IsConnected => true;
        public event EventHandler<MqttMessage>? MessageReceived;
        public event EventHandler<bool>? ConnectionStateChanged;

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SubscribeAsync(string topic, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnsubscribeAsync(string topic, CancellationToken ct = default) => Task.CompletedTask;
        public Task PublishAsync(string topic, string payload, bool retain = false, int qos = 0, CancellationToken ct = default) => Task.CompletedTask;
        public IReadOnlyList<MqttMessage> GetMessageHistory() => Array.Empty<MqttMessage>();

        // Suppress "never used" warnings.
        private void _Unused() { MessageReceived?.Invoke(this, null!); ConnectionStateChanged?.Invoke(this, false); }
    }
}
