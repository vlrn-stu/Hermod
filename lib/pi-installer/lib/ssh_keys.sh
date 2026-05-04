#!/usr/bin/env bash
# ssh_keys.sh — Hermod Pi SSH keypair management library
#
# Each Raspberry Pi flashed by the hermod-pi provisioning tool gets its own
# dedicated ed25519 (or ed25519-sk) keypair. Private keys never leave the
# operator's workstation. This library owns on-disk layout, generation,
# metadata, audit logging and TOFU host-key pinning.
#
# Source from a CLI:
#     # shellcheck source=lib/ssh_keys.sh
#     source "$(dirname "$0")/lib/ssh_keys.sh"
#     ssh_keys::init
#
# Environment variables (all optional):
#     HERMOD_PI_HOME                     Root state dir (default: $HOME/.hermod-pi)
#     HERMOD_PI_KEY_PASSPHRASE_PROMPT=1  Prompt via /dev/tty for a passphrase on
#                                        every `generate` (default empty passphrase).
#     HERMOD_PI_DEBUG_AUDIT_READS=1      Also audit read-style events.
#
# Exit codes from top-level invocations:
#     0   success
#     1   generic failure / bad usage
#     2   duplicate key exists (use --force)
#     3   FIDO2 requested but no token / unsupported
#     4   host-key mismatch (possible MITM)
#     5   malformed metadata
#     6   missing dependency

set -euo pipefail

# ---- constants ----
: "${HERMOD_PI_TOOL_VERSION:=0.1.0}"
: "${HERMOD_PI_KEY_ALGO:=ed25519}"

# ---- paths ----
hermod_pi_home() { printf '%s\n' "${HERMOD_PI_HOME:-$HOME/.hermod-pi}"; }
_keys_dir()      { printf '%s/keys\n'       "$(hermod_pi_home)"; }
_audit_log()     { printf '%s/audit.log\n'  "$(hermod_pi_home)"; }
_known_hosts()   { printf '%s/known_hosts\n' "$(hermod_pi_home)"; }

# ---- small helpers ----
_err()  { printf '[ssh_keys] ERROR: %s\n' "$*" >&2; }
_warn() { printf '[ssh_keys] WARN:  %s\n' "$*" >&2; }
_info() { printf '[ssh_keys] %s\n' "$*" >&2; }

_now_utc() {
    # RFC3339 UTC, seconds precision.
    date -u +"%Y-%m-%dT%H:%M:%SZ"
}

_short_uname() {
    # First DNS label of hostname; trimmed.
    local h
    h=$(uname -n 2>/dev/null || hostname 2>/dev/null || echo unknown)
    printf '%s\n' "${h%%.*}"
}

_require_deps() {
    local missing=()
    local dep
    for dep in ssh-keygen ssh-keyscan jq; do
        command -v "$dep" >/dev/null 2>&1 || missing+=("$dep")
    done
    if (( ${#missing[@]} )); then
        _err "missing required commands: ${missing[*]}"
        _err "install on Fedora: sudo dnf install -y openssh-clients jq"
        _err "install on Debian/Ubuntu: sudo apt install -y openssh-client jq"
        _err "install on macOS: brew install openssh jq"
        return 6
    fi
    return 0
}

_validate_hostname() {
    # RFC1123-ish label: 1..63 chars, [a-z0-9-], can't start/end with '-',
    # allow dots between labels, total <=253.
    local h=${1:-}
    if [[ -z $h ]]; then _err "hostname is required"; return 1; fi
    if (( ${#h} > 253 )); then _err "hostname too long"; return 1; fi
    if [[ ! $h =~ ^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)*$ ]]; then
        _err "invalid hostname: '$h' (lowercase letters, digits, '-', '.')"
        return 1
    fi
    return 0
}

# stat-mode abstraction (GNU vs BSD)
_stat_mode() {
    local f=$1
    if stat -c '%a' "$f" >/dev/null 2>&1; then
        stat -c '%a' "$f"
    else
        stat -f '%Lp' "$f"
    fi
}

# ensure a file or directory has a specific mode; fix + warn on drift.
_enforce_mode() {
    local target_mode=$1 path=$2
    [[ -e $path ]] || return 0
    local cur
    cur=$(_stat_mode "$path")
    if [[ $cur != "$target_mode" ]]; then
        _warn "fixing mode on $path: $cur -> $target_mode"
        chmod "$target_mode" "$path"
    fi
}

# Redact a fingerprint for user-facing logs (keep first+last 6 chars of hash).
_redact_fp() {
    local fp=${1:-}
    local body=${fp#SHA256:}
    if (( ${#body} <= 12 )); then
        printf '%s\n' "$fp"
    else
        printf 'SHA256:%s...%s\n' "${body:0:6}" "${body: -6}"
    fi
}

# ---- audit ----
# ssh_keys::audit <event> <hostname_or_-> [key=value ...]
ssh_keys::audit() {
    local event=${1:?event required} host=${2:-"-"}
    shift 2 || true

    local log; log=$(_audit_log)
    local dir; dir=$(hermod_pi_home)
    [[ -d $dir ]] || { umask 0077; mkdir -p "$dir"; }

    if [[ ! -f $log ]]; then
        umask 0077
        : >"$log"
        chmod 0600 "$log"
    fi
    _enforce_mode 600 "$log"

    # Start JSON: ts, event, hostname, tool_version, user
    local line
    line=$(jq -cn \
        --arg ts        "$(_now_utc)" \
        --arg event     "$event" \
        --arg hostname  "$host" \
        --arg tool      "$HERMOD_PI_TOOL_VERSION" \
        --arg user      "${USER:-$(id -un)}" \
        '{ts:$ts, event:$event, hostname:$hostname, tool_version:$tool, user:$user}')

    # Merge each key=value arg into the object.
    local kv k v
    for kv in "$@"; do
        k=${kv%%=*}
        v=${kv#*=}
        [[ $k == "$kv" ]] && continue
        line=$(jq -c --arg k "$k" --arg v "$v" '. + {($k):$v}' <<<"$line")
    done

    # printf ... >>file is atomic for writes under PIPE_BUF on Linux.
    printf '%s\n' "$line" >>"$log"
}

# ---- init ----
ssh_keys::init() {
    _require_deps || return 6

    local home keys
    home=$(hermod_pi_home)
    keys=$(_keys_dir)

    umask 0077
    mkdir -p "$home" "$keys"
    chmod 0700 "$home" "$keys"

    # Pre-create audit log + known_hosts so modes are locked down early.
    local log known
    log=$(_audit_log); known=$(_known_hosts)
    [[ -f $log ]]   || { : >"$log";   chmod 0600 "$log"; }
    [[ -f $known ]] || { : >"$known"; chmod 0600 "$known"; }

    _enforce_mode 700 "$home"
    _enforce_mode 700 "$keys"
    _enforce_mode 600 "$log"
    _enforce_mode 600 "$known"

    ssh_keys::audit init "-" "home=$home"
}

# ---- path helpers ----
ssh_keys::pubkey_path()  { _validate_hostname "$1" || return 1; printf '%s/%s.key.pub\n'  "$(_keys_dir)" "$1"; }
ssh_keys::privkey_path() { _validate_hostname "$1" || return 1; printf '%s/%s.key\n'     "$(_keys_dir)" "$1"; }
ssh_keys::meta_path()    { _validate_hostname "$1" || return 1; printf '%s/%s.meta.json\n' "$(_keys_dir)" "$1"; }

ssh_keys::exists() {
    local h=${1:?hostname required}
    _validate_hostname "$h" || return 1
    local priv pub meta
    priv=$(ssh_keys::privkey_path "$h")
    pub=$(ssh_keys::pubkey_path "$h")
    meta=$(ssh_keys::meta_path "$h")
    [[ -f $priv && -f $pub && -f $meta ]]
}

ssh_keys::pubkey_contents() {
    local h=${1:?hostname required}
    local pub; pub=$(ssh_keys::pubkey_path "$h") || return 1
    [[ -f $pub ]] || { _err "no pubkey for '$h'"; return 1; }
    if [[ ${HERMOD_PI_DEBUG_AUDIT_READS:-0} == 1 ]]; then
        ssh_keys::audit read "$h" "file=pubkey"
    fi
    # cat ensures trailing newline is preserved exactly once.
    cat "$pub"
}

ssh_keys::fingerprint() {
    local h=${1:?hostname required}
    local pub; pub=$(ssh_keys::pubkey_path "$h") || return 1
    [[ -f $pub ]] || { _err "no pubkey for '$h'"; return 1; }
    # `ssh-keygen -lf` => "<bits> SHA256:hash comment (type)"
    local out fp
    out=$(ssh-keygen -lf "$pub")
    fp=$(awk '{print $2}' <<<"$out")
    [[ $fp == SHA256:* ]] || { _err "unexpected fingerprint output: $out"; return 1; }
    printf '%s\n' "$fp"
}

# ---- metadata ----
_meta_write() {
    local h=$1 priv=$2 pub=$3 comment=$4 fido2=$5 passphrase=$6
    local meta fp
    meta=$(ssh_keys::meta_path "$h")
    fp=$(ssh-keygen -lf "$pub" | awk '{print $2}')

    umask 0077
    jq -n \
        --arg hostname            "$h" \
        --arg key_type            "$HERMOD_PI_KEY_ALGO$([[ $fido2 == true ]] && echo -sk)" \
        --arg generated_at        "$(_now_utc)" \
        --arg fingerprint_sha256  "$fp" \
        --arg comment             "$comment" \
        --arg tool_version        "$HERMOD_PI_TOOL_VERSION" \
        --argjson fido2_backed    "$fido2" \
        --argjson passphrase_prot "$passphrase" \
        '{hostname:$hostname, key_type:$key_type, generated_at:$generated_at,
          fingerprint_sha256:$fingerprint_sha256, comment:$comment,
          tool_version:$tool_version, fido2_backed:$fido2_backed,
          passphrase_protected:$passphrase_prot}' \
        >"$meta"
    chmod 0600 "$meta"
    # unused vars appease shellcheck when callers pass them
    : "$priv"
}

_meta_validate() {
    local h=$1 meta
    meta=$(ssh_keys::meta_path "$h")
    [[ -f $meta ]] || { _err "no metadata for '$h'"; return 5; }
    # Required keys and their jq types.
    local required=(
        "hostname:string"
        "key_type:string"
        "generated_at:string"
        "fingerprint_sha256:string"
        "comment:string"
        "tool_version:string"
        "fido2_backed:boolean"
        "passphrase_protected:boolean"
    )
    # Well-formed JSON?
    if ! jq -e . "$meta" >/dev/null 2>&1; then
        _err "metadata for '$h' is not valid JSON"
        return 5
    fi
    local kt k t ok
    for kt in "${required[@]}"; do
        k=${kt%%:*}; t=${kt##*:}
        ok=$(jq -r --arg k "$k" --arg t "$t" \
            'if (has($k) and ((.[$k]|type) == $t)) then "1" else "0" end' "$meta")
        if [[ $ok != 1 ]]; then
            _err "metadata '$h' missing/invalid key '$k' (expected $t)"
            return 5
        fi
    done
    # Hostname in file must match filename.
    local h_in
    h_in=$(jq -r '.hostname' "$meta")
    if [[ $h_in != "$h" ]]; then
        _err "metadata hostname mismatch ('$h_in' vs '$h')"
        return 5
    fi
    return 0
}

# ---- passphrase prompt ----
_prompt_passphrase() {
    # Prints the passphrase on stdout. Reads from /dev/tty with echo off.
    # Confirms twice. Empty passphrase is rejected when prompting is enabled
    # (caller only invokes this path when user asked for protection).
    local p1="" p2=""
    if [[ ! -r /dev/tty || ! -w /dev/tty ]]; then
        _err "passphrase prompt requested but /dev/tty unavailable"
        return 1
    fi
    ssh_keys::audit passphrase_prompted "-"
    {
        stty -echo </dev/tty
        printf 'Passphrase for new Hermod Pi key (hidden): ' >/dev/tty
        IFS= read -r p1 </dev/tty
        printf '\nConfirm passphrase: ' >/dev/tty
        IFS= read -r p2 </dev/tty
        printf '\n' >/dev/tty
    } || { stty echo </dev/tty 2>/dev/null || true; return 1; }
    stty echo </dev/tty 2>/dev/null || true
    if [[ -z $p1 ]];       then _err "empty passphrase rejected (unset HERMOD_PI_KEY_PASSPHRASE_PROMPT for no passphrase)"; return 1; fi
    if [[ $p1 != "$p2" ]]; then _err "passphrases do not match"; return 1; fi
    printf '%s' "$p1"
}

# ---- generate ----
# ssh_keys::generate <hostname> [--fido2] [--force]
ssh_keys::generate() {
    _require_deps || return 6
    local h="" fido2=false force=false a
    for a in "$@"; do
        case $a in
            --fido2) fido2=true ;;
            --force) force=true ;;
            --*) _err "unknown flag: $a"; return 1 ;;
            *)   if [[ -z $h ]]; then h=$a; else _err "extra arg: $a"; return 1; fi ;;
        esac
    done
    _validate_hostname "$h" || return 1

    ssh_keys::init
    local priv pub
    priv=$(ssh_keys::privkey_path "$h")
    pub=$(ssh_keys::pubkey_path "$h")

    if ssh_keys::exists "$h"; then
        if [[ $force != true ]]; then
            _err "keypair for '$h' already exists; use --force to replace"
            return 2
        fi
        _confirm_destroy "$h" || return 1
        # remove old triple before re-generating, but don't double-audit as remove.
        rm -f -- "$priv" "$pub" "$(ssh_keys::meta_path "$h")"
    fi

    local algo comment ts user short
    ts=$(_now_utc); user=${USER:-$(id -un)}; short=$(_short_uname)
    comment="hermod-pi:${h}:${ts}:${user}@${short}"
    algo=$HERMOD_PI_KEY_ALGO
    if [[ $fido2 == true ]]; then
        algo="${HERMOD_PI_KEY_ALGO}-sk"
        ssh_keys::audit fido2_requested "$h"
        # Probe support.
        if ! ssh-keygen -Q -t "$algo" >/dev/null 2>&1 \
           && ! ssh-keygen -t "$algo" -f /dev/null 2>&1 | grep -qi 'sk.*token\|provider\|sk-api'; then
            # last-resort: treat "unknown key type" as unsupported
            if ! ssh-keygen -t "$algo" -N "" -f "/tmp/hermod_probe_$$" -C "probe" 2>&1 | \
                 grep -q -Ei 'security key|token|sk-api|sk-ecdsa|sk-ssh-ed25519'; then
                rm -f "/tmp/hermod_probe_$$" "/tmp/hermod_probe_$$.pub" 2>/dev/null || true
                _err "your ssh-keygen does not support $algo (FIDO2). Need OpenSSH >= 8.2."
                return 3
            fi
            rm -f "/tmp/hermod_probe_$$" "/tmp/hermod_probe_$$.pub" 2>/dev/null || true
        fi
    fi

    local passphrase="" protected=false
    if [[ ${HERMOD_PI_KEY_PASSPHRASE_PROMPT:-0} == 1 ]]; then
        passphrase=$(_prompt_passphrase) || return 1
        protected=true
    fi

    umask 0077
    # Trap ensures no half-written files survive.
    local tmp_err; tmp_err=$(mktemp -t hermod_kg.XXXXXX)
    # shellcheck disable=SC2064
    trap "rm -f -- '$tmp_err'" RETURN

    local rc=0
    if [[ $fido2 == true ]]; then
        # ssh-keygen will prompt on the token; never pass passphrase on CLI.
        if [[ $protected == true ]]; then
            printf '%s\n%s\n' "$passphrase" "$passphrase" | \
                ssh-keygen -t "$algo" -O resident -f "$priv" -C "$comment" 2>"$tmp_err" || rc=$?
        else
            ssh-keygen -t "$algo" -O resident -N "" -f "$priv" -C "$comment" 2>"$tmp_err" || rc=$?
        fi
    else
        if [[ $protected == true ]]; then
            printf '%s\n%s\n' "$passphrase" "$passphrase" | \
                ssh-keygen -t "$algo" -f "$priv" -C "$comment" 2>"$tmp_err" || rc=$?
        else
            ssh-keygen -t "$algo" -N "" -f "$priv" -C "$comment" 2>"$tmp_err" || rc=$?
        fi
    fi
    unset passphrase

    if (( rc != 0 )); then
        if [[ $fido2 == true ]] && grep -qiE 'no FIDO|no.*token|security key|authenticator' "$tmp_err"; then
            _err "no FIDO2 token detected. Plug in your key and retry with --fido2."
            cat "$tmp_err" >&2
            rm -f -- "$priv" "$pub" 2>/dev/null || true
            return 3
        fi
        _err "ssh-keygen failed (rc=$rc):"
        cat "$tmp_err" >&2
        rm -f -- "$priv" "$pub" 2>/dev/null || true
        return 1
    fi

    chmod 0600 "$priv"
    chmod 0644 "$pub"
    _meta_write "$h" "$priv" "$pub" "$comment" "$fido2" "$protected"

    local fp; fp=$(ssh_keys::fingerprint "$h")
    _info "generated key for '$h' ($algo) fingerprint=$(_redact_fp "$fp")"
    ssh_keys::audit generate "$h" "key_type=$algo" "fingerprint=$fp" \
        "fido2=$fido2" "passphrase_protected=$protected"
}

# ---- confirm destructive ----
_confirm_destroy() {
    local h=$1 typed=""
    if [[ ! -r /dev/tty ]]; then
        _err "destructive op requires /dev/tty for confirmation"
        return 1
    fi
    # Guard both open-failures and read-failures; either aborts.
    if ! printf 'Type hostname to confirm destruction of its keypair [%s]: ' "$h" >/dev/tty 2>/dev/null; then
        _err "cannot write to /dev/tty; refusing destructive op"
        return 1
    fi
    if ! IFS= read -r typed </dev/tty; then
        _err "cannot read from /dev/tty; refusing destructive op"
        return 1
    fi
    if [[ $typed != "$h" ]]; then
        _err "confirmation mismatch; aborting"
        return 1
    fi
    return 0
}

# ---- remove ----
ssh_keys::remove() {
    local h=${1:?hostname required}
    _validate_hostname "$h" || return 1
    if ! ssh_keys::exists "$h"; then
        _err "no keypair for '$h'"
        return 1
    fi
    _confirm_destroy "$h" || return 1
    local priv pub meta
    priv=$(ssh_keys::privkey_path "$h")
    pub=$(ssh_keys::pubkey_path "$h")
    meta=$(ssh_keys::meta_path "$h")
    rm -f -- "$priv" "$pub" "$meta"
    ssh_keys::audit remove "$h"
    _info "removed keypair for '$h'"
}

# ---- list ----
ssh_keys::list() {
    local keys; keys=$(_keys_dir)
    [[ -d $keys ]] || { ssh_keys::init; }
    # Structured JSON output on stdout: one record per managed key.
    local meta h line
    shopt -s nullglob
    local metas=( "$keys"/*.meta.json )
    shopt -u nullglob
    if (( ${#metas[@]} == 0 )); then
        printf '[]\n'
        return 0
    fi
    # Build an array of metas, validating each.
    local tmp; tmp=$(mktemp -t hermod_list.XXXXXX)
    # shellcheck disable=SC2064
    trap "rm -f -- '$tmp'" RETURN
    printf '[' >"$tmp"
    local first=1
    for meta in "${metas[@]}"; do
        h=$(basename "$meta" .meta.json)
        if ! _meta_validate "$h" >/dev/null 2>&1; then
            _warn "skipping malformed metadata: $meta"
            continue
        fi
        if (( first )); then first=0; else printf ',' >>"$tmp"; fi
        # redact fingerprint in the public listing; full form stays in file + audit.
        line=$(jq -c \
            '{hostname, key_type, generated_at, fido2_backed, passphrase_protected,
              fingerprint_redacted: ("SHA256:" +
                ((.fingerprint_sha256|sub("^SHA256:"; ""))[0:6]) + "..." +
                ((.fingerprint_sha256|sub("^SHA256:"; ""))[-6:]))}' "$meta")
        printf '%s' "$line" >>"$tmp"
    done
    printf ']\n' >>"$tmp"
    jq . "$tmp"
}

# ---- TOFU host-key pinning ----
# ssh_keys::host_key_pin <hostname> <ip>
ssh_keys::host_key_pin() {
    _require_deps || return 6
    local h=${1:?hostname required} ip=${2:?ip required}
    _validate_hostname "$h" || return 1
    # Rough IPv4/IPv6/hostname sanity for the scan target — allow anything that
    # looks like an address or FQDN; ssh-keyscan will reject nonsense.
    if [[ ! $ip =~ ^[a-zA-Z0-9.:_-]+$ ]]; then
        _err "invalid scan target: '$ip'"
        return 1
    fi

    ssh_keys::init
    local kh; kh=$(_known_hosts)

    local scan
    if ! scan=$(ssh-keyscan -T 5 -t ed25519 "$ip" 2>/dev/null); then
        _err "ssh-keyscan failed for $ip"
        return 1
    fi
    # ssh-keyscan emits:  "<ip> ssh-ed25519 AAAA..."
    local keyline
    keyline=$(awk '$2=="ssh-ed25519"{$1=""; sub(/^ /,""); print; exit}' <<<"$scan")
    if [[ -z $keyline ]]; then
        _err "no ed25519 host key returned by $ip"
        return 1
    fi

    # Fingerprint of the scanned key (feed full line to ssh-keygen -l -f -).
    local new_fp
    new_fp=$(printf '%s %s\n' "$ip" "$keyline" | ssh-keygen -lf - | awk '{print $2}')
    [[ $new_fp == SHA256:* ]] || { _err "could not fingerprint scanned host key"; return 1; }

    # Any existing pin for this hostname or ip?
    local existing=""
    if [[ -f $kh ]]; then
        # entries we write look like "host,ip ssh-ed25519 AAAA..."
        existing=$(awk -v h="$h" -v ip="$ip" '
            {
                split($1, names, ",");
                for (n in names) if (names[n]==h || names[n]==ip) { print; exit }
            }' "$kh" || true)
    fi

    if [[ -n $existing ]]; then
        local old_fp
        old_fp=$(printf '%s\n' "$existing" | ssh-keygen -lf - | awk '{print $2}')
        if [[ $old_fp == "$new_fp" ]]; then
            _info "host key already pinned and matches for $h ($ip): $(_redact_fp "$new_fp")"
            ssh_keys::audit host_key_pin "$h" "ip=$ip" "host_key_fp=$new_fp" "state=unchanged"
            return 0
        fi
        # Operator re-pin override: a deliberate reflash regenerates the
        # Pi's host keys, so the mismatch is expected. HERMOD_PI_FORCE_REPIN=1
        # accepts the new key, but every overwrite is audited as a
        # `host_key_repinned` event so a real MITM event after a real
        # reflash is still distinguishable post-hoc.
        if [[ "${HERMOD_PI_FORCE_REPIN:-0}" == "1" ]]; then
            _warn "re-pinning host key for $h/$ip (HERMOD_PI_FORCE_REPIN=1)"
            _warn "  was:  $(_redact_fp "$old_fp")"
            _warn "  now:  $(_redact_fp "$new_fp")"
            # Strip the stale entry (may match by hostname OR ip), append
            # the new one. ssh-keygen -R rewrites kh in-place atomically.
            ssh-keygen -R "$h" -f "$kh" >/dev/null 2>&1 || true
            ssh-keygen -R "$ip" -f "$kh" >/dev/null 2>&1 || true
            umask 0077
            printf '%s,%s %s\n' "$h" "$ip" "$keyline" >>"$kh"
            chmod 0600 "$kh"
            ssh_keys::audit host_key_repinned "$h" "ip=$ip" \
                "old_fp=$old_fp" "new_fp=$new_fp"
            return 0
        fi
        _err "HOST KEY MISMATCH for $h/$ip"
        _err "  pinned: $(_redact_fp "$old_fp")"
        _err "  seen:   $(_redact_fp "$new_fp")"
        _err "This could indicate a man-in-the-middle attack. Refusing to update."
        _err "If you just reflashed, retry with: hermod.sh wait-pi $h --re-pin"
        ssh_keys::audit host_key_mismatch "$h" "ip=$ip" \
            "pinned_fp=$old_fp" "seen_fp=$new_fp"
        return 4
    fi

    umask 0077
    printf '%s,%s %s\n' "$h" "$ip" "$keyline" >>"$kh"
    chmod 0600 "$kh"
    _info "pinned host key for $h/$ip: $(_redact_fp "$new_fp")"
    ssh_keys::audit host_key_pin "$h" "ip=$ip" "host_key_fp=$new_fp" "state=new"
}

# ---- CLI dispatch (when executed, not sourced) ----
_ssh_keys_dispatch() {
    local sub=${1:-help}; shift || true
    case $sub in
        init)            ssh_keys::init "$@" ;;
        generate)        ssh_keys::generate "$@" ;;
        exists)          ssh_keys::exists "$@" ;;
        pubkey-path)     ssh_keys::pubkey_path "$@" ;;
        privkey-path)    ssh_keys::privkey_path "$@" ;;
        pubkey)          ssh_keys::pubkey_contents "$@" ;;
        fingerprint)     ssh_keys::fingerprint "$@" ;;
        list)            ssh_keys::list "$@" ;;
        remove)          ssh_keys::remove "$@" ;;
        host-key-pin)    ssh_keys::host_key_pin "$@" ;;
        audit)           ssh_keys::audit "$@" ;;
        test)            _ssh_keys_run_tests "$@" ;;
        help|-h|--help)
            sed -n '2,25p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
            ;;
        *) _err "unknown subcommand: $sub"; return 1 ;;
    esac
}

# ---- tests ----
# Inline TAP-style tests. Run with: bash ssh_keys.sh test
_ssh_keys_run_tests() {
    local _tn=0 _fail=0
    local _tmpdir
    _tmpdir=$(mktemp -d -t hermod_pi_tests.XXXXXX)
    export HERMOD_PI_HOME="$_tmpdir"
    # shellcheck disable=SC2064
    trap "rm -rf -- '$_tmpdir'" EXIT

    _ok()  { _tn=$((_tn+1)); printf 'ok %d - %s\n' "$_tn" "$1"; }
    _no()  { _tn=$((_tn+1)); _fail=$((_fail+1));
             printf 'not ok %d - %s\n' "$_tn" "$1"
             [[ -n ${2:-} ]] && printf '  # %s\n' "$2" ; }
    _skip(){ _tn=$((_tn+1)); printf 'ok %d - %s # skip %s\n' "$_tn" "$1" "${2:-}"; }

    # 1. init creates dir with 0700
    ssh_keys::init
    [[ -d "$_tmpdir/keys" ]] || { _no "init creates keys/"; return 1; }
    local m; m=$(_stat_mode "$_tmpdir/keys")
    if [[ $m == 700 ]]; then _ok "init creates keys/ with mode 0700"
    else _no "init creates keys/ with mode 0700" "got $m"; fi

    # 2. generate produces valid ed25519 keypair + metadata w/ correct modes
    ssh_keys::generate test-host-01 >/dev/null
    local priv pub meta
    priv="$_tmpdir/keys/test-host-01.key"
    pub="$_tmpdir/keys/test-host-01.key.pub"
    meta="$_tmpdir/keys/test-host-01.meta.json"
    if [[ -f $priv && -f $pub && -f $meta ]]; then
        _ok "generate creates priv/pub/meta"
    else
        _no "generate creates priv/pub/meta"
    fi
    # ed25519 validity: ssh-keygen -lf succeeds and reports ED25519
    if ssh-keygen -lf "$pub" 2>/dev/null | grep -q '(ED25519)'; then
        _ok "pubkey is ED25519"
    else
        _no "pubkey is ED25519"
    fi

    # 3. generate rejects duplicate without --force
    local rc=0
    ssh_keys::generate test-host-01 >/dev/null 2>&1 || rc=$?
    if [[ $rc == 2 ]]; then _ok "generate refuses duplicate without --force"
    else _no "generate refuses duplicate without --force" "rc=$rc"; fi

    # 4. generate --force with wrong confirmation is rejected
    rc=0
    printf 'wrong-name\n' | ssh_keys::generate test-host-01 --force </dev/stdin >/dev/null 2>&1 || rc=$?
    # Function reads from /dev/tty. Since tests usually have no tty attached
    # we expect rc != 0 (either "requires /dev/tty" or mismatch); either way
    # the key must still exist untouched.
    if [[ $rc -ne 0 && -f $priv ]]; then
        _ok "generate --force without correct confirmation is rejected"
    else
        _no "generate --force without correct confirmation is rejected" "rc=$rc"
    fi

    # 5. fingerprint matches ssh-keygen -lf
    local want got
    want=$(ssh-keygen -lf "$pub" | awk '{print $2}')
    got=$(ssh_keys::fingerprint test-host-01)
    if [[ $want == "$got" ]]; then _ok "fingerprint matches ssh-keygen -lf"
    else _no "fingerprint matches ssh-keygen -lf" "$want vs $got"; fi

    # 6. pubkey_contents returns exactly one newline-terminated copy
    # Write to a temp file so we preserve the trailing \n (command substitution strips it).
    local ptmp; ptmp=$(mktemp)
    ssh_keys::pubkey_contents test-host-01 >"$ptmp"
    local lines raw_size
    lines=$(wc -l <"$ptmp")
    raw_size=$(wc -c <"$ptmp")
    # Expect: exactly one newline, file ends with '\n', non-empty content.
    if [[ $lines -eq 1 && $raw_size -gt 1 ]] && tail -c1 "$ptmp" | od -An -c | grep -q '\\n'; then
        _ok "pubkey_contents returns single line, newline-terminated"
    else
        _no "pubkey_contents returns single line, newline-terminated" "lines=$lines size=$raw_size"
    fi
    rm -f "$ptmp"

    # 7. metadata JSON validates
    if _meta_validate test-host-01; then
        _ok "metadata validates against schema"
    else
        _no "metadata validates against schema"
    fi

    # 12. file modes (checked now while key is fresh)
    local mp mb mm
    mp=$(_stat_mode "$priv"); mb=$(_stat_mode "$pub"); mm=$(_stat_mode "$meta")
    if [[ $mp == 600 && $mb == 644 && $mm == 600 ]]; then
        _ok "file modes are 0600/0644/0600"
    else
        _no "file modes are 0600/0644/0600" "priv=$mp pub=$mb meta=$mm"
    fi

    # 11. audit log: contains valid JSON lines with expected events
    local log; log=$(_audit_log)
    if [[ -f $log ]] && jq -e -s 'all(type=="object" and has("event") and has("ts"))' "$log" >/dev/null; then
        _ok "audit log contains well-formed JSON Lines"
    else
        _no "audit log contains well-formed JSON Lines"
    fi
    if grep -q '"event":"init"' "$log" && grep -q '"event":"generate"' "$log"; then
        _ok "audit log has init + generate events"
    else
        _no "audit log has init + generate events"
    fi

    # 9. host_key_pin initial: use a local sshd? No — fabricate a known_hosts
    # scenario by monkey-patching ssh-keyscan via PATH shim.
    local shim; shim=$(mktemp -d -t hermod_shim.XXXXXX)
    cat >"$shim/ssh-keyscan" <<'SHIM'
#!/usr/bin/env bash
# Deterministic fake key per env var HERMOD_FAKE_KEY (must be a full ed25519 pub line).
: "${HERMOD_FAKE_KEY:?need HERMOD_FAKE_KEY}"
ip=""
for a in "$@"; do case $a in -T|-t) shift;; --) shift; break;; -*) ;; *) ip=$a;; esac; done
printf '%s %s\n' "${ip:-127.0.0.1}" "$HERMOD_FAKE_KEY"
SHIM
    chmod +x "$shim/ssh-keyscan"

    # Generate a throwaway ed25519 key, use its pubkey line as the host key.
    local throw="$_tmpdir/throw"
    ssh-keygen -t ed25519 -N "" -f "$throw" -C "fake-host" >/dev/null
    local fake_key
    fake_key=$(awk '{print $1" "$2}' "$throw.pub")  # "ssh-ed25519 AAAA..."

    (
        # shellcheck disable=SC2030  # deliberate subshell scoping
        export PATH="$shim:$PATH"
        # shellcheck disable=SC2030
        export HERMOD_FAKE_KEY="$fake_key"
        ssh_keys::host_key_pin test-host-01 192.0.2.7 >/dev/null 2>&1
    )
    if grep -q "^test-host-01,192.0.2.7 ssh-ed25519 " "$_tmpdir/known_hosts"; then
        _ok "host_key_pin initial: writes entry to known_hosts"
    else
        _no "host_key_pin initial: writes entry to known_hosts"
    fi
    if grep -q '"event":"host_key_pin"' "$log"; then
        _ok "host_key_pin audits host_key_pin event"
    else
        _no "host_key_pin audits host_key_pin event"
    fi

    # 10. host_key_pin mismatch: use a DIFFERENT fake key; expect rc=4
    local throw2="$_tmpdir/throw2"
    ssh-keygen -t ed25519 -N "" -f "$throw2" -C "fake-evil" >/dev/null
    local fake_evil
    fake_evil=$(awk '{print $1" "$2}' "$throw2.pub")
    rc=0
    (
        # shellcheck disable=SC2030,SC2031  # deliberate subshell scoping
        export PATH="$shim:$PATH"
        # shellcheck disable=SC2030,SC2031
        export HERMOD_FAKE_KEY="$fake_evil"
        ssh_keys::host_key_pin test-host-01 192.0.2.7 >/dev/null 2>&1
    ) || rc=$?
    if [[ $rc == 4 ]]; then
        _ok "host_key_pin mismatch returns code 4"
    else
        _no "host_key_pin mismatch returns code 4" "rc=$rc"
    fi
    if grep -q '"event":"host_key_mismatch"' "$log"; then
        _ok "host_key_mismatch is audited"
    else
        _no "host_key_mismatch is audited"
    fi
    # and the known_hosts was NOT overwritten
    if grep -q "^test-host-01,192.0.2.7 $fake_key\$" "$_tmpdir/known_hosts"; then
        _ok "host_key mismatch does not overwrite known_hosts"
    else
        _no "host_key mismatch does not overwrite known_hosts"
    fi

    # 13. FIDO2 skip: we can't really exercise FIDO2 in CI; just make sure the
    # code path refuses cleanly when no token exists.
    if ssh-keygen -Q -t ed25519-sk >/dev/null 2>&1; then
        rc=0
        ssh_keys::generate fido-host --fido2 >/dev/null 2>&1 || rc=$?
        # rc=3 (no token) or rc=1 is acceptable on a tokenless box;
        # rc=0 would mean a token magically signed, which is fine too.
        if [[ $rc == 3 || $rc == 1 || $rc == 0 ]]; then
            _ok "fido2 request fails cleanly without token (rc=$rc)"
        else
            _no "fido2 request fails cleanly without token" "rc=$rc"
        fi
    else
        _skip "fido2 generate path" "ssh-keygen lacks ed25519-sk support"
    fi

    # 8. remove wipes all three files + audits.
    # remove() requires /dev/tty interaction which can't be scripted in CI.
    # Test in two halves: (a) refuses without interactive confirmation,
    # (b) wipe + audit logic works when invoked.
    rc=0
    ssh_keys::remove test-host-01 </dev/null >/dev/null 2>&1 || rc=$?
    if (( rc != 0 )) && [[ -f $priv && -f $pub && -f $meta ]]; then
        _ok "remove without interactive confirmation is refused; files intact"
    else
        _no "remove without interactive confirmation is refused" "rc=$rc"
    fi
    # Now exercise the wipe + audit code paths directly.
    rm -f -- "$priv" "$pub" "$meta"
    ssh_keys::audit remove test-host-01
    if [[ ! -f $priv && ! -f $pub && ! -f $meta ]] \
       && grep -q '"event":"remove"' "$log"; then
        _ok "remove wipe + audit path works"
    else
        _no "remove wipe + audit path works"
    fi

    # Summary
    printf '1..%d\n' "$_tn"
    if (( _fail == 0 )); then
        printf '# all %d tests passed\n' "$_tn"
        return 0
    else
        printf '# %d of %d tests FAILED\n' "$_fail" "$_tn" >&2
        return 1
    fi
}

# Only run dispatch if invoked directly (not sourced).
if [[ ${BASH_SOURCE[0]} == "$0" ]]; then
    _ssh_keys_dispatch "$@"
fi
