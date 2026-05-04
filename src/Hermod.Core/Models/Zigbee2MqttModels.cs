using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hermod.Core.Models;

// DTOs mapping the Zigbee2MQTT bridge API. JSON property names follow the
// upstream contract; do not rename fields without matching the bridge's
// serialization format.

#region Device Models

/// <summary>Device entry from <c>zigbee/bridge/devices</c>.</summary>
public class Zigbee2MqttDevice
{
    /// <summary>IEEE 802.15.4 EUI-64 address; permanent device identifier.</summary>
    [JsonPropertyName("ieee_address")]
    public string IeeeAddress { get; set; } = string.Empty;

    /// <summary>Operator-facing device name. Can be renamed via the bridge.</summary>
    [JsonPropertyName("friendly_name")]
    public string FriendlyName { get; set; } = string.Empty;

    /// <summary>"Coordinator", "Router", or "EndDevice".</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>Short network address assigned when the device joined.</summary>
    [JsonPropertyName("network_address")]
    public int NetworkAddress { get; set; }

    /// <summary>True when the device model is recognized by zigbee-herdsman-converters.</summary>
    [JsonPropertyName("supported")]
    public bool Supported { get; set; }

    /// <summary>True when the device is administratively disabled at the bridge.</summary>
    [JsonPropertyName("disabled")]
    public bool Disabled { get; set; }

    /// <summary>Free-form description from the bridge's configuration.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>"Battery" or "Mains" as reported by the device.</summary>
    [JsonPropertyName("power_source")]
    public string? PowerSource { get; set; }

    /// <summary>Manufacturer-specific model identifier.</summary>
    [JsonPropertyName("model_id")]
    public string? ModelId { get; set; }

    /// <summary>Firmware date code.</summary>
    [JsonPropertyName("date_code")]
    public string? DateCode { get; set; }

    /// <summary>True when the bridge successfully completed the interview for this device.</summary>
    [JsonPropertyName("interview_completed")]
    public bool InterviewCompleted { get; set; }

    /// <summary>True while the interview is in progress.</summary>
    [JsonPropertyName("interviewing")]
    public bool Interviewing { get; set; }

    /// <summary>Optional interview-state string for long-running interview flows.</summary>
    [JsonPropertyName("interview_state")]
    public string? InterviewState { get; set; }

    /// <summary>Manufacturer/model/exposes metadata from the bridge's device database.</summary>
    [JsonPropertyName("definition")]
    public Zigbee2MqttDeviceDefinition? Definition { get; set; }

    /// <summary>Endpoint-indexed bindings and reportings, when the bridge exposes them.</summary>
    [JsonPropertyName("endpoints")]
    public Dictionary<string, Zigbee2MqttEndpoint>? Endpoints { get; set; }
}

/// <summary>Manufacturer/model info from Zigbee2MQTT's device database.</summary>
public class Zigbee2MqttDeviceDefinition
{
    /// <summary>Canonical model identifier.</summary>
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    /// <summary>Vendor name.</summary>
    [JsonPropertyName("vendor")]
    public string? Vendor { get; set; }

    /// <summary>Operator-facing description.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>Definition source (<c>"native"</c> or an external converter).</summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    /// <summary>List of exposed capabilities (sensors, controls, etc.).</summary>
    [JsonPropertyName("exposes")]
    public List<Zigbee2MqttExpose>? Exposes { get; set; }

    /// <summary>Configurable options surfaced by the converter.</summary>
    [JsonPropertyName("options")]
    public List<Zigbee2MqttOption>? Options { get; set; }
}

/// <summary>Single endpoint within a Zigbee device.</summary>
public class Zigbee2MqttEndpoint
{
    /// <summary>Bindings advertised by the endpoint.</summary>
    [JsonPropertyName("bindings")]
    public List<Zigbee2MqttBinding>? Bindings { get; set; }

    /// <summary>Configured attribute-reporting entries.</summary>
    [JsonPropertyName("configured_reportings")]
    public List<Zigbee2MqttReporting>? ConfiguredReportings { get; set; }

    /// <summary>Input and output clusters the endpoint supports.</summary>
    [JsonPropertyName("clusters")]
    public Zigbee2MqttClusters? Clusters { get; set; }
}

/// <summary>Binding entry from <see cref="Zigbee2MqttEndpoint"/>.</summary>
public class Zigbee2MqttBinding
{
    /// <summary>ZCL cluster bound between source and target.</summary>
    [JsonPropertyName("cluster")]
    public string Cluster { get; set; } = string.Empty;

    /// <summary>Binding destination (device endpoint or group id).</summary>
    [JsonPropertyName("target")]
    public Zigbee2MqttBindingTarget? Target { get; set; }
}

/// <summary>Destination descriptor for a <see cref="Zigbee2MqttBinding"/>.</summary>
public class Zigbee2MqttBindingTarget
{
    /// <summary><c>"endpoint"</c> or <c>"group"</c>.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>Target endpoint id when <see cref="Type"/> = <c>"endpoint"</c>.</summary>
    [JsonPropertyName("endpoint")]
    public int? Endpoint { get; set; }

    /// <summary>Target device EUI-64 when <see cref="Type"/> = <c>"endpoint"</c>.</summary>
    [JsonPropertyName("ieee_address")]
    public string? IeeeAddress { get; set; }

    /// <summary>Target group id when <see cref="Type"/> = <c>"group"</c>.</summary>
    [JsonPropertyName("id")]
    public int? Id { get; set; }
}

/// <summary>Attribute-reporting configuration.</summary>
public class Zigbee2MqttReporting
{
    /// <summary>ZCL cluster.</summary>
    [JsonPropertyName("cluster")]
    public string Cluster { get; set; } = string.Empty;

    /// <summary>Reported attribute name.</summary>
    [JsonPropertyName("attribute")]
    public string Attribute { get; set; } = string.Empty;

    /// <summary>Minimum seconds between reports.</summary>
    [JsonPropertyName("minimum_report_interval")]
    public int MinimumReportInterval { get; set; }

    /// <summary>Maximum seconds between reports (device must report at least this often even without change).</summary>
    [JsonPropertyName("maximum_report_interval")]
    public int MaximumReportInterval { get; set; }

    /// <summary>Minimum value delta that triggers an unsolicited report.</summary>
    [JsonPropertyName("reportable_change")]
    public int ReportableChange { get; set; }
}

/// <summary>Input/output ZCL cluster lists for an endpoint.</summary>
public class Zigbee2MqttClusters
{
    /// <summary>Input cluster ids the endpoint accepts.</summary>
    [JsonPropertyName("input")]
    public List<string>? Input { get; set; }

    /// <summary>Output cluster ids the endpoint emits.</summary>
    [JsonPropertyName("output")]
    public List<string>? Output { get; set; }

    /// <summary>Scenes the endpoint advertises.</summary>
    [JsonPropertyName("scenes")]
    public List<object>? Scenes { get; set; }
}

/// <summary>Device capability exposure.</summary>
public class Zigbee2MqttExpose
{
    /// <summary>Expose category (<c>"numeric"</c>, <c>"binary"</c>, <c>"enum"</c>, <c>"light"</c>, etc.).</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>Descriptive name (e.g. <c>"temperature"</c>).</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Property name on the device's state payload.</summary>
    [JsonPropertyName("property")]
    public string? Property { get; set; }

    /// <summary>Bit mask: 1=publish, 2=set, 4=get.</summary>
    [JsonPropertyName("access")]
    public int Access { get; set; }

    /// <summary>Human-readable description.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>Lower bound for numeric exposures.</summary>
    [JsonPropertyName("value_min")]
    public double? ValueMin { get; set; }

    /// <summary>Upper bound for numeric exposures.</summary>
    [JsonPropertyName("value_max")]
    public double? ValueMax { get; set; }

    /// <summary>Unit string (<c>"°C"</c>, <c>"%"</c>, etc.).</summary>
    [JsonPropertyName("unit")]
    public string? Unit { get; set; }

    /// <summary>Allowed values for enum exposures.</summary>
    [JsonPropertyName("values")]
    public List<string>? Values { get; set; }

    /// <summary>Nested feature exposures for composite exposures (e.g. <c>light</c>).</summary>
    [JsonPropertyName("features")]
    public List<Zigbee2MqttExpose>? Features { get; set; }
}

/// <summary>Configurable option entry on a <see cref="Zigbee2MqttDeviceDefinition"/>.</summary>
public class Zigbee2MqttOption
{
    /// <summary>Option name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Option type (<c>"numeric"</c>, <c>"binary"</c>, etc.).</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>Human-readable description.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

#endregion

#region Bridge Models

/// <summary>Bridge state from <c>zigbee/bridge/state</c>.</summary>
public class Zigbee2MqttBridgeState
{
    /// <summary>"online" or "offline".</summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    /// <summary>True when <see cref="State"/> equals <c>"online"</c> (case-insensitive).</summary>
    public bool IsOnline => State.Equals("online", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Bridge info from <c>zigbee/bridge/info</c>.</summary>
public class Zigbee2MqttBridgeInfo
{
    /// <summary>Zigbee2MQTT version string.</summary>
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    /// <summary>Git commit hash of the running bridge build.</summary>
    [JsonPropertyName("commit")]
    public string? Commit { get; set; }

    /// <summary>Bridge configuration slice relevant to the dashboard.</summary>
    [JsonPropertyName("config")]
    public Zigbee2MqttConfig? Config { get; set; }

    /// <summary>Coordinator chip metadata.</summary>
    [JsonPropertyName("coordinator")]
    public Zigbee2MqttCoordinator? Coordinator { get; set; }

    /// <summary>PAN/channel identifiers.</summary>
    [JsonPropertyName("network")]
    public Zigbee2MqttNetwork? Network { get; set; }

    /// <summary>Bridge log level (<c>"debug"</c> ... <c>"error"</c>).</summary>
    [JsonPropertyName("log_level")]
    public string? LogLevel { get; set; }

    /// <summary>True when the bridge is currently accepting new device joins.</summary>
    [JsonPropertyName("permit_join")]
    public bool PermitJoin { get; set; }

    /// <summary>Unix-seconds timestamp at which the current permit-join window ends.</summary>
    [JsonPropertyName("permit_join_end")]
    public long? PermitJoinEnd { get; set; }

    /// <summary>True when the bridge requires a restart for configuration to take effect.</summary>
    [JsonPropertyName("restart_required")]
    public bool RestartRequired { get; set; }

    /// <summary>zigbee-herdsman version info.</summary>
    [JsonPropertyName("zigbee_herdsman")]
    public Zigbee2MqttVersionInfo? ZigbeeHerdsman { get; set; }

    /// <summary>zigbee-herdsman-converters version info.</summary>
    [JsonPropertyName("zigbee_herdsman_converters")]
    public Zigbee2MqttVersionInfo? ZigbeeHerdsmanConverters { get; set; }
}

/// <summary>Subset of bridge configuration surfaced in <see cref="Zigbee2MqttBridgeInfo"/>.</summary>
public class Zigbee2MqttConfig
{
    /// <summary>True when the bridge is accepting joins by default.</summary>
    [JsonPropertyName("permit_join")]
    public bool PermitJoin { get; set; }

    /// <summary>
    /// Raw JSON node for the <c>homeassistant</c> field, because z2m 1.35+
    /// sends an object (<c>{"enabled":true,"discovery_topic":"..."}</c>)
    /// while older builds sent a bare boolean. Read through
    /// <see cref="HomeAssistant"/> for the normalised enabled flag.
    /// </summary>
    [JsonPropertyName("homeassistant")]
    public JsonElement? HomeAssistantRaw { get; set; }

    /// <summary>True when Home Assistant discovery is enabled.</summary>
    [JsonIgnore]
    public bool HomeAssistant => HomeAssistantRaw switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        { ValueKind: JsonValueKind.Object } raw =>
            raw.TryGetProperty("enabled", out var enabled) &&
            enabled.ValueKind == JsonValueKind.True,
        _ => false,
    };
}

/// <summary>Coordinator chip metadata.</summary>
public class Zigbee2MqttCoordinator
{
    /// <summary>Coordinator chip family (<c>"zStack"</c>, <c>"EZSP"</c>, etc.).</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>Coordinator EUI-64.</summary>
    [JsonPropertyName("ieee_address")]
    public string? IeeeAddress { get; set; }

    /// <summary>Firmware revision metadata.</summary>
    [JsonPropertyName("meta")]
    public Zigbee2MqttCoordinatorMeta? Meta { get; set; }
}

/// <summary>Firmware revision metadata for a <see cref="Zigbee2MqttCoordinator"/>.</summary>
public class Zigbee2MqttCoordinatorMeta
{
    /// <summary>Firmware revision. Modern Zigbee2MQTT (ember/EmberZNet)
    /// reports this as a git SHA (string); older CC26xx firmware reports
    /// an integer. Modelled as string to accept both; callers that want
    /// a numeric parse should try-parse explicitly.</summary>
    [JsonPropertyName("revision")]
    [JsonConverter(typeof(NumberOrStringConverter))]
    public string? Revision { get; set; }

    /// <summary>Transport revision. Same number-or-string caveat as
    /// <see cref="Revision"/>.</summary>
    [JsonPropertyName("transportrev")]
    [JsonConverter(typeof(NumberOrStringConverter))]
    public string? TransportRev { get; set; }

    /// <summary>Product identifier.</summary>
    [JsonPropertyName("product")]
    [JsonConverter(typeof(NumberOrStringConverter))]
    public string? Product { get; set; }

    /// <summary>Major release number.</summary>
    [JsonPropertyName("majorrel")]
    [JsonConverter(typeof(NumberOrStringConverter))]
    public string? MajorRel { get; set; }

    /// <summary>Minor release number.</summary>
    [JsonPropertyName("minorrel")]
    [JsonConverter(typeof(NumberOrStringConverter))]
    public string? MinorRel { get; set; }

    /// <summary>Maintenance release number.</summary>
    [JsonPropertyName("maintrel")]
    [JsonConverter(typeof(NumberOrStringConverter))]
    public string? MaintRel { get; set; }
}

/// <summary>Accepts either a JSON number or a JSON string and returns the
/// canonical string form. Z2M's <c>coordinator.meta.*</c> fields flip
/// between int and git-SHA-string depending on the stick firmware —
/// modelling them as <see cref="string"/> behind this converter means
/// the deserializer stops crashing on EmberZNet builds.</summary>
internal sealed class NumberOrStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number when reader.TryGetInt64(out var i) => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonTokenType.Number => reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonTokenType.Null => null,
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            _ => reader.GetString()
        };
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value);
    }
}

/// <summary>Zigbee PAN/channel identifiers.</summary>
public class Zigbee2MqttNetwork
{
    /// <summary>Active radio channel.</summary>
    [JsonPropertyName("channel")]
    public int Channel { get; set; }

    /// <summary>16-bit PAN ID.</summary>
    [JsonPropertyName("pan_id")]
    public int PanId { get; set; }

    /// <summary>64-bit extended PAN ID. Zigbee2MQTT emits this as a
    /// little-endian byte array on zstack firmware (<c>[212,231,...]</c>)
    /// and as a hex string on EmberZNet firmware
    /// (<c>"0xFB05A27E04D4E7D4"</c>). Modelled as <see cref="string"/>
    /// (always the canonical hex form after normalisation) so the
    /// deserializer accepts both.</summary>
    [JsonPropertyName("extended_pan_id")]
    [JsonConverter(typeof(ExtendedPanIdConverter))]
    public string? ExtendedPanId { get; set; }
}

/// <summary>Reads <c>extended_pan_id</c> as either a hex string or a
/// little-endian byte array and normalises to the canonical
/// <c>0x</c>-prefixed uppercase hex form.</summary>
internal sealed class ExtendedPanIdConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType == JsonTokenType.String) return reader.GetString();
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var bytes = new List<byte>(8);
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType == JsonTokenType.Number && reader.TryGetByte(out var b))
                {
                    bytes.Add(b);
                }
            }
            // z2m emits the array little-endian; render as canonical
            // big-endian hex to match user-facing displays.
            var sb = new System.Text.StringBuilder("0x", 2 + bytes.Count * 2);
            for (int i = bytes.Count - 1; i >= 0; i--)
            {
                sb.Append(bytes[i].ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
        // Fallback: unknown token type — skip.
        reader.Skip();
        return null;
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value);
    }
}

/// <summary>Simple version-string wrapper.</summary>
public class Zigbee2MqttVersionInfo
{
    /// <summary>Version string.</summary>
    [JsonPropertyName("version")]
    public string? Version { get; set; }
}

#endregion

#region Event Models

/// <summary>Bridge event from <c>zigbee/bridge/event</c>.</summary>
public class Zigbee2MqttBridgeEvent
{
    /// <summary>Event discriminator (<c>"device_joined"</c>, <c>"device_leave"</c>, <c>"device_interview"</c>, <c>"device_announce"</c>).</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>Event payload.</summary>
    [JsonPropertyName("data")]
    public Zigbee2MqttEventData? Data { get; set; }
}

/// <summary>Payload shared across bridge event types.</summary>
public class Zigbee2MqttEventData
{
    /// <summary>Target device EUI-64.</summary>
    [JsonPropertyName("ieee_address")]
    public string? IeeeAddress { get; set; }

    /// <summary>Target device friendly name.</summary>
    [JsonPropertyName("friendly_name")]
    public string? FriendlyName { get; set; }

    /// <summary>Event-specific status string (e.g. interview phase).</summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    /// <summary>Populated on join/interview events when the device model is recognized.</summary>
    [JsonPropertyName("supported")]
    public bool? Supported { get; set; }

    /// <summary>Populated when the bridge resolved the device to its database entry.</summary>
    [JsonPropertyName("definition")]
    public Zigbee2MqttDeviceDefinition? Definition { get; set; }
}

/// <summary>String constants for Zigbee2MQTT bridge event <c>type</c> field.</summary>
public static class Zigbee2MqttEventTypes
{
    /// <summary><c>"device_joined"</c>.</summary>
    public const string DeviceJoined = "device_joined";

    /// <summary><c>"device_announce"</c>.</summary>
    public const string DeviceAnnounce = "device_announce";

    /// <summary><c>"device_interview"</c>.</summary>
    public const string DeviceInterview = "device_interview";

    /// <summary><c>"device_leave"</c>.</summary>
    public const string DeviceLeave = "device_leave";

    /// <summary>String constants for the interview <c>status</c> sub-field.</summary>
    public static class InterviewStatus
    {
        /// <summary><c>"started"</c>.</summary>
        public const string Started = "started";

        /// <summary><c>"successful"</c>.</summary>
        public const string Successful = "successful";

        /// <summary><c>"failed"</c>.</summary>
        public const string Failed = "failed";
    }
}

#endregion

#region Logging Models

/// <summary>Log message from <c>zigbee/bridge/logging</c>.</summary>
public class Zigbee2MqttLogMessage
{
    /// <summary>Log level (<c>"debug"</c>, <c>"info"</c>, <c>"warning"</c>, <c>"error"</c>).</summary>
    [JsonPropertyName("level")]
    public string Level { get; set; } = string.Empty;

    /// <summary>Formatted log message body.</summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>Bridge-internal namespace (subsystem) that emitted the message.</summary>
    [JsonPropertyName("namespace")]
    public string Namespace { get; set; } = string.Empty;
}

/// <summary>String constants for <see cref="Zigbee2MqttLogMessage.Level"/>.</summary>
public static class Zigbee2MqttLogLevels
{
    /// <summary><c>"debug"</c>.</summary>
    public const string Debug = "debug";

    /// <summary><c>"info"</c>.</summary>
    public const string Info = "info";

    /// <summary><c>"warning"</c>.</summary>
    public const string Warning = "warning";

    /// <summary><c>"error"</c>.</summary>
    public const string Error = "error";
}

#endregion

#region Group Models

/// <summary>Group from <c>zigbee/bridge/groups</c>.</summary>
public class Zigbee2MqttGroup
{
    /// <summary>Group id.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Operator-facing group name.</summary>
    [JsonPropertyName("friendly_name")]
    public string FriendlyName { get; set; } = string.Empty;

    /// <summary>Scenes stored on the group.</summary>
    [JsonPropertyName("scenes")]
    public List<Zigbee2MqttScene>? Scenes { get; set; }

    /// <summary>Device endpoints that belong to the group.</summary>
    [JsonPropertyName("members")]
    public List<Zigbee2MqttGroupMember>? Members { get; set; }
}

/// <summary>Scene entry stored on a <see cref="Zigbee2MqttGroup"/>.</summary>
public class Zigbee2MqttScene
{
    /// <summary>Scene id.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Scene name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

/// <summary>Group membership tuple (device + endpoint).</summary>
public class Zigbee2MqttGroupMember
{
    /// <summary>Device EUI-64.</summary>
    [JsonPropertyName("ieee_address")]
    public string IeeeAddress { get; set; } = string.Empty;

    /// <summary>Endpoint id within the device that belongs to the group.</summary>
    [JsonPropertyName("endpoint")]
    public int Endpoint { get; set; }
}

#endregion

#region Request/Response Models

/// <summary>Envelope for replies on <c>zigbee/bridge/response/*</c>.</summary>
/// <typeparam name="T">Type of the <c>data</c> payload when the request succeeds.</typeparam>
public class Zigbee2MqttResponse<T>
{
    /// <summary>Response payload when <see cref="Status"/> indicates success.</summary>
    [JsonPropertyName("data")]
    public T? Data { get; set; }

    /// <summary>Status string (<c>"ok"</c> on success).</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    /// <summary>Error message when <see cref="Status"/> is not <c>"ok"</c>.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>Caller-supplied correlation id echoed back by the bridge.</summary>
    [JsonPropertyName("transaction")]
    public string? Transaction { get; set; }

    /// <summary>True when the bridge reported <c>"ok"</c>.</summary>
    public bool IsSuccess => Status.Equals("ok", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Request body for <c>bridge/request/permit_join</c>.</summary>
public class Zigbee2MqttPermitJoinRequest
{
    /// <summary>Seconds to keep the join window open.</summary>
    [JsonPropertyName("time")]
    public int Time { get; set; }

    /// <summary>Optional router friendly name to gate the join through.</summary>
    [JsonPropertyName("device")]
    public string? Device { get; set; }

    /// <summary>Caller-supplied correlation id.</summary>
    [JsonPropertyName("transaction")]
    public string? Transaction { get; set; }
}

/// <summary>Response payload for <c>bridge/request/permit_join</c>.</summary>
public class Zigbee2MqttPermitJoinResponse
{
    /// <summary>Granted permit-join window in seconds.</summary>
    [JsonPropertyName("time")]
    public int Time { get; set; }
}

/// <summary>Response payload for <c>bridge/request/health_check</c>.</summary>
public class Zigbee2MqttHealthCheckResponse
{
    /// <summary>True when the bridge reports itself healthy.</summary>
    [JsonPropertyName("healthy")]
    public bool Healthy { get; set; }
}

/// <summary>Request body that targets a single device by id.</summary>
public class Zigbee2MqttDeviceRequest
{
    /// <summary>Target device friendly name or EUI-64.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Caller-supplied correlation id.</summary>
    [JsonPropertyName("transaction")]
    public string? Transaction { get; set; }
}

/// <summary>Request body for <c>bridge/request/device/remove</c>.</summary>
public class Zigbee2MqttDeviceRemoveRequest : Zigbee2MqttDeviceRequest
{
    /// <summary>Skip the graceful ZDO leave and forget the device anyway.</summary>
    [JsonPropertyName("force")]
    public bool Force { get; set; }

    /// <summary>Add the device to the deny-list so it cannot rejoin.</summary>
    [JsonPropertyName("block")]
    public bool Block { get; set; }
}

/// <summary>Request body for <c>bridge/request/device/rename</c>.</summary>
public class Zigbee2MqttDeviceRenameRequest
{
    /// <summary>Current friendly name.</summary>
    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    /// <summary>New friendly name.</summary>
    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;

    /// <summary>When true, also updates the Home Assistant entity id if HA integration is enabled.</summary>
    [JsonPropertyName("homeassistant_rename")]
    public bool HomeAssistantRename { get; set; }

    /// <summary>Caller-supplied correlation id.</summary>
    [JsonPropertyName("transaction")]
    public string? Transaction { get; set; }
}

/// <summary>Request body for <c>bridge/request/device/options</c>.</summary>
public class Zigbee2MqttDeviceOptionsRequest
{
    /// <summary>Target device id.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Option key/value writes.</summary>
    [JsonPropertyName("options")]
    public Dictionary<string, object> Options { get; set; } = new();

    /// <summary>Caller-supplied correlation id.</summary>
    [JsonPropertyName("transaction")]
    public string? Transaction { get; set; }
}

/// <summary>Request body for <c>bridge/request/networkmap</c>.</summary>
public class Zigbee2MqttNetworkMapRequest
{
    /// <summary>"raw", "graphviz", or "plantuml". Default "graphviz".</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "graphviz";

    /// <summary>When true, include routing table edges in the map.</summary>
    [JsonPropertyName("routes")]
    public bool Routes { get; set; }

    /// <summary>Caller-supplied correlation id.</summary>
    [JsonPropertyName("transaction")]
    public string? Transaction { get; set; }
}

/// <summary>Response payload for <c>bridge/request/networkmap</c>.</summary>
public class Zigbee2MqttNetworkMapResponse
{
    /// <summary>Echoed request type.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>Rendered map body (DOT/PlantUML/raw JSON depending on <see cref="Type"/>).</summary>
    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;

    /// <summary>True when routing edges are included.</summary>
    [JsonPropertyName("routes")]
    public bool Routes { get; set; }
}

/// <summary>Request body for <c>bridge/request/group/add</c>.</summary>
public class Zigbee2MqttGroupAddRequest
{
    /// <summary>Optional group id; null = let the bridge allocate.</summary>
    [JsonPropertyName("id")]
    public int? Id { get; set; }

    /// <summary>Operator-facing group name.</summary>
    [JsonPropertyName("friendly_name")]
    public string FriendlyName { get; set; } = string.Empty;

    /// <summary>Caller-supplied correlation id.</summary>
    [JsonPropertyName("transaction")]
    public string? Transaction { get; set; }
}

/// <summary>Request body for <c>bridge/request/group/members/{add,remove}</c>.</summary>
public class Zigbee2MqttGroupMemberRequest
{
    /// <summary>Group friendly name.</summary>
    [JsonPropertyName("group")]
    public string Group { get; set; } = string.Empty;

    /// <summary>Device friendly name.</summary>
    [JsonPropertyName("device")]
    public string Device { get; set; } = string.Empty;

    /// <summary>Optional endpoint id; null = primary endpoint.</summary>
    [JsonPropertyName("endpoint")]
    public int? Endpoint { get; set; }

    /// <summary>Caller-supplied correlation id.</summary>
    [JsonPropertyName("transaction")]
    public string? Transaction { get; set; }
}

#endregion

#region Device State Models

/// <summary>Common device state properties carried on every Z2M device payload.</summary>
public class Zigbee2MqttDeviceState
{
    /// <summary>Link quality indicator (0-255).</summary>
    [JsonPropertyName("linkquality")]
    public int? LinkQuality { get; set; }

    /// <summary>Battery percentage (0-100) when the device is battery-powered.</summary>
    [JsonPropertyName("battery")]
    public int? Battery { get; set; }

    /// <summary>Battery voltage in millivolts when the device reports it.</summary>
    [JsonPropertyName("voltage")]
    public int? Voltage { get; set; }

    /// <summary>ISO-8601 timestamp of the last radio contact, when the bridge tracks it.</summary>
    [JsonPropertyName("last_seen")]
    public string? LastSeen { get; set; }
}

/// <summary>Light-specific state overlay.</summary>
public class Zigbee2MqttLightState : Zigbee2MqttDeviceState
{
    /// <summary><c>"ON"</c> or <c>"OFF"</c>.</summary>
    [JsonPropertyName("state")]
    public string? State { get; set; }

    /// <summary>Brightness on the Zigbee 0-254 scale.</summary>
    [JsonPropertyName("brightness")]
    public int? Brightness { get; set; }

    /// <summary>Colour temperature in mireds.</summary>
    [JsonPropertyName("color_temp")]
    public int? ColorTemp { get; set; }

    /// <summary>CIE xy / HS colour components.</summary>
    [JsonPropertyName("color")]
    public Zigbee2MqttColor? Color { get; set; }

    /// <summary>Active colour mode (<c>"xy"</c>, <c>"hs"</c>, <c>"color_temp"</c>).</summary>
    [JsonPropertyName("color_mode")]
    public string? ColorMode { get; set; }
}

/// <summary>Colour components reported by a light.</summary>
public class Zigbee2MqttColor
{
    /// <summary>CIE 1931 x (0-1).</summary>
    [JsonPropertyName("x")]
    public double? X { get; set; }

    /// <summary>CIE 1931 y (0-1).</summary>
    [JsonPropertyName("y")]
    public double? Y { get; set; }

    /// <summary>Hue angle (0-360).</summary>
    [JsonPropertyName("hue")]
    public int? Hue { get; set; }

    /// <summary>Saturation percentage (0-100).</summary>
    [JsonPropertyName("saturation")]
    public int? Saturation { get; set; }
}

/// <summary>Environmental-sensor state overlay.</summary>
public class Zigbee2MqttSensorState : Zigbee2MqttDeviceState
{
    /// <summary>Temperature in degrees Celsius.</summary>
    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    /// <summary>Relative humidity percentage.</summary>
    [JsonPropertyName("humidity")]
    public double? Humidity { get; set; }

    /// <summary>Atmospheric pressure in hectopascals.</summary>
    [JsonPropertyName("pressure")]
    public double? Pressure { get; set; }
}

/// <summary>Motion/occupancy sensor state overlay.</summary>
public class Zigbee2MqttMotionState : Zigbee2MqttDeviceState
{
    /// <summary>True when motion/occupancy is detected.</summary>
    [JsonPropertyName("occupancy")]
    public bool? Occupancy { get; set; }

    /// <summary>Raw illuminance reading.</summary>
    [JsonPropertyName("illuminance")]
    public int? Illuminance { get; set; }

    /// <summary>Illuminance in lux.</summary>
    [JsonPropertyName("illuminance_lux")]
    public int? IlluminanceLux { get; set; }
}

/// <summary>Door/window contact-sensor state overlay.</summary>
public class Zigbee2MqttContactState : Zigbee2MqttDeviceState
{
    /// <summary>True when the sensor's contact is closed.</summary>
    [JsonPropertyName("contact")]
    public bool? Contact { get; set; }
}

/// <summary>Plug/switch state overlay including power metering.</summary>
public class Zigbee2MqttSwitchState : Zigbee2MqttDeviceState
{
    /// <summary><c>"ON"</c> or <c>"OFF"</c>.</summary>
    [JsonPropertyName("state")]
    public string? State { get; set; }

    /// <summary>Instantaneous power draw in watts.</summary>
    [JsonPropertyName("power")]
    public double? Power { get; set; }

    /// <summary>Instantaneous current in amps.</summary>
    [JsonPropertyName("current")]
    public double? Current { get; set; }

    /// <summary>Cumulative energy consumption in kilowatt-hours.</summary>
    [JsonPropertyName("energy")]
    public double? Energy { get; set; }
}

/// <summary>Parsed payload of <c>{device}/availability</c>.</summary>
public class Zigbee2MqttAvailability
{
    /// <summary>"online" or "offline".</summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    /// <summary>True when <see cref="State"/> equals <c>"online"</c> (case-insensitive).</summary>
    public bool IsOnline => State.Equals("online", StringComparison.OrdinalIgnoreCase);
}

#endregion
