#!/usr/bin/env bash
# -----------------------------------------------------------------------------
# device_guard.sh — Hermod Pi installer: target-device safety library
# -----------------------------------------------------------------------------
# Sourceable library that protects the user from accidentally flashing an
# image to the wrong block device (e.g. their primary NVMe). Designed for
# the hermod-pi CLI on Linux, macOS, and WSL.
#
# USAGE (as a library):
#   source lib/device_guard.sh
#   device_guard::list_candidates
#   device_guard::check /dev/sda || handle_error $?
#   device_guard::confirm /dev/sda
#
# USAGE (as a self-test):
#   bash device_guard.sh test
#
# RETURN CODES (device_guard::check):
#   0  device is safe to flash
#   1  device exceeds the 256 GiB size guard (bypassable, see below)
#   2  device is not removable (HARD NO — never bypassable)
#   3  device path not found / not a block device
#   4  device has a mounted system partition (/, /boot, /home, etc.)
#   5  platform unsupported (unknown uname or Git Bash on Windows)
#   6  required dependency missing (e.g. jq)
#   7  device is an active encrypted system disk (LUKS/APFS root)
#
# OVERRIDE:
#   HERMOD_PI_DISABLE_SIZE_GUARD=i-understand-the-risks bypasses the size
#   check after a 10s countdown on stderr. The removable, mount and
#   encryption checks are NEVER bypassable. Every override is logged to
#   ~/.hermod-pi/audit.log.
#
# PLATFORM SUPPORT:
#   - Linux (lsblk + jq)
#   - macOS (diskutil + plutil + jq; requires bash 4+, so `brew install bash`
#            on stock macOS because /bin/bash is 3.2)
#   - WSL   (detected via /proc/sys/kernel/osrelease; treated as Linux)
#   - Git Bash / MINGW / Cygwin → refused with a clear message; use the
#     PowerShell sibling script instead.
#
# DEPENDENCIES:
#   bash 4.0+, coreutils, lsblk (Linux) or diskutil+plutil (macOS), jq.
#
# AUTHORS:
#   Hermod thesis project, 2026.
# -----------------------------------------------------------------------------

# Enable strict mode only if the caller hasn't already — avoid clobbering
# options the parent script cares about.
if [[ -z "${__DEVICE_GUARD_STRICT_APPLIED:-}" ]]; then
  set -o errexit
  set -o nounset
  set -o pipefail
  __DEVICE_GUARD_STRICT_APPLIED=1
fi

# Fail fast on ancient bash (macOS default /bin/bash is 3.2).
if (( BASH_VERSINFO[0] < 4 )); then
  # shellcheck disable=SC2016 # intentional literal backticks in the error message
  printf 'device_guard.sh: bash 4.0+ required (found %s). On macOS run `brew install bash`.\n' \
    "${BASH_VERSION}" >&2
  # shellcheck disable=SC2317 # exit is fallback when not sourced
  return 1 2>/dev/null || exit 1
fi

# -----------------------------------------------------------------------------
# Constants
# -----------------------------------------------------------------------------

# 256 GiB — the hard size fence. Larger devices likely aren't SD cards.
readonly DEVICE_GUARD_SIZE_LIMIT_BYTES=274877906944
readonly DEVICE_GUARD_OVERRIDE_TOKEN='i-understand-the-risks'
readonly DEVICE_GUARD_AUDIT_LOG="${HOME:-/tmp}/.hermod-pi/audit.log"

# Mount points that indicate an in-use system partition. /media/* and
# /run/media/* are excluded because those are auto-mount locations for
# removable media and are expected.
readonly DEVICE_GUARD_SYSTEM_MOUNTS=(
  '/' '/boot' '/boot/efi' '/home' '/var' '/usr' '/opt' '/srv'
)

# -----------------------------------------------------------------------------
# Internal helpers
# -----------------------------------------------------------------------------

# Print to stderr with a library prefix so main CLI logs are distinguishable.
device_guard::_err() {
  printf 'device_guard: %s\n' "$*" >&2
}

# Detect which platform we're on. Echoes one of: linux | macos | wsl | windows | unknown
device_guard::_platform() {
  local uname_s
  uname_s="$(uname -s 2>/dev/null || echo unknown)"
  case "${uname_s}" in
    Linux)
      if [[ -r /proc/sys/kernel/osrelease ]] && \
         grep -qiE 'microsoft|wsl' /proc/sys/kernel/osrelease 2>/dev/null; then
        echo wsl
      else
        echo linux
      fi
      ;;
    Darwin)             echo macos ;;
    MINGW*|CYGWIN*|MSYS*) echo windows ;;
    *)                  echo unknown ;;
  esac
}

# Verify jq is available; return 6 if not.
device_guard::_require_jq() {
  if ! command -v jq >/dev/null 2>&1; then
    device_guard::_err 'jq is required but not installed. Please install jq.'
    return 6
  fi
  return 0
}

# Log an override event. Never fails the caller — audit is best-effort.
device_guard::_audit() {
  local event="$1" dev="$2" size="$3"
  local dir
  dir="$(dirname -- "${DEVICE_GUARD_AUDIT_LOG}")"
  mkdir -p -- "${dir}" 2>/dev/null || return 0
  printf '%s\t%s\t%s\t%s\n' \
    "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
    "${event}" \
    "${dev}" \
    "${size}" \
    >> "${DEVICE_GUARD_AUDIT_LOG}" 2>/dev/null || true
}

# Pretty-print a byte count as GiB with one decimal.
device_guard::_bytes_to_gib() {
  local bytes="$1"
  # Guard against non-numeric input.
  if [[ ! "${bytes}" =~ ^[0-9]+$ ]]; then
    echo '?'
    return
  fi
  # Integer math: GiB × 10 then split, avoids requiring bc.
  local tenths=$(( bytes * 10 / 1073741824 ))
  printf '%d.%d GiB' "$(( tenths / 10 ))" "$(( tenths % 10 ))"
}

# -----------------------------------------------------------------------------
# Raw device info — platform dispatch
# -----------------------------------------------------------------------------
# device_guard::_raw_info <device>
#   Echoes one line of TAB-separated fields:
#     size_bytes <TAB> removable(0|1) <TAB> model <TAB> vendor <TAB> mount_csv
#   Returns 3 if the device doesn't exist, 5 if platform unsupported,
#   6 if a dep (jq) is missing.
# This is the ONE seam the tests stub — keep it small and pure.
device_guard::_raw_info() {
  local dev="$1"
  local platform
  platform="$(device_guard::_platform)"
  case "${platform}" in
    linux|wsl) device_guard::_raw_info_linux "${dev}" ;;
    macos)     device_guard::_raw_info_macos "${dev}" ;;
    windows)
      device_guard::_err 'Git Bash / MINGW / Cygwin is not supported. Run the PowerShell sibling script instead.'
      return 5
      ;;
    *)
      device_guard::_err "Unsupported platform (uname=$(uname -s))."
      return 5
      ;;
  esac
}

device_guard::_raw_info_linux() {
  local dev="$1"
  [[ -b "${dev}" ]] || { device_guard::_err "not a block device: ${dev}"; return 3; }
  device_guard::_require_jq || return $?

  local json
  if ! json="$(lsblk -b -o NAME,PATH,SIZE,TYPE,RM,MODEL,VENDOR,MOUNTPOINTS --json "${dev}" 2>/dev/null)"; then
    device_guard::_err "lsblk failed for ${dev}"
    return 3
  fi

  # Extract fields with jq. Always treat everything as strings to avoid
  # any chance of shell-interpreting adversarial content.
  local size rm model vendor mounts
  size="$(jq -r '.blockdevices[0].size     // empty' <<<"${json}")"
  # lsblk emits boolean true/false here; normalise to 0/1 at the jq source.
  rm="$(jq   -r '(.blockdevices[0].rm // false) | if . then 1 else 0 end' <<<"${json}")"
  model="$(jq -r '.blockdevices[0].model   // ""'    <<<"${json}")"
  vendor="$(jq -r '.blockdevices[0].vendor // ""'    <<<"${json}")"

  # Collect every mountpoint from the device and all its children.
  mounts="$(jq -r '
    def walk_mounts:
      ( .mountpoints // [] | .[]? | select(. != null) ),
      ( .children    // [] | .[]? | walk_mounts );
    [ .blockdevices[0] | walk_mounts ] | join(",")
  ' <<<"${json}")"

  if [[ -z "${size}" || ! "${size}" =~ ^[0-9]+$ ]]; then
    device_guard::_err "couldn't parse size from lsblk for ${dev}"
    return 3
  fi
  if [[ -z "${rm}" || ! "${rm}" =~ ^[01]$ ]]; then
    rm=0
  fi

  # Trim whitespace only — never execute.
  model="${model## }"; model="${model%% }"
  vendor="${vendor## }"; vendor="${vendor%% }"

  printf '%s\t%s\t%s\t%s\t%s\n' "${size}" "${rm}" "${model}" "${vendor}" "${mounts}"
}

device_guard::_raw_info_macos() {
  local dev="$1"
  [[ -b "${dev}" ]] || { device_guard::_err "not a block device: ${dev}"; return 3; }
  device_guard::_require_jq || return $?
  if ! command -v diskutil >/dev/null 2>&1 || ! command -v plutil >/dev/null 2>&1; then
    device_guard::_err 'diskutil and plutil are required on macOS'
    return 6
  fi

  local plist json
  if ! plist="$(diskutil info -plist "${dev}" 2>/dev/null)"; then
    device_guard::_err "diskutil info failed for ${dev}"
    return 3
  fi
  if ! json="$(printf '%s' "${plist}" | plutil -convert json -o - - 2>/dev/null)"; then
    device_guard::_err "plutil conversion failed for ${dev}"
    return 3
  fi

  local size rm model vendor mount internal boot_volume
  size="$(jq        -r '.Size              // empty' <<<"${json}")"
  rm="$(jq          -r '.RemovableMedia    // false' <<<"${json}")"
  model="$(jq       -r '.IORegistryEntryName // .MediaName // ""' <<<"${json}")"
  vendor="$(jq      -r '.DeviceVendor       // ""' <<<"${json}")"
  mount="$(jq       -r '.MountPoint         // ""' <<<"${json}")"
  internal="$(jq    -r '.Internal           // false' <<<"${json}")"
  boot_volume="$(jq -r '.BootVolume         // false' <<<"${json}")"

  # Normalise removable to 0/1.
  local rm_bit=0
  [[ "${rm}" == 'true' ]] && rm_bit=1

  # Treat internal + boot volume as a system mount so check() rejects it.
  if [[ "${internal}" == 'true' && "${boot_volume}" == 'true' ]]; then
    mount="/"
  fi

  if [[ -z "${size}" || ! "${size}" =~ ^[0-9]+$ ]]; then
    device_guard::_err "couldn't parse size from diskutil for ${dev}"
    return 3
  fi
  printf '%s\t%s\t%s\t%s\t%s\n' "${size}" "${rm_bit}" "${model}" "${vendor}" "${mount}"
}

# -----------------------------------------------------------------------------
# Public API
# -----------------------------------------------------------------------------

# List removable block device candidates, one per line. Suitable for piping.
device_guard::list_candidates() {
  local platform
  platform="$(device_guard::_platform)"
  case "${platform}" in
    linux|wsl)
      device_guard::_require_jq || return $?
      lsblk -b -o PATH,TYPE,RM --json 2>/dev/null | \
        jq -r '.blockdevices[]? | select(.type=="disk" and .rm=="1") | .path'
      ;;
    macos)
      device_guard::_require_jq || return $?
      command -v diskutil >/dev/null 2>&1 || {
        device_guard::_err 'diskutil missing'; return 6; }
      # /dev/diskN only; skip synthesized and internal.
      local ids id info
      ids="$(diskutil list -plist external physical 2>/dev/null \
        | plutil -convert json -o - - 2>/dev/null \
        | jq -r '.AllDisks[]? | select(test("^disk[0-9]+$"))')"
      while IFS= read -r id; do
        [[ -z "${id}" ]] && continue
        info="$(diskutil info -plist "/dev/${id}" 2>/dev/null \
          | plutil -convert json -o - - 2>/dev/null || true)"
        [[ -z "${info}" ]] && continue
        if [[ "$(jq -r '.RemovableMedia // false' <<<"${info}")" == 'true' ]]; then
          printf '/dev/%s\n' "${id}"
        fi
      done <<<"${ids}"
      ;;
    windows)
      device_guard::_err 'Git Bash / MINGW / Cygwin is not supported. Run the PowerShell sibling script instead.'
      return 5
      ;;
    *)
      device_guard::_err "Unsupported platform (uname=$(uname -s))."
      return 5
      ;;
  esac
}

# Print the size of <device> in bytes.
device_guard::size_bytes() {
  local dev="${1:-}"
  [[ -n "${dev}" ]] || { device_guard::_err 'size_bytes: missing device'; return 3; }
  local info size
  info="$(device_guard::_raw_info "${dev}")" || return $?
  IFS=$'\t' read -r size _ _ _ _ <<<"${info}"
  printf '%s\n' "${size}"
}

# Print a human-readable summary of <device>.
device_guard::human_info() {
  local dev="${1:-}"
  [[ -n "${dev}" ]] || { device_guard::_err 'human_info: missing device'; return 3; }
  local info size rm model vendor mounts
  info="$(device_guard::_raw_info "${dev}")" || return $?
  IFS=$'\t' read -r size rm model vendor mounts <<<"${info}"

  local rm_s='NO'
  [[ "${rm}" == '1' ]] && rm_s='YES'
  local mount_s="${mounts:-(none)}"

  printf 'Path:      %s\n'  "${dev}"
  printf 'Size:      %s (%s bytes)\n' "$(device_guard::_bytes_to_gib "${size}")" "${size}"
  printf 'Model:     %s\n'  "${model:-(unknown)}"
  printf 'Vendor:    %s\n'  "${vendor:-(unknown)}"
  printf 'Removable: %s\n'  "${rm_s}"
  printf 'Mounted:   %s\n'  "${mount_s}"
}

# Check whether a mount is a "system" (non-removable-autoloc) mount.
device_guard::_is_system_mount() {
  local mp="$1"
  [[ -z "${mp}" ]] && return 1
  # Removable auto-mount paths are OK.
  case "${mp}" in
    /media/*|/run/media/*|/Volumes/*|/mnt/hermod-*) return 1 ;;
  esac
  local sys
  for sys in "${DEVICE_GUARD_SYSTEM_MOUNTS[@]}"; do
    [[ "${mp}" == "${sys}" ]] && return 0
  done
  # Anything under /mnt/* (but not /mnt/hermod-*) we treat as system per spec.
  [[ "${mp}" == /mnt/* ]] && return 0
  return 1
}

# Countdown before a size-guard bypass. Goes to stderr, reads /dev/tty for
# Ctrl+C; unit-testable because it obeys HERMOD_PI_TEST_FAST_COUNTDOWN=1.
device_guard::_size_guard_countdown() {
  local dev="$1" size="$2"
  local gib
  gib="$(device_guard::_bytes_to_gib "${size}")"
  printf 'BYPASSING SIZE GUARD — device %s is %s, press Ctrl+C to abort.\n' \
    "${dev}" "${gib}" >&2
  local i sleep_cmd
  if [[ -n "${HERMOD_PI_TEST_FAST_COUNTDOWN:-}" ]]; then
    sleep_cmd=(true)
  else
    sleep_cmd=(sleep 1)
  fi
  for i in 10 9 8 7 6 5 4 3 2 1; do
    printf '%d... ' "${i}" >&2
    "${sleep_cmd[@]}"
  done
  printf '\n' >&2
}

# Run all device safety checks. See header for return codes.
device_guard::check() {
  local dev="${1:-}"
  [[ -n "${dev}" ]] || { device_guard::_err 'check: missing device'; return 3; }

  # Platform gate first so we fail loud on Git Bash / Windows before
  # touching anything else.
  local platform
  platform="$(device_guard::_platform)"
  case "${platform}" in
    linux|wsl|macos) ;;
    windows)
      device_guard::_err 'Git Bash / MINGW / Cygwin is not supported. Run the PowerShell sibling script instead.'
      return 5
      ;;
    *)
      device_guard::_err "Unsupported platform (uname=$(uname -s))."
      return 5
      ;;
  esac

  # Gather raw info via the (stubbable) seam.
  local info size rm _model _vendor mounts
  info="$(device_guard::_raw_info "${dev}")" || return $?
  IFS=$'\t' read -r size rm _model _vendor mounts <<<"${info}"

  # (1) removable — NEVER bypassable
  if [[ "${rm}" != '1' ]]; then
    device_guard::_err "refusing: ${dev} is NOT marked removable (internal drive). This check cannot be bypassed."
    return 2
  fi

  # (2) mounted system partition — NEVER bypassable
  if [[ -n "${mounts}" ]]; then
    local IFS_old="${IFS}" mp
    IFS=','
    # shellcheck disable=SC2206 # intentional splitting of our own CSV
    local mps=(${mounts})
    IFS="${IFS_old}"
    for mp in "${mps[@]}"; do
      [[ -z "${mp}" ]] && continue
      if device_guard::_is_system_mount "${mp}"; then
        device_guard::_err "refusing: ${dev} has a system partition mounted at ${mp}"
        return 4
      fi
    done
  fi

  # (3) encrypted system disk guard (Linux only, best-effort)
  if [[ "${platform}" == linux || "${platform}" == wsl ]]; then
    if command -v blkid >/dev/null 2>&1; then
      local btype
      btype="$(blkid -o value -s TYPE "${dev}" 2>/dev/null || true)"
      if [[ "${btype}" == 'crypto_LUKS' ]]; then
        # If any dm-crypt mapping backed by this device is open, refuse.
        if command -v lsblk >/dev/null 2>&1 \
           && lsblk -ln -o TYPE "${dev}" 2>/dev/null | grep -q '^crypt$'; then
          device_guard::_err "refusing: ${dev} hosts an OPEN LUKS volume"
          return 7
        fi
      fi
    fi
  fi

  # (4) size guard — bypassable
  if (( size > DEVICE_GUARD_SIZE_LIMIT_BYTES )); then
    if [[ "${HERMOD_PI_DISABLE_SIZE_GUARD:-}" == "${DEVICE_GUARD_OVERRIDE_TOKEN}" ]]; then
      device_guard::_size_guard_countdown "${dev}" "${size}"
      device_guard::_audit 'size-guard-bypass' "${dev}" "${size}"
    else
      device_guard::_err "refusing: ${dev} is ${size} bytes ($(device_guard::_bytes_to_gib "${size}")), limit is ${DEVICE_GUARD_SIZE_LIMIT_BYTES} bytes (256.0 GiB). Set HERMOD_PI_DISABLE_SIZE_GUARD=${DEVICE_GUARD_OVERRIDE_TOKEN} to override."
      return 1
    fi
  fi

  return 0
}

# Interactive confirmation. Reads from /dev/tty so pipes can't bypass it.
device_guard::confirm() {
  local dev="${1:-}"
  [[ -n "${dev}" ]] || { device_guard::_err 'confirm: missing device'; return 1; }

  local info size rm model vendor mounts
  info="$(device_guard::_raw_info "${dev}")" || return 1
  IFS=$'\t' read -r size rm model vendor mounts <<<"${info}"

  local rm_s='NO'
  [[ "${rm}" == '1' ]] && rm_s='YES'
  local mount_s="${mounts:-(none)}"
  if [[ -n "${mounts}" ]]; then
    mount_s="${mounts} (will be unmounted)"
  fi

  # Box-drawing summary — all to stderr so stdout stays clean for callers
  # that might want to capture something structured later.
  {
    printf '┌─ Target device ─────────────────────────────\n'
    printf '│ Path:      %s\n' "${dev}"
    printf '│ Size:      %s\n' "$(device_guard::_bytes_to_gib "${size}")"
    printf '│ Model:     %s\n' "${model:-(unknown)}"
    printf '│ Vendor:    %s\n' "${vendor:-(unknown)}"
    printf '│ Removable: %s\n' "${rm_s}"
    printf '│ Mounted:   %s\n' "${mount_s}"
    printf '└──────────────────────────────────────────────\n'
    printf 'Type the EXACT device path to confirm flashing (or '\''abort'\''): '
  } >&2

  local reply tty_src
  # Prefer /dev/tty so a piped stdin can't auto-confirm. Tests set
  # HERMOD_PI_TEST_TTY to inject a file path.
  if [[ -n "${HERMOD_PI_TEST_TTY:-}" ]]; then
    tty_src="${HERMOD_PI_TEST_TTY}"
  elif [[ -r /dev/tty ]]; then
    tty_src=/dev/tty
  else
    device_guard::_err 'confirm: no TTY available'
    return 1
  fi

  IFS= read -r reply < "${tty_src}" || { device_guard::_err 'confirm: read failed'; return 1; }

  if [[ "${reply}" == 'abort' ]]; then
    device_guard::_err 'aborted by user'
    return 1
  fi
  if [[ "${reply}" != "${dev}" ]]; then
    device_guard::_err "confirmation mismatch (got: ${reply})"
    return 1
  fi
  return 0
}

# -----------------------------------------------------------------------------
# ---- tests ----
# -----------------------------------------------------------------------------

# shellcheck disable=SC2317 # test block re-declares _raw_info as a stub; shellcheck can't see the indirect call path
device_guard::_run_tests() {
  local pass=0 fail=0 n=0
  # Use a module-level variable so the EXIT trap can still see the path
  # even after this function returns and its locals are popped.
  __DEVICE_GUARD_TEST_TMPDIR="$(mktemp -d)"
  local tmpdir="${__DEVICE_GUARD_TEST_TMPDIR}"
  trap 'rm -rf -- "${__DEVICE_GUARD_TEST_TMPDIR:-}"' EXIT

  _ok()    { n=$((n+1)); pass=$((pass+1)); printf 'ok %d - %s\n' "${n}" "$1"; }
  _notok() { n=$((n+1)); fail=$((fail+1)); printf 'not ok %d - %s\n' "${n}" "$1"; [[ -n "${2:-}" ]] && printf '  # %s\n' "$2"; }

  # Test 1: bytes_to_gib formatting.
  local out
  out="$(device_guard::_bytes_to_gib 128000000000)"
  if [[ "${out}" == '119.2 GiB' ]]; then
    _ok "bytes_to_gib formats 128GB correctly"
  else
    _notok "bytes_to_gib formats 128GB correctly" "got: ${out}"
  fi

  # Test 2: size guard rejects 300GB device.
  device_guard::_raw_info() { printf '322122547200\t1\tMockBig\tTest\t\n'; }
  unset HERMOD_PI_DISABLE_SIZE_GUARD
  local rc
  set +e; device_guard::check /dev/mock 2>/dev/null; rc=$?; set -e
  if (( rc == 1 )); then
    _ok "300GB device triggers size guard (rc=1)"
  else
    _notok "300GB device triggers size guard" "expected 1, got ${rc}"
  fi

  # Test 3: 128GB device passes all checks.
  device_guard::_raw_info() { printf '128000000000\t1\tMockSD\tTest\t\n'; }
  set +e; device_guard::check /dev/mock 2>/dev/null; rc=$?; set -e
  if (( rc == 0 )); then
    _ok "128GB removable device passes (rc=0)"
  else
    _notok "128GB removable device passes" "expected 0, got ${rc}"
  fi

  # Test 4: non-removable device rejected with code 2.
  device_guard::_raw_info() { printf '64000000000\t0\tInternalSSD\tTest\t\n'; }
  set +e; device_guard::check /dev/mock 2>/dev/null; rc=$?; set -e
  if (( rc == 2 )); then
    _ok "non-removable device rejected (rc=2)"
  else
    _notok "non-removable device rejected" "expected 2, got ${rc}"
  fi

  # Test 5: size override bypass + countdown on stderr.
  device_guard::_raw_info() { printf '322122547200\t1\tMockBig\tTest\t\n'; }
  local stderr_file="${tmpdir}/stderr5"
  HERMOD_PI_DISABLE_SIZE_GUARD='i-understand-the-risks' \
  HERMOD_PI_TEST_FAST_COUNTDOWN=1 \
  HOME="${tmpdir}" \
    bash -c '
      source "'"${BASH_SOURCE[0]}"'" >/dev/null 2>&1
      device_guard::_raw_info() { printf "322122547200\t1\tMockBig\tTest\t\n"; }
      device_guard::check /dev/mock
    ' 2>"${stderr_file}"
  rc=$?
  if (( rc == 0 )) && grep -q 'BYPASSING SIZE GUARD' "${stderr_file}" \
                   && grep -q '10\.\.\.' "${stderr_file}"; then
    _ok "size override bypass works with countdown"
  else
    _notok "size override bypass works with countdown" \
      "rc=${rc} stderr=$(tr '\n' '|' <"${stderr_file}")"
  fi
  unset HERMOD_PI_DISABLE_SIZE_GUARD HERMOD_PI_TEST_FAST_COUNTDOWN

  # Test 6: platform detection via stubbed uname.
  local saved_path="${PATH}"
  local fake_bin="${tmpdir}/fakebin"; mkdir -p "${fake_bin}"
  for sys in Linux Darwin MINGW64_NT-10.0 Plan9; do
    cat >"${fake_bin}/uname" <<EOF
#!/usr/bin/env bash
if [[ "\${1:-}" == "-s" ]]; then echo "${sys}"; else echo "${sys}"; fi
EOF
    chmod +x "${fake_bin}/uname"
    local detected
    detected="$(PATH="${fake_bin}:${saved_path}" bash -c 'uname -s; exit 0' 2>/dev/null)"
    # Use the real function with stubbed uname.
    detected="$(PATH="${fake_bin}:${saved_path}" bash -c '
      source "'"${BASH_SOURCE[0]}"'" >/dev/null 2>&1
      device_guard::_platform
    ')"
    case "${sys}:${detected}" in
      Linux:linux|Darwin:macos|MINGW64_NT-10.0:windows|Plan9:unknown)
        _ok "platform detection: ${sys} → ${detected}" ;;
      *)
        _notok "platform detection: ${sys} → ${detected}" "unexpected mapping" ;;
    esac
  done
  PATH="${saved_path}"

  # Test 7a: confirm returns 0 on exact match.
  device_guard::_raw_info() { printf '128000000000\t1\tMockSD\tTest\t\n'; }
  local tty_file="${tmpdir}/tty_ok"
  printf '/dev/mock\n' >"${tty_file}"
  set +e
  HERMOD_PI_TEST_TTY="${tty_file}" device_guard::confirm /dev/mock >/dev/null 2>&1
  rc=$?
  set -e
  if (( rc == 0 )); then
    _ok "confirm returns 0 on exact match"
  else
    _notok "confirm returns 0 on exact match" "rc=${rc}"
  fi

  # Test 7b: confirm returns 1 on mismatch.
  local tty_bad="${tmpdir}/tty_bad"
  printf 'y\n' >"${tty_bad}"
  set +e
  HERMOD_PI_TEST_TTY="${tty_bad}" device_guard::confirm /dev/mock >/dev/null 2>&1
  rc=$?
  set -e
  if (( rc == 1 )); then
    _ok "confirm returns 1 on mismatch"
  else
    _notok "confirm returns 1 on mismatch" "rc=${rc}"
  fi

  # Test 7c: confirm returns 1 on 'abort'.
  local tty_abort="${tmpdir}/tty_abort"
  printf 'abort\n' >"${tty_abort}"
  set +e
  HERMOD_PI_TEST_TTY="${tty_abort}" device_guard::confirm /dev/mock >/dev/null 2>&1
  rc=$?
  set -e
  if (( rc == 1 )); then
    _ok "confirm returns 1 on 'abort'"
  else
    _notok "confirm returns 1 on 'abort'" "rc=${rc}"
  fi

  # Test 8: adversarial JSON is treated as a pure string.
  # We feed a NAME containing shell metachars and ensure the library
  # doesn't execute it — if it did, `rm -rf /tmp/device_guard_canary`
  # would actually be run. We create the canary file; test passes iff
  # it still exists after the call.
  local canary="${tmpdir}/canary_keep_me"
  : > "${canary}"
  device_guard::_raw_info() {
    # Deliberately include characters that would be dangerous if eval'd.
    printf '%s\t1\t%s\t%s\t\n' \
      '64000000000' \
      "'; rm -rf ${canary}; #" \
      "\`rm -rf ${canary}\`"
  }
  set +e
  device_guard::human_info /dev/mock >/dev/null 2>&1
  device_guard::check      /dev/mock >/dev/null 2>&1
  set -e
  if [[ -f "${canary}" ]]; then
    _ok "adversarial JSON fields are not executed"
  else
    _notok "adversarial JSON fields are not executed" "canary was deleted"
  fi

  # Summary.
  printf '\n1..%d  # passed=%d failed=%d\n' "${n}" "${pass}" "${fail}"
  (( fail == 0 ))
}

# -----------------------------------------------------------------------------
# Entry point: allow `bash device_guard.sh test` as self-test runner.
# When sourced, $0 is the parent script, so BASH_SOURCE[0] != $0 and this
# block is skipped — the library just registers its functions.
# -----------------------------------------------------------------------------
if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
  case "${1:-}" in
    test)
      device_guard::_run_tests
      exit $?
      ;;
    list)    device_guard::list_candidates ;;
    check)   shift; device_guard::check "$@" ;;
    info)    shift; device_guard::human_info "$@" ;;
    confirm) shift; device_guard::confirm "$@" ;;
    size)    shift; device_guard::size_bytes "$@" ;;
    ''|help|-h|--help)
      cat <<EOF
device_guard.sh — Hermod Pi device safety library

Source it from a script, or use these CLI verbs for ad-hoc checks:
  bash device_guard.sh list
  bash device_guard.sh check <dev>
  bash device_guard.sh info  <dev>
  bash device_guard.sh size  <dev>
  bash device_guard.sh confirm <dev>
  bash device_guard.sh test
EOF
      ;;
    *)
      device_guard::_err "unknown verb: ${1}"
      exit 2
      ;;
  esac
fi
