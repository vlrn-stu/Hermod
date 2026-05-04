using Hermod.Core.Models;

namespace Hermod.Infrastructure.Services.Seed;

internal static class SampleDevices
{
    /// <summary>
    /// Builds a realistic small-home inventory across all four protocols
    /// (Aqara, Xiaomi, Philips Hue, IKEA, Sonoff, Govee, RuuviTag, Shelly,
    /// Tasmota, Waveshare LoRa) so the dashboard shows believable inventory
    /// out of the box. Timestamps are calibrated against <paramref name="now"/>:
    /// most online devices were seen in the last few minutes; offline ones
    /// were last seen ~2 hours ago to exercise the offline UI path.
    /// </summary>
    public static IReadOnlyList<Device> Build(DateTime now)
    {
        var devices = new List<Device>();
        devices.AddRange(ZigbeeDevices());
        devices.AddRange(LoraDevices());
        devices.AddRange(BluetoothDevices());
        devices.AddRange(WifiDevices());

        var createdAt = now.AddDays(-7);
        foreach (var device in devices)
        {
            device.CreatedAt = createdAt;
            device.UpdatedAt = now;
            device.LastSeen = device.Status == DeviceStatus.Offline
                ? now.AddHours(-2)
                : now.AddMinutes(-Random.Shared.Next(1, 9));
        }

        return devices;
    }

    private static IEnumerable<Device> ZigbeeDevices()
    {
        yield return new Device
        {
            Id = "aqara_motion_lr",
            Name = "Living Room Motion",
            Protocol = Protocol.Zigbee,
            Status = DeviceStatus.Online,
            Manufacturer = "Aqara",
            Model = "RTCGQ11LM",
            FirmwareVersion = "3.0.0_0014",
            Capabilities = new Dictionary<string, object>
            {
                { "type", "motion_sensor" },
                { "battery_percent", 87 },
                { "detects", new[] { "motion", "illuminance" } }
            },
            State = new Dictionary<string, object>
            {
                { "occupancy", false },
                { "illuminance_lux", 142 },
                { "linkquality", 204 }
            }
        };

        yield return new Device
        {
            Id = "aqara_contact_door",
            Name = "Front Door Contact",
            Protocol = Protocol.Zigbee,
            Status = DeviceStatus.Online,
            Manufacturer = "Aqara",
            Model = "MCCGQ11LM",
            FirmwareVersion = "3.0.0_0012",
            Capabilities = new Dictionary<string, object>
            {
                { "type", "contact_sensor" },
                { "battery_percent", 92 }
            },
            State = new Dictionary<string, object>
            {
                { "contact", true },
                { "linkquality", 189 }
            }
        };

        yield return new Device
        {
            Id = "xiaomi_temp_bedroom",
            Name = "Bedroom Climate",
            Protocol = Protocol.Zigbee,
            Status = DeviceStatus.Online,
            Manufacturer = "Xiaomi",
            Model = "WSDCGQ11LM",
            FirmwareVersion = "3.0.0_0010",
            Capabilities = new Dictionary<string, object>
            {
                { "type", "climate_sensor" },
                { "battery_percent", 74 },
                { "detects", new[] { "temperature", "humidity", "pressure" } }
            },
            State = new Dictionary<string, object>
            {
                { "temperature", 21.4 },
                { "humidity", 48.2 },
                { "pressure", 1013.2 }
            }
        };

        yield return new Device
        {
            Id = "hue_bulb_ceiling",
            Name = "Living Room Ceiling",
            Protocol = Protocol.Zigbee,
            Status = DeviceStatus.Online,
            Manufacturer = "Philips",
            Model = "LCT015",
            FirmwareVersion = "1.88.1",
            Capabilities = new Dictionary<string, object>
            {
                { "type", "color_bulb" },
                { "max_lumens", 800 },
                { "color_modes", new[] { "xy", "color_temp" } }
            },
            State = new Dictionary<string, object>
            {
                { "state", "ON" },
                { "brightness", 204 },
                { "color_temp", 370 }
            }
        };

        yield return new Device
        {
            Id = "ikea_outlet_tv",
            Name = "TV Outlet",
            Protocol = Protocol.Zigbee,
            Status = DeviceStatus.Online,
            Manufacturer = "IKEA",
            Model = "TRADFRI E1603",
            FirmwareVersion = "2.3.089",
            Capabilities = new Dictionary<string, object>
            {
                { "type", "smart_plug" },
                { "max_watts", 2300 }
            },
            State = new Dictionary<string, object>
            {
                { "state", "ON" },
                { "linkquality", 172 }
            }
        };

        yield return new Device
        {
            Id = "aqara_water_valve",
            Name = "Garden Irrigation Valve",
            Protocol = Protocol.Zigbee,
            Status = DeviceStatus.Online,
            Manufacturer = "Aqara",
            Model = "SAV01",
            FirmwareVersion = "0.4.11",
            Capabilities = new Dictionary<string, object>
            {
                { "type", "water_valve" },
                { "normally", "closed" }
            },
            State = new Dictionary<string, object>
            {
                { "state", "OFF" },
                { "linkquality", 165 }
            }
        };
    }

    private static IEnumerable<Device> LoraDevices()
    {
        yield return new Device
        {
            Id = "lora_weather_01",
            Name = "Backyard Weather Station",
            Protocol = Protocol.Lora,
            Status = DeviceStatus.Online,
            Manufacturer = "Waveshare",
            Model = "SX1262 + BME680",
            FirmwareVersion = "fw-1.4",
            Capabilities = new Dictionary<string, object>
            {
                { "type", "weather_station" },
                { "channel", 18 },
                { "spreading_factor", 7 }
            },
            State = new Dictionary<string, object>
            {
                { "temperature", 18.7 },
                { "humidity", 62.0 },
                { "pressure", 1009.4 },
                { "rssi", -78 },
                { "snr", 9.2 }
            }
        };

        yield return new Device
        {
            Id = "lora_soil_garden",
            Name = "Garden Soil Probe",
            Protocol = Protocol.Lora,
            Status = DeviceStatus.Online,
            Manufacturer = "Makerfabs",
            Model = "LoRaWAN Soil",
            FirmwareVersion = "fw-1.2",
            Capabilities = new Dictionary<string, object>
            {
                { "type", "soil_sensor" },
                { "detects", new[] { "moisture_pct", "temperature", "ec" } }
            },
            State = new Dictionary<string, object>
            {
                { "moisture_pct", 42 },
                { "temperature", 16.8 },
                { "ec_us_cm", 850 },
                { "rssi", -92 },
                { "snr", 6.1 }
            }
        };

        yield return new Device
        {
            Id = "lora_gate_sensor",
            Name = "Side Gate Contact",
            Protocol = Protocol.Lora,
            Status = DeviceStatus.Online,
            Manufacturer = "Dragino",
            Model = "LDS01",
            FirmwareVersion = "1.5.0",
            Capabilities = new Dictionary<string, object>
            {
                { "type", "contact_sensor" },
                { "battery_v", 3.42 }
            },
            State = new Dictionary<string, object>
            {
                { "contact", true },
                { "rssi", -101 },
                { "snr", 3.4 }
            }
        };

        yield return new Device
        {
            Id = "lora_water_meter",
            Name = "Main Water Meter",
            Protocol = Protocol.Lora,
            Status = DeviceStatus.Online,
            Manufacturer = "Dragino",
            Model = "LWL02",
            FirmwareVersion = "1.6.2",
            Capabilities = new Dictionary<string, object>
            {
                { "type", "pulse_counter" },
                { "units", "litres" }
            },
            State = new Dictionary<string, object>
            {
                { "total_litres", 14823.5 },
                { "rate_lpm", 0.0 },
                { "rssi", -88 },
                { "snr", 8.0 }
            }
        };
    }

    private static IEnumerable<Device> BluetoothDevices()
    {
        yield return new Device
        {
            Id = "govee_therm_fridge",
            Name = "Fridge Thermometer",
            Protocol = Protocol.Bluetooth,
            Status = DeviceStatus.Online,
            Manufacturer = "Govee",
            Model = "H5074",
            FirmwareVersion = "1.00.01",
            Capabilities = new Dictionary<string, object>
            {
                { "type", "ble_thermometer" },
                { "ble_address", "A4:C1:38:33:5F:21" }
            },
            State = new Dictionary<string, object>
            {
                { "temperature_c", 4.2 },
                { "humidity_pct", 38.0 },
                { "battery_pct", 91 },
                { "rssi", -63 }
            }
        };

        yield return new Device
        {
            Id = "xiaomi_lywsd03mmc_bathroom",
            Name = "Bathroom Climate",
            Protocol = Protocol.Bluetooth,
            Status = DeviceStatus.Online,
            Manufacturer = "Xiaomi",
            Model = "LYWSD03MMC",
            FirmwareVersion = "atc1441-fw-3.7",
            Capabilities = new Dictionary<string, object>
            {
                { "type", "ble_thermometer" },
                { "ble_address", "A4:C1:38:1C:7B:0A" }
            },
            State = new Dictionary<string, object>
            {
                { "temperature_c", 23.1 },
                { "humidity_pct", 58.0 },
                { "battery_pct", 76 },
                { "rssi", -71 }
            }
        };

        yield return new Device
        {
            Id = "ruuvi_outdoor",
            Name = "Balcony RuuviTag",
            Protocol = Protocol.Bluetooth,
            Status = DeviceStatus.Online,
            Manufacturer = "Ruuvi",
            Model = "RuuviTag 4.0",
            FirmwareVersion = "3.31.1",
            Capabilities = new Dictionary<string, object>
            {
                { "type", "ble_environment" },
                { "ble_address", "E8:2A:44:FF:0A:11" },
                { "detects", new[] { "temperature", "humidity", "pressure", "acceleration" } }
            },
            State = new Dictionary<string, object>
            {
                { "temperature_c", 17.5 },
                { "humidity_pct", 64.0 },
                { "pressure_hpa", 1009.0 },
                { "rssi", -81 }
            }
        };

        yield return new Device
        {
            Id = "mi_band_wrist",
            Name = "Mi Band 6",
            Protocol = Protocol.Bluetooth,
            Status = DeviceStatus.Offline,
            Manufacturer = "Xiaomi",
            Model = "Mi Band 6",
            FirmwareVersion = "1.0.9.92",
            Capabilities = new Dictionary<string, object>
            {
                { "type", "ble_wearable" },
                { "ble_address", "DC:A6:32:12:8B:44" }
            },
            State = new Dictionary<string, object>
            {
                { "heart_rate_bpm", 0 },
                { "battery_pct", 52 }
            }
        };
    }

    private static IEnumerable<Device> WifiDevices()
    {
        yield return new Device
        {
            Id = "esp32_display_hallway",
            Name = "Hallway E-Paper",
            Protocol = Protocol.Wifi,
            Status = DeviceStatus.Online,
            Manufacturer = "DIY",
            Model = "ESP32 + Waveshare 4.2 ePaper",
            FirmwareVersion = "display-0.9.1",
            Capabilities = new Dictionary<string, object>
            {
                { "type", "display" },
                { "resolution", "400x300" }
            },
            State = new Dictionary<string, object>
            {
                { "online", true },
                { "last_message", "Good morning" }
            }
        };

        yield return new Device
        {
            Id = "shelly_plug_dryer",
            Name = "Dryer Plug",
            Protocol = Protocol.Wifi,
            Status = DeviceStatus.Online,
            Manufacturer = "Shelly",
            Model = "Plug S Gen3",
            FirmwareVersion = "1.3.3",
            Capabilities = new Dictionary<string, object>
            {
                { "type", "smart_plug" },
                { "measures", new[] { "power_w", "energy_wh", "voltage_v" } },
                { "max_watts", 2500 }
            },
            State = new Dictionary<string, object>
            {
                { "state", "OFF" },
                { "power_w", 0.0 },
                { "voltage_v", 232.1 }
            }
        };

        yield return new Device
        {
            Id = "tasmota_fan_bathroom",
            Name = "Bathroom Fan",
            Protocol = Protocol.Wifi,
            Status = DeviceStatus.Online,
            Manufacturer = "Sonoff",
            Model = "Basic R2 (Tasmota)",
            FirmwareVersion = "Tasmota 13.4.0",
            Capabilities = new Dictionary<string, object>
            {
                { "type", "switch" },
                { "firmware_family", "tasmota" }
            },
            State = new Dictionary<string, object>
            {
                { "state", "OFF" }
            }
        };

        yield return new Device
        {
            Id = "govee_ac_controller",
            Name = "Living Room AC",
            Protocol = Protocol.Wifi,
            Status = DeviceStatus.Online,
            Manufacturer = "Govee",
            Model = "AC Controller B5178",
            FirmwareVersion = "1.0.22",
            Capabilities = new Dictionary<string, object>
            {
                { "type", "ir_hub" },
                { "controls", new[] { "ac", "tv", "fan" } }
            },
            State = new Dictionary<string, object>
            {
                { "ac_state", "OFF" },
                { "target_temp_c", 24 }
            }
        };
    }
}
