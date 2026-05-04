# prod-pi-cloudflare-zero-trust — active CF tunnel + Cloudflare Access gate

**Applying this overlay exposes the Coordinator publicly through
Cloudflare BUT only to identities allowed by a Cloudflare Access
policy.** Hermod's own auth still applies on top — Access is a second
gate, not a replacement.

This overlay layers on top of the parked Cloudflare Tunnel overlay and:

* scales `cloudflared` from 0 to 2
* injects a fail-closed init container that refuses to start until
  `zero-trust-marker.yaml` has been edited away from its placeholder
  values — this is the structural enforcement of "Zero Trust mode must
  be explicitly set"
* flips Coord HSTS preload + IncludeSubDomains on

## Pre-flight

1. The parked overlay `prod-pi-letsencrypt-cloudflare-tunnel` has been
   applied and the `cloudflared-token` Secret is present.
2. A Cloudflare Access **Application** has been created in the
   dashboard for `hermod.<your-domain>`:
   * Zero Trust → Access → Applications → Add an application
   * Self-hosted, name `hermod`, domain `hermod.<your-domain>`
   * Identity providers: pick at least one (Google, GitHub, OTP, etc.)
   * Add at least one policy with the identities allowed
3. Find the Application's **AUD** tag and your team domain in the
   dashboard, then edit `zero-trust-marker.yaml`:
   ```yaml
   data:
     CF_ACCESS_AUD: "<your AUD tag>"
     CF_ACCESS_TEAM_DOMAIN: "<your-team>.cloudflareaccess.com"
   ```
4. The placeholder strings `REPLACE_WITH_CF_ACCESS_APP_AUD` and
   `REPLACE_WITH_TEAM_DOMAIN` must both be gone. The init container
   will exit 1 and the pod will CrashLoopBackOff if either remains.

## Deploy

```sh
kubectl apply -k kubernetes/overlays/prod-pi-cloudflare-zero-trust/
kubectl -n hermod-prod rollout status deploy/cloudflared
# Verify the init container passed:
kubectl -n hermod-prod logs deploy/cloudflared -c zero-trust-config-check
# expect: "Zero Trust config check passed: AUD=..., team=..."
```

Then visit `https://hermod.<your-domain>` in a browser — Cloudflare
Access will redirect you to the configured identity provider before
the request ever reaches the Coord.

## Belt-and-suspenders verification

Optional but recommended: enforce the AUD claim on the Hermod side
too, so a misconfigured tunnel cannot bypass Access. The `CF_ACCESS_AUD`
and `CF_ACCESS_TEAM_DOMAIN` env vars are already mounted on the
cloudflared container; you can reference them from a future Coord
middleware that validates the `Cf-Access-Jwt-Assertion` header.
Tracked in `SECURITY.md` as a future-work item.

## Rollback

```sh
kubectl delete -k kubernetes/overlays/prod-pi-cloudflare-zero-trust/
# cloudflared scales back to 0; tunnel closes; the parked parent
# overlay remains in place.
```
