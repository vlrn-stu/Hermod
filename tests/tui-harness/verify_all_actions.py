#!/usr/bin/env python3
"""
Drive every TUI action and assert the modal/popup paints expected
text. Each test sends a short keystroke sequence then SIGKILLs the
TUI so the assertions run on raw bytes accumulated during settle.
"""
from __future__ import annotations
import os, pty, select, signal, sys, time

REPO_ROOT = "/mnt/school/Thesis/Hermod"
COLS, ROWS = 100, 30


def drive(keys: list[bytes], settle_ms: int = 1500) -> str:
    pid, fd = pty.fork()
    if pid == 0:
        os.environ["LINES"] = str(ROWS)
        os.environ["COLUMNS"] = str(COLS)
        os.environ["TERM"] = "xterm-256color"
        os.chdir(REPO_ROOT)
        os.execvp("bash", ["bash", "-c", f"stty rows {ROWS} cols {COLS}; ./hermod.sh tui"])

    buf = b""

    def drain(window: float) -> None:
        nonlocal buf
        deadline = time.time() + window
        while time.time() < deadline:
            r, _, _ = select.select([fd], [], [], 0.1)
            if not r:
                continue
            try:
                chunk = os.read(fd, 8192)
            except OSError:
                break
            if not chunk:
                break
            buf += chunk

    time.sleep(1.5)
    drain(0.5)

    for k in keys:
        os.write(fd, k)
        time.sleep(settle_ms / 1000)
        drain(0.4)

    drain(1.0)

    try:
        os.kill(pid, signal.SIGKILL)
    except ProcessLookupError:
        pass

    return buf.decode("utf-8", errors="replace")


def check(label: str, keys: list[bytes], must_have: list[str], settle_ms: int = 1500) -> bool:
    out = drive(keys, settle_ms)
    missing = [n for n in must_have if n not in out]
    status = "PASS" if not missing else "FAIL"
    print(f"  [{status}] {label}: missing={missing if missing else 'none'}")
    return not missing


def main() -> int:
    fails = 0
    print("=== Compose ===")
    for label, keys, expect in [
        ("[1] up confirm",       [b"c", b"1"], ["compose stack up"]),
        ("[2] down confirm",     [b"c", b"2"], ["Stop compose"]),
        ("[3] restart svc field",[b"c", b"3"], ["Service to restart"]),
        ("[4] logs svc field",   [b"c", b"4"], ["Service to tail logs"]),
        ("[5] creds popup",      [b"c", b"5"], ["Vault42 seed users", "viewer@hermod.local"]),
        ("[6] status popup",     [b"c", b"6"], ["Compose status"]),
        ("[7] build svc field",  [b"c", b"7"], ["Service to rebuild"]),
        ("[8] pull confirm",     [b"c", b"8"], ["fresh images"]),
        ("[R] reset confirm",    [b"c", b"R"], ["wipes ALL volumes"]),
    ]:
        if not check(label, keys, expect): fails += 1

    print("\n=== Provisioning ===")
    for label, keys, expect in [
        ("[1] flash config field", [b"g", b"1"], ["Path to Pi config", "default:"]),
        ("[2] wait-pi hostname",   [b"g", b"2"], ["Pi hostname", "must match"]),
        ("[3] provision config",   [b"g", b"3"], ["Path to Pi config", "FULL bring-up"]),
        ("[4] pi-keys popup",      [b"g", b"4"], ["Pi keypairs"]),
        ("[5] pi-status hostname", [b"g", b"5"], ["Pi hostname for status", "mDNS"]),
        ("[6] pi-doctor popup",    [b"g", b"6"], ["Pi installer doctor"]),
        ("[U] uninstall hostname", [b"g", b"U"], ["UNINSTALL", "DESTRUCTIVE"]),
    ]:
        if not check(label, keys, expect): fails += 1

    print("\n=== Production ===")
    for entry in [
        ("[1] install confirm", [b"p", b"1"], ["Install Hermod stack"], 1500),
        ("[2] update confirm",  [b"p", b"2"], ["rsync + apply overlay"], 1500),
        ("[3] status popup",    [b"p", b"3"], ["Status: prod-pi"], 3500),
        ("[4] kick deploy field",[b"p", b"4"], ["Deployment to kick", "roll-restart EVERY"], 1500),
        ("[5] roll-jwks confirm",[b"p", b"5"], ["Roll Vault42 JWKS"], 1500),
        ("[6] logs ask pod (drill candidate)",[b"p", b"6"], ["selected pod"], 3000),
        ("[7] ensure-secrets confirm",[b"p", b"7"], ["ensure-secrets"], 1500),
        ("[T] target cycle",    [b"p", b"T"], ["target → prod-kind"], 1500),
        ("[t] teardown confirm",[b"p", b"t"], ["Teardown prod-pi"], 1500),
        ("[D] redeploy confirm",[b"p", b"D"], ["Redeploy prod-pi"], 1500),
        ("[P] reset-db confirm",[b"p", b"P"], ["Drop vault + hermod"], 1500),
        ("[R] reset confirm",   [b"p", b"R"], ["RESET prod-pi"], 1500),
        ("[j] pod down highlight",[b"p", b"j", b"j"], ["▶"], 2500),
    ]:
        label, keys, expect, settle = entry
        if not check(label, keys, expect, settle): fails += 1

    print("\n=== Vault ===")
    for label, keys, expect in [
        ("section render plaintext", [b"v"], ["Vault — operator config encryption", "encrypt"]),
        ("[1] encrypt → PIN modal",  [b"v", b"1"], ["Set Vault PIN", "Enter = submit"]),
        ("[5] status popup",         [b"v", b"5"], ["Vault status"]),
    ]:
        if not check(label, keys, expect): fails += 1

    print("\n=== Users ===")
    for label, keys, expect in [
        ("section",                  [b"u"],          ["Vault42 users"]),
        ("[1] seed-users confirm",   [b"u", b"1"],    ["Reseed Vault42"]),
        ("[P] change-password ask",  [b"u", b"P"],    ["SAME operator-supplied"]),
    ]:
        if not check(label, keys, expect): fails += 1

    print("\n=== Network ===")
    for entry in [
        ("[1] cert-status popup",    [b"n", b"1"],    ["Cert status"]),
        ("[2] cert-request hostname",[b"n", b"2"],    ["Public hostname for the cert"]),
        ("[3] tunnel-secret yes/no", [b"n", b"3"],    ["Read TUNNEL_TOKEN"]),
        ("[4] dns-secret yes/no",    [b"n", b"4"],    ["Read CF DNS"]),
        ("[5] cert-show name field", [b"n", b"5"],    ["Certificate name to show", "hermod-public-tls"]),
        ("[R] rotate-certs confirm", [b"n", b"R"],    ["Rotate all internal mTLS"]),
    ]:
        label, keys, expect = entry
        settle = 3000 if "popup" in label else 1500
        if not check(label, keys, expect, settle): fails += 1

    print("\n=== Settings ===")
    for label, keys, expect in [
        ("[2] ensure-secrets confirm",[b"s", b"2"],   ["Re-run ensure-secrets"]),
        ("[3] config-show popup",     [b"s", b"3"],   ["Operator config", "HERMOD_PI_SSH_HOST"]),
        ("[4] image-source picker",   [b"s", b"4"],   ["Image source", "ghcr.io", "local builds"]),
        ("[5] protocol picker",       [b"s", b"5"],   ["Translator to toggle", "lora", "zigbee"]),
        ("[6] limiter picker",        [b"s", b"6"],   ["Rate-limit knob", "show current"]),
    ]:
        if not check(label, keys, expect): fails += 1

    print("\n=== Diagnostics ===")
    for entry in [
        ("[1] doctor popup",   [b"d", b"1"], ["Doctor"]),
        ("[2] metrics popup",  [b"d", b"2"], ["Metrics"]),
        ("[3] logs pod field", [b"d", b"3"], ["Pod name"]),
        ("[4] health popup",   [b"d", b"4"], ["Health"]),
        ("[5] pi-doctor popup",[b"d", b"5"], ["Pi doctor"]),
        ("[6] cleanup confirm",[b"d", b"6"], ["cluster cleanup"]),
    ]:
        label, keys, expect = entry
        settle = 3000 if "popup" in label else 1500
        if not check(label, keys, expect, settle): fails += 1

    print(f"\nfails: {fails}")
    return 1 if fails else 0


if __name__ == "__main__":
    sys.exit(main())
