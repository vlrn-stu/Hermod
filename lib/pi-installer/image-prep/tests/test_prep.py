"""Tests for hermod-pi image-prep.

Unit tests mock out subprocess / network; the docker integration test is
skipped automatically if docker isn't available on the host.

Run:
    pytest tests/ -v
"""

from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
import textwrap
from pathlib import Path
from unittest import mock

import pytest
import yaml


HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
sys.path.insert(0, str(ROOT))

import prep  # noqa: E402  -- sys.path injection above


VALID_ED25519 = (
    "ssh-ed25519 "
    "AAAAC3NzaC1lZDI1NTE5AAAAIEJkN8Q3nPQzR4Wv7yFvC2xXhL9sF3kR1nJqYx8yP6tO "
    "ci-test@hermod"
)
VALID_RSA = (
    "ssh-rsa "
    + "A" * 372  # ~372 chars of base64 roughly matches an RSA-3072 key
    + " ci-test@hermod"
)


# ---------- hostname ----------


@pytest.mark.parametrize(
    "name",
    ["pi", "hermod-01", "pi5-lab-a", "a", "a" * 63, "hermod-thesis-node-42"],
)
def test_hostname_validation_valid(name: str) -> None:
    assert prep.validate_hostname(name) == name


@pytest.mark.parametrize(
    "name",
    [
        "Pi",               # uppercase
        "1pi",              # leading digit
        "pi_01",            # underscore
        "pi.local",         # dot
        "-pi",              # leading hyphen
        "",                 # empty
        "a" * 64,           # too long
        "pi 01",            # space
        "pi$",              # special char
        "$(rm -rf /)",      # shell metachar injection attempt
        "pi;reboot",        # shell sep
        "pi`whoami`",       # backtick
    ],
)
def test_hostname_validation_invalid(name: str) -> None:
    with pytest.raises(prep.ValidationError):
        prep.validate_hostname(name)


# ---------- pubkey ----------


def test_pubkey_validation_ed25519() -> None:
    key_type, line = prep.validate_pubkey(VALID_ED25519)
    assert key_type == "ssh-ed25519"
    assert line == VALID_ED25519


def test_pubkey_validation_rsa() -> None:
    key_type, _ = prep.validate_pubkey(VALID_RSA)
    assert key_type == "ssh-rsa"


def test_pubkey_validation_ecdsa() -> None:
    k = "ecdsa-sha2-nistp256 " + "A" * 200 + " u@h"
    kt, _ = prep.validate_pubkey(k)
    assert kt == "ecdsa-sha2-nistp256"


@pytest.mark.parametrize(
    "bad",
    [
        "",
        "# just a comment",
        "not-a-key",
        "ssh-dss AAAAB3Nza... evil@host",   # dss intentionally rejected
        "ssh-ed25519",                      # no body
        "ssh-ed25519 short u@h",            # body too short
        "ssh-rsa\nmultiline junk",          # note: single-line parser takes first
    ],
)
def test_pubkey_validation_reject(bad: str) -> None:
    # "multiline junk" case: first line 'ssh-rsa' alone has no body -> reject.
    with pytest.raises(prep.ValidationError):
        prep.validate_pubkey(bad)


def test_pubkey_no_comment_warns(caplog: pytest.LogCaptureFixture) -> None:
    no_comment = "ssh-ed25519 " + "A" * 60
    with caplog.at_level("WARNING"):
        kt, _ = prep.validate_pubkey(no_comment)
    assert kt == "ssh-ed25519"
    assert any("no comment" in r.message for r in caplog.records)


def test_pubkey_never_logged(caplog: pytest.LogCaptureFixture) -> None:
    """The raw key body must not end up in any log record."""
    with caplog.at_level("DEBUG"):
        prep.validate_pubkey(VALID_ED25519)
    body = VALID_ED25519.split()[1]
    for rec in caplog.records:
        assert body not in rec.getMessage()


# ---------- timezone ----------


def test_timezone_valid() -> None:
    assert prep.validate_timezone("UTC") == "UTC"
    assert prep.validate_timezone("Europe/Berlin") == "Europe/Berlin"


def test_timezone_invalid() -> None:
    with pytest.raises(prep.ValidationError):
        prep.validate_timezone("Mars/Olympus_Mons")
    with pytest.raises(prep.ValidationError):
        prep.validate_timezone("utc")  # case matters


# ---------- wifi ----------


@pytest.mark.parametrize("cc", ["US", "DE", "GB", "NL", "JP"])
def test_wifi_country_valid(cc: str) -> None:
    assert prep.validate_wifi_country(cc) == cc


@pytest.mark.parametrize("cc", ["us", "USA", "", "U1", "D", "DEU"])
def test_wifi_country_invalid(cc: str) -> None:
    with pytest.raises(prep.ValidationError):
        prep.validate_wifi_country(cc)


# ---------- shell injection ----------


def test_no_shell_injection_hostname() -> None:
    for attempt in ["$(rm -rf /)", "pi;rm -rf /", "pi`id`", "pi&&reboot", "pi|nc 1.2.3.4 9"]:
        with pytest.raises(prep.ValidationError):
            prep.validate_hostname(attempt)


def test_no_shell_injection_ssid() -> None:
    # quotes/backslash/control chars must be rejected so template cannot break out.
    for attempt in ['ev"il', "back\\slash", "nul\x00byte", "new\nline"]:
        with pytest.raises(prep.ValidationError):
            prep.validate_wifi_ssid(attempt)


# ---------- config load ----------


def _write(tmp: Path, name: str, body: str) -> Path:
    p = tmp / name
    p.write_text(textwrap.dedent(body), encoding="utf-8")
    return p


def test_config_minimal(tmp_path: Path) -> None:
    cfg_file = _write(tmp_path, "config.yaml", "hostname: pi-test\n")
    cfg = prep.load_and_validate_config(cfg_file)
    assert cfg["hostname"] == "pi-test"
    assert cfg["timezone"] == "UTC"
    assert cfg["username"] == "hermod"
    assert cfg["sudo_nopasswd"] is True
    assert cfg["runcmd"] == []
    assert "wifi" not in cfg or cfg["wifi"] in (None, {})


def test_config_rejects_unknown_field(tmp_path: Path) -> None:
    cfg_file = _write(
        tmp_path,
        "config.yaml",
        """
        hostname: pi-test
        unknown_field: boom
        """,
    )
    with pytest.raises(Exception):  # jsonschema.ValidationError
        prep.load_and_validate_config(cfg_file)


def test_config_wifi_roundtrip(tmp_path: Path) -> None:
    cfg_file = _write(
        tmp_path,
        "config.yaml",
        """
        hostname: pi-wifi
        timezone: Europe/Berlin
        wifi:
          ssid: HermodLab
          passphrase: correcthorsebattery
          country: DE
        """,
    )
    cfg = prep.load_and_validate_config(cfg_file)
    assert cfg["wifi"]["country"] == "DE"
    assert cfg["wifi"]["ssid"] == "HermodLab"


# ---------- templates ----------


@pytest.fixture()
def template_dir() -> Path:
    return ROOT / "templates"


def test_template_rendering_user_data(template_dir: Path) -> None:
    cfg = {
        "hostname": "pi-a",
        "username": "hermod",
        "timezone": "Europe/Berlin",
        "locale": "en_US.UTF-8",
        "sudo_nopasswd": True,
        "runcmd": ['echo "hello from hermod"'],
    }
    out = prep.render_user_data(
        cfg=cfg,
        ssh_pubkey_line=VALID_ED25519,
        tool_version="1.2.3",
        generated_at="2026-04-19T00:00:00Z",
        template_dir=template_dir,
    )
    # Jinja didn't leak
    assert "{{" not in out and "{%" not in out
    # Key fields present
    assert out.startswith("#cloud-config")
    assert "hostname: pi-a" in out
    assert "- name: hermod" in out
    assert "timezone: Europe/Berlin" in out
    assert "ssh_pwauth: false" in out
    assert "disable_root: true" in out
    assert "rm -f /etc/ssh/ssh_host_*" in out
    assert "dpkg-reconfigure" in out
    assert VALID_ED25519 in out
    assert "/etc/hermod-pi/provisioned.json" in out
    # extra runcmd propagated
    assert 'echo "hello from hermod"' in out
    # valid YAML (cloud-config is YAML with a comment header)
    parsed = yaml.safe_load(out)
    assert parsed["hostname"] == "pi-a"
    assert parsed["ssh_pwauth"] is False
    assert parsed["users"][0]["name"] == "hermod"


def test_template_user_data_no_sudo_nopasswd(template_dir: Path) -> None:
    cfg = {
        "hostname": "pi-b",
        "username": "ubuntu",
        "timezone": "UTC",
        "locale": "en_US.UTF-8",
        "sudo_nopasswd": False,
        "runcmd": [],
    }
    out = prep.render_user_data(
        cfg, VALID_ED25519, "1.0.0", "2026-04-19T00:00:00Z", template_dir
    )
    assert "NOPASSWD" not in out
    assert 'sudo: ["ALL=(ALL) ALL"]' in out


def test_network_config_eth_only(template_dir: Path) -> None:
    out = prep.render_network_config({"hostname": "x"}, "1.0.0", template_dir)
    assert "{{" not in out and "{%" not in out
    parsed = yaml.safe_load(out)
    assert parsed["version"] == 2
    assert parsed["ethernets"]["eth0"]["dhcp4"] is True
    assert "wifis" not in parsed


def test_network_config_wifi(template_dir: Path) -> None:
    cfg = {
        "wifi": {
            "ssid": "HermodLab",
            "passphrase": "correcthorsebattery",
            "country": "DE",
        }
    }
    out = prep.render_network_config(cfg, "1.0.0", template_dir)
    parsed = yaml.safe_load(out)
    assert parsed["wifis"]["wlan0"]["regulatory-domain"] == "DE"
    assert parsed["wifis"]["wlan0"]["access-points"]["HermodLab"]["password"] == "correcthorsebattery"


def test_meta_data_format() -> None:
    md = prep.render_meta_data("pi-a", "2026-04-19T12:34:56Z")
    assert "instance-id: pi-a-" in md
    assert "local-hostname: pi-a" in md


# ---------- atomic write ----------


def test_atomic_write_bytes(tmp_path: Path) -> None:
    target = tmp_path / "foo.json"
    prep._atomic_write_bytes(target, b'{"k":1}')
    assert target.read_bytes() == b'{"k":1}'
    # .tmp.foo.json should not linger
    assert not (tmp_path / ".tmp.foo.json").exists()


# ---------- subprocess safety ----------


def test_run_uses_list_argv() -> None:
    with mock.patch("prep.subprocess.run") as m:
        m.return_value = subprocess.CompletedProcess(args=["true"], returncode=0)
        prep._run(["true", "--flag"])
        kw = m.call_args.kwargs
        assert kw["shell"] is False
        assert kw["check"] is True
        assert "timeout" in kw


# ---------- build smoke (skip_build mode) ----------


def test_build_skip_build_writes_previews(tmp_path: Path, template_dir: Path) -> None:
    cfg_file = _write(
        tmp_path,
        "config.yaml",
        """
        hostname: pi-smoke
        timezone: UTC
        wifi:
          ssid: Lab
          passphrase: correcthorsebattery
          country: US
        """,
    )
    pubkey_file = tmp_path / "key.pub"
    pubkey_file.write_text(VALID_ED25519 + "\n")

    out = tmp_path / "out"
    cache = tmp_path / "cache"

    manifest = prep.build(
        config_path=cfg_file,
        pubkey_path=pubkey_file,
        output_dir=out,
        cache_dir=cache,
        tool_version="9.9.9-test",
        template_dir=template_dir,
        skip_build=True,
    )
    assert manifest["skipped_build"] is True
    assert (out / "user-data.preview.yaml").exists()
    assert (out / "network-config.preview.yaml").exists()
    assert (out / "meta-data.preview").exists()
    ud = (out / "user-data.preview.yaml").read_text()
    assert "pi-smoke" in ud
    nc = (out / "network-config.preview.yaml").read_text()
    assert "Lab" in nc


# ---------- docker integration (optional) ----------


_DOCKER_AVAILABLE = shutil.which("docker") is not None


@pytest.mark.skipif(not _DOCKER_AVAILABLE, reason="docker not present on host")
def test_docker_build_and_skip_build_run(tmp_path: Path) -> None:
    """Build the container and run it with HERMOD_PI_SKIP_BUILD=1.

    This avoids needing privileged mode / loopback mounting in CI while still
    proving the image assembles and validates input end-to-end.
    """
    image_tag = "hermod/image-prep:test"
    ctx = ROOT
    subprocess.run(
        ["docker", "build", "-t", image_tag, str(ctx)],
        check=True,
        timeout=600,
    )

    cfg_file = tmp_path / "config.yaml"
    cfg_file.write_text("hostname: pi-ci\n")
    pub_file = tmp_path / "pubkey.pub"
    pub_file.write_text(VALID_ED25519 + "\n")
    out = tmp_path / "output"
    out.mkdir()

    subprocess.run(
        [
            "docker", "run", "--rm",
            "-e", "HERMOD_PI_VERSION=ci-test",
            "-e", "HERMOD_PI_SKIP_BUILD=1",
            "-v", f"{out}:/output",
            "-v", f"{cfg_file}:/config.yaml:ro",
            "-v", f"{pub_file}:/pubkey.pub:ro",
            image_tag,
        ],
        check=True,
        timeout=300,
    )
    assert (out / "user-data.preview.yaml").exists()
    preview = (out / "user-data.preview.yaml").read_text()
    assert "pi-ci" in preview


if __name__ == "__main__":
    raise SystemExit(pytest.main([__file__, "-v"]))
