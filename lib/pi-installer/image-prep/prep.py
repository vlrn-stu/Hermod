#!/usr/bin/env python3
"""hermod-pi image-prep

Take an official Ubuntu Server 24.04 arm64 preinstalled Raspberry Pi image,
inject cloud-init files (user-data, meta-data, network-config) into the
system-boot FAT32 partition, recompress, and emit a manifest.

Security posture
----------------
* All subprocess calls use shell=False, check=True, with an explicit timeout.
* Inputs are validated before Jinja rendering; Jinja autoescape is off but
  inputs have already been constrained to safe character sets.
* SSH pubkeys are never logged in full; only the key type.
* WiFi passphrases are never logged.
* Loopback mount is scoped to the system-boot partition only; root partition
  is never mounted writable.
* Output is written to a .tmp.* file, fsync'd, then os.rename'd atomically.
* umask 0077 for every write.

CLI
---
    prep.py --config /config.yaml --pubkey /pubkey.pub --output-dir /output

Environment
-----------
    HERMOD_PI_VERSION      baked into manifest + templates
    HERMOD_PI_CACHE        upstream image cache dir (default /cache)
    HERMOD_PI_OUTPUT       override for --output-dir
    HERMOD_PI_SKIP_BUILD   internal test hook — skip loopback build phase
"""

from __future__ import annotations

import argparse
import datetime as _dt
import hashlib
import json
import logging
import os
import re
import shutil
import subprocess
import sys
import tempfile
import urllib.request
import zoneinfo
from pathlib import Path
from typing import Any

import jinja2
import jsonschema
import yaml


# --- pinned upstream -------------------------------------------------------

UBUNTU_VERSION = "24.04.4"
UBUNTU_IMAGE_NAME = f"ubuntu-{UBUNTU_VERSION}-preinstalled-server-arm64+raspi.img.xz"
UBUNTU_IMAGE_URL = (
    f"https://cdimage.ubuntu.com/releases/24.04/release/{UBUNTU_IMAGE_NAME}"
)
# SHA256 from https://cdimage.ubuntu.com/releases/24.04/release/SHA256SUMS
# Verified 2026-04-19. Change this *and* UBUNTU_VERSION together.
UBUNTU_IMAGE_SHA256 = (
    "790652faeb4f61ce7bb12f5cb61734595c61d3cd882915b8b5f9918106c80d37"
)

# --- validation regex ------------------------------------------------------

# RFC 1123 strict-lowercase subset: start with a letter, 1..63 chars total,
# lowercase letters / digits / hyphens only. Matches what cloud-init will
# happily accept and what mDNS won't mangle.
HOSTNAME_RE = re.compile(r"^[a-z][a-z0-9-]{0,62}$")

# ISO 3166-1 alpha-2
ISO3166_RE = re.compile(r"^[A-Z]{2}$")

# SSH pubkey: `type base64 [comment]`. type is one of the accepted families.
SSH_TYPES = {
    "ssh-ed25519",
    "ssh-rsa",
    "ecdsa-sha2-nistp256",
    "ecdsa-sha2-nistp384",
    "ecdsa-sha2-nistp521",
    "sk-ssh-ed25519@openssh.com",
    "sk-ecdsa-sha2-nistp256@openssh.com",
}
SSH_PUBKEY_RE = re.compile(r"^([A-Za-z0-9\-@._]+)\s+([A-Za-z0-9+/=]+)(\s+.*)?$")

# --- config schema ---------------------------------------------------------

# strict: additionalProperties:false at every level so a typo fails loudly.
CONFIG_SCHEMA: dict[str, Any] = {
    "$schema": "https://json-schema.org/draft/2020-12/schema",
    "type": "object",
    "additionalProperties": False,
    "required": ["hostname"],
    "properties": {
        "hostname": {"type": "string", "minLength": 1, "maxLength": 63},
        "timezone": {"type": "string", "default": "UTC"},
        "username": {"type": "string", "default": "hermod"},
        "locale": {"type": "string", "default": "en_US.UTF-8"},
        "sudo_nopasswd": {"type": "boolean", "default": True},
        "runcmd": {
            "type": "array",
            "items": {"type": "string", "minLength": 1},
            "default": [],
        },
        "wifi": {
            "type": "object",
            "additionalProperties": False,
            "required": ["ssid", "passphrase", "country"],
            "properties": {
                "ssid": {"type": "string", "minLength": 1, "maxLength": 32},
                "passphrase": {"type": "string", "minLength": 8, "maxLength": 63},
                "country": {"type": "string", "minLength": 2, "maxLength": 2},
            },
        },
    },
}

CONFIG_DEFAULTS: dict[str, Any] = {
    "timezone": "UTC",
    "username": "hermod",
    "locale": "en_US.UTF-8",
    "sudo_nopasswd": True,
    "runcmd": [],
}


# --- logging ---------------------------------------------------------------


def _setup_logging() -> logging.Logger:
    log = logging.getLogger("hermod-pi.image-prep")
    if log.handlers:
        return log
    handler = logging.StreamHandler(sys.stderr)
    handler.setFormatter(
        logging.Formatter("[%(asctime)s] %(levelname)s %(message)s")
    )
    log.addHandler(handler)
    log.setLevel(logging.INFO)
    return log


LOG = _setup_logging()


# --- validation ------------------------------------------------------------


class ValidationError(ValueError):
    """Raised when user input fails validation. Always safe to print."""


def validate_hostname(hostname: str) -> str:
    if not isinstance(hostname, str):
        raise ValidationError("hostname must be a string")
    if not HOSTNAME_RE.fullmatch(hostname):
        raise ValidationError(
            f"hostname {hostname!r} is not RFC 1123 compliant: "
            "must start with a lowercase letter, contain only lowercase "
            "letters/digits/hyphens, and be 1..63 chars long"
        )
    return hostname


def validate_timezone(tz: str) -> str:
    if tz not in zoneinfo.available_timezones():
        raise ValidationError(f"timezone {tz!r} not in zoneinfo database")
    return tz


def validate_wifi_country(country: str) -> str:
    if not ISO3166_RE.fullmatch(country):
        raise ValidationError(
            f"wifi.country {country!r} must be ISO 3166-1 alpha-2 uppercase"
        )
    return country


def validate_wifi_ssid(ssid: str) -> str:
    # SSIDs allow almost anything, but we disallow control chars and the
    # quote/backslash that would escape our template's double-quoted YAML.
    if any(ord(c) < 0x20 for c in ssid):
        raise ValidationError("wifi.ssid contains control characters")
    if '"' in ssid or "\\" in ssid:
        raise ValidationError(
            'wifi.ssid may not contain " or \\ (template safety)'
        )
    return ssid


def validate_wifi_passphrase(pw: str) -> str:
    if any(ord(c) < 0x20 for c in pw):
        raise ValidationError("wifi.passphrase contains control characters")
    if '"' in pw or "\\" in pw:
        raise ValidationError(
            'wifi.passphrase may not contain " or \\ (template safety)'
        )
    return pw


def validate_username(name: str) -> str:
    if not re.fullmatch(r"[a-z_][a-z0-9_-]{0,31}", name):
        raise ValidationError(f"username {name!r} is not POSIX-safe")
    if name == "root":
        raise ValidationError("username must not be root")
    return name


def validate_locale(locale: str) -> str:
    if not re.fullmatch(r"[A-Za-z0-9_.@-]+", locale):
        raise ValidationError(f"locale {locale!r} has unexpected characters")
    return locale


def validate_runcmd(cmds: list[str]) -> list[str]:
    out: list[str] = []
    for cmd in cmds:
        if not isinstance(cmd, str):
            raise ValidationError("runcmd entries must be strings")
        if "\n" in cmd:
            raise ValidationError("runcmd entries may not contain newlines")
        out.append(cmd)
    return out


def validate_pubkey(raw: str, allow_no_comment: bool = False) -> tuple[str, str]:
    """Return (key_type, normalized_line). Never logs the key."""
    line = raw.strip()
    if not line or line.startswith("#"):
        raise ValidationError("SSH pubkey file is empty or only comments")
    if "\n" in line:
        # take just the first key line
        line = line.splitlines()[0].strip()
    m = SSH_PUBKEY_RE.fullmatch(line)
    if not m:
        raise ValidationError("SSH pubkey does not match 'type base64 [comment]'")
    key_type, b64, comment = m.group(1), m.group(2), m.group(3)
    if key_type not in SSH_TYPES:
        raise ValidationError(
            f"SSH pubkey type {key_type!r} not allowed "
            f"(allowed: {sorted(SSH_TYPES)})"
        )
    if len(b64) < 40:
        raise ValidationError("SSH pubkey base64 body suspiciously short")
    if comment is None or not comment.strip():
        if not allow_no_comment:
            LOG.warning(
                "SSH pubkey has no comment field — accepted but consider adding one"
            )
    return key_type, line


def load_and_validate_config(path: Path) -> dict[str, Any]:
    with path.open("r", encoding="utf-8") as fh:
        raw = yaml.safe_load(fh)
    if not isinstance(raw, dict):
        raise ValidationError("config YAML top level must be a mapping")
    jsonschema.validate(instance=raw, schema=CONFIG_SCHEMA)
    cfg: dict[str, Any] = {**CONFIG_DEFAULTS, **raw}
    cfg["hostname"] = validate_hostname(cfg["hostname"])
    cfg["timezone"] = validate_timezone(cfg["timezone"])
    cfg["username"] = validate_username(cfg["username"])
    cfg["locale"] = validate_locale(cfg["locale"])
    cfg["runcmd"] = validate_runcmd(cfg.get("runcmd", []))
    if "wifi" in cfg and cfg["wifi"]:
        w = cfg["wifi"]
        cfg["wifi"] = {
            "ssid": validate_wifi_ssid(w["ssid"]),
            "passphrase": validate_wifi_passphrase(w["passphrase"]),
            "country": validate_wifi_country(w["country"]),
        }
    return cfg


# --- templating ------------------------------------------------------------


def _jinja_env(template_dir: Path) -> jinja2.Environment:
    return jinja2.Environment(
        loader=jinja2.FileSystemLoader(str(template_dir)),
        autoescape=False,  # YAML is not HTML; we've sanitized inputs
        undefined=jinja2.StrictUndefined,
        keep_trailing_newline=True,
    )


def render_user_data(
    cfg: dict[str, Any],
    ssh_pubkey_line: str,
    tool_version: str,
    generated_at: str,
    template_dir: Path,
) -> str:
    env = _jinja_env(template_dir)
    tmpl = env.get_template("user-data.yaml.j2")
    return tmpl.render(
        hostname=cfg["hostname"],
        username=cfg["username"],
        timezone=cfg["timezone"],
        locale=cfg["locale"],
        sudo_nopasswd=cfg["sudo_nopasswd"],
        ssh_pubkey=ssh_pubkey_line,
        extra_runcmd=cfg.get("runcmd", []),
        tool_version=tool_version,
        ubuntu_version=UBUNTU_VERSION,
        generated_at=generated_at,
    )


def render_network_config(
    cfg: dict[str, Any],
    tool_version: str,
    template_dir: Path,
) -> str:
    env = _jinja_env(template_dir)
    tmpl = env.get_template("network-config.yaml.j2")
    return tmpl.render(
        wifi=cfg.get("wifi"),
        tool_version=tool_version,
    )


def render_meta_data(hostname: str, generated_at: str) -> str:
    # cloud-init NoCloud wants instance-id unique per machine so the datasource
    # re-runs when we re-flash a card with a new config.
    instance_id = f"{hostname}-{generated_at.replace(':', '').replace('-', '')}"
    return f"instance-id: {instance_id}\nlocal-hostname: {hostname}\n"


# --- subprocess helper -----------------------------------------------------


def _run(argv: list[str], timeout: int = 600, **kw: Any) -> subprocess.CompletedProcess[bytes]:
    LOG.debug("exec: %s", " ".join(argv))
    return subprocess.run(  # noqa: S603 — shell=False, argv is a list
        argv,
        check=True,
        shell=False,
        timeout=timeout,
        **kw,
    )


# --- upstream image fetch --------------------------------------------------


def _sha256_of(path: Path, chunk: int = 1024 * 1024) -> str:
    h = hashlib.sha256()
    with path.open("rb") as fh:
        while True:
            buf = fh.read(chunk)
            if not buf:
                break
            h.update(buf)
    return h.hexdigest()


def ensure_upstream_image(cache_dir: Path) -> Path:
    """Download (cached) the pinned upstream image and verify SHA256."""
    cache_dir.mkdir(parents=True, exist_ok=True)
    target = cache_dir / UBUNTU_IMAGE_NAME
    if target.exists():
        digest = _sha256_of(target)
        if digest == UBUNTU_IMAGE_SHA256:
            LOG.info("upstream image cache hit: %s", target)
            return target
        LOG.warning(
            "cached upstream image checksum mismatch (got %s); redownloading",
            digest,
        )
        target.unlink()
    LOG.info("downloading upstream image %s", UBUNTU_IMAGE_URL)
    tmp = target.with_suffix(target.suffix + ".partial")
    req = urllib.request.Request(
        UBUNTU_IMAGE_URL,
        headers={"User-Agent": "hermod-pi image-prep"},
    )
    with urllib.request.urlopen(req, timeout=300) as resp, tmp.open("wb") as out:
        shutil.copyfileobj(resp, out, length=1024 * 1024)
    digest = _sha256_of(tmp)
    if digest != UBUNTU_IMAGE_SHA256:
        tmp.unlink(missing_ok=True)
        raise RuntimeError(
            f"upstream image SHA256 mismatch: expected {UBUNTU_IMAGE_SHA256}, "
            f"got {digest}. refusing to proceed."
        )
    tmp.rename(target)
    LOG.info("upstream image verified: %s", target)
    return target


# --- image build -----------------------------------------------------------


def _decompress_xz(src: Path, dst: Path) -> None:
    # -T0 = parallel threads; -v prints a single-line progress meter to
    # stderr every few seconds so the operator does not see a frozen
    # image-prep container during the 1-3 min decompress.
    LOG.info("decompressing %s -> %s (xz -T0 -v)", src, dst)
    with dst.open("wb") as out:
        _run(
            ["xz", "--decompress", "--verbose", "--stdout", "--threads=0", str(src)],
            stdout=out,
            timeout=1800,
        )


def _compress_xz(src: Path, dst: Path) -> None:
    LOG.info("compressing %s -> %s (xz -T0 -6 -v)", src, dst)
    with dst.open("wb") as out:
        _run(
            ["xz", "--compress", "--verbose", "--stdout", "--threads=0", "-6", str(src)],
            stdout=out,
            timeout=3600,
        )


def _find_fat32_partition_offset(img: Path) -> int:
    """Return byte offset of the first FAT32 partition in the image.

    Uses parted --machine so we don't depend on kernel loopback or any mount.
    """
    cp = _run(
        ["parted", "--machine", "--script", str(img), "unit", "B", "print"],
        capture_output=True,
    )
    for line in cp.stdout.decode().splitlines():
        # parted -m rows: <n>:<start>:<end>:<size>:<fs>:<name>:<flags>;
        parts = line.strip().rstrip(";").split(":")
        if len(parts) >= 5 and parts[0].isdigit() and parts[4].lower() in ("fat32", "fat16"):
            raw = parts[1]
            if not raw.endswith("B"):
                raise RuntimeError(f"unexpected parted offset unit: {raw!r}")
            return int(raw[:-1])
    raise RuntimeError("no FAT32 partition found in image")


def inject_cloud_init(
    img_path: Path,
    user_data: str,
    meta_data: str,
    network_config: str,
) -> None:
    """Write three cloud-init files into the FAT32 system-boot partition.

    Uses mtools (mcopy) against the image file directly — no loopback mount,
    no kernel privileges required. Works cleanly under rootless podman.
    """
    offset = _find_fat32_partition_offset(img_path)
    LOG.info("FAT32 partition offset: %d bytes", offset)

    # mtools address syntax: <image>@@<offset_in_bytes> targets the partition.
    drive_spec = f"{img_path}@@{offset}"

    env = dict(os.environ)
    # Silence mtools warnings about non-standard configuration + suppress the
    # "mtools_skip_check" noise some Ubuntu images trigger.
    env["MTOOLS_SKIP_CHECK"] = "1"

    staging = Path(tempfile.mkdtemp(prefix="hermod-pi-stage-"))
    try:
        ud = staging / "user-data"
        md = staging / "meta-data"
        nc = staging / "network-config"
        ud.write_text(user_data, encoding="utf-8")
        md.write_text(meta_data, encoding="utf-8")
        nc.write_text(network_config, encoding="utf-8")

        # mcopy -o overwrites existing files; -i points at our image partition.
        for src, dst in ((ud, "user-data"), (md, "meta-data"), (nc, "network-config")):
            _run(
                ["mcopy", "-o", "-i", drive_spec, str(src), f"::{dst}"],
                timeout=60,
                env=env,
            )
            LOG.info("mcopy: injected %s", dst)
        os.sync()
    finally:
        for p in staging.iterdir():
            try:
                p.unlink()
            except OSError:
                pass
        try:
            staging.rmdir()
        except OSError:
            pass


# --- main ------------------------------------------------------------------


def _utc_now_iso() -> str:
    return _dt.datetime.now(_dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def _timestamp_slug() -> str:
    return _dt.datetime.now(_dt.timezone.utc).strftime("%Y%m%dT%H%M%SZ")


def _atomic_write_bytes(path: Path, data: bytes) -> None:
    tmp = path.with_name(f".tmp.{path.name}")
    with tmp.open("wb") as fh:
        fh.write(data)
        fh.flush()
        os.fsync(fh.fileno())
    os.rename(tmp, path)


def _atomic_move(src: Path, dst: Path) -> None:
    # src and dst must be on the same filesystem for atomicity.
    tmp = dst.with_name(f".tmp.{dst.name}")
    if src != tmp:
        shutil.move(str(src), str(tmp))
    with tmp.open("rb") as fh:
        os.fsync(fh.fileno())
    os.rename(tmp, dst)


def build(
    config_path: Path,
    pubkey_path: Path,
    output_dir: Path,
    cache_dir: Path,
    tool_version: str,
    template_dir: Path,
    skip_build: bool = False,
) -> dict[str, Any]:
    os.umask(0o077)

    cfg = load_and_validate_config(config_path)
    pubkey_raw = pubkey_path.read_text(encoding="utf-8")
    key_type, pubkey_line = validate_pubkey(pubkey_raw)
    LOG.info("pubkey injected (type: %s)", key_type)

    generated_at = _utc_now_iso()
    user_data = render_user_data(
        cfg, pubkey_line, tool_version, generated_at, template_dir
    )
    network_config = render_network_config(cfg, tool_version, template_dir)
    meta_data = render_meta_data(cfg["hostname"], generated_at)

    # Sanity: make sure Jinja really resolved everything.
    for rendered in (user_data, network_config, meta_data):
        if "{{" in rendered or "{%" in rendered:
            raise RuntimeError("Jinja template left unresolved tokens")

    output_dir.mkdir(parents=True, exist_ok=True)

    slug = _timestamp_slug()
    final_name = f"hermod-pi-{cfg['hostname']}-{slug}.img.xz"
    final_path = output_dir / final_name
    manifest_path = output_dir / f"hermod-pi-{cfg['hostname']}-{slug}.manifest.json"

    if skip_build:
        LOG.warning("HERMOD_PI_SKIP_BUILD set — writing rendered artefacts only")
        (output_dir / "user-data.preview.yaml").write_text(user_data, encoding="utf-8")
        (output_dir / "network-config.preview.yaml").write_text(network_config, encoding="utf-8")
        (output_dir / "meta-data.preview").write_text(meta_data, encoding="utf-8")
        return {
            "skipped_build": True,
            "hostname": cfg["hostname"],
            "output_dir": str(output_dir),
        }

    upstream_xz = ensure_upstream_image(cache_dir)

    with tempfile.TemporaryDirectory(prefix="hermod-pi-work-") as work_s:
        work = Path(work_s)
        raw_img = work / f"hermod-pi-{cfg['hostname']}-{slug}.img"
        _decompress_xz(upstream_xz, raw_img)
        inject_cloud_init(raw_img, user_data, meta_data, network_config)

        staged_xz = work / final_name
        _compress_xz(raw_img, staged_xz)

        # Atomic rename into /output.
        tmp_final = output_dir / f".tmp.{final_name}"
        shutil.move(str(staged_xz), str(tmp_final))
        with tmp_final.open("rb") as fh:
            os.fsync(fh.fileno())
        os.rename(tmp_final, final_path)

    size = final_path.stat().st_size
    digest = _sha256_of(final_path)
    manifest = {
        "schema": "hermod-pi/image-manifest/v1",
        "artifact": final_name,
        "sha256": digest,
        "size": size,
        "hostname": cfg["hostname"],
        "username": cfg["username"],
        "timezone": cfg["timezone"],
        "generated_at": generated_at,
        "tool_version": tool_version,
        "ubuntu_version": UBUNTU_VERSION,
        "source_image_url": UBUNTU_IMAGE_URL,
        "source_image_sha256": UBUNTU_IMAGE_SHA256,
        "wifi_configured": bool(cfg.get("wifi")),
    }
    _atomic_write_bytes(
        manifest_path,
        (json.dumps(manifest, indent=2, sort_keys=True) + "\n").encode("utf-8"),
    )
    LOG.info("wrote %s (%d bytes, sha256 %s)", final_path, size, digest)
    LOG.info("wrote manifest %s", manifest_path)
    return manifest


def _parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    p = argparse.ArgumentParser(prog="hermod-pi image-prep")
    p.add_argument("--config", type=Path, default=Path("/config.yaml"))
    p.add_argument("--pubkey", type=Path, default=Path("/pubkey.pub"))
    p.add_argument(
        "--output-dir",
        type=Path,
        default=Path(os.environ.get("HERMOD_PI_OUTPUT", "/output")),
    )
    p.add_argument(
        "--cache-dir",
        type=Path,
        default=Path(os.environ.get("HERMOD_PI_CACHE", "/cache")),
    )
    p.add_argument(
        "--template-dir",
        type=Path,
        default=Path(__file__).parent / "templates",
    )
    p.add_argument("--verbose", action="store_true")
    return p.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = _parse_args(argv)
    if args.verbose:
        LOG.setLevel(logging.DEBUG)
    tool_version = os.environ.get("HERMOD_PI_VERSION", "0.0.0-dev")
    skip_build = os.environ.get("HERMOD_PI_SKIP_BUILD") == "1"
    try:
        build(
            config_path=args.config,
            pubkey_path=args.pubkey,
            output_dir=args.output_dir,
            cache_dir=args.cache_dir,
            tool_version=tool_version,
            template_dir=args.template_dir,
            skip_build=skip_build,
        )
    except ValidationError as e:
        LOG.error("config validation failed: %s", e)
        return 2
    except subprocess.CalledProcessError as e:
        LOG.error("external command failed: %s (rc=%s)", e.cmd, e.returncode)
        return 3
    except subprocess.TimeoutExpired as e:
        LOG.error("external command timed out: %s", e.cmd)
        return 4
    except RuntimeError as e:
        LOG.error("%s", e)
        return 5
    except Exception as e:  # last-chance catchall
        LOG.exception("unexpected failure: %s", e)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
