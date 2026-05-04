namespace Hermod.Core.Telemetry;

/// <summary>
/// Null-object recorder bound when no <c>Hermod:Telemetry:TimestampsCsvPath</c>
/// is configured. Every call returns immediately so hot-path callers can
/// stamp unconditionally without a null check or a feature-flag branch.
/// </summary>
public sealed class NoopTimestampRecorder : ITimestampRecorder
{
    /// <summary>Singleton instance; the recorder is stateless.</summary>
    public static readonly NoopTimestampRecorder Instance = new();

    private NoopTimestampRecorder()
    {
    }

    /// <inheritdoc/>
    public void Record(string uuid, string stage, long timestampNs)
    {
    }
}
