using System.Net;
using Hermod.Core.Configuration;
using Hermod.Core.Interfaces;
using Hermod.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hermod.UnitTests;

/// <summary>
/// Pins <see cref="ProtocolTranslatorHealthChecker"/>'s per-translator
/// liveness classification. The probe feeds <see cref="StatsService"/>'s
/// <c>TranslatorOnline</c> field via <see cref="TranslatorHealth.Name"/>,
/// so regressions in names or status classification silently flip the
/// dashboard's per-protocol health column.
/// </summary>
public class ProtocolTranslatorHealthCheckerTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; }
            = _ => new HttpResponseMessage(HttpStatusCode.OK);

        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(Respond(request));
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public StubHandler Handler { get; } = new();

        public HttpClient CreateClient(string name) => new(Handler, disposeHandler: false);
    }

    private static ProtocolTranslatorHealthChecker Build(ProtocolTranslatorsSettings t, StubHttpClientFactory f)
    {
        var settings = Options.Create(new HermodSettings { ProtocolTranslators = t });
        return new ProtocolTranslatorHealthChecker(
            f, settings, NullLogger<ProtocolTranslatorHealthChecker>.Instance);
    }

    [Fact]
    public async Task CheckAllAsync_EmitsFourNamedRows_InStableOrder()
    {
        var factory = new StubHttpClientFactory();
        var sut = Build(new ProtocolTranslatorsSettings(), factory);

        var rows = await sut.CheckAllAsync();

        Assert.Equal(4, rows.Count);
        // Order is load-bearing: StatsService.MapTranslatorName keys off
        // these exact strings, and any shuffle breaks the protocol lookup.
        Assert.Equal("Zigbee2MQTT", rows[0].Name);
        Assert.Equal("LoRa2MQTT", rows[1].Name);
        Assert.Equal("BLE2MQTT", rows[2].Name);
        Assert.Equal("WiFi2MQTT", rows[3].Name);
    }

    [Fact]
    public async Task CheckAllAsync_UnconfiguredTranslator_ReportsConfiguredFalse_NoProbe()
    {
        var factory = new StubHttpClientFactory();
        var sut = Build(new ProtocolTranslatorsSettings(), factory);

        var rows = await sut.CheckAllAsync();

        Assert.All(rows, r =>
        {
            Assert.False(r.Configured);
            Assert.False(r.Reachable);
            Assert.Null(r.Error);
        });
        Assert.Equal(0, factory.Handler.CallCount);
    }

    [Fact]
    public async Task CheckAllAsync_DisabledTranslator_TreatedAsUnconfigured()
    {
        var factory = new StubHttpClientFactory();
        var sut = Build(new ProtocolTranslatorsSettings
        {
            Zigbee2Mqtt = new TranslatorSettings { Enabled = false, Url = "http://zigbee:8080" },
        }, factory);

        var rows = await sut.CheckAllAsync();

        var zigbee = rows.Single(r => r.Name == "Zigbee2MQTT");
        Assert.False(zigbee.Configured);
        Assert.False(zigbee.Reachable);
        Assert.Equal(0, factory.Handler.CallCount);
    }

    [Fact]
    public async Task CheckAllAsync_UnsupportedSchemeUrl_ReportsConfiguredButNotReachable()
    {
        // mqtt:// / mqtts:// / tcp:// / tls:// are now first-class
        // (they get a TCP probe for ble2mqtt + wifi2mqtt). Anything else
        // — ftp:// here — falls into the "Unsupported URL scheme"
        // branch: configured (so the operator knows it's not just unset)
        // but not reachable (the probe never fires).
        var factory = new StubHttpClientFactory();
        var sut = Build(new ProtocolTranslatorsSettings
        {
            Lora2Mqtt = new TranslatorSettings { Url = "ftp://lora:21" },
        }, factory);

        var rows = await sut.CheckAllAsync();

        var lora = rows.Single(r => r.Name == "LoRa2MQTT");
        Assert.True(lora.Configured);
        Assert.False(lora.Reachable);
        Assert.NotNull(lora.Error);
        Assert.Contains("Unsupported URL scheme", lora.Error, StringComparison.Ordinal);
        Assert.Equal(0, factory.Handler.CallCount);
    }

    [Fact]
    public async Task CheckAllAsync_HttpSuccess_ReportsReachable()
    {
        var factory = new StubHttpClientFactory
        {
            Handler = { Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) },
        };
        var sut = Build(new ProtocolTranslatorsSettings
        {
            Zigbee2Mqtt = new TranslatorSettings { Url = "http://zigbee:8080" },
        }, factory);

        var rows = await sut.CheckAllAsync();

        var zigbee = rows.Single(r => r.Name == "Zigbee2MQTT");
        Assert.True(zigbee.Configured);
        Assert.True(zigbee.Reachable);
        Assert.Null(zigbee.Error);
    }

    [Fact]
    public async Task CheckAllAsync_HttpError_ReportsStatusCodeInError()
    {
        var factory = new StubHttpClientFactory
        {
            Handler = { Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) },
        };
        var sut = Build(new ProtocolTranslatorsSettings
        {
            Ble2Mqtt = new TranslatorSettings { Url = "http://ble:8080" },
        }, factory);

        var rows = await sut.CheckAllAsync();

        var ble = rows.Single(r => r.Name == "BLE2MQTT");
        Assert.True(ble.Configured);
        Assert.False(ble.Reachable);
        Assert.Equal("HTTP 500", ble.Error);
    }

    [Fact]
    public async Task CheckAllAsync_InvalidUrl_CaughtAsConfiguredButUnreachable()
    {
        var factory = new StubHttpClientFactory();
        var sut = Build(new ProtocolTranslatorsSettings
        {
            Wifi2Mqtt = new TranslatorSettings { Url = "http://::not a valid host::/" },
        }, factory);

        var rows = await sut.CheckAllAsync();

        var wifi = rows.Single(r => r.Name == "WiFi2MQTT");
        Assert.True(wifi.Configured);
        Assert.False(wifi.Reachable);
        Assert.NotNull(wifi.Error);
    }

    [Fact]
    public async Task CheckAllAsync_TransportException_CaughtAsReachableFalse()
    {
        var factory = new StubHttpClientFactory
        {
            Handler = { Respond = _ => throw new HttpRequestException("broker down") },
        };
        var sut = Build(new ProtocolTranslatorsSettings
        {
            Zigbee2Mqtt = new TranslatorSettings { Url = "http://zigbee:8080" },
        }, factory);

        var rows = await sut.CheckAllAsync();

        var zigbee = rows.Single(r => r.Name == "Zigbee2MQTT");
        Assert.True(zigbee.Configured);
        Assert.False(zigbee.Reachable);
        Assert.Equal("HttpRequestException", zigbee.Error);
    }
}
