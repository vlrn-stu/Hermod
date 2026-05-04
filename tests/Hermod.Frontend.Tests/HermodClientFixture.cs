using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Hermod.Frontend.Tests;

/// <summary>Shared fixture. Tries three auth sources in order:
/// (1) HERMOD_SESSION_COOKIE env var (paste from browser), (2) HERMOD_USER
/// + HERMOD_PASSWORD env vars posted to /api/auth/login, (3) anonymous.
/// Authenticated state is recorded in <see cref="IsAuthenticated"/> so
/// tests that need auth can <see cref="Skip"/> when creds are unavailable.
/// </summary>
public sealed class HermodClientFixture : IAsyncLifetime
{
    public HttpClient Client { get; private set; } = default!;
    public string BaseUrl { get; private set; } = "";
    public string Username { get; private set; } = "";
    public bool IsAuthenticated { get; private set; }
    public string AuthSource { get; private set; } = "none";
    public string? LoginError { get; private set; }

    /// <summary>
    /// True iff the coordinator at <see cref="BaseUrl"/> answered a probe
    /// during fixture init. Tests that need a live server skip themselves
    /// when this is false instead of hitting Connection-refused — same
    /// pattern as <see cref="IsAuthenticated"/>, but for "is the server
    /// even running" rather than "do we have credentials".
    /// </summary>
    public bool ServerReachable { get; private set; }

    public async Task InitializeAsync()
    {
        BaseUrl = Environment.GetEnvironmentVariable("HERMOD_URL") ?? "http://localhost:42069";
        Username = Environment.GetEnvironmentVariable("HERMOD_USER") ?? "v@l.l";
        var password = Environment.GetEnvironmentVariable("HERMOD_PASSWORD") ?? "";
        var sessionCookie = Environment.GetEnvironmentVariable("HERMOD_SESSION_COOKIE") ?? "";

        var cookies = new CookieContainer();
        if (!string.IsNullOrWhiteSpace(sessionCookie))
        {
            cookies.Add(new Uri(BaseUrl), new Cookie("hermod_session", sessionCookie.Trim())
            {
                HttpOnly = true,
                Secure = BaseUrl.StartsWith("https", StringComparison.Ordinal)
            });
            IsAuthenticated = true;
            AuthSource = "env-cookie";
        }

        var handler = new HttpClientHandler
        {
            CookieContainer = cookies,
            AllowAutoRedirect = false,
            UseCookies = true
        };
        Client = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(30) };

        // Reachability probe — short timeout so a missing server fails fast
        // and the suite skips instead of stalling per-test on Connection-refused.
        try
        {
            using var probeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var probe = await Client.GetAsync("/healthz", probeCts.Token);
            ServerReachable = true;
        }
        catch (Exception)
        {
            ServerReachable = false;
        }

        if (ServerReachable && !IsAuthenticated && !string.IsNullOrWhiteSpace(password))
        {
            // AuthProxyController's Vault42LoginRequest expects "email",
            // not "username" — the Blazor Login.razor form sends email and
            // the coordinator forwards it straight to vault42. Using
            // "username" here deserialises to Email="" and Vault returns
            // 401 "invalid_credentials" for every login attempt, silently
            // skipping all auth'd tests.
            var resp = await Client.PostAsJsonAsync("/api/auth/login", new
            {
                email = Username,
                password = password
            });

            if (resp.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent)
            {
                // /api/* routes (and Blazor routes themselves) go through
                // AddVault which ONLY honours Authorization: Bearer — the
                // hermod_session cookie alone 401s. Extract the JWT and
                // pin it as the client's default Authorization header for
                // every subsequent request.
                var body = await resp.Content.ReadAsStringAsync();
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("access_token", out var tok)
                        && tok.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        var token = tok.GetString();
                        if (!string.IsNullOrEmpty(token))
                        {
                            Client.DefaultRequestHeaders.Authorization =
                                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                        }
                    }
                }
                catch (System.Text.Json.JsonException) { }

                IsAuthenticated = true;
                AuthSource = "env-password";
            }
            else
            {
                LoginError = $"{(int)resp.StatusCode} {resp.ReasonPhrase}: {await resp.Content.ReadAsStringAsync()}";
            }
        }
    }

    public Task DisposeAsync()
    {
        Client.Dispose();
        return Task.CompletedTask;
    }
}

[CollectionDefinition("hermod-auth")]
public sealed class HermodAuthCollection : ICollectionFixture<HermodClientFixture> { }
