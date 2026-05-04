using LoRa2MQTT.Service.Adapters;
using Xunit;

namespace Hermod.LoRa2MQTT.UnitTests;

/// <summary>
/// Pin the frame-parsing behaviour of the Waveshare LoRa adapter.
/// The pure parser is extracted from <c>WaveshareLoRaAdapter.ProcessReceivedData</c>
/// so it can be tested without a real serial port. Two entry points
/// are tested: <c>IsFrameComplete</c> (should-we-parse-yet predicate)
/// and <c>ParseFrame</c> (payload + optional trailing RSSI extraction).
/// </summary>
public class WaveshareLoRaAdapterParserTests
{
    [Fact]
    public void IsFrameComplete_EmptyBuffer_ReturnsFalse()
    {
        Assert.False(WaveshareLoRaAdapter.IsFrameComplete(string.Empty));
    }

    [Fact]
    public void IsFrameComplete_NoNewlineUnderCap_ReturnsFalse()
    {
        // A normal partial arrival: some chars landed, no terminator yet,
        // well below the 240-byte safety cap. The caller must keep
        // accumulating.
        Assert.False(WaveshareLoRaAdapter.IsFrameComplete("hello"));
    }

    [Fact]
    public void IsFrameComplete_NewlineTerminator_ReturnsTrue()
    {
        Assert.True(WaveshareLoRaAdapter.IsFrameComplete("hello\n"));
    }

    [Fact]
    public void IsFrameComplete_CarriageReturnOnly_ReturnsFalse()
    {
        // Only LF flips the predicate. A bare CR is not enough. This
        // matches the existing behaviour in `ProcessReceivedData`.
        Assert.False(WaveshareLoRaAdapter.IsFrameComplete("hello\r"));
    }

    [Fact]
    public void IsFrameComplete_InnerLfBeforeRssiSuffix_ReturnsFalse()
    {
        // Real Waveshare stream-mode output with AT+RSSI=1 is
        // `<payload>\n<rssi>\r\n`. The inner `\n` must NOT complete
        // the frame, otherwise RSSI arrives as a spurious second
        // one-line message. Regression guard for the parse-format bug.
        Assert.False(WaveshareLoRaAdapter.IsFrameComplete("hello\n-72"));
    }

    [Fact]
    public void IsFrameComplete_FullCrLfAfterRssiSuffix_ReturnsTrue()
    {
        // Same firmware layout, now with the terminal CR-LF attached.
        Assert.True(WaveshareLoRaAdapter.IsFrameComplete("hello\n-72\r\n"));
    }

    [Fact]
    public void IsFrameComplete_TrailingLfAfterInnerLf_ReturnsTrue()
    {
        // Even without the CR, a line-terminal LF (i.e. LF is the last
        // char of the buffer) still fires the predicate. Covers the
        // plain-LF mock-translator path where there's no RSSI suffix.
        Assert.True(WaveshareLoRaAdapter.IsFrameComplete("hello\n"));
    }

    [Fact]
    public void IsFrameComplete_OverHardCap_ReturnsTrue()
    {
        // The safety valve at 240 bytes. If the peer never sends a
        // terminator (misbehaving transmitter) we still flush.
        var big = new string('x', 241);
        Assert.True(WaveshareLoRaAdapter.IsFrameComplete(big));
    }

    [Fact]
    public void IsFrameComplete_ExactlyCap_ReturnsFalse()
    {
        // 240 is under the cap (the check is `> 240`).
        var edge = new string('x', 240);
        Assert.False(WaveshareLoRaAdapter.IsFrameComplete(edge));
    }

    [Fact]
    public void ParseFrame_SimplePayloadWithCrLf_StripsTerminators()
    {
        var result = WaveshareLoRaAdapter.ParseFrame("hello world\r\n", enableRssi: false);
        Assert.True(result.Emit);
        Assert.Equal("hello world", result.Payload);
        Assert.Null(result.Rssi);
    }

    [Fact]
    public void ParseFrame_PayloadWithTrailingRssi_ExtractsRssi()
    {
        // Waveshare format: payload is the JSON body, then comma, then
        // RSSI as the last comma-delimited field.
        var result = WaveshareLoRaAdapter.ParseFrame(
            "{\"temp\":23.5},-82\r\n",
            enableRssi: true);

        Assert.True(result.Emit);
        Assert.Equal("{\"temp\":23.5}", result.Payload);
        Assert.Equal(-82, result.Rssi);
    }

    [Fact]
    public void ParseFrame_PayloadWithMultipleCommas_OnlyLastIsRssi()
    {
        // Regression guard: `a,b,c,-70` must parse as payload `a,b,c`
        // and RSSI `-70`, NOT as payload `a` and RSSI `-70` (which would
        // drop the middle fields).
        var result = WaveshareLoRaAdapter.ParseFrame("a,b,c,-70\n", enableRssi: true);
        Assert.True(result.Emit);
        Assert.Equal("a,b,c", result.Payload);
        Assert.Equal(-70, result.Rssi);
    }

    [Fact]
    public void ParseFrame_NonNumericLastField_KeepsFullPayload()
    {
        // If the last comma-delimited field does not parse as an int the
        // parser must NOT truncate the payload. Example: a GPS tuple
        // `lat=42.1,lon=-71.3`.
        var result = WaveshareLoRaAdapter.ParseFrame(
            "lat=42.1,lon=-71.3\n",
            enableRssi: true);

        Assert.True(result.Emit);
        Assert.Equal("lat=42.1,lon=-71.3", result.Payload);
        Assert.Null(result.Rssi);
    }

    [Fact]
    public void ParseFrame_RssiDisabled_KeepsFullPayloadEvenIfNumericSuffix()
    {
        // When the operator disables RSSI parsing, a numeric suffix must
        // be treated as part of the payload.
        var result = WaveshareLoRaAdapter.ParseFrame(
            "temp=23.5,-82\n",
            enableRssi: false);

        Assert.True(result.Emit);
        Assert.Equal("temp=23.5,-82", result.Payload);
        Assert.Null(result.Rssi);
    }

    [Fact]
    public void ParseFrame_EmptyPayload_ReturnsEmitFalse()
    {
        // Bare terminator, nothing else. Do not emit an event on the
        // adapter's event stream.
        var result = WaveshareLoRaAdapter.ParseFrame("\r\n", enableRssi: true);
        Assert.False(result.Emit);
    }

    [Fact]
    public void ParseFrame_WhitespaceOnlyPayload_ReturnsEmitFalse()
    {
        var result = WaveshareLoRaAdapter.ParseFrame("   \t  \r\n", enableRssi: true);
        Assert.False(result.Emit);
    }

    [Fact]
    public void ParseFrame_RssiOnlyFragment_ReturnsEmitFalse()
    {
        // Malformed: leading comma with only an RSSI value. Old code
        // would split into ["", "-82"], strip the RSSI, leave payload ""
        // and then the empty-payload guard kicks in and returns Emit=false.
        // Regression guard.
        var result = WaveshareLoRaAdapter.ParseFrame(",-82\n", enableRssi: true);
        Assert.False(result.Emit);
    }

    [Fact]
    public void ParseFrame_NewlineSeparatedRssi_ExtractsRssi()
    {
        // Real Waveshare stream-mode firmware sends `<payload>\n<rssi>`
        // followed by the `\r\n` frame terminator. The parser must
        // prefer the newline form over the comma form for this
        // firmware variant — otherwise every bridged message shows
        // `"rssi":null` and the numeric suffix leaks into payload.
        var result = WaveshareLoRaAdapter.ParseFrame(
            "{\"temp\":23.5}\n-82\r\n",
            enableRssi: true);

        Assert.True(result.Emit);
        Assert.Equal("{\"temp\":23.5}", result.Payload);
        Assert.Equal(-82, result.Rssi);
    }

    [Fact]
    public void ParseFrame_NewlineRssiBeforeComma_PrefersNewline()
    {
        // A payload body that itself contains a comma followed by a
        // numeric tail (e.g. GPS) must not have its comma-tail
        // mis-parsed as RSSI when the firmware is using the newline
        // form. The \n split wins.
        var result = WaveshareLoRaAdapter.ParseFrame(
            "lat=42.1,lon=-71.3\n-95\r\n",
            enableRssi: true);

        Assert.True(result.Emit);
        Assert.Equal("lat=42.1,lon=-71.3", result.Payload);
        Assert.Equal(-95, result.Rssi);
    }

    [Fact]
    public void ParseFrame_PositiveRssi_ExtractsCorrectly()
    {
        // Most LoRa RSSI values are negative but the parser must not
        // special-case sign. Use a positive value to pin that the int
        // parser handles both.
        var result = WaveshareLoRaAdapter.ParseFrame("ping,10\n", enableRssi: true);
        Assert.True(result.Emit);
        Assert.Equal("ping", result.Payload);
        Assert.Equal(10, result.Rssi);
    }
}
