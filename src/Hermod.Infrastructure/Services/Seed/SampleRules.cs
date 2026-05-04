using Hermod.Core.Models.Rules;

namespace Hermod.Infrastructure.Services.Seed;

internal static class SampleRules
{
    public static IReadOnlyList<Rule> All { get; } = Build();

    private static List<Rule> Build()
    {
        var rules = new List<Rule>();
        rules.AddRange(BasicForwarders());
        rules.AddRange(ThresholdAlerts());
        rules.AddRange(MultiConditionRules());
        rules.AddRange(CrossProtocolBridges());
        rules.AddRange(DebouncedRules());
        rules.AddRange(StateChangeLoggers());
        rules.AddRange(PatternMatchRules());
        rules.AddRange(CrossTechAutomations());
        return rules;
    }

    // ── Basic forwarders ────────────────────────────────────────────

    private static IEnumerable<Rule> BasicForwarders()
    {
        yield return new Rule
        {
            Id = "rule-debug-passthrough",
            Name = "Debug All ZigBee",
            Description = "Forwards ALL ZigBee messages to debug topic for testing",
            Enabled = true,
            Priority = 50,
            Trigger = new RuleTrigger
            {
                TopicPattern = "zigbee/+",
                Type = TriggerType.OnMessage
            },
            Actions = new List<RuleAction>
            {
                new()
                {
                    Type = ActionType.Publish,
                    Topic = "hermod/debug/{{deviceName}}",
                    PassthroughPayload = true
                }
            },
            Tags = new List<string> { "debug", "testing", "zigbee" }
        };

        yield return new Rule
        {
            Id = "rule-payload-transform",
            Name = "Temperature Transform",
            Description = "Demonstrates payload substitution with {{source.property}} syntax",
            Enabled = true,
            Priority = 100,
            Trigger = new RuleTrigger
            {
                TopicPattern = "zigbee/+",
                Type = TriggerType.OnMessage
            },
            Conditions = new RuleConditionGroup
            {
                Logic = LogicOperator.All,
                Conditions = new List<RuleCondition>
                {
                    new() { Property = "temperature", Operator = ComparisonOperator.Exists }
                }
            },
            Actions = new List<RuleAction>
            {
                new()
                {
                    Type = ActionType.Publish,
                    Topic = "wifi/display/{{deviceName}}/temperature",
                    Payload = new Dictionary<string, object>
                    {
                        { "value", "{{source.temperature}}" },
                        { "unit", "celsius" },
                        { "source_device", "{{deviceName}}" },
                        { "source_topic", "{{topic}}" },
                        { "timestamp", "{{now}}" }
                    }
                }
            },
            Tags = new List<string> { "temperature", "transform", "example" }
        };
    }

    // ── Threshold alerts ────────────────────────────────────────────

    private static IEnumerable<Rule> ThresholdAlerts()
    {
        yield return new Rule
        {
            Id = "rule-high-temp-alert",
            Name = "High Temperature Alert",
            Description = "Alert when temperature exceeds 30°C",
            Enabled = true,
            Priority = 80,
            Trigger = new RuleTrigger
            {
                TopicPattern = "zigbee/+",
                Type = TriggerType.OnMessage
            },
            Conditions = new RuleConditionGroup
            {
                Logic = LogicOperator.All,
                Conditions = new List<RuleCondition>
                {
                    new() { Property = "temperature", Operator = ComparisonOperator.GreaterThan, Value = 30.0 }
                }
            },
            Actions = new List<RuleAction>
            {
                new()
                {
                    Type = ActionType.Publish,
                    Topic = "alerts/temperature/high",
                    Payload = new Dictionary<string, object>
                    {
                        { "alert", "HIGH_TEMPERATURE" },
                        { "device", "{{deviceName}}" },
                        { "temperature", "{{source.temperature}}" },
                        { "threshold", 30 },
                        { "severity", "warning" }
                    }
                }
            },
            Tags = new List<string> { "alert", "temperature", "threshold" }
        };

        yield return new Rule
        {
            Id = "rule-low-battery-alert",
            Name = "Low Battery Alert",
            Description = "Alert when battery drops below 20%",
            Enabled = true,
            Priority = 90,
            Trigger = new RuleTrigger
            {
                TopicPattern = "zigbee/+",
                Type = TriggerType.OnMessage
            },
            Conditions = new RuleConditionGroup
            {
                Logic = LogicOperator.All,
                Conditions = new List<RuleCondition>
                {
                    new() { Property = "battery", Operator = ComparisonOperator.Exists },
                    new() { Property = "battery", Operator = ComparisonOperator.LessThan, Value = 20.0 }
                }
            },
            Actions = new List<RuleAction>
            {
                new()
                {
                    Type = ActionType.Publish,
                    Topic = "alerts/battery/low",
                    Payload = new Dictionary<string, object>
                    {
                        { "alert", "LOW_BATTERY" },
                        { "device", "{{deviceName}}" },
                        { "battery_level", "{{source.battery}}" },
                        { "severity", "critical" }
                    }
                }
            },
            Tags = new List<string> { "alert", "battery", "maintenance" }
        };
    }

    // ── Multi-condition (AND / OR) rules ────────────────────────────

    private static IEnumerable<Rule> MultiConditionRules()
    {
        yield return new Rule
        {
            Id = "rule-comfort-zone-and",
            Name = "Comfort Zone Check (AND)",
            Description = "Alerts when BOTH temperature AND humidity are in comfortable range",
            Enabled = true,
            Priority = 100,
            Trigger = new RuleTrigger
            {
                TopicPattern = "zigbee/+",
                Type = TriggerType.OnMessage
            },
            Conditions = new RuleConditionGroup
            {
                Logic = LogicOperator.All,
                Conditions = new List<RuleCondition>
                {
                    new() { Property = "temperature", Operator = ComparisonOperator.GreaterThanOrEquals, Value = 18.0 },
                    new() { Property = "temperature", Operator = ComparisonOperator.LessThanOrEquals,    Value = 24.0 },
                    new() { Property = "humidity",    Operator = ComparisonOperator.GreaterThanOrEquals, Value = 30.0 },
                    new() { Property = "humidity",    Operator = ComparisonOperator.LessThanOrEquals,    Value = 60.0 }
                }
            },
            Actions = new List<RuleAction>
            {
                new()
                {
                    Type = ActionType.Publish,
                    Topic = "status/comfort/optimal",
                    Payload = new Dictionary<string, object>
                    {
                        { "status", "COMFORTABLE" },
                        { "device", "{{deviceName}}" },
                        { "temperature", "{{source.temperature}}" },
                        { "humidity", "{{source.humidity}}" }
                    }
                }
            },
            Tags = new List<string> { "comfort", "climate", "AND-logic" }
        };

        yield return new Rule
        {
            Id = "rule-any-alert-or",
            Name = "Any Sensor Issue (OR)",
            Description = "Alerts if ANY problematic condition is detected",
            Enabled = true,
            Priority = 70,
            Trigger = new RuleTrigger
            {
                TopicPattern = "zigbee/+",
                Type = TriggerType.OnMessage
            },
            Conditions = new RuleConditionGroup
            {
                Logic = LogicOperator.Any,
                Conditions = new List<RuleCondition>
                {
                    new() { Property = "temperature", Operator = ComparisonOperator.GreaterThan, Value = 35.0 },
                    new() { Property = "humidity",    Operator = ComparisonOperator.GreaterThan, Value = 80.0 },
                    new() { Property = "battery",     Operator = ComparisonOperator.LessThan,    Value = 10.0 }
                }
            },
            Actions = new List<RuleAction>
            {
                new()
                {
                    Type = ActionType.Publish,
                    Topic = "alerts/sensor/issue",
                    PassthroughPayload = true
                }
            },
            Tags = new List<string> { "alert", "multi-sensor", "OR-logic" }
        };
    }

    // ── Cross-protocol bridges ──────────────────────────────────────

    private static IEnumerable<Rule> CrossProtocolBridges()
    {
        yield return new Rule
        {
            Id = "rule-zigbee-to-wifi",
            Name = "ZigBee to WiFi Bridge",
            Description = "Bridges all ZigBee sensor data to WiFi namespace",
            Enabled = true,
            Priority = 200,
            Trigger = new RuleTrigger
            {
                TopicPattern = "zigbee/+",
                Type = TriggerType.OnMessage
            },
            Actions = new List<RuleAction>
            {
                new()
                {
                    Type = ActionType.Publish,
                    Topic = "wifi/bridged/{{deviceName}}",
                    PassthroughPayload = true
                }
            },
            Tags = new List<string> { "bridge", "zigbee", "wifi" }
        };

        yield return new Rule
        {
            Id = "rule-zigbee-to-lora-bridge",
            Name = "ZigBee to LoRa Bridge",
            Description = "Bridges ZigBee data to LoRa - only fires when values change",
            Enabled = true,
            Priority = 200,
            Trigger = new RuleTrigger
            {
                TopicPattern = "zigbee/+",
                Type = TriggerType.OnChange
            },
            Actions = new List<RuleAction>
            {
                new()
                {
                    Type = ActionType.Publish,
                    Topic = "lora/gateway/{{deviceName}}",
                    PassthroughPayload = true
                }
            },
            Tags = new List<string> { "bridge", "zigbee", "lora", "on-change" }
        };
    }

    // ── Debounced rules ─────────────────────────────────────────────

    private static IEnumerable<Rule> DebouncedRules()
    {
        yield return new Rule
        {
            Id = "rule-debounced-alert",
            Name = "Debounced Motion Alert",
            Description = "Motion alert with 1-minute debounce to prevent alert spam",
            Enabled = true,
            Priority = 100,
            Trigger = new RuleTrigger
            {
                TopicPattern = "zigbee/+",
                Type = TriggerType.OnMessage,
                Debounce = "1m"
            },
            Conditions = new RuleConditionGroup
            {
                Logic = LogicOperator.All,
                Conditions = new List<RuleCondition>
                {
                    new() { Property = "occupancy", Operator = ComparisonOperator.Equals, Value = true }
                }
            },
            Actions = new List<RuleAction>
            {
                new()
                {
                    Type = ActionType.Publish,
                    Topic = "alerts/motion/{{deviceName}}",
                    Payload = new Dictionary<string, object>
                    {
                        { "event", "MOTION_DETECTED" },
                        { "device", "{{deviceName}}" },
                        { "timestamp", "{{now}}" }
                    }
                }
            },
            Tags = new List<string> { "motion", "debounce", "alert" }
        };
    }

    // ── State-change loggers ────────────────────────────────────────

    private static IEnumerable<Rule> StateChangeLoggers()
    {
        yield return new Rule
        {
            Id = "rule-light-state-log",
            Name = "Light State Logger",
            Description = "Logs all light state changes (on/off)",
            Enabled = true,
            Priority = 150,
            Trigger = new RuleTrigger
            {
                TopicPattern = "zigbee/+",
                Type = TriggerType.OnChange
            },
            Conditions = new RuleConditionGroup
            {
                Logic = LogicOperator.All,
                Conditions = new List<RuleCondition>
                {
                    new() { Property = "state", Operator = ComparisonOperator.Exists }
                }
            },
            Actions = new List<RuleAction>
            {
                new()
                {
                    Type = ActionType.Publish,
                    Topic = "logs/lights/{{deviceName}}",
                    Payload = new Dictionary<string, object>
                    {
                        { "device", "{{deviceName}}" },
                        { "state", "{{source.state}}" },
                        { "brightness", "{{source.brightness}}" },
                        { "timestamp", "{{now}}" }
                    }
                }
            },
            Tags = new List<string> { "light", "logging", "state-change" }
        };
    }

    // ── Topic-pattern rules ─────────────────────────────────────────

    private static IEnumerable<Rule> PatternMatchRules()
    {
        yield return new Rule
        {
            Id = "rule-living-room-monitor",
            Name = "Living Room Monitor",
            Description = "Monitors all devices with 'living' in the topic",
            Enabled = true,
            Priority = 180,
            Trigger = new RuleTrigger
            {
                TopicPattern = "zigbee/living_room_+",
                Type = TriggerType.OnMessage
            },
            Actions = new List<RuleAction>
            {
                new()
                {
                    Type = ActionType.Publish,
                    Topic = "rooms/living_room/sensors",
                    PassthroughPayload = true
                }
            },
            Tags = new List<string> { "room", "living-room", "aggregation" }
        };

        yield return new Rule
        {
            Id = "rule-all-rooms",
            Name = "All Room Sensors",
            Description = "Catches all sensors using multi-level wildcard",
            Enabled = false,
            Priority = 250,
            Trigger = new RuleTrigger
            {
                TopicPattern = "zigbee/#",
                Type = TriggerType.OnMessage
            },
            Actions = new List<RuleAction>
            {
                new()
                {
                    Type = ActionType.Publish,
                    Topic = "aggregation/all_sensors",
                    Payload = new Dictionary<string, object>
                    {
                        { "source", "{{topic}}" },
                        { "data", "{{source}}" }
                    }
                }
            },
            Tags = new List<string> { "wildcard", "aggregation", "all" }
        };
    }

    // ── Cross-tech automations ──────────────────────────────────────

    private static IEnumerable<Rule> CrossTechAutomations()
    {
        yield return new Rule
        {
            Id = "rule-lora-soil-to-zigbee-valve",
            Name = "Garden Irrigation",
            Description = "When the LoRa garden soil sensor reports moisture below 30%, open the ZigBee irrigation valve",
            Enabled = true,
            Priority = 120,
            Trigger = new RuleTrigger
            {
                TopicPattern = "lora/lora_soil_garden",
                Type = TriggerType.OnChange
            },
            Conditions = new RuleConditionGroup
            {
                Logic = LogicOperator.All,
                Conditions = new List<RuleCondition>
                {
                    new() { Property = "moisture_pct", Operator = ComparisonOperator.LessThan, Value = 30 }
                }
            },
            Actions = new List<RuleAction>
            {
                new()
                {
                    Type = ActionType.Publish,
                    Topic = "zigbee/aqara_water_valve/set",
                    Payload = new Dictionary<string, object>
                    {
                        { "state", "ON" },
                        { "reason", "soil moisture {{source.moisture_pct}}%" }
                    }
                }
            },
            Tags = new List<string> { "cross-tech", "lora", "zigbee", "garden" }
        };

        yield return new Rule
        {
            Id = "rule-ble-fridge-warm",
            Name = "Fridge Warming Alert",
            Description = "When the BLE Govee thermometer in the fridge reads above 8C for any reading, raise an alert and show on the hallway display",
            Enabled = true,
            Priority = 90,
            Trigger = new RuleTrigger
            {
                TopicPattern = "bluetooth/govee_therm_fridge",
                Type = TriggerType.OnMessage
            },
            Conditions = new RuleConditionGroup
            {
                Logic = LogicOperator.All,
                Conditions = new List<RuleCondition>
                {
                    new() { Property = "temperature_c", Operator = ComparisonOperator.GreaterThan, Value = 8 }
                }
            },
            Actions = new List<RuleAction>
            {
                new()
                {
                    Type = ActionType.Publish,
                    Topic = "alerts/fridge/warm",
                    Payload = new Dictionary<string, object>
                    {
                        { "severity", "warning" },
                        { "device", "{{deviceName}}" },
                        { "temperature_c", "{{source.temperature_c}}" },
                        { "timestamp", "{{now}}" }
                    }
                },
                new()
                {
                    Type = ActionType.Publish,
                    Topic = "wifi/esp32_display_hallway/message",
                    Payload = new Dictionary<string, object>
                    {
                        { "line1", "FRIDGE WARM" },
                        { "line2", "{{source.temperature_c}} C" },
                        { "duration_s", 30 }
                    }
                }
            },
            Tags = new List<string> { "cross-tech", "bluetooth", "wifi", "alerts", "fridge" }
        };

        yield return new Rule
        {
            Id = "rule-wifi-shelly-overload",
            Name = "Dryer Power Overload",
            Description = "When the Wi-Fi Shelly plug on the dryer reports power above 3000W, raise an overload alert",
            Enabled = true,
            Priority = 80,
            Trigger = new RuleTrigger
            {
                TopicPattern = "wifi/shelly_plug_dryer",
                Type = TriggerType.OnChange
            },
            Conditions = new RuleConditionGroup
            {
                Logic = LogicOperator.All,
                Conditions = new List<RuleCondition>
                {
                    new() { Property = "power_w", Operator = ComparisonOperator.GreaterThan, Value = 3000 }
                }
            },
            Actions = new List<RuleAction>
            {
                new()
                {
                    Type = ActionType.Publish,
                    Topic = "alerts/dryer/overload",
                    Payload = new Dictionary<string, object>
                    {
                        { "severity", "critical" },
                        { "device", "{{deviceName}}" },
                        { "power_w", "{{source.power_w}}" },
                        { "timestamp", "{{now}}" }
                    }
                }
            },
            Tags = new List<string> { "cross-tech", "wifi", "alerts", "power" }
        };

        yield return new Rule
        {
            Id = "rule-ble-bathroom-fan",
            Name = "Bathroom Fan Auto",
            Description = "When the BLE bathroom humidity sensor reports humidity above 75%, turn on the Wi-Fi bathroom fan",
            Enabled = true,
            Priority = 100,
            Trigger = new RuleTrigger
            {
                TopicPattern = "bluetooth/xiaomi_lywsd03mmc_bathroom",
                Type = TriggerType.OnChange
            },
            Conditions = new RuleConditionGroup
            {
                Logic = LogicOperator.All,
                Conditions = new List<RuleCondition>
                {
                    new() { Property = "humidity_pct", Operator = ComparisonOperator.GreaterThan, Value = 75 }
                }
            },
            Actions = new List<RuleAction>
            {
                new()
                {
                    Type = ActionType.Publish,
                    Topic = "wifi/tasmota_fan_bathroom/cmnd/POWER",
                    Payload = new Dictionary<string, object>
                    {
                        { "state", "ON" },
                        { "source_humidity_pct", "{{source.humidity_pct}}" }
                    }
                }
            },
            Tags = new List<string> { "cross-tech", "bluetooth", "wifi", "bathroom" }
        };
    }
}
