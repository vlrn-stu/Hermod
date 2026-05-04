# Hermod Security Posture

This document describes the security model of Hermod as deployed by the
`prod` and `prod-pi` kustomize overlays. It is the canonical reference for
the thesis security section and for any future operational hardening work.

Citations point at the source-of-truth code so that drift is detectable
by re-grep rather than re-derivation.

---

## 1. Authentication

### 1.1 Identity provider

All services authenticate against Vault42, an internal RS256 JWT issuer.
Tokens are validated by `Vault42.AspNetCore.AddVault()` against a JWKS
fetched at cold-start.

- Coordinator wiring: `src/Hermod.Coordinator/Program.cs:66-77`
- LoRa2MQTT wiring: `src/LoRa2MQTT/LoRa2MQTT.Service/Program.cs:69-78`
- JWKS fetch is eager, not lazy, so an unreachable Vault42 fails the pod
  cold-start instead of silently degrading to "no auth" later.

### 1.2 Token transport

- Browser sessions use `HttpOnly; Secure; SameSite=Strict` cookies set
  by `AuthProxyController.SetTokenCookie()`
  (`src/Hermod.Coordinator/Controllers/AuthProxyController.cs:405`).
- The bearer JWT is **never** returned in a JSON response body. The
  `/auth/login` and `/auth/refresh` handlers strip `access_token` from
  the proxied response after writing the cookie
  (`AuthProxyController.cs:109-119, 222-229`).
- API/CLI callers send `Authorization: Bearer <jwt>` directly; no cookie
  is involved.

### 1.3 AuthBypass test toggle

The configuration key `Hermod:Security:AuthBypass=true` swaps the JWT
authentication scheme for `AuthBypassHandler`, which assigns
`admin + operator + viewer` roles to every request. It exists so test
profiles can run without deploying a Vault42 instance.

It is **gated by runtime environment**: if the toggle is true while
`ASPNETCORE_ENVIRONMENT=Production`, both the Coordinator and LoRa2MQTT
hard-fail at startup with `InvalidOperationException`. This is defense
in depth — environment variable pollution, copy-paste from a test
overlay, or a misconfigured CI job can no longer silently disable
authentication on a Production image.

- Coordinator guard: `src/Hermod.Coordinator/Program.cs:47-58`
- LoRa2MQTT guard: `src/LoRa2MQTT/LoRa2MQTT.Service/Program.cs:52-61`
- Runtime environment in cluster: `kubernetes/base/hermod/deployment.yaml:59-60`
  (`ASPNETCORE_ENVIRONMENT=Production` for both `prod` and `prod-pi`).
- Production deployments set the toggle in zero overlays/configs/env files
  (verified by `grep -rn "AuthBypass\|Hermod__Security" kubernetes/`),
  so the guard is purely a safety net.

---

## 2. Authorization

### 2.1 Deny-by-default

The Coordinator registers a `FallbackPolicy` that requires an
authenticated user for every endpoint that does not opt out via
`[AllowAnonymous]`:

```
options.FallbackPolicy = new AuthorizationPolicyBuilder()
    .RequireAuthenticatedUser()
    .Build();
```

(`src/Hermod.Coordinator/Program.cs:97-105`)

A new controller added without explicit `[Authorize]` is therefore
authenticated, not anonymous.

### 2.2 RBAC policies

Three roles, strictly nested: `viewer ⊂ operator ⊂ admin`. Policies
are registered via `Hermod.Coordinator.Authorization.Policies.Register`
and applied with `[Authorize(Policy = "admin")]` / `"operator"` /
`"viewer"` attributes. Backup, audit, and rule mutation routes are
admin-only; metric reads are viewer-or-above.

### 2.3 Anonymous routes (explicit allowlist)

Anonymous access is granted route-by-route. There is no glob, no
config-driven allowlist, and no path-pattern parsing. The middleware
stack uses `PathString.StartsWithSegments()` (which normalizes the
request path before matching) so trailing-slash, double-slash, and
`%2e` encodings cannot bypass the gate:

| Prefix | Purpose | Site |
|---|---|---|
| `/healthz`, `/readyz` | Kubernetes probes | `[AllowAnonymous]` on controller |
| `/auth/login`, `/auth/logout`, `/auth/refresh` | Auth proxy | `AuthProxyController` |
| `/mock/*` (dev only) | Test seam — middleware short-circuits to 404 unless `Dev:Endpoints=true` | `Program.cs:254-263` |

Mutating `/api/*` requests with cookie auth are additionally gated by a
CSRF check (Origin/Referer header verification) at `Program.cs:268-326`.

### 2.4 YARP reverse-proxy allowlist

The Coordinator hosts a YARP reverse proxy for the protocol translator
dashboards. It is **not** a generic in-cluster forwarder — only paths
that match an explicitly-registered route reach upstream services. The
registration logic lives in
`src/Hermod.Coordinator/Configuration/TranslatorProxyRegistration.cs`
and is constrained at three layers:

1. **Slug allowlist**: `TranslatorProxyRegistration.AllowedSlugs` is a
   hard-coded `IReadOnlySet<string>` containing exactly four values:
   `zigbee`, `lora`, `bluetooth`, `ble`. The `Add()` and `AddAdmin()`
   helpers throw `InvalidOperationException` at startup if asked to
   register a slug not in the set.
2. **Per-slug settings gate**: each slug only registers a route when
   the corresponding `TranslatorSettings.Enabled` flag is `true` AND
   `TranslatorSettings.Url` is non-empty. Disabled translators have
   no live YARP route.
3. **Per-slug authorization**: `/proxy/{slug}/**` carries no YARP
   policy (gated upstream by the Blazor [Authorize] attribute on the
   embedding page); `/admin/{slug}/**` carries the explicit
   `Authorization.Policies.Admin` policy via `RouteConfig.AuthorizationPolicy`.

Adding a new exposed translator requires three coordinated edits:

* Extend `AllowedSlugs` with the new slug
* Add a `TranslatorSettings` property to `ProtocolTranslatorsSettings`
  for it
* Add `Add(routes, clusters, "<slug>", translators.<NewProperty>)`
  (and an `AddAdmin` if the upstream has an admin UI) inside
  `TranslatorProxyRegistration.Build()`

This is a deliberate friction point. A misconfiguration that tried to
expose, say, `postgres` or `vault42` through `/proxy/postgres/` would
fail at startup because the slug is not in the allowlist — there is
no path-pattern under which an arbitrary in-cluster service can be
reached through YARP.

### 2.5 Known limitation: `/api/system/features` dual-gate

`SystemController.cs:42-54` carries `[AllowAnonymous]` but performs a
runtime `User.Identity.IsAuthenticated` check. The behaviour is correct
in both dev (`Dev:Endpoints=true`) and prod (`Dev:Endpoints=false`)
modes, but the attribute is misleading. Future contributors may copy
the pattern and forget the runtime check. Recommended cleanup:
remove `[AllowAnonymous]` and split into two endpoints with
unambiguous gating.

---

## 3. Transport (TLS / HTTPS)

### 3.1 In-cluster PKI

The `prod` and `prod-pi` overlays use a self-signed certificate
authority generated by `lib/issue-internal-certs.sh`:

- Root CA: 4096-bit RSA, 10-year validity, CN `hermod-internal-ca`.
- Twelve leaf certs (5 server + 7 client): 2048-bit RSA, 1-year
  validity, signed by the root.
- Cert chain verified with `openssl verify -CAfile ca.crt` before
  Secrets are upserted (`lib/issue-internal-certs.sh:100`).
- Certs land in `~/.hermod-prod-certs/`; `lib/seed-internal-certs.sh`
  upserts them as Kubernetes Secrets in the `hermod-prod` namespace
  outside `kustomize apply` so `delete -k` cannot wipe them.

### 3.2 Coordinator HTTPS

- TLS 1.3 only: `Program.cs:155` sets `SslProtocols.Tls13`.
- Cert hot-reload without pod restart: `ServerCertificateSelector`
  watches the cert file's mtime and re-reads on change
  (`Program.cs:149-172`). Existing TLS sessions hold the old cert
  until they reconnect; new handshakes use the new cert.
- HSTS: `max-age=31536000` (1 year), `IncludeSubDomains=false`,
  `Preload=false` (`Program.cs:117-121`). Preload and IncludeSubDomains
  are intentionally off for the self-signed deploy because Chrome
  refuses to render preloaded HSTS sites with untrusted certs. The
  publicly-trusted overlay (see §3.5) flips both to `true`.
- `Server` header suppressed (`Program.cs:133`) to reduce
  version fingerprinting.

### 3.3 mTLS to internal services

- Coordinator → NanoMQ: client cert + CA bundle, `mqtts://nanomq:8883`
  (`coord-prod.yaml:88-106`).
- Coordinator → Postgres: `SSL Mode=VerifyFull; Trust Server Certificate=false`
  with CA bundle pin (`coord-prod.yaml:107`).
- Coordinator → Vault42: HTTPS only (enforced by
  `Hermod:Security:VaultRequireHttps=true` in
  `coord-prod.yaml:111`), but **no client cert and no CA pin**. Vault42
  is ClusterIP-only, so a hostile pod inside the namespace would still
  need to defeat the JWKS signature check to forge tokens; nonetheless
  this is a known asymmetry vs. NanoMQ/Postgres and is tracked.

### 3.4 The browser-warning problem

The internal CA is trusted by Hermod pods (mounted as
`internal-ca.crt`) and by curl/openssl invocations that pass
`--cacert`, but it is not in any browser trust store. A user reaching
`https://<pi-lan-ip>:42069/` directly in Chrome or Firefox sees the
familiar "Your connection is not private" interstitial. This is
correct behaviour for a private CA — it is not a Hermod bug — but it
makes the system feel unsuitable for non-operator end users.

### 3.5 Publicly-trusted certificate path

Five sibling overlays document and gate the path from "self-signed
lab" to "trusted public deploy." All default to a passive /
non-exposing state — going live requires explicitly applying an
`*-active` or `*-ingress` variant by name. This split is a deliberate
safety property: a misapplied kustomize cannot accidentally publish
the Coord to the public internet.

| Overlay | State | What it adds |
|---|---|---|
| `prod-pi-letsencrypt` | passive | cert-manager ClusterIssuer + Certificate (cert acquired, no Ingress) |
| `prod-pi-letsencrypt-ingress` | active | nginx Ingress that references the Secret above; HSTS preload on |
| `prod-pi-letsencrypt-cloudflare-tunnel` | passive | `cloudflared` Deployment parked at `replicas: 0` |
| `prod-pi-letsencrypt-cloudflare-tunnel-active` | active | scales `cloudflared` to 2; HSTS preload on |
| `prod-pi-cloudflare-zero-trust` | active + ZT | tunnel + Cloudflare Access; fail-closed init container refuses to start until AUD/team domain are configured |

Reachability options for the LE-via-cert-manager family:

1. **HTTP-01** (default in `cluster-issuer.yaml`): port 80 reachable
   from the public internet. Simplest if your network supports it.
2. **DNS-01**: API token from your DNS provider. No inbound port
   needed; works behind CGNAT; supports wildcards.

Reachability for the Cloudflare Tunnel family is intrinsic — the Pi
makes outbound TLS to Cloudflare, no inbound ports are opened. The
trade-off is that Cloudflare terminates the public TLS at its edge
and therefore sees request plaintext.

Mullvad's port-forwarding feature was discontinued in 2023 and is not
a viable HTTP-01 path; it is mentioned only to close out the option.

---

## 4. Threat model and known gaps

| # | Concern | Status | Mitigation |
|---|---|---|---|
| 1 | JWT strings in GC heap, no zero-on-drop | Accepted | Process isolation would help; out of scope for thesis. Documented limitation. |
| 2 | No process isolation between auth and rule engine | Accepted | Single-binary trade-off. An RCE in any handler exposes all live tokens. Documented. |
| 3 | Coord → Vault42 not cert-pinned | Tracked | ClusterIP-only narrows the attack surface; pin is straightforward to add. |
| 4 | Self-signed CA → browser warning | Designed-around | `prod-pi-letsencrypt` overlay path. |
| 5 | AuthBypass leaking into prod | Eliminated | Production-runtime guard throws at startup (§1.3). |
| 6 | JWKS rotation cadence not documented | Tracked | Inherits Vault42.AspNetCore defaults; will be specified once Vault42 publishes a rotation policy. |
| 7 | Clock-skew tolerance implicit | Accepted | Inherits library defaults; cluster nodes use systemd-timesyncd, so realistic skew is sub-second. |

---

## 5. Security toggles cross-reference

| Key | Default | Purpose | Production behaviour |
|---|---|---|---|
| `Hermod:Security:AuthBypass` | `false` | Test seam: skip JWT validation | **Hard-fail at startup if true under Production** (§1.3) |
| `Hermod:Security:VaultRequireHttps` | `true` | Require HTTPS for JWKS metadata fetch | Set to `true` in `coord-prod.yaml:111` and `lora2mqtt-prod.yaml:77` |
| `Hermod:Hsts:Preload` | `false` | HSTS preload directive | Off for self-signed; flip to `true` in publicly-trusted overlay |
| `Dev:Endpoints` | `false` | Enables `/mock/*` and dev test endpoints | False in prod overlays |

---

## 6. Operational checks before declaring a deploy "secure"

Run these before treating any cluster as production:

```sh
# 1. AuthBypass must not be set anywhere in the rendered manifest.
kustomize build kubernetes/overlays/prod-pi | grep -i "AuthBypass"
# (expected: no output)

# 2. ASPNETCORE_ENVIRONMENT must be Production.
kubectl -n hermod-prod get deploy hermod-coordinator -o yaml | \
    grep -A1 ASPNETCORE_ENVIRONMENT
# (expected: value: "Production")

# 3. Vault HTTPS metadata enforcement must be on.
kubectl -n hermod-prod get deploy hermod-coordinator -o yaml | \
    grep -A1 VaultRequireHttps
# (expected: value: "true")

# 4. Coordinator must reject HTTP-only requests.
curl -sk http://<pi-lan-ip>:42069/healthz   # expect: connection refused or 308 to https
curl -sk https://<pi-lan-ip>:42069/healthz  # expect: 200 Healthy

# 5. Coordinator must reject anonymous mutating requests.
curl -sk -X POST https://<pi-lan-ip>:42069/api/rules
# (expect: 401)
```
