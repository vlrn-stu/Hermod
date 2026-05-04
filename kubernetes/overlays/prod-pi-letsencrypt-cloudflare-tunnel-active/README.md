# prod-pi-letsencrypt-cloudflare-tunnel-active — active CF tunnel

**Applying this overlay opens the Cloudflare Tunnel and exposes the
Coordinator publicly on `hermod.<your-domain>` through Cloudflare's
edge.** No inbound port on the home network is opened — the Pi makes
outbound TLS to Cloudflare.

Inherits the parked `prod-pi-letsencrypt-cloudflare-tunnel` overlay
and:

* scales the cloudflared Deployment from 0 to 2 replicas
* flips Coord HSTS preload + IncludeSubDomains on

For a Zero Trust gate (Cloudflare Access) in front, use the
`prod-pi-cloudflare-zero-trust` overlay instead — it adds a
fail-closed init container so you cannot accidentally publish without
an Access policy.

## Pre-flight

1. The parked overlay `prod-pi-letsencrypt-cloudflare-tunnel` has been
   applied (`cloudflared` exists at replicas=0).
2. The `cloudflared-token` Secret has been created in `hermod-prod`
   from a tunnel token generated in the Cloudflare Zero Trust dashboard.
3. A public hostname is configured in the dashboard pointing the
   tunnel at `hermod-coordinator.hermod-prod.svc.cluster.local:42069`.

## Deploy

```sh
kubectl apply -k kubernetes/overlays/prod-pi-letsencrypt-cloudflare-tunnel-active/
kubectl -n hermod-prod rollout status deploy/cloudflared
kubectl -n hermod-prod logs deploy/cloudflared | grep -E "Registered tunnel|connected"
curl -I https://hermod.<your-domain>/healthz
# expect: HTTP/2 200, Cloudflare's publicly-trusted cert
```

## Trade-off (be explicit about this in the thesis)

Cloudflare terminates the public TLS at its edge. **Cloudflare sees
request plaintext** between browser and Pi. Acceptable for a homelab
or thesis demo; for end-to-end TLS to the Pi use
`prod-pi-letsencrypt-ingress` with cert-manager DNS-01 instead.

## Rollback

```sh
kubectl delete -k kubernetes/overlays/prod-pi-letsencrypt-cloudflare-tunnel-active/
# cloudflared scales back to 0 — tunnel closes, Pi no longer publicly
# reachable through Cloudflare. The Secret + Deployment manifest
# remain in place from the parked parent overlay.
```
