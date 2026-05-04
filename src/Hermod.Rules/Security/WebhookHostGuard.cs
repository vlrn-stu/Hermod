using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;

namespace Hermod.Rules.Security;

/// <summary>
/// SSRF guard for the Webhook rule action. Rejects any URL that is not a
/// well-formed absolute http(s) URL pointing at a public host. A literal or
/// templated URL controlled by an MQTT publisher must not be able to steer
/// outbound HTTP into cluster-internal services, cloud metadata endpoints,
/// or RFC1918 space.
/// </summary>
public static class WebhookHostGuard
{
    private static readonly HashSet<string> BlockedHostnames = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost",
        "metadata",
        "metadata.google.internal",
        "metadata.azure.com",
        "postgres",
        "nanomq",
        "vault42",
        "zigbee2mqtt",
        "lora2mqtt",
        "ble2mqtt",
    };

    /// <summary>Returns <c>true</c> when <paramref name="url"/> passes the SSRF guard.</summary>
    public static bool IsAllowed(string url) => TryValidate(url, out _);

    /// <summary>
    /// Literal-only validation: rejects hostnames on the blocklist and IP
    /// literals in private/loopback ranges. Does NOT resolve DNS, so a public
    /// hostname pointing at an internal IP still passes this check. Use
    /// <see cref="TryValidateAsync"/> on any hot path that accepts
    /// attacker-influenced URLs.
    /// </summary>
    public static bool TryValidate(string url, out string? rejectionReason)
    {
        if (!TryParseAndShape(url, out var uri, out rejectionReason))
        {
            return false;
        }

        if (IsPrivateOrLoopbackLiteral(uri.Host))
        {
            rejectionReason = $"Target host '{uri.Host}' is loopback, private, or an internal service.";
            return false;
        }

        rejectionReason = null;
        return true;
    }

    /// <summary>
    /// Resolves the URL's host and rejects the call if ANY resolved IP lands
    /// in a loopback/private range. Closes the DNS-rebinding bypass where a
    /// public hostname resolves to an internal IP (cloud metadata, RFC1918,
    /// link-local). Residual TOCTOU remains because the actual HTTP request
    /// re-resolves, but blocks every naive public-DNS-points-internal attack.
    /// </summary>
    public static async Task<(bool Ok, string? Reason)> TryValidateAsync(
        string url, CancellationToken cancellationToken = default)
    {
        if (!TryParseAndShape(url, out var uri, out var reason))
        {
            return (false, reason);
        }

        if (IsPrivateOrLoopbackLiteral(uri.Host))
        {
            return (false, $"Target host '{uri.Host}' is loopback, private, or an internal service.");
        }

        // If the host was already an IP literal, the literal check above is
        // authoritative and DNS resolution adds nothing.
        if (IPAddress.TryParse(uri.Host, out _))
        {
            return (true, null);
        }

        IPAddress[] resolved;
        try
        {
            resolved = await Dns.GetHostAddressesAsync(uri.Host, cancellationToken);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return (false, $"DNS resolution failed for '{uri.Host}': {ex.Message}");
        }

        foreach (var ip in resolved)
        {
            if (IsPrivateOrLoopbackIp(ip))
            {
                return (false, $"Target host '{uri.Host}' resolves to private/loopback IP {ip}.");
            }
        }

        return (true, null);
    }

    private static bool TryParseAndShape(string url, [NotNullWhen(true)] out Uri? uri, out string? rejectionReason)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            rejectionReason = "URL must be an absolute http(s) URL.";
            uri = null;
            return false;
        }
        rejectionReason = null;
        return true;
    }

    private static bool IsPrivateOrLoopbackLiteral(string host)
    {
        if (string.IsNullOrEmpty(host)) return true;
        if (BlockedHostnames.Contains(host)) return true;

        if (!IPAddress.TryParse(host, out var ip)) return false;
        return IsPrivateOrLoopbackIp(ip);
    }

    private static bool IsPrivateOrLoopbackIp(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;

        // IPv4-mapped IPv6 (::ffff:x.y.z.w) would otherwise skip the IPv4
        // private-range check and be accepted.
        if (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        var bytes = ip.GetAddressBytes();
        return ip.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsPrivateIPv4(bytes),
            AddressFamily.InterNetworkV6 => IsPrivateIPv6(bytes),
            _ => false,
        };
    }

    private static bool IsPrivateIPv4(byte[] bytes) =>
        bytes[0] == 10
        || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
        || (bytes[0] == 192 && bytes[1] == 168)
        || (bytes[0] == 169 && bytes[1] == 254)
        || bytes[0] == 127;

    private static bool IsPrivateIPv6(byte[] bytes) =>
        (bytes[0] & 0xfe) == 0xfc
        || (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80);
}
