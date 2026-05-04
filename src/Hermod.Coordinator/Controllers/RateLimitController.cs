using Hermod.Coordinator.Authorization;
using Hermod.Core.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Hermod.Coordinator.Controllers;

/// <summary>
/// Runtime overrides for the per-topic ingress limiter. Mirrors
/// <see cref="RateLimitSettings.TopicOverrides"/> but applies in-memory
/// so the Settings UI can throttle a topic without a coordinator
/// restart. Overrides reset on pod restart; static config remains the
/// across-restart source of truth.
/// </summary>
[ApiController]
[Route("api/system/rate-limits")]
[Authorize(Policy = Policies.Operator)]
public sealed class RateLimitController : ControllerBase
{
    private readonly IRateLimitOverridesStore _store;
    private readonly IOptionsMonitor<HermodSettings> _settings;

    /// <summary>Creates the controller.</summary>
    public RateLimitController(IRateLimitOverridesStore store, IOptionsMonitor<HermodSettings> settings)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(settings);
        _store = store;
        _settings = settings;
    }

    /// <summary>Returns master switches, defaults, and merged static + runtime overrides.</summary>
    [HttpGet]
    public IActionResult Get()
    {
        var cfg = _settings.CurrentValue.RateLimit;
        return Ok(new RateLimitView(
            Enabled: cfg.Enabled,
            DedupEnabled: cfg.DedupEnabled,
            DefaultRatePerSecond: cfg.DefaultRatePerSecond,
            DefaultBurst: cfg.DefaultBurst,
            DedupWindowSeconds: cfg.DedupWindowSeconds,
            MaxTrackedKeys: cfg.MaxTrackedKeys,
            StaticOverrides: cfg.TopicOverrides
                .ToDictionary(kv => kv.Key, kv => Map(kv.Value)),
            RuntimeOverrides: _store.Snapshot()
                .ToDictionary(kv => kv.Key, kv => Map(kv.Value))));
    }

    /// <summary>Upserts a runtime per-topic override. Returns 200 with the stored value.</summary>
    [HttpPut("{topic}")]
    public async Task<IActionResult> Upsert(string topic, [FromBody] TopicRateOverrideView body, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(topic))
            return BadRequest(new { error = "topic must be non-empty" });
        if (body is null)
            return BadRequest(new { error = "body required" });
        if (body.RatePerSecond < 0 || body.Burst < 0)
            return BadRequest(new { error = "RatePerSecond and Burst must be non-negative" });

        var stored = new TopicRateOverride
        {
            RatePerSecond = body.RatePerSecond,
            Burst = body.Burst,
            DedupWindowSeconds = body.DedupWindowSeconds,
        };
        await _store.SetAsync(topic, stored, cancellationToken);
        return Ok(Map(stored));
    }

    /// <summary>Removes a runtime per-topic override. 204 on success, 404 when none.</summary>
    [HttpDelete("{topic}")]
    public async Task<IActionResult> Delete(string topic, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(topic))
            return BadRequest(new { error = "topic must be non-empty" });
        return await _store.RemoveAsync(topic, cancellationToken) ? NoContent() : NotFound();
    }

    private static TopicRateOverrideView Map(TopicRateOverride source) => new(
        RatePerSecond: source.RatePerSecond,
        Burst: source.Burst,
        DedupWindowSeconds: source.DedupWindowSeconds);
}

/// <summary>Top-level view returned by <see cref="RateLimitController.Get"/>.</summary>
/// <param name="Enabled">Master switch for the token-bucket rate cap.</param>
/// <param name="DedupEnabled">Master switch for the dedup window.</param>
/// <param name="DefaultRatePerSecond">Default sustained tokens per second per topic.</param>
/// <param name="DefaultBurst">Default token-bucket capacity per topic.</param>
/// <param name="DedupWindowSeconds">Default dedup window in seconds.</param>
/// <param name="MaxTrackedKeys">LRU cap on per-topic state objects.</param>
/// <param name="StaticOverrides">Static overrides from configuration.</param>
/// <param name="RuntimeOverrides">Runtime overrides installed through this controller.</param>
public sealed record RateLimitView(
    bool Enabled,
    bool DedupEnabled,
    double DefaultRatePerSecond,
    int DefaultBurst,
    int DedupWindowSeconds,
    int MaxTrackedKeys,
    IReadOnlyDictionary<string, TopicRateOverrideView> StaticOverrides,
    IReadOnlyDictionary<string, TopicRateOverrideView> RuntimeOverrides);

/// <summary>Per-topic override fields. Sentinel values fall back to the defaults.</summary>
/// <param name="RatePerSecond">Sustained rate; &lt;= 0 falls back to default.</param>
/// <param name="Burst">Burst capacity; &lt;= 0 falls back to default.</param>
/// <param name="DedupWindowSeconds">Dedup window; &lt; 0 falls back to default; 0 disables dedup.</param>
public sealed record TopicRateOverrideView(double RatePerSecond, int Burst, int DedupWindowSeconds);
