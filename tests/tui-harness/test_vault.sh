#!/usr/bin/env bash
# Vault end-to-end smoke against a temp env file. Drives mimir.sh
# directly (not through the TUI) so PIN modal isn't in the loop —
# that's tested separately with drive.py.

set -euo pipefail

cd "$(dirname "$0")/../.."
ROOT="$(pwd)"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

export HERMOD_MIMIR_CACHE_DIR="$TMP/cache"
export HERMOD_MIMIR_BACKUP_DIR="$TMP/backup"
F="$TMP/sample.env"

cat > "$F" <<EOF
HERMOD_PI_SSH_HOST=10.42.0.58
HERMOD_PI_SSH_USER=ubuntu
HERMOD_VAULT42_ADMIN_PASSWORD=topsecret
EOF

# shellcheck source=/dev/null
source "$ROOT/lib/mimir.sh"

echo "── 1. encrypt with PIN=1111"
HERMOD_MIMIR_PIN_NEW=1111 mimir_init "$F"
[[ -f "$F.mimir" ]]      || { echo FAIL: ciphertext missing; exit 1; }
[[ -f "$F.mimir.meta" ]] || { echo FAIL: meta missing; exit 1; }
[[ ! -f "$F" ]]          || { echo FAIL: plaintext should be gone; exit 1; }
ls "$HERMOD_MIMIR_BACKUP_DIR"/*.* >/dev/null || { echo FAIL: backup missing; exit 1; }
echo "  ok"

echo "── 2. load + verify content (warm cache)"
HERMOD_MIMIR_PIN=1111 mimir_load "$F" | grep -q topsecret || { echo FAIL: decrypt mismatch; exit 1; }
HERMOD_MIMIR_PIN=1111 mimir_load "$F" | grep -q 10.42.0.58 || { echo FAIL: decrypt mismatch; exit 1; }
echo "  ok"

echo "── 3. wrong PIN rejects"
if HERMOD_MIMIR_PIN=9999 mimir_unlock "$F" --force 2>/dev/null; then
    echo FAIL: wrong PIN should not decrypt; exit 1
fi
echo "  ok"

echo "── 4. rekey to PIN=4242"
HERMOD_MIMIR_PIN=1111 HERMOD_MIMIR_PIN_NEW=4242 mimir_rekey "$F"
HERMOD_MIMIR_PIN=4242 mimir_load "$F" | grep -q topsecret || { echo FAIL: post-rekey decrypt; exit 1; }
echo "  ok"

echo "── 5. lock (cache shred)"
mimir_lock "$F"
[[ ! -s "$HERMOD_MIMIR_CACHE_DIR"/*.unlocked ]] 2>/dev/null
echo "  ok"

echo "── 6. empty PIN init on fresh file"
F2="$TMP/empty.env"
echo "FOO=bar" > "$F2"
HERMOD_MIMIR_PIN_NEW="" mimir_init "$F2"
HERMOD_MIMIR_PIN="" mimir_load "$F2" | grep -q "FOO=bar" || { echo FAIL: empty-PIN load; exit 1; }
echo "  ok"

echo "── 7. ssh-key refusal"
KEY="$TMP/danger.key"
printf -- "-----BEGIN OPENSSH PRIVATE KEY-----\nblah\n" > "$KEY"
if HERMOD_MIMIR_PIN_NEW=x mimir_init "$KEY" 2>/dev/null; then
    echo FAIL: should refuse SSH key; exit 1
fi
echo "  ok"

echo
echo "vault end-to-end smoke PASS"
