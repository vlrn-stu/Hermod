using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Hermod.Coordinator.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.JSInterop;
using Xunit;

namespace Hermod.Coordinator.UnitTests;

/// <summary>
/// Pins <see cref="Vault42AuthStateProvider"/>'s JWT parsing +
/// expiration semantics. The Coordinator's Blazor circuit reads auth
/// state from this provider on every interactive render. It does NOT
/// verify the JWT signature — that's the API's job via Vault.AspNetCore —
/// but it MUST filter expired/malformed tokens client-side so the UI
/// doesn't show "authenticated" to a user whose token has just expired.
/// </summary>
public class Vault42AuthStateProviderTests
{
    [Fact]
    public async Task NoToken_AnywhereReturnsAnonymous()
    {
        var sut = Build(cookieValue: null);

        var state = await sut.GetAuthenticationStateAsync();

        Assert.False(state.User.Identity?.IsAuthenticated ?? false);
    }

    [Fact]
    public async Task ValidToken_ProducesAuthenticatedPrincipalWithSub()
    {
        var token = MakeJwt(new { sub = "alice", exp = Future(), roles = new[] { "admin" } });
        var sut = Build(cookieValue: token);

        var state = await sut.GetAuthenticationStateAsync();

        Assert.True(state.User.Identity?.IsAuthenticated);
        Assert.Equal("alice", state.User.Identity?.Name);
        Assert.Contains(state.User.Claims, c => c.Type == ClaimTypes.Role && c.Value == "admin");
    }

    [Fact]
    public async Task ExpiredToken_TreatedAsAnonymous()
    {
        var token = MakeJwt(new { sub = "alice", exp = Past() });
        var sut = Build(cookieValue: token);

        var state = await sut.GetAuthenticationStateAsync();

        Assert.False(state.User.Identity?.IsAuthenticated ?? false);
    }

    [Fact]
    public async Task MissingExpClaim_TreatedAsAnonymous()
    {
        // A token that strips the exp claim must NOT silently authenticate.
        var token = MakeJwt(new { sub = "alice" });
        var sut = Build(cookieValue: token);

        var state = await sut.GetAuthenticationStateAsync();

        Assert.False(state.User.Identity?.IsAuthenticated ?? false);
    }

    [Fact]
    public async Task UnparseableExpClaim_TreatedAsAnonymous()
    {
        var token = MakeJwt(new { sub = "alice", exp = "not-a-number" });
        var sut = Build(cookieValue: token);

        var state = await sut.GetAuthenticationStateAsync();

        Assert.False(state.User.Identity?.IsAuthenticated ?? false);
    }

    [Fact]
    public async Task WrongJwtShape_TreatedAsAnonymous()
    {
        // Two dots required. One segment is not a JWT.
        var sut = Build(cookieValue: "not.a.jwt.with.too.many.parts");

        var state = await sut.GetAuthenticationStateAsync();

        Assert.False(state.User.Identity?.IsAuthenticated ?? false);
    }

    [Fact]
    public async Task CorruptBase64Payload_TreatedAsAnonymous()
    {
        // Second segment is garbage base64; Convert.FromBase64String throws.
        var sut = Build(cookieValue: "aGVhZGVy.!!!not-base64!!!.c2ln");

        var state = await sut.GetAuthenticationStateAsync();

        Assert.False(state.User.Identity?.IsAuthenticated ?? false);
    }

    [Fact]
    public async Task ScopesArray_MapsToScopeClaims()
    {
        var token = MakeJwt(new { sub = "alice", exp = Future(), scopes = new[] { "read", "write" } });
        var sut = Build(cookieValue: token);

        var state = await sut.GetAuthenticationStateAsync();

        var scopes = state.User.Claims.Where(c => c.Type == "scope").Select(c => c.Value).ToHashSet();
        Assert.Contains("read", scopes);
        Assert.Contains("write", scopes);
    }

    [Fact]
    public async Task BooleanClaim_SerializedAsLowercaseString()
    {
        // Downstream consumers key off the lowercase canonical form.
        var token = MakeJwt(new { sub = "alice", exp = Future(), email_verified = true });
        var sut = Build(cookieValue: token);

        var state = await sut.GetAuthenticationStateAsync();

        var claim = state.User.Claims.FirstOrDefault(c => c.Type == "email_verified");
        Assert.NotNull(claim);
        Assert.Equal("true", claim.Value);
    }

    [Fact]
    public async Task NumberClaim_PreservedAsRawText()
    {
        var token = MakeJwt(new { sub = "alice", exp = Future(), age = 42 });
        var sut = Build(cookieValue: token);

        var state = await sut.GetAuthenticationStateAsync();

        Assert.Contains(state.User.Claims, c => c.Type == "age" && c.Value == "42");
    }

    private static long Future() => DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
    private static long Past() => DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();

    private static Vault42AuthStateProvider Build(string? cookieValue)
    {
        var context = new DefaultHttpContext();
        if (cookieValue is not null)
        {
            // Cookie header is the input the provider reads via
            // HttpContext.Request.Cookies[CookieName].
            context.Request.Headers.Cookie = $"{Vault42AuthStateProvider.CookieName}={cookieValue}";
        }
        var accessor = new StubHttpContextAccessor { HttpContext = context };
        return new Vault42AuthStateProvider(new StubJsRuntime(), accessor);
    }

    private static string MakeJwt(object payload)
    {
        // Build a well-shaped but unsigned JWT. Signature is the third
        // segment and the provider never verifies it — the API does.
        var header = Base64UrlEncode(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}"));
        var body = Base64UrlEncode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
        var signature = Base64UrlEncode(Encoding.UTF8.GetBytes("stub-signature"));
        return $"{header}.{body}.{signature}";
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private sealed class StubHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    // The JS-interop code path is only taken when the cookie is absent;
    // tests that set a cookie never invoke this. Tests that pass no
    // cookie hit InvokeAsync and the provider is documented to catch
    // JSException during SSR prerender.
    private sealed class StubJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => throw new JSException("JS interop unavailable in unit test");
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
            => throw new JSException("JS interop unavailable in unit test");
    }
}
