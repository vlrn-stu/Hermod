#!/usr/bin/env bash
# seed-internal-certs.sh — push the certs from issue-internal-certs.sh into
# the cluster as 13 Secrets in the hermod-prod namespace.
#
# Pairing convention:
#   internal-ca           Opaque, key=ca.crt          mounted into every consumer
#   <leaf-name>-tls       kubernetes.io/tls,
#                         tls.crt + tls.key + ca.crt  mounted by the owning pod
#
# Usage:
#   ./lib/seed-internal-certs.sh                    # apply to current ctx
#   HERMOD_KUBE_CTX=kind-hermod ./lib/seed-internal-certs.sh
#
# Idempotent — uses kubectl apply, so re-running on rotated certs updates them.

set -euo pipefail

CERT_DIR="${HERMOD_CERT_DIR:-$HOME/.hermod-prod-certs}"
CA_DIR="$CERT_DIR/ca"
LEAF_DIR="$CERT_DIR/leaves"
NAMESPACE="${HERMOD_PROD_NAMESPACE:-hermod-prod}"
CTX="${HERMOD_KUBE_CTX:-}"
# Honour KUBECTL env (matches ensure-secrets.sh). Pi only has
# `microk8s kubectl` — no standalone kubectl binary.
KCTL=(${KUBECTL:-kubectl})
[[ -n "$CTX" ]] && KCTL+=(--context "$CTX")

log() { printf '\033[1;32m[seed-certs]\033[0m %s\n' "$*"; }
die() { printf '\033[1;31m[seed-certs]\033[0m %s\n' "$*" >&2; exit 1; }

[[ -f "$CA_DIR/ca.crt" ]] || die "no CA found — run issue-internal-certs.sh first"
[[ -d "$LEAF_DIR" ]]      || die "no leaves dir — run issue-internal-certs.sh first"

# Ensure namespace exists
"${KCTL[@]}" get namespace "$NAMESPACE" >/dev/null 2>&1 || \
    "${KCTL[@]}" create namespace "$NAMESPACE"

# 1. internal-ca Secret (opaque; one key: ca.crt)
log "applying internal-ca → ns=$NAMESPACE"
"${KCTL[@]}" -n "$NAMESPACE" create secret generic internal-ca \
    --from-file=ca.crt="$CA_DIR/ca.crt" \
    --dry-run=client -o yaml | "${KCTL[@]}" apply -f -

# 2. one TLS Secret per leaf
count=0
for crt in "$LEAF_DIR"/*.crt; do
    name=$(basename "$crt" .crt)
    key="$LEAF_DIR/$name.key"
    [[ -f "$key" ]] || die "missing key for $name"

    secret_name="${name}-tls"
    log "applying $secret_name"

    # kubernetes.io/tls Secret + bundled ca.crt for chain verification
    "${KCTL[@]}" -n "$NAMESPACE" create secret generic "$secret_name" \
        --type=kubernetes.io/tls \
        --from-file=tls.crt="$crt" \
        --from-file=tls.key="$key" \
        --from-file=ca.crt="$CA_DIR/ca.crt" \
        --dry-run=client -o yaml | "${KCTL[@]}" apply -f -

    count=$((count + 1))
done

log "DONE — applied internal-ca + $count *-tls Secrets to ns=$NAMESPACE"
# `| head -N` SIGPIPEs kubectl when the namespace happens to have exactly
# N+1 lines of output (header + N rows). Print the full list and let the
# operator scroll; the namespace never holds more than ~30 Secrets and the
# step is purely informational anyway.
"${KCTL[@]}" -n "$NAMESPACE" get secrets || true
