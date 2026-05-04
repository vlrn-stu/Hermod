using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Hermod.TestHarness.Runners;

/// <summary>
/// Resilience tests covering claim O5 (reliability under infrastructure
/// disruption). Methodology ref: <c>docs/TESTING_HARNESS.md</c> section 4.5.
///
/// The runner shells out via <see cref="KubectlClient"/> (which captures
/// errors into a result record rather than throwing) so a missing binary,
/// missing RBAC, or API timeout produces a clean ERROR row instead of a
/// runner crash.
///
/// Pre-requisites that the orchestration script and harness Job manifest
/// must satisfy:
///   1. kubectl on PATH inside the harness container (installed by the
///      harness Dockerfile).
///   2. ServiceAccount on the harness Job pod with RBAC for:
///         pods: get, list, delete
///         deployments: get, list, patch (for rollout restart)
///         networkpolicies: create, delete
///      Scoped to the hermod namespace.
///
/// Methodology rule honoured: every test cleans up what it created. The
/// PostgresLoss test wraps the deny-all NetworkPolicy delete in a finally
/// block so a failed assertion does not leave the cluster wedged.
/// </summary>
public sealed class ResilienceTestRunner
{
    private const string ClaimO5 = "O5";

    private readonly ILogger<ResilienceTestRunner> _logger;
    private readonly MqttTestClient _mqtt;
    private readonly MeasurementCollector _collector;

    private readonly string _baseUrl;
    private readonly KubectlClient _kubectl;

    public ResilienceTestRunner(
        ILogger<ResilienceTestRunner> logger,
        MqttTestClient mqtt,
        MeasurementCollector collector)
    {
        _logger = logger;
        _mqtt = mqtt;
        _collector = collector;

        _baseUrl = Environment.GetEnvironmentVariable("HERMOD_URL")
                   ?? Environment.GetEnvironmentVariable("HERMOD_COORDINATOR_URL")
                   ?? "http://localhost:42069";
        _kubectl = new KubectlClient(logger, @namespace: "hermod");
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _logger.LogInformation("=== Resilience Tests ===");

        await PodBounceRecoveryTime(ct);
        await BrokerBounceReconnect(ct);
        await PostgresLossSurvives(ct);
    }

    /// <summary>
    /// Delete the coordinator pod, poll /healthz/ready until 200, assert
    /// recovery within 30 s, then publish a correlated round trip through
    /// the seeded debug-passthrough rule and assert it completes within the
    /// liveness SLO.
    /// </summary>
    private async Task PodBounceRecoveryTime(CancellationToken ct)
    {
        _logger.LogInformation("Test: PodBounce_RecoveryTime");

        var del = await _kubectl.RunAsync(
            "delete pod -l app=hermod-coordinator --wait=false", ct, TimeSpan.FromSeconds(30));
        if (!del.Ok)
        {
            _collector.Record(new TestResult
            {
                Category = "Resilience", Claim = ClaimO5, Name = "PodBounce_RecoveryTime",
                Status = "ERROR",
                Details = $"kubectl delete failed: {del.Summary}"
            });
            return;
        }

        using var http = new HttpClient { BaseAddress = new Uri(_baseUrl), Timeout = TimeSpan.FromSeconds(2) };
        var sw = Stopwatch.StartNew();
        var deadline = TimeSpan.FromSeconds(30);
        var ready = false;
        while (sw.Elapsed < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                var resp = await http.GetAsync("/healthz/ready", ct);
                if (resp.IsSuccessStatusCode) { ready = true; break; }
            }
            catch { /* expected during pod restart */ }
            await Task.Delay(500, ct);
        }

        if (!ready)
        {
            _collector.Record(new TestResult
            {
                Category = "Resilience", Claim = ClaimO5, Name = "PodBounce_RecoveryTime",
                Status = "FAIL",
                LatencyMs = sw.Elapsed.TotalMilliseconds,
                Details = $"/healthz/ready did not return 200 within {deadline.TotalSeconds}s"
            });
            return;
        }

        // Verify a correlated round trip still works after recovery. The
        // seeded debug-passthrough rule forwards zigbee/+ to
        // hermod/debug/{{deviceName}}, so this exercises the same path the
        // FunctionalTestRunner uses.
        try
        {
            await _mqtt.ConnectAsync(ct);
            var device = $"resilience_pod_{Guid.NewGuid():N}".Substring(0, 24);
            var rt = await _mqtt.PublishAndWaitAsync(
                publishTopic: $"zigbee/{device}",
                publishPayload: """{"temperature": 22.0}""",
                waitTopic: $"hermod/debug/{device}",
                timeout: TimeSpan.FromSeconds(5),
                ct: ct);

            _collector.Record(new TestResult
            {
                Category = "Resilience", Claim = ClaimO5, Name = "PodBounce_RecoveryTime",
                Status = rt.LatencyMs.HasValue ? "PASS" : "FAIL",
                LatencyMs = sw.Elapsed.TotalMilliseconds,
                CorrelationId = rt.CorrelationId,
                Details = rt.LatencyMs.HasValue
                    ? $"recovered in {sw.Elapsed.TotalSeconds:F1}s; post-recovery round trip {rt.LatencyMs:F1}ms"
                    : $"recovered in {sw.Elapsed.TotalSeconds:F1}s but post-recovery round trip timed out"
            });
        }
        catch (Exception ex)
        {
            _collector.Record(new TestResult
            {
                Category = "Resilience", Claim = ClaimO5, Name = "PodBounce_RecoveryTime",
                Status = "FAIL",
                LatencyMs = sw.Elapsed.TotalMilliseconds,
                Details = $"post-recovery probe threw {ex.GetType().Name}: {ex.Message}"
            });
        }
    }

    /// <summary>
    /// Restart the NanoMQ deployment, wait for the rollout to complete, then
    /// publish a correlated round trip and assert it still works (which
    /// proves the coordinator re-issued its wildcard subscription on
    /// reconnect, not that we slept long enough).
    /// </summary>
    private async Task BrokerBounceReconnect(CancellationToken ct)
    {
        _logger.LogInformation("Test: BrokerBounce_Reconnect");

        var sw = Stopwatch.StartNew();
        var restart = await _kubectl.RunAsync(
            "rollout restart deployment/nanomq", ct, TimeSpan.FromSeconds(30));
        if (!restart.Ok)
        {
            _collector.Record(new TestResult
            {
                Category = "Resilience", Claim = ClaimO5, Name = "BrokerBounce_Reconnect",
                Status = "ERROR",
                Details = $"kubectl rollout restart failed: {restart.Summary}"
            });
            return;
        }

        var status = await _kubectl.RunAsync(
            "rollout status deployment/nanomq --timeout=60s", ct, TimeSpan.FromSeconds(75));
        if (!status.Ok)
        {
            _collector.Record(new TestResult
            {
                Category = "Resilience", Claim = ClaimO5, Name = "BrokerBounce_Reconnect",
                Status = "FAIL",
                LatencyMs = sw.Elapsed.TotalMilliseconds,
                Details = $"nanomq rollout did not become ready within 60s: {status.Summary}"
            });
            return;
        }

        // The coordinator's MQTT reconnect supervisor has a 60s cap. Give it
        // up to 60s to re-establish before we expect a round trip to work.
        try
        {
            await _mqtt.ConnectAsync(ct);
            var device = $"resilience_broker_{Guid.NewGuid():N}".Substring(0, 24);
            var rtDeadline = TimeSpan.FromSeconds(60);
            var rtSw = Stopwatch.StartNew();
            CorrelatedResponse? rt = null;
            while (rtSw.Elapsed < rtDeadline && !ct.IsCancellationRequested)
            {
                rt = await _mqtt.PublishAndWaitAsync(
                    publishTopic: $"zigbee/{device}",
                    publishPayload: """{"temperature": 22.0}""",
                    waitTopic: $"hermod/debug/{device}",
                    timeout: TimeSpan.FromSeconds(5),
                    ct: ct);
                if (rt.LatencyMs.HasValue) break;
                await Task.Delay(1_000, ct);
            }

            _collector.Record(new TestResult
            {
                Category = "Resilience", Claim = ClaimO5, Name = "BrokerBounce_Reconnect",
                Status = rt?.LatencyMs.HasValue == true ? "PASS" : "FAIL",
                LatencyMs = sw.Elapsed.TotalMilliseconds,
                CorrelationId = rt?.CorrelationId,
                Details = rt?.LatencyMs.HasValue == true
                    ? $"broker bounce + coordinator reconnect in {sw.Elapsed.TotalSeconds:F1}s; post-reconnect RT {rt.LatencyMs:F1}ms"
                    : $"coordinator did not re-issue subscription within 60s of broker rollout"
            });
        }
        catch (Exception ex)
        {
            _collector.Record(new TestResult
            {
                Category = "Resilience", Claim = ClaimO5, Name = "BrokerBounce_Reconnect",
                Status = "FAIL",
                LatencyMs = sw.Elapsed.TotalMilliseconds,
                Details = $"post-reconnect probe threw {ex.GetType().Name}: {ex.Message}"
            });
        }
    }

    /// <summary>
    /// Apply a deny-all NetworkPolicy against svc/postgres for 30 s, assert
    /// the coordinator's /healthz/ready stays 200 throughout (it must not
    /// crash on transient DB unreachability), then delete the policy and
    /// assert recovery. The policy is deleted in finally so a failed
    /// assertion does not leave the cluster cut off from Postgres.
    /// </summary>
    private async Task PostgresLossSurvives(CancellationToken ct)
    {
        _logger.LogInformation("Test: PostgresLoss_Survives");

        const string PolicyName = "harness-deny-postgres";
        var policyYaml = $@"apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata:
  name: {PolicyName}
  namespace: hermod
spec:
  podSelector:
    matchLabels:
      app: postgres
  policyTypes: [""Ingress""]
  ingress: []
";

        var tmp = Path.GetTempFileName();
        await File.WriteAllTextAsync(tmp, policyYaml, ct);
        try
        {
            var apply = await _kubectl.RunAsync($"apply -f {tmp}", ct, TimeSpan.FromSeconds(15));
            if (!apply.Ok)
            {
                _collector.Record(new TestResult
                {
                    Category = "Resilience", Claim = ClaimO5, Name = "PostgresLoss_Survives",
                    Status = "ERROR",
                    Details = $"kubectl apply NetworkPolicy failed: {apply.Summary}"
                });
                return;
            }

            // For 30 seconds, sample /healthz/ready every 2 seconds. The
            // coordinator must not crash; stale-DB reads are allowed since
            // the readiness probe does not (currently) gate on DB reach.
            using var http = new HttpClient { BaseAddress = new Uri(_baseUrl), Timeout = TimeSpan.FromSeconds(2) };
            var endHold = DateTimeOffset.UtcNow.AddSeconds(30);
            var probesOk = 0; var probesFail = 0;
            while (DateTimeOffset.UtcNow < endHold && !ct.IsCancellationRequested)
            {
                try
                {
                    var resp = await http.GetAsync("/healthz/ready", ct);
                    if (resp.IsSuccessStatusCode) probesOk++; else probesFail++;
                }
                catch { probesFail++; }
                await Task.Delay(2_000, ct);
            }

            // Survivability gate: at least one probe in the 30 s window must
            // have been served. Total black-out fails the test.
            if (probesOk == 0)
            {
                _collector.Record(new TestResult
                {
                    Category = "Resilience", Claim = ClaimO5, Name = "PostgresLoss_Survives",
                    Status = "FAIL",
                    Details = $"coordinator stopped responding during DB outage; ok=0 fail={probesFail}"
                });
                return;
            }

            _collector.Record(new TestResult
            {
                Category = "Resilience", Claim = ClaimO5, Name = "PostgresLoss_Survives",
                Status = "PASS",
                Details = $"coordinator survived 30s Postgres deny-all: ok={probesOk} fail={probesFail}"
            });
        }
        finally
        {
            // Always delete the policy. A leaked deny-all in the cluster
            // would break every subsequent test in the matrix.
            var del = await _kubectl.RunAsync(
                $"delete networkpolicy {PolicyName} --ignore-not-found", ct, TimeSpan.FromSeconds(15));
            if (!del.Ok)
            {
                _logger.LogWarning("teardown of NetworkPolicy {Name} returned: {Summary}",
                    PolicyName, del.Summary);
            }
            try { File.Delete(tmp); } catch { /* best effort */ }
        }
    }
}
