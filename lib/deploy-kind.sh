#!/usr/bin/env bash
# Hermod — Fedora + kind + podman deployment
#
# Builds all images via podman, loads them into a local kind cluster,
# applies the selected overlay, and waits for rollouts.
#
# Usage:
#   deploy-kind.sh [--mock|--hardware|--auto] [--no-build]
#                  [--auto-secrets|--default-secrets|--keep-secrets]
#
#   --mock              Apply overlays/dev (default). Mock translators,
#                       no USB needed.
#   --hardware          Apply overlays/dev-hardware. Real Zigbee + LoRa
#                       dongles passed through from /dev/ttyUSB0 and
#                       /dev/ttyACM0.
#   --auto              Pick mode by probing for plugged dongles: if
#                       both ttyUSB0 and ttyACM0 exist AND are readable
#                       by the current user, use --hardware; otherwise
#                       --mock.
#   --no-build          Skip image builds; reuse whatever's already in
#                       the local podman storage.
#
#   --auto-secrets      Non-interactive: auto-generate every password,
#                       keep default usernames. Prints nothing sensitive.
#   --default-secrets   Non-interactive: use the in-repo `change-me`
#                       placeholder values (dev only — easy to spin up).
#   --keep-secrets      Non-interactive: only create Secrets that don't
#                       exist yet; never overwrite existing values.
#   (no flag)           Interactive: prompt per missing key, offering
#                       [A]uto / [D]efault / [C]ustom.
#
# Prerequisites on the host:
#   - podman (rootful or rootless)
#   - kind (https://kind.sigs.k8s.io)
#   - kubectl
#   - v in `dialout` group AND a udev rule chowning ttyUSB*/ttyACM*
#     to `v:dialout 0660`. Required only for --hardware. Rootless
#     kind's node runs as the host user `v`, so the tty node must be
#     owned by v for the kind node (and therefore the translator pods)
#     to open it.
#
# Builds happen inside .NET SDK containers via the project Dockerfiles,
# so a local dotnet install is NOT required.

set -euo pipefail

# ── paths ──────────────────────────────────────────────────────────────────
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"

CLUSTER_NAME="hermod"
KIND_CONFIG="$SCRIPT_DIR/kind-config.yaml"

# ── kind + podman ──────────────────────────────────────────────────────────
export KIND_EXPERIMENTAL_PROVIDER=podman

# Rootless podman + kind needs an extra cgroup delegation. If user.slice
# doesn't already have Delegate=yes, re-exec under a delegated systemd scope
# so kind's preflight check passes. Re-exec MUST happen before arg parsing
# — otherwise the `while ... shift` loop drains $@ and the new invocation
# gets no flags (silently falling back to --mock, the default).
if [[ "$(systemctl --user show user.slice -p Delegate --value 2>/dev/null)" != "yes" ]] \
   && [[ -z "${HERMOD_DELEGATED:-}" ]]; then
    export HERMOD_DELEGATED=1
    exec systemd-run --user --scope --quiet --property=Delegate=yes "$0" "$@"
fi

# ── args ───────────────────────────────────────────────────────────────────
MODE=""
# Honour HERMOD_NO_BUILD env (set by `hermod.sh install --no-build`) so the
# parent CLI can disable rebuilds without rewiring argv.
BUILD=1; [[ "${HERMOD_NO_BUILD:-0}" == "1" ]] && BUILD=0
SECRETS_MODE=""  # forwarded to lib/ensure-secrets.sh
while [[ $# -gt 0 ]]; do
    case "$1" in
        --mock)              MODE="mock"; shift ;;
        --hardware)          MODE="hardware"; shift ;;
        --auto)              MODE="auto"; shift ;;
        --no-build)          BUILD=0; shift ;;
        --auto-secrets)      SECRETS_MODE="auto"; shift ;;
        --default-secrets)   SECRETS_MODE="defaults"; shift ;;
        --keep-secrets)      SECRETS_MODE="keep"; shift ;;
        -h|--help)
            sed -n '3,40p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
            exit 0 ;;
        *)                   echo "unknown arg: $1" >&2; exit 2 ;;
    esac
done
MODE="${MODE:-mock}"

# ── helpers ────────────────────────────────────────────────────────────────
log()   { printf '\033[1;32m[deploy-kind]\033[0m %s\n' "$*"; }
warn()  { printf '\033[1;33m[deploy-kind]\033[0m %s\n' "$*" >&2; }
fatal() { printf '\033[1;31m[deploy-kind]\033[0m %s\n' "$*" >&2; exit 1; }

require() {
    command -v "$1" >/dev/null 2>&1 || fatal "missing required command: $1"
}

# ── mode resolution + hardware preflight ──────────────────────────────────
HAS_ZIGBEE=0; HAS_LORA=0
[[ -r /dev/ttyUSB0 && -w /dev/ttyUSB0 ]] && HAS_ZIGBEE=1
[[ -r /dev/ttyACM0 && -w /dev/ttyACM0 ]] && HAS_LORA=1

if [[ "$MODE" == "auto" ]]; then
    if (( HAS_ZIGBEE && HAS_LORA )); then
        MODE="hardware"
        log "auto: both dongles readable → using hardware mode"
    else
        MODE="mock"
        log "auto: dongles not readable (zigbee=$HAS_ZIGBEE, lora=$HAS_LORA) → using mock mode"
    fi
fi

OVERLAY_DIR="$REPO_ROOT/kubernetes/overlays/dev"
[[ "$MODE" == "hardware" ]] && OVERLAY_DIR="$REPO_ROOT/kubernetes/overlays/dev-hardware"

if [[ "$MODE" == "hardware" ]]; then
    (( HAS_ZIGBEE )) || fatal "hardware mode: /dev/ttyUSB0 not readable+writable by $USER (needed for zigbee2mqtt). Fix with:
  sudo sh -c 'usermod -aG dialout $USER; echo \"KERNEL==\\\"ttyUSB[0-9]*|ttyACM[0-9]*\\\", OWNER=\\\"$USER\\\", GROUP=\\\"dialout\\\", MODE=\\\"0660\\\"\" > /etc/udev/rules.d/70-hermod-serial.rules; udevadm control --reload; udevadm trigger; chown $USER:dialout /dev/ttyUSB0 /dev/ttyACM0; chmod 0660 /dev/ttyUSB0 /dev/ttyACM0'"
    (( HAS_LORA )) || fatal "hardware mode: /dev/ttyACM0 not readable+writable by $USER (needed for lora2mqtt). See command above."
fi

# ── 1. preflight ───────────────────────────────────────────────────────────
log "preflight: mode=$MODE build=$BUILD"
require podman
require kind
require kubectl


# ── 2. cluster ─────────────────────────────────────────────────────────────
if kind get clusters 2>/dev/null | grep -qx "$CLUSTER_NAME"; then
    log "cluster '$CLUSTER_NAME' already exists, reusing"
    if [[ "$MODE" == "hardware" ]]; then
        # Caveat: kind extraMounts bind /dev/ttyUSB0 + /dev/ttyACM0 at
        # node creation time. If a dongle was unplugged/replugged since
        # cluster create, the bind-mount captures a stale inode and the
        # device shows up as c--------- inside the node. The translators
        # will then fail with "error mounting /dev/ttyUSB0: no such
        # file or directory". Fix: `podman restart ${CLUSTER_NAME}-control-plane`.
        warn "reusing existing cluster in hardware mode — if a dongle was replugged since the cluster was created, restart the node: 'podman restart ${CLUSTER_NAME}-control-plane' before rolling pods."
    fi
else
    log "creating kind cluster '$CLUSTER_NAME' (podman provider)"
    kind create cluster --name "$CLUSTER_NAME" --config "$KIND_CONFIG"
fi

kubectl config use-context "kind-$CLUSTER_NAME" >/dev/null

# ── 3. build images via podman ─────────────────────────────────────────────
# Vault42.AspNetCore restores from public nuget.org during the
# in-container `dotnet restore`, so no local feed needs to be packed.
if (( BUILD )); then
    log "building hermod-coordinator:latest"
    podman build \
        --format=docker \
        -t hermod-coordinator:latest \
        -f "$REPO_ROOT/src/Hermod.Coordinator/Dockerfile" \
        "$REPO_ROOT/src"

    log "building lora2mqtt:latest"
    podman build \
        --format=docker \
        -t lora2mqtt:latest \
        -f "$REPO_ROOT/src/LoRa2MQTT/LoRa2MQTT.Service/Dockerfile" \
        "$REPO_ROOT/src"

    # Vault42 is consumed as a drop-in auth server — kubelet pulls
    # ghcr.io/42-v/vault42 at the version pinned in the deployment
    # manifest.
else
    log "skipping image builds (--no-build)"
fi

# ── 5. load into kind ──────────────────────────────────────────────────────
# kind+podman has known quirks resolving locally-built images by short name.
# Save each image to a tar and load via `kind load image-archive`, which is
# naming-agnostic and reliable across podman/docker provider differences.
# After loading, retag inside the kind node so the bare name resolves
# (kind/containerd stores under localhost/ but k8s manifests use bare names).
log "loading images into kind cluster (via tar archives)"
TMP_TARS="$(mktemp -d -t hermod-img-XXXXXX)"
trap 'rm -rf "$TMP_TARS"' EXIT
for img in hermod-coordinator:latest lora2mqtt:latest; do
    # Ensure both bare and localhost-prefixed names exist so the export resolves.
    podman tag "localhost/$img" "$img" 2>/dev/null || true
    tar="$TMP_TARS/${img%%:*}.tar"
    log "  saving $img → $(basename "$tar")"
    podman save -o "$tar" "$img"
    kind load image-archive "$tar" --name "$CLUSTER_NAME"
done
rm -rf "$TMP_TARS"
trap - EXIT

log "retagging images inside kind node so bare names resolve"
KIND_NODE="${CLUSTER_NAME}-control-plane"
for img in hermod-coordinator lora2mqtt; do
    podman exec "$KIND_NODE" sh -c \
        "ctr --namespace k8s.io images tag --force localhost/$img:latest docker.io/library/$img:latest" \
        > /dev/null 2>&1 || warn "retag of $img inside kind node failed"
done

# eclipse-mosquitto and other public images are pulled directly from the cluster.

# When HERMOD_SKIP_APPLY=1 (set by hermod.sh install prod-kind), exit early
# after cluster + image setup. The caller will run its own ensure-secrets
# + kubectl apply against the prod overlay in the hermod-prod namespace.
if [[ "${HERMOD_SKIP_APPLY:-0}" == "1" ]]; then
    log "HERMOD_SKIP_APPLY=1 — cluster + images ready; caller owns the apply"
    exit 0
fi

# ── 6. ensure Secrets ──────────────────────────────────────────────────────
# kubernetes/base/secrets.yaml is excluded from the base kustomization so
# this script owns credential material instead of shipping `change-me-in-
# production` placeholders straight from git. ensure-secrets.sh reads
# HERMOD_SECRETS_MODE to decide between interactive prompts,
# auto-generated passwords, literal defaults, or keep-existing.
log "ensuring cluster Secrets (namespace 'hermod' must exist first)"
kubectl get namespace hermod >/dev/null 2>&1 || kubectl create namespace hermod >/dev/null
# shellcheck source=lib/ensure-secrets.sh
HERMOD_SECRETS_MODE="$SECRETS_MODE" . "$SCRIPT_DIR/lib/ensure-secrets.sh"
ensure_secrets

# ── 7. apply manifests ─────────────────────────────────────────────────────
log "applying overlay: ${OVERLAY_DIR#$REPO_ROOT/}"
kubectl apply -k "$OVERLAY_DIR"

# ── 8. wait for rollouts ───────────────────────────────────────────────────
log "waiting for core deployments to become ready (timeout 5m)"
CORE_DEPS=(postgres vault42 nanomq wifi2mqtt hermod-coordinator lora2mqtt)
# zigbee2mqtt only gets a pod in hardware mode (nodeSelector keeps it
# Pending in the mock overlay because there's no real dongle labeled
# node). Wait for it only when --hardware.
[[ "$MODE" == "hardware" ]] && CORE_DEPS+=(zigbee2mqtt)
for dep in "${CORE_DEPS[@]}"; do
    kind="deployment"
    [[ "$dep" == "postgres" ]] && kind="statefulset"
    if kubectl -n hermod get "$kind/$dep" >/dev/null 2>&1; then
        kubectl -n hermod rollout status "$kind/$dep" --timeout=5m || \
            warn "$kind/$dep did not become ready in time"
    fi
done

# ── 9. summary ─────────────────────────────────────────────────────────────
cat <<EOF

────────────────────────────────────────────────────────
  Hermod is up (mode: $MODE).
────────────────────────────────────────────────────────

  Coordinator dashboard : http://localhost:42069
  Zigbee2MQTT dashboard : http://localhost:42080 $([[ "$MODE" == "hardware" ]] && echo "(real hardware)" || echo "(mock — no dongle passthrough)")
  LoRa2MQTT API         : http://localhost:42081 $([[ "$MODE" == "hardware" ]] && echo "(real hardware)" || echo "(mock)")

  Seeded logins (rotate via dashboard before exposing this anywhere):
    viewer    viewer@hermod.local    ${HERMOD_VAULT42_VIEWER_PASSWORD:-<see vault42-seed-credentials Secret>}
    user      user@hermod.local      ${HERMOD_VAULT42_USER_PASSWORD:-<see vault42-seed-credentials Secret>}
    operator  operator@hermod.local  ${HERMOD_VAULT42_OPERATOR_PASSWORD:-<see vault42-seed-credentials Secret>}
  (rendered into seed.json by the render-seed init container from the
   vault42-seed-credentials Secret; seed is consumed once on first boot)

  Cluster context       : kind-$CLUSTER_NAME
  Tear down             : kind delete cluster --name $CLUSTER_NAME

EOF
