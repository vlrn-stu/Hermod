using Hermod.Rules.Security;
using Xunit;

namespace Hermod.UnitTests;

/// <summary>
/// Covers the two SSRF bypasses that used to land in the rule engine:
/// (1) hostnames resolving to private/loopback IPs (DNS-rebinding surface);
/// (2) IPv4-mapped IPv6 addresses sidestepping the IPv4 private-range check.
/// Also pins the documented behaviour of the cheap literal-only TryValidate.
/// </summary>
public class WebhookHostGuardTests
{
    [Theory]
    [InlineData("http://127.0.0.1/ping")]
    [InlineData("http://10.0.0.5/ping")]
    [InlineData("http://172.16.0.1/ping")]
    [InlineData("http://192.168.1.1/ping")]
    [InlineData("http://169.254.169.254/latest/meta-data")] // AWS IMDS
    [InlineData("http://[::1]/ping")]
    [InlineData("http://[fe80::1]/ping")]
    [InlineData("http://postgres:5432/")]
    [InlineData("http://vault42/secret")]
    public void TryValidate_LiteralInternalAddress_Rejected(string url)
    {
        Assert.False(WebhookHostGuard.TryValidate(url, out var reason));
        Assert.NotNull(reason);
    }

    [Theory]
    [InlineData("http://[::ffff:127.0.0.1]/ping")]
    [InlineData("http://[::ffff:10.0.0.1]/ping")]
    [InlineData("http://[::ffff:192.168.1.1]/ping")]
    [InlineData("http://[::ffff:169.254.169.254]/meta")]
    public void TryValidate_IPv4MappedPrivateV6_Rejected(string url)
    {
        // Regression: without the MapToIPv4 unwrap these would pass the
        // IsPrivateIPv6 check and reach the outbound HTTP client.
        Assert.False(WebhookHostGuard.TryValidate(url, out var reason));
        Assert.NotNull(reason);
    }

    [Theory]
    [InlineData("ftp://example.com/")]
    [InlineData("file:///etc/passwd")]
    [InlineData("not a url")]
    [InlineData("")]
    public void TryValidate_NonHttpUrl_Rejected(string url)
    {
        Assert.False(WebhookHostGuard.TryValidate(url, out var reason));
        Assert.NotNull(reason);
    }

    [Fact]
    public void TryValidate_PublicHttpsUrl_Accepted()
    {
        // Literal-only path: a public hostname with no DNS check still
        // passes. TryValidateAsync (covered below) is the authoritative
        // check for attacker-influenced URLs.
        Assert.True(WebhookHostGuard.TryValidate("https://api.example.com/hook", out var reason));
        Assert.Null(reason);
    }

    [Fact]
    public async Task TryValidateAsync_LiteralLoopback_Rejected()
    {
        var (ok, reason) = await WebhookHostGuard.TryValidateAsync("http://127.0.0.1/");
        Assert.False(ok);
        Assert.NotNull(reason);
    }

    [Fact]
    public async Task TryValidateAsync_IPv4MappedPrivateV6_Rejected()
    {
        var (ok, reason) = await WebhookHostGuard.TryValidateAsync("http://[::ffff:10.1.2.3]/");
        Assert.False(ok);
        Assert.NotNull(reason);
    }

    [Fact]
    public async Task TryValidateAsync_DnsRebindingToLoopback_Rejected()
    {
        // localhost resolves to a loopback IP. Under the old literal-only
        // guard, any hostname (not on the small blocklist) that resolved to
        // 127.0.0.1 / 169.254.x via public DNS would slip through. Pinning
        // the resolved-IP path keeps that hole closed. Using "localhost" is
        // the one DNS name we can rely on every test host to resolve
        // deterministically.
        var (ok, reason) = await WebhookHostGuard.TryValidateAsync("http://localhost/");
        Assert.False(ok);
        Assert.NotNull(reason);
    }
}
