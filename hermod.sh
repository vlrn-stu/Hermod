#!/usr/bin/env bash
# hermod.sh — unified Hermod ops CLI.
#
# Subcommands:
#   compose <up|down|logs|restart|status|build|pull> [svc]
#                          Single-host docker compose stack (this machine).
#   install <target>       Provision target from scratch.
#   update  <target>       Rsync code → target + reapply manifests + rolling restart.
#   status  <target>       Show pod status for target.
#   doctor                 Verify host has the dependencies each path needs.
#   menu  (or no args)     Interactive TUI menu.
#
# Targets:
#   prod-pi        Raspberry Pi 5 (microk8s). SSH coordinates from
#                  HERMOD_PI_SSH_HOST + HERMOD_PI_SSH_KEY in
#                  ~/.config/hermod/config or hermod-prod.env.
#   prod-kind      Local kind cluster on the developer laptop.
#   prod-pi-letsencrypt[-ingress|-cloudflare-tunnel[-active]|-cloudflare-zero-trust]
#                  Edge-TLS overlays applied on top of an existing prod-pi
#                  install.
#   The legacy pi5-live and kind-hermod target names point at overlays
#   (overlays/dev-hardware, overlays/dev) that no longer exist; they are
#   kept only so old shell history fails fast with a deprecation message.
#
# Runs on Linux, macOS, WSL2, and git-bash on Windows. The compose path needs
# only docker; the k8s targets additionally need kubectl, ssh, and rsync.
#
# =============================================================================
# SECURITY WARNING — DEV / EVALUATION DEPLOYMENT, NOT PRODUCTION-HARDENED
# -----------------------------------------------------------------------------
# The compose path runs with:
#   * NanoMQ MQTT broker:    allow_anonymous=true, no_match=allow, plaintext 1883.
#   * Mosquitto wifi bridge: anonymous, plaintext 1883/1884.
#   * Coordinator + vault42: HTTP only, no TLS.
#   * Default DB / MQTT passwords baked into docker-compose.yaml.
# Bind these ports ONLY to host loopback or a trusted LAN segment.
# Do NOT expose 1883 / 1884 / 8080 / 8081 / 8083 / 42069 to the public
# internet. Production hardening (mTLS, per-service ACLs, real Vault42)
# is documented in docs/TODO.md and is out of scope for the compose path.
#
# ZIGBEE — NOT enabled by default. Adapter family (ember / zstack / deconz /
# ezsp), device path (/dev/ttyACM0, /dev/ttyAMA0, /dev/ttyUSB0), and host-
# side USB passthrough (Linux native; Windows/macOS need WSL2 + usbipd) all
# vary too much to ship one default. See docker-compose.yaml + INSTALL.md.
# =============================================================================

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" >/dev/null && pwd)"
readonly REPO_ROOT

# ── Pi SSH coordinates ──────────────────────────────────────────────────────
# Source from hermod-prod.env (operator vault, gitignored) so the committed
# script ships generic. Operator sets HERMOD_PI_SSH_HOST + HERMOD_PI_SSH_KEY
# in their env file (or exports them); resolve_target then composes the
# ssh_host / ssh_key values for every Pi-flavoured target without baking
# any specific operator's IP / key filename into version control.
#
# Falls back to PLACEHOLDER values that cmd_doctor flags so a fresh checkout
# fails with a clear "set HERMOD_PI_SSH_HOST" message rather than hanging on
# someone else's IP.
#
# Load order (later overrides earlier):
#   1. ~/.config/hermod/config        global, machine-wide (managed by `hermod.sh config`)
#   2. ./hermod-prod.env              per-repo, gitignored
#   3. shell environment              caller-exported vars survive `set -a` re-source
HERMOD_GLOBAL_CONFIG="${HERMOD_GLOBAL_CONFIG:-${XDG_CONFIG_HOME:-$HOME/.config}/hermod/config}"
if [[ -f "$HERMOD_GLOBAL_CONFIG" ]]; then
    # shellcheck disable=SC1090
    set -a; source "$HERMOD_GLOBAL_CONFIG"; set +a
fi
_hermod_env_file="${HERMOD_PROD_ENV:-$REPO_ROOT/hermod-prod.env}"
if [[ -f "$_hermod_env_file" ]]; then
    # shellcheck disable=SC1090
    set -a; source "$_hermod_env_file"; set +a
elif [[ -f "$_hermod_env_file.mimir" ]]; then
    # Encrypted vault present, plaintext absent — pull through mimir_load.
    # If the cache is warm or the .meta says no PIN, this is silent;
    # otherwise mimir_load prompts on /dev/tty.
    # shellcheck disable=SC1091
    source "$REPO_ROOT/lib/mimir.sh"
    if mimir_load "$_hermod_env_file" --source 2>/dev/null; then
        :  # successfully sourced
    else
        # Defer the failure: cmd_doctor + cmd_help should still run when
        # the vault is locked + no PIN supplied. Subcommands that need
        # the env (install, secrets, ensure-secrets) re-call mimir_load
        # later and surface a clean prompt then.
        :
    fi
fi
unset _hermod_env_file
PI_SSH_USER="${HERMOD_PI_SSH_USER:-hermod}"
PI_SSH_HOST="${HERMOD_PI_SSH_HOST:-PLACEHOLDER_PI_IP}"
PI_SSH_KEY="${HERMOD_PI_SSH_KEY:-$HOME/.hermod-pi/keys/PLACEHOLDER_HOSTNAME.key}"
PI_KNOWN_HOSTS="${HERMOD_PI_KNOWN_HOSTS:-$HOME/.hermod-pi/known_hosts}"
readonly PI_SSH_USER PI_SSH_HOST PI_SSH_KEY PI_KNOWN_HOSTS

# ── shared helpers ──────────────────────────────────────────────────────────
# OS / colour / logging / env-vault helpers all live in lib/lib.sh so the
# TUI and any future entrypoint can reuse them. Sourced before any usage.
# shellcheck disable=SC1091
source "$REPO_ROOT/lib/lib.sh"

# ── compose subcommand ──────────────────────────────────────────────────────
# Implementation lives in lib/cmd-compose.sh — pure mock-verification path.
# shellcheck disable=SC1091
source "$REPO_ROOT/lib/cmd-compose.sh"

# mimir.sh defines mimir_load / mimir_init etc. cmd-users.sh uses them
# transparently when the operator has encrypted seed-users.json; sourcing
# unconditionally so the helpers are always callable, not just when the
# .env vault is itself encrypted.
# shellcheck disable=SC1091
source "$REPO_ROOT/lib/mimir.sh"

# Implementation lives in lib/cmd-users.sh — local seed-users.json
# editor; pushed to vault42-seed-credentials Secret on install/update.
# shellcheck disable=SC1091
source "$REPO_ROOT/lib/cmd-users.sh"

# ── target registry ─────────────────────────────────────────────────────────
# Eval'd by install/update/status to populate locals: kind, ssh_host, ssh_key,
# known_hosts, kube_context, install_path, overlay, namespace.
resolve_target() {
    case "$1" in
        pi5-live|kind-hermod)
            # Legacy target names whose overlays (overlays/dev-hardware,
            # overlays/dev) were removed during the prod-* refactor. Fail
            # loudly here instead of producing config that breaks at apply.
            _die "target '$1' is deprecated; use 'prod-pi' or 'prod-kind' (see hermod.sh help)"
            ;;
        prod-kind)
            cat <<EOF
kind=pc-kind-prod
kube_context=kind-hermod
install_path=$REPO_ROOT
overlay=kubernetes/overlays/prod
namespace=hermod-prod
EOF
            ;;
        prod-pi)
            cat <<EOF
kind=pi-microk8s-prod
ssh_host=${PI_SSH_USER}@${PI_SSH_HOST}
ssh_key=${PI_SSH_KEY}
known_hosts=${PI_KNOWN_HOSTS}
kube_context=pi5-live
install_path=/opt/hermod
overlay=kubernetes/overlays/prod-pi
namespace=hermod-prod
EOF
            ;;
        # Edge-TLS variants. All inherit the prod-pi target host but swap
        # the kustomize overlay. Passive variants only acquire/install
        # resources; active variants expose traffic publicly and require
        # HERMOD_GO_LIVE=YES to install.
        prod-pi-letsencrypt)
            cat <<EOF
kind=pi-microk8s-prod-edge
ssh_host=${PI_SSH_USER}@${PI_SSH_HOST}
ssh_key=${PI_SSH_KEY}
known_hosts=${PI_KNOWN_HOSTS}
kube_context=pi5-live
install_path=/opt/hermod
overlay=kubernetes/overlays/prod-pi-letsencrypt
namespace=hermod-prod
go_live=0
EOF
            ;;
        prod-pi-letsencrypt-ingress)
            cat <<EOF
kind=pi-microk8s-prod-edge
ssh_host=${PI_SSH_USER}@${PI_SSH_HOST}
ssh_key=${PI_SSH_KEY}
known_hosts=${PI_KNOWN_HOSTS}
kube_context=pi5-live
install_path=/opt/hermod
overlay=kubernetes/overlays/prod-pi-letsencrypt-ingress
namespace=hermod-prod
go_live=1
EOF
            ;;
        prod-pi-letsencrypt-cloudflare-tunnel)
            cat <<EOF
kind=pi-microk8s-prod-edge
ssh_host=${PI_SSH_USER}@${PI_SSH_HOST}
ssh_key=${PI_SSH_KEY}
known_hosts=${PI_KNOWN_HOSTS}
kube_context=pi5-live
install_path=/opt/hermod
overlay=kubernetes/overlays/prod-pi-letsencrypt-cloudflare-tunnel
namespace=hermod-prod
go_live=0
EOF
            ;;
        prod-pi-letsencrypt-cloudflare-tunnel-active)
            cat <<EOF
kind=pi-microk8s-prod-edge
ssh_host=${PI_SSH_USER}@${PI_SSH_HOST}
ssh_key=${PI_SSH_KEY}
known_hosts=${PI_KNOWN_HOSTS}
kube_context=pi5-live
install_path=/opt/hermod
overlay=kubernetes/overlays/prod-pi-letsencrypt-cloudflare-tunnel-active
namespace=hermod-prod
go_live=1
EOF
            ;;
        prod-pi-cloudflare-zero-trust)
            cat <<EOF
kind=pi-microk8s-prod-edge
ssh_host=${PI_SSH_USER}@${PI_SSH_HOST}
ssh_key=${PI_SSH_KEY}
known_hosts=${PI_KNOWN_HOSTS}
kube_context=pi5-live
install_path=/opt/hermod
overlay=kubernetes/overlays/prod-pi-cloudflare-zero-trust
namespace=hermod-prod
go_live=1
EOF
            ;;
        *) _die "unknown target: $1
  Production:  prod-pi (Raspberry Pi 5)  |  prod-kind (PC kind sandbox)
  Edge-TLS:    prod-pi-letsencrypt           prod-pi-letsencrypt-ingress
               prod-pi-letsencrypt-cloudflare-tunnel
               prod-pi-letsencrypt-cloudflare-tunnel-active
               prod-pi-cloudflare-zero-trust
  Legacy:      pi5-live, kind-hermod  (deprecated; resolve_target dies on these)" ;;
    esac
}

_ssh() {
    ssh -i "$ssh_key" -o UserKnownHostsFile="$known_hosts" \
        -o IdentitiesOnly=yes -o StrictHostKeyChecking=yes \
        "$ssh_host" "$@"
}

# ── seed-users push helpers ────────────────────────────────────────────────
# Push the operator's local seed-users.json (managed by `hermod.sh users`)
# to a temp file on the Pi so ensure-secrets.sh can consume it via
# HERMOD_USERS_SEED_JSON_FILE. Returns 0 (and prints the remote path) when
# a seed was pushed; returns 1 silently when no local seed exists. Cleanup
# is the caller's responsibility (paired _users_clean_pi_seed call at the
# end of the install/update step).
#
# Caller MUST have eval'd resolve_target so $ssh_key + $known_hosts +
# $ssh_host are in scope (same contract as _ssh).
_HERMOD_PI_SEED_PATH="/tmp/hermod-seed-users.json"
_users_push_seed_to_pi() {
    [[ -f "$HERMOD_USERS_FILE" || -f "${HERMOD_USERS_FILE}.mimir" ]] || return 1
    local json
    json="$(users_dump_seed_json 2>/dev/null)" || return 1
    [[ -n "$json" ]] || return 1
    printf '%s' "$json" \
        | _ssh "umask 077 && cat > $_HERMOD_PI_SEED_PATH" \
        || return 1
    printf '%s\n' "$_HERMOD_PI_SEED_PATH"
}

_users_clean_pi_seed() {
    _ssh "rm -f $_HERMOD_PI_SEED_PATH" 2>/dev/null || true
}

# Rebuild a single service image on the Pi and side-load it into
# microk8s. Caller has already eval'd resolve_target so $install_path
# and the SSH helpers are in scope. The two services we ship today are
# the only valid svc names — adding a third means extending this case
# AND the cmd_install build block (search for `docker build -t`).
_pi_build_image() {
    local svc="$1"
    local image dockerfile
    case "$svc" in
        coord)
            image="hermod-coordinator:latest"
            dockerfile="src/Hermod.Coordinator/Dockerfile" ;;
        lora2mqtt)
            image="lora2mqtt:latest"
            dockerfile="src/LoRa2MQTT/LoRa2MQTT.Service/Dockerfile" ;;
        *) _die "_pi_build_image: unknown service '$svc'" ;;
    esac
    _log "rebuild ($svc): build $image on Pi"
    # Runtime feature-detect: prefer docker (with BuildKit if buildx is
    # present), fall back to podman (CLI is drop-in compatible for
    # build/save). Either way the resulting image is saved as a tarball
    # and side-loaded into microk8s' containerd via `ctr images import`,
    # so neither runtime needs to be the host's k8s runtime.
    #
    # Tag dance: podman saves unqualified images with an implicit
    # `localhost/` prefix, which `ctr images import` honours verbatim.
    # K8s manifests reference the bare `<image>:latest` form, which
    # containerd resolves through `docker.io/library/`. Without the
    # explicit re-tag the bare and `docker.io/library/` references
    # keep pointing at whatever stale SHA was imported FIRST, and the
    # rollout-restart below brings up pods on the old image — silent,
    # because the rollout itself reports success. Force-tag the freshly
    # built SHA over both references on every build.
    _ssh "cd $install_path && \
        if command -v docker >/dev/null 2>&1; then \
            RUNTIME=docker; \
            if docker buildx version >/dev/null 2>&1; then \
                BUILD='DOCKER_BUILDKIT=1 docker build --progress=plain'; \
            else \
                BUILD='docker build'; \
            fi; \
        elif command -v podman >/dev/null 2>&1; then \
            RUNTIME=podman; \
            BUILD='podman build'; \
        else \
            echo 'neither docker nor podman is installed on the Pi; install one (sudo apt install -y podman) before \\\`hermod.sh update\\\`' >&2; \
            exit 1; \
        fi && \
        eval \"\$BUILD -t $image -f $dockerfile src\" && \
        \$RUNTIME save $image | sudo microk8s ctr images import - && \
        sudo microk8s ctr images tag --force localhost/$image docker.io/library/$image && \
        sudo microk8s ctr images tag --force localhost/$image $image"
}

# Wipe the plaintext seed-json key on the cluster Secret once vault42
# has imported the operator's user roster. The init container has
# already rendered /rendered/seed.json from the Secret, vault42 has
# bcrypted the passwords into auth.users, and the plaintext is no
# longer needed in-cluster. Idempotent: empty input is a valid Secret
# value, and the render-seed init falls back to the template path
# when seed-json is empty.
#
# Caller must have eval'd resolve_target so $kind, $kube_context,
# $namespace, and the SSH helpers are in scope. Failure is logged but
# not fatal: the rest of the install/update flow has already
# succeeded by the time this runs.
_wipe_seed_secret() {
    local rc=0
    case "$kind" in
        pc-kind|pc-kind-prod)
            kubectl --context "$kube_context" -n "$namespace" patch secret vault42-seed-credentials --type=merge -p '{"data":{"seed-json":""}}' >/dev/null 2>&1 || rc=$?
            ;;
        pi-microk8s|pi-microk8s-prod|pi-microk8s-prod-edge)
            _ssh "microk8s kubectl -n $namespace patch secret vault42-seed-credentials --type=merge -p '{\"data\":{\"seed-json\":\"\"}}' >/dev/null 2>&1" || rc=$?
            ;;
        *) return 0 ;;
    esac
    if (( rc == 0 )); then
        _log "vault42 seed-json wiped from cluster Secret (plaintext now only on operator host)"
    else
        _warn "could not wipe vault42 seed-json (Secret missing? already empty? rc=$rc); continuing"
    fi
    return 0
}

# Run ensure-secrets on the Pi with the right env. Centralises the
# four near-identical call sites (cmd_install / cmd_update /
# cmd_change_password / cmd_seed_users), and makes the local-seed push
# transparent: if the operator has run `hermod.sh users init`, the
# JSON is shipped to the Pi as a temp file and ensure-secrets picks it
# up via HERMOD_USERS_SEED_JSON_FILE; otherwise the historic 3-account
# template path runs unchanged.
#
# Caller must have eval'd resolve_target so $install_path + $namespace
# + ssh helpers are in scope. $1 (optional) overrides HERMOD_SECRETS_MODE
# (default from-env).
_ensure_secrets_on_pi() {
    local secrets_mode="${1:-from-env}"
    local _seed_pushed=0
    if _users_push_seed_to_pi >/dev/null 2>&1; then _seed_pushed=1; fi
    local seed_env=""
    (( _seed_pushed )) && seed_env="HERMOD_USERS_SEED_JSON_FILE=$_HERMOD_PI_SEED_PATH"
    local rc=0
    _ssh "cd $install_path && set -a && . hermod-prod.env && set +a && $seed_env KUBECTL='microk8s kubectl' HERMOD_NAMESPACE=$namespace HERMOD_SECRETS_MODE=$secrets_mode bash -c 'source lib/ensure-secrets.sh && ensure_secrets'" || rc=$?
    (( _seed_pushed )) && _users_clean_pi_seed
    return "$rc"
}

# ── kubectl resolver ────────────────────────────────────────────────────────
# Local target uses `kubectl --context kind-hermod`. SSH-attached Pi runs
# `microk8s kubectl`. Sub-functions accept ${KCTL[@]} so the same flow works
# on both hosts.
_kctl_for() {
    case "$1" in
        pc-kind|pc-kind-prod) echo "kubectl --context $kube_context" ;;
        pi-microk8s|pi-microk8s-prod|pi-microk8s-prod-edge) echo "microk8s kubectl" ;;
        *) _die "no kubectl resolver for kind=$1" ;;
    esac
}

# Run a kubectl command against the resolved target — locally for kind,
# over SSH for the Pi. Caller must have eval'd resolve_target first so
# $kind + $KCTL + $namespace + $ssh_host are in scope. Eliminates the
# pc-kind-prod vs pi-microk8s-prod case duplication that ran through
# every state-change subcommand.
_kc() {
    case "$kind" in
        pc-kind|pc-kind-prod)                                 $KCTL "$@" ;;
        pi-microk8s|pi-microk8s-prod|pi-microk8s-prod-edge)   _ssh "$KCTL $*" ;;
        *) _die "no kubectl runner for kind=$kind" ;;
    esac
}

# ── prune-stuck-pods helper ─────────────────────────────────────────────────
# Delete pods whose containers (init or main) are stuck in
# CreateContainerConfigError, ImagePullBackOff, ErrImagePull, or
# CrashLoopBackOff. These are typically orphan replicas left behind by
# a partial rollout (Secret missing, image not yet published, etc).
# Runs after each cmd_update so the operator's `kubectl get pods`
# output stays clean once the underlying issue is resolved by the
# preceding ensure-secrets / kustomize apply pass.
_prune_stuck_pods() {
    local kind_in="$1" ns_in="$2"
    local KCTL_BIN
    case "$kind_in" in
        pc-kind|pc-kind-prod)                                 KCTL_BIN="kubectl --context $kube_context" ;;
        pi-microk8s|pi-microk8s-prod|pi-microk8s-prod-edge)   KCTL_BIN="microk8s kubectl" ;;
        *) return 0 ;;
    esac
    # Build the get-pods command as a shell pipeline because jsonpath
    # alone can't AND across containerStatuses[*] reasons. The Go
    # template emits one line per pod that has at least one waiting
    # container with a stuck reason; xargs delete each.
    local stuck_cmd=$'pods=$('"$KCTL_BIN -n $ns_in"$' get pods -o go-template=\'{{range .items}}{{$pod := .metadata.name}}{{range .status.initContainerStatuses}}{{if .state.waiting}}{{if (or (or (eq .state.waiting.reason "CreateContainerConfigError") (eq .state.waiting.reason "ImagePullBackOff")) (or (eq .state.waiting.reason "ErrImagePull") (eq .state.waiting.reason "CrashLoopBackOff")))}}{{$pod}}{{"\n"}}{{end}}{{end}}{{end}}{{range .status.containerStatuses}}{{if .state.waiting}}{{if (or (or (eq .state.waiting.reason "CreateContainerConfigError") (eq .state.waiting.reason "ImagePullBackOff")) (or (eq .state.waiting.reason "ErrImagePull") (eq .state.waiting.reason "CrashLoopBackOff")))}}{{$pod}}{{"\n"}}{{end}}{{end}}{{end}}{{end}}\' 2>/dev/null | sort -u | grep .); if [[ -n "$pods" ]]; then echo "[hermod] pruning stuck pods: $pods"; echo "$pods" | xargs '"$KCTL_BIN -n $ns_in"$' delete pod --grace-period=0 --force 2>/dev/null; fi'
    case "$kind_in" in
        pc-kind|pc-kind-prod)
            bash -c "$stuck_cmd" || true
            ;;
        pi-microk8s|pi-microk8s-prod|pi-microk8s-prod-edge)
            _ssh "bash -c $(printf %q "$stuck_cmd")" || true
            ;;
    esac
}

# ── cleanup ─────────────────────────────────────────────────────────────────
# Operator-facing wrapper around _prune_stuck_pods. Run after manually
# resolving an underlying cause (e.g. fixed an image tag, populated a
# missing Secret) to evict the orphan replicas without restarting the
# controllers themselves.
cmd_cleanup() {
    local target="${1:-}"; [[ -z "$target" ]] && _die "usage: hermod.sh cleanup <prod-kind|prod-pi>"
    local resolved; resolved="$(resolve_target "$target")" || exit $?
    eval "$resolved"
    _log "scanning $target ($namespace) for stuck pods"
    _prune_stuck_pods "$kind" "$namespace"
    _ok "$target cleaned up"
}

# ── ansible helper (Pi reset / verify) ──────────────────────────────────────
# Bundled ansible/ tree lives at $REPO_ROOT/ansible. This wrapper writes a
# transient inventory pointing at the prod-pi target and shells out to
# ansible-playbook directly. hermod.sh runs on the host, so ansible-playbook
# must be installed in the host PATH (apt/dnf install ansible). Callers
# inside a distrobox should invoke hermod.sh via host-exec themselves —
# this script never calls distrobox-host-exec internally.
_ansible_run() {
    local playbook="$1"; shift
    local extra_vars="${1:-}"
    [[ -d "$REPO_ROOT/ansible" ]] || _die "ansible/ tree missing at $REPO_ROOT/ansible (bundled with hermod.sh)"
    _have ansible-playbook || _die "ansible-playbook missing on deployer (apt/dnf install ansible)"
    local inv; inv="$(mktemp -t hermod-inv-XXXXXX.yml)"
    # Expand $inv at trap-definition time (NOT fire time) so set -u doesn't
    # trip when the local goes out of scope before the RETURN trap runs.
    trap "rm -f '$inv'" RETURN
    # Inline the cluster overrides so ansible doesn't have to find
    # ansible/group_vars/ — the transient inventory lives in $TMPDIR
    # and ansible only loads group_vars colocated with the inventory.
    cat > "$inv" <<EOF
all:
  vars:
    ansible_user: ${ssh_host%%@*}
    ansible_ssh_private_key_file: $ssh_key
    ansible_ssh_common_args: '-o UserKnownHostsFile=$known_hosts -o IdentitiesOnly=yes -o StrictHostKeyChecking=yes'
    hermod_namespace: $namespace
    hermod_install_path: $install_path
    hermod_user: ${ssh_host%%@*}
  children:
    hermod_nodes:
      hosts:
        rpi5:
          ansible_host: ${ssh_host##*@}
EOF
    # ANSIBLE_FORCE_COLOR + unbuffered Python keep task lines streaming over
    # the SSH transport instead of buffering until a play finishes. Default
    # callback already prints PLAY/TASK headers per step; that is enough
    # progress for the operator without `-v` noise.
    (cd "$REPO_ROOT/ansible" && \
        ANSIBLE_FORCE_COLOR=1 PYTHONUNBUFFERED=1 \
        ansible-playbook -i "$inv" "playbooks/$playbook" $extra_vars)
}

# ── install ─────────────────────────────────────────────────────────────────
# Flags (after target):
#   --no-build      Skip image build (use whatever's already on the cluster)
#   --build         Force rebuild (default for prod-* targets)
cmd_install() {
    local target="${1:-}"; [[ -z "$target" ]] && _die "usage: hermod.sh install <target> [--no-build]"
    shift || true
    # Default ON: prod targets always rebuild from source unless told otherwise.
    # Rationale: clean-slate runs must NOT inherit stale images from the cache.
    # vault42 is intentionally NOT built by hermod.sh — it ships as a
    # separately-published image (docker pull). Pre-May-3 the operator
    # pre-loads the image into microk8s ctr manually; post-May-3 the
    # deployment will declare an explicit image tag and the kubelet
    # pulls it.
    local build=1
    while [[ $# -gt 0 ]]; do
        case "$1" in
            --no-build)    build=0; shift ;;
            --build)       build=1; shift ;;
            *) _warn "ignoring unknown install flag: $1"; shift ;;
        esac
    done
    local resolved; resolved="$(resolve_target "$target")" || exit $?
    eval "$resolved"

    # Edge-TLS overlays carry go_live=1 when applying them exposes traffic
    # publicly (Ingress, active Cloudflare Tunnel, Zero Trust). Refuse to
    # install without an explicit operator confirmation in the environment.
    if [[ "${go_live:-0}" = "1" && "${HERMOD_GO_LIVE:-}" != "YES" ]]; then
        _die "target '$target' EXPOSES traffic publicly via $overlay.
  Re-run with HERMOD_GO_LIVE=YES to confirm:
    HERMOD_GO_LIVE=YES hermod.sh install $target"
    fi

    case "$kind" in
        pi-microk8s-prod-edge)
            # Light install — the prod-pi base must already be up. Pure
            # rsync + kustomize apply on the Pi against the chosen edge
            # overlay; no addon shuffling, no cert seeding, no rebuild.
            _have ssh   || _die "ssh missing — pi edge install needs it"
            _have rsync || _die "rsync missing — needed to sync overlay manifests"
            if ! _ssh "microk8s kubectl get ns $namespace >/dev/null 2>&1"; then
                _die "namespace $namespace not found on Pi — install the base first: hermod.sh install prod-pi"
            fi
            rsync -az --delete \
                --exclude '.git/' --exclude '.claude/' \
                --exclude 'tests/results/' --exclude 'tests/.overlays/' \
                --exclude 'tests/.venv/' \
                --exclude '**/bin/' --exclude '**/obj/' \
                --exclude 'node_modules/' --exclude 'snap/' \
                --exclude 'hermod-prod.env' \
                -e "ssh -i $ssh_key -o UserKnownHostsFile=$known_hosts -o IdentitiesOnly=yes -o StrictHostKeyChecking=yes" \
                "$REPO_ROOT/" "$ssh_host:$install_path/"
            _log "applying edge overlay $overlay on Pi"
            _ssh "cd $install_path && microk8s kubectl apply -k $overlay"
            _ok "$target applied (overlay=$overlay, namespace=$namespace)"
            _log "summary:"
            _ssh "microk8s kubectl -n $namespace get certificate,ingress,deployment 2>&1 | head -30 || true"
            ;;
        pi-microk8s)
            _have ssh || _die "ssh missing — Pi install needs it (legacy dev-hardware path; use prod-pi for hardened deploys)"
            _die "pi5-live target deprecated — use 'hermod.sh install prod-pi' (the only Pi path now)"
            ;;
        pc-kind)
            _have podman || _die "podman missing — prod-kind install needs it"
            _have kind   || _die "kind missing — install kind from PATH for prod-kind"
            _log "delegating to lib/deploy-kind.sh"
            (( build )) || export HERMOD_NO_BUILD=1
            "$REPO_ROOT/lib/deploy-kind.sh"
            ;;
        pc-kind-prod)
            _have podman  || _die "podman missing — prod-kind install needs it"
            _have kind    || _die "kind missing — install kind-hermod from PATH"
            _have kubectl || _die "kubectl missing — prod-kind install needs it"
            _log "step 1/5 — base cluster + images via deploy-kind.sh (no dev apply)"
            (( build )) || export HERMOD_NO_BUILD=1
            HERMOD_SKIP_APPLY=1 "$REPO_ROOT/lib/deploy-kind.sh"
            _log "step 2/5 — issue internal CA + 12 leaf certs"
            "$REPO_ROOT/lib/issue-internal-certs.sh"
            _log "step 3/5 — seed cert Secrets into $namespace"
            HERMOD_KUBE_CTX="$kube_context" HERMOD_PROD_NAMESPACE="$namespace" \
                "$REPO_ROOT/lib/seed-internal-certs.sh"
            _log "step 4/5 — populate app Secrets"
            # Source hermod-prod.env if present so passwords/master-keys come
            # from the operator's vault, not auto-gen. Falls through to
            # interactive prompting (TTY) or keep-existing (non-TTY) per
            # ensure-secrets.sh defaults. .env file lives at REPO_ROOT
            # (gitignored) — see hermod-prod.env.example for the full schema.
            local env_file="${HERMOD_PROD_ENV:-$REPO_ROOT/hermod-prod.env}"
            if [[ -f "$env_file" ]]; then
                _log "  sourcing $env_file → from-env mode"
                # shellcheck disable=SC1090
                local _resolved; _resolved="$(_env_resolve_to_plaintext "$env_file")" \
                    || _die "could not resolve $env_file (vault locked? run 'hermod.sh mimir unlock')"
                set -a && . "$_resolved" && set +a
                _env_resolve_cleanup
                : "${HERMOD_SECRETS_MODE:=from-env}"
            else
                _log "  no $env_file — falling back to interactive prompts (use HERMOD_PROD_ENV to override path)"
            fi
            HERMOD_NAMESPACE="$namespace" KUBECTL="kubectl --context $kube_context" \
                ensure_secrets_with_users
            _log "step 5/5 — kustomize apply prod overlay"
            kubectl --context "$kube_context" apply -k "$REPO_ROOT/$overlay"
            kubectl --context "$kube_context" -n "$namespace" rollout status deployment/vault42 --timeout=120s 2>/dev/null || true
            _wipe_seed_secret
            _ok "$target installed (namespace=$namespace, overlay=$overlay)"
            _log "rollout status:"
            kubectl --context "$kube_context" -n "$namespace" get pods
            ;;
        pi-microk8s-prod|pi-microk8s-prod-edge)
            _have rsync || _die "rsync missing — Pi prod install needs it"
            _have ssh   || _die "ssh missing"
            local env_file="${HERMOD_PROD_ENV:-$REPO_ROOT/hermod-prod.env}"
            _env_present "$env_file" || _die "no $env_file (operator vault). Copy hermod-prod.env.example and fill it in."

            local total_steps=$(( build ? 10 : 9 ))
            _log "step 1/$total_steps — Pi host deps (bluez for ble2mqtt BLE scan via host bluetoothd)"
            # Theengs Gateway needs org.bluez over the host DBus socket. The
            # podman container is privileged + bind-mounts /var/run/dbus,
            # but if bluez isn't installed/enabled on the host there's no
            # DBus service to talk to → "org.bluez was not provided".
            _ssh "dpkg -s bluez >/dev/null 2>&1 || sudo DEBIAN_FRONTEND=noninteractive apt-get install -y bluez < /dev/null > /dev/null 2>&1"
            _ssh "sudo systemctl enable --now bluetooth >/dev/null 2>&1"
            _log "step 2/$total_steps — wait for microk8s ready + ensure addons + NodePort range covers 1024-65535"
            # microk8s reset wipes all addon state; install must restore them
            # before any kubectl apply runs. Idempotent — `enable` is no-op
            # when an addon is already on. Extending NodePort range lets
            # us bind 42069 + 8883 directly as NodePorts (no iptables).
            #
            # `ingress` addon is INTENTIONALLY OMITTED — it spins up an
            # nginx-ingress DaemonSet with hostNetwork that binds host
            # :80 + :443. We expose only :42069 (coord) + :8883 (mosquitto)
            # via NodePort, so ingress is pure attack surface. If a future
            # service needs HTTP routing, add a Service NodePort instead.
            _ssh "sudo microk8s status --wait-ready --timeout=120 >/dev/null"
            _ssh "sudo microk8s enable dns hostpath-storage registry 2>&1 | tail -5"
            _ssh "sudo microk8s disable ingress >/dev/null 2>&1 || true"
            _ssh "sudo grep -q 'service-node-port-range=1024-65535' /var/snap/microk8s/current/args/kube-apiserver \
                  || (sudo sed -i '/--service-node-port-range/d' /var/snap/microk8s/current/args/kube-apiserver \
                      && echo '--service-node-port-range=1024-65535' | sudo tee -a /var/snap/microk8s/current/args/kube-apiserver >/dev/null \
                      && sudo snap restart microk8s.daemon-kubelite \
                      && sudo microk8s status --wait-ready --timeout=120 >/dev/null)"
            _ssh "sudo mkdir -p $install_path && sudo chown -R \$(id -u):\$(id -g) $install_path"
            _log "step 3/$total_steps — rsync repo to Pi"
            rsync -az --delete \
                --exclude '.git/' --exclude '.claude/' \
                --exclude 'tests/results/' --exclude 'tests/.overlays/' \
                --exclude 'tests/.venv/' \
                --exclude '**/bin/' --exclude '**/obj/' \
                --exclude 'node_modules/' --exclude 'snap/' \
                --exclude 'hermod-prod.env' \
                -e "ssh -i $ssh_key -o UserKnownHostsFile=$known_hosts -o IdentitiesOnly=yes -o StrictHostKeyChecking=yes" \
                "$REPO_ROOT/" "$ssh_host:$install_path/"
            _log "step 4/$total_steps — push hermod-prod.env (operator secrets) to Pi"
            local _scp_src; _scp_src="$(_env_resolve_to_plaintext "$env_file")" \
                || _die "could not resolve $env_file (vault locked? run 'hermod.sh mimir unlock')"
            scp -i "$ssh_key" -o UserKnownHostsFile="$known_hosts" -o IdentitiesOnly=yes -o StrictHostKeyChecking=yes \
                "$_scp_src" "$ssh_host:$install_path/hermod-prod.env"
            _env_resolve_cleanup
            _ssh "chmod 0600 $install_path/hermod-prod.env"
            if (( build )); then
                _log "step 5/$total_steps — build hermod-coordinator + lora2mqtt arm64 images on Pi (5-10 min; skip with --no-build)"
                # The Pi runs microk8s (its own containerd) — there's no
                # docker daemon. Detect whatever OCI builder is available
                # in PATH (podman first, since the ansible base role
                # installs it; docker as a fallback when the operator
                # has rolled their own). Both produce images consumable
                # by `microk8s ctr image import`.
                _ssh "cd $install_path && \
                    if command -v podman >/dev/null 2>&1; then \
                        BUILD='podman build --format=docker'; \
                        SAVE='podman save --format=docker-archive'; \
                    elif command -v docker >/dev/null 2>&1; then \
                        if docker buildx version >/dev/null 2>&1; then \
                            BUILD='DOCKER_BUILDKIT=1 docker build --progress=plain'; \
                        else \
                            BUILD='docker build'; \
                        fi; \
                        SAVE='docker save'; \
                    else \
                        echo 'ERROR: neither podman nor docker is installed on the Pi'; \
                        echo 'Install with: sudo apt-get install -y podman   (or re-run hermod.sh provision)'; \
                        exit 127; \
                    fi && \
                    eval \"\$BUILD -t hermod-coordinator:latest -f src/Hermod.Coordinator/Dockerfile src\" && \
                    eval \"\$BUILD -t lora2mqtt:latest         -f src/LoRa2MQTT/LoRa2MQTT.Service/Dockerfile src\" && \
                    eval \"\$SAVE hermod-coordinator:latest\" | sudo microk8s ctr images import - && \
                    eval \"\$SAVE lora2mqtt:latest\"          | sudo microk8s ctr images import - && \
                    sudo microk8s ctr images tag --force localhost/hermod-coordinator:latest docker.io/library/hermod-coordinator:latest && \
                    sudo microk8s ctr images tag --force localhost/hermod-coordinator:latest hermod-coordinator:latest && \
                    sudo microk8s ctr images tag --force localhost/lora2mqtt:latest         docker.io/library/lora2mqtt:latest && \
                    sudo microk8s ctr images tag --force localhost/lora2mqtt:latest         lora2mqtt:latest"
            else
                _log "step 5/$total_steps — SKIP build (--no-build); using cached images"
            fi
            local s=6
            _log "step $((s++))/$total_steps — issue internal CA + 12 leaf certs locally then push"
            "$REPO_ROOT/lib/issue-internal-certs.sh"
            _ssh "mkdir -p ~/.hermod-prod-certs"
            rsync -az -e "ssh -i $ssh_key -o UserKnownHostsFile=$known_hosts -o IdentitiesOnly=yes -o StrictHostKeyChecking=yes" \
                "$HOME/.hermod-prod-certs/" "$ssh_host:.hermod-prod-certs/"
            _log "step $((s++))/$total_steps — seed cert Secrets into $namespace on Pi"
            _ssh "KUBECTL='microk8s kubectl' HERMOD_PROD_NAMESPACE=$namespace bash $install_path/lib/seed-internal-certs.sh"
            _log "step $((s++))/$total_steps — populate app Secrets on Pi (from-env mode)"
            _ensure_secrets_on_pi from-env
            _log "step $((s++))/$total_steps — kustomize apply prod overlay on Pi"
            _ssh "cd $install_path && microk8s kubectl apply -k $overlay"
            if (( build )); then
                # Apply alone won't roll pods when only the image bytes
                # changed (manifest stable). Force a fresh ReplicaSet so
                # the new image actually runs. Includes ble2mqtt so
                # Theengs picks up host bluez that may have been added
                # this install run, and z2m/wifi2mqtt so any nanomq cert
                # rotation lands consistently.
                # Older microk8s kubectl doesn't accept `--all` on rollout
                # restart, so list the deployments explicitly. Order =
                # least-disruptive first (translators), coord last so
                # the dashboard reload comes after its dependencies.
                _log "step $s/$total_steps — rollout restart Hermod-owned deployments (vault42 lifecycle is separate, see seed-users)"
                _ssh "microk8s kubectl -n $namespace rollout restart \
                    deployment/lora2mqtt deployment/zigbee2mqtt \
                    deployment/wifi2mqtt deployment/ble2mqtt \
                    deployment/nanomq \
                    deployment/hermod-coordinator"
                for d in lora2mqtt zigbee2mqtt wifi2mqtt ble2mqtt nanomq hermod-coordinator; do
                    _ssh "microk8s kubectl -n $namespace rollout status deployment/$d --timeout=180s"
                done
            fi
            # vault42 was reachable by the time coord's wait-for-vault42
            # init finished, so its seed import has completed by now.
            # Wipe the plaintext seed-json key from the cluster Secret
            # so a leak doesn't expose the operator's first-login
            # passwords (vault42 keeps its own bcrypted copy).
            _wipe_seed_secret
            _ok "$target installed (Pi5: namespace=$namespace, overlay=$overlay; dashboard at https://$(echo $ssh_host | cut -d@ -f2):42069/)"
            _log "rollout status:"
            _ssh "microk8s kubectl -n $namespace get pods"
            ;;
        *) _die "install not implemented for kind=$kind" ;;
    esac
}

# ── update ──────────────────────────────────────────────────────────────────
cmd_update() {
    # Parse --rebuild [<svc>] before the positional target. Without the
    # flag, update only rsyncs the source tree and rolls coord (matches
    # the historic behaviour). With it, the named service's image is
    # rebuilt on the Pi (BuildKit when available, plain docker build
    # otherwise) and side-loaded into microk8s before the rollout. The
    # plain `--rebuild` (no value) form rebuilds both shipped services.
    #
    # Supported values: coord (= hermod-coordinator dashboard + API),
    # lora2mqtt (the .NET LoRa adapter), all (both). Anything else dies
    # rather than silently doing nothing.
    local rebuild=""
    local -a positional=()
    while [[ $# -gt 0 ]]; do
        case "$1" in
            --rebuild)
                if [[ -n "${2:-}" && "$2" != --* ]]; then
                    rebuild="$2"; shift 2
                else
                    rebuild="all"; shift
                fi ;;
            --rebuild=*) rebuild="${1#--rebuild=}"; shift ;;
            *) positional+=("$1"); shift ;;
        esac
    done
    set -- "${positional[@]}"
    case "$rebuild" in
        ""|coord|lora2mqtt|all) ;;
        *) _die "update: invalid --rebuild value '$rebuild' (use coord, lora2mqtt, or all)" ;;
    esac

    local target="${1:-}"; [[ -z "$target" ]] && _die "usage: hermod.sh update <target> [--rebuild <coord|lora2mqtt|all>]"
    local resolved; resolved="$(resolve_target "$target")" || exit $?
    eval "$resolved"

    case "$kind" in
        pi-microk8s)
            _have rsync || _die "rsync missing — Pi update needs it"
            _have ssh   || _die "ssh missing"
            _log "rsyncing $REPO_ROOT/ → $ssh_host:$install_path/"
            rsync -az --delete \
                --exclude '.git/' --exclude '.claude/' \
                --exclude 'tests/results/' --exclude 'tests/.overlays/' \
                --exclude 'tests/.venv/' \
                --exclude '**/bin/' --exclude '**/obj/' \
                --exclude 'node_modules/' --exclude 'snap/' \
                -e "ssh -i $ssh_key -o UserKnownHostsFile=$known_hosts -o IdentitiesOnly=yes -o StrictHostKeyChecking=yes" \
                "$REPO_ROOT/" "$ssh_host:$install_path/"
            _log "reconciling Secrets (keep mode)"
            _ensure_secrets_on_pi keep
            _log "applying kustomize overlay ($overlay) on Pi"
            _ssh "cd $install_path && microk8s kubectl apply -k $overlay"
            _log "rolling restart of hermod-coordinator"
            _ssh "microk8s kubectl -n $namespace rollout restart deployment/hermod-coordinator"
            _ssh "microk8s kubectl -n $namespace rollout status deployment/hermod-coordinator --timeout=120s"
            _prune_stuck_pods "$kind" "$namespace"
            _wipe_seed_secret
            _ok "$target updated"
            ;;
        pc-kind|pc-kind-prod)
            _have kubectl || _die "kubectl missing — kind update needs it"
            _log "reconciling Secrets (keep mode) on $kube_context"
            HERMOD_KUBE_CTX="$kube_context" HERMOD_NAMESPACE="$namespace" HERMOD_SECRETS_MODE=keep \
                ensure_secrets_with_users
            _log "kustomize apply on $kube_context ($overlay)"
            kubectl --context "$kube_context" apply -k "$REPO_ROOT/$overlay"
            kubectl --context "$kube_context" -n "$namespace" rollout restart deployment/hermod-coordinator
            kubectl --context "$kube_context" -n "$namespace" rollout status deployment/hermod-coordinator --timeout=120s
            _prune_stuck_pods "$kind" "$namespace"
            _wipe_seed_secret
            _ok "$target updated"
            ;;
        pi-microk8s-prod|pi-microk8s-prod-edge)
            _have rsync || _die "rsync missing"
            _have ssh   || _die "ssh missing"
            _log "step 1/5 — rsync $REPO_ROOT/ → $ssh_host:$install_path/"
            # --info=progress2 prints a single-line aggregate progress meter
            # so the operator sees bytes-on-the-wire instead of a frozen
            # screen during the 30-60s transfer. -h adds human-readable units.
            rsync -az -h --info=progress2 --delete \
                --exclude '.git/' --exclude '.claude/' \
                --exclude 'tests/results/' --exclude 'tests/.overlays/' \
                --exclude 'tests/.venv/' \
                --exclude '**/bin/' --exclude '**/obj/' \
                --exclude 'node_modules/' --exclude 'snap/' \
                -e "ssh -i $ssh_key -o UserKnownHostsFile=$known_hosts -o IdentitiesOnly=yes -o StrictHostKeyChecking=yes" \
                "$REPO_ROOT/" "$ssh_host:$install_path/"
            # Ensure-secrets in keep mode: any new Secret key added by
            # the apply below (e.g. the per-translator MQTT users that
            # nanomq-acl-init reads) gets a fresh auto-generated value
            # without disturbing existing operator-set secrets.
            _log "step 2/5 — reconcile Secrets on Pi (keep mode)"
            _ensure_secrets_on_pi keep
            _log "step 3/5 — kustomize apply $overlay on Pi"
            _ssh "cd $install_path && microk8s kubectl apply -k $overlay"
            # Optional rebuild step. Runs BEFORE the rollout so the
            # restart picks up the freshly-imported image. Without
            # --rebuild this branch is a no-op and update keeps its
            # historic "rsync + apply + roll coord" semantics.
            if [[ -n "$rebuild" ]]; then
                _log "step 3b/5 — rebuild image(s) on Pi: $rebuild"
                if [[ "$rebuild" == "all" || "$rebuild" == "coord" ]]; then
                    _pi_build_image coord
                fi
                if [[ "$rebuild" == "all" || "$rebuild" == "lora2mqtt" ]]; then
                    _pi_build_image lora2mqtt
                fi
            fi
            _log "step 4/5 — rolling restart hermod-coordinator"
            _ssh "microk8s kubectl -n $namespace rollout restart deployment/hermod-coordinator"
            _ssh "microk8s kubectl -n $namespace rollout status deployment/hermod-coordinator --timeout=120s"
            if [[ "$rebuild" == "all" || "$rebuild" == "lora2mqtt" ]]; then
                _log "step 4b/5 — rolling restart lora2mqtt (rebuilt above)"
                _ssh "microk8s kubectl -n $namespace rollout restart deployment/lora2mqtt"
                _ssh "microk8s kubectl -n $namespace rollout status deployment/lora2mqtt --timeout=180s"
            fi
            _log "step 5/5 — prune stuck pods (no-op on a healthy cluster)"
            _prune_stuck_pods "$kind" "$namespace"
            _wipe_seed_secret
            _ok "$target updated${rebuild:+ (rebuilt: $rebuild)}"
            ;;
        *) _die "update not implemented for kind=$kind" ;;
    esac
}

# ── rotate-certs ────────────────────────────────────────────────────────────
# Roll the per-service mTLS leaves (server + client) without touching the
# CA. Coord hot-reloads its server cert via Kestrel's
# ServerCertificateSelector (verified by P4 T4.5 — fingerprint changes
# within 30 s of the Secret update, no pod restart). Translator clients
# load their cert at process start, so we kick a rolling restart on
# them so the new client cert is picked up — coord stays up the whole time.
#
# CA stays put (~/.hermod-prod-certs/ca/ca.crt, mode 0644 with key 0600).
# Leaf private keys live in ~/.hermod-prod-certs/leaves/, mode 0600.
cmd_rotate_certs() {
    local target="${1:-}"; [[ -z "$target" ]] && _die "usage: hermod.sh rotate-certs <prod-kind|prod-pi>"
    local resolved; resolved="$(resolve_target "$target")" || exit $?
    eval "$resolved"

    _log "step 1/3 — re-issuing 12 leaves (--force; CA preserved)"
    "$REPO_ROOT/lib/issue-internal-certs.sh" --force

    _log "step 2/3 — re-seeding cert Secrets in $namespace"
    case "$kind" in
        pc-kind-prod)
            _have kubectl || _die "kubectl missing"
            HERMOD_KUBE_CTX="$kube_context" HERMOD_PROD_NAMESPACE="$namespace" \
                "$REPO_ROOT/lib/seed-internal-certs.sh"
            _log "step 3/3 — rolling translator restarts to pick up new client certs (coord hot-reloads in place)"
            kubectl --context "$kube_context" -n "$namespace" rollout restart \
                deployment/lora2mqtt deployment/zigbee2mqtt deployment/wifi2mqtt deployment/ble2mqtt deployment/nanomq 2>&1 | head
            ;;
        pi-microk8s-prod|pi-microk8s-prod-edge)
            _have ssh || _die "ssh missing"
            _ssh "mkdir -p ~/.hermod-prod-certs"
            rsync -az -e "ssh -i $ssh_key -o UserKnownHostsFile=$known_hosts -o IdentitiesOnly=yes -o StrictHostKeyChecking=yes" \
                "$HOME/.hermod-prod-certs/" "$ssh_host:.hermod-prod-certs/"
            _ssh "cd $install_path && KUBECTL=microk8s\\ kubectl HERMOD_PROD_NAMESPACE=$namespace bash lib/seed-internal-certs.sh"
            _log "step 3/3 — rolling restarts on Pi"
            _ssh "microk8s kubectl -n $namespace rollout restart deployment/lora2mqtt deployment/zigbee2mqtt deployment/wifi2mqtt deployment/ble2mqtt deployment/nanomq"
            ;;
        *) _die "rotate-certs not implemented for kind=$kind" ;;
    esac

    local fp
    fp="$(openssl x509 -in "$HOME/.hermod-prod-certs/ca/ca.crt" -noout -fingerprint -sha256 | cut -d= -f2)"
    _ok "certs rotated. CA fp (unchanged): $fp"
    _warn "operator action: back up ~/.hermod-prod-certs/ca/ca.key to your offline vault if not already (it signs everything)."
}

# ── change-password ─────────────────────────────────────────────────────────
# Rotate the vault42 seed-user passwords. Vault42 only reads seed.json
# on first boot (DB-empty path), so to actually rotate we also drop the
# `vault` database in postgres and restart vault42 — its render-seed
# init container picks up the new passwords from the Secret (re-derived
# from hermod-prod.env via ensure-secrets from-env mode).
cmd_change_password() {
    local target="${1:-}"; [[ -z "$target" ]] && _die "usage: hermod.sh change-password <prod-kind|prod-pi> [explicit-password]"
    local explicit="${2:-}"
    local resolved; resolved="$(resolve_target "$target")" || exit $?
    eval "$resolved"

    local env_file="${HERMOD_PROD_ENV:-$REPO_ROOT/hermod-prod.env}"
    _env_present "$env_file" || _die "no $env_file (seed source). Bootstrap with hermod-prod.env.example first."

    local newuser newviewer newoperator
    if [[ -n "$explicit" ]]; then
        _log "step 1/5 — using operator-supplied password for all three seed accounts"
        newuser="$explicit"
        newviewer="$explicit"
        newoperator="$explicit"
    else
        _log "step 1/5 — generating new vault42 passwords for viewer / user / operator"
        newuser="$(openssl rand -base64 36 | tr -d '\n=+/' | head -c 24)"
        newviewer="$(openssl rand -base64 36 | tr -d '\n=+/' | head -c 24)"
        newoperator="$(openssl rand -base64 36 | tr -d '\n=+/' | head -c 24)"
    fi
    sed -i -E "s|^HERMOD_VAULT42_USER_PASSWORD=.*|HERMOD_VAULT42_USER_PASSWORD=$newuser|" "$env_file"
    sed -i -E "s|^HERMOD_VAULT42_VIEWER_PASSWORD=.*|HERMOD_VAULT42_VIEWER_PASSWORD=$newviewer|" "$env_file"
    sed -i -E "s|^HERMOD_VAULT42_OPERATOR_PASSWORD=.*|HERMOD_VAULT42_OPERATOR_PASSWORD=$newoperator|" "$env_file"
    # Drop any legacy admin slot that might still be in operator env files.
    sed -i -E '/^HERMOD_VAULT42_ADMIN_PASSWORD=/d' "$env_file"

    case "$kind" in
        pc-kind-prod)
            _have kubectl || _die "kubectl missing"
            local KCTL="kubectl --context $kube_context"
            _log "step 2/5 — dropping vault database in postgres (forces re-seed)"
            $KCTL -n "$namespace" exec statefulset/postgres -c postgres -- \
                psql -U postgres -d postgres -c "DROP DATABASE IF EXISTS vault;" -c "CREATE DATABASE vault;" 2>&1 | tail -3
            _log "step 3/5 — re-seeding app Secrets from $env_file"
            local _resolved; _resolved="$(_env_resolve_to_plaintext "$env_file")" \
                || _die "could not resolve $env_file (vault locked? run 'hermod.sh mimir unlock')"
            set -a && . "$_resolved" && set +a
            _env_resolve_cleanup
            HERMOD_NAMESPACE="$namespace" HERMOD_SECRETS_MODE=from-env KUBECTL="$KCTL" \
                ensure_secrets_with_users
            _log "step 4/5 — restarting vault42 (re-seeds on first boot)"
            $KCTL -n "$namespace" rollout restart deployment/vault42
            $KCTL -n "$namespace" rollout status deployment/vault42 --timeout=120s
            _wipe_seed_secret
            _log "step 5/5 — restarting coord so JWKS refreshes"
            $KCTL -n "$namespace" rollout restart deployment/hermod-coordinator
            ;;
        pi-microk8s-prod|pi-microk8s-prod-edge)
            _have ssh || _die "ssh missing"
            _log "step 2/5 — dropping vault database on Pi (scale vault42→0 first to release connections)"
            _ssh "microk8s kubectl -n $namespace scale deployment/vault42 --replicas=0"
            _ssh "microk8s kubectl -n $namespace wait --for=delete pod -l app=vault42 --timeout=60s 2>&1 | tail -2 || true"
            # DROP+CREATE strips schema-public grants from vault_mig/vault_app
            # (PG 15+ revoke). Re-issue the same GRANT block postgres-init
            # runs on first boot so vault42 can migrate again. Two psql
            # calls because schema-level GRANTs need a connection to the
            # target DB (not the postgres DB), and \c inside a heredoc
            # over SSH+kubectl exec is escape-fragile.
            _ssh "microk8s kubectl -n $namespace exec statefulset/postgres -c postgres -- psql -U postgres -d postgres -c 'DROP DATABASE IF EXISTS vault WITH (FORCE);' -c 'CREATE DATABASE vault OWNER vault_mig;' -c 'GRANT ALL PRIVILEGES ON DATABASE vault TO vault_mig;' -c 'GRANT CONNECT ON DATABASE vault TO vault_app;'" 2>&1 | tail -5
            _ssh "microk8s kubectl -n $namespace exec statefulset/postgres -c postgres -- psql -U postgres -d vault -c 'GRANT ALL ON SCHEMA public TO vault_mig;' -c 'GRANT USAGE ON SCHEMA public TO vault_app;' -c 'ALTER SCHEMA public OWNER TO vault_mig;' -c 'ALTER DEFAULT PRIVILEGES FOR ROLE vault_mig IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO vault_app;' -c 'ALTER DEFAULT PRIVILEGES FOR ROLE vault_mig IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO vault_app;'" 2>&1 | tail -7
            _log "step 3/5 — pushing fresh hermod-prod.env to Pi + reseeding"
            local _scp_src; _scp_src="$(_env_resolve_to_plaintext "$env_file")" \
                || _die "could not resolve $env_file (vault locked? run 'hermod.sh mimir unlock')"
            scp -i "$ssh_key" -o UserKnownHostsFile="$known_hosts" -o IdentitiesOnly=yes -o StrictHostKeyChecking=yes \
                "$_scp_src" "$ssh_host:$install_path/hermod-prod.env"
            _env_resolve_cleanup
            _ensure_secrets_on_pi from-env
            _log "step 4/5 — scaling vault42 back to 1 (re-seeds from new Secret on first boot)"
            _ssh "microk8s kubectl -n $namespace scale deployment/vault42 --replicas=1"
            _ssh "microk8s kubectl -n $namespace rollout status deployment/vault42 --timeout=120s"
            _wipe_seed_secret
            _log "step 5/5 — restarting coord on Pi (refresh JWKS for new signing key)"
            _ssh "microk8s kubectl -n $namespace rollout restart deployment/hermod-coordinator"
            ;;
        *) _die "change-password not implemented for kind=$kind (use prod-kind or prod-pi)" ;;
    esac

    _ok "passwords rotated. New seed credentials:"
    printf '  viewer    viewer@hermod.local    %s\n' "$newviewer"
    printf '  user      user@hermod.local      %s\n' "$newuser"
    printf '  operator  operator@hermod.local  %s\n' "$newoperator"
    _warn "rotate again from the Vault42 dashboard for ongoing changes; this path is for reset-from-zero only."
}

# ── reset (clean slate) ─────────────────────────────────────────────────────
# WARNING: full wipe. For prod-pi: ansible uninstall.yml + microk8s reset.
# For prod-kind: `kind delete cluster --name hermod` + cert dir prompt.
# Requires HERMOD_RESET_CONFIRM=YES to proceed (deliberate friction).
cmd_reset() {
    local target="${1:-}"; [[ -z "$target" ]] && _die "usage: hermod.sh reset <prod-kind|prod-pi>"
    [[ "${HERMOD_RESET_CONFIRM:-}" == "YES" ]] || _die "DESTRUCTIVE. re-run with HERMOD_RESET_CONFIRM=YES (wipes data + cluster)."
    local resolved; resolved="$(resolve_target "$target")" || exit $?
    eval "$resolved"

    case "$kind" in
        pc-kind-prod)
            _have kind || _die "kind missing"
            _log "delete kind cluster 'hermod'"
            kind delete cluster --name hermod 2>&1 | head
            _log "wipe ~/.hermod-prod-certs (the CA goes too — you'll re-issue on next install)"
            rm -rf "$HOME/.hermod-prod-certs"
            ;;
        pi-microk8s-prod|pi-microk8s-prod-edge)
            _have ssh || _die "ssh missing"
            # Older installs used iptables REDIRECT to map host:42069→NodePort:32069
            # before we extended microk8s --service-node-port-range. Strip any
            # leftover rules so they don't outlive the NodePort migration.
            _log "step 1/4 — strip legacy iptables port-forwards (no-op on fresh installs)"
            _ssh "for rule in 'PREROUTING -p tcp --dport 42069 -j REDIRECT --to-port 32069' \
                            'OUTPUT -p tcp --dport 42069 -d 127.0.0.1 -j REDIRECT --to-port 32069' \
                            'PREROUTING -p tcp --dport 8883  -j REDIRECT --to-port 32183'; do \
                    sudo iptables -t nat -D \$rule 2>/dev/null || true; \
                done; sudo netfilter-persistent save 2>&1 | tail -1 || true"
            _log "step 2/4 — ansible uninstall (deletes hermod ns + PVCs on Pi; can run 30-60s silent between tasks)"
            _ansible_run uninstall.yml "-e confirm_uninstall=yes"
            # Stream microk8s reset directly. Older code piped to `tail -3`
            # which buffers the entire 60-120s reset before printing anything,
            # so the operator sees a frozen screen. The full output is more
            # useful here than three trailing lines anyway.
            _log "step 3/4 — microk8s reset on Pi (factory state; 1-2 minutes)"
            _ssh "sudo microk8s reset --destroy-storage 2>&1 || true"
            _log "step 4/4 — wipe Pi-side ~/.hermod-prod-certs and operator env"
            _ssh "rm -rf ~/.hermod-prod-certs /opt/hermod/hermod-prod.env"
            ;;
        *) _die "reset not implemented for kind=$kind" ;;
    esac
    _ok "$target reset to clean slate"
}

# ── roll-jwks ───────────────────────────────────────────────────────────────
# Restart vault42 → coord re-fetches /.well-known/jwks.json. Persistent
# SIGNING_KEY in vault42-secrets stays the same (existing JWTs still
# verify); restart only refreshes vault42's in-memory key derivative.
cmd_roll_jwks() {
    local target="${1:-}"; [[ -z "$target" ]] && _die "usage: hermod.sh roll-jwks <prod-kind|prod-pi>"
    local resolved; resolved="$(resolve_target "$target")" || exit $?
    eval "$resolved"
    local KCTL; KCTL="$(_kctl_for "$kind")"
    _kc -n "$namespace" rollout restart deployment/vault42
    _kc -n "$namespace" rollout status  deployment/vault42 --timeout=90s
    _kc -n "$namespace" rollout restart deployment/hermod-coordinator
    _ok "vault42 + coord rolled (JWKS re-fetched)"
}

# ── reset-db ────────────────────────────────────────────────────────────────
# Drop both DBs (vault, hermod) → restart consumers. Triggers a full
# re-init: vault42 reseeds from vault42-seed-credentials Secret on first
# boot; coord re-runs migrations / starts with empty rules+devices.
cmd_reset_db() {
    local target="${1:-}"; [[ -z "$target" ]] && _die "usage: hermod.sh reset-db <prod-kind|prod-pi>"
    [[ "${HERMOD_RESET_CONFIRM:-}" == "YES" ]] || _die "DESTRUCTIVE. re-run with HERMOD_RESET_CONFIRM=YES (drops vault + hermod DBs)."
    local resolved; resolved="$(resolve_target "$target")" || exit $?
    eval "$resolved"
    local KCTL; KCTL="$(_kctl_for "$kind")"
    local sql='DROP DATABASE IF EXISTS vault; DROP DATABASE IF EXISTS hermod; CREATE DATABASE vault; CREATE DATABASE hermod;'
    _kc -n "$namespace" exec statefulset/postgres -c postgres -- psql -U postgres -d postgres -c "$sql" 2>&1 | tail -5
    _kc -n "$namespace" rollout restart deployment/vault42 deployment/hermod-coordinator
    _ok "vault + hermod DBs recreated; vault42 + coord rolling"
}

# ── protocol on/off ─────────────────────────────────────────────────────────
# Scale a translator deployment to 0 or 1 replicas. Names match the
# deployments in the prod overlay.
cmd_protocol() {
    local action="${1:-}" name="${2:-}" target="${3:-}"
    if [[ "$action" == "show" ]]; then
        target="${name:-${HERMOD_TARGET:-prod-pi}}"
        local resolved; resolved="$(resolve_target "$target")" || exit $?
        eval "$resolved"
        local KCTL; KCTL="$(_kctl_for "$kind")"
        printf 'protocol enable state on %s (%s):\n\n' "$target" "$namespace"
        local d nm
        for d in lora2mqtt zigbee2mqtt ble2mqtt wifi2mqtt; do
            case "$d" in
                lora2mqtt)   nm=lora ;;
                zigbee2mqtt) nm=zigbee ;;
                ble2mqtt)     nm=ble ;;
                wifi2mqtt)   nm=wifi ;;
            esac
            local desired ready
            desired=$(_kc -n "$namespace" get deployment "$d" -o jsonpath='{.spec.replicas}' 2>/dev/null || echo '?')
            ready=$(_kc   -n "$namespace" get deployment "$d" -o jsonpath='{.status.readyReplicas}' 2>/dev/null || echo 0)
            local state
            case "$desired" in
                0) state="off" ;;
                "") state="(missing)" ;;
                ?) state="on  (ready=${ready:-0}/${desired})" ;;
            esac
            printf '  %-7s %-15s %s\n' "$nm" "$d" "$state"
        done
        return 0
    fi
    [[ -z "$action" || -z "$name" || -z "$target" ]] && \
        _die "usage: hermod.sh protocol <on|off|show> <lora|zigbee|ble|wifi|target> [<target>]"
    local replicas
    case "$action" in on) replicas=1 ;; off) replicas=0 ;; *) _die "action must be on, off, or show" ;; esac
    local deploy
    case "$name" in
        lora)   deploy=lora2mqtt ;;
        zigbee) deploy=zigbee2mqtt ;;
        ble)    deploy=ble2mqtt ;;
        wifi)   deploy=wifi2mqtt ;;
        *) _die "name must be lora, zigbee, ble, or wifi" ;;
    esac
    local resolved; resolved="$(resolve_target "$target")" || exit $?
    eval "$resolved"
    local KCTL; KCTL="$(_kctl_for "$kind")"
    _kc -n "$namespace" scale deployment/"$deploy" --replicas="$replicas"
    _ok "$deploy scaled to $replicas on $target"
}

# ── limiter on/off ──────────────────────────────────────────────────────────
# Hot-toggle the per-topic ingress limiter on a live coordinator. Rate
# cap and dedup window are independently switchable; either, both, or
# neither can be active. `set env` triggers a rollout that picks up the
# new value via IOptionsMonitor on the next request.
#
# This is a runtime override only: it patches the live Deployment, NOT
# the kustomize overlay. To make a change permanent, edit
# kubernetes/overlays/<target>/coord-prod.yaml under
# `Hermod__RateLimit__{Enabled,DedupEnabled}` and `hermod.sh update`.
cmd_limiter() {
    local target="${1:-}" knob="${2:-}" action="${3:-}"
    [[ -z "$target" || -z "$knob" ]] && \
        _die "usage: hermod.sh limiter <target> <rate|dedup|show> [on|off]"

    local resolved; resolved="$(resolve_target "$target")" || exit $?
    eval "$resolved"
    local KCTL; KCTL="$(_kctl_for "$kind")"

    case "$knob" in
        show)
            _log "limiter envs on $target ($namespace):"
            _kc -n "$namespace" set env deployment/hermod-coordinator --list \
                | grep -E '^Hermod__RateLimit__(Enabled|DedupEnabled)=' || \
                _warn "no RateLimit envs set — limiter is using defaults (both off)"
            ;;
        rate|dedup)
            [[ "$action" != "on" && "$action" != "off" ]] && \
                _die "action must be on or off (got: ${action:-<empty>})"
            local var
            case "$knob" in
                rate)  var="Hermod__RateLimit__Enabled" ;;
                dedup) var="Hermod__RateLimit__DedupEnabled" ;;
            esac
            local val; [[ "$action" == "on" ]] && val=true || val=false
            _kc -n "$namespace" set env deployment/hermod-coordinator "$var=$val"
            _ok "$target: $var=$val (rollout triggered)"
            ;;
        *) _die "knob must be rate, dedup, or show" ;;
    esac
}

# ── ensure-secrets (apply / re-apply Secrets) ───────────────────────────────
# Thin wrapper around lib/ensure-secrets.sh that picks the right mode
# and runs against the target cluster. Default mode is `keep`: any
# existing Secret value survives, only missing keys get a fresh
# auto-generated value. Pass --from-env to read every value from the
# corresponding HERMOD_* env var in hermod-prod.env (rotation flow).
# Pass --interactive on a TTY to be prompted per-slot (initial bootstrap).
cmd_ensure_secrets() {
    local target="${1:-}"; [[ -z "$target" ]] && _die "usage: hermod.sh ensure-secrets <prod-kind|prod-pi> [--from-env|--interactive|--auto|--keep]"
    shift || true
    local mode="${HERMOD_SECRETS_MODE:-keep}"
    while [[ $# -gt 0 ]]; do
        case "$1" in
            --from-env)    mode=from-env ;;
            --interactive) mode=interactive ;;
            --auto)        mode=auto ;;
            --keep)        mode=keep ;;
            *) _die "unknown flag: $1" ;;
        esac
        shift
    done
    local resolved; resolved="$(resolve_target "$target")" || exit $?
    eval "$resolved"

    local env_file="${HERMOD_PROD_ENV:-$REPO_ROOT/hermod-prod.env}"
    case "$kind" in
        pc-kind-prod)
            _have kubectl || _die "kubectl missing"
            _log "ensure-secrets ($mode) → $target ($namespace)"
            if [[ "$mode" == "from-env" && -f "$env_file" ]]; then
                local _resolved; _resolved="$(_env_resolve_to_plaintext "$env_file")" \
                    || _die "could not resolve $env_file (vault locked? run 'hermod.sh mimir unlock')"
                set -a && . "$_resolved" && set +a
                _env_resolve_cleanup
            fi
            HERMOD_NAMESPACE="$namespace" HERMOD_SECRETS_MODE="$mode" \
            KUBECTL="kubectl --context $kube_context" \
                ensure_secrets_with_users
            ;;
        pi-microk8s-prod|pi-microk8s-prod-edge)
            _have ssh || _die "ssh missing"
            _log "ensure-secrets ($mode) → $target ($namespace, via ssh)"
            if [[ "$mode" == "from-env" ]]; then
                _env_present "$env_file" || _die "no $env_file (required for from-env mode)"
                _ensure_secrets_on_pi from-env
            else
                _ssh "cd $install_path && KUBECTL='microk8s kubectl' HERMOD_NAMESPACE=$namespace HERMOD_SECRETS_MODE=$mode bash -c 'source lib/ensure-secrets.sh && ensure_secrets'"
            fi
            ;;
        *) _die "ensure-secrets not implemented for kind=$kind" ;;
    esac
    _ok "$target Secrets reconciled (mode=$mode)"
}

# ── metrics (Prometheus exposition fetch) ──────────────────────────────────
# kubectl-exec into a coord pod and curl localhost:42069/metrics. Optional
# <pattern> argument is a grep filter applied to the output (e.g.
# "rate_limited|topic_limited"). Reading-only; uses kubectl exec inside the
# pod's network namespace so the cluster's NetworkPolicy doesn't have to
# allow scraper traffic from outside.
cmd_metrics() {
    local target="${1:-}"; [[ -z "$target" ]] && _die "usage: hermod.sh metrics <target> [pattern]"
    local pattern="${2:-}"
    local resolved; resolved="$(resolve_target "$target")" || exit $?
    eval "$resolved"
    local KCTL; KCTL="$(_kctl_for "$kind")"
    local pod_cmd="$KCTL -n $namespace get pods -l app=hermod-coordinator -o jsonpath='{.items[0].metadata.name}'"
    local pod
    case "$kind" in
        pc-kind|pc-kind-prod) pod="$(eval "$pod_cmd")" ;;
        pi-microk8s|pi-microk8s-prod|pi-microk8s-prod-edge) pod="$(_ssh "$pod_cmd")" ;;
        *) _die "metrics not implemented for kind=$kind" ;;
    esac
    [[ -z "$pod" ]] && _die "no hermod-coordinator pod found in $namespace"
    local exec_cmd="$KCTL -n $namespace exec $pod -c hermod-coordinator -- curl -sk https://localhost:42069/metrics"
    local raw
    case "$kind" in
        pc-kind|pc-kind-prod) raw="$(eval "$exec_cmd" 2>&1)" ;;
        pi-microk8s|pi-microk8s-prod|pi-microk8s-prod-edge) raw="$(_ssh "$exec_cmd" 2>&1)" ;;
    esac
    if [[ -n "$pattern" ]]; then
        echo "$raw" | grep -E "$pattern"
    else
        echo "$raw"
    fi
}

# ── secrets (display) ───────────────────────────────────────────────────────
# Pull the operator-relevant Secrets out of the cluster + present them
# with security context. Output goes to stdout; redirect with > to
# capture (caller is expected to chmod 0600 the file).
cmd_secrets() {
    local target="${1:-}"; [[ -z "$target" ]] && _die "usage: hermod.sh secrets <prod-kind|prod-pi>"
    local resolved; resolved="$(resolve_target "$target")" || exit $?
    eval "$resolved"
    local KCTL; KCTL="$(_kctl_for "$kind")"

    _get() {
        local secret="$1" key="$2"
        case "$kind" in
            pc-kind-prod)    $KCTL -n "$namespace" get secret "$secret" -o jsonpath="{.data.$key}" 2>/dev/null | base64 -d ;;
            pi-microk8s-prod|pi-microk8s-prod-edge) _ssh "$KCTL -n $namespace get secret $secret -o jsonpath='{.data.$key}'" 2>/dev/null | base64 -d ;;
        esac
    }

    cat <<EOF
─────────────────────────────────────────────────────────────────
  Hermod prod secrets — target: $target ($namespace)
─────────────────────────────────────────────────────────────────

  Vault42 seed credentials (rotate via dashboard after first login):
    viewer    viewer@hermod.local    $(_get vault42-seed-credentials viewer-password)
    user      user@hermod.local      $(_get vault42-seed-credentials user-password)
    operator  operator@hermod.local  $(_get vault42-seed-credentials operator-password)

  MQTT service credential (translators + bridges):
    user            $(_get hermod-secrets mqtt-username)
    pass            $(_get hermod-secrets mqtt-password)

  NanoMQ HTTP admin (broker management :8081):
    user            $(_get nanomq-http-admin http-user)
    pass            $(_get nanomq-http-admin http-password)

  Postgres app password (hermod_app role):
    pass            $(_get hermod-db-credentials hermod-db-password)

  Internal CA fingerprint (verify TLS chain matches):
    sha256          $(openssl x509 -in "$HOME/.hermod-prod-certs/ca/ca.crt" -noout -fingerprint -sha256 2>/dev/null | cut -d= -f2 || echo "(no local CA — issued only on Pi?)")

  Coord dashboard:
    URL             $(case "$kind" in pc-kind-prod) echo "https://localhost:42069/ (kind extraPort)" ;; pi-microk8s-prod|pi-microk8s-prod-edge) echo "https://${ssh_host##*@}:42069/ (Pi NodePort)" ;; esac)

─────────────────────────────────────────────────────────────────
  STORE THIS OUTPUT SECURELY. chmod 0600 if redirected to a file.
─────────────────────────────────────────────────────────────────
EOF
}

# ── TLS edge subcommands (tunnel-secret / dns-secret / cert) ───────────────
# Implementation lives in lib/cmd-tls.sh. Sourced after _kc, _ssh,
# _kctl_for, and resolve_target are all defined further up.
# shellcheck disable=SC1091
source "$REPO_ROOT/lib/cmd-tls.sh"

# ── teardown ────────────────────────────────────────────────────────────────
# Wipe the rendered overlay (kubectl delete -k). The hermod-prod Namespace
# is preserved via $patch: delete in overlays/prod/kustomization.yaml so cert
# Secrets survive. PVCs ARE in the render so postgres/hermod/z2m/nanomq data
# IS wiped — fresh slate on next install (matches the "no devices, no rules" semantics teardown is documented as).
cmd_teardown() {
    local target="${1:-}"; [[ -z "$target" ]] && _die "usage: hermod.sh teardown <target>"
    local resolved; resolved="$(resolve_target "$target")" || exit $?
    eval "$resolved"
    case "$kind" in
        pc-kind|pc-kind-prod)
            _have kubectl || _die "kubectl missing"
            _log "deleting overlay $overlay (cert Secrets in $namespace preserved)"
            kubectl --context "$kube_context" delete -k "$REPO_ROOT/$overlay" --ignore-not-found=true --wait=true || true
            if [[ "$kind" == "pc-kind-prod" ]]; then
                _log "removing all PVCs in $namespace (volumeClaimTemplates persist past delete -k; postgres role passwords would diverge from new vault42-secrets otherwise)"
                kubectl --context "$kube_context" -n "$namespace" delete pvc --all --ignore-not-found=true --wait=true || true
                _log "removing legacy dev hermod namespace if present"
                kubectl --context "$kube_context" delete namespace hermod --ignore-not-found=true --wait=true || true
            fi
            _ok "$target torn down (cert Secrets in $namespace preserved)"
            ;;
        pi-microk8s|pi-microk8s-prod|pi-microk8s-prod-edge)
            _have ssh || _die "ssh missing"
            # `delete -k --wait=true` blocks until every resource (and any
            # PVC/StatefulSet finalizer) is gone, which is 30-60s of silence
            # over SSH. Print what is being torn down up front and let
            # kubectl stream the per-resource deletion lines.
            _log "deleting overlay $overlay on Pi (--wait=true blocks until all resources are gone, 30-60s)"
            _ssh "cd $install_path && microk8s kubectl delete -k $overlay --ignore-not-found=true --wait=true -v=2 2>&1 || true"
            _ok "$target torn down on Pi"
            ;;
        *) _die "teardown not implemented for kind=$kind" ;;
    esac
}

# ── image-source (toggle local vs public images for prod-pi) ───────────────
# Default = local: prod-pi consumes hermod-coordinator:latest /
# lora2mqtt:latest, which assumes the operator built and pushed those
# images into microk8s' internal registry (or kind's), e.g. via
# `microk8s ctr image import`. Public = pull from ghcr.io published
# tags.
#
# Mechanic: edits prod-pi/kustomization.yaml in-place to add or remove
# `components: [../../components/images-public]`. The change is tracked
# by git; `image-source local` reverts to the committed default.
# Status read from the file itself (no side-car needed).
cmd_image_source() {
    local mode="${1:-status}"
    local kfile="$REPO_ROOT/kubernetes/overlays/prod-pi/kustomization.yaml"
    [[ -f "$kfile" ]] || _die "expected $kfile (run from repo root)"
    case "$mode" in
        status)
            if grep -q "components-public-marker" "$kfile" 2>/dev/null; then
                _log "image source: public (ghcr.io)"
            else
                _log "image source: local (microk8s/kind imported)"
            fi
            ;;
        local)
            # Strip the marker block if present.
            if grep -q "components-public-marker" "$kfile"; then
                # Remove the 3-line marker block atomically.
                local tmp; tmp="$(mktemp)"
                awk '/# components-public-marker BEGIN/{skip=1} !skip; /# components-public-marker END/{skip=0}' \
                    "$kfile" > "$tmp" && mv "$tmp" "$kfile"
                _ok "image source switched to local"
            else
                _log "image source already local; nothing to do"
            fi
            ;;
        public)
            if grep -q "components-public-marker" "$kfile"; then
                _log "image source already public; nothing to do"
            else
                cat >> "$kfile" <<'EOF'
# components-public-marker BEGIN — written by `hermod.sh image-source`
components:
  - ../../components/images-public
# components-public-marker END
EOF
                _ok "image source switched to public (ghcr.io)"
            fi
            ;;
        *) _die "usage: hermod.sh image-source <status|local|public>" ;;
    esac
}

# ── redeploy ────────────────────────────────────────────────────────────────
# Convenience: full teardown → install cycle in one command.
cmd_redeploy() {
    local target="${1:-}"; [[ -z "$target" ]] && _die "usage: hermod.sh redeploy <target>"
    _log "redeploy: tearing down $target"
    cmd_teardown "$target"
    _log "redeploy: brief pause for resources to settle"
    sleep 3
    _log "redeploy: reinstalling $target from scratch"
    cmd_install "$target"
}

# ── seed-users (re-provision the 3-role vault42 users) ──────────────────────
# Drops vault DB → re-applies vault42-seed-credentials Secret (so new
# viewer/user/operator passwords land if hermod-prod.env was edited) →
# re-grants schema-public privileges (DROP wipes them) → restarts
# vault42 so first-boot seeding runs with the current three-user
# roster (viewer@hermod.local viewer + user@hermod.local user +
# operator@hermod.local operator+admin).
cmd_seed_users() {
    local target="${1:-}"; [[ -z "$target" ]] && _die "usage: hermod.sh seed-users <prod-kind|prod-pi>"
    if [[ "${HERMOD_RESET_CONFIRM:-}" != "YES" ]]; then
        printf '%s\n' "$(_c '0;31' '[hermod WARN]')" \
            'seed-users is DESTRUCTIVE. Vault42 only seeds users on the first DB-empty boot,' \
            'so this command drops the vault PostgreSQL database and recreates it so the' \
            'render-seed init container runs again with the current seed.json (this is the' \
            'only way to land a roster change after Vault42 has been booted once).' \
            'All existing Vault42 users, refresh tokens and password rotations are lost.'
        if [[ -t 0 && -t 1 ]]; then
            local reply
            read -r -p "Type YES to drop the vault DB on $target: " reply
            [[ "$reply" == "YES" ]] || _die "aborted (got '$reply', expected 'YES')."
        else
            _die "non-interactive: re-run with HERMOD_RESET_CONFIRM=YES to drop the vault DB."
        fi
    fi
    local resolved; resolved="$(resolve_target "$target")" || exit $?
    eval "$resolved"
    local env_file="${HERMOD_PROD_ENV:-$REPO_ROOT/hermod-prod.env}"
    _env_present "$env_file" || _die "no $env_file (seed source). Bootstrap with hermod-prod.env.example first."

    case "$kind" in
        pc-kind-prod)
            _have kubectl || _die "kubectl missing"
            local KCTL="kubectl --context $kube_context"
            _log "step 1/4 — vault42 → 0 + drop vault DB + restore vault_mig grants"
            $KCTL -n "$namespace" scale deployment/vault42 --replicas=0
            $KCTL -n "$namespace" wait --for=delete pod -l app=vault42 --timeout=60s 2>&1 | tail -2 || true
            $KCTL -n "$namespace" exec statefulset/postgres -c postgres -- \
                psql -U postgres -d postgres \
                -c 'DROP DATABASE IF EXISTS vault WITH (FORCE);' \
                -c 'CREATE DATABASE vault OWNER vault_mig;' \
                -c 'GRANT ALL PRIVILEGES ON DATABASE vault TO vault_mig;' \
                -c 'GRANT CONNECT ON DATABASE vault TO vault_app;' 2>&1 | tail -5
            $KCTL -n "$namespace" exec statefulset/postgres -c postgres -- \
                psql -U postgres -d vault \
                -c 'GRANT ALL ON SCHEMA public TO vault_mig;' \
                -c 'GRANT USAGE ON SCHEMA public TO vault_app;' \
                -c 'ALTER SCHEMA public OWNER TO vault_mig;' \
                -c 'ALTER DEFAULT PRIVILEGES FOR ROLE vault_mig IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO vault_app;' \
                -c 'ALTER DEFAULT PRIVILEGES FOR ROLE vault_mig IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO vault_app;' 2>&1 | tail -5
            _log "step 2/4 — re-apply Secret (picks up VIEWER/OPERATOR password edits)"
            local _resolved; _resolved="$(_env_resolve_to_plaintext "$env_file")" \
                || _die "could not resolve $env_file (vault locked? run 'hermod.sh mimir unlock')"
            set -a && . "$_resolved" && set +a
            _env_resolve_cleanup
            HERMOD_NAMESPACE="$namespace" HERMOD_SECRETS_MODE=from-env KUBECTL="$KCTL" \
                ensure_secrets_with_users
            _log "step 3/4 — vault42 → 1 (re-seeds with the 4-user roster)"
            $KCTL -n "$namespace" scale deployment/vault42 --replicas=1
            $KCTL -n "$namespace" rollout status deployment/vault42 --timeout=120s
            _wipe_seed_secret
            _log "step 4/4 — coord rollout (refresh JWKS for new keys)"
            $KCTL -n "$namespace" rollout restart deployment/hermod-coordinator
            ;;
        pi-microk8s-prod|pi-microk8s-prod-edge)
            _have ssh || _die "ssh missing"
            _log "step 1/4 — vault42 → 0 + drop vault DB + restore vault_mig grants on Pi"
            _ssh "microk8s kubectl -n $namespace scale deployment/vault42 --replicas=0"
            _ssh "microk8s kubectl -n $namespace wait --for=delete pod -l app=vault42 --timeout=60s 2>&1 | tail -2 || true"
            _ssh "microk8s kubectl -n $namespace exec statefulset/postgres -c postgres -- psql -U postgres -d postgres -c 'DROP DATABASE IF EXISTS vault WITH (FORCE);' -c 'CREATE DATABASE vault OWNER vault_mig;' -c 'GRANT ALL PRIVILEGES ON DATABASE vault TO vault_mig;' -c 'GRANT CONNECT ON DATABASE vault TO vault_app;'" 2>&1 | tail -5
            _ssh "microk8s kubectl -n $namespace exec statefulset/postgres -c postgres -- psql -U postgres -d vault -c 'GRANT ALL ON SCHEMA public TO vault_mig;' -c 'GRANT USAGE ON SCHEMA public TO vault_app;' -c 'ALTER SCHEMA public OWNER TO vault_mig;' -c 'ALTER DEFAULT PRIVILEGES FOR ROLE vault_mig IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO vault_app;' -c 'ALTER DEFAULT PRIVILEGES FOR ROLE vault_mig IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO vault_app;'" 2>&1 | tail -5
            _log "step 2/4 — push hermod-prod.env to Pi + re-seed Secret"
            local _scp_src; _scp_src="$(_env_resolve_to_plaintext "$env_file")" \
                || _die "could not resolve $env_file (vault locked? run 'hermod.sh mimir unlock')"
            scp -i "$ssh_key" -o UserKnownHostsFile="$known_hosts" -o IdentitiesOnly=yes -o StrictHostKeyChecking=yes \
                "$_scp_src" "$ssh_host:$install_path/hermod-prod.env"
            _env_resolve_cleanup
            _ensure_secrets_on_pi from-env
            _log "step 3/4 — vault42 → 1 on Pi"
            _ssh "microk8s kubectl -n $namespace scale deployment/vault42 --replicas=1 && microk8s kubectl -n $namespace rollout status deployment/vault42 --timeout=120s"
            _wipe_seed_secret
            _log "step 4/4 — coord rollout on Pi"
            _ssh "microk8s kubectl -n $namespace rollout restart deployment/hermod-coordinator"
            ;;
        *) _die "seed-users not implemented for kind=$kind (use prod-kind or prod-pi)" ;;
    esac

    _ok "seeded 3 vault42 users on $target. Login with any of:"
    printf '  viewer    viewer@hermod.local    %s\n' "${HERMOD_VAULT42_VIEWER_PASSWORD:-?}"
    printf '  user      user@hermod.local      %s\n' "${HERMOD_VAULT42_USER_PASSWORD:-?}"
    printf '  operator  operator@hermod.local  %s   (operator + admin)\n' "${HERMOD_VAULT42_OPERATOR_PASSWORD:-?}"
}

# ── kick (force rollout, no rebuild) ────────────────────────────────────────
# Cheap way to refresh pods so they re-read current ConfigMaps + Secrets,
# without paying for an image rebuild. Used when a config-only change
# needs to land on running pods (e.g., editing a configmap in the overlay
# and `apply -k` doesn't restart pods because the manifest hash didn't
# change).
cmd_kick() {
    local target="${1:-}"; [[ -z "$target" ]] && _die "usage: hermod.sh kick <target> [deployment]"
    local deploy="${2:-}"
    local resolved; resolved="$(resolve_target "$target")" || exit $?
    eval "$resolved"
    local KCTL; KCTL="$(_kctl_for "$kind")"
    local args
    if [[ -n "$deploy" ]]; then
        args="deployment/$deploy"
    else
        # All hermod-prod deployments. Order = least-disruptive first
        # (translators), coord last so dashboard reload comes after deps.
        args="deployment/lora2mqtt deployment/zigbee2mqtt deployment/wifi2mqtt deployment/ble2mqtt deployment/nanomq deployment/vault42 deployment/hermod-coordinator"
    fi
    case "$kind" in
        pc-kind|pc-kind-prod)    $KCTL -n "$namespace" rollout restart $args ;;
        pi-microk8s|pi-microk8s-prod|pi-microk8s-prod-edge) _ssh "$KCTL -n $namespace rollout restart $args" ;;
        *) _die "kick not implemented for kind=$kind" ;;
    esac
    _ok "rollout restart fired on $target ($args)"
}

# ── logs ────────────────────────────────────────────────────────────────────
# Wrap kubectl logs for the right context. Without args = list all pods +
# tail every container 30 lines. With <pod> = full log of that pod, every
# container concatenated. With <pod> <container> = scoped to one container.
cmd_logs() {
    local target="${1:-}"; [[ -z "$target" ]] && _die "usage: hermod.sh logs <target> [pod] [container]"
    local pod="${2:-}" container="${3:-}"
    local resolved; resolved="$(resolve_target "$target")" || exit $?
    eval "$resolved"
    local KCTL; KCTL="$(_kctl_for "$kind")"
    _kc() { case "$kind" in pi-microk8s|pi-microk8s-prod|pi-microk8s-prod-edge) _ssh "$KCTL $*" ;; *) $KCTL "$@" ;; esac; }

    if [[ -z "$pod" ]]; then
        _log "tail every pod (last 30 lines, all containers) on $target"
        local pods
        pods="$(_kc -n "$namespace" get pods -o name 2>&1 | grep '^pod/')"
        for p in $pods; do
            printf '\n%s %s\n' "$(_c '1;36' '===')" "$(_c '1;37' "${p#pod/}")"
            _kc -n "$namespace" logs "$p" --all-containers --prefix --tail=30 2>&1 || true
        done
    elif [[ -z "$container" ]]; then
        _log "logs for pod=$pod (all containers) on $target"
        _kc -n "$namespace" logs "$pod" --all-containers --prefix 2>&1
    else
        _log "logs for pod=$pod container=$container on $target"
        _kc -n "$namespace" logs "$pod" -c "$container" 2>&1
    fi
}

# ── status ──────────────────────────────────────────────────────────────────
cmd_status() {
    local target="${1:-}"; [[ -z "$target" ]] && _die "usage: hermod.sh status <target>"
    local resolved; resolved="$(resolve_target "$target")" || exit $?
    eval "$resolved"

    case "$kind" in
        pi-microk8s|pi-microk8s-prod|pi-microk8s-prod-edge)
            _have ssh || _die "ssh missing"
            _ssh "microk8s kubectl -n $namespace get deploy,pod,svc"
            ;;
        pc-kind|pc-kind-prod)
            _have kubectl || _die "kubectl missing"
            kubectl --context "$kube_context" -n "$namespace" get deploy,pod,svc
            ;;
        *) _die "status not implemented for kind=$kind" ;;
    esac
}

# ── doctor ──────────────────────────────────────────────────────────────────
# Verify host has what each path needs. Compose path needs only docker.
# k8s paths additionally need kubectl, ssh, rsync.
cmd_update_repo() {
    # Safe `git pull --ff-only` of the Hermod source tree. Refuses to run
    # when the working tree is dirty so the operator never silently loses
    # local edits, and uses --ff-only so a divergent local branch is
    # surfaced (operator must rebase or hard-reset themselves).
    cd "$REPO_ROOT" || _die "cannot cd to $REPO_ROOT"
    [[ -d .git ]] || _die "$REPO_ROOT is not a git checkout (was the repo cloned, or copied as a tarball?)"
    _have git || _die "git not on PATH"
    if ! git diff --quiet --ignore-submodules HEAD 2>/dev/null; then
        _die "working tree has uncommitted changes; commit, stash, or revert them before update-repo"
    fi
    if ! git diff --cached --quiet --ignore-submodules 2>/dev/null; then
        _die "index has staged changes; commit or unstage them before update-repo"
    fi
    local branch; branch="$(git rev-parse --abbrev-ref HEAD 2>/dev/null)"
    [[ -n "$branch" && "$branch" != "HEAD" ]] || _die "detached HEAD; check out a branch before update-repo"
    _log "fetching origin"
    git fetch --prune origin
    _log "fast-forward $branch from origin/$branch"
    if ! git pull --ff-only origin "$branch"; then
        _die "pull is non-fast-forward; resolve manually (rebase or reset --hard) and re-run"
    fi
    _ok "repo updated to $(git rev-parse --short HEAD) on $branch"
}

cmd_doctor() {
    local fail=0
    local PASS FAIL WARN
    PASS="$(_doctor_pass)"; FAIL="$(_doctor_fail)"; WARN="$(_doctor_warn)"
    printf '%s\n\n' "$(_c '1;36' "Hermod doctor (OS: $OS)")"

    # ── compose path ────────────────────────────────────────────────
    printf '%s\n' "$(_c '1;37' '## compose path (single-host evaluation)')"
    if _have docker; then printf '  %s docker (%s)\n' "$PASS" "$(docker --version 2>/dev/null | head -1)"
    else printf '  %s docker not found, install Docker Desktop or docker-ce\n' "$FAIL"; fail=1
    fi
    local compose; compose="$(_compose_cmd 2>/dev/null)"
    if [[ -n "$compose" ]]; then printf '  %s compose impl: %s\n' "$PASS" "$compose"
    else printf '  %s no compose found, install the compose plugin or docker-compose\n' "$FAIL"; fail=1
    fi
    if _have docker && docker info >/dev/null 2>&1; then printf '  %s docker daemon reachable\n' "$PASS"
    elif _have docker;                                 then printf '  %s docker daemon unreachable, start Docker Desktop or systemctl start docker\n' "$WARN"
    fi
    printf '\n'

    # ── kubernetes path ─────────────────────────────────────────────
    printf '%s\n' "$(_c '1;37' '## kubernetes path (prod-pi / prod-kind)')"
    for tool in kubectl ssh rsync; do
        if _have "$tool"; then printf '  %s %s\n' "$PASS" "$tool"
        else printf '  %s %s not found, needed for k8s targets\n' "$WARN" "$tool"
        fi
    done
    if _have kind || _have podman; then
        _have kind   && printf '  %s kind\n'   "$PASS"
        _have podman && printf '  %s podman\n' "$PASS"
    else
        printf '  %s neither kind nor podman found, prod-kind installs need at least one\n' "$WARN"
    fi
    printf '\n'

    # ── pi provisioning path ────────────────────────────────────────
    printf '%s\n' "$(_c '1;37' '## pi provisioning path (greenfield Pi installs)')"
    if _have ansible-playbook; then printf '  %s ansible-playbook\n' "$PASS"
    else printf '  %s ansible-playbook not found, run apt/dnf install ansible to provision a fresh Pi\n' "$WARN"
    fi
    for tool in xz wget curl; do
        if _have "$tool"; then printf '  %s %s\n' "$PASS" "$tool"
        else printf '  %s %s not found, SD-card flash needs it\n' "$WARN" "$tool"
        fi
    done
    printf '\n'

    # ── general utilities ───────────────────────────────────────────
    printf '%s\n' "$(_c '1;37' '## general utilities')"
    for tool in jq openssl bash; do
        if _have "$tool"; then printf '  %s %s\n' "$PASS" "$tool"
        else printf '  %s %s not found\n' "$FAIL" "$tool"; fail=1
        fi
    done
    if _have age || _have age-keygen; then printf '  %s age (mimir vault backend)\n' "$PASS"
    else printf '  %s age not found, mimir vault encryption is unavailable\n' "$WARN"
    fi
    printf '\n'

    # ── repository state ────────────────────────────────────────────
    printf '%s\n' "$(_c '1;37' '## repository state')"
    if [[ -f "$REPO_ROOT/hermod-prod.env" ]]; then
        printf '  %s hermod-prod.env present\n' "$PASS"
    elif [[ -f "$REPO_ROOT/hermod-prod.env.mimir" ]]; then
        printf '  %s hermod-prod.env.mimir present (encrypted by mimir)\n' "$PASS"
    elif [[ -f "$REPO_ROOT/hermod-prod.env.example" ]]; then
        printf '  %s hermod-prod.env not found, copy from hermod-prod.env.example before install\n' "$WARN"
    else
        printf '  %s no env file or example, repository may be incomplete\n' "$FAIL"; fail=1
    fi
    if [[ -d "$REPO_ROOT/kubernetes/base" && -d "$REPO_ROOT/kubernetes/overlays" ]]; then
        printf '  %s kubernetes/base + overlays\n' "$PASS"
    else
        printf '  %s kubernetes manifests missing\n' "$FAIL"; fail=1
    fi
    if [[ -d "$REPO_ROOT/ansible" ]]; then printf '  %s ansible/ tree\n' "$PASS"
    else printf '  %s ansible/ tree missing, Pi provisioning will fail\n' "$WARN"
    fi
    printf '\n'

    if [[ $fail -eq 0 ]]; then _ok "doctor: required dependencies satisfied"
    else _warn "doctor: required dependencies missing (see above)"
    fi
    return $fail
}

# ── interactive TUI ─────────────────────────────────────────────────────────
_menu_draw() {
    clear 2>/dev/null || printf '\n\n'
    _hr 58
    printf '  %s\n' "$(_c '1;36' 'Hermod IoT Translator — Ops CLI')"
    printf '  OS: %s    repo: %s\n' "$OS" "$(basename "$REPO_ROOT")"
    _hr 58
    printf '\n'
    printf '  %s (single-host docker)\n' "$(_c '1;37' 'COMPOSE')"
    printf '    1) Start the stack (up + build)\n'
    printf '    2) Stop the stack (down)\n'
    printf '    3) Show status\n'
    printf '    4) Tail logs (all services)\n'
    printf '    5) Tail logs (one service)\n'
    printf '    6) Restart a service\n'
    printf '    7) Pull/rebuild images\n'
    printf '    r) Reset (down + WIPE all volumes)\n'
    printf '\n'
    printf '  %s (advanced)\n' "$(_c '1;37' 'KUBERNETES TARGETS')"
    printf '    8) Update Pi 5 (pi5-live)\n'
    printf '    9) Update PC kind (kind-hermod)\n'
    printf '   10) Status: Pi 5\n'
    printf '   11) Status: PC kind\n'
    printf '   12) Install Pi 5 from scratch\n'
    printf '   13) Install PC kind from scratch\n'
    printf '\n'
    printf '  %s (require base prod-pi installed first)\n' "$(_c '1;37' 'EDGE-TLS PROFILES')"
    printf '   20) Apply LE cert-only (DNS-01)              %s\n' "$(_c '0;32' 'passive')"
    printf '   21) Apply LE + Ingress (PUBLIC EXPOSURE)     %s\n' "$(_c '1;31' 'ACTIVE')"
    printf '   22) Apply CF Tunnel (parked, replicas=0)     %s\n' "$(_c '0;32' 'passive')"
    printf '   23) Apply CF Tunnel (open, PUBLIC EXPOSURE)  %s\n' "$(_c '1;31' 'ACTIVE')"
    printf '   24) Apply CF Zero Trust (Tunnel + Access)    %s\n' "$(_c '1;31' 'ACTIVE')"
    printf '   25) Write Cloudflared TUNNEL_TOKEN Secret\n'
    printf '   26) Write Cloudflare DNS API token Secret\n'
    printf '\n'
    printf '    d) Doctor (verify deps)\n'
    printf '    h) Help text\n'
    printf '    q) Quit\n'
    printf '\n'
    _hr 58
}

# Two-step typed-confirmation gate for menu-driven active installs. Prints
# the warning, requires the operator to type the target name verbatim, and
# returns 0 on confirm, 1 on cancel. Cancels on empty input.
_menu_go_live_confirm() {
    local target="$1"
    local warning="$2"
    printf '\n%s %s\n' "$(_c '1;31' '!! ACTIVE OVERLAY !!')" "$warning"
    printf '\n%s\n' "Apply will set HERMOD_GO_LIVE=YES."
    printf '%s ' "Type the target name '$target' to proceed (anything else cancels):"
    local typed; read -r typed
    if [[ "$typed" = "$target" ]]; then
        return 0
    else
        _log "cancelled — typed input did not match '$target'"
        return 1
    fi
}

# ── config (manage ~/.config/hermod/config) ─────────────────────────────────
# Per-machine settings, sourced before hermod-prod.env so per-repo files still
# win. Stored as KEY=VALUE pairs; only HERMOD_* keys are accepted to avoid
# leaking arbitrary shell into a sourced file. Mode 0600 — keys/IPs in here.
cmd_config() {
    local sub="${1:-show}"
    [[ $# -gt 0 ]] && shift
    local cfg="$HERMOD_GLOBAL_CONFIG"
    case "$sub" in
        show)
            printf 'config: %s%s\n\n' "$cfg" "$([[ -f $cfg ]] || printf ' (not yet written)')"
            local -a keys=(HERMOD_PI_SSH_HOST HERMOD_PI_SSH_USER HERMOD_PI_SSH_KEY HERMOD_PI_KNOWN_HOSTS HERMOD_PROD_ENV)
            local k v src
            for k in "${keys[@]}"; do
                v="${!k:-}"
                if [[ -z "$v" ]]; then src='unset'
                elif [[ -f "$cfg" ]] && grep -qE "^${k}=" "$cfg" 2>/dev/null; then src='config'
                else src='env'
                fi
                printf '  %-22s = %s  (%s)\n' "$k" "${v:-<unset>}" "$src"
            done
            printf '\nresolved Pi SSH endpoint: %s@%s (key=%s)\n' "$PI_SSH_USER" "$PI_SSH_HOST" "$PI_SSH_KEY"
            ;;
        set)
            local kv="${1:-}"
            [[ "$kv" == *=* ]] || _die "usage: hermod.sh config set KEY=VALUE  (e.g. HERMOD_PI_SSH_HOST=10.42.0.58)"
            local key="${kv%%=*}" val="${kv#*=}"
            [[ "$key" =~ ^HERMOD_[A-Z0-9_]+$ ]] || _die "key must match HERMOD_[A-Z0-9_]+ (got: $key)"
            mkdir -p "$(dirname "$cfg")"
            [[ -f "$cfg" ]] || { : > "$cfg"; chmod 600 "$cfg"; }
            local tmp; tmp="$(mktemp "${cfg}.XXXXXX")"
            chmod 600 "$tmp"
            awk -v k="$key" -v v="$val" '
                BEGIN { wrote = 0 }
                $0 ~ "^"k"=" { print k"="v; wrote = 1; next }
                { print }
                END { if (!wrote) print k"="v }
            ' "$cfg" > "$tmp" && mv "$tmp" "$cfg"
            _ok "$key set in $cfg"
            ;;
        unset)
            local key="${1:-}"
            [[ -n "$key" ]] || _die "usage: hermod.sh config unset KEY"
            [[ -f "$cfg" ]] || _die "no config file: $cfg"
            local tmp; tmp="$(mktemp "${cfg}.XXXXXX")"
            chmod 600 "$tmp"
            grep -vE "^${key}=" "$cfg" > "$tmp" || true
            mv "$tmp" "$cfg"
            _ok "$key removed from $cfg"
            ;;
        edit)
            mkdir -p "$(dirname "$cfg")"
            [[ -f "$cfg" ]] || { : > "$cfg"; chmod 600 "$cfg"; }
            "${EDITOR:-vi}" "$cfg"
            chmod 600 "$cfg"
            ;;
        path)
            printf '%s\n' "$cfg"
            ;;
        init)
            local inv_dir="$HOME/.hermod-pi/inventories"
            local inv; inv="$(ls -1 "$inv_dir"/*.hosts.yml 2>/dev/null | head -1)"
            [[ -n "$inv" ]] || _die "no inventory in $inv_dir; run 'hermod.sh config set HERMOD_PI_SSH_HOST=...' manually"
            local host key user
            host="$(awk '/ansible_host:/{print $2; exit}' "$inv")"
            key="$(awk '/ansible_ssh_private_key_file:/{print $2; exit}' "$inv")"
            user="$(awk '/ansible_user:/{print $2; exit}' "$inv")"
            [[ -n "$host" && -n "$key" && -n "$user" ]] || _die "could not parse host/key/user from $inv"
            mkdir -p "$(dirname "$cfg")"
            cat > "$cfg" <<EOF
# Hermod global config — auto-generated $(date -u +%Y-%m-%dT%H:%M:%SZ)
# Source: $inv
# Per-shell exports and per-repo hermod-prod.env still override these.
HERMOD_PI_SSH_HOST=$host
HERMOD_PI_SSH_USER=$user
HERMOD_PI_SSH_KEY=$key
HERMOD_PI_KNOWN_HOSTS=$HOME/.hermod-pi/known_hosts
EOF
            chmod 600 "$cfg"
            _ok "wrote $cfg from $inv"
            sed 's/^/  /' "$cfg" >&2
            ;;
        *)
            _die "usage: hermod.sh config <show|set KEY=VALUE|unset KEY|edit|init|path>"
            ;;
    esac
}

cmd_menu() {
    while true; do
        _menu_draw
        local choice svc
        printf '%s ' "$(_c '1;36' 'Choose:')"
        read -r choice
        printf '\n'
        case "${choice:-}" in
            1)  cmd_compose up ;;
            2)  cmd_compose down ;;
            3)  cmd_compose status ;;
            4)  cmd_compose logs ;;
            5)  printf 'service name (blank = all): '; read -r svc
                if [[ -n "$svc" ]]; then cmd_compose logs "$svc"; else cmd_compose logs; fi ;;
            6)  printf 'service to restart (blank = all): '; read -r svc
                if [[ -n "$svc" ]]; then cmd_compose restart "$svc"; else cmd_compose restart; fi ;;
            7)  cmd_compose pull && cmd_compose build ;;
            r|R) printf 'wipe ALL compose volumes? [y/N] '; read -r ans
                 [[ "$ans" =~ ^[yY]$ ]] && cmd_compose reset || _log "cancelled" ;;
            8)  cmd_update pi5-live ;;
            9)  cmd_update kind-hermod ;;
            10) cmd_status pi5-live ;;
            11) cmd_status kind-hermod ;;
            12) cmd_install pi5-live ;;
            13) cmd_install kind-hermod ;;
            20) cmd_install prod-pi-letsencrypt ;;
            21) _menu_go_live_confirm prod-pi-letsencrypt-ingress \
                    "This will create an nginx Ingress and EXPOSE the Coordinator publicly on hermod.<your-domain>." \
                    && HERMOD_GO_LIVE=YES cmd_install prod-pi-letsencrypt-ingress ;;
            22) cmd_install prod-pi-letsencrypt-cloudflare-tunnel ;;
            23) _menu_go_live_confirm prod-pi-letsencrypt-cloudflare-tunnel-active \
                    "This will scale cloudflared to 2 replicas and OPEN the Cloudflare Tunnel." \
                    && HERMOD_GO_LIVE=YES cmd_install prod-pi-letsencrypt-cloudflare-tunnel-active ;;
            24) _menu_go_live_confirm prod-pi-cloudflare-zero-trust \
                    "This activates the tunnel AND requires zero-trust-marker.yaml is configured (init container fails closed otherwise). The CF Access policy must already exist in your dashboard." \
                    && HERMOD_GO_LIVE=YES cmd_install prod-pi-cloudflare-zero-trust ;;
            25) cmd_tunnel_secret prod-pi ;;
            26) cmd_dns_secret prod-pi ;;
            d|D) cmd_doctor || true ;;
            h|H) cmd_help ;;
            q|Q) _ok "bye"; return 0 ;;
            "")  continue ;;  # empty Enter just redraws menu
            *)   _warn "unknown choice: $choice" ;;
        esac
        printf '\n%s ' "$(_c '0;36' 'Press Enter to continue…')"
        read -r _
    done
}

# ── pi provisioning (flash → wait → deploy) ──────────────────────────────
# Implementation lives in lib/cmd-pi.sh — thin delegates to the
# lib/pi-installer/hermod-pi tool that owns flash/wait/ansible bring-up.
# shellcheck disable=SC1091
source "$REPO_ROOT/lib/cmd-pi.sh"

# ── help ────────────────────────────────────────────────────────────────────
cmd_help() {
    cat <<EOF
hermod.sh — unified Hermod ops CLI

Usage:
  hermod.sh                                  Interactive menu (TUI)
  hermod.sh config <subcmd>                  Manage ~/.config/hermod/config (Pi SSH coords + env)
                                               subcmds: show | set KEY=VALUE | unset KEY | edit | init | path
                                               'init' bootstraps from ~/.hermod-pi/inventories/*.hosts.yml
  hermod.sh compose <action> [svc]           Single-host docker compose
  hermod.sh install  <target>                Provision target from scratch
  hermod.sh update   <target> [--rebuild <coord|lora2mqtt|all>]
                                             Rsync + kustomize apply + rollout. With --rebuild, also docker-builds
                                             the named image(s) on the Pi and restarts the matching deployment.
  hermod.sh teardown <target>                kubectl delete -k overlay (cert Secrets preserved)
  hermod.sh redeploy <target>                teardown + install (full cycle)
  hermod.sh change-password <target>         rotate vault42 seed creds (drops vault DB + reseed)
  hermod.sh rotate-certs <target>            roll mTLS leaves (CA preserved; coord hot-reloads, others restart)
  hermod.sh reset <target>                   DESTRUCTIVE clean-slate (HERMOD_RESET_CONFIRM=YES required)
  hermod.sh pi-reset                         alias for: reset prod-pi
  hermod.sh roll-jwks <target>               restart vault42 + coord (refresh JWKS without rotating SIGNING_KEY)
  hermod.sh reset-db <target>                drop vault + hermod DBs + restart consumers (HERMOD_RESET_CONFIRM=YES)
  hermod.sh protocol <on|off> <name> <tgt>   scale lora|zigbee|ble|wifi to 0/1 replicas
  hermod.sh limiter <target> <knob> [on|off] hot-toggle ingress limiter on the live coord
                                               knobs: rate | dedup | show
                                               permanent change: edit kubernetes/overlays/<target>/coord-prod.yaml
  hermod.sh secrets <target>                 print current Vault42/MQTT/PG/CA fingerprints from cluster Secrets
  hermod.sh metrics <target> [pattern]       fetch coord /metrics; optional grep-style pattern filter
  hermod.sh seed-users <target>              re-render vault42 seed Secret + restart vault42 to import users
  hermod.sh users <action> [args]            local seed-users.json editor (init/list/add/remove/set-role/set-password)
                                               source-of-truth for vault42 first-boot accounts; pushed on install/update
  hermod.sh kick <target> [deployment]       rolling restart (no rebuild). With [deployment] only that one rolls; without, all Hermod-owned ones do.
  hermod.sh tunnel-secret <target> [flags]   write cloudflared TUNNEL_TOKEN Secret to target
                                               flags: --from-file PATH (default: silent prompt; HERMOD_TUNNEL_TOKEN_FILE env override)
                                               extracts the JWT from any pasted text (e.g. the install command)
  hermod.sh dns-secret <target> [flags]      write Cloudflare DNS API token Secret to cert-manager namespace
                                               flags: --from-file PATH (default: silent prompt; HERMOD_DNS_API_TOKEN_FILE env override)
                                               required scopes: Zone:DNS:Edit + Zone:Zone:Read
  hermod.sh cert <target> <subcmd> [args]    cert-manager wrappers (status | request <hostname> | show [name])
  hermod.sh logs <target> [pod] [container]  tail logs (all pods if no pod given)
  hermod.sh status   <target>                Show pod status
  hermod.sh doctor                           Verify deps for each path

Pi greenfield provisioning (cloud-init image → flash → ansible install):
  hermod.sh flash <config.yaml> [<device>]   Stage 1: build image + write SD
  hermod.sh wait-pi <hostname> [timeout]     Stage 2a: mDNS + first-boot marker
  hermod.sh provision <config.yaml> [<dev>]  Stage 1+2: full bring-up
  hermod.sh pi-status <hostname>             ansible verify (host health)
  hermod.sh pi-uninstall <hostname>          DESTRUCTIVE: remove microk8s + state
  hermod.sh pi-keys                          list managed Pi keypairs
  hermod.sh pi-doctor                        verify pi-installer deps

Compose actions: up | down | restart | logs | status | build | pull
Targets:         pi5-live | kind-hermod | prod-kind | prod-pi
Edge-TLS targets (require base prod-pi installed; *-active and *-zero-trust
need HERMOD_GO_LIVE=YES to install — they expose traffic publicly):
                 prod-pi-letsencrypt                          (passive: cert acquired, no Ingress)
                 prod-pi-letsencrypt-ingress                  (active: nginx Ingress + HSTS preload)
                 prod-pi-letsencrypt-cloudflare-tunnel        (passive: tunnel parked at replicas=0)
                 prod-pi-letsencrypt-cloudflare-tunnel-active (active: tunnel scaled to 2)
                 prod-pi-cloudflare-zero-trust                (active: tunnel + CF Access gate)

Examples:
  hermod.sh                                  # menu
  hermod.sh compose up
  hermod.sh compose logs coordinator
  hermod.sh update pi5-live
  hermod.sh status kind-hermod
  hermod.sh doctor

OS: $OS    color: $USE_COLOR
EOF
}

# ── tui ─────────────────────────────────────────────────────────────────────
# Two-pane keyboard-driven console. Pure bash + ANSI; no extra deps.
# Sources lib/hermod-tui.sh which orchestrates the existing cmd_* handlers.
cmd_tui() {
    if [[ ! -t 0 || ! -t 1 ]]; then
        _die "TUI needs an interactive terminal. Pipe-friendly subcommands are listed under 'hermod.sh help'."
    fi
    # shellcheck disable=SC1091
    source "$REPO_ROOT/lib/hermod-tui.sh"
    _tui_main
}

# ── mimir (PIN-protected env vault) ────────────────────────────────────────
# Thin dispatcher around lib/mimir.sh. The TUI calls into it directly;
# this command exists for headless / scripted use (and as the recovery
# path when an operator wants to rekey or unlock outside the TUI).
cmd_mimir() {
    local action="${1:-status}"; shift || true
    # shellcheck disable=SC1091
    source "$REPO_ROOT/lib/mimir.sh"
    local default_env="${HERMOD_PROD_ENV:-$REPO_ROOT/hermod-prod.env}"
    case "$action" in
        init)    mimir_init   "${1:-$default_env}" ;;
        unlock)  mimir_unlock "${1:-$default_env}" "${2:-}" ;;
        lock)    mimir_lock   "${1:-}" ;;
        rekey)   mimir_rekey  "${1:-$default_env}" ;;
        status)  mimir_status "${1:-}" ;;
        load)    mimir_load   "${1:-$default_env}" ;;
        help|-h|--help)
            cat <<EOF
hermod.sh mimir <action> [file]

  init    [file]    encrypt <file> with a PIN (Enter for no-PIN)
  unlock  [file]    populate session cache (force-prompts with --force)
  lock    [file]    shred session cache for <file>, or all when omitted
  rekey   [file]    decrypt + re-encrypt with a new PIN
  status  [file]    show cache state (all .mimir under repo by default)
  load    [file]    print decrypted contents to stdout

Default <file> is \$HERMOD_PROD_ENV or \$REPO_ROOT/hermod-prod.env.
Tunables: HERMOD_MIMIR_TTL (idle seconds, default 600),
          HERMOD_MIMIR_ITER (PBKDF2 iterations, default 600000).
EOF
            ;;
        *) _die "mimir: unknown action '$action' (try 'hermod.sh mimir help')" ;;
    esac
}

# ── dispatch ────────────────────────────────────────────────────────────────
case "${1:-tui}" in
    tui)             shift || true; cmd_tui      "$@" ;;
    mimir)           shift; cmd_mimir            "$@" ;;
    compose)         shift; cmd_compose         "$@" ;;
    config)          shift; cmd_config          "$@" ;;
    install)         shift; cmd_install         "$@" ;;
    update)          shift; cmd_update          "$@" ;;
    teardown)        shift; cmd_teardown        "$@" ;;
    redeploy)        shift; cmd_redeploy        "$@" ;;
    image-source)    shift; cmd_image_source    "$@" ;;
    change-password) shift; cmd_change_password "$@" ;;
    rotate-certs)    shift; cmd_rotate_certs    "$@" ;;
    reset)           shift; cmd_reset           "$@" ;;
    pi-reset)        cmd_reset prod-pi ;;
    roll-jwks)       shift; cmd_roll_jwks       "$@" ;;
    reset-db)        shift; cmd_reset_db        "$@" ;;
    protocol)        shift; cmd_protocol        "$@" ;;
    limiter)         shift; cmd_limiter         "$@" ;;
    secrets)         shift; cmd_secrets         "$@" ;;
    metrics)         shift; cmd_metrics         "$@" ;;
    ensure-secrets)  shift; cmd_ensure_secrets  "$@" ;;
    cleanup)         shift; cmd_cleanup         "$@" ;;
    tunnel-secret)   shift; cmd_tunnel_secret   "$@" ;;
    dns-secret)      shift; cmd_dns_secret      "$@" ;;
    cert)            shift; cmd_cert            "$@" ;;
    seed-users)      shift; cmd_seed_users      "$@" ;;
    users)           shift; cmd_users           "$@" ;;
    kick)            shift; cmd_kick            "$@" ;;
    logs)            shift; cmd_logs            "$@" ;;
    status)          shift; cmd_status          "$@" ;;
    doctor)          shift; cmd_doctor          "$@" ;;
    update-repo)     shift || true; cmd_update_repo "$@" ;;
    flash)           shift; cmd_flash           "$@" ;;
    wait-pi)         shift; cmd_wait_pi         "$@" ;;
    provision)       shift; cmd_provision       "$@" ;;
    pi-status)       shift; cmd_pi_status       "$@" ;;
    pi-uninstall)    shift; cmd_pi_uninstall    "$@" ;;
    pi-keys)         shift; cmd_pi_keys         "$@" ;;
    pi-doctor)       shift; cmd_pi_doctor       "$@" ;;
    menu)            cmd_menu                   "$@" ;;  # no shift: $1 may be unset (default-menu via ${1:-menu}); bash 5.3 shift errors under set -e
    -h|--help|help) cmd_help ;;
    *)       _die "unknown command: $1 (run 'hermod.sh help' for usage)" ;;
esac
