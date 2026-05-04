using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Hermod.TestHarness;

/// <summary>
/// Thin HTTP client wrapper around the live Hermod coordinator.
/// Handles login flow + token storage so runners can focus on assertions.
/// </summary>
public sealed class HermodApiClient : IDisposable
{
    private readonly HttpClient _http;
    private string? _accessToken;

    // Remembered credentials so we can re-login on 401. The first successful
    // LoginAsync() stashes these; subsequent API helpers route through
    // SendWithRefreshAsync below and auto-retry once after a re-login if the
    // coordinator's JWT has expired mid-test (long perf suites used to 401
    // on rule teardown after ~10 min because we never refreshed the token).
    private string? _email;
    private string? _password;

    public string BaseUrl { get; }
    public string? AccessToken => _accessToken;

    public HermodApiClient(string baseUrl)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient
        {
            BaseAddress = new Uri(this.BaseUrl),
            Timeout = TimeSpan.FromSeconds(30)
        };
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public void SetToken(string? token)
    {
        _accessToken = token;
        _http.DefaultRequestHeaders.Authorization = string.IsNullOrEmpty(token)
            ? null
            : new AuthenticationHeaderValue("Bearer", token);
    }

    public async Task<bool> LoginAsync(string email, string password, CancellationToken ct)
    {
        SetToken(null);
        var resp = await _http.PostAsJsonAsync("/api/auth/login", new
        {
            email,
            password,
            remember_me = false
        }, ct);

        if (!resp.IsSuccessStatusCode) return false;

        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("access_token", out var tokenElement))
        {
            return false;
        }

        var token = tokenElement.GetString();
        if (string.IsNullOrEmpty(token)) return false;

        SetToken(token);
        _email = email;
        _password = password;
        return true;
    }

    /// <summary>
    /// Re-issues the stored credentials to /api/auth/login and updates the
    /// Bearer token. No-op returning false if LoginAsync was never called.
    /// Used by SendWithRefreshAsync on a 401 retry.
    /// </summary>
    private async Task<bool> RefreshTokenAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_email) || string.IsNullOrEmpty(_password)) return false;
        return await LoginAsync(_email!, _password!, ct);
    }

    /// <summary>
    /// Sends a request factory, retries once after a token refresh if the
    /// first attempt comes back 401. A factory (not a pre-built request) is
    /// used because HttpRequestMessage can only be sent once — the retry
    /// needs a fresh instance with the new Bearer attached.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithRefreshAsync(
        Func<HttpRequestMessage> factory, CancellationToken ct)
    {
        var resp = await _http.SendAsync(factory(), ct);
        if (resp.StatusCode != System.Net.HttpStatusCode.Unauthorized) return resp;

        resp.Dispose();
        if (!await RefreshTokenAsync(ct))
        {
            // No stored credentials or refresh failed — let the 401 bubble up
            // to the caller rather than silently swallowing it.
            return await _http.SendAsync(factory(), ct);
        }
        return await _http.SendAsync(factory(), ct);
    }

    public Task<HttpResponseMessage> GetAsync(string path, CancellationToken ct)
        => SendWithRefreshAsync(() => new HttpRequestMessage(HttpMethod.Get, path), ct);

    /// <summary>
    /// Reads GET /api/stats and returns the (messages_processed, rules_executed)
    /// counters. Throws on non-success so callers can choose between "error" and
    /// "counter delta".
    /// </summary>
    public async Task<(long Messages, long Rules)> GetStatsCountersAsync(CancellationToken ct)
    {
        using var resp = await _http.GetAsync("/api/stats", ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);

        long messages = 0;
        long rules = 0;
        if (doc.RootElement.TryGetProperty("messagesProcessed", out var m) && m.TryGetInt64(out var mv)) messages = mv;
        if (doc.RootElement.TryGetProperty("rulesExecuted", out var r) && r.TryGetInt64(out var rv)) rules = rv;
        return (messages, rules);
    }

    public Task<HttpResponseMessage> PostJsonAsync<T>(string path, T body, CancellationToken ct)
        => SendWithRefreshAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, path);
            req.Content = JsonContent.Create(body);
            return req;
        }, ct);

    public Task<HttpResponseMessage> DeleteAsync(string path, CancellationToken ct)
        => SendWithRefreshAsync(() => new HttpRequestMessage(HttpMethod.Delete, path), ct);

    public Task<HttpResponseMessage> PutJsonAsync<T>(string path, T body, CancellationToken ct)
        => SendWithRefreshAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Put, path);
            req.Content = JsonContent.Create(body);
            return req;
        }, ct);

    /// <summary>
    /// Sends a request with an explicit Authorization header value.
    /// Used by attack tests that need to bypass the stored token.
    /// </summary>
    public async Task<HttpResponseMessage> SendWithRawAuthAsync(
        HttpMethod method,
        string path,
        string? authorizationHeader,
        CancellationToken ct,
        HttpContent? content = null)
    {
        var req = new HttpRequestMessage(method, path);
        if (!string.IsNullOrEmpty(authorizationHeader))
        {
            req.Headers.TryAddWithoutValidation("Authorization", authorizationHeader);
        }
        if (content is not null)
        {
            req.Content = content;
        }
        return await _http.SendAsync(req, ct);
    }

    public void Dispose() => _http.Dispose();
}
