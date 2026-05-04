# Hermod Ansible Deployment

This directory contains an Ansible-based deployment and update system for the Hermod
Universal IoT Translator stack. It is a fully idempotent, role-based solution that can
install, update, verify, and uninstall the stack on a Raspberry Pi 5 running Ubuntu
Server 24.04 LTS arm64.

The system installs MicroK8s, configures USB udev rules for the ZigBee and LoRa radio
dongles, deploys the complete Hermod Kubernetes stack via kustomize, and verifies that
every service is healthy before finishing. All playbooks support Ansible's `--check`
flag for dry-run mode and are safe to rerun at any time.

The optional `updater` role installs a systemd oneshot service and timer that can run
`ansible-pull` on a schedule to keep the deployment up to date. The timer is disabled
by default; you must opt in by setting `hermod_updater_enabled: true`.


## Prerequisites (control machine)

- Ansible >= 2.15 (`pip install --user ansible`)
- `ansible-lint` for linting (optional): `pip install --user ansible-lint`
- `sshpass` if you authenticate with a password instead of an SSH key:
  - Debian/Ubuntu: `sudo apt install sshpass`
  - macOS: `brew install hudochenkov/sshpass/sshpass`
- Python 3.10+


## Prerequisites (target Raspberry Pi 5)

- Ubuntu Server 24.04 LTS arm64, freshly installed
- SSH enabled and reachable from the control machine
- A user named `hermod` (default, created by the cloud-init image hermod-pi flashes) with passwordless sudo, OR a user with a known sudo password
- The Pi must have internet access for apt and snap package downloads
- USB radio dongles may be connected before or after provisioning; the udev rules will
  detect them on next plug-in regardless of when they are installed


## Step-by-step

### 1. Copy and edit the inventory

```bash
cd Hermod/ansible
cp inventory/hosts.yml.example inventory/hosts.yml
```

Edit `inventory/hosts.yml` and replace `192.168.1.XXX` with the Pi's actual IP address
or hostname. If you use a password instead of an SSH key, uncomment `ansible_password`.

> `inventory/hosts.yml` is gitignored (see repo-root `.gitignore`); `hosts.yml.example`
> is tracked. Never commit real credentials.

### 2. Review variables

Open `group_vars/all.yml` and check the defaults. The most important settings are:

| Variable | Default | Notes |
|---|---|---|
| `hermod_git_remote` | `git@PLACEHOLDER:...` | **Must be replaced** with real URL |
| `hermod_git_branch` | `main` | Branch to deploy |
| `hermod_install_path` | `/opt/hermod` | Where the repo is cloned on the Pi |
| `microk8s_channel` | `1.30/stable` | MicroK8s snap channel |
| `hermod_timezone` | `Etc/UTC` | System timezone (override per-deployment) |
| `hermod_updater_enabled` | `false` | Set to `true` to enable scheduled auto-updates |

### 3. Dry run (no changes applied)

```bash
ansible-playbook -i inventory/hosts.yml playbooks/install.yml --check
```

### 4. First-time install

```bash
ansible-playbook -i inventory/hosts.yml playbooks/install.yml
```

This will:
1. Update apt and install system packages
2. Configure swap (2 GB by default) and sysctl for Kubernetes
3. Install MicroK8s and enable dns, storage, registry, ingress, metrics-server
4. Deploy udev rules for the ZigBee and LoRa dongles
5. Clone the repo and apply `Hermod/kubernetes/base/` via kustomize
6. Install the ansible-pull updater service (disabled by default)
7. Run health checks and print access URLs

### 5. Update to latest version

```bash
ansible-playbook -i inventory/hosts.yml playbooks/update.yml
```

This pulls the latest commit from the configured git remote and reapplies the
kustomize manifests. If pods fail to become ready after the update, `kubectl rollout undo`
is attempted automatically for each Deployment.

### 6. Verify health (at any time)

```bash
ansible-playbook -i inventory/hosts.yml playbooks/verify.yml
```

### 7. Automatic state detection

```bash
ansible-playbook -i inventory/hosts.yml playbooks/site.yml
```

`site.yml` detects whether Hermod is already installed and runs install or update tasks
accordingly. You can override detection with `-e hermod_force_install=true` or
`-e hermod_force_update=true`.

### 8. Uninstall (removes Hermod, keeps MicroK8s)

```bash
ansible-playbook -i inventory/hosts.yml playbooks/uninstall.yml -e confirm_uninstall=yes
```

**Warning:** This deletes the `hermod` Kubernetes namespace including all
PersistentVolumeClaims and their data. Make backups first.


## Playbook reference

| Playbook | Purpose |
|---|---|
| `install.yml` | First-time full install |
| `update.yml` | Pull latest and redeploy with rollback |
| `verify.yml` | Health checks only, no changes |
| `uninstall.yml` | Tear down Hermod stack (keep MicroK8s) |
| `site.yml` | Auto-detect install vs update |


## Role reference

| Role | Purpose |
|---|---|
| `base` | OS packages, swap, sysctl, timezone, hostname |
| `microk8s` | MicroK8s snap install, addons, kubeconfig |
| `usb_passthrough` | udev rules for ZigBee and LoRa dongles |
| `hermod_deploy` | Git clone, node labels, `ensure-secrets.sh` (`keep` mode — fills in missing Secrets only), `kubectl apply -k`, pod wait |
| `updater` | systemd service + timer for ansible-pull auto-updates |
| `healthcheck` | Pod readiness, port checks, access URL summary |


## Git remote placeholder

The variable `hermod_git_remote` in `group_vars/all.yml` is set to:

```
git@PLACEHOLDER:REPLACE_WITH_YOUR_USER/hermod.git
```

Replace this with the real SSH or HTTPS URL of the repository before running any
playbook. Example:

```yaml
hermod_git_remote: "git@github.com:your-org/hermod.git"
```

If the remote is not accessible from the Pi (e.g. private repo, no SSH key on the Pi),
pre-clone the repo manually and set `hermod_install_path` to point to it. The git
tasks will then act as a `git pull` rather than a fresh clone.


## Troubleshooting

### MicroK8s snap install is very slow on Raspberry Pi

The snap download can take several minutes on a slow SD card or network connection.
The role sets `--timeout 300` on `microk8s status --wait-ready`. If this expires,
re-run the playbook; it will detect the snap is already installed and skip reinstalling.

### USB udev rules not applying

Run on the Pi:

```bash
sudo udevadm control --reload-rules
sudo udevadm trigger --subsystem-match=tty
ls -la /dev/zigbee_dongle /dev/lora_dongle
```

If the symlinks still do not appear, verify the vendor/product IDs with:

```bash
lsusb
udevadm info -a /dev/ttyUSB0 | grep -E 'idVendor|idProduct|serial'
```

The LoRa dongle IDs may differ depending on the USB-to-serial chip variant.
See the TODO comment in `roles/usb_passthrough/files/99-hermod-usb.rules`.

### kubectl context issues

If `microk8s kubectl` works but plain `kubectl` does not, either use the alias
`/usr/local/bin/kubectl_mk8s` that the microk8s role installs, or run:

```bash
microk8s config > ~/.kube/config
```

The role writes this automatically, but group membership changes require a new login
session to take effect. Log out and back in, then try again.

### Pods stuck in Pending or ImagePullBackOff

The base kustomize overlay uses `imagePullPolicy: Never` for the coordinator image,
meaning the image must already be present in the MicroK8s registry or node. Build and
push the image first:

```bash
# On a machine with Docker and the source code:
docker build -t localhost:32000/hermod/coordinator:latest -f Hermod.Coordinator/Dockerfile src/
docker push localhost:32000/hermod/coordinator:latest
# (adjust registry address to the Pi's IP if pushing remotely)
```

### ansible-pull updater fails

Check the journal on the Pi:

```bash
journalctl -u hermod-updater.service -n 50
```

The most common cause is that `hermod_git_remote` still contains the PLACEHOLDER value,
or the Pi does not have an SSH key authorised for the repository.
