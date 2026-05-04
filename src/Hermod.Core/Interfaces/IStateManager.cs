namespace Hermod.Core.Interfaces;

/// <summary>
/// In-memory state store backing the rules engine. Holds three namespaces
/// (per-rule, global, per-device) plus optional JSON-file persistence that
/// is a no-op when no path is configured.
/// </summary>
public interface IStateManager
{
    /// <summary>Returns the rule's state dict, creating an empty one if none exists. Never null.</summary>
    Dictionary<string, object> GetRuleState(string ruleId);
    /// <summary>Writes a single <paramref name="key"/>/<paramref name="value"/> entry into the rule's state.</summary>
    void SetRuleState(string ruleId, string key, object value);

    /// <summary>Drops every key from the rule's state.</summary>
    void ClearRuleState(string ruleId);

    /// <summary>True iff the state manager already holds state for <paramref name="ruleId"/>. Used to avoid clobbering live state during periodic rule-index rebuilds.</summary>
    bool HasRuleState(string ruleId);

    /// <summary>
    /// Overwrites the rule's state with <paramref name="state"/>. Called at
    /// engine startup to seed from the persisted <c>Rule.State</c> column.
    /// </summary>
    void ImportRuleState(string ruleId, Dictionary<string, object> state);

    /// <summary>Snapshot of every tracked rule state, for flushing back to <c>Rule.State</c>.</summary>
    IReadOnlyDictionary<string, Dictionary<string, object>> SnapshotRuleStates();

    /// <summary>Reads global state coerced to <typeparamref name="T"/>, or <c>default</c> if missing or not convertible.</summary>
    T? GetGlobal<T>(string key);

    /// <summary>Reads global state coerced to <typeparamref name="T"/>, falling back to <paramref name="defaultValue"/>.</summary>
    T GetGlobal<T>(string key, T defaultValue);

    /// <summary>Writes a value into the shared global state.</summary>
    void SetGlobal(string key, object value);

    /// <summary>True if a global value exists at <paramref name="key"/>.</summary>
    bool HasGlobal(string key);

    /// <summary>Removes <paramref name="key"/> from global state. Returns false if the key was not present.</summary>
    bool RemoveGlobal(string key);

    /// <summary>Enumerates every global-state key currently set.</summary>
    IEnumerable<string> GetGlobalKeys();

    /// <summary>
    /// Cached immutable view of the global state. Hot-path callers (the
    /// rule engine's context builder) can use this in place of building a
    /// fresh dict from <see cref="GetGlobalKeys"/> every time. The snapshot
    /// is rebuilt lazily only after a write has invalidated it; safe to
    /// hand out by reference because the returned map is never mutated
    /// after publication.
    /// </summary>
    IReadOnlyDictionary<string, object> GetGlobalSnapshot();

    /// <summary>Last observed state for a device, or null if none has been recorded.</summary>
    Dictionary<string, object>? GetDeviceState(string deviceName);

    /// <summary>
    /// Records a new state for <paramref name="deviceName"/>. The prior value is
    /// retained in a parallel slot so <see cref="GetPreviousDeviceState"/> can
    /// drive change-detection conditions.
    /// </summary>
    void SetDeviceState(string deviceName, Dictionary<string, object> state);

    /// <summary>State from the previous <see cref="SetDeviceState"/> call, for edge/diff conditions.</summary>
    Dictionary<string, object>? GetPreviousDeviceState(string deviceName);

    /// <summary>Flushes state to the configured persistence path. No-op if unset.</summary>
    Task PersistAsync(CancellationToken cancellationToken = default);

    /// <summary>Restores state from the persistence path on startup. No-op if unset or missing.</summary>
    Task LoadAsync(CancellationToken cancellationToken = default);
}
