#!/usr/bin/env bash
# cmd-compose.sh — `hermod.sh compose <action>` subcommand.
#
# The compose stack exists strictly for mock verification on a dev box.
# It runs everything in one host network with baked-in passwords, no
# real TLS, and AuthBypass on lora2mqtt so the dashboard renders
# without a JWT. Real deploys go through `hermod.sh install prod-pi`.
#
# Sourced by hermod.sh once $REPO_ROOT is set + lib/lib.sh is loaded.

[[ -n "${_HERMOD_CMD_COMPOSE_LOADED:-}" ]] && return 0
_HERMOD_CMD_COMPOSE_LOADED=1

# ── compose binary detection ───────────────────────────────────────────────
# Thin wrapper around the shared _compose_cmd helper in lib/lib.sh so the
# old name keeps working for callers in this file. _compose_cmd caches its
# result; the _die path here keeps the original error message that matched
# the rest of the compose path.
_compose_bin() {
    local c; c="$(_compose_cmd 2>/dev/null)" \
        || _die "no docker compose available — install Docker Desktop or docker-ce + compose plugin"
    printf '%s\n' "$c"
}

# Big yellow banner stating the compose stack is unsupported. Printed
# at the top of every compose subcommand that touches state (up / build
# / restart / reset) so an operator can't lose track of which path they
# are on.
_compose_warn_banner() {
    printf '\033[1;33m'
    cat <<'EOF'

═════════════════════════════════════════════════════════════════
  ⚠  COMPOSE STACK — UNSUPPORTED, MOCK-VERIFICATION ONLY  ⚠
═════════════════════════════════════════════════════════════════
  This path is NOT a production deployment and is NOT secure:
    - Baked-in dev passwords in docker-compose.yaml
    - Self-signed CA regenerated on every `compose reset`
    - LoRa2MQTT runs with Hermod__Security__AuthBypass=true so the
      dashboard works without a real session JWT
    - Coordinator listens HTTP only (no Kestrel TLS)
    - z2m disabled (no USB adapter on the compose host)
    - ble2mqtt runs with `-b 0` (no Bluetooth scan)
  Use it to smoke-test code changes locally. For anything real,
  install on the Pi: hermod.sh install prod-pi
═════════════════════════════════════════════════════════════════
EOF
    printf '\033[0m'
}

# Print the baked-in compose dev credentials. Compose is strictly for
# mock verification, so the seed users + MQTT/admin passwords live in
# lib/compose/vault42-seed.json + docker-compose.yaml defaults.
_compose_print_creds() {
    cat <<'EOF'

─────────────────────────────────────────────────────────────────
  Hermod compose dev credentials  (mock verification only)
─────────────────────────────────────────────────────────────────

  Vault42 seed users — log in at http://localhost:42069/login:
    viewer    viewer@hermod.local    asdfghjklVIEWER123
    user      user@hermod.local      asdfghjklUSER123
    operator  operator@hermod.local  asdfghjklOPER123

  MQTT service credential (translators + bridges):
    user            hermod-service
    pass            change-me-mqtt

  Postgres app password (hermod_app role):
    pass            change-me-hermod-app

  Endpoints:
    Coord dashboard       http://localhost:42069   (compose runs HTTP; prod overlay flips to HTTPS)
    Vault42 (internal)    https://vault42:8443     (container DNS, self-signed cert)
    NanoMQ MQTT           mqtt://localhost:1883
    NanoMQ admin          http://localhost:8081
    NanoMQ websocket      ws://localhost:18083     (host port remapped from 8083)
    Wifi2MQTT bridge      mqtt://localhost:1884    (Tasmota / ESPHome ingress)

EOF
}

cmd_compose() {
    local action="${1:-help}"; shift || true
    if [[ "$action" == "help" || "$action" == "-h" || "$action" == "--help" ]]; then
        cat <<EOF
hermod.sh compose <action> [svc]

  ⚠ UNSUPPORTED, MOCK-VERIFICATION ONLY — not for production. Baked-in
    dev passwords, self-signed CA, AuthBypass on lora2mqtt. Use
    'hermod.sh install prod-pi' for any real deployment.

  up         create + start (builds images first)
  down       stop + remove containers (volumes preserved)
  reset      down + wipe ALL volumes (destroys postgres + nanomq + hermod data)
  restart    restart [service]
  logs       tail logs [service]
  status     ps
  build      rebuild [service]
  pull       refresh images
  creds      print baked-in dev usernames + passwords (also printed on \`up\`)
EOF
        return 0
    fi
    # `creds` only prints baked-in strings; no need for docker.
    if [[ "$action" == "creds" ]]; then
        _compose_print_creds; return 0
    fi
    _have docker || _die "docker not on PATH. Install Docker Desktop (Win/macOS) or docker-ce + compose plugin (Linux)."
    local C; C="$(_compose_bin)"
    case "$action" in
        up)
            _compose_warn_banner
            (cd "$REPO_ROOT" && $C up -d --build)
            printf '\n'
            _ok "Hermod is up. Coordinator: http://localhost:42069"
            _compose_print_creds
            _compose_warn_banner
            ;;
        creds)   _compose_print_creds ;;
        down)    (cd "$REPO_ROOT" && $C down) ;;
        reset)   _warn "destroying ALL compose volumes (postgres + nanomq + hermod data)"
                 (cd "$REPO_ROOT" && $C down -v)
                 _ok "stack stopped + volumes removed; 'compose up' starts a fresh install" ;;
        restart) (cd "$REPO_ROOT" && $C restart "$@") ;;
        logs)    (cd "$REPO_ROOT" && $C logs -f "$@") ;;
        status)  (cd "$REPO_ROOT" && $C ps) ;;
        build)   (cd "$REPO_ROOT" && $C build "$@") ;;
        pull)    (cd "$REPO_ROOT" && $C pull) ;;
        *) _die "compose: unknown action '$action' (try 'hermod.sh compose help')" ;;
    esac
}
