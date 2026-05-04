using LoRa2MQTT.Service.Services;
using Xunit;

namespace Hermod.LoRa2MQTT.UnitTests;

/// <summary>
/// Pins the CRITICAL rule that the LoRa mock control topic `.../mock/#`
/// must ONLY be subscribed in mock mode. In hardware mode the topic is
/// an unauthenticated back door that any MQTT client can use to inject
/// fake LoRa frames and mutate the adapter's internal state, bypassing
/// the HTTP RequireAuthorization gate that protects the equivalent REST
/// endpoints.
///
/// Topic-selection logic lives in the pure static helper
/// `GetSubscriptionTopics(baseTopic, mockMode)` so it can be unit
/// tested without instantiating a real MQTT client.
/// </summary>
public class MqttServiceSubscriptionTests
{
    [Fact]
    public void GetSubscriptionTopics_HardwareMode_DoesNotIncludeMockControlTopic()
    {
        // Hardware mode: the mock/# topic MUST be absent.
        var topics = MqttService.GetSubscriptionTopics("lora", mockMode: false);

        Assert.Contains("lora/+/set", topics);
        Assert.DoesNotContain("lora/mock/#", topics);
        Assert.Single(topics);
    }

    [Fact]
    public void GetSubscriptionTopics_MockMode_IncludesBothCommandAndMockControlTopics()
    {
        var topics = MqttService.GetSubscriptionTopics("lora", mockMode: true);

        Assert.Contains("lora/+/set", topics);
        Assert.Contains("lora/mock/#", topics);
        Assert.Equal(2, topics.Length);
    }

    [Fact]
    public void GetSubscriptionTopics_RespectsCustomBaseTopic()
    {
        // Rare case: the operator overrides BaseTopic in appsettings.
        // The gate logic must scale with whatever base is configured.
        var topics = MqttService.GetSubscriptionTopics("custom/prefix", mockMode: true);

        Assert.Contains("custom/prefix/+/set", topics);
        Assert.Contains("custom/prefix/mock/#", topics);
    }

    [Fact]
    public void GetSubscriptionTopics_HardwareMode_NoTopicContainsMock()
    {
        // Stronger assertion: not just "does not contain the canonical
        // mock topic string" but "no topic in the returned list mentions
        // mock at all". Guards against a future refactor that renames
        // the mock topic and forgets to re-gate.
        var topics = MqttService.GetSubscriptionTopics("lora", mockMode: false);

        foreach (var t in topics)
        {
            Assert.DoesNotContain("mock", t, StringComparison.OrdinalIgnoreCase);
        }
    }
}
