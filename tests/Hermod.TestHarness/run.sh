#!/usr/bin/env bash
# Hermod.TestHarness/run.sh — one-shot harness runner.
#
# Builds the harness image, loads it into the kind cluster, applies the
# baseline overlay Job, waits for the harness to write its results,
# extracts them to ./results/<run-id>/, then cleans up.
#
# Use this for iterative "what do my numbers look like right now" work.
# For the full multi-profile matrix (baseline + tight + breaking, with
# `kubectl top` sampling and pre/mid/post pod snapshots), use
# ../../scripts/k8s-test-run.sh instead.
#
# Env overrides:
#   HERMOD_ADMIN_PASSWORD   defaults to the vault42-seed value for v@l.l
#   PROFILE                 kustomize overlay name (default: test-limits-baseline)
#   RUN_ID                  output dir name under ./results/ (default: run-<UTC>)
#   CLUSTER                 kind cluster name (default: hermod)
#   NS                      namespace (default: hermod)
#
# Why the sleep-wrap inside the Job:
#   `kubectl cp` does a `kubectl exec` under the hood, and newer kubectl
#   refuses to exec into a Succeeded pod. If we let the harness exit
#   naturally the pod flips to Succeeded AND kubelet GCs the emptyDir
#   volume within seconds, so /app/results is gone before we can copy
#   it. We override the container command to run the harness, then sleep
#   600s — keeps the pod in Running just long enough for us to cp the
#   results out. TTL on the Job cleans everything up afterwards.

set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"     # .../Hermod
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"            # .../Hermod/tests/Hermod.TestHarness
IMG="localhost/hermod-test-harness:latest"
CLUSTER="${CLUSTER:-hermod}"
NS="${NS:-hermod}"
KCTX="kind-${CLUSTER}"
PROFILE="${PROFILE:-test-limits-baseline}"
PW="${HERMOD_ADMIN_PASSWORD:-change-me-in-production-user}"
RUN_ID="${RUN_ID:-run-$(date -u +%Y%m%dT%H%M%SZ)}"
OUT="$HERE/results/$RUN_ID"

mkdir -p "$OUT"

log()  { printf '[run] %s\n' "$*"; }
die()  { printf '[run] FATAL: %s\n' "$*" >&2; exit 1; }

require() { command -v "$1" >/dev/null 2>&1 || die "$1 not in PATH"; }
for t in podman kind kubectl python3; do require "$t"; done

# ── 1. build ──────────────────────────────────────────────────────
log "build $IMG from $REPO"
( cd "$REPO" && podman build --format=docker -t "$IMG" \
    -f tests/Hermod.TestHarness/Dockerfile . ) > "$OUT/build.log" 2>&1 \
    || { tail -30 "$OUT/build.log"; die "build failed (see $OUT/build.log)"; }

# ── 2. load into kind ─────────────────────────────────────────────
log "kind load into cluster=$CLUSTER"
TAR="$(mktemp -t hermod-run-XXXXXX.tar)"
trap 'rm -f "$TAR"' EXIT
podman save -o "$TAR" "$IMG" 2>> "$OUT/build.log"
KIND_EXPERIMENTAL_PROVIDER=podman \
    kind load image-archive "$TAR" --name "$CLUSTER" \
    >> "$OUT/build.log" 2>&1 \
    || die "kind load failed"

# Retag inside the node so bare-name manifests (image: hermod-test-harness:latest)
# also resolve. Containerd does not auto-prefix docker.io/library.
podman exec "${CLUSTER}-control-plane" \
    ctr --namespace k8s.io images tag --force \
    "$IMG" "docker.io/library/${IMG#localhost/}" >/dev/null 2>&1 || true

# ── 3. apply overlay with sleep-wrapped Job command ───────────────
log "apply overlay $PROFILE (Job command wrapped with post-run sleep)"
kubectl --context="$KCTX" -n "$NS" delete job hermod-test-harness --ignore-not-found >/dev/null 2>&1 || true

kubectl --context="$KCTX" kustomize "$REPO/kubernetes/overlays/$PROFILE" \
 | python3 - <<'PY' \
 | kubectl --context="$KCTX" apply -f - >> "$OUT/build.log" 2>&1
import sys, yaml
for doc in yaml.safe_load_all(sys.stdin):
    if not doc:
        continue
    if doc.get("kind") == "Job" and doc.get("metadata", {}).get("name") == "hermod-test-harness":
        c = doc["spec"]["template"]["spec"]["containers"][0]
        # The harness's Dockerfile ENTRYPOINT is ["dotnet", "Hermod.TestHarness.dll"].
        # Replacing command+args here means we explicitly run it under sh so we
        # can chain the sleep after it exits.
        c["command"] = ["/bin/sh", "-c"]
        c["args"] = ["dotnet Hermod.TestHarness.dll; echo HARNESS_EXIT=$?; sleep 600"]
    print("---")
    yaml.safe_dump(doc, sys.stdout, default_flow_style=False, sort_keys=False)
PY

# ── 4. seed the real admin password (overlay ships a placeholder) ─
# The harness Job reads HERMOD_ADMIN_PASSWORD from
# hermod-test-harness-secrets.admin-password. Manifest ships a
# `REPLACE_AT_APPLY_TIME` placeholder so it validates in isolation;
# this step patches it to the live password (from $PW, which defaults
# to the vault42-seed user default but is operator-overridable via
# HERMOD_ADMIN_PASSWORD env).
kubectl --context="$KCTX" -n "$NS" create secret generic hermod-test-harness-secrets \
    --from-literal=admin-password="$PW" \
    --dry-run=client -o yaml \
 | kubectl --context="$KCTX" apply -f - > /dev/null

# ── 5. wait for harness completion (not Job — it's sleeping now) ──
log "waiting for harness to finish writing results (15 min cap)"
POD=""
for i in $(seq 1 180); do    # 180 * 5s = 15m
    POD="$(kubectl --context="$KCTX" -n "$NS" get pod -l app=hermod-test-harness \
           -o jsonpath='{.items[0].metadata.name}' 2>/dev/null || true)"
    if [[ -n "$POD" ]] && \
       kubectl --context="$KCTX" -n "$NS" logs "$POD" 2>/dev/null \
         | grep -q "Test run complete"; then
        log "harness done after ~$((i*5))s (pod=$POD)"
        break
    fi
    sleep 5
done
[[ -n "$POD" ]] || die "no harness pod appeared within 15 min"

# ── 6. extract results + logs ─────────────────────────────────────
log "cp /app/results → $OUT"
kubectl --context="$KCTX" -n "$NS" cp "$POD:/app/results" "$OUT/" 2>&1 | tail -3 || true
kubectl --context="$KCTX" -n "$NS" logs "$POD" > "$OUT/harness.log" 2>&1 || true

# ── 7. clean up the Job (cancels the sleep, triggers TTL) ─────────
kubectl --context="$KCTX" -n "$NS" delete job hermod-test-harness --ignore-not-found >/dev/null 2>&1 || true

# ── 8. summarise ──────────────────────────────────────────────────
log "results in $OUT"
ls -la "$OUT" | sed 's/^/    /'

# Print a quick count if we landed a results.json.
shopt -s nullglob
for r in "$OUT"/results_*.json; do
    n=$(python3 -c 'import json,sys; print(len(json.load(open(sys.argv[1]))))' "$r" 2>/dev/null || echo "?")
    log "$(basename "$r"): $n rows"
done
