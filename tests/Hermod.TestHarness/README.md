# Hermod Test Harness

A .NET 10 console app that drives integration, performance, security, and resilience tests against a running Hermod deployment. Implements the methodology in [`Hermod/docs/TESTING_METHODOLOGY.md`](../../docs/TESTING_METHODOLOGY.md) and the per-runner contracts in [`Hermod/docs/TESTING_HARNESS.md`](../../docs/TESTING_HARNESS.md).

## What it covers

| Mode | Runner | Claims | What it does |
|------|--------|--------|--------------|
| `--functional` | `FunctionalTestRunner` | C2, C3 | Cross-protocol translation, multi-protocol rules, semantic preservation across primitives, no-spurious-actions safety check |
| `--performance` | `PerformanceTestRunner` | C1, C4, C5 | End-to-end latency p99 (200 round-trips), stability under design load (`Performance__StabilityDurationSec` s, $\lambda$=`Performance__DesignRateMsgPerSec` msg/s; matrix sets 120 s / 500 msg/s), soak progress (`Performance__SoakMinutes` min at the same $\lambda$) |
| `--e2e` | `HttpE2ETestRunner` | O3 | Health endpoints, anonymous-401 contract, login, devices CRUD, rules list, stats history persistence |
| `--auth-attack` | `AuthAttackTestRunner` | O4 | 22 JWT and Authorization-header attack variants against every protected surface, plus YARP proxy gating |
| `--security` | `SecurityTestRunner` | O4 | Malformed-payload barrage, MQTT topic-injection counter delta. LoRa replay/spoof reported as `NOT_IMPLEMENTED` (deferred to future work) |
| `--resilience` | `ResilienceTestRunner` | O5 | Pod bounce, broker bounce, DB loss — each shells out via `KubectlClient` to delete / restore the target object and checks recovery |
| `--lora` | `LoRaTestRunner` | O5 RF | Real RF tests against a Waveshare SX1262 module. Off the laptop test path |
| `--all` | every runner | every claim | Sequential execution with `MeasurementCollector` flushed once at exit |

## Output

Every assertion produces one row in `MeasurementCollector` and is flushed to both `results_<ts>.json` and `results_<ts>.csv` in `HERMOD_RESULTS_DIR` (default `./results/`). The JSON envelope additionally records `generatedAt`, `environment`, `gitSha`, and a `byStatus` summary. Permitted statuses are `PASS`, `FAIL`, `NOT_IMPLEMENTED`, `INFO`, `ERROR`. Any other value is rejected at record time.

## Required environment

```bash
HERMOD_URL=http://hermod-coordinator.hermod.svc.cluster.local:42069
HERMOD_ADMIN_EMAIL=v@l.l            # matches the seeded user in vault42-seed-configmap
HERMOD_ADMIN_PASSWORD=...           # required for --e2e, --auth-attack, --security, --all;
                                    # source-of-truth is the vault42-seed-credentials Secret
                                    # (deploy-kind.sh --default-secrets renders it as
                                    # change-me-in-production-user; --auto-secrets generates
                                    # a random value printed in the deploy summary)
Mqtt__Host=nanomq.hermod.svc.cluster.local
Mqtt__Port=1883
Performance__LatencyIterations=200      # matrix job: 100
Performance__DesignRateMsgPerSec=50     # matrix job: 500
Performance__StabilityDurationSec=30    # matrix job: 120
Performance__SoakMinutes=5              # matrix job: 5
HERMOD_RESULTS_DIR=/app/results
GIT_SHA=$(git rev-parse --short HEAD)
```

`HERMOD_ADMIN_PASSWORD` is required for any mode that touches the authenticated REST surface. The runner exits with a single `ERROR` row before starting if the variable is missing.

## Running locally with `dotnet`

```bash
distrobox enter dotnet-box -- dotnet run \
    --project tests/Hermod.TestHarness -- --functional
```

Pre-requirement: a Hermod deployment is reachable. For the laptop loop this means `scripts/deploy-kind.sh` has been run and the kind cluster is up.

## Running the full laptop / kind matrix

The matrix runs every mode against the three resource profiles `baseline`, `tight`, and `breaking`. Total wall-clock budget is approximately twenty-five minutes for the system-under-test work, plus image build and per-profile rollouts.

```bash
export HERMOD_ADMIN_PASSWORD=...
scripts/deploy-kind.sh                  # one-time cluster bring-up
scripts/k8s-test-run.sh                 # all three profiles
scripts/k8s-test-run.sh baseline        # one profile
```

Pre-requirement that is not yet automated: install `metrics-server` so the orchestration script can sample `kubectl top pod` at one Hertz.

```bash
kubectl apply -f https://github.com/kubernetes-sigs/metrics-server/releases/latest/download/components.yaml
kubectl -n kube-system patch deployment metrics-server --type=json \
  -p='[{"op":"add","path":"/spec/template/spec/containers/0/args/-","value":"--kubelet-insecure-tls"}]'
```

## Outputs of a matrix run

```
tests/results/<RUN_ID>/
  baseline/
    results_<ts>.{json,csv}      MeasurementCollector rows
    top.tsv                      kubectl top pod stream at 1 Hz
    pods_pre.json                kubectl get pod -o json
    pods_mid.json
    pods_post.json
    stats_history.json           coordinator-side counters
    harness.log                  pod logs from the harness Job
  tight/        (same shape)
  breaking/     (same shape)

figures/test_results/
  latency_percentiles.png        bar chart, p99 per profile vs C4 SLO line
  stability_rho.png              bar chart, observed rho per profile vs C1 SLO line
  soak_progress.png              per-minute completions, three profiles overlaid
  resource_top_<profile>.png     CPU and memory time series, one panel per pod
  failure_modes.png              restart counts (and OOMKilled) per pod per profile
  pass_rate_table.tex            LaTeX longtable, claim x profile pass count
```

## Known gaps (as of 2026-04-23)

- `PerformanceTestRunner` seeds `harness-perf-fwd-<guid>` via `HermodApiClient` when `HERMOD_ADMIN_PASSWORD` is set and tears it down after the run; without the password it skips with a `NOT_IMPLEMENTED` row explaining the missing credential (the runner itself is complete).
- `LoRaTestRunner` requires a Waveshare SX1262 module and is therefore out of scope for the laptop / kind path.
- `kubectl top` requires `metrics-server`, which is not installed in `kind` by default. The orchestration script warns rather than fails if the metrics endpoint is unreachable.

## Methodology rules (extracted from `TESTING_METHODOLOGY.md` section 4)

These are non-negotiable when adding new tests:

- No assertion shall pass on "nothing happened". Scope absences to a run-unique topic, or count a positive rejection signal.
- No `Status = INFO` on a path that claims to validate a property. Use `PASS`, `FAIL`, or `NOT_IMPLEMENTED`.
- Every wait has a termination condition other than a fixed sleep.
- Every correlation uses an explicit GUID injected into the source payload, matched against the forwarded output. No topic re-subscription, no `Contains`, no `ToString` comparison.
- Every test cleans up what it created.
- No hardcoded credentials. Read from env, fail fast if missing.
- Percentiles require at least sixty samples (enforced by `Percentiles.MinSamplesForPercentiles`); below that, emit a sample-size row and a `NOT_IMPLEMENTED` claim row.
