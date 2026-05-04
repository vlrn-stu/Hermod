using System.Collections.Concurrent;
using Hermod.Core;
using Hermod.Core.Configuration;
using Hermod.Core.Interfaces;
using Hermod.Core.Models;
using Hermod.Core.Models.Rules;
using Hermod.Core.Telemetry;
using Hermod.Rules.Indexing;
using Hermod.Rules.Payload;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hermod.Rules;

/// <summary>
/// Rules engine with trigger dispatch, complex condition evaluation,
/// chainable actions, state, scheduling, and availability tracking.
/// </summary>
public sealed partial class EnhancedRulesEngine : IRulesEngine, IDisposable
{
    /// <summary>Named <see cref="IHttpClientFactory"/> client used by webhook actions.</summary>
    public const string WebhookHttpClientName = "HermodWebhook";

    private const int MaxChainDepth = 16;
    private static readonly AsyncLocal<int> _chainDepth = new();

    private readonly IRulesService _rulesService;
    private readonly IMqttService _mqttService;
    private readonly IStatsService _statsService;
    private readonly IExpressionEvaluator _expressionEvaluator;
    private readonly IStateManager _stateManager;
    private readonly IScheduler _scheduler;
    private readonly IDeviceService? _deviceService;
    private readonly IEnumerable<IProtocolHandler> _protocolHandlers;
    private readonly IProtocolHandler _fallbackHandler;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IRuleAuditRepository _ruleAudit;
    private readonly HermodMetrics _metrics;
    private readonly ITimestampRecorder _timestampRecorder;
    private readonly ILogger<EnhancedRulesEngine> _logger;
    private readonly HermodSettings _settings;

    private readonly ConditionEvaluator _conditionEvaluator;
    private readonly JsonPayloadConverter _payloadConverter;

    private readonly TimeSpan _cacheRefreshInterval;
    private readonly bool _cacheEnabled;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private volatile RuleIndex _index = RuleIndex.Empty;
    private long _lastCacheRefreshTicks = DateTime.MinValue.Ticks;
    // Snapshot of rules.Count at the last cache rebuild — read O(1) by the
    // RulesController to enforce HermodLimits.MaxRules without a per-POST DB
    // round-trip. Slightly stale on bursts; the limit can be exceeded by a
    // tiny transient amount before the next rebuild closes the window.
    private int _totalRuleCount;
    /// <inheritdoc />
    public int TotalRuleCount => Volatile.Read(ref _totalRuleCount);

    private readonly ConcurrentDictionary<string, RuleStats> _pendingStats = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastTriggerTimes = new();
    private readonly Timer _statsFlushTimer;

    // Deduped so one bad rule doesn't spam the log per matching
    // message. Cleared on cache refresh so fixed rules re-surface.
    private readonly ConcurrentDictionary<(string, ActionType, string), byte>
        _reportedActionFailures = new();

    // Batched execution log: one Debug per firing was measurable
    // throughput tax; aggregator flushes summaries per interval.
    private readonly ConcurrentDictionary<string, LogAgg> _pendingLogs = new();
    private int _pendingLogCount;
    private readonly Timer? _logFlushTimer;
    private readonly int _logBatchSize;

    /// <summary>Raised after a rule's actions complete successfully.</summary>
    public event EventHandler<RuleExecutedEventArgs>? RuleExecuted;

    /// <summary>Raised when a rule throws during dispatch or action execution.</summary>
    public event EventHandler<RuleErrorEventArgs>? RuleError;

    /// <summary>Creates the rules engine and wires scheduler and device-availability handlers.</summary>
    public EnhancedRulesEngine(
        IRulesService rulesService,
        IMqttService mqttService,
        IStatsService statsService,
        IExpressionEvaluator expressionEvaluator,
        IStateManager stateManager,
        IScheduler scheduler,
        IEnumerable<IProtocolHandler> protocolHandlers,
        IHttpClientFactory httpClientFactory,
        IRuleAuditRepository ruleAudit,
        HermodMetrics metrics,
        IOptions<HermodSettings> settings,
        ILogger<EnhancedRulesEngine> logger,
        IDeviceService? deviceService = null,
        ITimestampRecorder? timestampRecorder = null)
    {
        ArgumentNullException.ThrowIfNull(rulesService);
        ArgumentNullException.ThrowIfNull(mqttService);
        ArgumentNullException.ThrowIfNull(statsService);
        ArgumentNullException.ThrowIfNull(expressionEvaluator);
        ArgumentNullException.ThrowIfNull(stateManager);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(protocolHandlers);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(ruleAudit);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);

        _rulesService = rulesService;
        _mqttService = mqttService;
        _statsService = statsService;
        _expressionEvaluator = expressionEvaluator;
        _stateManager = stateManager;
        _scheduler = scheduler;
        _protocolHandlers = protocolHandlers;
        _fallbackHandler = new FallbackProtocolHandler();
        _httpClientFactory = httpClientFactory;
        _ruleAudit = ruleAudit;
        _metrics = metrics;
        _timestampRecorder = timestampRecorder ?? NoopTimestampRecorder.Instance;
        _logger = logger;
        _deviceService = deviceService;
        _settings = settings.Value;

        _cacheEnabled = _settings.Features.RuleCache;
        var refreshSeconds = Math.Max(1, _settings.Engine.RuleCacheRefreshSeconds);
        _cacheRefreshInterval = TimeSpan.FromSeconds(refreshSeconds);

        _conditionEvaluator = new ConditionEvaluator(_expressionEvaluator);
        _payloadConverter = new JsonPayloadConverter(_expressionEvaluator);

        _scheduler.ItemDue += OnScheduledItemDue;
        if (_deviceService is not null)
        {
            _deviceService.AvailabilityChanged += OnDeviceAvailabilityChanged;
        }

        _statsFlushTimer = new Timer(
            async _ => await FlushStatsAsync(),
            null,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30));

        _logBatchSize = Math.Max(1, _settings.Engine.LogBatchSize);
        if (_settings.Engine.LogBatching)
        {
            var interval = TimeSpan.FromMilliseconds(Math.Max(50, _settings.Engine.LogBatchIntervalMs));
            _logFlushTimer = new Timer(_ => FlushExecutionLog(), null, interval, interval);
        }
    }

    /// <summary>
    /// Matches <paramref name="message"/> against every loaded OnMessage rule
    /// and dispatches in priority order. Skips dispatch when the pipeline
    /// marked the message as non-triggering.
    /// </summary>
    public async Task ProcessMessageAsync(ProcessedMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!message.ShouldTriggerRules) return;

        var index = await GetIndexAsync(cancellationToken);
        var matches = index.Messages.Match(message.OriginalMessage.Topic);
        if (matches.Count == 0) return;

        await DispatchMatchesAsync(matches, message, chainData: null, cancellationToken);
    }

    /// <summary>
    /// Runs the raw <paramref name="message"/> through the protocol handler
    /// pipeline, updates device state, then dispatches matching rules.
    /// </summary>
    public async Task ProcessMessageAsync(MqttMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var handler = _protocolHandlers.FindHandler(message.Topic) ?? _fallbackHandler;

        var processed = await handler.ProcessMessageAsync(message, cancellationToken);
        if (processed is null) return;

        if (!string.IsNullOrEmpty(processed.DeviceName) && processed.ParsedPayload is not null)
        {
            _stateManager.SetDeviceState(processed.DeviceName, processed.ParsedPayload);
        }

        await ProcessMessageAsync(processed, cancellationToken);
    }

    /// <summary>
    /// Triggers a rule by id, respecting the chain-depth safety cap that
    /// prevents rule cycles from blowing the stack.
    /// </summary>
    public async Task TriggerRuleAsync(string ruleId, Dictionary<string, object>? chainData = null,
        ProcessedMessage? sourceMessage = null, CancellationToken cancellationToken = default)
    {
        var depth = _chainDepth.Value;
        if (depth >= MaxChainDepth)
        {
            _logger.LogError("Rule chain depth limit ({Max}) reached while triggering {RuleId}; likely cycle",
                MaxChainDepth, ruleId);
            return;
        }

        var rule = await _rulesService.GetRuleAsync(ruleId, cancellationToken);
        if (rule is null || !rule.Enabled)
        {
            _logger.LogWarning("Cannot trigger rule {RuleId}: not found or disabled", ruleId);
            return;
        }

        _chainDepth.Value = depth + 1;
        try
        {
            await ProcessRuleAsync(rule, sourceMessage, chainData, cancellationToken);
        }
        finally
        {
            _chainDepth.Value = depth;
        }
    }

    /// <summary>Schedules a deferred trigger; returns the scheduler id for cancellation.</summary>
    public string ScheduleRuleTrigger(string ruleId, TimeSpan delay, Dictionary<string, object>? chainData = null) =>
        _scheduler.ScheduleDelay(ruleId, delay, chainData);

    /// <summary>Cancels a scheduled trigger previously returned by <see cref="ScheduleRuleTrigger"/>.</summary>
    public bool CancelScheduledTrigger(string scheduleId) => _scheduler.Cancel(scheduleId);

    /// <summary>
    /// Runs every OnStartup rule in priority order, then registers OnSchedule
    /// rules with the cron scheduler. Individual rule failures are logged and
    /// do not abort the remaining startup sequence.
    /// </summary>
    public async Task ExecuteStartupRulesAsync(CancellationToken cancellationToken = default)
    {
        var startupRules = await _rulesService.GetRulesByTriggerTypeAsync(TriggerType.OnStartup, cancellationToken);
        foreach (var rule in startupRules.Where(r => r.Enabled).OrderBy(r => r.Priority))
        {
            try
            {
                await ProcessRuleAsync(rule, null, null, cancellationToken);
                _logger.LogInformation("Executed startup rule: {RuleName}", rule.Name);
            }
#pragma warning disable CA1031 // one startup rule throwing must not block the remaining startup sequence
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogError(ex, "Error executing startup rule {RuleName}", rule.Name);
                _statsService.IncrementRulesErrored();
            }
        }

        var scheduledRules = await _rulesService.GetRulesByTriggerTypeAsync(TriggerType.OnSchedule, cancellationToken);
        foreach (var rule in scheduledRules.Where(r => r.Enabled && !string.IsNullOrEmpty(r.Trigger.Schedule)))
        {
            _scheduler.ScheduleCron(rule.Id, rule.Trigger.Schedule!);
            _logger.LogDebug("Registered cron schedule for rule {RuleName}: {Schedule}",
                rule.Name, rule.Trigger.Schedule);
        }
    }

    /// <summary>Reads a global state key; returns <c>default(T)</c> when missing.</summary>
    public T? GetGlobalState<T>(string key) => _stateManager.GetGlobal<T>(key);

    /// <summary>Assigns a global state key.</summary>
    public void SetGlobalState(string key, object value) => _stateManager.SetGlobal(key, value);

    /// <summary>Marks the rule index stale so the next dispatch rebuilds from the repository.</summary>
    public void InvalidateCache()
    {
        Volatile.Write(ref _lastCacheRefreshTicks, DateTime.MinValue.Ticks);
        _logger.LogDebug("Rule cache invalidated");
    }

    /// <summary>
    /// Unsubscribes from scheduler / device events, disposes timers, and flushes
    /// any pending stats/log batches. Safe to call more than once.
    /// </summary>
    public void Dispose()
    {
        _scheduler.ItemDue -= OnScheduledItemDue;
        if (_deviceService is not null)
        {
            _deviceService.AvailabilityChanged -= OnDeviceAvailabilityChanged;
        }
        _statsFlushTimer.Dispose();
        _logFlushTimer?.Dispose();

        try
        {
            // Best-effort flush; drop stats rather than hang shutdown.
            Task.Run(FlushStatsAsync).Wait(TimeSpan.FromSeconds(2));
        }
#pragma warning disable CA1031 // Dispose must never throw; a failed flush is logged via the final execution-log emission
        catch
#pragma warning restore CA1031
        {
        }

        FlushExecutionLog();

        _cacheLock.Dispose();
    }

    private void AccumulateExecutionLog(string ruleId, double elapsedMs)
    {
        _pendingLogs.AddOrUpdate(
            ruleId,
            _ => new LogAgg { Count = 1, TotalMs = elapsedMs, MaxMs = elapsedMs },
            (_, agg) =>
            {
                // Summary aggregation tolerates a racing writer losing
                // one observation of itself.
                Interlocked.Increment(ref agg.Count);
                agg.TotalMs += elapsedMs;
                if (elapsedMs > agg.MaxMs) agg.MaxMs = elapsedMs;
                return agg;
            });

        if (Interlocked.Increment(ref _pendingLogCount) >= _logBatchSize)
        {
            FlushExecutionLog();
        }
    }

    private void FlushExecutionLog()
    {
        if (_pendingLogs.IsEmpty) return;

        List<KeyValuePair<string, LogAgg>> snapshot = [];
        foreach (var key in _pendingLogs.Keys.ToList())
        {
            if (_pendingLogs.TryRemove(key, out var agg))
            {
                snapshot.Add(new KeyValuePair<string, LogAgg>(key, agg));
            }
        }
        Interlocked.Exchange(ref _pendingLogCount, 0);

        if (snapshot.Count == 0) return;

        var total = 0;
        double totalMs = 0;
        var topRuleId = snapshot[0].Key;
        var topMax = snapshot[0].Value.MaxMs;
        foreach (var kvp in snapshot)
        {
            total += kvp.Value.Count;
            totalMs += kvp.Value.TotalMs;
            if (kvp.Value.MaxMs > topMax)
            {
                topMax = kvp.Value.MaxMs;
                topRuleId = kvp.Key;
            }
        }

        _logger.LogDebug(
            "Rule exec batch: {Total} firings across {Distinct} rules, avg={AvgMs:F2}ms, top={TopRuleId}@{TopMs:F2}ms",
            total, snapshot.Count, total == 0 ? 0 : totalMs / total, topRuleId, topMax);
    }

    private sealed class LogAgg
    {
        public int Count;
        public double TotalMs;
        public double MaxMs;
    }

    private async Task DispatchMatchesAsync(
        List<Rule> matches,
        ProcessedMessage message,
        Dictionary<string, object>? chainData,
        CancellationToken cancellationToken)
    {
        matches.Sort(static (a, b) => a.Priority.CompareTo(b.Priority));

        foreach (var rule in matches)
        {
            try
            {
                await ProcessRuleAsync(rule, message, chainData, cancellationToken);
            }
#pragma warning disable CA1031 // one rule throwing must not stop dispatch of the remaining matches; error is surfaced via RuleError event
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogError(ex, "Error processing rule {RuleId}", rule.Id);
                RecordRuleError(rule.Id, ex.Message);
                _statsService.IncrementRulesErrored();
                RuleError?.Invoke(this, new RuleErrorEventArgs
                {
                    Rule = rule,
                    Message = message,
                    Exception = ex,
                });
            }
        }
    }

    private void OnScheduledItemDue(object? sender, ScheduledItemDueEventArgs e)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await TriggerRuleAsync(e.Item.RuleId, e.Item.ChainData);
            }
#pragma warning disable CA1031 // fire-and-forget scheduler callback; swallowing is the only way to keep the tick loop alive
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogError(ex, "Error executing scheduled rule {RuleId}", e.Item.RuleId);
                _statsService.IncrementRulesErrored();
            }
        });
    }

    private void OnDeviceAvailabilityChanged(object? sender, DeviceAvailabilityChangedEventArgs e)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await DispatchAvailabilityAsync(e, CancellationToken.None);
            }
#pragma warning disable CA1031 // fire-and-forget availability-change callback; swallowing keeps the device event bus unblocked
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogError(ex, "Error dispatching OnAvailability rules for device {DeviceId}", e.DeviceId);
            }
        });
    }

    private async Task DispatchAvailabilityAsync(DeviceAvailabilityChangedEventArgs e, CancellationToken cancellationToken)
    {
        var index = await GetIndexAsync(cancellationToken);
        if (index.Availability.Count == 0) return;

        var topic = !string.IsNullOrEmpty(e.Topic) ? e.Topic : $"availability/{e.DeviceId}";
        var matches = index.Availability.Match(topic);
        if (matches.Count == 0) return;

        var synthetic = new ProcessedMessage
        {
            OriginalMessage = new MqttMessage { Topic = topic, Payload = string.Empty },
            DeviceName = e.DeviceId,
            ShouldTriggerRules = true,
            ParsedPayload = new Dictionary<string, object>
            {
                ["deviceId"] = e.DeviceId,
                ["previousStatus"] = e.PreviousStatus.ToString(),
                ["currentStatus"] = e.CurrentStatus.ToString(),
                ["isOnline"] = e.CurrentStatus == DeviceStatus.Online,
                ["changedAt"] = e.ChangedAt,
            },
        };

        await DispatchMatchesAsync(matches, synthetic, chainData: null, cancellationToken);
    }

    private async Task<RuleIndex> GetIndexAsync(CancellationToken cancellationToken)
    {
        // Cache disabled == baseline for the no-cache pairing profile.
        if (!_cacheEnabled)
        {
            _metrics.IncRuleCacheMisses();
            var uncachedRules = await _rulesService.GetAllRulesAsync(cancellationToken);
            return RuleIndex.Build(uncachedRules, _logger);
        }

        var nowTicks = DateTime.UtcNow.Ticks;
        if (nowTicks - Volatile.Read(ref _lastCacheRefreshTicks) < _cacheRefreshInterval.Ticks)
        {
            _metrics.IncRuleCacheHits();
            return _index;
        }

        // Non-blocking rebuild: only one thread rebuilds at a time; other
        // readers see the stale (but valid) _index and do NOT wait for the
        // rebuild to finish. Before this, every ingest worker that happened
        // to straddle the expiry tick blocked behind the rebuild, and at
        // 10K+ rules the GetAllRulesAsync + RuleIndex.Build walltime was
        // long enough to starve the ingest drain and cause drops.
        if (!await _cacheLock.WaitAsync(0, cancellationToken))
        {
            _metrics.IncRuleCacheHits();
            return _index;
        }
        try
        {
            if (DateTime.UtcNow.Ticks - Volatile.Read(ref _lastCacheRefreshTicks) < _cacheRefreshInterval.Ticks)
            {
                _metrics.IncRuleCacheHits();
                return _index;
            }
            _metrics.IncRuleCacheMisses();

            var rules = await _rulesService.GetAllRulesAsync(cancellationToken);
            var rulesList = rules as IList<Rule> ?? rules.ToList();
            Volatile.Write(ref _totalRuleCount, rulesList.Count);
            var next = RuleIndex.Build(rulesList, _logger);

            // Seed rule.State only on first observation so in-memory
            // SetState mutations survive cache rebuilds.
            foreach (var rule in rulesList)
            {
                if (rule.State.Count > 0 && !_stateManager.HasRuleState(rule.Id))
                {
                    _stateManager.ImportRuleState(rule.Id, rule.State);
                }
            }

            _index = next;
            Volatile.Write(ref _lastCacheRefreshTicks, DateTime.UtcNow.Ticks);
            // Reset log-once dedup so fixed rules re-surface warnings.
            _reportedActionFailures.Clear();
            _logger.LogDebug("Rule cache refreshed: {MessageRules} message rules, {AvailabilityRules} availability rules",
                next.Messages.Count, next.Availability.Count);

            return next;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private bool CheckDebounce(Rule rule)
    {
        var now = DateTime.UtcNow;
        var debounceSpan = DelayParser.Parse(rule.Trigger.Debounce!);

        if (_lastTriggerTimes.TryGetValue(rule.Id, out var lastTrigger) &&
            now - lastTrigger < debounceSpan)
        {
            return false;
        }

        _lastTriggerTimes[rule.Id] = now;
        return true;
    }

    private static bool IsInActiveWindow(List<TimeWindow> windows)
    {
        var now = DateTime.UtcNow;
        var currentTime = TimeOnly.FromDateTime(now);
        var currentDay = now.DayOfWeek;

        foreach (var window in windows)
        {
            if (window.Days is not null && !window.Days.Contains(currentDay))
            {
                continue;
            }

            var matches = window.Start <= window.End
                ? currentTime >= window.Start && currentTime <= window.End
                : currentTime >= window.Start || currentTime <= window.End;

            if (matches) return true;
        }

        return false;
    }

    private ExpressionContext BuildContext(Rule rule, ProcessedMessage? message, Dictionary<string, object>? chainData)
    {
        // Cached snapshot — rebuilt lazily only after a global-state write.
        var globalSnapshot = _stateManager.GetGlobalSnapshot();

        // Snapshot rule state under the live-dict lock — GetRuleState returns
        // the backing Dictionary ref, and Dictionary<TK,TV>.TryGetValue raced
        // against SetRuleState's resize can throw IndexOutOfRangeException.
        // Parallel-actions within a rule fire (Engine.Parallelism=4) share
        // this context, so concurrent state reads and set_state writes are
        // reachable. Copy-in / write-through on SetRuleState keeps callers
        // observing each others' writes on the NEXT fire.
        var liveState = _stateManager.GetRuleState(rule.Id);
        Dictionary<string, object> stateSnapshot;
        lock (liveState) { stateSnapshot = new Dictionary<string, object>(liveState); }

        return new ExpressionContext
        {
            Source = message?.ParsedPayload ?? [],
            Topic = message?.OriginalMessage.Topic ?? string.Empty,
            DeviceName = message?.DeviceName,
            TraceUuid = message?.OriginalMessage.TraceUuid,
            State = stateSnapshot,
            Global = globalSnapshot is Dictionary<string, object> dict
                ? dict
                : new Dictionary<string, object>(globalSnapshot),
            ChainData = chainData,
            Previous = message?.DeviceName is not null
                ? _stateManager.GetPreviousDeviceState(message.DeviceName)
                : null,
            Now = DateTime.UtcNow,
            GetDeviceState = _stateManager.GetDeviceState,
        };
    }

    private void RecordRuleExecution(string ruleId)
    {
        _pendingStats.AddOrUpdate(
            ruleId,
            _ => new RuleStats { ExecutionCount = 1, LastExecutedAt = DateTime.UtcNow },
            (_, stats) =>
            {
                Interlocked.Increment(ref stats.ExecutionCount);
                stats.LastExecutedAt = DateTime.UtcNow;
                return stats;
            });
    }

    private void RecordRuleError(string ruleId, string error)
    {
        _pendingStats.AddOrUpdate(
            ruleId,
            _ => new RuleStats { LastError = error, LastErrorAt = DateTime.UtcNow },
            (_, stats) =>
            {
                stats.LastError = error;
                stats.LastErrorAt = DateTime.UtcNow;
                return stats;
            });
    }

    private async Task FlushStatsAsync()
    {
        if (_pendingStats.IsEmpty) return;

        // Snapshot + drain via direct enumerator so we don't allocate a
        // list of keys. TryRemove races with writers that may have added
        // more entries since we started; those just wait for the next tick.
        List<RuleStatsUpdate> updates = [];
        foreach (var kvp in _pendingStats)
        {
            if (_pendingStats.TryRemove(kvp.Key, out var stats))
            {
                updates.Add(new RuleStatsUpdate(
                    RuleId: kvp.Key,
                    DeltaExecutionCount: stats.ExecutionCount,
                    LastExecutedAt: stats.LastExecutedAt,
                    LastError: stats.LastError,
                    LastErrorAt: stats.LastErrorAt));
            }
        }

        if (updates.Count == 0) return;

        try
        {
            await _rulesService.BulkUpdateStatsAsync(updates);
        }
#pragma warning disable CA1031 // stats flush is best-effort; DB hiccups must not break the hot path or the periodic flush timer
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "Failed to flush stats batch ({Count} rules)", updates.Count);
        }
    }

    private sealed class RuleStats
    {
        public int ExecutionCount;
        public DateTime LastExecutedAt;
        public string? LastError;
        public DateTime? LastErrorAt;
    }
}
