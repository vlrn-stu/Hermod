using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Hermod.TestHarness.Runners;

/// <summary>
/// End-to-end HTTP tests against the live Hermod coordinator. Hits every
/// REST controller and verifies the happy paths plus the basic 401 contract
/// for protected endpoints. Runs after the cluster is up.
/// </summary>
public sealed class HttpE2ETestRunner
{
    private readonly ILogger<HttpE2ETestRunner> _logger;
    private readonly MeasurementCollector _collector;

    private readonly string _baseUrl;
    private readonly string _adminEmail;
    private readonly string _adminPassword;

    public HttpE2ETestRunner(
        ILogger<HttpE2ETestRunner> logger,
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
        _logger.LogInformation("=== HTTP E2E Tests ({Url}) ===", _baseUrl);

        using var api = new HermodApiClient(_baseUrl);

        await TestHealthEndpointsAnonymous(api, ct);
        await TestProtectedEndpointsRejectAnonymous(api, ct);

        var loggedIn = await TestLogin(api, ct);
        if (!loggedIn)
        {
            _logger.LogWarning("Login failed — skipping authenticated tests");
            return;
        }

        await TestAuthMe(api, ct);
        await TestStatsEndpoints(api, ct);
        await TestDeviceCrud(api, ct);
        await TestRulesList(api, ct);
        await TestStatsHistoryAfterFlush(api, ct);
    }

    private async Task TestHealthEndpointsAnonymous(HermodApiClient api, CancellationToken ct)
    {
        foreach (var path in new[] { "/healthz", "/healthz/ready" })
        {
            var resp = await api.GetAsync(path, ct);
            _collector.Record(new TestResult
            {
                Category = "E2E",
            Claim = "O3",
                Name = $"Health_{path}",
                Status = resp.StatusCode == HttpStatusCode.OK ? "PASS" : "FAIL",
                Details = $"GET {path} → {(int)resp.StatusCode}"
            });
        }
    }

    private async Task TestProtectedEndpointsRejectAnonymous(HermodApiClient api, CancellationToken ct)
    {
        var protectedEndpoints = new[]
        {
            "/api/devices",
            "/api/rules",
            "/api/stats",
            "/api/stats/protocols",
            "/api/stats/history",
            "/api/auth/me",
        };

        foreach (var path in protectedEndpoints)
        {
            var resp = await api.GetAsync(path, ct);
            var ok = resp.StatusCode == HttpStatusCode.Unauthorized;
            _collector.Record(new TestResult
            {
                Category = "E2E",
            Claim = "O3",
                Name = $"Anonymous_Rejected_{path}",
                Status = ok ? "PASS" : "FAIL",
                Details = ok
                    ? $"GET {path} → 401 as expected"
                    : $"GET {path} → {(int)resp.StatusCode} (expected 401)"
            });
        }
    }

    private async Task<bool> TestLogin(HermodApiClient api, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ok = await api.LoginAsync(_adminEmail, _adminPassword, ct);
        sw.Stop();

        _collector.Record(new TestResult
        {
            Category = "E2E",
            Claim = "O3",
            Name = "Login_Admin",
            Status = ok ? "PASS" : "FAIL",
            LatencyMs = sw.Elapsed.TotalMilliseconds,
            Details = ok
                ? $"Logged in as {_adminEmail}, got JWT ({api.AccessToken?.Length ?? 0} chars)"
                : $"Login as {_adminEmail} failed"
        });

        return ok;
    }

    private async Task TestAuthMe(HermodApiClient api, CancellationToken ct)
    {
        var resp = await api.GetAsync("/api/auth/me", ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        var ok = resp.IsSuccessStatusCode && body.Contains("\"id\":");

        _collector.Record(new TestResult
        {
            Category = "E2E",
            Claim = "O3",
            Name = "AuthMe",
            Status = ok ? "PASS" : "FAIL",
            Details = $"GET /api/auth/me → {(int)resp.StatusCode}; body: {Truncate(body, 120)}"
        });
    }

    private async Task TestStatsEndpoints(HermodApiClient api, CancellationToken ct)
    {
        var stats = await api.GetAsync("/api/stats", ct);
        _collector.Record(new TestResult
        {
            Category = "E2E",
            Claim = "O3",
            Name = "Stats_Current",
            Status = stats.IsSuccessStatusCode ? "PASS" : "FAIL",
            Details = $"GET /api/stats → {(int)stats.StatusCode}"
        });

        var protocols = await api.GetAsync("/api/stats/protocols", ct);
        _collector.Record(new TestResult
        {
            Category = "E2E",
            Claim = "O3",
            Name = "Stats_Protocols",
            Status = protocols.IsSuccessStatusCode ? "PASS" : "FAIL",
            Details = $"GET /api/stats/protocols → {(int)protocols.StatusCode}"
        });
    }

    private async Task TestDeviceCrud(HermodApiClient api, CancellationToken ct)
    {
        // List
        var list = await api.GetAsync("/api/devices", ct);
        _collector.Record(new TestResult
        {
            Category = "E2E",
            Claim = "O3",
            Name = "Devices_List",
            Status = list.IsSuccessStatusCode ? "PASS" : "FAIL",
            Details = $"GET /api/devices → {(int)list.StatusCode}"
        });

        // Create a synthetic device
        var deviceId = $"e2e-test-{Guid.NewGuid():N}";
        var create = await api.PostJsonAsync("/api/devices", new
        {
            id = deviceId,
            name = "E2E Synthetic Device",
            protocol = 1, // ZigBee
            status = 1,   // Online
            manufacturer = "Hermod Test",
            model = "Synthetic"
        }, ct);

        var createOk = create.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK;
        _collector.Record(new TestResult
        {
            Category = "E2E",
            Claim = "O3",
            Name = "Devices_Create",
            Status = createOk ? "PASS" : "FAIL",
            Details = $"POST /api/devices → {(int)create.StatusCode}"
        });

        if (!createOk) return;

        // Read it back
        var read = await api.GetAsync($"/api/devices/{deviceId}", ct);
        _collector.Record(new TestResult
        {
            Category = "E2E",
            Claim = "O3",
            Name = "Devices_GetById",
            Status = read.IsSuccessStatusCode ? "PASS" : "FAIL",
            Details = $"GET /api/devices/{deviceId} → {(int)read.StatusCode}"
        });

        // Delete
        var del = await api.DeleteAsync($"/api/devices/{deviceId}", ct);
        var delOk = del.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent;
        _collector.Record(new TestResult
        {
            Category = "E2E",
            Claim = "O3",
            Name = "Devices_Delete",
            Status = delOk ? "PASS" : "FAIL",
            Details = $"DELETE /api/devices/{deviceId} → {(int)del.StatusCode}"
        });
    }

    private async Task TestRulesList(HermodApiClient api, CancellationToken ct)
    {
        var resp = await api.GetAsync("/api/rules", ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        int? ruleCount = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                ruleCount = doc.RootElement.GetArrayLength();
            }
        }
        catch
        {
            // not an array — leave ruleCount null
        }

        _collector.Record(new TestResult
        {
            Category = "E2E",
            Claim = "O3",
            Name = "Rules_List",
            Status = resp.IsSuccessStatusCode ? "PASS" : "FAIL",
            Details = ruleCount.HasValue
                ? $"GET /api/rules → {(int)resp.StatusCode}, {ruleCount} rules seeded"
                : $"GET /api/rules → {(int)resp.StatusCode}"
        });
    }

    private async Task TestStatsHistoryAfterFlush(HermodApiClient api, CancellationToken ct)
    {
        // The MetricsPersistenceService flushes every 15s. Wait at least one tick
        // and then check we get a non-empty history back.
        _logger.LogInformation("Waiting 17s for metrics flush tick...");
        await Task.Delay(TimeSpan.FromSeconds(17), ct);

        var resp = await api.GetAsync("/api/stats/history?limit=10", ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        int? snapshotCount = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                snapshotCount = doc.RootElement.GetArrayLength();
            }
        }
        catch
        {
            // ignore
        }

        var ok = resp.IsSuccessStatusCode && snapshotCount.HasValue && snapshotCount.Value > 0;
        _collector.Record(new TestResult
        {
            Category = "E2E",
            Claim = "O3",
            Name = "Stats_HistoryPersisted",
            Status = ok ? "PASS" : "FAIL",
            Details = ok
                ? $"GET /api/stats/history → {snapshotCount} snapshot(s) persisted to PostgreSQL"
                : $"GET /api/stats/history → {(int)resp.StatusCode}, snapshots: {snapshotCount}"
        });
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s.Substring(0, max) + "...";
}
