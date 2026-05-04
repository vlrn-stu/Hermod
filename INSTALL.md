# Hermod Installation Guide

This guide walks through every step of installing Hermod, in the order
the operator runs them. Each numbered step matches a single tool
invocation so the same sequence renders cleanly as a figure-by-figure
walkthrough in the technical-documentation appendix.

There are two installation paths. Pick one:

| Path | Where it runs | Use when |
|---|---|---|
| **Path 1 — Local preview** (`hermod.bat` / `hermod.sh compose`) | Windows / Linux / macOS laptop | You want to evaluate Hermod in 5 minutes without dedicated hardware. Gives you a working dashboard at `http://localhost:42069`. |
| **Path 2 — Pi production** (`hermod.sh`) | Operator host + Raspberry Pi 5 8 GB | You're deploying for real. Pi runs MicroK8s; every internal connection is mTLS; optional Let's Encrypt or Cloudflare-Tunnel TLS at the edge. |

Detailed operator reference for every subcommand, target, and overlay
lives in [`docs/HERMOD_SH.md`](docs/HERMOD_SH.md). Threat model and
trust-boundary discussion is in [`SECURITY.md`](SECURITY.md).

---

## Path 1 — Local preview

A single-host Docker Compose stack with mock translators. Coordinator
+ dashboard + Postgres + NanoMQ broker + Vault42 + LoRa2MQTT (mock
mode) + ble2mqtt + Mosquitto Wi-Fi bridge. Dev-grade defaults
throughout; never expose these ports beyond loopback or a trusted LAN.

### Prerequisites

- Docker Desktop ≥ 4.30 (Windows / macOS) or Docker Engine ≥ 24.0 (Linux)
- 4 GB RAM free, 2 CPU cores
- Ports `1883`, `1884`, `8080`, `8081`, `8083`, `42069` free on the host

### Step 1 — Clone and start

```bash
git clone https://github.com/<owner>/hermod.git
cd hermod

# Linux / macOS:
./hermod.sh compose up
# Windows:
hermod.bat compose up
```

First run builds the Coordinator + LoRa2MQTT images locally (~2–3 min).
Subsequent runs are fast.

### Step 2 — Open the dashboard

Browse to `http://localhost:42069`. Default seed login:

- email: `user@l.l`
- password: printed at the end of the `up` output (or recover with
  `compose logs coordinator | grep seed`).

### Step 3 — Day-2

```bash
./hermod.sh compose status     # pod table
./hermod.sh compose logs       # tail every service
./hermod.sh compose down       # stop, keep volumes
./hermod.sh compose reset      # stop AND wipe volumes
```

---

## Path 2 — Pi production install

Installs Hermod on a Raspberry Pi 5 8 GB running Ubuntu Server 24.04
LTS arm64 with MicroK8s. Coordinator behind HTTPS on the LAN, NanoMQ
internal-only on mTLS, Vault42 issuing RS256 JWTs, NetworkPolicies
deny-by-default, and an `internal-ca` trust chain managed for you.

### Prerequisites

**Operator host** (Linux, macOS, or WSL2 on Windows):

- `bash`, `ssh`, `scp`, `rsync`, `kubectl`, `ansible-playbook`
- A clone of this repo
- The control host can reach the Pi over the LAN (or you have a USB-Ethernet
  link as in the matrix-test rig)

**Raspberry Pi 5 8 GB** target (any of three starting points works):

- *(greenfield)* a blank SD card — `hermod.sh provision` flashes Ubuntu
  Server 24.04 arm64 with the cloud-init you specify
- *(self-flashed)* you've already flashed Ubuntu Server 24.04 arm64
  yourself and the Pi is reachable over SSH
- *(WSL2)* not supported as a target — only as the operator host

**Operator vault**:

- A populated `hermod-prod.env` at the repo root (template:
  `hermod-prod.env.example`). Holds DB password, MQTT credentials,
  optional Cloudflare API token, optional ACME email.

### Step 1 — Clone the repo and prepare the env file

```bash
git clone https://github.com/<owner>/hermod.git
cd hermod
cp hermod-prod.env.example hermod-prod.env
$EDITOR hermod-prod.env       # fill in passwords + WiFi creds + cluster overrides
chmod 600 hermod-prod.env
```

The env file is local to the operator host; it never leaves the
control machine in plaintext (Mimir's `mimir.sh` encrypted vault is
the recommended store — see `lib/mimir.sh`).

### Step 2 — Provision the Pi

**Greenfield (blank SD)**:

```bash
./hermod.sh provision configs/your-pi.yaml
```

This runs the `hermod-pi` tool. It downloads the official Ubuntu
Server 24.04 arm64 preinstalled image, injects cloud-init from
your config (hostname, SSH key, network), prompts for the SD card,
writes + verifies it, then waits for the Pi to come up and answer SSH.

**Self-flashed Pi already booted**:

```bash
./hermod.sh wait-pi <hostname>
```

mDNS-discovers the Pi, TOFU-pins its host key, and confirms SSH works.

### Step 3 — Run the installer

```bash
./hermod.sh install prod-pi
```

The installer is a 10-step bring-up, each step printed with a
`[hermod] step N/10 — <description>` banner so you can follow along.
Approximate wall time on the first run: 10–15 min (most of it in the
arm64 image build during step 5; subsequent installs with `--no-build`
finish in ~2 min).

The 10 steps are:

1. **bluez install** — operator-host package the BLE2MQTT scripts call.
2. **MicroK8s addons** — `dns`, `storage`, `ingress`, `metrics-server`.
3. **internal CA + cert seed** — generates the cluster's root CA,
   issues server + client leaves, drops them in the `internal-ca` and
   `coord-server-tls` Secrets.
4. **rsync source tree** — repo + `hermod-prod.env` to `/opt/hermod`.
5. **build arm64 images** — Coordinator + LoRa2MQTT built **on the Pi**
   using whichever container builder is installed (podman is preferred
   because the ansible base role installs it; docker is accepted as a
   fallback). Both images are saved as tarballs and side-loaded into
   MicroK8s' containerd via `microk8s ctr image import`.
6. **ensure-secrets** — synchronises `hermod-prod.env` values to the
   `hermod-secrets` and `hermod-db-credentials` Secrets in `hermod-prod`.
7. **kustomize apply prod-pi** — applies the `prod-pi` overlay layered
   on the hardened `prod` base.
8. **rollout-wait** — blocks until every Deployment is `Available`.
9. **smoke check** — TLS handshake without client cert must fail;
   unauthenticated `/api/devices` must return 401; deny-by-default
   fallback must be in force; viewer credentials must be refused on an
   admin-only write.
10. **summary** — prints pod table, certificate fingerprints, and the
    LAN URL to open.

### Step 4 — Verify

```bash
./hermod.sh status prod-pi
```

Every pod should be `1/1 Running`. If `vault42` shows `0/1 Pending`
for more than ~30 seconds on the first install, see Troubleshooting
below — usually a cert-Secret-not-yet-mounted ordering hiccup that
clears with one `./hermod.sh kick prod-pi`.

Then browse to `https://<pi-ip>:42069`. The browser will warn about a
self-signed certificate authority on first connect — that's expected;
the cluster's internal CA isn't in your trust store. To get a
publicly-trusted certificate, see Step 5.

### Step 5 (optional) — Edge TLS

For a publicly-trusted certificate on the Pi via Let's Encrypt + DNS-01
(no inbound port required, works behind CGNAT or a hostile NAT):

```bash
./hermod.sh dns-secret prod-pi --from-file /path/to/cf-token
./hermod.sh install prod-pi-letsencrypt
./hermod.sh cert prod-pi request hermod.your-domain.example
./hermod.sh cert prod-pi status            # watch it land
```

For a Cloudflare-tunnelled deployment with no public-IP requirement at all:

```bash
./hermod.sh tunnel-secret prod-pi --from-file /path/to/tunnel-token
./hermod.sh install prod-pi-letsencrypt-cloudflare-tunnel-active
```

The full edge-TLS overlay family (LE-only, LE+ingress, tunnel-passive,
tunnel-active, zero-trust) is documented in
`docs/HERMOD_SH.md` → *Certificate management*.

### Day-2 operations

```bash
./hermod.sh status prod-pi              # pod table
./hermod.sh logs prod-pi                # tail every pod
./hermod.sh logs prod-pi <pod>          # tail one pod
./hermod.sh update prod-pi              # rsync code + rebuild images + apply + rollout
./hermod.sh kick prod-pi                # rollout-restart all pods (no rebuild)
./hermod.sh secrets prod-pi             # show seeded credentials + cert fingerprints
./hermod.sh rotate-certs prod-pi        # roll mTLS leaves (CA preserved)
./hermod.sh limiter prod-pi show        # show ingress-limiter live env
./hermod.sh metrics prod-pi             # fetch coord /metrics
```

For removal:

```bash
./hermod.sh teardown prod-pi            # delete the overlay (cert Secrets preserved)
./hermod.sh reset prod-pi               # DESTRUCTIVE clean-slate (HERMOD_RESET_CONFIRM=YES required)
```

---

## Troubleshooting

Issues a first-time installer can realistically hit. Stale-state /
half-finished install issues that only happen on the second-or-later
run are not in this list.

### 5 — `update` fails with `docker: command not found`

```
[hermod] step 3b/5 — rebuild image(s) on Pi: coord
[hermod] rebuild (coord): docker build hermod-coordinator:latest
bash: line 1: docker: command not found
```

The Pi installed via the ansible role has **podman**, not docker.
The `update` path now feature-detects both (preferring whichever the
Pi has) and falls through to a clear error message if neither is
installed. If you see the error above on a build dated before this
fix, run `sudo apt install -y podman` on the Pi and retry.

### 5 — `apt` fails on first boot with "Release file not yet valid"

The Pi's hardware clock is typically 60–70 days behind real time
until `systemd-timesyncd` synchronises. The cloud-init runs `apt
update` before that completes, and apt rejects repository Release
files with a future-dated `Valid-Until`.

```
sudo timedatectl set-ntp false
sudo timedatectl set-ntp true
# wait ~10 s for the sync
sudo apt update
```

The ansible base role does this automatically on subsequent runs, but
the very first cloud-init pass occasionally hits the window.

### 7 — Vault42 stuck `0/1 Pending` on first install

```
$ kubectl -n hermod-prod get pod
NAME                              READY   STATUS    RESTARTS   AGE
hermod-coordinator-...            0/1     Init:0/2  0          12s
vault42-...                       0/1     Pending   0          15s
```

The vault42 pod is waiting on the `vault42-server-tls` Secret which
the cert-issuance step is still propagating. Almost always clears in
one `./hermod.sh kick prod-pi` after step 7 prints `[hermod OK]`.
If it persists past 60 seconds, `kubectl describe pod vault42-…`
will show whether it's actually a Secret-mount issue or a node-selector
mismatch.

### 7 — Coordinator crashloop on first install: `JWKS not yet reachable`

Same root as above: Vault42 must be `1/1 Ready` before the Coordinator's
startup probe stops failing. The Coordinator's `startupProbe` absorbs
the long database-migration phase but it does NOT absorb a missing
JWKS endpoint. One `./hermod.sh kick prod-pi` after vault42 is up
clears it.

### 9 — Smoke check reports "TLS handshake without client cert succeeded"

This is a real failure: it means NanoMQ accepted an anonymous
connection because either `allow_anonymous = true` slipped in, or the
production overlay's strategic-merge patch removing the plaintext
`1883` listener didn't apply. Check:

```
./hermod.sh logs prod-pi nanomq | grep -i 'allow_anonymous\|listener'
```

Should show `allow_anonymous = false` and only the `tls://*:8883` listener.

### Edge-TLS — Let's Encrypt DNS-01 stuck "Waiting for propagation"

Most often the upstream resolver has a stale NXDOMAIN cached for the
ACME challenge record. Either wait (5–15 min for typical resolver TTLs)
or, if you control the resolver, flush. The `cert-manager` logs in
`./hermod.sh logs prod-pi cert-manager` show what propagation it's
checking against.

### Edge-TLS — cert-manager `Certificate` status `False, IssuerNotFound`

The `prod-pi-letsencrypt` overlay installs the `ClusterIssuer` and the
`Certificate` resource together, and on a slow Pi the `Certificate`
reconciler can fire before the `ClusterIssuer` is `Ready`. Refire
the reconciler:

```
kubectl -n hermod-prod annotate certificate hermod cert-manager.io/issue-temporary-certificate=true --overwrite
```

### `./hermod.sh doctor` says deps are missing

Install whatever it lists. On Debian/Ubuntu hosts:

```
sudo apt install -y rsync ansible openssh-client
sudo snap install kubectl --classic
```

### Box-drawing characters look broken in the CLI / TUI

Terminal locale isn't UTF-8. Try `LANG=C.UTF-8 ./hermod.sh` or set
your terminal emulator's locale to a UTF-8 variant.

---

## Repository layout (operator-facing)

```
hermod.sh                  Linux/macOS CLI entry point (also launches the TUI)
hermod.bat                 Windows CLI entry point (compose only)
docker-compose.yaml        compose stack referenced by both wrappers
hermod-prod.env.example    operator vault template
lib/                       hermod.sh-owned helpers (TUI, mimir vault, certs, pi-installer, compose)
ansible/                   Pi provisioning playbooks (microk8s, base, hermod_deploy, ...)
kubernetes/                base manifests + prod overlays + edge-TLS overlay family
src/                       .NET source for Coordinator + LoRa2MQTT
tests/                     unit + integration tests (xUnit)
docs/HERMOD_SH.md          comprehensive operator reference
SECURITY.md                full security model + threat analysis
README.md                  high-level overview and pointers
```
