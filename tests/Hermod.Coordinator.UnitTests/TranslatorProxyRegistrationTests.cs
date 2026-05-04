using Hermod.Coordinator.Configuration;
using Hermod.Core.Configuration;
using Xunit;

namespace Hermod.Coordinator.UnitTests;

/// <summary>
/// Pins <see cref="TranslatorProxyRegistration.Build"/>: the YARP route
/// table driving <c>/proxy/{zigbee,lora,bluetooth}/**</c> is built once
/// at startup. A regression that fabricates a route without a cluster
/// (or vice versa) fails at first request with an opaque YARP config
/// error; this fixture catches those at unit-test time.
/// </summary>
public class TranslatorProxyRegistrationTests
{
    [Fact]
    public void Build_AllTranslatorsDisabled_EmitsNoRoutes()
    {
        var settings = new ProtocolTranslatorsSettings
        {
            Zigbee2Mqtt = new TranslatorSettings { Enabled = false, Url = "http://ignored" },
            Lora2Mqtt = new TranslatorSettings { Enabled = false, Url = "http://ignored" },
            Ble2Mqtt = new TranslatorSettings { Enabled = false, Url = "http://ignored" },
        };

        var (routes, clusters) = TranslatorProxyRegistration.Build(settings);

        Assert.Empty(routes);
        Assert.Empty(clusters);
    }

    [Fact]
    public void Build_NullOrEmptyUrl_SkipsRegistrationEvenWhenEnabled()
    {
        // Enabled + Url unset is the "not deployed yet" state. The proxy
        // must NOT register a route pointing at null or it will 500 the
        // request with a YARP config error instead of a clean 404.
        var settings = new ProtocolTranslatorsSettings
        {
            Zigbee2Mqtt = new TranslatorSettings { Enabled = true, Url = null },
            Lora2Mqtt = new TranslatorSettings { Enabled = true, Url = "" },
        };

        var (routes, clusters) = TranslatorProxyRegistration.Build(settings);

        Assert.Empty(routes);
        Assert.Empty(clusters);
    }

    [Fact]
    public void Build_OneTranslatorEnabled_EmitsProxyAndAdminLanes()
    {
        // Each enabled translator produces TWO route/cluster pairs:
        //   /proxy/{slug}/**  — anonymous-by-default, iframe-gated upstream
        //   /admin/{slug}/**  — explicit Admin policy on the YARP route
        // SECURITY.md §7.4 + the admin-gateway plan rely on this split.
        var settings = new ProtocolTranslatorsSettings
        {
            Zigbee2Mqtt = new TranslatorSettings { Enabled = true, Url = "http://zigbee:8080" },
        };

        var (routes, clusters) = TranslatorProxyRegistration.Build(settings);

        Assert.Equal(2, routes.Count);
        Assert.Equal(2, clusters.Count);

        var routeIds = routes.Select(r => r.RouteId).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("zigbee-route", routeIds);
        Assert.Contains("admin-zigbee-route", routeIds);

        var clusterIds = clusters.Select(c => c.ClusterId).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("zigbee-cluster", clusterIds);
        Assert.Contains("admin-zigbee-cluster", clusterIds);
    }

    [Fact]
    public void Build_AllThreeEnabled_ProducesSymmetricRouteClusterPairs()
    {
        var settings = new ProtocolTranslatorsSettings
        {
            Zigbee2Mqtt = new TranslatorSettings { Enabled = true, Url = "http://zigbee:8080" },
            Lora2Mqtt = new TranslatorSettings { Enabled = true, Url = "http://lora:8080" },
            Ble2Mqtt = new TranslatorSettings { Enabled = true, Url = "http://ble:8080" },
        };

        var (routes, clusters) = TranslatorProxyRegistration.Build(settings);

        // 3 translators × 2 lanes (proxy + admin) = 6 pairs.
        Assert.Equal(6, routes.Count);
        Assert.Equal(6, clusters.Count);

        // Every route's ClusterId must resolve to a real cluster.
        var clusterIds = clusters.Select(c => c.ClusterId ?? "").ToHashSet(StringComparer.Ordinal);
        foreach (var r in routes)
        {
            Assert.Contains(r.ClusterId ?? "", clusterIds);
        }
    }

    [Theory]
    [InlineData("zigbee", "/proxy/zigbee/{**catch-all}")]
    [InlineData("lora", "/proxy/lora/{**catch-all}")]
    [InlineData("bluetooth", "/proxy/bluetooth/{**catch-all}")]
    public void Build_ProxyLane_PathTemplates_AreCanonical(string slug, string expected)
    {
        var zigbee = slug == "zigbee" ? new TranslatorSettings { Enabled = true, Url = "http://x" } : new TranslatorSettings();
        var lora = slug == "lora" ? new TranslatorSettings { Enabled = true, Url = "http://x" } : new TranslatorSettings();
        var ble = slug == "bluetooth" ? new TranslatorSettings { Enabled = true, Url = "http://x" } : new TranslatorSettings();
        var settings = new ProtocolTranslatorsSettings { Zigbee2Mqtt = zigbee, Lora2Mqtt = lora, Ble2Mqtt = ble };

        var (routes, _) = TranslatorProxyRegistration.Build(settings);

        var proxyRoute = routes.Single(r => (r.RouteId ?? "").StartsWith("admin-", StringComparison.Ordinal) == false);
        Assert.Equal(expected, proxyRoute.Match.Path);
    }

    [Fact]
    public void Build_ProxyLane_EmitsPathRemovePrefixTransform()
    {
        // The transform strips /proxy/{slug} before forwarding; without
        // it the downstream (Z2M frontend) would see the /proxy/zigbee
        // prefix and serve 404s on every asset.
        var settings = new ProtocolTranslatorsSettings
        {
            Zigbee2Mqtt = new TranslatorSettings { Enabled = true, Url = "http://zigbee:8080" },
        };

        var (routes, _) = TranslatorProxyRegistration.Build(settings);

        var proxyRoute = routes.Single(r => r.RouteId == "zigbee-route");
        var transforms = proxyRoute.Transforms;
        Assert.NotNull(transforms);
        var transform = Assert.Single(transforms);
        Assert.Equal("/proxy/zigbee", transform["PathRemovePrefix"]);
    }

    [Fact]
    public void Build_AdminLane_GatesOnAdminPolicy()
    {
        // The admin lane must carry the Admin authorization policy so an
        // unauth or non-admin caller can't reach the upstream admin UI
        // through YARP. Pinning this here catches a regression where the
        // policy attribute is dropped or mistyped.
        var settings = new ProtocolTranslatorsSettings
        {
            Zigbee2Mqtt = new TranslatorSettings { Enabled = true, Url = "http://zigbee:8080" },
        };

        var (routes, _) = TranslatorProxyRegistration.Build(settings);

        var adminRoute = routes.Single(r => r.RouteId == "admin-zigbee-route");
        Assert.Equal("/admin/zigbee/{**catch-all}", adminRoute.Match.Path);
        Assert.Equal(Hermod.Coordinator.Authorization.Policies.Admin, adminRoute.AuthorizationPolicy);
    }

    [Fact]
    public void Build_ClusterDestinationMatchesConfiguredUrl()
    {
        var settings = new ProtocolTranslatorsSettings
        {
            Lora2Mqtt = new TranslatorSettings { Enabled = true, Url = "http://lora.internal:9090" },
        };

        var (_, clusters) = TranslatorProxyRegistration.Build(settings);

        // Both proxy and admin lanes point at the same configured URL.
        Assert.Equal(2, clusters.Count);
        foreach (var cluster in clusters)
        {
            Assert.NotNull(cluster.Destinations);
            var dest = Assert.Single(cluster.Destinations);
            Assert.Equal("default", dest.Key);
            Assert.Equal("http://lora.internal:9090", dest.Value.Address);
        }
    }
}
