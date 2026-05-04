#!/usr/bin/env bash
# mimir.sh — optional PIN-protected file vault for Hermod.
#
# Wraps `gpg --symmetric` (OpenPGP) so the trust model is the same one
# every Linux/macOS dev machine already ships. Source this file; it
# provides the `mimir_*` API used by hermod.sh and the TUI when (and
# only when) the operator chooses to encrypt their hermod-prod.env.
#
# Mimir is OPTIONAL. With no `.mimir` file present, hermod.sh and the
# TUI run unchanged against the plaintext env file. Encryption is a
# turn-on; mimir_load transparently handles either case.
#
# Storage layout:
#   <file>                        — plaintext (only when Mimir not used)
#   <file>.mimir                  — gpg --symmetric AES-256 ciphertext
#   <file>.mimir.meta             — sidecar JSON: { version, pin_required }
#   $cache_dir/<sha1>.unlocked    — decrypted plaintext, mode 0600;
#                                   mtime touched on every read for the
#                                   idle-TTL refresh
#
# Cache directory selection:
#   1. /run/user/$UID/hermod      — Linux/WSL tmpfs (preferred; vanishes on logout)
#   2. ${XDG_CACHE_HOME:-~/.cache}/hermod/session — macOS / fallback
#
# Crypto: gpg --symmetric --cipher-algo AES256 --s2k-mode 3 with the
# operator-tunable s2k-count. The OpenPGP wire format is documented in
# RFC 4880; gpg-agent isn't used (passphrase-fd, no caching outside this
# session) so the cache TTL we manage here is the only freshness window.
# Empty PIN is allowed: pin_required=false in meta, gpg still encrypts
# with the empty string as passphrase. That's security theatre but it
# keeps the file format uniform — `mimir rekey` later adds a real PIN.
#
# All chatter goes to stderr; only data goes to stdout (mimir_load
# prints decrypted contents).

[[ -n "${_HERMOD_MIMIR_LOADED:-}" ]] && return 0
_HERMOD_MIMIR_LOADED=1

# ── tunables ──────────────────────────────────────────────────────────
: "${HERMOD_MIMIR_TTL:=600}"           # idle seconds before re-prompt
: "${HERMOD_MIMIR_S2K_COUNT:=65011712}" # gpg s2k iteration count (max-ish)
: "${HERMOD_MIMIR_QUIET:=0}"

# ── logging (stderr) ──────────────────────────────────────────────────
_mimir_log()  { [[ "$HERMOD_MIMIR_QUIET" == "1" ]] && return 0; printf '\033[1;34m[mimir]\033[0m %s\n' "$*" >&2; }
_mimir_warn() { printf '\033[1;33m[mimir]\033[0m %s\n' "$*" >&2; }
_mimir_die()  { printf '\033[1;31m[mimir]\033[0m %s\n' "$*" >&2; return 1; }

# ── tool detection ────────────────────────────────────────────────────
_mimir_gpg() {
    if command -v gpg2 >/dev/null 2>&1; then printf 'gpg2'; return 0; fi
    if command -v gpg  >/dev/null 2>&1; then printf 'gpg';  return 0; fi
    return 1
}

# ── cache directory ───────────────────────────────────────────────────
_mimir_cache_dir() {
    local d
    if [[ -n "${HERMOD_MIMIR_CACHE_DIR:-}" ]]; then
        d="$HERMOD_MIMIR_CACHE_DIR"
    elif [[ -d "/run/user/${UID:-$(id -u)}" ]]; then
        d="/run/user/${UID:-$(id -u)}/hermod"
    else
        d="${XDG_CACHE_HOME:-$HOME/.cache}/hermod/session"
    fi
    mkdir -p "$d" 2>/dev/null || return 1
    chmod 0700 "$d" 2>/dev/null || true
    printf '%s' "$d"
}

_mimir_sha1() {
    if command -v sha1sum >/dev/null 2>&1; then
        printf '%s' "$1" | sha1sum | awk '{print $1}'
    elif command -v shasum >/dev/null 2>&1; then
        printf '%s' "$1" | shasum -a 1 | awk '{print $1}'
    else
        printf '%s' "$1" | cksum | awk '{print $1}'
    fi
}

_mimir_cache_path() {
    local file="$1"
    local abs
    abs="$(cd "$(dirname "$file")" 2>/dev/null && printf '%s/%s' "$PWD" "$(basename "$file")")" || abs="$file"
    local hash; hash="$(_mimir_sha1 "$abs")"
    printf '%s/%s.unlocked' "$(_mimir_cache_dir)" "$hash"
}

# ── shred helper (cross-platform best-effort) ─────────────────────────
# Used for tmpfs caches AND for the operator's plaintext original after
# encryption. Earlier versions copied the plaintext to a backup directory,
# which defeated the encryption (a plaintext copy on disk is worse than
# no PIN at all). Operators who want a one-shot archive before init must
# do it themselves.
_mimir_shred() {
    local f="$1"
    [[ -f "$f" ]] || return 0
    if command -v shred >/dev/null 2>&1; then
        shred -uz "$f" 2>/dev/null && return 0
    fi
    if rm -P "$f" 2>/dev/null; then return 0; fi
    : > "$f" 2>/dev/null || true
    rm -f "$f"
}

# Plaintext archival was removed: keeping a 0600 plaintext copy of the
# very file we just encrypted defeats the encryption (anyone who can
# read the cache can read the archive). Operators who want a backup
# must `cp` the plaintext to safe storage themselves before invoking
# `mimir init`. The post-init plaintext is shredded in-place.

# Refuse to encrypt obviously-dangerous targets that the operator is
# more likely to lock themselves out of than to want encrypted: SSH
# private keys (OpenSSH header) and Hermod's pi-key dir. Errs on the
# side of NOT encrypting; the operator can override with
# HERMOD_MIMIR_FORCE=1 if they really mean it.
_mimir_assert_safe_target() {
    local file="$1"
    [[ "${HERMOD_MIMIR_FORCE:-0}" == "1" ]] && return 0
    case "$(realpath "$file" 2>/dev/null || printf '%s' "$file")" in
        */.hermod-pi/keys/*|*/.ssh/id_*)
            _mimir_die "refusing to encrypt SSH private key $file (set HERMOD_MIMIR_FORCE=1 to override)"
            return 1 ;;
    esac
    if head -c 64 "$file" 2>/dev/null | grep -qE 'BEGIN (OPENSSH|RSA|DSA|EC|ENCRYPTED) PRIVATE KEY'; then
        _mimir_die "refusing to encrypt SSH private key contents in $file (set HERMOD_MIMIR_FORCE=1 to override)"
        return 1
    fi
    return 0
}

# ── meta (sidecar JSON) ───────────────────────────────────────────────
_mimir_meta_path() { printf '%s.meta' "$1.mimir"; }

_mimir_meta_write() {
    local file="$1" pin_required="$2"
    local meta; meta="$(_mimir_meta_path "$file")"
    cat > "$meta" <<EOF
{
  "version": 2,
  "pin_required": $pin_required,
  "cipher": "openpgp-aes256",
  "s2k_count": $HERMOD_MIMIR_S2K_COUNT
}
EOF
    chmod 0644 "$meta"
}

_mimir_meta_get() {
    local file="$1" key="$2"
    local meta; meta="$(_mimir_meta_path "$file")"
    [[ -f "$meta" ]] || return 1
    awk -v k="\"$key\"" '
        $0 ~ k {
            sub(/^[^:]*:[ \t]*/, "")
            sub(/[,}\r\n]+$/, "")
            sub(/^"/, ""); sub(/"$/, "")
            print
            exit
        }
    ' "$meta"
}

# gpg 2.x rejects an empty passphrase outright, so when the operator
# chose no-PIN we feed a fixed sentinel under the hood. The meta file
# records pin_required=false; load-time skips the prompt and reuses
# this sentinel. It is genuinely no security — that's the contract — but
# it keeps the wire format uniform so a later `mimir rekey` swaps in a
# real PIN without touching anything else.
_MIMIR_NO_PIN_SENTINEL='hermod:vault:no-pin'

# ── crypto (gpg --symmetric / --decrypt) ──────────────────────────────
# Stdin: passphrase line. Stdout: ciphertext.
_mimir_encrypt() {
    local infile="$1" outfile="$2" gpg_bin
    gpg_bin="$(_mimir_gpg)" || { _mimir_die "gpg not on PATH (install gnupg)"; return 1; }
    "$gpg_bin" --batch --yes --quiet --no-tty \
        --pinentry-mode loopback \
        --symmetric --cipher-algo AES256 \
        --s2k-mode 3 --s2k-count "$HERMOD_MIMIR_S2K_COUNT" \
        --s2k-digest-algo SHA512 \
        --passphrase-fd 0 \
        --output "$outfile" "$infile"
}

_mimir_decrypt() {
    local infile="$1" outfile="$2" gpg_bin
    gpg_bin="$(_mimir_gpg)" || { _mimir_die "gpg not on PATH (install gnupg)"; return 1; }
    "$gpg_bin" --batch --yes --quiet --no-tty \
        --pinentry-mode loopback \
        --decrypt --passphrase-fd 0 \
        --output "$outfile" "$infile"
}

_mimir_effective_pin() {
    if [[ -z "$1" ]]; then
        printf '%s' "$_MIMIR_NO_PIN_SENTINEL"
    else
        printf '%s' "$1"
    fi
}

# ── prompt helpers (silent) ───────────────────────────────────────────
_mimir_prompt_pin() {
    local prompt="$1" pin
    if ! IFS= read -rs -p "$prompt" pin </dev/tty; then
        printf '\n' >&2; return 1
    fi
    printf '\n' >&2
    printf '%s' "$pin"
}

_mimir_prompt_pin_twice() {
    local p1 p2
    p1="$(_mimir_prompt_pin 'Vault PIN (Enter for no-PIN): ')" || return 1
    p2="$(_mimir_prompt_pin 'Confirm:                     ')" || return 1
    if [[ "$p1" != "$p2" ]]; then
        _mimir_warn "PINs did not match"; return 1
    fi
    printf '%s' "$p1"
}

# ── public API ────────────────────────────────────────────────────────

# mimir_init <file>
#   Encrypt <file> in place: writes <file>.mimir, <file>.mimir.meta,
#   shreds the original. PIN sourced from HERMOD_MIMIR_PIN_NEW or a
#   double tty prompt. Empty PIN allowed.
mimir_init() {
    local file="$1"
    [[ -f "$file" ]] || { _mimir_die "no such file: $file"; return 1; }
    if [[ -f "$file.mimir" ]]; then
        _mimir_warn "$file.mimir already exists; use 'mimir_rekey' to change PIN"
        return 1
    fi
    _mimir_gpg >/dev/null || { _mimir_die "gpg not on PATH (install gnupg)"; return 1; }
    _mimir_assert_safe_target "$file" || return 1

    local pin
    if [[ -n "${HERMOD_MIMIR_PIN_NEW+x}" ]]; then
        pin="$HERMOD_MIMIR_PIN_NEW"
    else
        pin="$(_mimir_prompt_pin_twice)" || return 1
    fi

    local pin_required="false"
    [[ -n "$pin" ]] && pin_required="true"

    if ! printf '%s\n' "$(_mimir_effective_pin "$pin")" | _mimir_encrypt "$file" "$file.mimir"; then
        _mimir_die "gpg --symmetric failed"; return 1
    fi
    chmod 0600 "$file.mimir"
    _mimir_meta_write "$file" "$pin_required"
    # Shred the plaintext rather than archiving it. A plaintext copy on
    # disk after a deliberate `init` is worse-than-no-encryption theatre:
    # any operator who wants a backup must take it themselves before
    # running init.
    _mimir_shred "$file"

    if [[ "$pin_required" == "true" ]]; then
        _mimir_log "encrypted $file → $file.mimir (PIN required); plaintext shredded"
    else
        _mimir_log "encrypted $file → $file.mimir (no PIN — run mimir_rekey to add one); plaintext shredded"
    fi
}

# mimir_load <file> [--source]
#   When <file> exists in plaintext and no .mimir sibling: pass through.
#   When <file>.mimir exists: decrypt to the session cache (or reuse if
#   warm), then echo or `source -a` into the caller's shell.
mimir_load() {
    local file="$1"; shift || true
    local mode="cat"
    [[ "${1:-}" == "--source" ]] && mode="source"

    if [[ -f "$file" && ! -f "$file.mimir" ]]; then
        if [[ "$mode" == "source" ]]; then
            # shellcheck disable=SC1090
            set -a; source "$file"; set +a
        else
            cat "$file"
        fi
        return 0
    fi

    [[ -f "$file.mimir" ]] || { _mimir_die "no plaintext or encrypted file at $file"; return 1; }

    local cache; cache="$(_mimir_cache_path "$file")"
    local now; now=$(date +%s)
    if [[ -f "$cache" ]]; then
        local mtime; mtime=$(stat -c %Y "$cache" 2>/dev/null || stat -f %m "$cache" 2>/dev/null || echo 0)
        if (( now - mtime <= HERMOD_MIMIR_TTL )); then
            touch "$cache"
            if [[ "$mode" == "source" ]]; then
                set -a; source "$cache"; set +a
            else
                cat "$cache"
            fi
            return 0
        fi
        _mimir_shred "$cache"
    fi

    local pin_required; pin_required="$(_mimir_meta_get "$file" "pin_required" 2>/dev/null || echo unknown)"
    local pin
    if [[ -n "${HERMOD_MIMIR_PIN+x}" ]]; then
        pin="$HERMOD_MIMIR_PIN"
    elif [[ "$pin_required" == "true" ]]; then
        pin="$(_mimir_prompt_pin "Vault PIN for $(basename "$file"): ")" || return 1
    else
        pin=""
    fi

    if ! printf '%s\n' "$(_mimir_effective_pin "$pin")" | _mimir_decrypt "$file.mimir" "$cache"; then
        _mimir_shred "$cache"
        _mimir_die "decrypt failed (wrong PIN?)"; return 1
    fi
    chmod 0600 "$cache"

    if [[ "$mode" == "source" ]]; then
        set -a; source "$cache"; set +a
    else
        cat "$cache"
    fi
}

# mimir_unlock <file> [--force]
#   Force a fresh prompt + repopulate the cache. With --force, blow
#   away any warm cache first.
mimir_unlock() {
    local file="$1" force="${2:-}"
    if [[ "$force" == "--force" ]]; then
        _mimir_shred "$(_mimir_cache_path "$file")"
    fi
    mimir_load "$file" >/dev/null
}

# mimir_lock [<file>]
#   Shred the cache for <file>, or for every cached file when no arg.
mimir_lock() {
    local file="${1:-}"
    if [[ -n "$file" ]]; then
        _mimir_shred "$(_mimir_cache_path "$file")"
        _mimir_log "locked $file"
        return 0
    fi
    local dir; dir="$(_mimir_cache_dir)"
    local n=0 f
    for f in "$dir"/*.unlocked; do
        [[ -f "$f" ]] || continue
        _mimir_shred "$f"; n=$((n+1))
    done
    _mimir_log "locked $n cached file(s)"
}

# mimir_rekey <file>
#   Decrypt + re-encrypt with a new PIN. Old cache invalidated.
mimir_rekey() {
    local file="$1"
    [[ -f "$file.mimir" ]] || { _mimir_die "not encrypted: $file"; return 1; }
    local plain; plain="$(_mimir_load_to_tempfile "$file")" || return 1

    _mimir_log "set new PIN (Enter for no-PIN)"
    local new_pin
    if [[ -n "${HERMOD_MIMIR_PIN_NEW+x}" ]]; then
        new_pin="$HERMOD_MIMIR_PIN_NEW"
    else
        new_pin="$(_mimir_prompt_pin_twice)" || { _mimir_shred "$plain"; return 1; }
    fi
    local pin_required="false"; [[ -n "$new_pin" ]] && pin_required="true"

    if ! printf '%s\n' "$(_mimir_effective_pin "$new_pin")" | _mimir_encrypt "$plain" "$file.mimir.new"; then
        _mimir_shred "$plain"
        _mimir_die "re-encrypt failed"; return 1
    fi
    mv "$file.mimir.new" "$file.mimir"
    chmod 0600 "$file.mimir"
    _mimir_meta_write "$file" "$pin_required"
    _mimir_shred "$plain"
    _mimir_shred "$(_mimir_cache_path "$file")"
    _mimir_log "rekeyed $file (PIN required: $pin_required)"
}

_mimir_load_to_tempfile() {
    local file="$1"
    local tmp
    tmp="$(mktemp "$(_mimir_cache_dir)/rekey.XXXXXX")" || return 1
    chmod 0600 "$tmp"
    if mimir_load "$file" >"$tmp"; then
        printf '%s' "$tmp"; return 0
    fi
    _mimir_shred "$tmp"; return 1
}

# mimir_status [<file>]
#   Print cache state for <file>, or for every encrypted blob found
#   under REPO_ROOT (max-depth 3 to avoid wandering).
mimir_status() {
    local file="${1:-}"
    if [[ -n "$file" ]]; then
        _mimir_status_one "$file"; return
    fi
    local root="${REPO_ROOT:-$(pwd)}"
    local found=0 m
    while IFS= read -r m; do
        _mimir_status_one "${m%.mimir}"; found=1
    done < <(find "$root" -maxdepth 3 -type f -name '*.mimir' 2>/dev/null)
    [[ "$found" == "0" ]] && _mimir_log "no .mimir files under $root (vault is optional — see 'hermod.sh mimir help')"
}

_mimir_status_one() {
    local file="$1"
    local cache; cache="$(_mimir_cache_path "$file")"
    local pin_required; pin_required="$(_mimir_meta_get "$file" "pin_required" 2>/dev/null || echo unknown)"
    local state="[locked]"
    if [[ -f "$cache" ]]; then
        local mtime; mtime=$(stat -c %Y "$cache" 2>/dev/null || stat -f %m "$cache" 2>/dev/null || echo 0)
        local now; now=$(date +%s)
        local rem=$(( HERMOD_MIMIR_TTL - (now - mtime) ))
        if (( rem > 0 )); then
            state="[unlocked, ${rem}s left]"
        fi
    fi
    printf '  %-40s pin=%-7s %s\n' "$file" "$pin_required" "$state"
}
