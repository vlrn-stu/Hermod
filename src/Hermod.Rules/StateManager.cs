using System.Collections.Concurrent;
using System.Text.Json;
using Hermod.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Hermod.Rules;

/// <summary>
/// In-memory state manager with optional file-based persistence.
/// Thread-safe for concurrent access.
/// </summary>
public class StateManager : IStateManager
{
    private static readonly JsonSerializerOptions PersistOptions = new() { WriteIndented = true };

    private readonly ConcurrentDictionary<string, Dictionary<string, object>> _ruleStates = new();
    private readonly ConcurrentDictionary<string, object> _globalState = new();
    private readonly ConcurrentDictionary<string, DeviceStateEntry> _deviceStates = new();
    private readonly ILogger<StateManager>? _logger;
    private readonly string? _persistPath;

    // Cached immutable snapshot of the global-state dict. BuildContext on
    // the rule hot path reads this every firing; rebuilding a fresh dict
    // from ConcurrentDictionary on each call was a per-message allocation.
    // Invalidated by writes via the dirty flag below.
    private IReadOnlyDictionary<string, object> _globalSnapshot = new Dictionary<string, object>();
    private volatile bool _globalSnapshotDirty = true;
    private readonly object _globalSnapshotLock = new();

    // Immutable so concurrent SetDeviceState calls cannot produce a torn
    // previous/current split when ConcurrentDictionary's update factory
    // is invoked multiple times under contention.
    private sealed class DeviceStateEntry(Dictionary<string, object> current, Dictionary<string, object>? previous = null)
    {
        public Dictionary<string, object> Current { get; } = current;
        public Dictionary<string, object>? Previous { get; } = previous;
    }

    /// <summary>
    /// Creates a state manager, optionally persisting to
    /// <paramref name="persistPath"/> when <see cref="PersistAsync"/> is called.
    /// </summary>
    public StateManager(ILogger<StateManager>? logger = null, string? persistPath = null)
    {
        _logger = logger;
        _persistPath = persistPath;
    }

    /// <summary>
    /// Returns the live backing dictionary for a rule's state (creating it if
    /// absent). Enumerating the result under a concurrent <see cref="SetRuleState"/>
    /// requires a <c>lock (result)</c>, or use <see cref="SnapshotRuleStates"/>.
    /// </summary>
    public Dictionary<string, object> GetRuleState(string ruleId)
    {
        return _ruleStates.GetOrAdd(ruleId, _ => []);
    }

    /// <summary>Sets a single key on a rule's state dict under an exclusive lock.</summary>
    public void SetRuleState(string ruleId, string key, object value)
    {
        var state = GetRuleState(ruleId);
        lock (state)
        {
            state[key] = value;
        }
    }

    /// <summary>Removes any stored state for <paramref name="ruleId"/>.</summary>
    public void ClearRuleState(string ruleId)
    {
        _ruleStates.TryRemove(ruleId, out _);
    }

    /// <summary>Returns whether any state has been recorded for <paramref name="ruleId"/>.</summary>
    public bool HasRuleState(string ruleId) => _ruleStates.ContainsKey(ruleId);

    /// <summary>Imports a rule state from a persistence payload; replaces any existing entry.</summary>
    public void ImportRuleState(string ruleId, Dictionary<string, object> state)
    {
        ArgumentNullException.ThrowIfNull(state);
        // Defensive copy so the caller's dict cannot mutate the stored map.
        _ruleStates[ruleId] = new Dictionary<string, object>(state);
    }

    /// <summary>Returns a defensive deep copy of every rule state for flushing to disk.</summary>
    public IReadOnlyDictionary<string, Dictionary<string, object>> SnapshotRuleStates()
    {
        // Defensive copy of every tracked state so a flush walking the result
        // cannot observe mid-update tearing from concurrent SetRuleState calls.
        var snapshot = new Dictionary<string, Dictionary<string, object>>(_ruleStates.Count);
        foreach (var (ruleId, state) in _ruleStates)
        {
            lock (state)
            {
                snapshot[ruleId] = new Dictionary<string, object>(state);
            }
        }
        return snapshot;
    }

    /// <summary>Reads a global key as <typeparamref name="T"/>; returns <c>default</c> when absent or uncoerceable.</summary>
    public T? GetGlobal<T>(string key)
    {
        if (!_globalState.TryGetValue(key, out var value))
            return default;

        if (value is T typed)
            return typed;

        try
        {
            if (value is JsonElement je)
            {
                return JsonSerializer.Deserialize<T>(je.GetRawText());
            }

            return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value));
        }
#pragma warning disable CA1031 // conversion failures degrade to default rather than leaking into the rule hot path
        catch
#pragma warning restore CA1031
        {
            return default;
        }
    }

    /// <summary>Reads a global key; returns <paramref name="defaultValue"/> when absent.</summary>
    public T GetGlobal<T>(string key, T defaultValue)
    {
        return GetGlobal<T>(key) ?? defaultValue;
    }

    /// <summary>Assigns a global key and marks the cached snapshot dirty.</summary>
    public void SetGlobal(string key, object value)
    {
        _globalState[key] = value;
        _globalSnapshotDirty = true;
    }

    /// <summary>Returns whether the given global key is present.</summary>
    public bool HasGlobal(string key)
    {
        return _globalState.ContainsKey(key);
    }

    /// <summary>Removes a global key; returns <c>true</c> when a value was removed.</summary>
    public bool RemoveGlobal(string key)
    {
        var removed = _globalState.TryRemove(key, out _);
        if (removed) _globalSnapshotDirty = true;
        return removed;
    }

    /// <summary>Enumerates the current global key set.</summary>
    public IEnumerable<string> GetGlobalKeys()
    {
        return _globalState.Keys;
    }

    /// <summary>
    /// Immutable read-only view of the global state, rebuilt lazily only
    /// when a writer has touched the backing dict since the last read. Safe
    /// to hand out by reference because the returned map is never mutated
    /// after it is published.
    /// </summary>
    public IReadOnlyDictionary<string, object> GetGlobalSnapshot()
    {
        if (!_globalSnapshotDirty) return _globalSnapshot;

        lock (_globalSnapshotLock)
        {
            if (!_globalSnapshotDirty) return _globalSnapshot;
            // Clear dirty BEFORE reading the dict. A writer racing the
            // enumeration below will re-raise dirty via SetGlobal's
            // _globalSnapshotDirty=true, and the next reader will rebuild.
            // Previous order (clear-after-read) lost the writer's update:
            // writer sets dirty, we clear it, snapshot missed the write.
            _globalSnapshotDirty = false;
            var fresh = new Dictionary<string, object>(_globalState.Count);
            foreach (var kvp in _globalState)
            {
                fresh[kvp.Key] = kvp.Value;
            }
            _globalSnapshot = fresh;
            return _globalSnapshot;
        }
    }

    /// <summary>Returns a defensive copy of the current state for <paramref name="deviceName"/>, or <c>null</c>.</summary>
    public Dictionary<string, object>? GetDeviceState(string deviceName)
    {
        if (_deviceStates.TryGetValue(deviceName, out var entry))
            return new Dictionary<string, object>(entry.Current);

        return null;
    }

    /// <summary>
    /// Replaces the current state for <paramref name="deviceName"/>, preserving
    /// the previous snapshot for <c>{{previous.*}}</c> lookups.
    /// </summary>
    public void SetDeviceState(string deviceName, Dictionary<string, object> state)
    {
        ArgumentNullException.ThrowIfNull(state);
        // Defensive-copy the caller's dict once, so the entry owns its
        // Current and the caller cannot mutate the stored state after
        // the call returns.
        var newCurrent = new Dictionary<string, object>(state);

        _deviceStates.AddOrUpdate(
            deviceName,
            _ => new DeviceStateEntry(newCurrent),
            (_, existing) => new DeviceStateEntry(newCurrent, previous: existing.Current));
    }

    /// <summary>Returns a defensive copy of the prior state for <paramref name="deviceName"/>, or <c>null</c>.</summary>
    public Dictionary<string, object>? GetPreviousDeviceState(string deviceName)
    {
        if (_deviceStates.TryGetValue(deviceName, out var entry) && entry.Previous != null)
            return new Dictionary<string, object>(entry.Previous);

        return null;
    }

    /// <summary>Writes every rule and global state to the configured persistence path.</summary>
    public async Task PersistAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_persistPath))
            return;

        try
        {
            // Snapshot under per-dict monitors so a concurrent SetRuleState
            // cannot tear a mid-serialize enumeration.
            var state = new PersistedState
            {
                RuleStates = SnapshotRuleStates().ToDictionary(x => x.Key, x => x.Value),
                GlobalState = _globalState.ToDictionary(x => x.Key, x => x.Value),
            };

            var json = JsonSerializer.Serialize(state, PersistOptions);
            await File.WriteAllTextAsync(_persistPath, json, cancellationToken);

            _logger?.LogDebug("State persisted to {Path}", _persistPath);
        }
#pragma warning disable CA1031 // persistence is best-effort; I/O or JSON errors are logged and swallowed rather than crashing the engine
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger?.LogError(ex, "Failed to persist state to {Path}", _persistPath);
        }
    }

    /// <summary>Loads persisted rule and global states from the configured path, if it exists.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_persistPath) || !File.Exists(_persistPath))
            return;

        try
        {
            var json = await File.ReadAllTextAsync(_persistPath, cancellationToken);
            var state = JsonSerializer.Deserialize<PersistedState>(json);

            if (state?.RuleStates != null)
            {
                foreach (var (ruleId, ruleState) in state.RuleStates)
                {
                    _ruleStates[ruleId] = ruleState;
                }
            }

            if (state?.GlobalState != null)
            {
                foreach (var (key, value) in state.GlobalState)
                {
                    _globalState[key] = value;
                }
            }

            _logger?.LogInformation("State loaded from {Path}", _persistPath);
        }
#pragma warning disable CA1031 // load is best-effort at startup; missing/corrupt file falls back to an empty in-memory store
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger?.LogError(ex, "Failed to load state from {Path}", _persistPath);
        }
    }

    private sealed class PersistedState
    {
        public Dictionary<string, Dictionary<string, object>>? RuleStates { get; set; }
        public Dictionary<string, object>? GlobalState { get; set; }
    }
}
