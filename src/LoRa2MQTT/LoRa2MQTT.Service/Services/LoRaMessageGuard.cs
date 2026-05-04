using System.Collections.Concurrent;
using LoRa2MQTT.Service.Configuration;
using LoRa2MQTT.Service.Models;
using Microsoft.Extensions.Options;

namespace LoRa2MQTT.Service.Services;

/// <summary>
/// Inspects inbound LoRa messages and decides whether they should be
/// forwarded to MQTT. Enforces the configurable guards in
/// <see cref="LoRaSecurityOptions"/>: payload cap, per-address rate limit,
/// duplicate suppression, and an optional source-address allowlist.
/// </summary>
public sealed class LoRaMessageGuard
{
    private readonly LoRaSecurityOptions _options;
    private readonly ConcurrentDictionary<int, RateWindow> _rateState = new();
    private readonly ConcurrentDictionary<int, ReplayTracker> _replayState = new();
    private readonly HashSet<int> _allowlist;

    /// <summary>
    /// Initializes a new instance of <see cref="LoRaMessageGuard"/>.
    /// </summary>
    /// <param name="options">Security options carrying the configured guard thresholds.</param>
    public LoRaMessageGuard(IOptions<LoRaSecurityOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _allowlist = new HashSet<int>(_options.AddressAllowlist ?? Array.Empty<int>());
    }

    /// <summary>
    /// Decision record. <see cref="Accept"/> false means drop the message;
    /// <see cref="Reason"/> carries a human-readable explanation (logged
    /// at warn level by the bridge worker).
    /// </summary>
    public readonly record struct GuardVerdict(bool Accept, string? Reason);

    /// <summary>Inspects <paramref name="message"/> using the current wall-clock time.</summary>
    public GuardVerdict Inspect(LoRaMessage message) =>
        Inspect(message, DateTimeOffset.UtcNow);

    /// <summary>Time-injectable inspection for deterministic tests.</summary>
    public GuardVerdict Inspect(LoRaMessage message, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(message);
        // Length is char count; ASCII from hardware means char==byte.
        // A non-ASCII payload would under-count vs the configured cap.
        if (_options.MaxPayloadBytes > 0 && message.Payload.Length > _options.MaxPayloadBytes)
        {
            return new GuardVerdict(false, $"payload exceeds MaxPayloadBytes ({message.Payload.Length} > {_options.MaxPayloadBytes})");
        }

        if (_allowlist.Count > 0 && !_allowlist.Contains(message.Address))
        {
            return new GuardVerdict(false, $"address {message.Address} not in allowlist");
        }

        if (_options.DedupWindowSeconds > 0 && IsReplay(message, now))
        {
            return new GuardVerdict(false, "duplicate within dedup window");
        }

        if (_options.MaxMessagesPerMinutePerAddress > 0)
        {
            var window = _rateState.GetOrAdd(message.Address, _ => new RateWindow());
            if (!window.TryAdmit(_options.MaxMessagesPerMinutePerAddress, now))
            {
                return new GuardVerdict(false, $"rate limit exceeded ({_options.MaxMessagesPerMinutePerAddress}/min)");
            }
        }

        return new GuardVerdict(true, null);
    }

    private bool IsReplay(LoRaMessage message, DateTimeOffset now)
    {
        var tracker = _replayState.GetOrAdd(message.Address, _ => new ReplayTracker());
        return tracker.IsReplay(message.Payload, now, _options.DedupWindowSeconds);
    }

    /// <summary>
    /// Per-address sliding window of recent payloads. Keeps up to
    /// <see cref="MaxEntries"/> payloads and prunes anything older than
    /// the configured window on every check, catching alternating
    /// replay patterns (A, B, A, B, …). Compares full payload strings
    /// so hash collisions cannot drop a legitimate message.
    /// </summary>
    private sealed class ReplayTracker
    {
        private const int MaxEntries = 64;
        private readonly LinkedList<(string Payload, DateTimeOffset At)> _entries = new();
        private readonly object _lock = new();

        public bool IsReplay(string payload, DateTimeOffset now, int windowSec)
        {
            if (windowSec <= 0) return false;

            lock (_lock)
            {
                var cutoff = now.AddSeconds(-windowSec);
                while (_entries.First is { } first && first.Value.At < cutoff)
                {
                    _entries.RemoveFirst();
                }

                for (var node = _entries.First; node is not null; node = node.Next)
                {
                    if (string.Equals(node.Value.Payload, payload, StringComparison.Ordinal)) return true;
                }

                _entries.AddLast((payload, now));
                while (_entries.Count > MaxEntries)
                {
                    _entries.RemoveFirst();
                }
                return false;
            }
        }
    }

    private sealed class RateWindow
    {
        private readonly object _lock = new();
        private readonly Queue<DateTimeOffset> _timestamps = new();

        public bool TryAdmit(int maxPerMinute, DateTimeOffset now)
        {
            lock (_lock)
            {
                var cutoff = now.AddSeconds(-60);
                while (_timestamps.Count > 0 && _timestamps.Peek() < cutoff)
                {
                    _timestamps.Dequeue();
                }
                if (_timestamps.Count >= maxPerMinute) return false;
                _timestamps.Enqueue(now);
                return true;
            }
        }
    }
}
