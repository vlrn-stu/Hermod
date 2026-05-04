#!/usr/bin/env bash
# cmd-users.sh — `hermod.sh users <action>` subcommand.
#
# Local source-of-truth for vault42 seed users. Format matches the
# seed.json vault42 imports on first boot (kubernetes/base/vault42/
# seed-configmap.yaml). Lives at ~/.hermod-pi/seed-users.json by default;
# encrypts/decrypts via mimir when the operator has run mimir_init on it.
#
# Workflow:
#   1. hermod.sh users init                        bootstrap with 3 defaults
#   2. hermod.sh users add admin@hermod.local admin
#   3. hermod.sh install prod-pi                   ensure-secrets pushes
#                                                  seed-json key to the
#                                                  vault42-seed-credentials
#                                                  Secret; render-seed init
#                                                  picks it up; vault42
#                                                  imports on first boot
#   4. After rollout, ensure-secrets wipes the seed-json key (replaces
#      with {"users":[]}) so the cluster never holds plaintext seed
#      passwords once vault has bcrypted them internally.
#   5. To re-seed (rotate operator pw, add a user post-deploy):
#        hermod.sh users set-password ... / users add ...
#        hermod.sh reset-db prod-pi          drop vault DB
#        hermod.sh install prod-pi           reseed
#      Vault42 won't re-import an existing user, so the drop-DB step is
#      mandatory for changes to existing accounts.
#
# Sourced by hermod.sh once $REPO_ROOT is set + lib/lib.sh + lib/mimir.sh
# are loaded.

[[ -n "${_HERMOD_CMD_USERS_LOADED:-}" ]] && return 0
_HERMOD_CMD_USERS_LOADED=1

# ── paths ──────────────────────────────────────────────────────────────
HERMOD_USERS_FILE="${HERMOD_USERS_FILE:-$HOME/.hermod-pi/seed-users.json}"

# Valid role names. Must match vault42's policy enum (Viewer/User/Operator/
# Admin in the C# enum, lowercased here for the JSON seed). Adding a new
# role on the vault42 side is a separate change in that repo's RoleClaim
# constants.
_USERS_VALID_ROLES=(viewer user operator admin)

# ── helpers ────────────────────────────────────────────────────────────
_users_log()  { printf '\033[1;32m[users]\033[0m %s\n' "$*"; }
_users_warn() { printf '\033[1;33m[users]\033[0m %s\n' "$*" >&2; }
_users_die()  { printf '\033[1;31m[users]\033[0m %s\n' "$*" >&2; exit 1; }

# Encrypted-form path. mimir_init creates <file>.mimir alongside the plain
# file; mimir_load reads either form transparently.
_users_mimir_path() { printf '%s.mimir\n' "$HERMOD_USERS_FILE"; }

_users_is_encrypted() {
    [[ -f "$(_users_mimir_path)" ]]
}

# Decrypt+read into stdout. If the file is plaintext-only, cats it. If a
# .mimir sibling exists, mimir_load decrypts to the session cache and
# echoes. Returns 1 if neither exists.
_users_read() {
    if [[ ! -f "$HERMOD_USERS_FILE" && ! -f "$(_users_mimir_path)" ]]; then
        return 1
    fi
    HERMOD_MIMIR_QUIET=1 mimir_load "$HERMOD_USERS_FILE"
}

# Write the given JSON content to the seed file. If the file is currently
# encrypted (has a .mimir sibling), re-encrypts after writing the
# plaintext (mimir_init shreds the plaintext immediately). If not
# encrypted, leaves the plaintext at $HERMOD_USERS_FILE.
_users_write() {
    local json="$1"
    # Validate JSON before touching anything on disk.
    printf '%s' "$json" | jq -e . >/dev/null 2>&1 \
        || _users_die "internal error: refusing to write non-JSON content"

    mkdir -p "$(dirname "$HERMOD_USERS_FILE")"
    chmod 700 "$(dirname "$HERMOD_USERS_FILE")" 2>/dev/null || true

    if _users_is_encrypted; then
        # Read pin_required from the existing .meta BEFORE we shred the
        # ciphertext. Re-encryption must reuse the same PIN regime as
        # the original lock; otherwise an `add` against a no-PIN vault
        # would suddenly demand a PIN, and an `add` against a PIN vault
        # would silently downgrade to no-PIN.
        local meta_path; meta_path="$(_users_mimir_path).meta"
        local pin_required="false"
        if [[ -f "$meta_path" ]]; then
            pin_required="$(_mimir_meta_get "$HERMOD_USERS_FILE" pin_required 2>/dev/null || echo false)"
        fi

        local new_pin
        if [[ "$pin_required" == "true" ]]; then
            if [[ -n "${HERMOD_MIMIR_PIN:-}" ]]; then
                new_pin="$HERMOD_MIMIR_PIN"
            else
                _users_die "seed-users.json is PIN-encrypted; run 'hermod.sh mimir unlock $HERMOD_USERS_FILE' first so the cached PIN re-locks the new ciphertext"
            fi
        else
            new_pin=""
        fi

        printf '%s' "$json" > "$HERMOD_USERS_FILE"
        chmod 0600 "$HERMOD_USERS_FILE"
        rm -f "$(_users_mimir_path)" "$meta_path"

        # Export with `+x` semantics: HERMOD_MIMIR_PIN_NEW='' counts as
        # set-to-empty for mimir_init, which correctly reads it as
        # no-PIN encryption (empty string allowed).
        HERMOD_MIMIR_PIN_NEW="$new_pin" HERMOD_MIMIR_QUIET=1 \
            mimir_init "$HERMOD_USERS_FILE" \
            || _users_die "re-encrypt failed; plaintext at $HERMOD_USERS_FILE"
        # Shred the session cache: a previous list/read warmed it with
        # the pre-edit content, and mimir_load short-circuits to the
        # cache when fresh. Without this, the next read returns stale
        # data despite the new ciphertext on disk.
        local cache_path; cache_path="$(_mimir_cache_path "$HERMOD_USERS_FILE")"
        [[ -f "$cache_path" ]] && _mimir_shred "$cache_path"
    else
        printf '%s' "$json" > "$HERMOD_USERS_FILE"
        chmod 0600 "$HERMOD_USERS_FILE"
    fi
}

# Random 24-char alphanumeric password. Same recipe as ensure-secrets.sh
# for any auto-generated slot.
_users_random_password() {
    if command -v openssl >/dev/null 2>&1; then
        openssl rand -base64 48 | tr -d '=+/\n' | head -c 24
    else
        head -c 48 /dev/urandom | base64 | tr -d '=+/\n' | head -c 24
    fi
}

# Silent password prompt with confirm. Echoes the password on stdout.
# Empty input rejected; mismatched confirm reprompts.
_users_prompt_password() {
    local prompt_label="${1:-Password}"
    local p1 p2
    while true; do
        printf '%s: ' "$prompt_label" >&2
        IFS= read -rs p1; printf '\n' >&2
        if [[ -z "$p1" ]]; then
            _users_warn "empty password rejected"
            continue
        fi
        printf '%s (confirm): ' "$prompt_label" >&2
        IFS= read -rs p2; printf '\n' >&2
        if [[ "$p1" != "$p2" ]]; then
            _users_warn "passwords do not match; try again"
            continue
        fi
        printf '%s' "$p1"
        return 0
    done
}

_users_validate_role() {
    local role="$1" valid
    for valid in "${_USERS_VALID_ROLES[@]}"; do
        [[ "$role" == "$valid" ]] && return 0
    done
    _users_die "invalid role '$role' (valid: ${_USERS_VALID_ROLES[*]})"
}

# Default seed roster. Identical to the historic 3-account default the
# hardcoded vault42 seed-configmap shipped with, just emitted at runtime
# from the local file rather than baked into the cluster ConfigMap.
_users_default_seed() {
    local viewer_pass user_pass operator_pass
    viewer_pass="$(_users_random_password)"
    user_pass="$(_users_random_password)"
    operator_pass="$(_users_random_password)"
    jq -n \
        --arg vp "$viewer_pass" \
        --arg up "$user_pass" \
        --arg op "$operator_pass" '
        {
            users: [
                {email:"viewer@hermod.local",   password:$vp, display_name:"viewer",   locale:"en", email_verified:true, roles:["viewer"]},
                {email:"user@hermod.local",     password:$up, display_name:"user",     locale:"en", email_verified:true, roles:["user"]},
                {email:"operator@hermod.local", password:$op, display_name:"operator", locale:"en", email_verified:true, roles:["operator"]}
            ]
        }'
}

# ── subcommands ────────────────────────────────────────────────────────

_users_cmd_help() {
    cat <<'EOF'
hermod.sh users <action> [args]

  Local source-of-truth for vault42 seed accounts. Stored at
  ~/.hermod-pi/seed-users.json (mimir-encrypted if you've run
  `mimir init` on it). Pushed to the cluster Secret on every
  install/update, then wiped post-rollout so plaintext passwords
  don't linger in the cluster.

  init                            bootstrap with 3 default accounts
                                  (viewer/user/operator @hermod.local)
  list [--with-passwords]         show roster (passwords masked unless flag)
  add <email> <role> [display]    add a user; prompts for password
  remove <email>                  drop a user
  set-role <email> <role>         change role
  set-password <email>            prompt + replace password

  Roles: viewer, user, operator, admin (must match vault42 enum).

  Changes to EXISTING users only take effect after dropping the vault DB:
    hermod.sh reset-db <target>
    hermod.sh install <target>
EOF
}

_users_cmd_init() {
    if [[ -f "$HERMOD_USERS_FILE" || -f "$(_users_mimir_path)" ]]; then
        _users_die "seed file already exists at $HERMOD_USERS_FILE; refusing to overwrite"
    fi
    local seed; seed="$(_users_default_seed)"
    _users_write "$seed"
    _users_log "wrote 3 default users (viewer/user/operator @hermod.local) to $HERMOD_USERS_FILE"
    _users_log "run 'hermod.sh users list --with-passwords' to see the generated passwords"
}

_users_cmd_list() {
    local with_passwords=0
    [[ "${1:-}" == "--with-passwords" ]] && with_passwords=1
    local seed; seed="$(_users_read)" || _users_die "no seed file (run 'hermod.sh users init' first)"
    if (( with_passwords )); then
        printf '%s' "$seed" | jq -r '.users[] | "\(.email)\t\(.roles|join(","))\t\(.display_name)\t\(.password)"' \
            | column -t -s $'\t' -N "EMAIL,ROLES,DISPLAY,PASSWORD"
    else
        printf '%s' "$seed" | jq -r '.users[] | "\(.email)\t\(.roles|join(","))\t\(.display_name)\t****"' \
            | column -t -s $'\t' -N "EMAIL,ROLES,DISPLAY,PASSWORD"
    fi
}

_users_cmd_add() {
    local email="${1:-}" role="${2:-}" display="${3:-}"
    [[ -n "$email" ]]   || _users_die "usage: hermod.sh users add <email> <role> [display]"
    [[ -n "$role" ]]    || _users_die "usage: hermod.sh users add <email> <role> [display]"
    [[ "$email" == *@* ]] || _users_die "email must contain '@'"
    _users_validate_role "$role"
    [[ -n "$display" ]] || display="${email%%@*}"

    local seed; seed="$(_users_read)" || _users_die "no seed file (run 'hermod.sh users init' first)"

    # Reject duplicates up-front rather than letting vault42 silently
    # skip the second copy on import.
    if printf '%s' "$seed" | jq -e --arg e "$email" '.users[] | select(.email == $e)' >/dev/null 2>&1; then
        _users_die "user '$email' already exists; use 'set-role' or 'set-password' instead"
    fi

    local password; password="$(_users_prompt_password "Password for $email")"

    local updated; updated="$(printf '%s' "$seed" | jq \
        --arg e "$email" --arg p "$password" --arg d "$display" --arg r "$role" \
        '.users += [{email:$e, password:$p, display_name:$d, locale:"en", email_verified:true, roles:[$r]}]')"

    _users_write "$updated"
    _users_log "added $email (role=$role, display=$display)"
    _users_log "this account becomes active after the NEXT install/update + first vault42 boot"
}

_users_cmd_remove() {
    local email="${1:-}"
    [[ -n "$email" ]] || _users_die "usage: hermod.sh users remove <email>"
    local seed; seed="$(_users_read)" || _users_die "no seed file"
    if ! printf '%s' "$seed" | jq -e --arg e "$email" '.users[] | select(.email == $e)' >/dev/null 2>&1; then
        _users_die "user '$email' not found"
    fi
    local updated; updated="$(printf '%s' "$seed" | jq --arg e "$email" '.users |= map(select(.email != $e))')"
    _users_write "$updated"
    _users_log "removed $email from local seed (vault42 keeps the existing account until you reset-db + install)"
}

_users_cmd_set_role() {
    local email="${1:-}" role="${2:-}"
    [[ -n "$email" && -n "$role" ]] || _users_die "usage: hermod.sh users set-role <email> <role>"
    _users_validate_role "$role"
    local seed; seed="$(_users_read)" || _users_die "no seed file"
    if ! printf '%s' "$seed" | jq -e --arg e "$email" '.users[] | select(.email == $e)' >/dev/null 2>&1; then
        _users_die "user '$email' not found"
    fi
    local updated; updated="$(printf '%s' "$seed" | jq --arg e "$email" --arg r "$role" \
        '.users |= map(if .email == $e then .roles = [$r] else . end)')"
    _users_write "$updated"
    _users_log "set $email role → $role (takes effect after reset-db + install)"
}

_users_cmd_set_password() {
    local email="${1:-}"
    [[ -n "$email" ]] || _users_die "usage: hermod.sh users set-password <email>"
    local seed; seed="$(_users_read)" || _users_die "no seed file"
    if ! printf '%s' "$seed" | jq -e --arg e "$email" '.users[] | select(.email == $e)' >/dev/null 2>&1; then
        _users_die "user '$email' not found"
    fi
    local password; password="$(_users_prompt_password "New password for $email")"
    local updated; updated="$(printf '%s' "$seed" | jq --arg e "$email" --arg p "$password" \
        '.users |= map(if .email == $e then .password = $p else . end)')"
    _users_write "$updated"
    _users_log "rotated password for $email (takes effect after reset-db + install)"
}

# Print the current seed JSON to stdout. Used by ensure-secrets.sh to
# pump the seed into the cluster Secret. Returns 1 if the file is
# missing — caller falls back to the historic 3-account behaviour.
users_dump_seed_json() {
    _users_read
}

# Run the ensure_secrets entrypoint locally with HERMOD_USERS_SEED_JSON
# pre-populated from the operator's seed-users.json (empty if no local
# file; ensure-secrets falls back to the 3-account template). Other env
# vars the caller wants to forward (HERMOD_NAMESPACE, KUBECTL,
# HERMOD_SECRETS_MODE) must already be exported in the caller's shell.
ensure_secrets_with_users() {
    local seed; seed="$(users_dump_seed_json 2>/dev/null || true)"
    HERMOD_USERS_SEED_JSON="$seed" \
        bash -c "source '$REPO_ROOT/lib/ensure-secrets.sh' && ensure_secrets"
}

# ── dispatcher ─────────────────────────────────────────────────────────

cmd_users() {
    local action="${1:-help}"; shift || true
    case "$action" in
        help|-h|--help) _users_cmd_help ;;
        init)           _users_cmd_init "$@" ;;
        list|ls)        _users_cmd_list "$@" ;;
        add)            _users_cmd_add "$@" ;;
        remove|rm)      _users_cmd_remove "$@" ;;
        set-role)       _users_cmd_set_role "$@" ;;
        set-password)   _users_cmd_set_password "$@" ;;
        *) _users_die "users: unknown action '$action' (try 'hermod.sh users help')" ;;
    esac
}
