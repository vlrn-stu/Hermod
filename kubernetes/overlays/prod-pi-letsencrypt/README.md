# prod-pi-letsencrypt — passive / cert-only edge TLS (default)

Pi5 deploy with Let's Encrypt at the cluster edge **acquired only**.
This overlay is the safe default. It installs a cert-manager
ClusterIssuer and a Certificate request, then stops. **No Ingress is
created** — the Coordinator is not exposed to the public internet by
this overlay alone.

The point: validate the whole acquisition pipeline (cert-manager +
ClusterIssuer + ACME challenge reachability) without flipping the
public-traffic switch. Once you can see a valid Secret on the cluster
with a real LE chain in it, you know the active sibling overlay will
work too.

## Sibling overlays in the family

| Overlay | What it does |
|---|---|
| `prod-pi-letsencrypt`                          | this one — cert acquired, no traffic |
| `prod-pi-letsencrypt-ingress`                  | adds nginx Ingress, serves traffic |
| `prod-pi-letsencrypt-cloudflare-tunnel`        | parks a cloudflared Deployment (replicas=0) |
| `prod-pi-letsencrypt-cloudflare-tunnel-active` | scales cloudflared to 2, opens the tunnel |
| `prod-pi-cloudflare-zero-trust`                | tunnel + Cloudflare Access gate, fail-closed init check |

## Prerequisites

1. **cert-manager** installed in the cluster:
   ```sh
   kubectl apply -f https://github.com/cert-manager/cert-manager/releases/latest/download/cert-manager.yaml
   ```
2. **Reachability for the chosen ACME challenge** (HTTP-01 needs port
   80 reachable from the public internet; DNS-01 needs DNS provider
   API credentials — see "Reachability options" below).
3. **A public DNS A record** pointing your hostname at the Pi.

## Configuration

Two placeholders to substitute before applying:

* `cluster-issuer.yaml` — replace `EMAIL_PLACEHOLDER` with a real email
  (Let's Encrypt sends renewal-failure notifications there).
* `coord-certificate.yaml` — replace `HOST_PLACEHOLDER` (twice) with
  your FQDN.

Then:

```sh
# Use the staging issuer first to avoid burning prod LE quota.
sed -i 's/letsencrypt-prod/letsencrypt-staging/' coord-certificate.yaml
kubectl apply -k kubernetes/overlays/prod-pi-letsencrypt/

# Verify cert issuance:
kubectl -n hermod-prod describe certificate hermod-coordinator-public-tls
kubectl -n hermod-prod get secret hermod-coordinator-public-tls

# Once the staging cert is issued cleanly, flip back to prod:
sed -i 's/letsencrypt-staging/letsencrypt-prod/' coord-certificate.yaml
kubectl apply -k kubernetes/overlays/prod-pi-letsencrypt/
```

## Going live

When you are satisfied that the certificate is being issued and renewed
cleanly, apply the active sibling:

```sh
kubectl apply -k kubernetes/overlays/prod-pi-letsencrypt-ingress/
```

That overlay layers an Ingress on top of this one, references the
Secret created here, and flips on HSTS preload.

## Reachability options for the ACME challenge

Let's Encrypt has to prove you control the domain. Three ways:

### 1. HTTP-01 (default in `cluster-issuer.yaml`)

ACME server hits `http://<your-host>/.well-known/acme-challenge/...`.

* **Requires:** TCP port 80 reachable from the public internet.
* **Pros:** Simple, no external API tokens.
* **Cons:** Won't work behind CGNAT or firewalled networks. No wildcards.

### 2. DNS-01 with cert-manager

cert-manager creates a TXT record via your DNS provider's API.

* **Requires:** API token from your DNS provider (Cloudflare, Route53,
  DigitalOcean, etc.) stored as a Kubernetes Secret.
* **Pros:** No inbound port needed. Works behind CGNAT. Supports wildcards.
* **Cons:** DNS-provider-specific Secret management.
* **How:** Uncomment the DNS-01 solver block in `cluster-issuer.yaml`
  and create the Secret:
  ```sh
  kubectl create secret generic cloudflare-api-token \
      --from-literal=api-token='<token>' \
      -n cert-manager
  ```

### 3. Cloudflare Tunnel — different overlay family

If you cannot open any inbound port, do not use this overlay at all.
Use `prod-pi-letsencrypt-cloudflare-tunnel` (parked default) and then
`prod-pi-letsencrypt-cloudflare-tunnel-active` to open it. CF terminates
public TLS at its edge; no LE challenge dance, no port 80, no
cert-manager needed.

## Why is in-cluster mTLS still self-signed?

The internal CA chain (Coord ↔ NanoMQ ↔ Postgres ↔ Vault42) is the
"private trust" PKI. The Let's Encrypt cert is the "public trust" PKI.
Different threat models — see `SECURITY.md` §3.5.
