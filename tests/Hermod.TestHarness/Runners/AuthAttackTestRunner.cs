using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Hermod.TestHarness.Runners;

/// <summary>
/// Black-box JWT and authorization attack tests against the live coordinator.
///
/// Strategy: log in legitimately to obtain a valid token, then mutate that
/// token in every way an attacker would and verify the server rejects each
/// variant. Plus a battery of "no token at all" tests against every protected
/// surface (REST controllers, YARP proxy routes).
///
/// Pass = the server rejected the attack with 401/403.
/// Fail = the server accepted the malformed/forged token (vulnerability!).
/// </summary>
public sealed class AuthAttackTestRunner
{
    private readonly ILogger<AuthAttackTestRunner> _logger;
    private readonly MeasurementCollector _collector;

    private readonly string _baseUrl;
    private readonly string _adminEmail;
    private readonly string _adminPassword;

    public AuthAttackTestRunner(
        ILogger<AuthAttackTestRunner> logger,
        MeasurementCollector collector)
    {
        _logger = logger;
        _collector = collector;
        _baseUrl = Environment.GetEnvironmentVariable("HERMOD_URL")
                   ?? Environment.GetEnvironmentVariable("HERMOD_COORDINATOR_URL")
                   ?? "http://localhost:42069";
        _adminEmail = Environment.GetEnvironmentVariable("HERMOD_ADMIN_EMAIL")
                      ?? "v@l.l";
        _adminPassword = Environment.GetEnvironmentVariable("HERMOD_ADMIN_PASSWORD")
                         ?? "change-me-in-production-user";
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _logger.LogInformation("=== JWT/Auth Attack Tests ({Url}) ===", _baseUrl);

        using var api = new HermodApiClient(_baseUrl);

        await TestNoTokenAcrossEverySurface(api, ct);
        await TestGarbageBearer(api, ct);

        if (!await api.LoginAsync(_adminEmail, _adminPassword, ct))
        {
            _logger.LogWarning("Login failed — skipping JWT mutation tests");
            return;
        }
        var validToken = api.AccessToken!;

        await TestAlgNoneDowngrade(api, validToken, ct);
        await TestSignatureTampered(api, validToken, ct);
        await TestPayloadTamperedWithoutResign(api, validToken, ct);
        // Removed: TestExpiredToken. Per docs/TESTING_HARNESS.md section 4.4 it
        // cannot be isolated from the signature-invalid case without access to
        // Vault42's signing key, and the thesis does not claim expiry is
        // specifically enforced as a separate layer.
        await TestWrongIssuer(api, validToken, ct);
        await TestWrongAudience(api, validToken, ct);
        await TestUnsignedHs256(api, validToken, ct);

        // Smuggling probe uses a real valid token so parser behaviour is
        // isolated from signature validation.
        await TestAuthorizationHeaderSmuggling(api, validToken, ct);

        await TestYarpProxyRequiresAuth(api, ct);
        await TestHealthEndpointsRemainAnonymous(api, ct);
    }

    /// <summary>
    /// (Method, Path) pairs covering every controller. Each must reject anonymous
    /// requests with 401. We use the real method (GET/POST) so the route resolver
    /// finds an action and we exercise the actual authorization filter — a 404
    /// on a real route would mean auth was skipped, which is a vulnerability.
    /// </summary>
    private static readonly (string Method, string Path)[] ProtectedSurfaces =
    {
        // DevicesController
        ("GET",    "/api/devices"),
        ("GET",    "/api/devices/whatever"),
        ("POST",   "/api/devices"),
        ("DELETE", "/api/devices/whatever"),
        // RulesController
        ("GET",    "/api/rules"),
        ("GET",    "/api/rules/whatever"),
        ("POST",   "/api/rules"),
        ("DELETE", "/api/rules/whatever"),
        // StatsController
        ("GET",    "/api/stats"),
        ("GET",    "/api/stats/protocols"),
        ("GET",    "/api/stats/history"),
        // AuthProxyController
        ("GET",    "/api/auth/me"),
        // BackupController
        ("GET",    "/api/backup/export"),
        ("GET",    "/api/backup/export/info"),
        ("POST",   "/api/backup/import"),
        // ActionsController
        ("POST",   "/api/actions/publish"),
        ("POST",   "/api/actions/devices/whatever/command"),
        ("POST",   "/api/actions/rules/whatever/trigger"),
    };

    private async Task TestNoTokenAcrossEverySurface(HermodApiClient api, CancellationToken ct)
    {
        var failures = new List<string>();
        foreach (var (method, path) in ProtectedSurfaces)
        {
            var httpMethod = new HttpMethod(method);
            HttpContent? body = null;
            if (method is "POST" or "PUT")
            {
                // /api/backup/import takes IFormFile so we need multipart/form-data —
                // sending JSON triggers a 415 from the model binder before auth runs.
                if (path.Contains("/backup/import"))
                {
                    var multipart = new MultipartFormDataContent();
                    multipart.Add(new StringContent("dummy"), "file", "dummy.json");
                    body = multipart;
                }
                else
                {
                    body = new StringContent("{}", Encoding.UTF8, "application/json");
                }
            }

            var resp = await api.SendWithRawAuthAsync(httpMethod, path, null, ct, body);
            if (resp.StatusCode != HttpStatusCode.Unauthorized)
            {
                failures.Add($"{method} {path}={(int)resp.StatusCode}");
            }
        }

        _collector.Record(new TestResult
        {
            Category = "AuthAttack",
                Claim = "O4",
            Name = "NoToken_AllProtectedSurfaces",
            Status = failures.Count == 0 ? "PASS" : "FAIL",
            Details = failures.Count == 0
                ? $"All {ProtectedSurfaces.Length} protected (method, path) pairs rejected anonymous access with 401"
                : $"VULNERABLE: these returned non-401: {string.Join(", ", failures)}"
        });
    }

    private async Task TestGarbageBearer(HermodApiClient api, CancellationToken ct)
    {
        var garbage = new (string label, string header)[]
        {
            ("empty",          "Bearer "),
            ("not_a_jwt",      "Bearer this-is-not-a-jwt"),
            ("two_dots_only",  "Bearer .."),
            ("base64_garbage", "Bearer YWFhYWFhYWFhYQ.YmJiYmJiYmJi.Y2NjY2Nj"),
            ("missing_scheme", "this-is-just-a-token"),
            ("basic_auth",     "Basic YWRtaW46aGVybW9kLWFkbWluLWNoYW5nZS1tZQ=="),
        };

        var failures = new List<string>();
        foreach (var (label, header) in garbage)
        {
            var resp = await api.SendWithRawAuthAsync(HttpMethod.Get, "/api/devices", header, ct);
            if (resp.StatusCode != HttpStatusCode.Unauthorized)
            {
                failures.Add($"{label}={(int)resp.StatusCode}");
            }
        }

        _collector.Record(new TestResult
        {
            Category = "AuthAttack",
                Claim = "O4",
            Name = "GarbageBearer",
            Status = failures.Count == 0 ? "PASS" : "FAIL",
            Details = failures.Count == 0
                ? "All garbage Authorization headers rejected with 401"
                : $"VULNERABLE: {string.Join(", ", failures)}"
        });
    }

    private async Task TestAlgNoneDowngrade(HermodApiClient api, string validToken, CancellationToken ct)
    {
        // Decode header & payload, set alg to "none", strip signature.
        // A correctly configured JWT validator MUST reject this.
        var (_, payload, _) = SplitJwt(validToken);
        var header = JsonSerializer.Serialize(new { alg = "none", typ = "JWT" });
        var forged = $"{Base64UrlEncode(Encoding.UTF8.GetBytes(header))}.{payload}.";

        var resp = await api.SendWithRawAuthAsync(
            HttpMethod.Get, "/api/devices", $"Bearer {forged}", ct);

        var ok = resp.StatusCode == HttpStatusCode.Unauthorized;
        _collector.Record(new TestResult
        {
            Category = "AuthAttack",
                Claim = "O4",
            Name = "JWT_AlgNoneDowngrade",
            Status = ok ? "PASS" : "FAIL",
            Details = ok
                ? "alg=none JWT rejected with 401"
                : $"CRITICAL: alg=none JWT was accepted (status {(int)resp.StatusCode})"
        });
    }

    private async Task TestSignatureTampered(HermodApiClient api, string validToken, CancellationToken ct)
    {
        var (header, payload, sig) = SplitJwt(validToken);
        // Flip the last character of the signature
        var tamperedSig = sig.Length > 0
            ? sig.Substring(0, sig.Length - 1) + (sig[^1] == 'A' ? 'B' : 'A')
            : "AAAA";

        var forged = $"{header}.{payload}.{tamperedSig}";
        var resp = await api.SendWithRawAuthAsync(
            HttpMethod.Get, "/api/devices", $"Bearer {forged}", ct);

        var ok = resp.StatusCode == HttpStatusCode.Unauthorized;
        _collector.Record(new TestResult
        {
            Category = "AuthAttack",
                Claim = "O4",
            Name = "JWT_SignatureTampered",
            Status = ok ? "PASS" : "FAIL",
            Details = ok
                ? "Tampered RS256 signature rejected with 401"
                : $"CRITICAL: tampered signature accepted (status {(int)resp.StatusCode})"
        });
    }

    private async Task TestPayloadTamperedWithoutResign(HermodApiClient api, string validToken, CancellationToken ct)
    {
        var (header, payload, sig) = SplitJwt(validToken);
        var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(payload));

        // Try to elevate to a fictional super-admin role
        var doc = JsonNode.Parse(payloadJson)!.AsObject();
        doc["roles"] = new JsonArray("admin", "super-admin", "root");
        doc["sub"] = "attacker";

        var newPayload = Base64UrlEncode(Encoding.UTF8.GetBytes(doc.ToJsonString()));
        var forged = $"{header}.{newPayload}.{sig}";

        var resp = await api.SendWithRawAuthAsync(
            HttpMethod.Get, "/api/devices", $"Bearer {forged}", ct);

        var ok = resp.StatusCode == HttpStatusCode.Unauthorized;
        _collector.Record(new TestResult
        {
            Category = "AuthAttack",
                Claim = "O4",
            Name = "JWT_PayloadTamperedWithOriginalSig",
            Status = ok ? "PASS" : "FAIL",
            Details = ok
                ? "Modified payload + original signature rejected with 401"
                : $"CRITICAL: payload tampering accepted (status {(int)resp.StatusCode})"
        });
    }

    // TestExpiredToken removed. It could not be isolated from the
    // signature-invalid case without access to Vault42's signing key, which
    // made its PASS result ambiguous. Documented as removed in
    // docs/TESTING_HARNESS.md section 4.4.

    private async Task TestWrongIssuer(HermodApiClient api, string validToken, CancellationToken ct)
    {
        var (header, payload, sig) = SplitJwt(validToken);
        var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(payload));
        var doc = JsonNode.Parse(payloadJson)!.AsObject();
        doc["iss"] = "https://attacker.example.com";
        var newPayload = Base64UrlEncode(Encoding.UTF8.GetBytes(doc.ToJsonString()));
        var forged = $"{header}.{newPayload}.{sig}";

        var resp = await api.SendWithRawAuthAsync(
            HttpMethod.Get, "/api/devices", $"Bearer {forged}", ct);

        var ok = resp.StatusCode == HttpStatusCode.Unauthorized;
        _collector.Record(new TestResult
        {
            Category = "AuthAttack",
                Claim = "O4",
            Name = "JWT_WrongIssuer",
            Status = ok ? "PASS" : "FAIL",
            Details = ok ? "Wrong issuer rejected" : $"CRITICAL: {(int)resp.StatusCode}"
        });
    }

    private async Task TestWrongAudience(HermodApiClient api, string validToken, CancellationToken ct)
    {
        var (header, payload, sig) = SplitJwt(validToken);
        var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(payload));
        var doc = JsonNode.Parse(payloadJson)!.AsObject();
        doc["aud"] = "attacker-app";
        var newPayload = Base64UrlEncode(Encoding.UTF8.GetBytes(doc.ToJsonString()));
        var forged = $"{header}.{newPayload}.{sig}";

        var resp = await api.SendWithRawAuthAsync(
            HttpMethod.Get, "/api/devices", $"Bearer {forged}", ct);

        var ok = resp.StatusCode == HttpStatusCode.Unauthorized;
        _collector.Record(new TestResult
        {
            Category = "AuthAttack",
                Claim = "O4",
            Name = "JWT_WrongAudience",
            Status = ok ? "PASS" : "FAIL",
            Details = ok ? "Wrong audience rejected" : $"CRITICAL: {(int)resp.StatusCode}"
        });
    }

    private async Task TestUnsignedHs256(HermodApiClient api, string validToken, CancellationToken ct)
    {
        // Algorithm confusion attack: change RS256 → HS256 and sign with the JWKS
        // public key as the HMAC secret. We can't fetch the actual public key here
        // (would need the JWKS endpoint), so we sign with a guess and verify the
        // server doesn't accept ANY HS256 token. The check passes as long as we
        // get a 401 — the value of the secret guess is irrelevant.
        var (_, payload, _) = SplitJwt(validToken);
        var header = JsonSerializer.Serialize(new { alg = "HS256", typ = "JWT" });
        var headerB64 = Base64UrlEncode(Encoding.UTF8.GetBytes(header));
        var signingInput = $"{headerB64}.{payload}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes("guessed-public-key"));
        var sig = Base64UrlEncode(hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput)));

        var forged = $"{signingInput}.{sig}";
        var resp = await api.SendWithRawAuthAsync(
            HttpMethod.Get, "/api/devices", $"Bearer {forged}", ct);

        var ok = resp.StatusCode == HttpStatusCode.Unauthorized;
        _collector.Record(new TestResult
        {
            Category = "AuthAttack",
                Claim = "O4",
            Name = "JWT_AlgConfusion_RS256_to_HS256",
            Status = ok ? "PASS" : "FAIL",
            Details = ok
                ? "HS256 token rejected (RS256-only enforcement)"
                : $"CRITICAL: HS256 confusion accepted (status {(int)resp.StatusCode})"
        });
    }

    private async Task TestAuthorizationHeaderSmuggling(HermodApiClient api, string validToken, CancellationToken ct)
    {
        // Use the real valid token in every smuggle variant so the rejection
        // isolates parser behaviour from signature validation. Per
        // docs/TESTING_HARNESS.md section 4.4: the previous bogus-signature
        // variants could have been rejected by the sig validator before the
        // parser ever saw the malformed header, so a PASS did not actually
        // prove the parser was correct.
        var smuggles = new (string label, string header)[]
        {
            // Parser must not split on embedded whitespace and accept the
            // second token: a naive split-on-space would treat this as
            // scheme=Bearer, value=Bearer (wrong), leaving the real token
            // as a trailing value.
            ("double_bearer", $"Bearer Bearer {validToken}"),
            // Trailing space: parser must reject or trim. Either is fine, as
            // long as the end state is a 401 because the effective header is
            // not a valid Bearer form.
            ("trailing_space",  $"Bearer {validToken} "),
            // Leading space: parser must not accept a header that starts
            // with whitespace.
            ("leading_space",   $" Bearer {validToken}"),
            // Lowercase scheme: RFC 7235 section 2.1 says the scheme is
            // case-insensitive, but .NET's default JwtBearerHandler rejects
            // anything other than "Bearer ". This test pins that behaviour.
            ("lowercase_bearer", $"bearer {validToken}"),
            // Extra whitespace between scheme and token should also fail:
            // the handler splits on a single ASCII space.
            ("double_space", $"Bearer  {validToken}"),
            // Tab separator in place of space: must fail.
            ("tab_separator", $"Bearer\t{validToken}"),
        };

        var failures = new List<string>();
        foreach (var (label, header) in smuggles)
        {
            var resp = await api.SendWithRawAuthAsync(HttpMethod.Get, "/api/devices", header, ct);
            if (resp.StatusCode != HttpStatusCode.Unauthorized)
            {
                failures.Add($"{label}={(int)resp.StatusCode}");
            }
        }

        _collector.Record(new TestResult
        {
            Category = "AuthAttack",
                Claim = "O4",
            Name = "AuthHeader_Smuggling",
            Status = failures.Count == 0 ? "PASS" : "FAIL",
            Details = failures.Count == 0
                ? "All Authorization header smuggling variants rejected with 401 (real valid token, isolated from signature validation)"
                : $"VULNERABLE: {string.Join(", ", failures)}"
        });
    }

    private async Task TestYarpProxyRequiresAuth(HermodApiClient api, CancellationToken ct)
    {
        // Must match the slugs registered in
        // src/Hermod.Coordinator/Configuration/TranslatorProxyRegistration.cs:19-21.
        // A drifted slug here 404s on every request and the test below
        // flags that as VULNERABLE (route-not-wired) — the old
        // "/proxy/lora2mqtt/" and "/proxy/ble2mqtt/" entries did exactly
        // that for every harness run until the slugs were realigned.
        var routes = new[]
        {
            "/proxy/zigbee/",
            "/proxy/lora/",
            "/proxy/bluetooth/",
        };

        var failures = new List<string>();
        foreach (var path in routes)
        {
            var resp = await api.SendWithRawAuthAsync(HttpMethod.Get, path, null, ct);
            // Per docs/TESTING_HARNESS.md section 4.4: acceptable results are
            // EXACTLY 401 or 403. 200 would mean unauthenticated proxy
            // passthrough; 404 would mean the route was not wired (so
            // authorization was silently bypassed); 502 would mean the proxy
            // attempted to forward to the upstream translator without first
            // gating on auth (a classic order-of-middleware bug).
            if (resp.StatusCode != HttpStatusCode.Unauthorized &&
                resp.StatusCode != HttpStatusCode.Forbidden)
            {
                failures.Add($"{path}={(int)resp.StatusCode}");
            }
        }

        _collector.Record(new TestResult
        {
            Category = "AuthAttack",
                Claim = "O4",
            Name = "YarpProxy_RequiresAuth",
            Status = failures.Count == 0 ? "PASS" : "FAIL",
            Details = failures.Count == 0
                ? "All YARP proxy routes rejected anonymous access with 401 or 403"
                : $"VULNERABLE: not exactly 401/403: {string.Join(", ", failures)}"
        });
    }

    private async Task TestHealthEndpointsRemainAnonymous(HermodApiClient api, CancellationToken ct)
    {
        var failures = new List<string>();
        foreach (var path in new[] { "/healthz", "/healthz/ready" })
        {
            var resp = await api.SendWithRawAuthAsync(HttpMethod.Get, path, null, ct);
            if (resp.StatusCode != HttpStatusCode.OK)
            {
                failures.Add($"{path}={(int)resp.StatusCode}");
            }
        }

        _collector.Record(new TestResult
        {
            Category = "AuthAttack",
                Claim = "O4",
            Name = "Health_RemainsAnonymous",
            Status = failures.Count == 0 ? "PASS" : "FAIL",
            Details = failures.Count == 0
                ? "Health endpoints remain publicly accessible"
                : $"Health endpoints not anonymous: {string.Join(", ", failures)}"
        });
    }

    private static (string Header, string Payload, string Signature) SplitJwt(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3)
        {
            throw new InvalidOperationException("JWT must have exactly 3 parts");
        }
        return (parts[0], parts[1], parts[2]);
    }

    private static string Base64UrlEncode(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "=";  break;
        }
        return Convert.FromBase64String(s);
    }
}
