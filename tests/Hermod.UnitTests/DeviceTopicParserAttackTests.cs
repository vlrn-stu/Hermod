using System.Linq;
using Hermod.Core.Mqtt;
using Xunit;

namespace Hermod.UnitTests;

/// <summary>
/// Attack-surface tests for <see cref="DeviceTopicParser"/>. MQTT
/// topics arrive from brokers that may have been compromised or
/// mis-configured; an incoming topic is untrusted input. These tests
/// pin behaviour for the edges that a malicious publisher might
/// exploit: traversal segments in the device-id slot, empty-segment
/// smuggling via double slashes, $SYS bypass attempts, and long-topic
/// DoS. The parser itself does not enforce a routing policy — its
/// job is to extract a device-id string or return null. Downstream
/// code is responsible for sanitising the extracted id before using
/// it in authorization decisions; the tests record what the parser
/// returns so the caller can reason about the attack surface
/// explicitly.
/// </summary>
public class DeviceTopicParserAttackTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Parse_NullOrEmpty_ReturnsNull(string? topic)
    {
        Assert.Null(DeviceTopicParser.Parse(topic));
    }

    [Theory]
    [InlineData("$SYS/brokers/hermod/stats")]
    [InlineData("$")]
    [InlineData("$whatever")]
    public void Parse_SysTopic_ReturnsNull(string topic)
    {
        Assert.Null(DeviceTopicParser.Parse(topic));
    }

    [Fact]
    public void Parse_DollarIsExactMatchNotCaseInsensitive()
    {
        // MQTT reserves `$SYS` exactly; any other prefix byte is a
        // normal topic. Use a non-system second segment so we measure
        // only the `$` check, not the second-segment deny list.
        Assert.Equal("regularDevice", DeviceTopicParser.Parse("SYS/regularDevice"));
    }

    [Theory]
    [InlineData("zigbee/bridge/devices")]
    [InlineData("zigbee/Bridge/devices")]
    [InlineData("zigbee/BRIDGE/devices")]
    [InlineData("zigbee/system/info")]
    [InlineData("zigbee/mock/inject")]
    public void Parse_SystemSegmentIsCaseInsensitive(string topic)
    {
        Assert.Null(DeviceTopicParser.Parse(topic));
    }

    [Theory]
    [InlineData("zigbee/../hermod/state", "../hermod")]
    [InlineData("zigbee/../../hermod", "../../hermod")]
    [InlineData("zigbee/.../evil", ".../evil")]
    public void Parse_TraversalSegments_ReturnedVerbatim(string topic, string expected)
    {
        // The parser is deliberately a string extractor, not a
        // sanitiser — it returns the literal segments it walked.
        // Pinning this so auth callers know the returned device id
        // may contain ../ and must be validated before any file,
        // topic, or RBAC decision uses it.
        Assert.Equal(expected, DeviceTopicParser.Parse(topic));
    }

    [Theory]
    [InlineData("zigbee//device/state", "/device")]
    [InlineData("zigbee///device", "//device")]
    [InlineData("zigbee/device//", "device//")]
    public void Parse_EmptySegments_PreservedInDeviceId(string topic, string expected)
    {
        // Double slashes create empty segments; the walk retains them
        // because `ActionSuffixes.Contains("")` is false. Downstream
        // must treat empty-segment device ids as invalid — this test
        // pins what gets passed to them so a normaliser can be added
        // at the boundary without guessing the shape.
        Assert.Equal(expected, DeviceTopicParser.Parse(topic));
    }

    [Fact]
    public void Parse_SingleSegment_ReturnsNull()
    {
        // One segment is neither "{protocol}/{device}" nor a system
        // topic; parser drops it.
        Assert.Null(DeviceTopicParser.Parse("onlyone"));
    }

    [Fact]
    public void Parse_LeadingSlash_ProducesOddButBoundedResult()
    {
        // Malformed input from a client that included the broker's
        // leading slash. parts[0] is empty, parts[1] is the intended
        // protocol. Parser still returns the remainder joined. Pin the
        // behaviour so a future validator at the boundary has a fixed
        // shape to reject.
        Assert.Equal("zigbee/device", DeviceTopicParser.Parse("/zigbee/device"));
    }

    [Fact]
    public void Parse_TrailingSlash_YieldsTrailingEmptyDeviceSegment()
    {
        // topic "zigbee/device/" splits to ["zigbee","device",""];
        // walk from index 1 collects ["device", ""] until end (no
        // matching action suffix), joins to "device/".
        Assert.Equal("device/", DeviceTopicParser.Parse("zigbee/device/"));
    }

    [Fact]
    public void Parse_ActionSuffixCaseInsensitive_TerminatesWalk()
    {
        // SET / Get / STATE / AVAILABILITY must all stop the walk.
        Assert.Equal("kitchen/light", DeviceTopicParser.Parse("zigbee/kitchen/light/SET"));
        Assert.Equal("kitchen/light", DeviceTopicParser.Parse("zigbee/kitchen/light/Get"));
        Assert.Equal("kitchen/light", DeviceTopicParser.Parse("zigbee/kitchen/light/STATE"));
        Assert.Equal("sensor", DeviceTopicParser.Parse("zigbee/sensor/Availability"));
    }

    [Fact]
    public void Parse_ExtremelyLongTopic_DoesNotBlowMemoryOrHang()
    {
        // 10 000 segments. The parser allocates a List<string> sized
        // to the segment count — bounded, not recursive. Regression
        // guard against any future rewrite that tries to build a
        // nested structure and blows through the heap.
        var longTopic = "zigbee/" + string.Join('/', Enumerable.Repeat("seg", 10_000));

        var result = DeviceTopicParser.Parse(longTopic);

        Assert.NotNull(result);
        Assert.StartsWith("seg/", result);
    }

    [Fact]
    public void Parse_ControlCharacterInSegment_PreservedVerbatim()
    {
        // Null byte, tab, newline in a device id. Parser must not
        // crash and must return the literal string so a downstream
        // sanitiser can reject it. Many broker implementations will
        // have already rejected such a topic; this is the last-line
        // guard against a compromised broker forwarding through.
        var topic = "zigbee/dev\0ice/state";

        var result = DeviceTopicParser.Parse(topic);

        Assert.Equal("dev\0ice", result);
    }

    [Fact]
    public void IsSystemOrBridge_DollarExact_TreatedAsSystem()
    {
        Assert.True(DeviceTopicParser.IsSystemOrBridge("$SYS/brokers/hermod"));
        Assert.True(DeviceTopicParser.IsSystemOrBridge("$"));
    }

    [Theory]
    [InlineData("zigbee/bridge")]
    [InlineData("zigbee/BRIDGE")]
    [InlineData("zigbee/system")]
    public void IsSystemOrBridge_SystemSegmentMatches(string topic)
    {
        Assert.True(DeviceTopicParser.IsSystemOrBridge(topic));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("oneword")]
    [InlineData("zigbee/device")]
    public void IsSystemOrBridge_NonSystem_ReturnsFalse(string? topic)
    {
        Assert.False(DeviceTopicParser.IsSystemOrBridge(topic));
    }

    [Theory]
    [InlineData("zigbee/state")]
    [InlineData("zigbee/set")]
    [InlineData("zigbee/get")]
    [InlineData("zigbee/availability")]
    [InlineData("lora/state")]
    public void Parse_TwoSegmentActionSuffix_ReturnsNull(string topic)
    {
        // Regression: parts.Length==2 used to return parts[1] unconditionally,
        // so zigbee/state fabricated "state" as a device id and downstream
        // consumers would treat an action suffix as a routable device.
        Assert.Null(DeviceTopicParser.Parse(topic));
    }
}
