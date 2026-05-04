using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Hermod.Core.Interfaces;
using Hermod.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Hermod.Coordinator.Controllers;

/// <summary>REST surface over the device store, with Zigbee2MQTT-aware rename handling.</summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
[Authorize]
public sealed class DevicesController : ControllerBase
{
    // Same character set Zigbee2MQTT itself accepts for friendly names. Allowing
    // a forward slash so nested-name conventions (kitchen/light) keep working.
    // Spaces are allowed so "Living Room Light" type names work end-to-end.
    // Leading/trailing whitespace is stripped by .Trim() before this match;
    // internal spaces are legal in MQTT topic segments and in z2m's
    // friendly_name field.
    private static readonly Regex FriendlyNameRegex = new(@"^[A-Za-z0-9_\-/. ]{1,64}$", RegexOptions.Compiled);

    private readonly IDeviceService _deviceService;
    private readonly IZigbee2MqttService _zigbeeService;
    private readonly ILogger<DevicesController> _logger;

    /// <summary>Creates the controller with its device store, Zigbee bridge and logger.</summary>
    /// <param name="deviceService">Device store.</param>
    /// <param name="zigbeeService">Zigbee2MQTT bridge used for upstream renames.</param>
    /// <param name="logger">Logger for device lifecycle events.</param>
    public DevicesController(
        IDeviceService deviceService,
        IZigbee2MqttService zigbeeService,
        ILogger<DevicesController> logger)
    {
        ArgumentNullException.ThrowIfNull(deviceService);
        ArgumentNullException.ThrowIfNull(zigbeeService);
        ArgumentNullException.ThrowIfNull(logger);
        _deviceService = deviceService;
        _zigbeeService = zigbeeService;
        _logger = logger;
    }

    private string ActorName => User?.Identity?.Name ?? "unknown";

    /// <summary>Paginated device listing with optional protocol filter and free-text search.</summary>
    /// <param name="protocol">Optional protocol filter (Zigbee/LoRa/Bluetooth/Wifi).</param>
    /// <param name="offset">Zero-based row offset. Default 0.</param>
    /// <param name="limit">Page size (clamped 1..1000). Default 100.</param>
    /// <param name="q">Free-text filter on device id / name. Optional.</param>
    /// <param name="cancellationToken">Token to abort the query.</param>
    /// <returns>200 with <see cref="DevicePage"/>.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(DevicePage), StatusCodes.Status200OK)]
    public async Task<ActionResult<DevicePage>> GetAll(
        [FromQuery] Protocol? protocol,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 100,
        [FromQuery] string? q = null,
        CancellationToken cancellationToken = default)
    {
        var page = await _deviceService.GetDevicesPageAsync(offset, limit, q, protocol, cancellationToken);
        return Ok(page);
    }

    /// <summary>Get device by ID.</summary>
    /// <param name="id">Device identifier.</param>
    /// <param name="cancellationToken">Token to abort the query.</param>
    /// <returns>200 with the device, 404 if missing.</returns>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(Device), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Device>> GetById(string id, CancellationToken cancellationToken = default)
    {
        var device = await _deviceService.GetDeviceAsync(id, cancellationToken);
        return device is null ? NotFoundForDevice(id) : Ok(device);
    }

    /// <summary>Create or update a device.</summary>
    /// <param name="device">Device to upsert.</param>
    /// <param name="cancellationToken">Token to abort the upsert.</param>
    /// <returns>200 with the saved device, 400 when <c>Id</c> is empty.</returns>
    [HttpPost]
    [Authorize(Policy = Hermod.Coordinator.Authorization.Policies.Operator)]
    [ProducesResponseType(typeof(Device), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<Device>> CreateOrUpdate(
        [FromBody] Device device,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (string.IsNullOrEmpty(device.Id))
        {
            return BadRequest(new { message = "Device ID is required" });
        }

        var result = await _deviceService.AddOrUpdateDeviceAsync(device, cancellationToken);
        _logger.LogInformation("Device {DeviceId} created/updated via API by {Actor}", device.Id, ActorName);
        return Ok(result);
    }

    /// <summary>Merge a state dictionary into the device's live state.</summary>
    /// <param name="id">Device identifier.</param>
    /// <param name="state">State keys and values to merge.</param>
    /// <param name="cancellationToken">Token to abort the update.</param>
    /// <returns>200 on success, 404 if the device is missing.</returns>
    [HttpPatch("{id}/state")]
    [Authorize(Policy = Hermod.Coordinator.Authorization.Policies.Operator)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> UpdateState(
        string id,
        [FromBody] Dictionary<string, object> state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        var device = await _deviceService.GetDeviceAsync(id, cancellationToken);
        if (device is null) return NotFoundForDevice(id);

        await _deviceService.UpdateDeviceStateAsync(id, state, cancellationToken);
        _logger.LogInformation("Device {DeviceId} state patched via API by {Actor}", id, ActorName);
        return Ok(new { message = "State updated" });
    }

    /// <summary>Update device online/offline/unknown status.</summary>
    /// <param name="id">Device identifier.</param>
    /// <param name="update">New status value.</param>
    /// <param name="cancellationToken">Token to abort the update.</param>
    /// <returns>200 on success, 404 if the device is missing.</returns>
    [HttpPatch("{id}/status")]
    [Authorize(Policy = Hermod.Coordinator.Authorization.Policies.Operator)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> UpdateStatus(
        string id,
        [FromBody] DeviceStatusUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        var device = await _deviceService.GetDeviceAsync(id, cancellationToken);
        if (device is null) return NotFoundForDevice(id);

        await _deviceService.UpdateDeviceStatusAsync(id, update.Status, cancellationToken);
        _logger.LogInformation("Device {DeviceId} status set to {Status} via API by {Actor}", id, update.Status, ActorName);
        return Ok(new { message = "Status updated" });
    }

    /// <summary>
    /// Renames a device. For Zigbee devices this also calls Zigbee2MQTT's
    /// <c>bridge/request/device/rename</c> endpoint so the upstream friendly
    /// name and the coordinator's record stay in lockstep. For non-Zigbee
    /// devices only the coordinator-side row is touched.
    /// </summary>
    /// <param name="id">Current device identifier.</param>
    /// <param name="request">Rename request with the new friendly name.</param>
    /// <param name="cancellationToken">Token to abort the rename.</param>
    /// <returns>200 with the renamed device, 400 on invalid name, 404 if missing, 409 if the target name is taken, 502 if the Zigbee bridge rejects the call.</returns>
    [HttpPost("{id}/rename")]
    [Authorize(Policy = Hermod.Coordinator.Authorization.Policies.Operator)]
    [ProducesResponseType(typeof(Device), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Upstream Zigbee2MQTT failures are shaped into 502 responses so the UI can distinguish bridge errors from validation errors.")]
    public async Task<ActionResult<Device>> Rename(
        string id,
        [FromBody] DeviceRenameRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.NewName))
        {
            return BadRequest(new { message = "newName is required" });
        }

        var newName = request.NewName.Trim();
        if (!FriendlyNameRegex.IsMatch(newName))
        {
            return BadRequest(new { message = "newName may contain only letters, digits, spaces, '_', '-', '.', '/' (1–64 chars)" });
        }

        if (string.Equals(id, newName, StringComparison.Ordinal))
        {
            // No-op rename; surface the current device so the UI doesn't choke.
            var unchanged = await _deviceService.GetDeviceAsync(id, cancellationToken);
            return unchanged is null ? NotFoundForDevice(id) : Ok(unchanged);
        }

        var device = await _deviceService.GetDeviceAsync(id, cancellationToken);
        if (device is null) return NotFoundForDevice(id);

        if (device.Protocol == Protocol.Zigbee)
        {
            // Tell Zigbee2MQTT first; if it refuses we don't want a rename
            // that the coordinator and the upstream bridge disagree on.
            bool z2mOk;
            try
            {
                z2mOk = await _zigbeeService.RenameDeviceAsync(id, newName, homeAssistantRename: false, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Z2M rename request failed for device {DeviceId}", id);
                return Problem(
                    title: "Zigbee bridge rejected the rename",
                    detail: "The Zigbee2MQTT bridge did not accept the rename request. Check the bridge log.",
                    statusCode: StatusCodes.Status502BadGateway);
            }

            if (!z2mOk)
            {
                return Conflict(new { message = "Zigbee2MQTT refused the rename (name taken or invalid for the bridge)" });
            }
        }

        var renamed = await _deviceService.RenameDeviceAsync(id, newName, cancellationToken);
        if (!renamed)
        {
            return Conflict(new { message = $"Device '{newName}' already exists or '{id}' was concurrently removed" });
        }

        var updated = await _deviceService.GetDeviceAsync(newName, cancellationToken);
        _logger.LogInformation("Renamed device {OldId} -> {NewId} via API by {Actor}", id, newName, ActorName);
        return Ok(updated);
    }

    /// <summary>Delete a device.</summary>
    /// <param name="id">Device identifier.</param>
    /// <param name="cancellationToken">Token to abort the deletion.</param>
    /// <returns>200 on success, 404 if the device is missing.</returns>
    [HttpDelete("{id}")]
    [Authorize(Policy = Hermod.Coordinator.Authorization.Policies.Operator)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Delete(string id, CancellationToken cancellationToken = default)
    {
        var removed = await _deviceService.RemoveDeviceAsync(id, cancellationToken);
        if (!removed) return NotFoundForDevice(id);

        _logger.LogInformation("Device {DeviceId} deleted via API by {Actor}", id, ActorName);
        return Ok(new { message = "Device deleted" });
    }

    private NotFoundObjectResult NotFoundForDevice(string id) =>
        NotFound(new { message = $"Device '{id}' not found" });
}

/// <summary>Body for <see cref="DevicesController.UpdateStatus"/>.</summary>
public sealed class DeviceStatusUpdate
{
    /// <summary>New device status.</summary>
    public DeviceStatus Status { get; set; }
}

/// <summary>Body for <see cref="DevicesController.Rename"/>.</summary>
public sealed class DeviceRenameRequest
{
    /// <summary>New friendly name for the device.</summary>
    public string NewName { get; set; } = string.Empty;
}
