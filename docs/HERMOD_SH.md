# `hermod.sh` — Operator Reference

`hermod.sh` is the single operator entry point for the Hermod stack. It unifies docker-compose (single-host dev), kustomize-on-microk8s (production Pi), and kustomize-on-kind (PC sandbox) under one CLI, plus secret management, the bundled text user interface, and Pi greenfield provisioning helpers.

This document is the canonical reference. Run `./hermod.sh help` for the short version; this file covers every subcommand with intent, side effects, and prerequisites.

---

## Table of contents

1. [Run model](#run-model)
2. [The text UI](#the-text-ui)
3. [Targets](#targets)
4. [Subcommands](#subcommands)
5. [Pi greenfield bring-up flow](#pi-greenfield-bring-up-flow)
6. [Edge-TLS profile flow](#edge-tls-profile-flow)
7. [Secrets and the operator vault](#secrets-and-the-operator-vault)
8. [Users and seed roster](#users-and-seed-roster)
9. [Safety properties](#safety-properties)

---

## Run model

`hermod.sh` runs **on the operator's host**. It assumes the host has:

* `bash`, `ssh`, `rsync`, `scp` for Pi targets
* `kubectl`, `kind`, `podman` (or docker) for kind targets
* `docker compose` (plugin) for compose targets
* `ansible-playbook` for Pi greenfield bring-up
* `jq`, `openssl` for secret + cert generation
* `gpg` for the optional mimir-encrypted operator vault

`./hermod.sh doctor` walks the dependency surface for each path and reports `[+]` / `[X]` / `[!]` per tool.

The script is `set -euo pipefail` from the top. SSH connections use `StrictHostKeyChecking=yes` against an explicit `UserKnownHostsFile`; first-boot key pinning happens during greenfield provisioning via `lib/pi-installer/hermod-pi`.

---

## The text UI

Run `./hermod.sh` (no args) or `./hermod.sh tui` to drop into the alternate-buffer text UI. Eight sections, each with a one-letter mnemonic:

* `c` Compose — dev stack actions
* `g` Provisioning — Pi flash, wait-for-Pi, and full provision
* `p` Production — install / update / status / kick / logs / reset / change-password / etc.
* `v` Secrets — operator vault (mimir): init / unlock / lock / rekey / status
* `u` Users — local seed roster editor (init / list / add / remove / set-role / set-password)
* `n` Network/TLS — cert-manager, tunnel-secret, dns-secret, rotate-certs
* `s` Settings — env file, image source, protocol toggles, ingress limiter
* `d` Diagnostics — doctor, metrics, logs, health, pi-doctor, cleanup

Right pane shows the current section; bottom row shows context-sensitive action keys (`1`–`9` for primary actions, uppercase for destructive or secret-revealing actions). Cancel-friendly: every yes/no prompt accepts Esc, every input prompt shows the Enter / Esc contract.

`/` filters the current pane, `:` opens a free-form command palette, `?` toggles the keybind reference, `q` quits without affecting running deployments.

---

## Targets

Targets are resolved by `resolve_target()`. Each target emits `kind=`, `overlay=`, `namespace=`, and friends, which the calling subcommand `eval`s into local scope.

| Target | Overlay | Notes |
|---|---|---|
| `prod-pi` | `overlays/prod-pi` (extends `overlays/prod`) | Raspberry Pi 5, microk8s, full prod hardening (mTLS, internal CA). Pi-specific patches enable real LoRa + BLE hardware and add the LAN NodePort split. The default operator target. |
| `prod-kind` | `overlays/prod` | Local kind cluster, full prod-grade stack on the operator's PC. Useful for dry-running prod changes before they hit the Pi. |
| `prod-pi-letsencrypt` | `overlays/prod-pi-letsencrypt` | **Passive.** Acquires a Let's Encrypt cert via DNS-01, no Ingress, no public traffic. |
| `prod-pi-letsencrypt-ingress` | `overlays/prod-pi-letsencrypt-ingress` | **Active.** Adds nginx Ingress + HSTS preload. Public exposure on `hermod.<your-domain>`. |
| `prod-pi-letsencrypt-cloudflare-tunnel` | `overlays/prod-pi-letsencrypt-cloudflare-tunnel` | **Passive.** Installs cloudflared at replicas=0 (parked). |
| `prod-pi-letsencrypt-cloudflare-tunnel-active` | `overlays/prod-pi-letsencrypt-cloudflare-tunnel-active` | **Active.** Scales tunnel to 2; opens public reach via Cloudflare. |
| `prod-pi-cloudflare-zero-trust` | `overlays/prod-pi-cloudflare-zero-trust` | **Active.** Tunnel + Cloudflare Access gate; fail-closed init container. |

The five edge-TLS targets share the same Pi host as `prod-pi`; they swap only the kustomize overlay. Their installs are *light*: pure `kustomize apply` against the chosen overlay; no addon shuffling, no cert seeding, no image rebuild.

Targets carrying `go_live=1` (the three `*-active` and the `*-zero-trust` variant) refuse to install unless `HERMOD_GO_LIVE=YES` is set in the environment. This is a defence-in-depth gate: applying these overlays exposes traffic publicly, and the variable forces an explicit operator confirmation per invocation.

---

## Subcommands

### Lifecycle

| Subcommand | Purpose |
|---|---|
| `tui` *(default if no args)* | Launch the text UI. |
| `compose <action> [svc]` | Single-host docker compose. `action` ∈ `up`, `down`, `restart`, `logs`, `status`, `build`, `pull`, `creds`, `reset`. |
| `install <target> [--no-build]` | Provision target from scratch. Idempotent on most steps. `--no-build` skips the on-Pi image rebuild and uses whatever is already cached. |
| `update <target> [--rebuild <coord\|lora2mqtt\|all>]` | Rsync source → target + reapply manifests + rolling restart. With `--rebuild`, also `docker build` the named image on the Pi and side-load it into microk8s before the rollout. |
| `redeploy <target>` | `teardown` + `install`. Full cycle, with a short settle pause. |
| `teardown <target>` | `kubectl delete -k overlay`. Cert Secrets are preserved by `$patch: delete` on the Namespace. PVCs **are** deleted. |
| `reset <target>` | DESTRUCTIVE clean-slate. Requires `HERMOD_RESET_CONFIRM=YES`. |
| `pi-reset` | Alias for `reset prod-pi`. |
| `update-repo` | Safe `git pull --ff-only` of the repo root; refuses to merge or rebase. Used to refresh the operator host before a deploy. |

### State and visibility

| Subcommand | Purpose |
|---|---|
| `status <target>` | Pod / certificate / ingress status table. |
| `logs <target> [pod] [container]` | Tail logs. No args = all pods, last 30 lines, all containers. |
| `secrets <target>` | Print Vault42 / MQTT / Postgres / CA fingerprints from cluster Secrets. Redirect to a file, then `chmod 0600`. |
| `metrics <target> [pattern]` | Fetch the Coordinator's `/metrics` endpoint; optional grep-style filter. |
| `kick <target> [deploy]` | Force rollout restart without rebuild. No deploy arg = restart all hermod-prod deployments in least-disruptive order. |
| `cleanup <target>` | Prune stuck or evicted pods from the target Hermod namespace. |
| `roll-jwks <target>` | Restart vault42 + coord without rotating the signing key. Refreshes JWKS caches. |
| `protocol <on\|off> <name> <target>` | Scale a translator (`lora`, `zigbee`, `ble`, `wifi`) to 0 or 1 replicas. |
| `limiter <target> <knob> [on\|off]` | Hot-toggle the ingress limiter (`rate`, `dedup`, `show`) on the live Coordinator without restart. |
| `image-source <target> <local\|ghcr>` | Switch the cluster between the on-Pi rebuilt image and the published GHCR image. |
| `doctor` | Verify the host has the dependencies needed for each path. |

### Auth and credential rotation

| Subcommand | Purpose |
|---|---|
| `users <action> [args]` | Edit the local seed roster (`~/.hermod-pi/seed-users.json`, optionally mimir-encrypted). Actions: `init`, `list`, `add <email> <role>`, `remove <email>`, `set-role <email> <role>`, `set-password <email>`. The next `install` or `update` pushes the file to the cluster Secret; the post-rollout hook wipes it once Vault42 has bcrypted the passwords into `auth.users`. |
| `change-password <target>` | Rotate Vault42 seed credentials (drops the vault DB, reseeds with the new password set via `users` or in `hermod-prod.env`). |
| `rotate-certs <target>` | Roll the mTLS leaf certificates. The internal CA is preserved; the Coordinator hot-reloads, other components are restarted to pick up theirs. |
| `seed-users <target>` | Re-render the Vault42 seed Secret and restart vault42 to import the roster. Use after editing `users` when you do not want a full `install`. |
| `reset-db <target>` | Drop the Vault42 + Hermod databases and restart consumers. Requires `HERMOD_RESET_CONFIRM=YES`. |

### Operator vault (`mimir`)

| Subcommand | Purpose |
|---|---|
| `mimir init [file]` | Encrypt `<file>` (default `hermod-prod.env`) with a PIN; press Enter for no-PIN. Plaintext is shredded after encryption. |
| `mimir unlock [file]` | Populate the session cache so subsequent subcommands do not prompt again. |
| `mimir lock [file]` | Shred the session cache. Without a file, locks every encrypted vault under the repo. |
| `mimir rekey [file]` | Decrypt + re-encrypt with a new PIN. |
| `mimir status [file]` | Show cache state for every `.mimir` file under the repo. |
| `mimir load [file]` | Print the decrypted contents to stdout. |

The mimir vault is opt-in: with no `.mimir` file, `hermod.sh` reads `hermod-prod.env` as plain text. With a `.mimir` file present, it auto-decrypts on boot (using the cached PIN if warm).

### Edge-TLS secrets (Cloudflare integrations)

| Subcommand | Purpose |
|---|---|
| `tunnel-secret <target> [--from-file PATH]` | Write a Secret named `cloudflared-token` holding the Cloudflare Tunnel JWT. Reads from a silent terminal prompt by default; `--from-file` or `HERMOD_TUNNEL_TOKEN_FILE` for scripted use. Input is filtered through a JWT extractor so pasting the entire `cloudflared service install --token …` line works. |
| `dns-secret <target> [--from-file PATH]` | Write a Secret named `cloudflare-api-token` (key `api-token`) in the `cert-manager` namespace, used by the DNS-01 ACME solver. Same prompt model. Required token scopes: `Zone:DNS:Edit` AND `Zone:Zone:Read` on the zone(s) cert-manager will manage. |

### Certificate management

| Subcommand | Purpose |
|---|---|
| `cert <target> status` | List every Certificate resource and any in-flight Challenges. |
| `cert <target> request <hostname>` | Write a `Certificate` resource for `<hostname>` in the target's namespace. Uses the `letsencrypt-prod` ClusterIssuer (DNS-01 via Cloudflare); 90-day cert with ECDSA P-256 key, auto-renews 30d before expiry. Secret name = hostname with dots replaced by hyphens. |
| `cert <target> show [name]` | Print Certificate status + the x509 chain details (Subject, Issuer, Validity, SAN) from the underlying TLS Secret. Default name: `hermod-public-tls`. |

### Pi greenfield provisioning

These are thin delegates to `lib/pi-installer/hermod-pi`. The tool owns the heavy lifting (cloud-init image-prep container, mDNS discovery, ansible wrapper, TOFU host-key pin, dedicated ed25519 keypair per Pi).

| Subcommand | Purpose |
|---|---|
| `flash <config.yaml> [<device>]` | Build a customised cloud-init image and write the SD card via `pkexec` / `osascript`. |
| `wait-pi <hostname> [timeout]` | mDNS-discover `<hostname>.local`, TOFU-pin its host key, wait for the cloud-init first-boot marker. |
| `provision <config.yaml> [<device>]` | Full bring-up: flash → wait → ansible install → ansible verify. |
| `pi-status <hostname>` | Ansible verify playbook (host health: kernel cmdline, cgroup v2, microk8s version, addon list, namespace presence). |
| `pi-uninstall <hostname>` | DESTRUCTIVE: remove microk8s + Hermod state via ansible. Use when retiring a Pi. |
| `pi-keys` | List managed Pi keypairs under `~/.hermod-pi/keys/`. |
| `pi-doctor` | Pi-installer's own dependency check. |

### Misc

| Subcommand | Purpose |
|---|---|
| `config <subcmd>` | Manage `~/.config/hermod/config` (Pi SSH coords, default env path). Subcommands: `show`, `set KEY=VALUE`, `unset KEY`, `edit`, `init` (bootstraps from the inventory under `~/.hermod-pi/`), `path`. |
| `menu` | Old alias for the TUI; kept for muscle memory. |
| `help` | Short usage. |

---

## Pi greenfield bring-up flow

```
1. hermod.sh flash <config.yaml>
       └─> hermod-pi flash
             └─> lib/pi-installer/image-prep/prep.py
                   • Downloads pinned Ubuntu Server 24.04.4 arm64 preinstalled image
                   • Verifies SHA256
                   • Loop-mounts the system-boot FAT32 partition
                   • Injects user-data, network-config, meta-data templates
                       (SSH key, network IP, hostname, swap, apt upgrades)
                   • Recompresses, emits manifest
             └─> writes the customised image to the SD card
                  via dd + sudo/pkexec, with explicit device guards
                  (lib/device_guard.sh refuses to write to /dev/sda etc.)

2. Insert SD into Pi, power on. Ubuntu boots, cloud-init runs, SSH
   becomes reachable, /etc/hermod-pi/provisioned.json appears.

3. hermod.sh wait-pi <hostname>
       └─> hermod-pi wait
             • mDNS-discovers <hostname>.local
             • TOFU-pins the host key into ~/.hermod-pi/known_hosts
             • Polls for the first-boot marker

4. hermod.sh install prod-pi
       └─> Bring-up:
            1. Pi host deps (bluez for ble2mqtt BLE scan)
            2. Wait microk8s ready, restore addons (dns, hostpath-storage,
               registry), extend NodePort range to 1024-65535, disable the
               ingress addon (NodePort-only philosophy)
            3. Rsync the repo to /opt/hermod
            4. SCP the operator vault (hermod-prod.env) to the Pi
            5. Build hermod-coordinator + lora2mqtt arm64 images on the Pi
               via Docker, save+import into microk8s ctr
            6. Issue the internal CA + 12 leaf certs locally on the operator
               host, then rsync ~/.hermod-prod-certs/ to the Pi
            7. Seed cert Secrets into the namespace via lib/seed-internal-certs.sh
            8. Populate app Secrets (Vault42, MQTT, Postgres) from
               hermod-prod.env via ensure-secrets.sh in from-env mode,
               and push the local seed-users.json into the seed-json key
            9. kustomize apply overlays/prod-pi
           10. Rollout restart all Hermod-owned deployments
           11. Wipe the seed-json key once Vault42 has imported it

5. hermod.sh pi-status <hostname>   # verify host health
   hermod.sh status prod-pi         # verify pod state
```

The base install is destructive on the namespace: PVCs are wiped on each install. Cert Secrets are preserved across `teardown` because the Namespace deletion is excluded from the rendered overlay.

---

## Edge-TLS profile flow

After `hermod.sh install prod-pi` completes, the cluster is reachable on `https://<pi-lan-ip>:42069/` (the IP from `HERMOD_PI_SSH_HOST`) with a self-signed internal CA cert that browsers do not trust. The five edge-TLS profiles add publicly-trusted TLS at the cluster edge in graduated stages:

```
hermod.sh dns-secret prod-pi
    └─> writes cloudflare-api-token Secret in cert-manager namespace
        (Cloudflare API token with Zone:DNS:Edit + Zone:Zone:Read on the zone)

hermod.sh install prod-pi-letsencrypt
    └─> Light apply: kustomize creates ClusterIssuer + Certificate request.
        cert-manager runs DNS-01 against your CF zone and stores the issued
        cert in a Secret named hermod-public-tls. NO Ingress, NO traffic.

# Then ONE of:

(a) HERMOD_GO_LIVE=YES hermod.sh install prod-pi-letsencrypt-ingress
    └─> Adds nginx Ingress + HSTS preload; exposes the Coordinator publicly
        on hermod.<your-domain>. nginx terminates the public TLS and
        re-encrypts to the Coord pod over the internal CA mTLS chain.

(b) hermod.sh tunnel-secret prod-pi
    hermod.sh install prod-pi-letsencrypt-cloudflare-tunnel
    └─> Installs a parked cloudflared Deployment (replicas=0). The tunnel
        is not yet open; this is just a manifest validation step.

    HERMOD_GO_LIVE=YES hermod.sh install prod-pi-letsencrypt-cloudflare-tunnel-active
    └─> Scales cloudflared to 2 replicas. Tunnel opens. Cloudflare terminates
        the public TLS at its edge with its own publicly-trusted cert.

(c) HERMOD_GO_LIVE=YES hermod.sh install prod-pi-cloudflare-zero-trust
    └─> Active tunnel + CF Access gate. The cloudflared Deployment carries
        an init container that refuses to start until zero-trust-marker.yaml
        is edited away from its placeholder values (CF_ACCESS_AUD,
        CF_ACCESS_TEAM_DOMAIN). This is the structural "must be explicitly
        set" enforcement.
```

See `kubernetes/overlays/prod-pi-letsencrypt/README.md` and the sibling READMEs for per-overlay prerequisites and rationale; see `SECURITY.md` for the two-PKI rationale (internal CA for in-cluster mTLS; publicly-trusted cert at the edge).

---

## Secrets and the operator vault

`hermod.sh` reads its operator secrets from a single file, `hermod-prod.env` (gitignored), at the repo root. Override the path with `HERMOD_PROD_ENV`. See `hermod-prod.env.example` for the full schema.

Two paths to manage that file:

* **Plaintext** — keep `hermod-prod.env` on a trusted operator host with `chmod 0600`. Simplest, fine for a single-laptop deploy.
* **Mimir-encrypted** — `hermod.sh mimir init` encrypts the file in place with a PIN, shreds the plaintext, and decrypts on demand into a session cache. Used when the operator host is shared or backed up to a less-trusted location.

Two Cloudflare-side secrets bypass `hermod-prod.env` and use a dedicated subcommand each: `tunnel-secret` (cloudflared JWT) and `dns-secret` (Cloudflare DNS API token, scopes `Zone:DNS:Edit` + `Zone:Zone:Read`). Both subcommands accept the token from a silent terminal prompt by default, or from a file via `--from-file PATH`. The token never appears in argv (kubectl reads YAML from stdin) or in shell history (`read -rs`).

---

## Users and seed roster

Vault42 imports its first-login user roster from a seed file on first boot. Hermod ships a single canonical roster source that lives on the operator host:

* **Local file**: `~/.hermod-pi/seed-users.json`, mimir-encrypted if you have run `mimir init` on it. Edit it via `hermod.sh users add | remove | set-role | set-password`.
* **Push on install/update**: every `install` and `update` ships the file to the cluster as the `seed-json` key on the `vault42-seed-credentials` Secret.
* **Wipe after import**: once Vault42's startup has bcrypted the passwords into `auth.users`, the post-rollout hook replaces the `seed-json` key with `""`. Plaintext seed material does not linger in the cluster.

Vault42 will not re-import an existing email; to change a user that has already been imported you must drop the vault DB before the next install:

```bash
hermod.sh users set-password admin@hermod.local
hermod.sh reset-db prod-pi          # drops vault + hermod DBs
hermod.sh install prod-pi           # reseeds from the new file
```

Three default accounts (`viewer`, `user`, `operator` on `@hermod.local`) are bootstrapped by `hermod.sh users init` if you do not want to invent your own.

---

## Safety properties

These are the invariants `hermod.sh` enforces beyond what the underlying tools do:

* **`set -euo pipefail`** — any command failure aborts.
* **Strict SSH host-key checking** — every SSH invocation uses `StrictHostKeyChecking=yes` with an explicit `UserKnownHostsFile`. No TOFU inside `hermod.sh`; key pinning happens during greenfield provisioning via `hermod-pi`.
* **Secrets pass via stdin, not argv** — `tunnel-secret` and `dns-secret` both `kubectl apply -f -` from a heredoc; the token value never appears in `ps`.
* **`go_live=1` gate on active overlays** — the three `*-active` variants and `*-zero-trust` refuse to install without `HERMOD_GO_LIVE=YES`. The TUI menu adds typed-confirmation on top.
* **Reset confirmation** — `cmd_reset` and `cmd_reset_db` require `HERMOD_RESET_CONFIRM=YES`. The `pi-reset` alias is intentional and also requires it.
* **Post-rollout seed wipe** — every install/update path that pushes a seed-roster Secret runs `_wipe_seed_secret` once vault42 reaches Ready, replacing the `seed-json` key with `""`. The plaintext only ever lives on the operator host.
* **No image-pull from public registries during install** — coord and lora2mqtt are built from source on the Pi and imported into microk8s `ctr` directly. `image-source <target> ghcr` switches to the published image once the GHCR pipeline ships.

See `SECURITY.md` for the full security model and threat analysis.
