namespace Hermod.Core.Mqtt;

/// <summary>Topic-string constants and helpers for the Zigbee2MQTT contract.</summary>
public static class Zigbee2MqttTopics
{
    /// <summary>
    /// Protocol-name-based prefix: zigbee traffic lives under <c>zigbee/</c>,
    /// lora under <c>lora/</c>, wifi under <c>wifi/</c>, bluetooth under
    /// <c>bluetooth/</c>. z2m's <c>configuration.yaml</c> <c>mqtt.base_topic</c>
    /// must be kept in sync with this.
    /// </summary>
    public const string BaseTopic = "zigbee";

    /// <summary>Retained/event topics published under <c>zigbee/bridge/*</c>.</summary>
    public static class Bridge
    {
        /// <summary>Bridge online/offline state.</summary>
        public const string State = BaseTopic + "/bridge/state";

        /// <summary>Bridge info payload (version, coordinator, network).</summary>
        public const string Info = BaseTopic + "/bridge/info";

        /// <summary>Full device inventory dump.</summary>
        public const string Devices = BaseTopic + "/bridge/devices";

        /// <summary>Full group inventory dump.</summary>
        public const string Groups = BaseTopic + "/bridge/groups";

        /// <summary>Bridge-scope events (joined/left/interview).</summary>
        public const string Event = BaseTopic + "/bridge/event";

        /// <summary>Bridge log stream.</summary>
        public const string Logging = BaseTopic + "/bridge/logging";

        /// <summary>Bridge extensions (addons) topic.</summary>
        public const string Extensions = BaseTopic + "/bridge/extensions";

        /// <summary>Device definitions dump.</summary>
        public const string Definitions = BaseTopic + "/bridge/definitions";
    }

    /// <summary>Request topics published to <c>zigbee/bridge/request/*</c>.</summary>
    public static class Request
    {
        /// <summary>Request to open the join window.</summary>
        public const string PermitJoin = BaseTopic + "/bridge/request/permit_join";

        /// <summary>Request the bridge to run its health check.</summary>
        public const string HealthCheck = BaseTopic + "/bridge/request/health_check";

        /// <summary>Request the bridge to verify coordinator connectivity.</summary>
        public const string CoordinatorCheck = BaseTopic + "/bridge/request/coordinator_check";

        /// <summary>Request a bridge restart.</summary>
        public const string Restart = BaseTopic + "/bridge/request/restart";

        /// <summary>Request a network-map render.</summary>
        public const string NetworkMap = BaseTopic + "/bridge/request/networkmap";

        /// <summary>Request a database backup.</summary>
        public const string Backup = BaseTopic + "/bridge/request/backup";

        /// <summary>Write bridge-level options.</summary>
        public const string Options = BaseTopic + "/bridge/request/options";

        /// <summary>Device-scope bridge requests.</summary>
        public static class Device
        {
            /// <summary>Remove/forget a device.</summary>
            public const string Remove = BaseTopic + "/bridge/request/device/remove";

            /// <summary>Re-run the configure routine.</summary>
            public const string Configure = BaseTopic + "/bridge/request/device/configure";

            /// <summary>Re-run the interview.</summary>
            public const string Interview = BaseTopic + "/bridge/request/device/interview";

            /// <summary>Write device-level options.</summary>
            public const string Options = BaseTopic + "/bridge/request/device/options";

            /// <summary>Rename a device.</summary>
            public const string Rename = BaseTopic + "/bridge/request/device/rename";

            /// <summary>Bind a cluster to another endpoint/group.</summary>
            public const string Bind = BaseTopic + "/bridge/request/device/bind";

            /// <summary>Remove a binding.</summary>
            public const string Unbind = BaseTopic + "/bridge/request/device/unbind";
        }

        /// <summary>Group-scope bridge requests.</summary>
        public static class Group
        {
            /// <summary>Create a new group.</summary>
            public const string Add = BaseTopic + "/bridge/request/group/add";

            /// <summary>Remove a group.</summary>
            public const string Remove = BaseTopic + "/bridge/request/group/remove";

            /// <summary>Rename a group.</summary>
            public const string Rename = BaseTopic + "/bridge/request/group/rename";

            /// <summary>Write group-level options.</summary>
            public const string Options = BaseTopic + "/bridge/request/group/options";

            /// <summary>Add a member to a group.</summary>
            public const string MembersAdd = BaseTopic + "/bridge/request/group/members/add";

            /// <summary>Remove a member from a group.</summary>
            public const string MembersRemove = BaseTopic + "/bridge/request/group/members/remove";
        }

        /// <summary>Prefix every bridge request topic shares.</summary>
        public const string BridgeRequestPrefix = BaseTopic + "/bridge/request/";

        /// <summary>Prefix every bridge response topic shares.</summary>
        public const string BridgeResponsePrefix = BaseTopic + "/bridge/response/";
    }

    /// <summary>Returns the retained state topic for a device.</summary>
    public static string DeviceState(string friendlyName) => $"{BaseTopic}/{friendlyName}";

    /// <summary>Returns the availability topic for a device.</summary>
    public static string DeviceAvailability(string friendlyName) => $"{BaseTopic}/{friendlyName}/availability";

    /// <summary>Returns the set topic for a device.</summary>
    public static string DeviceSet(string friendlyName) => $"{BaseTopic}/{friendlyName}/set";

    /// <summary>Returns the get topic for a device.</summary>
    public static string DeviceGet(string friendlyName) => $"{BaseTopic}/{friendlyName}/get";

    /// <summary>Flips a <c>/request/</c> topic into its matching <c>/response/</c> topic.</summary>
    /// <param name="requestTopic">Request topic to transform.</param>
    /// <returns>The corresponding response topic.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="requestTopic"/> is null.</exception>
    public static string GetResponseTopic(string requestTopic)
    {
        ArgumentNullException.ThrowIfNull(requestTopic);
        return requestTopic.Replace("/request/", "/response/", StringComparison.Ordinal);
    }

    /// <summary>True when <paramref name="topic"/> sits under <c>{BaseTopic}/bridge/</c>.</summary>
    /// <exception cref="ArgumentNullException">When <paramref name="topic"/> is null.</exception>
    public static bool IsBridgeTopic(string topic)
    {
        ArgumentNullException.ThrowIfNull(topic);
        return topic.StartsWith($"{BaseTopic}/bridge/", StringComparison.Ordinal);
    }

    /// <summary>True when <paramref name="topic"/> sits under <c>{BaseTopic}/</c> but is not a bridge topic.</summary>
    /// <exception cref="ArgumentNullException">When <paramref name="topic"/> is null.</exception>
    public static bool IsDeviceTopic(string topic)
    {
        ArgumentNullException.ThrowIfNull(topic);
        return topic.StartsWith($"{BaseTopic}/", StringComparison.Ordinal) && !IsBridgeTopic(topic);
    }

    /// <summary>
    /// Extracts the friendly device name from a <c>{BaseTopic}/...</c> device
    /// topic, delegating to <see cref="DeviceTopicParser.Parse"/>. Returns
    /// null for bridge or non-Z2M topics.
    /// </summary>
    public static string? ExtractDeviceName(string topic) =>
        IsDeviceTopic(topic) ? DeviceTopicParser.Parse(topic) : null;

    /// <summary>Classifies <paramref name="topic"/> into its Z2M semantic role.</summary>
    /// <exception cref="ArgumentNullException">When <paramref name="topic"/> is null.</exception>
    public static Zigbee2MqttTopicAction GetTopicAction(string topic)
    {
        ArgumentNullException.ThrowIfNull(topic);
        if (topic.EndsWith("/set", StringComparison.Ordinal)) return Zigbee2MqttTopicAction.Set;
        if (topic.EndsWith("/get", StringComparison.Ordinal)) return Zigbee2MqttTopicAction.Get;
        if (topic.EndsWith("/availability", StringComparison.Ordinal)) return Zigbee2MqttTopicAction.Availability;
        if (topic.Contains("/request/", StringComparison.Ordinal)) return Zigbee2MqttTopicAction.Request;
        if (topic.Contains("/response/", StringComparison.Ordinal)) return Zigbee2MqttTopicAction.Response;
        return Zigbee2MqttTopicAction.State;
    }
}

/// <summary>Semantic role of a Zigbee2MQTT topic.</summary>
public enum Zigbee2MqttTopicAction
{
    /// <summary>Retained state topic (<c>{BaseTopic}/{device}</c>).</summary>
    State,

    /// <summary>Write topic (<c>{BaseTopic}/{device}/set</c>).</summary>
    Set,

    /// <summary>Read topic (<c>{BaseTopic}/{device}/get</c>).</summary>
    Get,

    /// <summary>Availability topic (<c>{BaseTopic}/{device}/availability</c>).</summary>
    Availability,

    /// <summary>Bridge request topic (<c>{BaseTopic}/bridge/request/*</c>).</summary>
    Request,

    /// <summary>Bridge response topic (<c>{BaseTopic}/bridge/response/*</c>).</summary>
    Response
}
