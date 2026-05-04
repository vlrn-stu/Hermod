#!/usr/bin/env bash
# cmd-pi.sh — Pi greenfield provisioning subcommands.
#
# Thin delegates to lib/pi-installer/hermod-pi. That tool owns the heavy
# lifting (image-prep container, mDNS discover, ansible wrapper, TOFU
# host-key pin, dedicated ed25519 keypair per Pi); hermod.sh is the
# single operator entry point.
#
# Sourced by hermod.sh once $REPO_ROOT is set + lib/lib.sh is loaded.

[[ -n "${_HERMOD_CMD_PI_LOADED:-}" ]] && return 0
_HERMOD_CMD_PI_LOADED=1

_pi_installer_path() {
    local p="$REPO_ROOT/lib/pi-installer/hermod-pi"
    [[ -x "$p" ]] || _die "pi-installer missing or not executable: $p"
    printf '%s\n' "$p"
}

# Stage 1 of greenfield Pi5 provisioning: build a customised cloud-init
# image from <config.yaml>, prompt for the SD card, write + verify + eject
# via pkexec/osascript (no rpi-imager required by default).
cmd_flash() {
    local tool; tool=$(_pi_installer_path)
    "$tool" flash "$@"
}

# Stage 2a: mDNS-discover the Pi at <hostname>, TOFU-pin its host key into
# ~/.hermod-pi/known_hosts, wait for the cloud-init first-boot marker.
# When wait succeeds the discovered IP also lands in the inventory; we
# then refresh ~/.config/hermod/config from that inventory so the next
# `install prod-pi` uses the actual lease the Pi grabbed (DHCP can hand
# out a different IP than the operator was assuming).
cmd_wait_pi() {
    local tool; tool=$(_pi_installer_path)
    "$tool" wait "$@" || return $?
    if cmd_config init >/dev/null 2>&1; then
        _ok "operator config refreshed from new inventory"
    else
        _warn "could not auto-refresh operator config; run 'hermod.sh config init' manually"
    fi
}

# Stage 1+2 in one go: flash → prompt for power-on → wait → ansible
# install → ansible verify. End-to-end greenfield bring-up.
cmd_provision() {
    local tool; tool=$(_pi_installer_path)
    "$tool" all "$@"
}

# Runs the ansible verify playbook (DIFFERENT from cluster `status`,
# which only checks pod state). Health-checks the host: kernel cmdline,
# cgroup v2, microk8s version, addon list, hermod-prod ns presence, etc.
cmd_pi_status() {
    local tool; tool=$(_pi_installer_path)
    "$tool" status "$@"
}

# Runs the ansible uninstall playbook on the Pi. DESTRUCTIVE: removes
# microk8s + Hermod state. Use when retiring or repurposing the host.
# NOT the same as `hermod.sh teardown prod-pi` (which only deletes the
# k8s overlay + leaves microk8s + the OS intact).
cmd_pi_uninstall() {
    local tool; tool=$(_pi_installer_path)
    "$tool" uninstall "$@"
}

# Lists every Pi keypair under ~/.hermod-pi/keys/.
cmd_pi_keys() {
    local tool; tool=$(_pi_installer_path)
    "$tool" list "$@"
}

# Pi-installer's own dependency check (jq, ssh-keygen, ansible-playbook,
# rpi-imager OR podman/docker, mDNS resolver). Distinct from the cluster
# `doctor` which checks kubectl/rsync/ssh.
cmd_pi_doctor() {
    local tool; tool=$(_pi_installer_path)
    "$tool" config-check "$@"
}
