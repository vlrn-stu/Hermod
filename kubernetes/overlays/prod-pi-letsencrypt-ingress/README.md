# prod-pi-letsencrypt-ingress — active edge TLS

**Applying this overlay exposes the Coordinator publicly on
`hermod.<your-domain>` over HTTPS.**

Inherits the cert-only `prod-pi-letsencrypt` overlay and adds:

* an nginx Ingress that terminates the public TLS at the cluster edge
  and re-encrypts to the Coord pod over HTTPS using the internal CA
* Coord HSTS preload + IncludeSubDomains flipped on (now safe because
  the cert is publicly trusted)

## Pre-flight

1. The passive overlay `prod-pi-letsencrypt` has been applied and
   `kubectl describe certificate hermod-coordinator-public-tls`
   reports `Ready: True`.
2. The DNS A record for your hostname actually points at the Pi.
3. `nginx-ingress` is installed in the cluster (`microk8s enable
   ingress`).
4. You have edited `coord-ingress.yaml` to substitute
   `HOST_PLACEHOLDER` (twice) with your real FQDN.

## Deploy

```sh
kubectl apply -k kubernetes/overlays/prod-pi-letsencrypt-ingress/
kubectl -n hermod-prod get ingress hermod-coordinator
curl -I https://hermod.<your-domain>/healthz
# expect: HTTP/2 200, publicly-trusted cert chain
```

## Rollback

Drop just this overlay; the cert and ClusterIssuer stay in place
(provided by the parent passive overlay):

```sh
kubectl delete -k kubernetes/overlays/prod-pi-letsencrypt-ingress/
# the Coord is no longer publicly reachable; cert remains issued
```
