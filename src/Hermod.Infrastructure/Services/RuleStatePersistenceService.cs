using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Hermod.Core.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hermod.Infrastructure.Services;

/// <summary>
/// Periodically flushes per-rule state from <see cref="IStateManager"/> back
/// to the <c>rules.state</c> JSONB column so <c>SetState</c> actions survive
/// coordinator restarts.
///
/// Dirty-tracking: only rules whose serialized state changed since the last
/// flush are written, to keep the rule table write load bounded during idle
/// periods.
/// </summary>
public sealed class RuleStatePersistenceService : BackgroundService
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IStateManager _stateManager;
    private readonly IRulesService _rulesService;
    private readonly ILogger<RuleStatePersistenceService> _logger;
    private readonly Dictionary<string, string> _lastFlushedHashes = new();

    /// <summary>
    /// Creates a service that periodically reconciles in-memory rule state
    /// with the <c>rules.state</c> JSONB column.
    /// </summary>
    public RuleStatePersistenceService(
        IStateManager stateManager,
        IRulesService rulesService,
        ILogger<RuleStatePersistenceService> logger)
    {
        ArgumentNullException.ThrowIfNull(stateManager);
        ArgumentNullException.ThrowIfNull(rulesService);
        ArgumentNullException.ThrowIfNull(logger);
        _stateManager = stateManager;
        _rulesService = rulesService;
        _logger = logger;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("RuleStatePersistenceService started (flush interval: {Interval})", FlushInterval);

        using var timer = new PeriodicTimer(FlushInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await FlushAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            await FlushAsync(CancellationToken.None);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Per-rule flush failure must not halt the sweep; each rule is independent.")]
    internal async Task FlushAsync(CancellationToken cancellationToken)
    {
        var snapshot = _stateManager.SnapshotRuleStates();
        var flushed = 0;

        foreach (var (ruleId, state) in snapshot)
        {
            var hash = JsonSerializer.Serialize(state, JsonOptions);
            if (_lastFlushedHashes.TryGetValue(ruleId, out var previous) && string.Equals(previous, hash, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                var updated = await _rulesService.UpdateRuleStateAsync(ruleId, state, cancellationToken);
                if (updated)
                {
                    _lastFlushedHashes[ruleId] = hash;
                    flushed++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to flush state for rule {RuleId}", ruleId);
            }
        }

        if (flushed > 0)
        {
            _logger.LogDebug("Flushed state for {Count} rule(s)", flushed);
        }
    }
}
