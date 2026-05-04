using Hermod.Core.Interfaces;
using Hermod.Core.Models;
using Hermod.Core.Telemetry;

namespace Hermod.Infrastructure.Services;

/// <summary>
/// Default <see cref="IStatsService"/> that combines in-process atomic
/// counters with server-side aggregates for dashboard reads. Counters
/// survive restarts because <c>MetricsPersistenceService</c> seeds them
/// from the <c>metrics_counters</c> table on boot.
/// </summary>
public sealed class StatsService : IStatsService, IDisposable
{
    private readonly IDeviceService _deviceService;
    private readonly IRulesService _rulesService;
    private readonly IMetricsRepository _metricsRepository;
    private readonly IProtocolTranslatorHealth? _translatorHealth;
    private readonly HermodMetrics? _metrics;
    // Serializes ResetCountersAsync against GetCounters so a concurrent
    // MetricsPersistenceService flush can't read pre-reset values and
    // then UPSERT them back on top of the freshly-zeroed DB row.
    // Without this, an operator clicking "Reset Counters" around a flush
    // tick could observe the dashboard snap back to pre-reset values on
    // the next pod restart (DB gets re-seeded from the stale flush).
    private readonly SemaphoreSlim _resetLock = new(1, 1);
    private readonly DateTime _startTime = DateTime.UtcNow;
    private long _messagesProcessed;
    private long _rulesExecuted;
    private long _messagesDropped;
    private long _rulesErrored;
    private long _actionsErrored;
    private readonly long[] _messagesByProtocol = new long[Enum.GetValues<Protocol>().Length];

    /// <summary>
    /// Creates a stats service that pulls totals through the device and rule
    /// services and windowed rates from the metrics repository.
    /// <paramref name="translatorHealth"/> is optional: when supplied
    /// (the production DI graph does), <see cref="GetProtocolStatsAsync"/>
    /// reports the per-protocol translator's actual reachability instead
    /// of the previous hard-coded `true`. Tests pass null and accept the
    /// fallback `false` (unknown).
    /// </summary>
    public StatsService(
        IDeviceService deviceService,
        IRulesService rulesService,
        IMetricsRepository metricsRepository,
        IProtocolTranslatorHealth? translatorHealth = null,
        HermodMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(deviceService);
        ArgumentNullException.ThrowIfNull(rulesService);
        ArgumentNullException.ThrowIfNull(metricsRepository);
        _deviceService = deviceService;
        _rulesService = rulesService;
        _metricsRepository = metricsRepository;
        _translatorHealth = translatorHealth;
        _metrics = metrics;
    }

    /// <inheritdoc/>
    public async Task<SystemStats> GetCurrentStatsAsync(CancellationToken cancellationToken = default)
    {
        // Server-side COUNT: ~400 KB of JSON per poll vs shipping every row.
        var deviceCounts = await _deviceService.GetCountsAsync(cancellationToken);
        var activeRules = await _rulesService.CountActiveAsync(cancellationToken);

        var uptime = DateTime.UtcNow - _startTime;
        var messagesProcessed = Interlocked.Read(ref _messagesProcessed);
        var rulesExecuted = Interlocked.Read(ref _rulesExecuted);

        // Lifetime rate is misleading post-restart (uptime resets but
        // counter is seeded from persisted); dashboards prefer the windowed
        // rates below.
        var messagesPerSecond = uptime.TotalSeconds > 0
            ? messagesProcessed / uptime.TotalSeconds
            : 0;

        var rate1m = await _metricsRepository.GetRateOverWindowAsync(TimeSpan.FromMinutes(1), cancellationToken);
        var rate5m = await _metricsRepository.GetRateOverWindowAsync(TimeSpan.FromMinutes(5), cancellationToken);
        var rate1h = await _metricsRepository.GetRateOverWindowAsync(TimeSpan.FromHours(1), cancellationToken);

        return new SystemStats
        {
            TotalDevices = deviceCounts.Total,
            OnlineDevices = deviceCounts.Online,
            ActiveRules = activeRules,
            MessagesProcessed = messagesProcessed,
            RulesExecuted = rulesExecuted,
            MessagesDropped = Interlocked.Read(ref _messagesDropped),
            RulesErrored = Interlocked.Read(ref _rulesErrored),
            ActionsErrored = Interlocked.Read(ref _actionsErrored),
            DevicesByProtocol = deviceCounts.ByProtocol.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            LastUpdated = DateTime.UtcNow,
            Uptime = uptime,
            MessagesPerSecond = messagesPerSecond,
            MessagesPerSecond1m = rate1m,
            MessagesPerSecond5m = rate5m,
            MessagesPerSecond1h = rate1h,
            TopicLimitedRate = _metrics?.TopicLimitedRate ?? 0,
            TopicLimitedDedup = _metrics?.TopicLimitedDedup ?? 0,
            ProtocolLimitedIngressTotal = _metrics?.ProtocolLimitedIngressTotal ?? 0,
            ProtocolLimitedEgressTotal = _metrics?.ProtocolLimitedEgressTotal ?? 0,
            RateLimitedTotal = _metrics?.RateLimitedTotal ?? 0,
        };
    }

    /// <inheritdoc/>
    public void IncrementMessagesProcessed()
    {
        Interlocked.Increment(ref _messagesProcessed);
    }

    /// <inheritdoc/>
    public void IncrementMessagesByProtocol(Protocol protocol)
    {
        var idx = (int)protocol;
        if ((uint)idx >= (uint)_messagesByProtocol.Length) return;
        Interlocked.Increment(ref _messagesByProtocol[idx]);
    }

    /// <inheritdoc/>
    public void IncrementRulesExecuted()
    {
        Interlocked.Increment(ref _rulesExecuted);
    }

    /// <inheritdoc/>
    public void IncrementMessagesDropped()
    {
        Interlocked.Increment(ref _messagesDropped);
    }

    /// <inheritdoc/>
    public void IncrementRulesErrored()
    {
        Interlocked.Increment(ref _rulesErrored);
    }

    /// <inheritdoc/>
    public void IncrementActionsErrored()
    {
        Interlocked.Increment(ref _actionsErrored);
    }

    /// <inheritdoc/>
    public void SeedCounters(long messagesProcessed, long rulesExecuted, long messagesDropped = 0,
        long rulesErrored = 0, long actionsErrored = 0)
    {
        Interlocked.Exchange(ref _messagesProcessed, messagesProcessed);
        Interlocked.Exchange(ref _rulesExecuted, rulesExecuted);
        Interlocked.Exchange(ref _messagesDropped, messagesDropped);
        Interlocked.Exchange(ref _rulesErrored, rulesErrored);
        Interlocked.Exchange(ref _actionsErrored, actionsErrored);
    }

    /// <inheritdoc/>
    public async Task ResetCountersAsync(CancellationToken cancellationToken = default)
    {
        // Take the reset-lock so a concurrent GetCounters flush can't read
        // pre-reset atomics, land after our UpsertCountersAsync(0...), and
        // re-seed the DB with the stale high values. Both paths touch the
        // same atomics+DB row; the lock makes the reset one atomic unit.
        await _resetLock.WaitAsync(cancellationToken);
        try
        {
            // Zero the in-memory atomics first so a concurrent flush
            // (if any reached GetCounters before the lock was taken) still
            // couldn't land a pre-reset write — we flip atomics BEFORE the
            // DB write inside the same lock.
            Interlocked.Exchange(ref _messagesProcessed, 0);
            Interlocked.Exchange(ref _rulesExecuted, 0);
            Interlocked.Exchange(ref _messagesDropped, 0);
            Interlocked.Exchange(ref _rulesErrored, 0);
            Interlocked.Exchange(ref _actionsErrored, 0);
            for (var i = 0; i < _messagesByProtocol.Length; i++)
            {
                Interlocked.Exchange(ref _messagesByProtocol[i], 0);
            }

            // Also zero the Prometheus-side atomics so /metrics and /api/stats
            // stay aligned after reset; without this the two permanently
            // diverge until the next pod restart.
            _metrics?.ResetAll();

            // Upsert zeros into metrics_counters so the next restart's seed
            // reads 0 instead of the pre-reset high-water values.
            await _metricsRepository.UpsertCountersAsync(0, 0, 0, 0, 0, cancellationToken);
        }
        finally { _resetLock.Release(); }
    }

    /// <inheritdoc/>
    public (long MessagesProcessed, long RulesExecuted, long MessagesDropped,
            long RulesErrored, long ActionsErrored) GetCounters()
    {
        // Take the reset-lock so a flush racing an operator-triggered reset
        // gets an atomic snapshot (either all pre-reset or all post-reset,
        // never a mixed read that lets the DB row de-sync). Contention is
        // effectively zero — reset is user-initiated, flush is 15s periodic.
        _resetLock.Wait();
        try
        {
            return (
                Interlocked.Read(ref _messagesProcessed),
                Interlocked.Read(ref _rulesExecuted),
                Interlocked.Read(ref _messagesDropped),
                Interlocked.Read(ref _rulesErrored),
                Interlocked.Read(ref _actionsErrored));
        }
        finally { _resetLock.Release(); }
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<ProtocolStats>> GetProtocolStatsAsync(CancellationToken cancellationToken = default)
    {
        // Bounded stream so a 220k-device registry doesn't allocate a 220k-long
        // list here just to compute the per-protocol rollup.
        var deviceList = new List<Device>();
        await foreach (var d in _deviceService.StreamAllDevicesAsync(pageSize: 1000, cancellationToken: cancellationToken))
        {
            deviceList.Add(d);
        }

        // Probe each translator once and key the result by protocol so the
        // per-row lookup below is O(1). Null check keeps the 3-arg test ctor
        // working (Reachable defaults to false = "unknown").
        var reachability = _translatorHealth is null
            ? new Dictionary<Protocol, bool>()
            : (await _translatorHealth.CheckAllAsync(cancellationToken))
                .Select(h => (Protocol: MapTranslatorName(h.Name), h.Reachable))
                .Where(x => x.Protocol != Protocol.Unknown)
                .ToDictionary(x => x.Protocol, x => x.Reachable);

        return Enum.GetValues<Protocol>()
            .Where(p => p != Protocol.Unknown)
            .Select(protocol => new ProtocolStats
            {
                Protocol = protocol,
                DeviceCount = deviceList.Count(d => d.Protocol == protocol),
                MessageCount = Interlocked.Read(ref _messagesByProtocol[(int)protocol]),
                TranslatorOnline = reachability.TryGetValue(protocol, out var r) && r,
                LastActivity = deviceList
                    .Where(d => d.Protocol == protocol)
                    .OrderByDescending(d => d.LastSeen)
                    .FirstOrDefault()?.LastSeen ?? DateTime.MinValue
            });
    }

    private static Protocol MapTranslatorName(string name) => name switch
    {
        "Zigbee2MQTT" => Protocol.Zigbee,
        "LoRa2MQTT" => Protocol.Lora,
        "BLE2MQTT" => Protocol.Bluetooth,
        "WiFi2MQTT" => Protocol.Wifi,
        _ => Protocol.Unknown,
    };

    /// <summary>Releases the reset semaphore. Singleton in DI so disposal happens at process exit.</summary>
    public void Dispose() => _resetLock.Dispose();
}
