#!/usr/bin/env bash
# smoke-prod-pi.sh — 5 end-to-end assertions against a deployed prod-pi
# cluster. Run this AFTER `hermod.sh install prod-pi` lands and the
# pods stabilise; it doesn't deploy or rotate anything itself.
#
# Inputs (env vars; defaults work for the LAN-side install):
#   COORD_HOST              host:port of the coordinator (default 10.0.0.1:42069)
#   MQTT_HOST               host:port of nanomq        (default 10.0.0.1:8883)
#   CA_BUNDLE               path to internal-CA cert    (default ~/.hermod-prod-certs/ca.crt)
#   CLIENT_CERT, CLIENT_KEY paths to the operator client cert
#                          (default ~/.hermod-prod-certs/client/{tls.crt,tls.key})
#   ADMIN_BEARER            JWT for an admin-tier vault42 user
#   VIEWER_BEARER           JWT for a viewer-tier vault42 user
#
# Get fresh JWTs via:
#   curl -s -X POST https://${COORD_HOST}/api/auth/login \
#     -H 'Content-Type: application/json' \
#     -d '{"email":"operator@hermod.local","password":"$HERMOD_VAULT42_OPERATOR_PASSWORD"}' \
#     --cacert "$CA_BUNDLE" | jq -r .access_token
#
# Exits non-zero on first failed assertion. Prints PASS/FAIL per check
# and a summary at the end.

set -euo pipefail

COORD_HOST="${COORD_HOST:-10.0.0.1:42069}"
MQTT_HOST="${MQTT_HOST:-10.0.0.1:8883}"
CA_BUNDLE="${CA_BUNDLE:-$HOME/.hermod-prod-certs/ca.crt}"
CLIENT_CERT="${CLIENT_CERT:-$HOME/.hermod-prod-certs/client/tls.crt}"
CLIENT_KEY="${CLIENT_KEY:-$HOME/.hermod-prod-certs/client/tls.key}"
ADMIN_BEARER="${ADMIN_BEARER:-}"
VIEWER_BEARER="${VIEWER_BEARER:-}"

PASS=0
FAIL=0

_pass() { printf '\033[0;32mPASS\033[0m %s\n' "$1"; PASS=$((PASS + 1)); }
_fail() { printf '\033[0;31mFAIL\033[0m %s — %s\n' "$1" "${2:-}" >&2; FAIL=$((FAIL + 1)); }

_require_cmd() { command -v "$1" >/dev/null 2>&1 || { printf 'missing dep: %s\n' "$1" >&2; exit 2; }; }
_require_cmd curl
_require_cmd mosquitto_sub
_require_cmd jq
[[ -f "$CA_BUNDLE" ]] || { printf 'CA_BUNDLE not found: %s\n' "$CA_BUNDLE" >&2; exit 2; }

# ── 1. HTTPS /healthz returns 200 with our internal CA ──────────────
check_healthz() {
    local code
    code="$(curl -sS --cacert "$CA_BUNDLE" -o /dev/null -w '%{http_code}' \
        "https://${COORD_HOST}/healthz" || true)"
    if [[ "$code" == "200" ]]; then
        _pass "1/5 https://${COORD_HOST}/healthz returns 200 verified by internal CA"
    else
        _fail "1/5 healthz" "got HTTP $code (expected 200)"
    fi
}

# ── 2. MQTT TLS subscribe with our client cert succeeds ─────────────
check_mqtt_with_cert() {
    local mqtt_h="${MQTT_HOST%:*}"
    local mqtt_p="${MQTT_HOST##*:}"
    # 2 s timeout, dummy topic. Success = clean disconnect (exit 0)
    # OR a 27 (timeout) — not a connection refusal.
    local rc
    timeout 3 mosquitto_sub \
        -h "$mqtt_h" -p "$mqtt_p" \
        --cafile "$CA_BUNDLE" \
        --cert "$CLIENT_CERT" --key "$CLIENT_KEY" \
        -t 'smoke/probe' -W 2 >/dev/null 2>&1 || rc=$?
    if [[ -z "${rc:-}" || "${rc}" == "27" ]]; then
        _pass "2/5 mosquitto_sub TLS w/ client cert connects"
    else
        _fail "2/5 mqtt-with-cert" "mosquitto_sub exited rc=$rc"
    fi
}

# ── 3. MQTT TLS subscribe WITHOUT client cert is rejected ───────────
check_mqtt_without_cert() {
    local mqtt_h="${MQTT_HOST%:*}"
    local mqtt_p="${MQTT_HOST##*:}"
    local rc
    timeout 3 mosquitto_sub \
        -h "$mqtt_h" -p "$mqtt_p" \
        --cafile "$CA_BUNDLE" \
        -t 'smoke/probe' -W 2 >/dev/null 2>&1 || rc=$?
    # Anything non-zero is fine — we expect TLS handshake refusal.
    # Exit 0 means the broker accepted us anonymously, which is the
    # security failure this check guards against.
    if [[ -z "${rc:-}" ]]; then
        _fail "3/5 mqtt-without-cert" "broker accepted anonymous TLS connect (security regression!)"
    else
        _pass "3/5 mosquitto_sub TLS w/o client cert is rejected (rc=$rc)"
    fi
}

# ── 4. /api/system/features 401 without bearer token ────────────────
check_features_unauth() {
    local code
    code="$(curl -sS --cacert "$CA_BUNDLE" -o /dev/null -w '%{http_code}' \
        "https://${COORD_HOST}/api/system/features" || true)"
    if [[ "$code" == "401" ]]; then
        _pass "4/5 /api/system/features returns 401 without bearer"
    else
        _fail "4/5 features-unauth" "got HTTP $code (expected 401)"
    fi
}

# ── 5. Admin bearer 200 on write; viewer bearer 403 on the same write ─
check_rbac_write() {
    if [[ -z "$ADMIN_BEARER" || -z "$VIEWER_BEARER" ]]; then
        _fail "5/5 rbac-write" "ADMIN_BEARER or VIEWER_BEARER unset — see header for how to mint them"
        return
    fi

    local target_endpoint="https://${COORD_HOST}/api/devices/smoke-probe-device/status"
    local payload='{"status":"online"}'

    # Admin: expect 200/204 (write accepted). 404 is also fine — the
    # device may not exist; the auth gate is what we're pinning.
    local admin_code
    admin_code="$(curl -sS --cacert "$CA_BUNDLE" \
        -H "Authorization: Bearer $ADMIN_BEARER" \
        -H 'Content-Type: application/json' \
        -X PATCH -d "$payload" \
        -o /dev/null -w '%{http_code}' \
        "$target_endpoint" || true)"
    case "$admin_code" in
        200|204|404) ;;
        *) _fail "5/5 rbac-write/admin" "admin got HTTP $admin_code (expected 200/204/404)"; return ;;
    esac

    # Viewer: expect 403.
    local viewer_code
    viewer_code="$(curl -sS --cacert "$CA_BUNDLE" \
        -H "Authorization: Bearer $VIEWER_BEARER" \
        -H 'Content-Type: application/json' \
        -X PATCH -d "$payload" \
        -o /dev/null -w '%{http_code}' \
        "$target_endpoint" || true)"
    if [[ "$viewer_code" == "403" ]]; then
        _pass "5/5 admin write OK ($admin_code), viewer write blocked ($viewer_code)"
    else
        _fail "5/5 rbac-write/viewer" "viewer got HTTP $viewer_code (expected 403)"
    fi
}

# ── runner ──────────────────────────────────────────────────────────
printf 'smoke-prod-pi: COORD=%s MQTT=%s CA=%s\n' "$COORD_HOST" "$MQTT_HOST" "$CA_BUNDLE"
check_healthz
check_mqtt_with_cert
check_mqtt_without_cert
check_features_unauth
check_rbac_write

printf '\n=== smoke summary: PASS=%d FAIL=%d ===\n' "$PASS" "$FAIL"
[[ "$FAIL" -eq 0 ]]
