using LoRa2MQTT.Service;
using LoRa2MQTT.Service.Adapters;
using LoRa2MQTT.Service.Configuration;
using LoRa2MQTT.Service.Models;
using LoRa2MQTT.Service.Services;
using Prometheus;
using Vault42.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<LoRaOptions>(
    builder.Configuration.GetSection(LoRaOptions.SectionName));
builder.Services.Configure<MqttOptions>(
    builder.Configuration.GetSection(MqttOptions.SectionName));
builder.Services.Configure<MockOptions>(
    builder.Configuration.GetSection(MockOptions.SectionName));
builder.Services.Configure<LoRaSecurityOptions>(
    builder.Configuration.GetSection(LoRaSecurityOptions.SectionName));

var loraOptions = builder.Configuration
    .GetSection(LoRaOptions.SectionName)
    .Get<LoRaOptions>() ?? new LoRaOptions();

if (loraOptions.MockMode)
{
    builder.Services.AddSingleton<MockLoRaAdapter>();
    builder.Services.AddSingleton<ILoRaAdapter>(sp => sp.GetRequiredService<MockLoRaAdapter>());
}
else
{
    builder.Services.AddSingleton<ILoRaAdapter, WaveshareLoRaAdapter>();
}

builder.Services.AddSingleton<MqttService>();
builder.Services.AddSingleton<LoRaMessageGuard>();
builder.Services.AddHostedService<LoRaBridgeWorker>();

builder.Services.AddHealthChecks()
    .AddCheck<LoRaHealthCheck>("lora")
    .AddCheck<MqttHealthCheck>("mqtt");

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// Vault42 JWT (RS256 via JWKS). RequireHttpsMetadata defaults to true in
// 0.2.x; opt-out via Hermod:Security:VaultRequireHttps=false (dev only).
// Hermod:Security:AuthBypass=true short-circuits to a scheme that always
// returns admin claims — used by test profiles deployed without vault42.
// NEVER set in prod.
var authBypass = builder.Configuration.GetValue("Hermod:Security:AuthBypass", false);

// Defense-in-depth: refuse to start if AuthBypass leaks into a Production
// runtime. Mirrors the Coordinator guard so neither service can silently
// disable authentication when shipped under ASPNETCORE_ENVIRONMENT=Production.
if (authBypass && builder.Environment.IsProduction())
{
    throw new InvalidOperationException(
        "Hermod:Security:AuthBypass is enabled but ASPNETCORE_ENVIRONMENT=Production. " +
        "AuthBypass is a test-only feature and must never be active in a Production runtime. " +
        "Either unset Hermod:Security:AuthBypass or change ASPNETCORE_ENVIRONMENT.");
}

if (authBypass)
{
    builder.Services.AddAuthentication(LoRa2MQTT.Service.Services.AuthBypassHandler.SchemeName)
        .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, LoRa2MQTT.Service.Services.AuthBypassHandler>(
            LoRa2MQTT.Service.Services.AuthBypassHandler.SchemeName, _ => { });
}
else
{
    var vaultRequireHttps = builder.Configuration.GetValue("Hermod:Security:VaultRequireHttps", true);
    builder.Services.AddAuthentication(VaultDefaults.AuthenticationScheme)
        .AddVault(options =>
        {
            options.Authority = builder.Configuration["Vault42:Authority"] ?? "http://localhost:8080";
            options.RequireHttpsMetadata = vaultRequireHttps;
            options.ValidateFingerprint = false;
        });
}

// Deny-by-default: any endpoint added without explicit
// .RequireAuthorization() or .AllowAnonymous() must still fail closed.
// Matches the Coordinator's FallbackPolicy so the two services agree on
// "no accidental open endpoints."
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// Capture per-request HTTP duration / status histograms before any
// route is matched. Together with MapMetrics this exposes the standard
// prometheus-net request set: http_request_duration_seconds_*,
// http_requests_in_progress, http_requests_received_total. Plus the
// process collectors (process_cpu_seconds_total, .net thread pool,
// GC heap) come for free at /metrics.
app.UseHttpMetrics();

// Skipped when AuthBypass=true: no vault42 deployment, JWKS fetch would hang.
if (!authBypass)
{
    await app.Services.UseVaultAuthenticationAsync();
}

app.MapHealthChecks("/health").AllowAnonymous();
app.MapHealthChecks("/health/ready").AllowAnonymous();
app.MapHealthChecks("/health/live").AllowAnonymous();

// Prometheus scrape endpoint. Anonymous: the cluster network is the
// trust boundary in dev/test; prod overlays would re-add a
// .RequireAuthorization() if /metrics is exposed beyond the cluster.
app.MapMetrics("/metrics").AllowAnonymous();

app.MapControllers();

// Anonymous {connected, mode} so the Coordinator status poll doesn't
// need a Bearer; no PII, info equivalent to a health probe.
app.MapGet("/api/status", (ILoRaAdapter adapter, MqttService mqtt) => new
{
    LoRaConnected = adapter.IsConnected,
    MqttConnected = mqtt.IsConnected,
    Mode = loraOptions.MockMode ? "mock" : "hardware"
}).AllowAnonymous();

if (loraOptions.MockMode)
{
    app.MapGet("/api/mock/status", (MockLoRaAdapter adapter) => new
    {
        IsConnected = adapter.IsConnected,
        AutoSimulationRunning = adapter.AutoSimulationRunning,
        DeviceCount = adapter.Devices.Count,
        Mode = "mock"
    }).RequireAuthorization();

    app.MapGet("/api/mock/devices", (MockLoRaAdapter adapter) =>
        adapter.Devices.Select(d => new
        {
            d.Id,
            d.Type,
            d.Manufacturer,
            d.Model
        })).RequireAuthorization();

    app.MapPost("/api/mock/devices", async (MockDevice device, MockLoRaAdapter adapter) =>
    {
        if (string.IsNullOrEmpty(device.Id))
            return Results.BadRequest(new { error = "Device ID is required" });

        adapter.AddDevice(device);
        return Results.Created($"/api/mock/devices/{device.Id}", device);
    }).RequireAuthorization();

    app.MapDelete("/api/mock/devices/{id}", (string id, MockLoRaAdapter adapter) =>
    {
        adapter.RemoveDevice(id);
        return Results.NoContent();
    }).RequireAuthorization();

    app.MapDelete("/api/mock/devices", (MockLoRaAdapter adapter) =>
    {
        adapter.ClearDevices();
        return Results.NoContent();
    }).RequireAuthorization();

    app.MapPost("/api/mock/trigger/{id}", (string id, MockLoRaAdapter adapter) =>
    {
        if (id == "all")
            adapter.TriggerAllDevices();
        else
            adapter.TriggerDevice(id);
        return Results.Ok();
    }).RequireAuthorization();

    app.MapPost("/api/mock/auto/start", (MockLoRaAdapter adapter) =>
    {
        adapter.StartAutoSimulation();
        return Results.Ok(new { AutoSimulationRunning = adapter.AutoSimulationRunning });
    }).RequireAuthorization();

    app.MapPost("/api/mock/auto/stop", (MockLoRaAdapter adapter) =>
    {
        adapter.StopAutoSimulation();
        return Results.Ok(new { AutoSimulationRunning = adapter.AutoSimulationRunning });
    }).RequireAuthorization();
}

app.Run();
