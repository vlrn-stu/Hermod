# hermod-pi image-prep

Containerized image builder for the Hermod Pi provisioning tool. Takes an
official Ubuntu Server 24.04 LTS arm64 preinstalled Raspberry Pi image,
injects a validated cloud-init payload (`user-data`, `meta-data`,
`network-config`) into the `system-boot` FAT32 partition, recompresses, and
emits the result plus a signed manifest.

The container is a pure transform: in goes an SSH key and a config, out goes
a flashable `.img.xz` and its manifest. It does not install MicroK8s, Ansible,
or anything else — that's Stage 2 over SSH.

## Files in this directory

| file | purpose |
|------|---------|
| `Dockerfile` | Debian slim + xz + util-linux + python3.11 + jinja2 + PyYAML + jsonschema |
| `prep.py` | main logic: validate, render, download, mount, inject, compress, manifest |
| `templates/user-data.yaml.j2` | cloud-config rendered to `system-boot/user-data` |
| `templates/network-config.yaml.j2` | netplan v2 rendered to `system-boot/network-config` |
| `tests/test_prep.py` | pytest suite, including an opt-in docker integration test |

## Pinned upstream image

* **File**: `ubuntu-24.04.4-preinstalled-server-arm64+raspi.img.xz`
* **URL**: https://cdimage.ubuntu.com/releases/24.04/release/
* **SHA256**: `790652faeb4f61ce7bb12f5cb61734595c61d3cd882915b8b5f9918106c80d37`

Changing the version requires updating **both** `UBUNTU_VERSION` and
`UBUNTU_IMAGE_SHA256` in `prep.py` — the container refuses to proceed on
checksum mismatch.

## Build

```bash
cd Hermod/tools/pi-installer/image-prep
docker build -t hermod/image-prep:dev .
```

## Run

The container needs loopback-mount capability. Two options, pick one:

### Option A — fully privileged (simplest)

```bash
docker run --rm \
  --privileged \
  -e HERMOD_PI_VERSION=0.1.0 \
  -v "$PWD/output:/output" \
  -v "$PWD/cache:/cache" \
  -v "$PWD/config.yaml:/config.yaml:ro" \
  -v "$PWD/pubkey.pub:/pubkey.pub:ro" \
  hermod/image-prep:dev
```

### Option B — least privilege (preferred for CI)

```bash
docker run --rm \
  --cap-add=SYS_ADMIN \
  --device=/dev/loop-control \
  --security-opt apparmor=unconfined \
  -e HERMOD_PI_VERSION=0.1.0 \
  -v "$PWD/output:/output" \
  -v "$PWD/cache:/cache" \
  -v "$PWD/config.yaml:/config.yaml:ro" \
  -v "$PWD/pubkey.pub:/pubkey.pub:ro" \
  hermod/image-prep:dev
```

Either way you end up with:

```
output/hermod-pi-<hostname>-<UTC-timestamp>.img.xz
output/hermod-pi-<hostname>-<UTC-timestamp>.manifest.json
```

Cache-mount `/cache` to persist the upstream image across runs — saves ~1.2 GB
of re-download per invocation.

## `config.yaml` schema (strict, unknown fields rejected)

```yaml
hostname: pi-thesis-01          # required, RFC 1123 strict (lowercase)
timezone: Europe/Berlin         # default UTC; must exist in tzdata
username: hermod                # default 'hermod'; POSIX-safe, not root
locale: en_US.UTF-8             # default en_US.UTF-8
sudo_nopasswd: true             # default true
runcmd:                         # optional list of shell strings
  - "echo provisioned >> /var/log/hermod.log"
wifi:                           # optional; omit for ethernet-only
  ssid: HermodLab
  passphrase: correcthorsebattery
  country: DE                   # ISO 3166-1 alpha-2 uppercase
```

### Validation rules enforced

| field | rule |
|-------|------|
| `hostname` | `^[a-z][a-z0-9-]{0,62}$` — lowercase, leading letter, max 63 |
| `timezone` | must appear in `zoneinfo.available_timezones()` |
| `username` | `^[a-z_][a-z0-9_-]{0,31}$`, not `root` |
| `locale` | `^[A-Za-z0-9_.@-]+$` |
| `wifi.country` | `^[A-Z]{2}$` |
| `wifi.ssid` | no control chars, no `"` or `\` |
| `wifi.passphrase` | no control chars, no `"` or `\`; 8..63 chars |
| `runcmd` entries | strings, no embedded newlines |
| SSH pubkey | type in {ed25519, rsa, ecdsa-256/384/521, sk-ed25519, sk-ecdsa-256}; body ≥ 40 chars; no-comment warns |
| Unknown config fields | `additionalProperties: false` at every level — typos fail loudly |

## `pubkey.pub`

Single SSH public key in `authorized_keys` format:

```
ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIEJkN8Q3...  v@hermod-laptop
```

Accepted types: `ssh-ed25519`, `ssh-rsa`, `ecdsa-sha2-nistp{256,384,521}`,
`sk-ssh-ed25519@openssh.com`, `sk-ecdsa-sha2-nistp256@openssh.com`.

DSS and unknown types are rejected. The key body is **never** logged; only the
type name is logged (`pubkey injected (type: ssh-ed25519)`).

## Manifest format (`*.manifest.json`)

```json
{
  "schema": "hermod-pi/image-manifest/v1",
  "artifact": "hermod-pi-pi-thesis-01-20260419T120000Z.img.xz",
  "sha256": "...",
  "size": 1234567890,
  "hostname": "pi-thesis-01",
  "username": "hermod",
  "timezone": "Europe/Berlin",
  "generated_at": "2026-04-19T12:00:00Z",
  "tool_version": "0.1.0",
  "ubuntu_version": "24.04.4",
  "source_image_url": "https://cdimage.ubuntu.com/releases/24.04/release/ubuntu-24.04.4-preinstalled-server-arm64+raspi.img.xz",
  "source_image_sha256": "790652faeb4f61ce7bb12f5cb61734595c61d3cd882915b8b5f9918106c80d37",
  "wifi_configured": false
}
```

## What cloud-init does on first boot

* Sets hostname, locale, timezone (via `timedatectl` in `runcmd` for
  deterministic logs).
* Creates the configured user with the injected SSH pubkey, optional
  passwordless sudo, shell `/bin/bash`.
* Disables password SSH globally and disables root login via
  `/etc/ssh/sshd_config.d/10-hermod-pi.conf`.
* `apt update`, installs `snapd openssh-server ca-certificates python3 curl`
  (belt & suspenders — preinstalled images already have these).
* Deletes `/etc/ssh/ssh_host_*` and runs `dpkg-reconfigure openssh-server`
  so every flashed card ends up with unique host keys.
* Writes `/etc/hermod-pi/provisioned.json` (hostname, generated_at,
  tool_version, ubuntu_version) so Stage 2 can verify first boot succeeded.

MicroK8s is **not** installed here — Ansible handles that over SSH.

## Tests

```bash
cd Hermod/tools/pi-installer/image-prep
python3 -m pip install jinja2 PyYAML jsonschema pytest
PYTHONPATH=. pytest tests/ -v
```

Unit tests mock subprocess and do not require root, loop devices, or network.
The docker integration test auto-skips when `docker` is unavailable, and it
always runs in `HERMOD_PI_SKIP_BUILD=1` mode (writes rendered `user-data` /
`network-config` previews into `/output`) so CI doesn't need loopback support.

## Exit codes

| code | meaning |
|------|---------|
| 0 | success |
| 1 | unexpected exception (bug) |
| 2 | config / pubkey validation failed |
| 3 | external command returned non-zero (`xz`, `losetup`, `mount`, ...) |
| 4 | external command timed out |
| 5 | runtime error (e.g. upstream SHA256 mismatch) |

## Security notes

* `subprocess.run(..., shell=False, check=True, timeout=...)` everywhere.
* `umask 0o077` before any output write.
* Output written to `.tmp.<name>` then `fsync` + `os.rename` — atomic swap.
* Only the `system-boot` FAT32 partition is loopback-mounted, with
  `rw,noexec,nodev,nosuid,umask=0077`. The root partition is never mounted.
* Upstream image SHA256 is pinned and verified after download; mismatch aborts
  before any loopback work.
* WiFi passphrases and SSH key bodies are never written to logs.

## Caveats for integrators

* `cache/` volume is optional but strongly recommended — the Ubuntu Pi image
  is ~1.2 GB.
* `xz -6 -T0` compression takes 60–180 s on modern hardware. Drop to `-3` if
  you want faster CI cycles.
* Privileged mode is required for loopback mounting inside the container.
  Option B (SYS_ADMIN + /dev/loop-control) works on most hosts but can fail
  under certain AppArmor / SELinux policies — fall back to `--privileged`.
* `instance-id` is derived from `hostname + UTC timestamp slug`, so flashing
  the same hostname twice produces two different instance-ids (cloud-init
  will re-run on first boot each time).
* Timezone is applied both via cloud-init's `timezone:` key **and**
  `timedatectl` in `runcmd` — intentional belt-and-suspenders.
