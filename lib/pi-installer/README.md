# hermod-pi — Raspberry Pi 5 provisioning for Hermod

A cross-platform CLI that provisions a Raspberry Pi 5 from bare SD card to a
running Hermod deployment in four commands. Built as a thesis artifact: every
step is reproducible, auditable, and documented.

```
flash   →   wait   →   deploy   →   status
```

## Design

Three problems have to be solved cleanly between "hand a user an SD card" and
"Hermod pods serving traffic":

1. **Image preparation** — turn the vanilla Ubuntu Server 24.04 LTS arm64 image
   into one that boots headless with the right user, SSH key, hostname, WiFi,
   timezone. Done inside a container (cross-platform, reproducible).
2. **Flashing** — write the customised image to the correct SD card, never
   anything else. Native `xz | dd` with platform-appropriate privilege
   escalation (`pkexec` on Linux, graphical admin-privileges via `osascript`
   on macOS, clear error on WSL). rpi-imager is available as a fallback via
   `--use-imager`.
3. **Post-boot deployment** — MicroK8s install and Hermod manifest rollout.
   Wraps the existing Ansible playbooks at `../../ansible/`.

```
┌────────────┐   ┌──────────────┐   ┌──────────────┐   ┌────────────┐
│ flash      │ → │ wait         │ → │ deploy       │ → │ status     │
│ prep + SD  │   │ mDNS + SSH   │   │ ansible      │   │ verify.yml │
└────────────┘   └──────────────┘   └──────────────┘   └────────────┘
       │                │                   │
       ▼                ▼                   ▼
  image-prep       ssh_keys.sh         ansible_wrapper.sh
  container        TOFU pin            installs MicroK8s +
  (cloud-init)                         deploys Hermod manifests
```

## Security surface

This is deliberately paranoid. Every design decision documented here.

| Mechanism | Where | Why |
|---|---|---|
| Per-Pi ed25519 keypair, 0600 | `lib/ssh_keys.sh` | No reuse of the user's primary keys; compromise of one Pi never expands blast radius |
| Optional FIDO2 (`ed25519-sk`) | `lib/ssh_keys.sh --fido2` | Hardware-backed SSH if a YubiKey is present |
| Target-size guard ≤ 256 GiB | `lib/device_guard.sh` | Safety net — you physically cannot target a 2 TB NVMe without explicit env opt-in |
| Removable-only enforcement | `lib/device_guard.sh` | Internal drives are a hard no, not bypassable |
| Interactive device confirm | `lib/device_guard.sh::confirm` | User must type the exact device path back; no "y" shortcuts |
| TOFU host-key pinning | `lib/ssh_keys.sh::host_key_pin` | First-boot host keys are captured and locked; any later drift halts Stage 2 with `StrictHostKeyChecking=yes` |
| Tool-local known_hosts | `$HERMOD_PI_HOME/known_hosts` | Never pollutes the user's `~/.ssh/known_hosts` |
| Strict cloud-init schema | `image-prep/prep.py` | Typos in config fail loudly; no silent fall-through |
| SSH host-key regeneration on first boot | `user-data.yaml.j2` | Every flashed card gets unique host identities |
| `ssh_pwauth: false`, no root | `user-data.yaml.j2` | Key auth only, end of story |
| Ubuntu image SHA256 pinned | `image-prep/prep.py` | Mismatched download aborts before loopback |
| Append-only audit log | `$HERMOD_PI_HOME/audit.log` | One JSON line per sensitive op (generate, host_key_pin, mismatches, bypasses) |
| Ansible `StrictHostKeyChecking=yes` | `lib/ansible_wrapper.sh` | Overrides the cfg's `False` — no "accept unknown host" surprises |
| Refuses `ansible_become_password=` on CLI | `lib/ansible_wrapper.sh` | Forces vault for secrets; no `ps`-visible passwords |
| `umask 0077` on every file write | all libs | Leaks-via-perms eliminated |

## Cross-platform support

| OS | Status |
|---|---|
| Linux (Fedora/RHEL/Debian/Ubuntu/Arch/Alpine) | fully supported |
| macOS (bash 4+ via `brew install bash`) | fully supported |
| WSL2 | supported; SD reader needs `usbipd-win` to be visible |
| Git Bash / native Windows | use the PowerShell sibling (planned) |

`hermod-pi config-check` inventories the platform and suggests the exact
install command for any missing dependency.

## Dependencies

All of these are auto-detected. Missing ones print a paste-ready install line
for your platform.

| Dep | Purpose |
|---|---|
| `jq` | JSON parsing in libs |
| `ssh-keygen`, `ssh-keyscan` | keypair + TOFU |
| `ansible-playbook` | Stage 2 deploy |
| `podman` or `docker` | image-prep container |
| `xz`, `dd`, `pkexec` (Linux) / `osascript` (macOS) | native flash |
| `rpi-imager` (optional, via `--use-imager`) | alternative flash UI |
| `avahi-resolve` (Linux/WSL) / `dns-sd` (macOS) | mDNS discovery |

## Configuration

Two levels, deliberately separated:

### Tool config — `hermod-pi.conf` (behaviour)

`./hermod-pi.conf` → `$XDG_CONFIG_HOME/hermod-pi/config.conf` → built-in defaults.

See `hermod-pi.conf.example`. Only a whitelisted set of keys are honoured
(unknown keys → warning, never shell-exec).

### Provisioning config — `config.yaml` (what goes on the SD)

See `config.example.yaml`. Schema is defined by `image-prep/prep.py::CONFIG_SCHEMA`
with `additionalProperties: false` at every nesting level.

## Usage

```bash
# First-run sanity check
hermod-pi config-check

# Stage 1 — prep + flash
hermod-pi flash config.yaml                     # auto-pick removable device
hermod-pi flash config.yaml /dev/sda            # or explicit

# Stage 2 — boot the Pi, then:
hermod-pi wait hermod-edge-01                   # polls mDNS + SSH + first-boot marker
HERMOD_GIT_REMOTE=git@github.com:you/hermod.git \
    hermod-pi deploy hermod-edge-01             # ansible install.yml

# Health check
hermod-pi status hermod-edge-01                 # ansible verify.yml

# List all provisioned Pis
hermod-pi list

# Full end-to-end (interactive pause after flash)
HERMOD_GIT_REMOTE=... hermod-pi all config.yaml
```

### Dry run (no image download, no flash)

```bash
HERMOD_PI_SKIP_BUILD=1 hermod-pi flash config.yaml
```

Renders `user-data.preview.yaml`, `meta-data.preview`, and
`network-config.preview.yaml` into `$HERMOD_PI_HOME/images/`. Great for
reviewing exactly what cloud-init will see before committing to a flash.

## Directory layout

```
tools/pi-installer/
├── hermod-pi                  # main CLI (bash, Linux/macOS/WSL)
├── hermod-pi.conf.example     # tool-behaviour config
├── config.example.yaml        # provisioning config (thesis appendix artefact)
├── lib/
│   ├── device_guard.sh        # cross-platform SD safety checks (708 lines)
│   ├── ssh_keys.sh            # per-Pi ed25519 keypair mgmt + TOFU (820 lines)
│   └── ansible_wrapper.sh     # wraps ../../ansible/ (837 lines)
├── image-prep/
│   ├── Dockerfile             # podman/docker buildable
│   ├── prep.py                # strict config schema + cloud-init injection
│   ├── templates/
│   │   ├── user-data.yaml.j2
│   │   └── network-config.yaml.j2
│   ├── tests/test_prep.py     # 56 unit + 1 integration (docker-gated)
│   └── README.md
└── README.md
```

State directory (`$HERMOD_PI_HOME`, default `$HOME/.hermod-pi`):

```
~/.hermod-pi/
├── keys/                       # 0700
│   ├── <hostname>.key          # 0600 ed25519 private
│   ├── <hostname>.key.pub      # 0644 pubkey
│   └── <hostname>.meta.json    # 0600 generation metadata
├── known_hosts                 # 0600 TOFU pin database
├── inventories/                # 0700
│   └── <hostname>.hosts.yml    # 0600 ansible inventory
├── images/                     # 0700
│   └── hermod-pi-<host>-<ts>.img.xz
├── cache/                      # 0700 Ubuntu base image cache (~1.2 GB)
└── audit.log                   # 0600 append-only JSONL
```

## Audit log

Every sensitive operation appends one JSON line. Sample:

```json
{"ts":"2026-04-19T18:44:05Z","event":"generate","hostname":"hermod-edge-01","key_type":"ed25519","fingerprint":"SHA256:LPYTWB...","tool_version":"0.1.0","user":"v"}
{"ts":"2026-04-19T18:45:10Z","event":"host_key_pin","hostname":"hermod-edge-01","ip":"192.168.1.42","state":"new"}
{"ts":"2026-04-19T18:50:00Z","event":"playbook_run","playbook":"install.yml","hostname":"hermod-edge-01","exit_code":0,"duration_sec":184}
```

Events: `init`, `generate`, `remove`, `host_key_pin`, `host_key_mismatch`,
`fido2_requested`, `passphrase_prompted`, `playbook_run`, `size_guard_bypass`.

## Testing

Each library has inline tests runnable directly:

```bash
bash lib/device_guard.sh test      # 13 tests
bash lib/ssh_keys.sh test          # 19 tests
bash lib/ansible_wrapper.sh test   # 12 tests

cd image-prep && pytest tests/     # 56 unit tests
```

## Troubleshooting

| Symptom | Usually | Fix |
|---|---|---|
| `Permission denied: '/pubkey.pub'` in container | SELinux | Fedora host detected automatically and uses `:z` labels — ensure `/etc/selinux/config` matches runtime |
| `no removable candidate devices found` | SD reader unplugged or not removable-labelled | Re-seat; check `lsblk -o NAME,RM` shows RM=1 |
| `host_key_mismatch` after reflash | Pi got a new ed25519 host key (expected on first boot regen) | Remove the `<hostname>` entry from `$HERMOD_PI_HOME/known_hosts` and re-run `wait` |
| `mDNS discovery timed out` | Ethernet with no router that proxies mDNS | Pass `--ip=192.168.x.x` to `wait` / `discover` |
| `HERMOD_GIT_REMOTE must be set` on deploy | Intentional | Set it or deploy will clone a placeholder |
| Container `--privileged` refused | Rootless podman without loop access | Set `HERMOD_PI_FORCE_PRIVILEGED=1` or use docker |

## Thesis context

This tool implements the "operational simplicity" story (claim O5) for Hermod:
reproducible deployment from bare hardware to running system in a documented,
auditable way. The audit log plus `config.example.yaml` are meant to serve as
appendix-grade artefacts — paste the log, paste the config, done.
