#!/usr/bin/env python3
"""
TUI test harness — spawn `hermod.sh tui` in a 30x100 pty, send keystroke
sequences, capture output through a pyte virtual terminal, dump the
final screen as clean text + assert markers.

Usage:
  drive.py keys c v d q                 # press c, v, d, q in order
  drive.py keys c v d q --assert 'Vault' --assert 'Diagnostics'
  drive.py screenshot                   # boot, render, dump screen, exit
  drive.py keys c 1 q --raw             # legacy ANSI byte stream

Named keys: ESC TAB ENTER UP DOWN LEFT RIGHT SHIFT-TAB BACKSPACE
            PGUP PGDN HOME END SPACE F1..F12

The TUI is keyboard-driven; every assertion mirrors a real operator
keypress. Screen dumps go through pyte so layout is preserved and
assertions can match on what the operator actually sees.
"""
from __future__ import annotations
import argparse, os, pty, select, signal, sys, time

try:
    import pyte
    _HAVE_PYTE = True
except ImportError:
    _HAVE_PYTE = False

REPO_ROOT = "/mnt/school/Thesis/Hermod"
DEFAULT_ROWS = 30
DEFAULT_COLS = 100


def spawn_tui(cols: int, rows: int) -> tuple[int, int]:
    pid, fd = pty.fork()
    if pid == 0:
        os.environ["LINES"] = str(rows)
        os.environ["COLUMNS"] = str(cols)
        os.environ.setdefault("TERM", "xterm-256color")
        os.chdir(REPO_ROOT)
        os.execvp("bash", [
            "bash", "-c",
            f"stty rows {rows} cols {cols}; ./hermod.sh tui"
        ])
    return pid, fd


def drain(fd: int, deadline: float) -> bytes:
    buf = b""
    while time.time() < deadline:
        r, _, _ = select.select([fd], [], [], 0.2)
        if not r:
            continue
        try:
            chunk = os.read(fd, 8192)
        except OSError:
            break
        if not chunk:
            break
        buf += chunk
    return buf


_NAMED_KEYS = {
    "ESC": b"\x1b", "TAB": b"\t", "ENTER": b"\r", "BACKSPACE": b"\x7f",
    "UP": b"\x1b[A", "DOWN": b"\x1b[B",
    "RIGHT": b"\x1b[C", "LEFT": b"\x1b[D",
    "SHIFT-TAB": b"\x1b[Z",
    "PGUP": b"\x1b[5~", "PGDN": b"\x1b[6~",
    "HOME": b"\x1b[H", "END": b"\x1b[F",
    "SPACE": b" ",
    "F1": b"\x1bOP", "F2": b"\x1bOQ", "F3": b"\x1bOR", "F4": b"\x1bOS",
}


def send(fd: int, key: str) -> None:
    """Translate a friendly key name → bytes, single char → byte, or
    multi-char ASCII → typed as if the operator banged it out at the
    keyboard. Useful for filter/palette inputs."""
    if key in _NAMED_KEYS:
        os.write(fd, _NAMED_KEYS[key])
        return
    if len(key) == 1:
        os.write(fd, key.encode())
        return
    # Multi-char ASCII: type each char with a tiny gap so the TUI's
    # canonical-mode line edit can keep up.
    if all(0x20 <= ord(c) < 0x7f for c in key):
        for c in key:
            os.write(fd, c.encode())
            time.sleep(0.005)
        return
    raise ValueError(f"unknown key: {key!r} (use single chars, named keys, or printable ASCII strings)")


class VirtualTerm:
    """Wraps pyte to give us a clean 2D char grid view of the TUI."""

    def __init__(self, cols: int, rows: int) -> None:
        if not _HAVE_PYTE:
            raise RuntimeError("pyte not installed — pip install --user pyte")
        self.screen = pyte.Screen(cols, rows)
        self.stream = pyte.Stream(self.screen)
        self.cols = cols
        self.rows = rows

    def feed(self, data: bytes) -> None:
        self.stream.feed(data.decode("utf-8", errors="replace"))

    def dump(self) -> str:
        """Return the screen as plain text, one row per line, trailing
        whitespace trimmed per row."""
        return "\n".join(line.rstrip() for line in self.screen.display)


def run_keys(keys: list[str], asserts: list[str] | None,
             cols: int, rows: int, settle_ms: int,
             raw_mode: bool) -> int:
    pid, fd = spawn_tui(cols, rows)
    vt = None if raw_mode else VirtualTerm(cols, rows)
    raw = b""

    def absorb(blob: bytes) -> None:
        nonlocal raw
        raw += blob
        if vt is not None:
            vt.feed(blob)

    absorb(drain(fd, time.time() + 1.5))   # initial render settle
    drain_window = max(settle_ms / 1000, 0.4)
    for k in keys:
        send(fd, k)
        time.sleep(settle_ms / 1000)
        absorb(drain(fd, time.time() + drain_window))

    if "q" not in keys:
        send(fd, "q")
        absorb(drain(fd, time.time() + 0.5))

    try:
        os.kill(pid, signal.SIGKILL)
    except ProcessLookupError:
        pass

    if vt is not None:
        screen = vt.dump()
        print(screen)
    else:
        text = raw.decode("utf-8", errors="replace")
        print(text[-3000:], end="")
    print()

    haystack = vt.dump() if vt else raw.decode("utf-8", errors="replace")
    rc = 0
    for needle in (asserts or []):
        hits = haystack.count(needle)
        marker = "PASS" if hits else "FAIL"
        print(f"[{marker}] '{needle}' → {hits} hit(s)", file=sys.stderr)
        if not hits:
            rc = 1
    return rc


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("mode", choices=["keys", "screenshot"])
    ap.add_argument("keys", nargs="*",
                    help="keystroke sequence (single chars or named: ESC, TAB, UP, DOWN, ENTER, SHIFT-TAB, PGUP, PGDN, F1..F4)")
    ap.add_argument("--assert", dest="asserts", action="append", default=[],
                    help="substring that must appear in captured output (repeatable)")
    ap.add_argument("--cols", type=int, default=DEFAULT_COLS)
    ap.add_argument("--rows", type=int, default=DEFAULT_ROWS)
    ap.add_argument("--settle-ms", type=int, default=300,
                    help="ms to wait between keystrokes for the TUI to redraw")
    ap.add_argument("--raw", action="store_true",
                    help="dump raw ANSI byte stream instead of pyte-rendered screen")
    args = ap.parse_args()
    if args.raw and not _HAVE_PYTE:
        pass  # raw was the only mode anyway
    elif not _HAVE_PYTE:
        print("warn: pyte not installed; falling back to raw mode", file=sys.stderr)
        args.raw = True
    if args.mode == "screenshot":
        return run_keys([], args.asserts, args.cols, args.rows, args.settle_ms, args.raw)
    return run_keys(args.keys, args.asserts, args.cols, args.rows, args.settle_ms, args.raw)


if __name__ == "__main__":
    sys.exit(main())
