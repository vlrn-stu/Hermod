#!/usr/bin/env bash
# cmd-tls.sh — Hermod TLS-edge subcommands.
#   - tunnel-secret: write the cloudflared TUNNEL_TOKEN Secret
#   - dns-secret:    write the cert-manager Cloudflare API token Secret
#   - cert:          cert-manager wrappers (status / request / show)
#
# All three feed Secrets into the cluster from operator input and never
# leak the value via argv (kubectl reads YAML from stdin) or shell
# history (read -rs).
#
# Sourced by hermod.sh once $REPO_ROOT, _kctl_for, resolve_target, _kc,
# _ssh and the lib/lib.sh helpers are loaded.

[[ -n "${_HERMOD_CMD_TLS_LOADED:-}" ]] && return 0
_HERMOD_CMD_TLS_LOADED=1

# ── tunnel-secret (cloudflared TUNNEL_TOKEN) ───────────────────────────────
# Input is filtered through a JWT regex so pasting the entire
# `cloudflared service install --token …` line is accepted; only the
# JWT lands in the Secret.
_extract_jwt() {
    printf '%s' "$1" \
        | grep -oE 'eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+' \
        | head -1
}

cmd_tunnel_secret() {
    local target="${1:-}"
    [[ -z "$target" ]] && _die "usage: hermod.sh tunnel-secret <target> [--from-file PATH]
  Default reads the token from a silent terminal prompt. Paste the
  token or the entire 'cloudflared service install --token …' line —
  the JWT is extracted automatically. HERMOD_TUNNEL_TOKEN_FILE may
  override the default file path."
    shift

    local source_path="${HERMOD_TUNNEL_TOKEN_FILE:-}"
    while [[ $# -gt 0 ]]; do
        case "$1" in
            --from-file) source_path="${2:?--from-file needs a PATH}"; shift 2 ;;
            *) _die "unknown tunnel-secret flag: $1" ;;
        esac
    done

    local resolved; resolved="$(resolve_target "$target")" || exit $?
    eval "$resolved"
    local KCTL; KCTL="$(_kctl_for "$kind")"

    local raw=""
    if [[ -n "$source_path" ]]; then
        [[ -r "$source_path" ]] || _die "cannot read $source_path"
        raw="$(cat "$source_path")"
        _log "reading token from $source_path"
    else
        _log "paste the tunnel token (input hidden) and press Enter:"
        IFS= read -rs raw
        printf '\n'
        [[ -n "$raw" ]] || _die "empty input"
    fi

    local token; token="$(_extract_jwt "$raw")"
    raw=""
    [[ -n "$token" ]] || _die "no JWT (eyJ…xx.yy.zz) found in input"
    [[ ${#token} -ge 100 ]] || _warn "token is unusually short (${#token} chars) — proceeding"

    _log "applying Secret cloudflared-token to namespace=$namespace"
    _kc apply -n "$namespace" -f - <<EOF
apiVersion: v1
kind: Secret
metadata:
  name: cloudflared-token
type: Opaque
stringData:
  TUNNEL_TOKEN: "$token"
EOF
    token=""
    _ok "Secret cloudflared-token written to $namespace"
}

# ── dns-secret (cert-manager Cloudflare DNS API token) ─────────────────────
# Required token scopes (custom token from CF dashboard):
#   * Zone:DNS:Edit on the zone(s) cert-manager will manage
#   * Zone:Zone:Read on the same zone(s) (without this cert-manager
#     fails with "/zones//dns_records/..." empty-zone-id errors)
cmd_dns_secret() {
    local target="${1:-}"
    [[ -z "$target" ]] && _die "usage: hermod.sh dns-secret <target> [--from-file PATH]
  Default reads the API token from a silent terminal prompt.
  HERMOD_DNS_API_TOKEN_FILE may override the default file path."
    shift

    local source_path="${HERMOD_DNS_API_TOKEN_FILE:-}"
    while [[ $# -gt 0 ]]; do
        case "$1" in
            --from-file) source_path="${2:?--from-file needs a PATH}"; shift 2 ;;
            *) _die "unknown dns-secret flag: $1" ;;
        esac
    done

    local resolved; resolved="$(resolve_target "$target")" || exit $?
    eval "$resolved"
    local KCTL; KCTL="$(_kctl_for "$kind")"

    local raw=""
    if [[ -n "$source_path" ]]; then
        [[ -r "$source_path" ]] || _die "cannot read $source_path"
        raw="$(cat "$source_path")"
        _log "reading token from $source_path"
    else
        _log "paste the Cloudflare API token (input hidden) and press Enter:"
        IFS= read -rs raw
        printf '\n'
        [[ -n "$raw" ]] || _die "empty input"
    fi

    # Trim whitespace; CF tokens are 40 ASCII chars (alphanumeric + _-).
    local token; token="$(printf '%s' "$raw" | tr -d ' \t\r\n')"
    raw=""
    [[ -n "$token" ]] || _die "empty token after trim"
    [[ ${#token} -ge 30 ]] || _warn "token is unusually short (${#token} chars) — proceeding"

    _log "applying Secret cloudflare-api-token to namespace=cert-manager"
    _kc apply -n cert-manager -f - <<EOF
apiVersion: v1
kind: Secret
metadata:
  name: cloudflare-api-token
type: Opaque
stringData:
  api-token: "$token"
EOF
    token=""
    _ok "Secret cloudflare-api-token written to cert-manager"
}

# ── cert (cert-manager wrappers) ───────────────────────────────────────────
# Operator wrappers for cert-manager: list certs, request a new one,
# show chain details. Assumes a ClusterIssuer named letsencrypt-prod
# is in place (created by applying overlays/prod-pi-letsencrypt with
# the dns-secret applied first).
cmd_cert() {
    local target="${1:-}"
    local sub="${2:-status}"
    [[ -z "$target" ]] && _die "usage: hermod.sh cert <target> <status|request|show> [args]
  status                 list all Certificate resources + their state
  request <hostname>     write a Certificate for <hostname> (uses letsencrypt-prod ClusterIssuer)
  show [name]            print chain details for the named cert (default: hermod-public-tls)"
    shift 2 2>/dev/null || true

    local resolved; resolved="$(resolve_target "$target")" || exit $?
    eval "$resolved"
    local KCTL; KCTL="$(_kctl_for "$kind")"

    case "$sub" in
        status)
            _log "certificates in cluster:"
            _kc get certificate -A 2>&1
            _log "cert-manager challenges (in flight):"
            _kc get challenge -A 2>&1
            ;;
        request)
            local host="${1:-}"
            [[ -z "$host" ]] && _die "usage: hermod.sh cert $target request <hostname>"
            _log "writing Certificate request for $host in namespace=$namespace"
            _kc apply -f - <<EOF
apiVersion: cert-manager.io/v1
kind: Certificate
metadata:
  name: ${host//./-}
  namespace: $namespace
spec:
  secretName: ${host//./-}
  issuerRef:
    name: letsencrypt-prod
    kind: ClusterIssuer
  commonName: $host
  dnsNames:
    - $host
  duration: 2160h
  renewBefore: 720h
  privateKey:
    algorithm: ECDSA
    size: 256
    rotationPolicy: Always
  usages:
    - server auth
    - digital signature
    - key encipherment
EOF
            _ok "Certificate ${host//./-} requested. Watch with: hermod.sh cert $target status"
            ;;
        show)
            local name="${1:-hermod-public-tls}"
            _log "Certificate $name in $namespace:"
            _kc -n "$namespace" describe certificate "$name" 2>&1 | tail -30
            _log "x509 chain (from Secret $name):"
            case "$kind" in
                pi-microk8s|pi-microk8s-prod|pi-microk8s-prod-edge)
                    _ssh "$KCTL -n $namespace get secret $name -o jsonpath='{.data.tls\\.crt}' 2>/dev/null | base64 -d | openssl x509 -noout -subject -issuer -dates -ext subjectAltName 2>&1" ;;
                pc-kind|pc-kind-prod)
                    $KCTL -n "$namespace" get secret "$name" -o jsonpath='{.data.tls\.crt}' 2>/dev/null | base64 -d | openssl x509 -noout -subject -issuer -dates -ext subjectAltName 2>&1 ;;
            esac
            ;;
        *)
            _die "unknown cert subcommand: $sub (status|request|show)"
            ;;
    esac
}
