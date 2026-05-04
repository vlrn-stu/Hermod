using System.Diagnostics;
using System.Net;
using AngleSharp.Html.Parser;
using Xunit;
using Xunit.Abstractions;

namespace Hermod.Frontend.Tests;

[Collection("hermod-auth")]
public sealed class PageLoadTests
{
    private readonly HermodClientFixture _fx;
    private readonly ITestOutputHelper _out;
    private static readonly HtmlParser _parser = new();

    public PageLoadTests(HermodClientFixture fx, ITestOutputHelper output)
    {
        _fx = fx;
        _out = output;
    }

    /// <summary>Every Blazor route, expected to return 200 OK for an authed admin.</summary>
    public static IEnumerable<object[]> Routes() => new[]
    {
        // Public routes — no auth required, always expected to render.
        // /login budget is 3 s because it's typically the first hit after a
        // coordinator restart; Kestrel JIT + static-asset prime add ~1 s on
        // top of the per-request cost.
        new object[] { "/login",          "form, input", 3000, false },
        // Authenticated routes — need either HERMOD_SESSION_COOKIE or
        // HERMOD_USER + HERMOD_PASSWORD. Skipped when no creds available.
        new object[] { "/",               "#app",         1500, true },
        new object[] { "/devices",        "table, .empty-state", 5000, true },
        new object[] { "/zigbee",         "#app",         3000, true },
        new object[] { "/lora",           "#app",         5000, true },
        new object[] { "/bluetooth",      "#app",         1500, true },
        new object[] { "/topology",       "#app",         5000, true },
        new object[] { "/health",         "#app",         5000, true },
        new object[] { "/metrics-dashboard", "#app",      3000, true },
        new object[] { "/messages",       "#app",         2000, true },
        new object[] { "/rules",          "#app",         2000, true },
        new object[] { "/backup",         "#app",         1500, true },
        new object[] { "/settings",       "#app",         1500, true },
        new object[] { "/mock",           "#app",         1500, true },
        new object[] { "/mock/zigbee",    "#app",         1500, true },
        new object[] { "/mock/lora",      "#app",         1500, true },
    };

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Page_Loads_Within_Latency_Budget(string path, string expectedSelectors, int latencyBudgetMs, bool requiresAuth)
    {
        if (!_fx.ServerReachable)
        {
            _out.WriteLine($"{path,-20} SKIP: no coordinator at {_fx.BaseUrl}. Start hermod.sh up and re-run.");
            return;
        }
        if (requiresAuth && !_fx.IsAuthenticated)
        {
            _out.WriteLine($"{path,-20} SKIP: no auth (LoginError={_fx.LoginError}). Set HERMOD_SESSION_COOKIE or HERMOD_PASSWORD.");
            return;
        }

        var sw = Stopwatch.StartNew();
        var resp = await _fx.Client.GetAsync(path);
        sw.Stop();
        var body = await resp.Content.ReadAsStringAsync();

        _out.WriteLine($"{path,-20} status={(int)resp.StatusCode} elapsed={sw.ElapsedMilliseconds}ms bytes={body.Length} authVia={_fx.AuthSource}");

        Assert.True(
            resp.StatusCode is HttpStatusCode.OK or HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"{path}: expected 200/302, got {(int)resp.StatusCode}. Body head: {body[..Math.Min(400, body.Length)]}");

        Assert.True(
            sw.ElapsedMilliseconds <= latencyBudgetMs,
            $"{path}: load took {sw.ElapsedMilliseconds}ms, budget was {latencyBudgetMs}ms");

        if (resp.StatusCode == HttpStatusCode.OK && body.Contains("<!DOCTYPE"))
        {
            var doc = await _parser.ParseDocumentAsync(body);
            var selectors = expectedSelectors.Split(',', StringSplitOptions.TrimEntries);
            var anyMatched = selectors.Any(sel => doc.QuerySelector(sel) != null);
            Assert.True(anyMatched,
                $"{path}: none of the expected selectors matched ({expectedSelectors}). Likely a skeleton render or error page.");
        }
    }

    [Fact]
    public async Task Healthz_Is_Anonymous_And_Fast()
    {
        if (!_fx.ServerReachable)
        {
            _out.WriteLine($"/healthz SKIP: no coordinator at {_fx.BaseUrl}.");
            return;
        }

        var sw = Stopwatch.StartNew();
        var resp = await _fx.Client.GetAsync("/healthz");
        sw.Stop();
        _out.WriteLine($"/healthz status={(int)resp.StatusCode} elapsed={sw.ElapsedMilliseconds}ms");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(sw.ElapsedMilliseconds < 500, $"/healthz took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task ReadyZ_Reports_Ready()
    {
        if (!_fx.ServerReachable)
        {
            _out.WriteLine($"/healthz/ready SKIP: no coordinator at {_fx.BaseUrl}.");
            return;
        }

        var resp = await _fx.Client.GetAsync("/healthz/ready");
        var body = await resp.Content.ReadAsStringAsync();
        _out.WriteLine($"/healthz/ready status={(int)resp.StatusCode} body={body}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Devices_Api_Returns_Paginated_Envelope_Not_Raw_Array()
    {
        // Guards the F1 regression: /api/devices must ship a
        // {items, total, offset, limit} page envelope, not the pre-fix
        // raw List<Device> that blew up memory at 220k rows. Also pins
        // the offset/limit contract so future callers can rely on
        // stable paging semantics.
        if (!_fx.IsAuthenticated)
        {
            _out.WriteLine("/api/devices SKIP: requires auth.");
            return;
        }

        var resp = await _fx.Client.GetAsync("/api/devices?offset=0&limit=5");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();

        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.Equal(System.Text.Json.JsonValueKind.Object, root.ValueKind);
        Assert.True(root.TryGetProperty("items", out var items),
            $"/api/devices missing 'items' — body head: {body[..Math.Min(200, body.Length)]}");
        Assert.Equal(System.Text.Json.JsonValueKind.Array, items.ValueKind);
        Assert.True(items.GetArrayLength() <= 5, "limit=5 was not honored");

        Assert.True(root.TryGetProperty("total", out var total)
                    && total.ValueKind == System.Text.Json.JsonValueKind.Number,
            "/api/devices missing numeric 'total'");
        Assert.True(root.TryGetProperty("offset", out var off) && off.GetInt32() == 0,
            "/api/devices offset echo mismatch");
        Assert.True(root.TryGetProperty("limit", out var lim) && lim.GetInt32() == 5,
            "/api/devices limit echo mismatch");
    }

    [Fact]
    public async Task Prometheus_Metrics_Endpoint_Renders_Expected_Counters()
    {
        // Coverage-expansion cycle: guards both that /metrics serves
        // text/plain Prometheus format AND that the PID-added counters
        // are actually being emitted (device writes, mqtt reconnects
        // etc.) — regressions here go silent otherwise.
        if (!_fx.IsAuthenticated)
        {
            _out.WriteLine("/metrics SKIP: requires auth. Set HERMOD_PASSWORD.");
            return;
        }

        var sw = Stopwatch.StartNew();
        var resp = await _fx.Client.GetAsync("/metrics");
        sw.Stop();
        var body = await resp.Content.ReadAsStringAsync();

        _out.WriteLine($"/metrics status={(int)resp.StatusCode} elapsed={sw.ElapsedMilliseconds}ms bytes={body.Length}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(sw.ElapsedMilliseconds < 2000,
            $"/metrics took {sw.ElapsedMilliseconds}ms — scrape budget is 2s.");
        Assert.Contains("text/plain", resp.Content.Headers.ContentType?.ToString() ?? "");
        // Shape check: every exported counter has a HELP + TYPE header.
        Assert.Contains("# HELP hermod_messages_ingested_total", body);
        Assert.Contains("# TYPE hermod_messages_ingested_total counter", body);
        Assert.Contains("hermod_mqtt_reconnects_total", body);
        Assert.Contains("hermod_device_state_writes_total", body);
    }

    [Fact]
    public async Task Devices_Api_Is_Paginated_Or_Bounded()
    {
        // Surfaces the 220k-device pagination regression explicitly:
        // if the endpoint takes more than 10 s or returns more than 5 MB
        // without a pagination envelope, fail with the remediation path.
        if (!_fx.IsAuthenticated)
        {
            _out.WriteLine("/api/devices SKIP: requires auth. Set HERMOD_SESSION_COOKIE or HERMOD_PASSWORD.");
            return;
        }

        var sw = Stopwatch.StartNew();
        var resp = await _fx.Client.GetAsync("/api/devices");
        sw.Stop();
        var body = await resp.Content.ReadAsStringAsync();

        _out.WriteLine($"/api/devices status={(int)resp.StatusCode} elapsed={sw.ElapsedMilliseconds}ms bytes={body.Length}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(sw.ElapsedMilliseconds < 10_000,
            $"/api/devices took {sw.ElapsedMilliseconds}ms — pagination needed; see CYCLE_FRONTEND.md F1.");
        Assert.True(body.Length < 5_000_000,
            $"/api/devices returned {body.Length} bytes — likely unpaginated; pagination needed per CYCLE_FRONTEND.md F1.");
    }
}
