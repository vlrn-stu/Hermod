using System;
using System.Text;
using Hermod.Core.Mqtt;
using Xunit;

namespace Hermod.UnitTests;

/// <summary>
/// Attack-surface tests for <see cref="PayloadUuidExtractor"/>. The
/// extractor runs on every inbound MQTT message when UUID tracing is
/// enabled, so a malformed or adversarial payload must degrade
/// gracefully: the extractor returns null on anything it can't parse
/// and never throws, never blocks, never leaks memory. Tests here pin
/// both the fast-path substring gate (no JSON parse when the literal
/// <c>"_uuid"</c> key isn't present) and the downstream parse path's
/// resilience to depth, size, and value-shape abuse.
/// </summary>
public class PayloadUuidExtractorAttackTests
{
    [Fact]
    public void TryExtract_DeeplyNestedObject_DoesNotThrow()
    {
        // JsonDocument's default MaxDepth is 64; a deeper payload
        // triggers JsonException, which the extractor swallows.
        // Constructing 200 levels of nesting exercises both the
        // fast-path "_uuid" substring scan (which DOES trip on the
        // marker at the bottom) and the subsequent JsonDocument
        // rejection. Must return null, never throw.
        var nested = new StringBuilder();
        for (var i = 0; i < 200; i++) nested.Append("{\"x\":");
        nested.Append("{\"_uuid\":\"bomb\"}");
        for (var i = 0; i < 200; i++) nested.Append('}');

        Assert.Null(PayloadUuidExtractor.TryExtract(nested.ToString()));
    }

    [Fact]
    public void TryExtract_UuidInsideNestedObject_NotExtracted()
    {
        // Top-level-only: a `_uuid` nested inside another object must
        // not be returned. Otherwise an attacker could smuggle a
        // correlation id via a payload sub-field that was supposed to
        // be opaque to the coordinator.
        var payload = "{\"data\": {\"_uuid\": \"nested\"}, \"state\": \"ON\"}";

        Assert.Null(PayloadUuidExtractor.TryExtract(payload));
    }

    [Fact]
    public void TryExtract_DuplicateUuidKeys_LastWinsPerSystemTextJson()
    {
        // JSON technically allows duplicate keys; System.Text.Json
        // returns the last one via TryGetProperty. Pin this so a
        // later switch of deserializer or an upgrade can't silently
        // flip which side wins (that would let an attacker override a
        // legitimate id appended by a trusted producer).
        var payload = "{\"_uuid\":\"first\",\"_uuid\":\"second\"}";

        Assert.Equal("second", PayloadUuidExtractor.TryExtract(payload));
    }

    [Fact]
    public void TryExtract_EmptyStringUuid_ReturnedAsEmpty()
    {
        // An empty string is a valid JSON string. The extractor
        // returns it verbatim; downstream (FileTimestampRecorder)
        // already ignores empty uuids when recording, so an empty
        // value is effectively dropped but does not cause a crash here.
        var payload = "{\"_uuid\":\"\"}";

        Assert.Equal("", PayloadUuidExtractor.TryExtract(payload));
    }

    [Fact]
    public void TryExtract_WhitespaceUuid_ReturnedVerbatim()
    {
        // Attacker tries to slip a whitespace-only id in. Extractor
        // preserves it as-is; downstream correlators treat it like
        // any other string — it becomes a distinct trace bucket.
        var payload = "{\"_uuid\":\"   \"}";

        Assert.Equal("   ", PayloadUuidExtractor.TryExtract(payload));
    }

    [Fact]
    public void TryExtract_UuidWithControlChars_PreservedVerbatim()
    {
        // A payload carrying newlines / tab in the uuid value could
        // corrupt a naive downstream CSV writer. Extractor returns
        // the raw string; FileTimestampRecorder's csv writer is
        // responsible for handling such values safely via its
        // delimiter discipline. Pinning the extractor's neutrality
        // here: it does not silently sanitise.
        var payload = "{\"_uuid\":\"a\\nb\\tc\"}";

        Assert.Equal("a\nb\tc", PayloadUuidExtractor.TryExtract(payload));
    }

    [Fact]
    public void TryExtract_UuidKeyWithTrailingSpace_FastPathMisses()
    {
        // The fast-path substring probe looks for `"_uuid"` exactly.
        // A key like `"_uuid "` (trailing space inside the key
        // string) would not be a valid duplicate, and System.Text.Json
        // would resolve it as a different property. Both paths
        // conspire to produce null. Pin so a future refactor that
        // loosens the fast-path scan doesn't start false-matching.
        var payload = "{\"_uuid \":\"smuggled\"}";

        Assert.Null(PayloadUuidExtractor.TryExtract(payload));
    }

    [Fact]
    public void TryExtract_HomoglyphUnderscore_DoesNotMatch()
    {
        // U+FF3F is "fullwidth low line" — a homoglyph for `_`. A
        // payload using it does not contain the ASCII `"_uuid"` byte
        // sequence so the fast path returns null without even
        // reaching JsonDocument.
        var payload = "{\"\uFF3Fuuid\":\"wrong\"}";

        Assert.Null(PayloadUuidExtractor.TryExtract(payload));
    }

    [Fact]
    public void TryExtract_VeryLargePayload_CompletesInBoundedTime()
    {
        // 1 MB of filler plus the uuid near the end. Extractor must
        // complete: fast-path IndexOf is O(n), JsonDocument.Parse is
        // O(n). Asserts the happy path at size, and ensures no
        // regression that would accidentally switch to O(n^2).
        var sb = new StringBuilder();
        sb.Append("{\"filler\":\"");
        sb.Append('x', 1_000_000);
        sb.Append("\",\"_uuid\":\"late-marker\"}");

        Assert.Equal("late-marker", PayloadUuidExtractor.TryExtract(sb.ToString()));
    }

    [Fact]
    public void TryExtract_NoUuidInLargePayload_ShortCircuitsWithoutParse()
    {
        // Same 1 MB payload but no `_uuid` key. The fast-path probe
        // exits on the missing marker; JsonDocument.Parse must not
        // run. No direct way to assert the negative at the unit-test
        // layer, so we assert the output is null and rely on the
        // IndexOf contract pinned by TryExtract_Substring* tests.
        var sb = new StringBuilder();
        sb.Append("{\"a\":\"");
        sb.Append('x', 1_000_000);
        sb.Append("\"}");

        Assert.Null(PayloadUuidExtractor.TryExtract(sb.ToString()));
    }

    [Fact]
    public void TryExtract_PayloadWithByteOrderMark_ReturnsNull()
    {
        // UTF-8 BOM (U+FEFF) in front of otherwise-valid JSON.
        // JsonDocument.Parse on a string rejects a leading BOM with
        // JsonException; the extractor swallows that and returns null.
        // A malicious publisher prepending a BOM therefore cannot
        // smuggle a uuid past the parser — the message is correlated
        // as "no uuid", which is safer than silent acceptance.
        var payload = "\uFEFF" + "{\"_uuid\":\"with-bom\"}";

        Assert.Null(PayloadUuidExtractor.TryExtract(payload));
    }

    [Fact]
    public void TryExtract_TruncatedJson_DoesNotThrow()
    {
        // Attacker or flaky broker sends half a message. Fast-path
        // finds the marker; parse fails; null returned, no throw.
        var payload = "{\"_uuid\":\"truncated";

        Assert.Null(PayloadUuidExtractor.TryExtract(payload));
    }

    [Fact]
    public void TryExtract_ManyPayloadsOnSameCall_StatelessAcrossCalls()
    {
        // The extractor is static; tests here confirm no hidden
        // static state carries over across invocations. A uuid from
        // one call must not leak into the next.
        Assert.Equal("a", PayloadUuidExtractor.TryExtract("{\"_uuid\":\"a\"}"));
        Assert.Null(PayloadUuidExtractor.TryExtract("{\"other\":1}"));
        Assert.Equal("b", PayloadUuidExtractor.TryExtract("{\"_uuid\":\"b\"}"));
    }

    [Fact]
    public void TryExtract_PayloadThatIsJustWhitespace_ReturnsNull()
    {
        Assert.Null(PayloadUuidExtractor.TryExtract("   \n  "));
    }

    [Fact]
    public void TryExtract_PayloadIsJsonNull_ReturnsNull()
    {
        // Parse succeeds but root is JsonValueKind.Null → not an
        // object → skip without searching for _uuid.
        Assert.Null(PayloadUuidExtractor.TryExtract("null"));
    }

    [Fact]
    public void TryExtract_UuidValueIsNumberWithUnderscoreKey_ReturnsNull()
    {
        // Fast-path matches (key is present), deep parse finds the
        // key but value kind is Number, not String. Returns null so
        // downstream trace correlation never stamps a non-string id.
        Assert.Null(PayloadUuidExtractor.TryExtract("{\"_uuid\":12345}"));
    }

    [Fact]
    public void TryExtract_DoesNotAllocateWhenFastPathMisses()
    {
        // Functional assertion, not an allocation guard. Confirms a
        // payload that clearly doesn't contain the marker returns
        // null without having to be valid JSON at all — exercising
        // the early-return path that production hot code relies on.
        var garbage = "this is not json and does not contain the marker";

        Assert.Null(PayloadUuidExtractor.TryExtract(garbage));
    }
}
