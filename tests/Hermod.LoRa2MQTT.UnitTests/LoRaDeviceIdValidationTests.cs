using LoRa2MQTT.Service;
using Xunit;

namespace Hermod.LoRa2MQTT.UnitTests;

/// <summary>
/// Pins the topic-safety rules on <c>device_id</c> values extracted from
/// adversarial LoRa payloads. Invalid ids must fall through to the
/// synthesized <c>device_&lt;address&gt;</c> id so nothing an attacker
/// puts in <c>_uuid</c> or <c>device_id</c> can inject MQTT topic
/// segments or control bytes into downstream routing.
/// </summary>
public class LoRaDeviceIdValidationTests
{
    [Theory]
    [InlineData("sensor_01")]
    [InlineData("Sensor-01")]
    [InlineData("dev.42")]
    [InlineData("a")]
    public void IsValidDeviceId_AcceptsPlainIds(string id)
    {
        Assert.True(LoRaBridgeWorker.IsValidDeviceId(id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("  \t  ")]
    public void IsValidDeviceId_RejectsEmptyAndWhitespace(string? id)
    {
        Assert.False(LoRaBridgeWorker.IsValidDeviceId(id));
    }

    [Theory]
    [InlineData("foo/bar")]
    [InlineData("../escape")]
    [InlineData("+")]
    [InlineData("#")]
    [InlineData("a+b")]
    [InlineData("a#b")]
    public void IsValidDeviceId_RejectsTopicSeparatorsAndWildcards(string id)
    {
        Assert.False(LoRaBridgeWorker.IsValidDeviceId(id));
    }

    [Theory]
    [InlineData("good\nbad")]
    [InlineData("good\rbad")]
    [InlineData("good\tbad")]
    [InlineData("good\0bad")]
    public void IsValidDeviceId_RejectsControlCharacters(string id)
    {
        Assert.False(LoRaBridgeWorker.IsValidDeviceId(id));
    }

    [Fact]
    public void IsValidDeviceId_RejectsIdsAboveLengthCap()
    {
        var huge = new string('x', 65);
        Assert.False(LoRaBridgeWorker.IsValidDeviceId(huge));
    }

    [Fact]
    public void IsValidDeviceId_AcceptsIdsAtLengthCap()
    {
        var atCap = new string('x', 64);
        Assert.True(LoRaBridgeWorker.IsValidDeviceId(atCap));
    }
}
