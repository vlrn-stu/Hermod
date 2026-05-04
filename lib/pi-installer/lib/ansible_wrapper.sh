#!/usr/bin/env bash
# ansible_wrapper.sh - Hermod Pi installer: Ansible orchestration library.
#
# Sourced by the hermod-pi main CLI. Exposes ansible::* functions that
# discover a freshly-imaged Raspberry Pi on the LAN, generate a per-host
# inventory, and drive the existing ansible/ playbooks (install, verify,
# uninstall) without modifying them.
#
# Security posture:
#   - Per-invocation SSH always uses StrictHostKeyChecking=yes and a
#     tool-owned known_hosts file (TOFU handled upstream by ssh_keys.sh).
#   - The repo's ansible.cfg disables host key checking; we override with
#     ANSIBLE_HOST_KEY_CHECKING=True and ANSIBLE_SSH_ARGS at call time.
#   - Inventories are written 0600. Private key paths are never cat'd.
#   - Become passwords must come from ansible-vault, never -e on the CLI.
#
# Public interface (see bottom of file for signatures):
#   ansible::check_deps
#   ansible::discover       <hostname> [--timeout=N] [--ip=ADDR]
#   ansible::generate_inventory <hostname> <ip> <ssh_key_path> <user>
#   ansible::run_playbook   <playbook_name> <hostname> [extra-vars...]
#   ansible::install        <hostname>
#   ansible::verify         <hostname>
#   ansible::uninstall      <hostname>
#   ansible::ssh_opts       <hostname>
#
# Run ``bash ansible_wrapper.sh test`` for the self-test suite.

set -euo pipefail

# ---------------------------------------------------------------------------
# Globals
# ---------------------------------------------------------------------------

: "${HERMOD_PI_HOME:=${XDG_STATE_HOME:-$HOME/.local/state}/hermod-pi}"
: "${HERMOD_PI_TOOL_VERSION:=0.0.0-dev}"
# HERMOD_ANSIBLE_DIR may be set by the caller or auto-detected.
: "${HERMOD_ANSIBLE_DIR:=}"

# Specific exit codes used by run_playbook / verify so the main CLI can
# branch on them.
readonly ANSIBLE_WRAPPER_EXIT_MISSING_DEPS=10
readonly ANSIBLE_WRAPPER_EXIT_UNSAFE_INPUT=11
readonly ANSIBLE_WRAPPER_EXIT_NO_INVENTORY=12
readonly ANSIBLE_WRAPPER_EXIT_NO_PLAYBOOK=13
readonly ANSIBLE_WRAPPER_EXIT_NO_TOFU=14
readonly ANSIBLE_WRAPPER_EXIT_DISCOVERY=15

# ---------------------------------------------------------------------------
# Small helpers
# ---------------------------------------------------------------------------

_aw::log()  { printf '[ansible-wrapper] %s\n' "$*" >&2; }
_aw::warn() { printf '[ansible-wrapper] WARN: %s\n' "$*" >&2; }
_aw::err()  { printf '[ansible-wrapper] ERROR: %s\n' "$*" >&2; }

# Portable millisecond epoch (seconds is fine for our purposes).
_aw::now() { date +%s; }

# Reject anything that is not a plain DNS/mDNS label.  Hostnames land in
# filenames and on the command line, so we clamp them hard: RFC 1123 label
# (a-z0-9 and hyphen, not leading/trailing hyphen, 1-63 chars).
_aw::sanitize_hostname() {
    local h=$1
    if [[ -z $h ]]; then
        _aw::err "hostname is empty"
        return "$ANSIBLE_WRAPPER_EXIT_UNSAFE_INPUT"
    fi
    if (( ${#h} > 63 )); then
        _aw::err "hostname too long (>63 chars): $h"
        return "$ANSIBLE_WRAPPER_EXIT_UNSAFE_INPUT"
    fi
    if [[ ! $h =~ ^[a-zA-Z0-9]([a-zA-Z0-9-]*[a-zA-Z0-9])?$ ]]; then
        _aw::err "hostname contains unsafe characters: $h"
        return "$ANSIBLE_WRAPPER_EXIT_UNSAFE_INPUT"
    fi
    printf '%s' "$h"
}

# IPv4 dotted-quad check. Keeps the inventory writer honest.
_aw::is_ipv4() {
    local ip=$1 seg
    [[ $ip =~ ^([0-9]{1,3}\.){3}[0-9]{1,3}$ ]] || return 1
    IFS=. read -r -a _awsegs <<< "$ip"
    for seg in "${_awsegs[@]}"; do
        (( seg >= 0 && seg <= 255 )) || return 1
    done
    return 0
}

_aw::platform() {
    case "$(uname -s)" in
        Linux)  printf 'linux' ;;
        Darwin) printf 'macos' ;;
        *)      printf 'unknown' ;;
    esac
}

# Walk up from a starting directory until we find one that contains an
# ``ansible`` subdirectory with an ansible.cfg.  Used when
# HERMOD_ANSIBLE_DIR is not set.
_aw::find_ansible_dir() {
    local start=${1:-${BASH_SOURCE[0]}}
    local dir
    dir=$(cd "$(dirname "$start")" && pwd)
    while [[ $dir != / && $dir != . ]]; do
        if [[ -f "$dir/ansible/ansible.cfg" ]]; then
            printf '%s' "$dir/ansible"
            return 0
        fi
        dir=$(dirname "$dir")
    done
    return 1
}

_aw::ansible_dir() {
    if [[ -n ${HERMOD_ANSIBLE_DIR:-} ]]; then
        printf '%s' "$HERMOD_ANSIBLE_DIR"
        return 0
    fi
    _aw::find_ansible_dir "${BASH_SOURCE[0]}"
}

_aw::tool_home()        { printf '%s' "$HERMOD_PI_HOME"; }
_aw::inventories_dir()  { printf '%s/inventories' "$HERMOD_PI_HOME"; }
_aw::known_hosts_path() { printf '%s/known_hosts' "$HERMOD_PI_HOME"; }
_aw::audit_log_path()   { printf '%s/audit.log'   "$HERMOD_PI_HOME"; }
_aw::inventory_path()   { printf '%s/inventories/%s.hosts.yml' "$HERMOD_PI_HOME" "$1"; }

_aw::ensure_tool_home() {
    local inv; inv=$(_aw::inventories_dir)
    mkdir -p "$inv"
    chmod 0700 "$(_aw::tool_home)" "$inv"
}

# TOFU gate: the known_hosts file (managed by ssh_keys.sh) must contain
# an entry for this hostname before we dare run ansible against it.
_aw::has_pinned_host_key() {
    local hostname=$1
    local kh; kh=$(_aw::known_hosts_path)
    [[ -r $kh ]] || return 1
    ssh-keygen -F "$hostname" -f "$kh" >/dev/null 2>&1
}

# ---------------------------------------------------------------------------
# ansible::check_deps
# ---------------------------------------------------------------------------

ansible::check_deps() {
    local plat; plat=$(_aw::platform)
    local missing=()
    local tool

    for tool in ansible-playbook ansible-inventory ssh ssh-keygen; do
        command -v "$tool" >/dev/null 2>&1 || missing+=("$tool")
    done

    # One of {nc, ncat, bash /dev/tcp} is fine for SSH reachability.
    if ! command -v nc >/dev/null 2>&1 && ! command -v ncat >/dev/null 2>&1; then
        _aw::warn "nc/ncat not found; falling back to bash /dev/tcp for SSH probe"
    fi

    # mDNS resolver is platform specific.
    case "$plat" in
        linux)
            command -v avahi-resolve >/dev/null 2>&1 || missing+=("avahi-resolve")
            ;;
        macos)
            command -v dns-sd >/dev/null 2>&1 || missing+=("dns-sd")
            ;;
    esac

    if (( ${#missing[@]} > 0 )); then
        _aw::err "missing required tools: ${missing[*]}"
        case "$plat" in
            linux)
                _aw::err "Linux install hints:"
                _aw::err "  Fedora:  sudo dnf install ansible-core openssh-clients avahi-tools nmap-ncat"
                _aw::err "  Debian:  sudo apt install ansible openssh-client avahi-utils netcat-openbsd"
                _aw::err "  pipx:    pipx install --include-deps ansible"
                ;;
            macos)
                _aw::err "macOS install hints:"
                _aw::err "  brew install ansible    (dns-sd and nc ship with macOS)"
                ;;
            *)
                _aw::err "Unsupported platform $(uname -s); this tool requires Linux or macOS."
                ;;
        esac
        return "$ANSIBLE_WRAPPER_EXIT_MISSING_DEPS"
    fi

    return 0
}

# ---------------------------------------------------------------------------
# ansible::discover
# ---------------------------------------------------------------------------
# Resolves mDNS name <hostname>.local, then polls TCP/22 until open
# or a timeout elapses.  Prints the resolved IPv4 on stdout.  --ip=ADDR
# skips the mDNS step and probes ADDR directly.

_aw::mdns_resolve() {
    local fqdn=$1
    local plat; plat=$(_aw::platform)
    case "$plat" in
        linux)
            if ! command -v avahi-resolve >/dev/null 2>&1; then
                _aw::err "avahi-resolve not installed (install avahi-utils/avahi-tools)"
                return 1
            fi
            # avahi-resolve prints "<fqdn>\t<ip>"; we want field 2.
            local out
            out=$(avahi-resolve -n4 "$fqdn" 2>/dev/null || true)
            [[ -n $out ]] || return 1
            awk '{print $2; exit}' <<< "$out"
            ;;
        macos)
            if ! command -v dns-sd >/dev/null 2>&1; then
                _aw::err "dns-sd not available on this system"
                return 1
            fi
            # dns-sd -G blocks; run with a short timeout and grep the IPv4.
            local out
            out=$( ( dns-sd -G v4 "$fqdn" & local pid=$!; sleep 3; kill "$pid" 2>/dev/null || true ) 2>/dev/null \
                | awk '/Add/ && /[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+/ {for(i=1;i<=NF;i++) if ($i ~ /^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$/){print $i; exit}}')
            [[ -n $out ]] || return 1
            printf '%s' "$out"
            ;;
        *)
            _aw::err "mDNS not supported on this platform"
            return 1
            ;;
    esac
}

# Raspberry Pi vendor OUIs (Foundation + Trading Ltd, every era).
# When mDNS fails we walk the kernel ARP table and accept any neighbour
# whose MAC matches one of these prefixes — it's vanishingly unlikely a
# non-Pi device on the LAN spoofs these and also answers Ubuntu sshd.
_AW_PI_OUI_REGEX='^(b8:27:eb|dc:a6:32|e4:5f:01|d8:3a:dd|2c:cf:67):'

# Kernel ARP cache → list of "ip mac" lines for every neighbour with a
# Pi-vendor MAC that's currently REACHABLE/STALE/DELAY/PROBE. Linux only;
# macOS would need `arp -a` parsing which is not wired up yet.
_aw::arp_pi_candidates() {
    [[ "$(_aw::platform)" == "linux" ]] || return 1
    ip -4 neigh show 2>/dev/null \
        | awk -v re="$_AW_PI_OUI_REGEX" '
            $5 ~ re && $NF ~ /(REACHABLE|STALE|DELAY|PROBE)/ {
                print $1, $5
            }'
}

# Quick parallel ping sweep across the local /24 to populate ARP entries
# for hosts the kernel hasn't talked to yet. Best-effort and silent;
# `wait` caps the runtime to one ping interval.
_aw::warm_arp_cache() {
    local cidr="$1" base prefix o1 o2 o3 _ i
    base="${cidr%/*}"; prefix="${cidr#*/}"
    [[ "$prefix" == "24" ]] || return 0
    IFS=. read -r o1 o2 o3 _ <<< "$base"
    for i in $(seq 1 254); do
        ping -c1 -W1 -q "$o1.$o2.$o3.$i" >/dev/null 2>&1 &
    done
    wait
}

# Probe TCP/22 + grab the SSH banner. Returns 0 iff the banner contains
# "Ubuntu" — narrows ARP candidates to actually-our-Pi vs random Pi-MAC
# devices (a neighbour's print server, an unrelated home-lab host, ...).
_aw::ssh_banner_is_ubuntu() {
    local ip="$1" banner
    banner=$(timeout 3 bash -c "exec 3<>/dev/tcp/$ip/22 && head -1 <&3 && exec 3<&-" 2>/dev/null) \
        || return 1
    [[ "$banner" == *Ubuntu* ]]
}

# ARP-scan fallback for ansible::discover. Pings the LAN to warm the
# cache, walks the table for Pi-OUI MACs, sanity-checks each via SSH
# banner, prints the first hit. Returns 1 if no candidate validates.
_aw::discover_via_arp() {
    local iface cidr
    iface=$(ip -4 route show default 2>/dev/null | awk '/default/ {print $5; exit}')
    [[ -n "$iface" ]] || return 1
    cidr=$(ip -4 -o addr show dev "$iface" 2>/dev/null | awk '{print $4; exit}')
    [[ -n "$cidr" ]] || return 1

    _aw::log "warming ARP cache via ping sweep on $cidr ..."
    _aw::warm_arp_cache "$cidr"

    local hits ip mac
    hits=$(_aw::arp_pi_candidates) || return 1
    [[ -n "$hits" ]] || return 1

    while read -r ip mac; do
        [[ -z "$ip" ]] && continue
        _aw::log "ARP candidate: $ip ($mac) — probing SSH ..."
        if _aw::ssh_banner_is_ubuntu "$ip"; then
            _aw::log "$ip answers Ubuntu OpenSSH — accepting as Pi"
            printf '%s\n' "$ip"
            return 0
        fi
        _aw::warn "$ip:22 not Ubuntu sshd, skipping"
    done <<< "$hits"
    return 1
}

_aw::tcp_probe() {
    local ip=$1 port=${2:-22}
    if command -v nc >/dev/null 2>&1; then
        nc -zw2 "$ip" "$port" >/dev/null 2>&1
    elif command -v ncat >/dev/null 2>&1; then
        ncat -zw2 "$ip" "$port" >/dev/null 2>&1
    else
        # /dev/tcp has no real timeout, but the default connect timeout is
        # short enough for LAN use.  We additionally background + wait so a
        # tarpit can't hang us forever.
        (
            exec 9<>/dev/tcp/"$ip"/"$port"
        ) >/dev/null 2>&1 &
        local pid=$!
        local deadline=$(( $(_aw::now) + 3 ))
        while kill -0 "$pid" 2>/dev/null; do
            if (( $(_aw::now) >= deadline )); then
                kill "$pid" 2>/dev/null || true
                wait "$pid" 2>/dev/null || true
                return 1
            fi
            sleep 1
        done
        wait "$pid"
    fi
}

ansible::discover() {
    local hostname="" timeout=120 forced_ip="" arp_only=0
    while (( $# > 0 )); do
        case "$1" in
            --timeout=*) timeout=${1#*=} ;;
            --ip=*)      forced_ip=${1#*=} ;;
            --arp-only)  arp_only=1 ;;
            --*)         _aw::err "unknown flag: $1"; return 2 ;;
            *)
                if [[ -z $hostname ]]; then hostname=$1
                else _aw::err "unexpected positional: $1"; return 2
                fi
                ;;
        esac
        shift
    done
    hostname=$(_aw::sanitize_hostname "$hostname") || return $?

    local ip
    if [[ -n $forced_ip ]]; then
        if ! _aw::is_ipv4 "$forced_ip"; then
            _aw::err "--ip=$forced_ip is not a valid IPv4 address"
            return "$ANSIBLE_WRAPPER_EXIT_UNSAFE_INPUT"
        fi
        ip=$forced_ip
        _aw::log "skipping mDNS, using supplied IP $ip"
    elif (( arp_only )); then
        _aw::log "--arp-only: skipping mDNS, going straight to ARP scan"
        if ip=$(_aw::discover_via_arp); then
            _aw::log "ARP discovery resolved $hostname -> $ip"
        else
            _aw::err "ARP scan found no Pi-vendor MACs on the LAN"
            _aw::err "hint: confirm the Pi is on the same L2 segment, or pass --ip=<addr>."
            return "$ANSIBLE_WRAPPER_EXIT_DISCOVERY"
        fi
    else
        local fqdn="${hostname}.local"
        # mDNS gets the larger share of the budget (it's the canonical
        # path); ARP fallback gets the rest. With the default 300s wait,
        # that's ~210s of avahi polling then ~90s of ping-sweep + SSH
        # banner probing if mDNS never answers (some routers drop UDP
        # 5353 multicast across VLANs).
        local mdns_budget=$(( timeout * 7 / 10 ))
        (( mdns_budget < 30 )) && mdns_budget=30
        _aw::log "polling mDNS for $fqdn (up to ${mdns_budget}s; press any key to skip → ARP scan)"
        local mstart mdeadline skipped=0
        mstart=$(_aw::now)
        mdeadline=$(( mstart + mdns_budget ))
        while (( $(_aw::now) < mdeadline )); do
            if ip=$(_aw::mdns_resolve "$fqdn" 2>/dev/null); then
                local elapsed=$(( $(_aw::now) - mstart ))
                _aw::log "mDNS resolved $fqdn -> $ip (after ${elapsed}s)"
                break
            fi
            # Interruptible 3-second wait: any keystroke aborts the
            # mDNS phase early. Falls back to plain sleep when stdin
            # isn't a tty (CI / piped invocation).
            if [[ -e /dev/tty ]] && IFS= read -rsN1 -t 3 _key </dev/tty 2>/dev/null; then
                _aw::log "operator interrupt — skipping mDNS, going to ARP scan"
                skipped=1
                break
            elif [[ ! -e /dev/tty ]]; then
                sleep 3
            fi
        done
        if [[ -z "${ip:-}" ]]; then
            (( skipped )) || _aw::warn "mDNS timed out — falling back to ARP scan for Pi-vendor MACs"
            if ip=$(_aw::discover_via_arp); then
                _aw::log "ARP discovery resolved $hostname -> $ip"
            else
                _aw::err "no Pi found via mDNS or ARP scan after ${timeout}s"
                _aw::err "hint: confirm the Pi is on the same L2 segment + powered with"
                _aw::err "      cloud-init done; or pass --ip=<addr> to skip discovery."
                return "$ANSIBLE_WRAPPER_EXIT_DISCOVERY"
            fi
        fi
    fi

    _aw::log "polling SSH on $ip:22 (timeout ${timeout}s)..."
    local start deadline
    start=$(_aw::now)
    deadline=$(( start + timeout ))
    while (( $(_aw::now) < deadline )); do
        if _aw::tcp_probe "$ip" 22; then
            local elapsed=$(( $(_aw::now) - start ))
            _aw::log "SSH reachable after ${elapsed}s"
            _aw::log "next step: pin host key via ssh_keys::host_key_pin '$hostname' '$ip'"
            printf '%s\n' "$ip"
            return 0
        fi
        sleep 2
    done

    _aw::err "SSH on $ip:22 not reachable within ${timeout}s"
    return "$ANSIBLE_WRAPPER_EXIT_DISCOVERY"
}

# ---------------------------------------------------------------------------
# ansible::generate_inventory
# ---------------------------------------------------------------------------

ansible::generate_inventory() {
    if (( $# != 4 )); then
        _aw::err "generate_inventory: need <hostname> <ip> <ssh_key_path> <user>"
        return 2
    fi
    local hostname=$1 ip=$2 key=$3 user=$4
    hostname=$(_aw::sanitize_hostname "$hostname") || return $?

    if ! _aw::is_ipv4 "$ip"; then
        _aw::err "ip is not a valid IPv4 address: $ip"
        return "$ANSIBLE_WRAPPER_EXIT_UNSAFE_INPUT"
    fi
    if [[ ! -r $key ]]; then
        _aw::err "ssh private key not readable: $key"
        return "$ANSIBLE_WRAPPER_EXIT_UNSAFE_INPUT"
    fi
    # User must be a plausible unix login name.
    if [[ ! $user =~ ^[a-z_][a-z0-9_-]{0,31}$ ]]; then
        _aw::err "user is not a valid POSIX login name: $user"
        return "$ANSIBLE_WRAPPER_EXIT_UNSAFE_INPUT"
    fi

    _aw::ensure_tool_home
    local inv_path; inv_path=$(_aw::inventory_path "$hostname")
    local kh;       kh=$(_aw::known_hosts_path)

    # Write atomically via temp file.
    local tmp; tmp=$(mktemp "${inv_path}.XXXXXX")
    chmod 0600 "$tmp"
    cat > "$tmp" <<YAML
---
# Auto-generated by hermod-pi ansible_wrapper.sh
# Host: ${hostname}
# Do NOT commit this file.  Mode 0600.
all:
  vars:
    ansible_user: ${user}
    ansible_ssh_private_key_file: ${key}
    ansible_ssh_common_args: >-
      -o UserKnownHostsFile=${kh}
      -o StrictHostKeyChecking=yes
      -o ConnectTimeout=10
      -o ServerAliveInterval=20
      -o ServerAliveCountMax=3
  children:
    hermod_nodes:
      hosts:
        ${hostname}:
          ansible_host: ${ip}
          hermod_hostname: ${hostname}
YAML
    mv "$tmp" "$inv_path"
    chmod 0600 "$inv_path"

    # Drop a documentation-only vault password example next to it.  We
    # never create a real password file.
    local vp_example="${inv_path%.hosts.yml}.vault-password.example"
    cat > "$vp_example" <<'EOF'
# Example ansible-vault setup for hermod-pi.
# To add sudo become passwords or secrets for this host:
#
#   1. Create a password file (NOT in this directory):
#        touch ~/.hermod-pi-vault && chmod 0600 ~/.hermod-pi-vault
#        echo 'my-long-random-passphrase' > ~/.hermod-pi-vault
#
#   2. Encrypt a variables file:
#        ansible-vault create \
#          --vault-password-file ~/.hermod-pi-vault \
#          <HERMOD_PI_HOME>/inventories/<hostname>.vault.yml
#      Put secrets like ansible_become_password inside.
#
#   3. Tell hermod-pi to use it:
#        export ANSIBLE_VAULT_PASSWORD_FILE=~/.hermod-pi-vault
#        hermod-pi install <hostname>
#
# NEVER pass ansible_become_password via -e on the command line.
EOF
    chmod 0644 "$vp_example"

    _aw::log "wrote inventory: $inv_path"
    printf '%s\n' "$inv_path"
}

# ---------------------------------------------------------------------------
# ansible::ssh_opts
# ---------------------------------------------------------------------------

ansible::ssh_opts() {
    if (( $# != 1 )); then
        _aw::err "ssh_opts: need <hostname>"
        return 2
    fi
    local hostname=$1
    hostname=$(_aw::sanitize_hostname "$hostname") || return $?
    local inv; inv=$(_aw::inventory_path "$hostname")
    if [[ ! -r $inv ]]; then
        _aw::err "no inventory for '$hostname' at $inv"
        return "$ANSIBLE_WRAPPER_EXIT_NO_INVENTORY"
    fi
    # Extract key + user with a tiny python parser; we already require python3.
    local key user
    key=$(HERMOD_INV="$inv" python3 -c '
import os, yaml
d = yaml.safe_load(open(os.environ["HERMOD_INV"]))
print(d["all"]["vars"]["ansible_ssh_private_key_file"])
') || { _aw::err "failed to parse inventory"; return 1; }
    user=$(HERMOD_INV="$inv" python3 -c '
import os, yaml
d = yaml.safe_load(open(os.environ["HERMOD_INV"]))
print(d["all"]["vars"]["ansible_user"])
')
    local kh; kh=$(_aw::known_hosts_path)
    printf -- '-i %s -o UserKnownHostsFile=%s -o StrictHostKeyChecking=yes -o ConnectTimeout=10 -l %s\n' \
        "$key" "$kh" "$user"
}

# ---------------------------------------------------------------------------
# ansible::run_playbook
# ---------------------------------------------------------------------------

# Known playbooks we expose.  New entries land here, not in main CLI.
_AW_KNOWN_PLAYBOOKS=(install verify uninstall update)

_aw::is_known_playbook() {
    local name=$1 p
    for p in "${_AW_KNOWN_PLAYBOOKS[@]}"; do
        [[ $p == "$name" ]] && return 0
    done
    return 1
}

_aw::audit() {
    local ts playbook hostname rc duration
    ts=$(date -u +%Y-%m-%dT%H:%M:%SZ)
    playbook=$1; hostname=$2; rc=$3; duration=$4
    _aw::ensure_tool_home
    printf '%s\tplaybook=%s\thost=%s\trc=%s\tduration_sec=%s\n' \
        "$ts" "$playbook" "$hostname" "$rc" "$duration" \
        >> "$(_aw::audit_log_path)"
}

ansible::run_playbook() {
    if (( $# < 2 )); then
        _aw::err "run_playbook: need <playbook_name> <hostname> [extra-vars...]"
        return 2
    fi
    local playbook=$1 hostname=$2
    shift 2
    local extra_args=("$@")

    hostname=$(_aw::sanitize_hostname "$hostname") || return $?

    if ! _aw::is_known_playbook "$playbook"; then
        _aw::err "unknown playbook: $playbook (known: ${_AW_KNOWN_PLAYBOOKS[*]})"
        return "$ANSIBLE_WRAPPER_EXIT_NO_PLAYBOOK"
    fi

    local adir; adir=$(_aw::ansible_dir) || {
        _aw::err "cannot locate ansible/ directory (set HERMOD_ANSIBLE_DIR)"
        return "$ANSIBLE_WRAPPER_EXIT_NO_PLAYBOOK"
    }
    local pb_path="$adir/playbooks/${playbook}.yml"
    if [[ ! -r $pb_path ]]; then
        _aw::err "playbook not found: $pb_path"
        return "$ANSIBLE_WRAPPER_EXIT_NO_PLAYBOOK"
    fi

    local inv; inv=$(_aw::inventory_path "$hostname")
    if [[ ! -r $inv ]]; then
        _aw::err "inventory not found for '$hostname': $inv"
        _aw::err "run: hermod-pi generate-inventory $hostname <ip> <key> <user>"
        return "$ANSIBLE_WRAPPER_EXIT_NO_INVENTORY"
    fi

    if ! _aw::has_pinned_host_key "$hostname"; then
        _aw::err "no TOFU-pinned host key for '$hostname' in $(_aw::known_hosts_path)"
        _aw::err "refuse to run ansible without a pinned key."
        _aw::err "run: hermod-pi pin-host-key $hostname <ip>   (invokes ssh_keys::host_key_pin)"
        return "$ANSIBLE_WRAPPER_EXIT_NO_TOFU"
    fi

    # Reject obviously unsafe extra-vars: bare ansible_become_password=...
    # on the command line defeats the ansible-vault rule.
    local v
    for v in "${extra_args[@]}"; do
        case "$v" in
            *ansible_become_password=*|*ansible_password=*)
                _aw::err "refusing to pass '$v' on command line (use ansible-vault)"
                return "$ANSIBLE_WRAPPER_EXIT_UNSAFE_INPUT"
                ;;
        esac
    done

    # Override repo-level ansible.cfg permissiveness for the duration of
    # this invocation.
    local kh; kh=$(_aw::known_hosts_path)
    export ANSIBLE_HOST_KEY_CHECKING=True
    export ANSIBLE_SSH_ARGS="-o ControlMaster=auto -o ControlPersist=60s -o StrictHostKeyChecking=yes -o UserKnownHostsFile=${kh}"

    local cmd=(
        "${HERMOD_PI_ANSIBLE_PLAYBOOK:-ansible-playbook}"
        -i "$inv"
        "$pb_path"
        -e "hermod_hostname=$hostname"
        -e "hermod_pi_tool_version=$HERMOD_PI_TOOL_VERSION"
    )
    # Our inventory lives outside the Hermod repo, so ansible doesn't auto-load
    # group_vars/all.yml. Pass it as a vars file so all.yml defaults apply.
    local group_all="$adir/group_vars/all.yml"
    if [[ -f $group_all ]]; then
        cmd+=(-e "@$group_all")
    fi
    for v in "${extra_args[@]}"; do
        cmd+=(-e "$v")
    done
    cmd+=(--diff)

    _aw::log "running: ${cmd[*]}"
    _aw::log "cwd: $adir"

    local start end rc=0
    start=$(_aw::now)

    # Must cd into the ansible dir because ansible.cfg's roles_path is
    # relative.  Stream output live (no command substitution).
    if [[ -n ${HERMOD_PI_ANSIBLE_DRY_RUN:-} ]]; then
        # Test-only escape: echo command to stdout instead of executing.
        printf '%s\n' "${cmd[*]}"
        rc=0
    else
        ( cd "$adir" && "${cmd[@]}" )
        rc=$?
    fi

    end=$(_aw::now)
    _aw::audit "$playbook" "$hostname" "$rc" "$((end - start))"

    if (( rc != 0 )); then
        _aw::err "ansible-playbook exited with rc=$rc"
        _aw::err "to resume at a specific failed task:"
        _aw::err "  (cd $adir && ansible-playbook -i $inv $pb_path --start-at-task='<task name>' --diff)"
    fi
    return $rc
}

# ---------------------------------------------------------------------------
# Convenience wrappers
# ---------------------------------------------------------------------------

ansible::install() {
    if (( $# < 1 )); then _aw::err "install: need <hostname>"; return 2; fi
    ansible::run_playbook install "$@"
}

ansible::verify() {
    if (( $# < 1 )); then _aw::err "verify: need <hostname>"; return 2; fi
    local hostname=$1
    shift
    ansible::run_playbook verify "$hostname" "$@"
    local rc=$?
    if (( rc == 0 )); then
        _aw::log "verify: all healthcheck tasks passed"
    else
        _aw::err "verify: one or more healthcheck tasks FAILED (rc=$rc)"
        _aw::err "scan output above for 'fatal:' or 'FAILED!' lines to find the failing task."
    fi
    return $rc
}

ansible::uninstall() {
    if (( $# < 1 )); then _aw::err "uninstall: need <hostname>"; return 2; fi
    local hostname=$1
    shift
    # The uninstall.yml playbook already refuses without confirm_uninstall=yes,
    # but we add an interactive gate too unless HERMOD_PI_ASSUME_YES is set.
    if [[ -z ${HERMOD_PI_ASSUME_YES:-} ]]; then
        printf 'This will DELETE the Hermod stack + all PVCs on %s.\nType YES to continue: ' "$hostname" >&2
        local reply
        read -r reply
        if [[ $reply != YES ]]; then
            _aw::err "uninstall aborted"
            return 1
        fi
    fi
    ansible::run_playbook uninstall "$hostname" confirm_uninstall=yes "$@"
}

# ---------------------------------------------------------------------------
# Library-safe entrypoint
# ---------------------------------------------------------------------------

# If this file is being executed (not sourced), dispatch to a subcommand.
# When sourced, BASH_SOURCE[0] != $0 and we do nothing.
_aw::main() {
    local subcmd=${1:-}
    shift || true
    case "$subcmd" in
        check-deps)          ansible::check_deps "$@" ;;
        discover)            ansible::discover "$@" ;;
        generate-inventory)  ansible::generate_inventory "$@" ;;
        run)                 ansible::run_playbook "$@" ;;
        install)             ansible::install "$@" ;;
        verify)              ansible::verify "$@" ;;
        uninstall)           ansible::uninstall "$@" ;;
        ssh-opts)            ansible::ssh_opts "$@" ;;
        test)                _aw::run_tests "$@" ;;
        ''|-h|--help)
            cat <<EOF
ansible_wrapper.sh - library and self-test driver.

Subcommands (also available as ansible::* shell functions when sourced):
  check-deps
  discover           <hostname> [--timeout=N] [--ip=ADDR]
  generate-inventory <hostname> <ip> <key> <user>
  run                <playbook> <hostname> [extra-vars...]
  install            <hostname>
  verify             <hostname>
  uninstall          <hostname>
  ssh-opts           <hostname>
  test               run inline self-tests
EOF
            ;;
        *)
            _aw::err "unknown subcommand: $subcmd"
            return 2
            ;;
    esac
}

# ---- tests ----
#
# TAP-ish self-test suite.  Runnable via ``bash ansible_wrapper.sh test``.
# Covers the pure-shell units (inventory, path discovery, TOFU gate,
# playbook command construction).  Does not hit the network.

_AW_TESTS_PASS=0
_AW_TESTS_FAIL=0
_AW_TESTS_PLAN=0

_aw::t_ok()    { _AW_TESTS_PASS=$((_AW_TESTS_PASS+1)); printf 'ok %d - %s\n' "$((_AW_TESTS_PASS+_AW_TESTS_FAIL))" "$1"; }
_aw::t_fail()  { _AW_TESTS_FAIL=$((_AW_TESTS_FAIL+1)); printf 'not ok %d - %s\n' "$((_AW_TESTS_PASS+_AW_TESTS_FAIL))" "$1"; shift; for l in "$@"; do printf '# %s\n' "$l"; done; }
_aw::t_diag()  { printf '# %s\n' "$*"; }

_aw::t_assert_eq() {
    local got=$1 want=$2 name=$3
    if [[ $got == "$want" ]]; then
        _aw::t_ok "$name"
    else
        _aw::t_fail "$name" "want: $want" "got:  $got"
    fi
}

_aw::t_assert_rc() {
    local actual=$1 want=$2 name=$3
    if (( actual == want )); then
        _aw::t_ok "$name"
    else
        _aw::t_fail "$name" "want rc=$want, got rc=$actual"
    fi
}

_aw::run_tests() {
    _AW_TESTS_PLAN=12
    printf '1..%d\n' "$_AW_TESTS_PLAN"

    local tmp; tmp=$(mktemp -d)
    # shellcheck disable=SC2064  # expand $tmp now, not at trap time
    trap "rm -rf '$tmp'" EXIT
    export HERMOD_PI_HOME="$tmp/state"

    # Fixture SSH key (content doesn't matter, file just needs to exist).
    local fake_key="$tmp/fake_key"
    : > "$fake_key"; chmod 0600 "$fake_key"

    # -----------------------------------------------------------------
    # 1 & 2. sanitize_hostname: accept good, reject path traversal.
    # -----------------------------------------------------------------
    local rc=0 out
    out=$(_aw::sanitize_hostname "rpi5-kitchen" 2>/dev/null) || rc=$?
    _aw::t_assert_eq "$out:$rc" "rpi5-kitchen:0" "sanitize accepts rpi5-kitchen"

    rc=0
    _aw::sanitize_hostname "../../../etc/passwd" >/dev/null 2>&1 || rc=$?
    _aw::t_assert_rc "$rc" "$ANSIBLE_WRAPPER_EXIT_UNSAFE_INPUT" "sanitize rejects path traversal"

    # -----------------------------------------------------------------
    # 3. ansible_dir discovery walks up.
    # -----------------------------------------------------------------
    local fake_root="$tmp/proj"
    mkdir -p "$fake_root/ansible" "$fake_root/tools/sub/dir"
    : > "$fake_root/ansible/ansible.cfg"
    local probe="$fake_root/tools/sub/dir/foo.sh"
    : > "$probe"
    out=$(_aw::find_ansible_dir "$probe")
    _aw::t_assert_eq "$out" "$fake_root/ansible" "ansible_dir walk-up discovery"

    # -----------------------------------------------------------------
    # 4. generate_inventory + YAML parses.
    # -----------------------------------------------------------------
    out=$(ansible::generate_inventory "rpi5" "192.168.1.42" "$fake_key" "hermod" 2>/dev/null)
    local inv_path=$out
    if [[ -f $inv_path ]]; then
        _aw::t_ok "inventory file created"
    else
        _aw::t_fail "inventory file created" "not found: $inv_path"
    fi

    local mode; mode=$(stat -c '%a' "$inv_path" 2>/dev/null || stat -f '%Lp' "$inv_path")
    _aw::t_assert_eq "$mode" "600" "inventory mode is 0600"

    if python3 -c "import yaml,sys; yaml.safe_load(open('$inv_path'))" 2>/dev/null; then
        _aw::t_ok "inventory is valid YAML"
    else
        _aw::t_fail "inventory is valid YAML"
    fi

    # -----------------------------------------------------------------
    # 5. discover --ip= bypass skips mDNS.  Probe a closed port so SSH
    #    check fails quickly, but we want to prove mDNS wasn't consulted
    #    (no failure message referencing mDNS).
    # -----------------------------------------------------------------
    rc=0
    local disc_err
    disc_err=$(ansible::discover "rpi5" --ip=127.0.0.1 --timeout=2 2>&1 >/dev/null) || rc=$?
    # Bypass is proven by the log line "skipping mDNS, using supplied IP"
    # and by the absence of any "resolving ... via mDNS" probe message.
    if (( rc != 0 )) \
       && grep -q "skipping mDNS" <<< "$disc_err" \
       && ! grep -q "resolving .* via mDNS" <<< "$disc_err"; then
        _aw::t_ok "discover --ip= bypasses mDNS"
    else
        _aw::t_fail "discover --ip= bypasses mDNS" "rc=$rc" "stderr=$disc_err"
    fi

    # -----------------------------------------------------------------
    # 6. run_playbook without TOFU entry refuses, correct exit code.
    # -----------------------------------------------------------------
    mkdir -p "$(_aw::known_hosts_path | xargs dirname)"
    : > "$(_aw::known_hosts_path)"   # exists but empty
    # Need an ansible dir fixture; reuse fake_root but add a playbook.
    mkdir -p "$fake_root/ansible/playbooks"
    : > "$fake_root/ansible/playbooks/install.yml"
    export HERMOD_ANSIBLE_DIR="$fake_root/ansible"

    rc=0
    ansible::run_playbook install "rpi5" >/dev/null 2>&1 || rc=$?
    _aw::t_assert_rc "$rc" "$ANSIBLE_WRAPPER_EXIT_NO_TOFU" "run_playbook refuses without TOFU entry"

    # -----------------------------------------------------------------
    # 7. Unknown playbook refused.
    # -----------------------------------------------------------------
    rc=0
    ansible::run_playbook totally-fake "rpi5" >/dev/null 2>&1 || rc=$?
    _aw::t_assert_rc "$rc" "$ANSIBLE_WRAPPER_EXIT_NO_PLAYBOOK" "run_playbook rejects unknown playbook"

    # -----------------------------------------------------------------
    # 8. Command construction.  Stamp a TOFU entry for rpi5 and set
    #    HERMOD_PI_ANSIBLE_DRY_RUN so run_playbook echoes the argv.
    # -----------------------------------------------------------------
    # Generate a throwaway host key entry in the correct format.
    # ssh-keygen -F needs a real line; the simplest is to write a
    # hashed entry with ssh-keygen -H over a seed line.
    local seed kh; kh=$(_aw::known_hosts_path)
    seed="rpi5 ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
    printf '%s\n' "$seed" > "$kh"
    ssh-keygen -H -f "$kh" >/dev/null 2>&1 || true
    rm -f "${kh}.old"

    export HERMOD_PI_ANSIBLE_DRY_RUN=1
    export HERMOD_PI_TOOL_VERSION="1.2.3-test"
    local got_cmd
    got_cmd=$(ansible::run_playbook install "rpi5" "hermod_git_remote=git@example.com:me/hermod.git" 2>/dev/null)
    if [[ $got_cmd == *"ansible-playbook"* \
       && $got_cmd == *"-i $(_aw::inventory_path rpi5)"* \
       && $got_cmd == *"$fake_root/ansible/playbooks/install.yml"* \
       && $got_cmd == *"-e hermod_hostname=rpi5"* \
       && $got_cmd == *"-e hermod_pi_tool_version=1.2.3-test"* \
       && $got_cmd == *"-e hermod_git_remote=git@example.com:me/hermod.git"* \
       && $got_cmd == *"--diff"* ]]; then
        _aw::t_ok "run_playbook constructs correct command line"
    else
        _aw::t_fail "run_playbook constructs correct command line" "got: $got_cmd"
    fi
    unset HERMOD_PI_ANSIBLE_DRY_RUN

    # -----------------------------------------------------------------
    # 9. Refuse become password on command line.
    # -----------------------------------------------------------------
    export HERMOD_PI_ANSIBLE_DRY_RUN=1
    rc=0
    ansible::run_playbook install "rpi5" "ansible_become_password=hunter2" >/dev/null 2>&1 || rc=$?
    _aw::t_assert_rc "$rc" "$ANSIBLE_WRAPPER_EXIT_UNSAFE_INPUT" "refuses ansible_become_password on CLI"
    unset HERMOD_PI_ANSIBLE_DRY_RUN

    # -----------------------------------------------------------------
    # 10. ssh_opts output format.
    # -----------------------------------------------------------------
    out=$(ansible::ssh_opts "rpi5")
    if [[ $out == *"-i $fake_key"* \
       && $out == *"-o UserKnownHostsFile=$(_aw::known_hosts_path)"* \
       && $out == *"-o StrictHostKeyChecking=yes"* \
       && $out == *"-l hermod"* ]]; then
        _aw::t_ok "ssh_opts formatted as expected"
    else
        _aw::t_fail "ssh_opts formatted as expected" "got: $out"
    fi

    # -----------------------------------------------------------------
    # Summary.
    # -----------------------------------------------------------------
    local total=$((_AW_TESTS_PASS + _AW_TESTS_FAIL))
    _aw::t_diag "plan=$_AW_TESTS_PLAN ran=$total pass=$_AW_TESTS_PASS fail=$_AW_TESTS_FAIL"
    (( _AW_TESTS_FAIL == 0 )) || return 1
    return 0
}

# Dispatch only when executed directly.
if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
    _aw::main "$@"
fi
