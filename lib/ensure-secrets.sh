#!/usr/bin/env bash
# ensure-secrets.sh — populate the K8s Secrets Hermod's manifests require.
#
# Sourced (not executed) by deploy-kind.sh and by the ansible
# hermod_deploy role AFTER the cluster is reachable and BEFORE
# `kubectl apply -k`. Replaces the
# static placeholder values in `kubernetes/base/secrets.yaml` (which
# is no longer part of the kustomization) with real per-cluster
# values: dev-friendly defaults by default, auto-generated passwords
# when asked, or interactively-prompted values.
#
# Modes — set HERMOD_SECRETS_MODE before sourcing:
#   interactive (default) — prompt for any missing / still-default key,
#                           offering [A]uto-generate / [D]efault / custom
#   auto                  — non-interactive: auto-generate every password,
#                           keep defaults for usernames
#   defaults              — non-interactive: same effect as auto for password
#                           slots (every default is now generated at runtime
#                           via _sec_random_password / _sec_random_crypto_key)
#   keep                  — non-interactive: only create Secrets that
#                           don't exist in the cluster yet; never touch
#                           existing ones, no matter what their value is.
#                           Use for idempotent re-runs after the operator
#                           has set real values via kubectl directly.
#   from-env              — non-interactive: each secret value is read
#                           from a HERMOD_* env var (HERMOD_MQTT_PASSWORD,
#                           HERMOD_VAULT42_MASTER_KEY, etc — see
#                           hermod-prod.env.example for the full set).
#                           Falls back to the in-file default with a warn
#                           if a particular env var is unset; never
#                           silently auto-generates.
#
# The main deploy scripts resolve the mode from flags (--auto-secrets,
# --default-secrets, --keep-secrets) and default to `interactive` when
# stdin is a TTY, else `keep` (so automated CI reruns don't clobber).

set -euo pipefail

# ── logging ───────────────────────────────────────────────────────────
_sec_log()  { printf '\033[1;34m[ensure-secrets]\033[0m %s\n' "$*" >&2; }
_sec_warn() { printf '\033[1;33m[ensure-secrets]\033[0m %s\n' "$*" >&2; }

# ── helpers ───────────────────────────────────────────────────────────

# Random 24-char alphanumeric. Avoids =/+ which trip shell-quoting in
# downstream configs that interpolate the value into a URL or HOCON
# string. 24 chars of base64 ≈ 144 bits of entropy.
_sec_random_password() {
    if command -v openssl >/dev/null 2>&1; then
        openssl rand -base64 48 | tr -d '=+/\n' | head -c 24
    else
        head -c 48 /dev/urandom | base64 | tr -d '=+/\n' | head -c 24
    fi
}

# 32-byte raw random, base64-encoded and newline-stripped. Used for
# crypto material (vault42 master-key, hmac-secret, signing-key) where
# the full 256 bits of entropy matter more than pretty printable form.
# =+/ characters are preserved because vault42 reads these via the
# _FILE convention (raw bytes into the signer/HMAC functions) and
# stripping changes the decoded length.
_sec_random_crypto_key() {
    if command -v openssl >/dev/null 2>&1; then
        openssl rand -base64 32 | tr -d '\n'
    else
        head -c 32 /dev/urandom | base64 | tr -d '\n'
    fi
}

# RSA 2048 private key in PEM form. Used for vault42's JWT signing key:
# vault42 expects a PEM-encoded RSA key (LoadSigningKeyPEM), not raw bytes.
# Without this the signing-key file is invalid and vault42 either crashes
# (env var pointed at a real file path) or falls back to an ephemeral
# in-memory key (env var name typo'd) — both kill sessions on restart.
_sec_random_rsa_pem() {
    if ! command -v openssl >/dev/null 2>&1; then
        _sec_warn "openssl required to generate RSA signing key" >&2
        return 1
    fi
    openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 2>/dev/null
}

_sec_current_value() {
    # Returns the current value of <secret>.<key> if the Secret exists
    # AND the key is present, else empty string. base64 decoded.
    local secret="$1" key="$2"
    ${KUBECTL:-kubectl} -n "${HERMOD_NAMESPACE:-hermod-prod}" get secret "$secret" -o jsonpath="{.data.$key}" 2>/dev/null \
        | base64 -d 2>/dev/null \
        || true
}

# _sec_resolve <mode> <kind> <default> [env_var_name]
#   kind ∈ user | pass | crypto
#   When mode=from-env and env_var_name is set + non-empty, that value
#   wins. Otherwise falls back per the listed mode.
#   Prints the chosen value on stdout.
_sec_resolve() {
    local mode="$1" kind="$2" default="$3" env_name="${4:-}"

    if [[ "$mode" == "from-env" ]]; then
        if [[ -n "$env_name" ]]; then
            local env_val="${!env_name:-}"
            if [[ -n "$env_val" ]]; then
                printf '%s' "$env_val"
                return
            fi
        fi
        _sec_warn "from-env: \$${env_name:-?} unset; using default '$default' (set this in hermod-prod.env to override)"
        printf '%s' "$default"
        return
    fi

    case "$mode" in
        defaults|keep)
            # `keep` mode means "never overwrite an existing value"; the
            # caller already short-circuited keys whose Secret value is
            # already populated, so we only land here for missing keys.
            # In that case the supplied default (a fresh random for
            # password slots, or a documented fallback for usernames)
            # IS what we want to write.
            printf '%s' "$default"
            ;;
        auto)
            case "$kind" in
                pass)   _sec_random_password ;;
                crypto) _sec_random_crypto_key ;;
                *)      printf '%s' "$default" ;;
            esac
            ;;
        interactive)
            local prompt choice custom
            case "$kind" in
                pass|crypto)
                    prompt="  [A]uto-generate / [D]efault ($default) / [C]ustom: "
                    ;;
                *)
                    prompt="  [D]efault ($default) / [C]ustom: "
                    ;;
            esac
            while true; do
                read -r -p "$prompt" choice
                case "${choice:-D}" in
                    A|a)
                        case "$kind" in
                            pass)   _sec_random_password; return ;;
                            crypto) _sec_random_crypto_key; return ;;
                            *)      _sec_warn "auto-generate not supported for user fields" ;;
                        esac
                        ;;
                    D|d|"")
                        printf '%s' "$default"
                        return
                        ;;
                    C|c)
                        read -r -p "    value: " custom
                        if [[ -z "$custom" ]]; then
                            _sec_warn "empty value; try again"
                            continue
                        fi
                        printf '%s' "$custom"
                        return
                        ;;
                    *)
                        _sec_warn "pick A, D, or C"
                        ;;
                esac
            done
            ;;
        *)
            _sec_warn "unknown HERMOD_SECRETS_MODE='$mode'; treating as defaults"
            printf '%s' "$default"
            ;;
    esac
}

# _sec_apply <secret> <key1=value1> [<key2=value2>...]
#   Upserts the Secret with the given key=value pairs. Preserves any
#   other keys already present on the Secret.
_sec_apply() {
    local secret="$1"; shift
    local -a from_literal=()
    local arg
    for arg in "$@"; do from_literal+=(--from-literal="$arg"); done

    # --dry-run=client -o yaml | kubectl apply -f - is the canonical
    # upsert idiom. Merges with any existing Secret's other keys.
    ${KUBECTL:-kubectl} -n "${HERMOD_NAMESPACE:-hermod-prod}" create secret generic "$secret" "${from_literal[@]}" \
        --dry-run=client -o yaml | ${KUBECTL:-kubectl} apply -f - >/dev/null
}

# ── public entry point ────────────────────────────────────────────────

ensure_secrets() {
    local mode="${HERMOD_SECRETS_MODE:-}"
    if [[ -z "$mode" ]]; then
        if [[ -t 0 ]]; then mode="interactive"; else mode="keep"; fi
    fi

    # If a local seed-users.json was pushed (Pi flow) or sourced (kind
    # flow), pull its content into HERMOD_USERS_SEED_JSON. This is the
    # single env channel the apply step keys on; the file path variant
    # is just a transport detail to bridge the local-to-Pi boundary
    # without command-line escape hell.
    if [[ -z "${HERMOD_USERS_SEED_JSON:-}" && -n "${HERMOD_USERS_SEED_JSON_FILE:-}" ]]; then
        if [[ -f "$HERMOD_USERS_SEED_JSON_FILE" ]]; then
            HERMOD_USERS_SEED_JSON="$(cat "$HERMOD_USERS_SEED_JSON_FILE")"
            _sec_log "vault42 seed: loaded $(printf '%s' "$HERMOD_USERS_SEED_JSON" | jq -r '.users | length' 2>/dev/null || echo '?') users from $HERMOD_USERS_SEED_JSON_FILE"
        else
            _sec_warn "HERMOD_USERS_SEED_JSON_FILE=$HERMOD_USERS_SEED_JSON_FILE not found; falling back to historic 3-account seed"
        fi
    fi

    _sec_log "mode: $mode"

    # Collect values once per logical group so services that share a
    # credential get the same value.
    local mqtt_user mqtt_pass
    local nanomq_admin_user nanomq_admin_pass
    local pg_hermod_pass

    local maybe

    # ── MQTT service credential (shared by Coordinator + broker) ─────
    maybe="$(_sec_current_value hermod-secrets mqtt-username)"
    if [[ "$mode" == "keep" && -n "$maybe" && "$maybe" != "hermod-service" ]]; then
        mqtt_user="$maybe"
    else
        [[ "$mode" == "interactive" ]] && _sec_log "MQTT service username (for hermod-secrets + nanomq-credentials):"
        mqtt_user="$(_sec_resolve "$mode" user "hermod-service" HERMOD_MQTT_USERNAME)"
    fi

    maybe="$(_sec_current_value hermod-secrets mqtt-password)"
    if [[ "$mode" == "keep" && -n "$maybe" ]]; then
        mqtt_pass="$maybe"
    else
        [[ "$mode" == "interactive" ]] && _sec_log "MQTT service password:"
        mqtt_pass="$(_sec_resolve "$mode" pass "$(_sec_random_password)" HERMOD_MQTT_PASSWORD)"
    fi

    # ── NanoMQ HTTP admin (broker management API on :8081) ───────────
    maybe="$(_sec_current_value nanomq-http-admin http-user)"
    if [[ "$mode" == "keep" && -n "$maybe" && "$maybe" != "admin" ]]; then
        nanomq_admin_user="$maybe"
    else
        [[ "$mode" == "interactive" ]] && _sec_log "NanoMQ HTTP admin username:"
        nanomq_admin_user="$(_sec_resolve "$mode" user "admin" HERMOD_NANOMQ_ADMIN_USERNAME)"
    fi

    maybe="$(_sec_current_value nanomq-http-admin http-password)"
    if [[ "$mode" == "keep" && -n "$maybe" ]]; then
        nanomq_admin_pass="$maybe"
    else
        [[ "$mode" == "interactive" ]] && _sec_log "NanoMQ HTTP admin password:"
        nanomq_admin_pass="$(_sec_resolve "$mode" pass "$(_sec_random_password)" HERMOD_NANOMQ_ADMIN_PASSWORD)"
    fi

    # ── Postgres (hermod app DB) ─────────────────────────────────────
    maybe="$(_sec_current_value hermod-db-credentials hermod-db-password)"
    if [[ "$mode" == "keep" && -n "$maybe" ]]; then
        pg_hermod_pass="$maybe"
    else
        [[ "$mode" == "interactive" ]] && _sec_log "PostgreSQL password for the hermod app DB user:"
        pg_hermod_pass="$(_sec_resolve "$mode" pass "$(_sec_random_password)" HERMOD_PG_PASSWORD)"
    fi

    # ── Vault42 crypto material + DB role passwords ──────────────────
    # Five keys total in vault42-secrets:
    #   master-key, hmac-secret, signing-key   — 256-bit crypto material
    #   db-mig-password                         — postgres master + vault_mig
    #   db-app-password                         — vault_app
    # The three crypto keys use the full 32-byte random generator
    # rather than the 24-char password generator — a weak signing key
    # would be a JWT-forgery vector.
    local vault42_master_key vault42_hmac vault42_signing
    local vault42_db_mig vault42_db_app

    maybe="$(_sec_current_value vault42-secrets master-key)"
    if [[ "$mode" == "keep" && -n "$maybe" ]]; then
        vault42_master_key="$maybe"
    else
        [[ "$mode" == "interactive" ]] && _sec_log "Vault42 master encryption key:"
        vault42_master_key="$(_sec_resolve "$mode" crypto "$(_sec_random_crypto_key)" HERMOD_VAULT42_MASTER_KEY)"
    fi

    maybe="$(_sec_current_value vault42-secrets hmac-secret)"
    if [[ "$mode" == "keep" && -n "$maybe" ]]; then
        vault42_hmac="$maybe"
    else
        [[ "$mode" == "interactive" ]] && _sec_log "Vault42 HMAC secret:"
        vault42_hmac="$(_sec_resolve "$mode" crypto "$(_sec_random_crypto_key)" HERMOD_VAULT42_HMAC_SECRET)"
    fi

    maybe="$(_sec_current_value vault42-secrets signing-key)"
    # Signing key is persistent crypto: rotating it invalidates every
    # outstanding JWT. Keep an existing valid PEM regardless of mode;
    # regenerate only when missing or not a PEM private key.
    if [[ -n "$maybe" && "$maybe" == *"-----BEGIN"*"PRIVATE KEY-----"* ]]; then
        vault42_signing="$maybe"
    elif [[ -n "${HERMOD_VAULT42_SIGNING_KEY:-}" ]]; then
        vault42_signing="$HERMOD_VAULT42_SIGNING_KEY"
    else
        [[ "$mode" == "interactive" ]] && _sec_log "Vault42 JWT signing key (generating fresh RSA 2048 PEM):"
        vault42_signing="$(_sec_random_rsa_pem)"
    fi

    maybe="$(_sec_current_value vault42-secrets db-mig-password)"
    if [[ "$mode" == "keep" && -n "$maybe" ]]; then
        vault42_db_mig="$maybe"
    else
        [[ "$mode" == "interactive" ]] && _sec_log "Vault42 DB migration-role (vault_mig) password — this is also the postgres master password:"
        vault42_db_mig="$(_sec_resolve "$mode" pass "$(_sec_random_password)" HERMOD_VAULT42_DB_MIG_PASSWORD)"
    fi

    maybe="$(_sec_current_value vault42-secrets db-app-password)"
    if [[ "$mode" == "keep" && -n "$maybe" ]]; then
        vault42_db_app="$maybe"
    else
        [[ "$mode" == "interactive" ]] && _sec_log "Vault42 DB runtime-role (vault_app) password:"
        vault42_db_app="$(_sec_resolve "$mode" pass "$(_sec_random_password)" HERMOD_VAULT42_DB_APP_PASSWORD)"
    fi

    # ── Vault42 first-login seed (user@hermod.local) ─────────────────
    local vault42_user_pass
    maybe="$(_sec_current_value vault42-seed-credentials user-password)"
    if [[ "$mode" == "keep" && -n "$maybe" ]]; then
        vault42_user_pass="$maybe"
    else
        [[ "$mode" == "interactive" ]] && _sec_log "Vault42 seed password for user@hermod.local (used only on first boot):"
        vault42_user_pass="$(_sec_resolve "$mode" pass "$(_sec_random_password)" HERMOD_VAULT42_USER_PASSWORD)"
    fi

    # ── Per-translator MQTT users (prod-pi nanomq ACL chain) ─────────
    # Each translator authenticates to nanomq with its own user, mapped
    # to its topic subtree by templates/nanomq_acl.conf.tmpl. Sharing
    # the bridge mosquitto -> nanomq republish under user
    # `wifi2mqtt-bridge` keeps wifi traffic isolated from coord-side
    # subscribers.
    local mqtt_zigbee_pass mqtt_lora_pass mqtt_wifi_bridge_pass mqtt_ble_pass
    maybe="$(_sec_current_value hermod-mqtt-users zigbee2mqtt-password)"
    if [[ "$mode" == "keep" && -n "$maybe" ]]; then
        mqtt_zigbee_pass="$maybe"
    else
        [[ "$mode" == "interactive" ]] && _sec_log "MQTT password for zigbee2mqtt:"
        mqtt_zigbee_pass="$(_sec_resolve "$mode" pass "$(_sec_random_password)" HERMOD_MQTT_ZIGBEE_PASSWORD)"
    fi
    maybe="$(_sec_current_value hermod-mqtt-users lora2mqtt-password)"
    if [[ "$mode" == "keep" && -n "$maybe" ]]; then
        mqtt_lora_pass="$maybe"
    else
        [[ "$mode" == "interactive" ]] && _sec_log "MQTT password for lora2mqtt:"
        mqtt_lora_pass="$(_sec_resolve "$mode" pass "$(_sec_random_password)" HERMOD_MQTT_LORA_PASSWORD)"
    fi
    maybe="$(_sec_current_value hermod-mqtt-users wifi2mqtt-bridge-password)"
    if [[ "$mode" == "keep" && -n "$maybe" ]]; then
        mqtt_wifi_bridge_pass="$maybe"
    else
        [[ "$mode" == "interactive" ]] && _sec_log "MQTT password for wifi2mqtt-bridge:"
        mqtt_wifi_bridge_pass="$(_sec_resolve "$mode" pass "$(_sec_random_password)" HERMOD_MQTT_WIFI_BRIDGE_PASSWORD)"
    fi
    maybe="$(_sec_current_value hermod-mqtt-users ble2mqtt-password)"
    if [[ "$mode" == "keep" && -n "$maybe" ]]; then
        mqtt_ble_pass="$maybe"
    else
        [[ "$mode" == "interactive" ]] && _sec_log "MQTT password for ble2mqtt (Theengs gateway):"
        mqtt_ble_pass="$(_sec_resolve "$mode" pass "$(_sec_random_password)" HERMOD_MQTT_BLE_PASSWORD)"
    fi

    # ── viewer + operator role accounts ─────────────────────────────
    # Each role-tier seed account gets its own password slot. The
    # operator account is always seeded (the system needs at least one
    # full-control identity); viewer + user are optional and can be
    # disabled by setting HERMOD_SEED_VIEWER=0 / HERMOD_SEED_USER=0
    # before sourcing this script. Disabled accounts are signalled to
    # the render-seed init container by writing the literal sentinel
    # "DISABLED" into the password slot — render-seed drops any account
    # whose password matches that sentinel before vault42 reads the
    # seed.
    local vault42_viewer_pass vault42_operator_pass
    if [[ "${HERMOD_SEED_VIEWER:-1}" == "0" || "${HERMOD_SEED_VIEWER:-1}" == "false" ]]; then
        vault42_viewer_pass="DISABLED"
    else
        maybe="$(_sec_current_value vault42-seed-credentials viewer-password)"
        if [[ "$mode" == "keep" && -n "$maybe" && "$maybe" != "DISABLED" ]]; then
            vault42_viewer_pass="$maybe"
        else
            vault42_viewer_pass="$(_sec_resolve "$mode" pass "$(_sec_random_password)" HERMOD_VAULT42_VIEWER_PASSWORD)"
        fi
    fi
    if [[ "${HERMOD_SEED_USER:-1}" == "0" || "${HERMOD_SEED_USER:-1}" == "false" ]]; then
        vault42_user_pass="DISABLED"
    fi
    maybe="$(_sec_current_value vault42-seed-credentials operator-password)"
    if [[ "$mode" == "keep" && -n "$maybe" ]]; then
        vault42_operator_pass="$maybe"
    else
        vault42_operator_pass="$(_sec_resolve "$mode" pass "$(_sec_random_password)" HERMOD_VAULT42_OPERATOR_PASSWORD)"
    fi

    # ── apply ────────────────────────────────────────────────────────
    _sec_log "applying Secrets"
    _sec_apply hermod-secrets \
        "mqtt-username=$mqtt_user" \
        "mqtt-password=$mqtt_pass"
    _sec_apply nanomq-credentials \
        "username=$mqtt_user" \
        "password=$mqtt_pass"
    _sec_apply nanomq-http-admin \
        "http-user=$nanomq_admin_user" \
        "http-password=$nanomq_admin_pass"
    _sec_apply hermod-db-credentials \
        "hermod-db-password=$pg_hermod_pass"
    _sec_apply vault42-secrets \
        "master-key=$vault42_master_key" \
        "hmac-secret=$vault42_hmac" \
        "signing-key=$vault42_signing" \
        "db-mig-password=$vault42_db_mig" \
        "db-app-password=$vault42_db_app"
    # Vault42 seed Secret. Two paths:
    #   1. Operator has run `hermod.sh users init` (and optionally added
    #      more users) → HERMOD_USERS_SEED_JSON env carries the dumped
    #      JSON; we write it into a dedicated `seed-json` key. The
    #      render-seed init container in vault42 prefers seed-json over
    #      the template-substitution path, so arbitrary user rosters work.
    #   2. No local seed file → fall back to the historic 3-account
    #      template path: viewer/user/operator passwords landed under
    #      the named keys, render-seed substitutes placeholders.
    # Both paths populate the same Secret name; render-seed picks at
    # boot. Empty `seed-json` (the post-rollout cleanup state) tells
    # render-seed to use the template path with whatever passwords are
    # already present.
    if [[ -n "${HERMOD_USERS_SEED_JSON:-}" ]]; then
        _sec_apply vault42-seed-credentials \
            "user-password=$vault42_user_pass" \
            "viewer-password=$vault42_viewer_pass" \
            "operator-password=$vault42_operator_pass" \
            "seed-json=$HERMOD_USERS_SEED_JSON"
        _sec_log "vault42 seed: pushed seed-json from local seed-users.json"
    else
        _sec_apply vault42-seed-credentials \
            "user-password=$vault42_user_pass" \
            "viewer-password=$vault42_viewer_pass" \
            "operator-password=$vault42_operator_pass" \
            "seed-json="
    fi
    # Per-translator MQTT users — consumed by nanomq-acl-init renderer
    # (kubernetes/overlays/prod/nanomq-acl-init.yaml). Coord still
    # connects with mqtt_user/mqtt_pass from hermod-secrets above; the
    # ACL maps that username to the coord topic patterns.
    _sec_apply hermod-mqtt-users \
        "coord-username=$mqtt_user" \
        "coord-password=$mqtt_pass" \
        "zigbee2mqtt-username=zigbee2mqtt" \
        "zigbee2mqtt-password=$mqtt_zigbee_pass" \
        "lora2mqtt-username=lora2mqtt" \
        "lora2mqtt-password=$mqtt_lora_pass" \
        "wifi2mqtt-bridge-username=wifi2mqtt-bridge" \
        "wifi2mqtt-bridge-password=$mqtt_wifi_bridge_pass" \
        "ble2mqtt-username=ble2mqtt" \
        "ble2mqtt-password=$mqtt_ble_pass"

    # Export the seed values so the caller (deploy-kind.sh) can print
    # them in its summary — a user running a fresh deploy needs to know
    # how to log in for the first time, and the seed gets deleted once
    # vault42 imports it so we can't show it later.
    export HERMOD_VAULT42_USER_PASSWORD="$vault42_user_pass"
    export HERMOD_VAULT42_VIEWER_PASSWORD="$vault42_viewer_pass"
    export HERMOD_VAULT42_OPERATOR_PASSWORD="$vault42_operator_pass"

    _sec_log "ok"
}
