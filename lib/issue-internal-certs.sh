#!/usr/bin/env bash
# issue-internal-certs.sh — generate the internal CA + per-service leaf certs
# for the Hermod prod-k8s overlay's mTLS mesh.
#
# Layout (under $CERT_DIR, default ~/.hermod-prod-certs/):
#   ca/
#     ca.crt           self-signed root, 4096-bit RSA, 10-year validity
#     ca.key           private key for the root (mode 0600)
#   leaves/
#     <name>.key       leaf private key (RSA traditional PEM, mode 0600)
#     <name>.csr       certificate signing request (transient)
#     <name>.crt       leaf cert signed by ca.crt (1-year validity)
#
# Each leaf cert lifetime: 1 year. Re-running with --force rotates all leaves
# while keeping the CA intact (Secrets re-seed via seed-internal-certs.sh).
#
# Leaf set (12 total — 5 server + 7 client):
#   Servers:  hermod-coord-server, hermod-nanomq-server,
#             hermod-mosquitto-server, hermod-vault42-server,
#             hermod-postgres-server
#   Clients:  hermod-coord-client, hermod-mosquitto-client,
#             hermod-zigbee2mqtt-client, hermod-ble2mqtt-client,
#             hermod-vault42-client, hermod-postgres-client,
#             hermod-lora2mqtt-client
#
# Reproducibility: the SANs and CN for each leaf match the in-cluster
# Service DNS plus the bare service name; the CA self-signs all of them.
# A receiver verifies a peer cert by chain-up-to ca.crt.

set -euo pipefail

CERT_DIR="${HERMOD_CERT_DIR:-$HOME/.hermod-prod-certs}"
CA_DIR="$CERT_DIR/ca"
LEAF_DIR="$CERT_DIR/leaves"
FORCE=0
[[ "${1:-}" == "--force" ]] && FORCE=1

mkdir -p "$CA_DIR" "$LEAF_DIR"
chmod 700 "$CA_DIR" "$LEAF_DIR"

log()  { printf '\033[1;32m[issue-certs]\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m[issue-certs]\033[0m %s\n' "$*" >&2; }

# ── 1. CA ─────────────────────────────────────────────────────────────
if [[ ! -f "$CA_DIR/ca.crt" ]]; then
    log "issuing internal CA (4096-bit RSA, 10-year)"
    openssl genrsa -traditional -out "$CA_DIR/ca.key" 4096 2>/dev/null
    chmod 600 "$CA_DIR/ca.key"
    openssl req -new -x509 -key "$CA_DIR/ca.key" -out "$CA_DIR/ca.crt" \
        -days 3650 -sha256 \
        -subj "/CN=hermod-internal-ca/O=Hermod/OU=Internal" \
        -extensions v3_ca \
        -config <(printf '[req]\ndistinguished_name=req\n[v3_ca]\nbasicConstraints=critical,CA:TRUE\nkeyUsage=critical,keyCertSign,cRLSign\nsubjectKeyIdentifier=hash\n')
    chmod 644 "$CA_DIR/ca.crt"
else
    log "CA already exists; reusing"
fi

# ── 2. helper to issue one leaf ────────────────────────────────────────
# usage: issue_leaf <name> <cn> <san-csv> <client|server|both>
issue_leaf() {
    local name="$1" cn="$2" san="$3" kind="$4"
    local key="$LEAF_DIR/$name.key"
    local csr="$LEAF_DIR/$name.csr"
    local crt="$LEAF_DIR/$name.crt"

    if [[ -f "$crt" && $FORCE -eq 0 ]]; then
        return 0
    fi

    local eku
    case "$kind" in
        client) eku="clientAuth" ;;
        server) eku="serverAuth" ;;
        both)   eku="serverAuth,clientAuth" ;;
        *) warn "bad kind for $name: $kind"; return 1 ;;
    esac

    log "  issue $name (CN=$cn, EKU=$eku)"
    # -traditional forces "BEGIN RSA PRIVATE KEY" PEM. OpenSSL 3.x
    # defaults to PKCS#8 ("BEGIN PRIVATE KEY") which .NET 9's
    # X509Certificate2.CreateFromPemFile rejects when paired with an
    # RSA cert ("key contents do not contain a PEM").
    openssl genrsa -traditional -out "$key" 2048 2>/dev/null
    chmod 600 "$key"

    openssl req -new -key "$key" -out "$csr" \
        -subj "/CN=$cn/O=Hermod/OU=Internal" \
        -addext "subjectAltName=$san"

    openssl x509 -req -in "$csr" -out "$crt" \
        -CA "$CA_DIR/ca.crt" -CAkey "$CA_DIR/ca.key" -CAcreateserial \
        -days 365 -sha256 \
        -extfile <(printf 'subjectAltName=%s\nextendedKeyUsage=%s\nkeyUsage=critical,digitalSignature,keyEncipherment\nbasicConstraints=critical,CA:FALSE\n' \
            "$san" "$eku") \
        2>/dev/null

    chmod 644 "$crt"

    if ! openssl verify -CAfile "$CA_DIR/ca.crt" "$crt" >/dev/null 2>&1; then
        warn "leaf $name FAILED verification against CA"
        return 1
    fi
    rm -f "$csr"
}

# ── 3. issue leaves ────────────────────────────────────────────────────
log "issuing 12 leaf certs (5 server + 7 client)"

# Servers — DNS includes both the bare service name and the FQDN. localhost
# is added to coord because the dev port-forward path hits localhost.
issue_leaf hermod-coord-server     hermod-coordinator \
    "DNS:hermod-coordinator,DNS:hermod-coordinator.hermod-prod.svc,DNS:hermod-coordinator.hermod-prod.svc.cluster.local,DNS:hermod.local,DNS:localhost" \
    server
issue_leaf hermod-nanomq-server    nanomq \
    "DNS:nanomq,DNS:nanomq.hermod-prod.svc,DNS:nanomq.hermod-prod.svc.cluster.local" \
    server
issue_leaf hermod-mosquitto-server mosquitto \
    "DNS:wifi2mqtt,DNS:wifi2mqtt.hermod-prod.svc,DNS:wifi2mqtt.hermod-prod.svc.cluster.local,DNS:mosquitto" \
    server
issue_leaf hermod-vault42-server   vault42 \
    "DNS:vault42,DNS:vault42.hermod-prod.svc,DNS:vault42.hermod-prod.svc.cluster.local" \
    server
issue_leaf hermod-postgres-server  postgres \
    "DNS:postgres,DNS:postgres.hermod-prod.svc,DNS:postgres.hermod-prod.svc.cluster.local,DNS:postgres-headless,DNS:postgres-0.postgres-headless.hermod-prod.svc.cluster.local" \
    server

# Clients — the CN is the service identity nanomq's ACL keys on.
issue_leaf hermod-coord-client       hermod-coord       "DNS:hermod-coord"       client
issue_leaf hermod-mosquitto-client   wifi-bridge        "DNS:wifi-bridge"        client
issue_leaf hermod-zigbee2mqtt-client zigbee2mqtt        "DNS:zigbee2mqtt"        client
issue_leaf hermod-ble2mqtt-client     ble2mqtt            "DNS:ble2mqtt"            client
issue_leaf hermod-vault42-client     vault42-client     "DNS:vault42-client"     client
issue_leaf hermod-postgres-client    postgres-client    "DNS:postgres-client"    client
issue_leaf hermod-lora2mqtt-client   lora2mqtt          "DNS:lora2mqtt"          client

log "DONE — CA fp = $(openssl x509 -in "$CA_DIR/ca.crt" -noout -fingerprint -sha256 | cut -d= -f2)"
log "leaves: $(ls "$LEAF_DIR"/*.crt 2>/dev/null | wc -l)"
