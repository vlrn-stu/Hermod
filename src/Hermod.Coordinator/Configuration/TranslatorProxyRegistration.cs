using Hermod.Core.Configuration;
using Yarp.ReverseProxy.Configuration;

namespace Hermod.Coordinator.Configuration;

/// <summary>
/// Builds YARP route + cluster configs for the protocol translator
/// dashboards. The proxy is NOT a generic in-cluster forwarder — it
/// only registers routes for the slugs listed in <see cref="AllowedSlugs"/>
/// and only when the corresponding <see cref="TranslatorSettings.Enabled"/>
/// flag is set + an upstream <see cref="TranslatorSettings.Url"/> is
/// configured. Adding a new exposed translator therefore requires:
/// (a) extending <see cref="AllowedSlugs"/>, (b) wiring it into
/// <see cref="ProtocolTranslatorsSettings"/>, and (c) editing
/// <see cref="Build"/> to map the new slug to its settings property.
/// SECURITY.md §7.4 documents this allowlist.
/// </summary>
internal static class TranslatorProxyRegistration
{
    /// <summary>
    /// Public path slugs allowed under <c>/proxy/{slug}/**</c> and
    /// <c>/admin/{slug}/**</c>. Any slug not in this list is unreachable
    /// through YARP — the request never matches a registered route and
    /// falls through to the Blazor app's 404.
    /// </summary>
    public static readonly IReadOnlySet<string> AllowedSlugs = new HashSet<string>(StringComparer.Ordinal)
    {
        "zigbee",
        "lora",
        "bluetooth",
        "ble",
    };

    public static (List<RouteConfig> Routes, List<ClusterConfig> Clusters) Build(
        ProtocolTranslatorsSettings translators)
    {
        var routes = new List<RouteConfig>();
        var clusters = new List<ClusterConfig>();

        // /proxy/* routes — anonymous-by-default, gated upstream by the
        // Blazor pages that iframe them (each iframe-host page is
        // [Authorize]'d so an unauth user never reaches the proxy URL).
        Add(routes, clusters, "zigbee", translators.Zigbee2Mqtt);
        Add(routes, clusters, "lora", translators.Lora2Mqtt);
        Add(routes, clusters, "bluetooth", translators.Ble2Mqtt);

        // /admin/* routes — explicitly admin-only. These bypass the
        // anonymous /proxy/ chain so the gateway carries its own RBAC
        // boundary: only admins reach upstream admin UIs.
        AddAdmin(routes, clusters, "zigbee", translators.Zigbee2Mqtt);
        AddAdmin(routes, clusters, "lora", translators.Lora2Mqtt);
        AddAdmin(routes, clusters, "ble", translators.Ble2Mqtt);

        return (routes, clusters);
    }

    private static void AddAdmin(
        List<RouteConfig> routes,
        List<ClusterConfig> clusters,
        string slug,
        TranslatorSettings translator)
    {
        if (!AllowedSlugs.Contains(slug))
            throw new InvalidOperationException($"Slug '{slug}' not in YARP allowlist (TranslatorProxyRegistration.AllowedSlugs).");
        if (!translator.Enabled || string.IsNullOrEmpty(translator.Url)) return;

        routes.Add(new RouteConfig
        {
            RouteId = $"admin-{slug}-route",
            ClusterId = $"admin-{slug}-cluster",
            Match = new RouteMatch { Path = $"/admin/{slug}/{{**catch-all}}" },
            AuthorizationPolicy = Authorization.Policies.Admin,
            Transforms = new List<IReadOnlyDictionary<string, string>>
            {
                new Dictionary<string, string> { { "PathRemovePrefix", $"/admin/{slug}" } }
            }
        });

        clusters.Add(new ClusterConfig
        {
            ClusterId = $"admin-{slug}-cluster",
            Destinations = new Dictionary<string, DestinationConfig>
            {
                { "default", new DestinationConfig { Address = translator.Url } }
            }
        });
    }

    private static void Add(
        List<RouteConfig> routes,
        List<ClusterConfig> clusters,
        string slug,
        TranslatorSettings translator)
    {
        if (!AllowedSlugs.Contains(slug))
            throw new InvalidOperationException($"Slug '{slug}' not in YARP allowlist (TranslatorProxyRegistration.AllowedSlugs).");
        if (!translator.Enabled || string.IsNullOrEmpty(translator.Url)) return;

        routes.Add(new RouteConfig
        {
            RouteId = $"{slug}-route",
            ClusterId = $"{slug}-cluster",
            Match = new RouteMatch { Path = $"/proxy/{slug}/{{**catch-all}}" },
            // No AuthorizationPolicy: the proxy is reached via the same
            // Kestrel port as the Blazor app, and every page that embeds
            // it (<iframe src="/proxy/zigbee/"> or the Lora.razor
            // HttpClient poll) is already [Authorize]'d by the Blazor
            // auth pipeline. Leaving the YARP "default" policy in here
            // would double-gate the request with a Bearer check that
            // mismatches the Blazor cookie-auth the browser actually
            // sends — causing the zigbee iframe to render the login
            // redirect in-frame ("proxying itself") and the LoRa status
            // poll to 401 loop after any vault42 token rotation.
            Transforms = new List<IReadOnlyDictionary<string, string>>
            {
                new Dictionary<string, string> { { "PathRemovePrefix", $"/proxy/{slug}" } }
            }
        });

        clusters.Add(new ClusterConfig
        {
            ClusterId = $"{slug}-cluster",
            Destinations = new Dictionary<string, DestinationConfig>
            {
                { "default", new DestinationConfig { Address = translator.Url } }
            }
        });
    }
}
