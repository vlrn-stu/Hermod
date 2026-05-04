# prod-pi-letsencrypt-cloudflare-tunnel — passive / parked-tunnel (default)

Pi5 deploy that **installs the Cloudflare Tunnel pieces but leaves the
tunnel closed.** The `cloudflared` Deployment ships with `replicas: 0`
so no traffic flows. The point: validate that the Secret is in place
and the manifest renders cleanly before flipping the switch on the
sibling overlay `prod-pi-letsencrypt-cloudflare-tunnel-active`.

For Cloudflare Access (Zero Trust) gating in front of the tunnel,
apply the sibling `prod-pi-cloudflare-zero-trust` overlay instead —
it adds a fail-closed init container so you cannot publish without an
Access policy in place.

Cloudflare terminates the public TLS with its own publicly-trusted
certificate and forwards traffic to the in-cluster Coord Service over
the tunnel. No `cert-manager`, no Let's Encrypt rate limits, no ACME
challenge dance.

## When to use this overlay vs. `prod-pi-letsencrypt`

| Situation | Use |
|---|---|
| Behind CGNAT or can't open port 80 | this overlay |
| Want CF Access (SSO/2FA) in front of the UI | this overlay |
| Already on Cloudflare for DNS, want zero-config | this overlay |
| Want end-to-end TLS with no third-party in the path | `prod-pi-letsencrypt` (DNS-01) |
| Need to defend "zero external dependencies" in the thesis | `prod-pi-letsencrypt` |

## Trade-off (be explicit about this in the thesis)

Cloudflare terminates the public TLS at its edge. This means
**Cloudflare sees request plaintext** between the browser and the Pi.
For a homelab or a thesis demo this is fine; for stricter end-to-end
requirements use the `prod-pi-letsencrypt` overlay with DNS-01 instead,
so the cert lives on the Pi and only the Pi has the private key.

In-cluster mTLS to NanoMQ/Postgres/Vault42 is unchanged — the tunnel
only fronts the Coord HTTP API/UI.

## Prerequisites

1. **A Cloudflare account** with Zero Trust enabled (free tier covers
   personal use).
2. **A Cloudflare-managed DNS zone** for the domain you want to expose.
3. **A tunnel created via the dashboard:**
   * Zero Trust → Networks → Tunnels → Create a tunnel
   * Pick "Cloudflared" as the connector type
   * Name it (e.g. `hermod-pi`)
   * Copy the token from the install command (the long string after
     `--token`)
4. **A public hostname configured on the tunnel:**
   * Public Hostname tab → Add a public hostname
   * Subdomain: `hermod` (or your choice)
   * Domain: pick from your CF DNS zones
   * Service: `HTTPS` → `hermod-coordinator.hermod-prod.svc.cluster.local:42069`
   * Additional Application Settings → TLS:
     * Origin Server Name: `hermod-coordinator.hermod-prod.svc.cluster.local`
     * No TLS Verify: ON (origin uses internal CA)
5. **Store the tunnel token as a Kubernetes Secret:**
   ```sh
   kubectl create secret generic cloudflared-token \
       --from-literal=TUNNEL_TOKEN='<paste-token-here>' \
       -n hermod-prod
   ```

## Deploy

```sh
kubectl apply -k kubernetes/overlays/prod-pi-letsencrypt-cloudflare-tunnel/
kubectl -n hermod-prod rollout status deploy/cloudflared
kubectl -n hermod-prod logs deploy/cloudflared | grep "Registered tunnel"
```

You should see `Registered tunnel connection` lines. Then:

```sh
curl -I https://hermod.your-domain.example/healthz
# expect: HTTP/2 200, with a publicly-trusted cert chain
```

## Optional hardening

* **Cloudflare Access**: Zero Trust → Access → Applications → Add an
  application for `hermod.your-domain.example`. Pick an identity
  provider (Google, GitHub, OTP via email) and a policy. CF will gate
  access at the edge before any request reaches your tunnel — Hermod's
  own auth still applies on top.
* **WAF rules**: turn on the managed ruleset for the hostname.
* **Tunnel network policy**: scope the cloudflared Deployment with a
  NetworkPolicy that only allows egress to `*.cloudflare.com` and
  in-cluster Coord — already enforced by the cluster's default-deny
  ingress; add explicit egress allow if your cluster restricts that.

## Rollback

```sh
kubectl delete -k kubernetes/overlays/prod-pi-letsencrypt-cloudflare-tunnel/
# Then reapply the plain prod-pi overlay:
kubectl apply -k kubernetes/overlays/prod-pi/
```

The tunnel hostname stops resolving once `cloudflared` exits; the
Coord stays reachable on its NodePort `:42069` (with the internal-CA
warning).
