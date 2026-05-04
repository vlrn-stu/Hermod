#!/usr/bin/env bash
# lib.sh — shared primitives for hermod.sh and friends.
#
# Sourced from hermod.sh after $REPO_ROOT is set. Provides:
#   - OS detection: $OS, detect_os
#   - colour helpers: _c, _log, _ok, _warn, _die
#   - probes:        _have, _hr
#   - env vault interop: _env_present, _env_resolve_to_plaintext,
#                        _env_resolve_cleanup
#
# Idempotent: re-sourcing is safe.

[[ -n "${_HERMOD_LIB_LOADED:-}" ]] && return 0
_HERMOD_LIB_LOADED=1

# ── platform detection ──────────────────────────────────────────────────────
detect_os() {
    case "$(uname -s 2>/dev/null)" in
        Linux*)
            if grep -qi microsoft /proc/version 2>/dev/null; then echo wsl
            else echo linux
            fi
            ;;
        Darwin*)              echo macos ;;
        MINGW*|MSYS*|CYGWIN*) echo windows ;;
        *)                    echo unknown ;;
    esac
}
: "${OS:=$(detect_os)}"

# ── colour ──────────────────────────────────────────────────────────────────
# Honour NO_COLOR (https://no-color.org); skip colour for non-TTY stdout.
: "${USE_COLOR:=1}"
[[ -t 1 ]] || USE_COLOR=0
[[ -n "${NO_COLOR:-}" ]] && USE_COLOR=0

_c() {
    if [[ $USE_COLOR -eq 1 ]]; then printf '\033[%sm%s\033[0m' "$1" "$2"
    else printf '%s' "$2"
    fi
}
_log()  { printf '%s %s\n' "$(_c '0;36' '[hermod]')"     "$*" >&2; }
_ok()   { printf '%s %s\n' "$(_c '0;32' '[hermod OK]')"  "$*" >&2; }
_warn() { printf '%s %s\n' "$(_c '0;33' '[hermod !!]')"  "$*" >&2; }
_die()  { printf '%s %s\n' "$(_c '0;31' '[hermod ERR]')" "$*" >&2; exit 1; }
_have() { command -v "$1" >/dev/null 2>&1; }
_hr()   { printf '%.s=' $(seq 1 "${1:-58}"); printf '\n'; }

# ── compose impl detection ──────────────────────────────────────────────────
# Different hosts ship different compose CLIs:
#   - Docker Desktop / docker-ce w/ plugin   → `docker compose <verb>`
#   - Older or distro-packaged installs      → `docker-compose <verb>`
#   - Neither                                → no compose path
# _compose_cmd echoes the working command (one or two words) and returns 0,
# or returns 1 with no output when no compose is reachable. Wrap with $(...)
# at the call site so word-splitting stays correct.
_HERMOD_COMPOSE_CMD_CACHE=""
_compose_cmd() {
    if [[ -n "$_HERMOD_COMPOSE_CMD_CACHE" ]]; then
        printf '%s' "$_HERMOD_COMPOSE_CMD_CACHE"; return 0
    fi
    if _have docker && docker compose version >/dev/null 2>&1; then
        _HERMOD_COMPOSE_CMD_CACHE="docker compose"
    elif _have docker-compose; then
        _HERMOD_COMPOSE_CMD_CACHE="docker-compose"
    else
        return 1
    fi
    printf '%s' "$_HERMOD_COMPOSE_CMD_CACHE"
}

# Pass/fail glyphs used by the doctor and TUI status surfaces. ASCII-bracketed
# so terminals without proper Unicode support (or font fallbacks that render
# ✓/✗/! as boxes) still show readable markers. Match the [+]/[X]/[!]
# convention from the TUI status row.
_doctor_pass()  { _c '0;32' '[+]'; }
_doctor_fail()  { _c '0;31' '[X]'; }
_doctor_warn()  { _c '0;33' '[!]'; }

# ── env vault interop ───────────────────────────────────────────────────────
# True when either plaintext or `<file>.mimir` exists. Use everywhere
# the legacy code wrote `[[ -f "$env_file" ]]` so the operator can
# choose either encryption mode without breaking the deploy path.
_env_present() {
    [[ -f "$1" || -f "$1.mimir" ]]
}

# Resolve <file> to a plaintext path on disk. Plaintext: returns the
# original path. Encrypted: decrypts to a 0600 tempfile and returns
# that path; caller must clean it up via _env_resolve_cleanup.
_HERMOD_RESOLVED_TMP=""
_env_resolve_to_plaintext() {
    local f="$1"
    if [[ -f "$f" && ! -f "$f.mimir" ]]; then
        printf '%s' "$f"; return 0
    fi
    if [[ -f "$f.mimir" ]]; then
        [[ "${_HERMOD_MIMIR_LOADED:-}" == "1" ]] || \
            # shellcheck disable=SC1091
            source "$REPO_ROOT/lib/mimir.sh"
        local tmp; tmp="$(mktemp)" || return 1
        chmod 0600 "$tmp"
        if mimir_load "$f" >"$tmp" 2>/dev/null; then
            _HERMOD_RESOLVED_TMP="$tmp"
            printf '%s' "$tmp"; return 0
        fi
        rm -f "$tmp"
    fi
    return 1
}

_env_resolve_cleanup() {
    [[ -n "$_HERMOD_RESOLVED_TMP" && -f "$_HERMOD_RESOLVED_TMP" ]] && rm -f "$_HERMOD_RESOLVED_TMP"
    _HERMOD_RESOLVED_TMP=""
}
