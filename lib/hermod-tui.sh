#!/usr/bin/env bash
# hermod-tui.sh — keyboard-driven operator console.
#
# Two-pane layout: section list on the left, contextual content + action
# keys on the right, single status line on top. Pure bash + ANSI; no
# `dialog`/`whiptail`/`gum` dependencies so it works identically on
# macOS bash 3.2, Linux bash 5.x, and WSL2.
#
# Layered on top of hermod.sh's existing `cmd_*` functions — the TUI is
# a thin orchestrator that calls those handlers and re-renders. Mímir
# unlock is inline (gated by the same _mimir_load that hermod.sh uses
# for hermod-prod.env).
#
# Sources:
#   This file is sourced by hermod.sh (or invoked via `hermod.sh tui`).
#   It expects $REPO_ROOT to be set + cmd_* functions to be in scope.

[[ -n "${_HERMOD_TUI_LOADED:-}" ]] && return 0
_HERMOD_TUI_LOADED=1

# Mímir is required for env loading. The TUI is silent about it unless
# something fails.
if ! type mimir_load >/dev/null 2>&1; then
    # shellcheck disable=SC1091
    source "${REPO_ROOT:-.}/lib/mimir.sh"
fi

# ── ANSI primitives ──────────────────────────────────────────────────
# Brand green is #00FF42 — closest 256-color match is 46. Standard 32
# (forest) is muted on most terminals; 38;5;46 keeps the emerald punch
# everywhere except `linux` console which falls back to the 16-color
# table on its own.
_TUI_ESC=$'\033'
_TUI_RESET="${_TUI_ESC}[0m"
_TUI_BOLD="${_TUI_ESC}[1m"
_TUI_DIM="${_TUI_ESC}[2m"
_TUI_INV="${_TUI_ESC}[7m"
_TUI_FG_GREEN="${_TUI_ESC}[38;5;46m"
_TUI_FG_YELLOW="${_TUI_ESC}[33m"
_TUI_FG_RED="${_TUI_ESC}[31m"
_TUI_FG_CYAN="${_TUI_ESC}[36m"
_TUI_FG_GREY="${_TUI_ESC}[90m"

_tui_clear()       { printf '%s[2J%s[H' "$_TUI_ESC" "$_TUI_ESC"; }
_tui_cup()         { printf '%s[%d;%dH' "$_TUI_ESC" "$1" "$2"; }
_tui_clear_line()  { printf '%s[2K' "$_TUI_ESC"; }
_tui_hide_cursor() { printf '%s[?25l' "$_TUI_ESC"; }
_tui_show_cursor() { printf '%s[?25h' "$_TUI_ESC"; }
_tui_alt_buffer()  { printf '%s[?1049h' "$_TUI_ESC"; }
_tui_main_buffer() { printf '%s[?1049l' "$_TUI_ESC"; }

# ── state ────────────────────────────────────────────────────────────
_TUI_SECTION="production"   # current section id
_TUI_PANE_FOCUS="sidebar"   # sidebar | pane — controls where arrow keys go
_TUI_TARGET="prod-pi"        # default Pi target
_TUI_FILTER=""               # current /filter
_TUI_ALERT=""                # transient flash message
_TUI_REFRESH_INTERVAL=10     # background status probe cadence (seconds)
_TUI_RUNNING=1
_TUI_ROWS=24
_TUI_COLS=80
_TUI_LEFT_W=20

# Pod-list state (Production / Diagnostics sections). Populated during
# render; consumed by j/k/Enter in the section key handler.
_TUI_POD_LIST=()
_TUI_POD_INDEX=0

# Status-bar cache (refreshed by _tui_status_refresh)
_TUI_STATUS_MIMIR=""
_TUI_STATUS_PI=""
_TUI_STATUS_COMPOSE=""
_TUI_STATUS_CLUSTER=""

# Section registry. Keys are letter shortcuts; values are { id, label }.
# Order matters — controls left-pane render order + Tab cycle.
_TUI_SECTIONS=(
    "c:compose:Compose"
    "g:provisioning:Provisioning"
    "p:production:Production"
    "v:vault:Secrets"
    "u:users:Users"
    "n:network:Network/TLS"
    "s:settings:Settings"
    "d:diagnostics:Diagnostics"
)

# ── terminal lifecycle ──────────────────────────────────────────────
_tui_init() {
    _tui_alt_buffer
    _tui_hide_cursor
    # `-icanon` makes single-byte reads return immediately (no \n wait).
    # Do NOT set `time 0 min 0` — that turns reads non-blocking, which
    # makes every keypress arrive as an empty string and the dispatcher
    # silently drops it. Letting `read -t N` handle the timeout instead.
    # `-isig` disables Ctrl+C/Ctrl+Z signal generation so those keys
    # arrive as bytes (\x03 / \x1a) and the dispatcher can handle them
    # the same way as `q` and `Esc` instead of killing the UI mid-paint.
    # `-icrnl` stops the kernel translating Enter (\r) into \n on input.
    # Without this, `read -n1` sees \n, treats it as its line delimiter,
    # and returns an EMPTY string for the keystroke — submission looked
    # like a no-op and Enter never reached the prompt's `break`.
    stty -echo -icanon -isig -icrnl 2>/dev/null || true
    trap '_tui_cleanup' EXIT TERM
    trap '_tui_resize' WINCH
    _tui_resize
    _tui_status_start
    _TUI_FORCE_FULL=1   # first frame must paint the whole alt-screen
}

_tui_cleanup() {
    _tui_status_stop
    _tui_show_cursor
    _tui_main_buffer
    stty sane 2>/dev/null || true
    trap - EXIT INT TERM WINCH
}

_tui_resize() {
    local sz; sz=$(stty size 2>/dev/null || printf '24 80')
    _TUI_ROWS=${sz% *}; _TUI_COLS=${sz#* }
    [[ "$_TUI_ROWS" -lt 20 ]] && _TUI_ROWS=20
    [[ "$_TUI_COLS" -lt 80 ]] && _TUI_COLS=80
    _TUI_FORCE_FULL=1   # geometry change ⇒ repaint everything
}

# Status-row probes run in a background process that writes a single
# cache file every 10s. The render path only reads the cache (cheap),
# so a missing Pi or stalled docker daemon can't freeze a keypress.
_TUI_STATUS_CACHE=""        # set by _tui_init
_TUI_STATUS_PID=""          # background updater PID
_TUI_STATUS_FIFO_LAST=0     # last successful read mtime

_tui_status_cache_path() {
    local d
    if [[ -d "/run/user/${UID:-$(id -u)}" ]]; then
        d="/run/user/${UID:-$(id -u)}/hermod"
    else
        d="${XDG_CACHE_HOME:-$HOME/.cache}/hermod/session"
    fi
    mkdir -p "$d" 2>/dev/null
    chmod 0700 "$d" 2>/dev/null
    printf '%s/tui-status.cache' "$d"
}

# Single probe pass. Writes "field=value" lines to stdout — caller
# redirects into the cache atomically.
_tui_status_probe_once() {
    local now; now=$(date +%s)

    local cache_dir; cache_dir="$(_mimir_cache_dir 2>/dev/null)"
    local n=0 f m
    if [[ -n "$cache_dir" ]]; then
        for f in "$cache_dir"/*.unlocked; do
            [[ -f "$f" ]] || continue
            m=$(stat -c %Y "$f" 2>/dev/null || stat -f %m "$f" 2>/dev/null || echo 0)
            (( now - m <= HERMOD_MIMIR_TTL )) && n=$((n+1))
        done
    fi
    printf 'mimir_n=%s\n' "$n"

    local pi_host="${HERMOD_PI_HOST:-${PI_SSH_HOST:-}}"
    local pi_ok=0
    if [[ -n "$pi_host" && "$pi_host" != PLACEHOLDER_* ]]; then
        if timeout 1 bash -c "exec 3<>/dev/tcp/$pi_host/22" 2>/dev/null; then
            pi_ok=1
        fi
    fi
    printf 'pi_ok=%s\npi_host=%s\n' "$pi_ok" "$pi_host"

    local compose; compose="$(_compose_cmd 2>/dev/null)"
    if [[ -n "$compose" ]]; then
        local up total
        up=$(cd "$REPO_ROOT" 2>/dev/null && timeout 2 $compose ps --status running -q 2>/dev/null | wc -l | tr -d ' ' || echo 0)
        total=$(cd "$REPO_ROOT" 2>/dev/null && timeout 2 $compose ps -aq 2>/dev/null | wc -l | tr -d ' ' || echo 0)
        printf 'compose_up=%s\ncompose_total=%s\n' "${up:-0}" "${total:-0}"
    else
        printf 'compose_up=0\ncompose_total=0\n'
    fi

    printf 'updated=%s\n' "$now"
}

# Background updater loop. Writes the cache atomically (tmp + mv) every
# $_TUI_REFRESH_INTERVAL seconds. Exits when its parent dies.
_tui_status_loop() {
    local cache="$1" interval="$2" parent_pid="$3"
    while kill -0 "$parent_pid" 2>/dev/null; do
        local tmp="$cache.tmp.$$"
        _tui_status_probe_once >"$tmp" 2>/dev/null && mv "$tmp" "$cache" 2>/dev/null
        rm -f "$tmp"
        sleep "$interval"
    done
}

_tui_status_start() {
    _TUI_STATUS_CACHE="$(_tui_status_cache_path)"
    # First-pass synchronous so the very first frame has data.
    _tui_status_probe_once >"$_TUI_STATUS_CACHE" 2>/dev/null
    # Background loop. Detach from job control so it doesn't print
    # "Done" messages on quit; nohup-style.
    ( _tui_status_loop "$_TUI_STATUS_CACHE" "$_TUI_REFRESH_INTERVAL" $$ ) </dev/null >/dev/null 2>&1 &
    _TUI_STATUS_PID=$!
    disown "$_TUI_STATUS_PID" 2>/dev/null || true
}

_tui_status_stop() {
    [[ -n "$_TUI_STATUS_PID" ]] && kill "$_TUI_STATUS_PID" 2>/dev/null || true
    _TUI_STATUS_PID=""
}

# Reader: parses the cache and updates the colored status fragments.
# Cheap; safe to call every render.
_tui_status_refresh() {
    [[ -f "$_TUI_STATUS_CACHE" ]] || { _tui_status_default_fragments; return; }
    local mimir_n=0 pi_ok=0 pi_host="" compose_up=0 compose_total=0 updated=0
    local line key val
    while IFS='=' read -r key val; do
        case "$key" in
            mimir_n)        mimir_n="$val" ;;
            pi_ok)          pi_ok="$val" ;;
            pi_host)        pi_host="$val" ;;
            compose_up)     compose_up="$val" ;;
            compose_total)  compose_total="$val" ;;
            updated)        updated="$val" ;;
        esac
    done <"$_TUI_STATUS_CACHE"

    if (( mimir_n > 0 )); then
        _TUI_STATUS_MIMIR="${_TUI_FG_GREEN}[+]${_TUI_RESET} vault(${mimir_n})"
    else
        _TUI_STATUS_MIMIR="${_TUI_FG_GREY}[-] vault${_TUI_RESET}"
    fi

    _TUI_STATUS_PI_OK="$pi_ok"
    if [[ -z "$pi_host" || "$pi_host" == PLACEHOLDER_* ]]; then
        _TUI_STATUS_PI="${_TUI_FG_GREY}[-] pi${_TUI_RESET}"
    elif [[ "$pi_ok" == "1" ]]; then
        _TUI_STATUS_PI="${_TUI_FG_GREEN}[+]${_TUI_RESET} pi"
    else
        _TUI_STATUS_PI="${_TUI_FG_RED}[X]${_TUI_RESET} pi"
    fi

    if (( compose_total == 0 )); then
        _TUI_STATUS_COMPOSE="${_TUI_FG_GREY}[-] compose${_TUI_RESET}"
    elif [[ "$compose_up" == "$compose_total" ]]; then
        _TUI_STATUS_COMPOSE="${_TUI_FG_GREEN}[+]${_TUI_RESET} compose ${compose_up}/${compose_total}"
    else
        _TUI_STATUS_COMPOSE="${_TUI_FG_YELLOW}[!]${_TUI_RESET} compose ${compose_up}/${compose_total}"
    fi

    if [[ "$_TUI_SECTION" == "production" ]]; then
        if [[ "$_TUI_STATUS_PI_OK" == "1" ]]; then
            _TUI_STATUS_CLUSTER="${_TUI_FG_GREEN}[+]${_TUI_RESET} ${_TUI_TARGET}"
        else
            _TUI_STATUS_CLUSTER="${_TUI_FG_GREY}[-] ${_TUI_TARGET}${_TUI_RESET}"
        fi
    else
        _TUI_STATUS_CLUSTER=""
    fi
}

_tui_status_default_fragments() {
    _TUI_STATUS_MIMIR="${_TUI_FG_GREY}[-] vault${_TUI_RESET}"
    _TUI_STATUS_PI="${_TUI_FG_GREY}[?] pi${_TUI_RESET}"
    _TUI_STATUS_COMPOSE="${_TUI_FG_GREY}[?] compose${_TUI_RESET}"
    _TUI_STATUS_CLUSTER=""
    _TUI_STATUS_PI_OK=0
}

# Render the whole frame. We don't clear-screen between frames anymore.
# Instead each row writer emits "\033[K" (clear-to-EOL) BEFORE painting
# its row, which wipes any stale tail from a longer previous frame
# without leaving a blank gap visible to the operator. Only force_full
# (section change, target change, resize, popup dismissal) warrants a
# whole-pane clear.
_tui_render() {
    _tui_status_refresh
    local force_full=0
    if [[ "${_TUI_FORCE_FULL:-0}" == "1" ]]; then
        _tui_clear
        _TUI_FORCE_FULL=0
        force_full=1
    fi
    _tui_render_status
    _tui_render_sections
    # The wholesale pane wipe is what flashed on every refresh.
    # _tui_pane_write now clears its row before painting, so a steady
    # render keeps the pane cells stable; we only blank the whole pane
    # when something actually changed the layout (force_full set on
    # section change, target change, resize, popup dismissal). The
    # tradeoff: if the section's row count shrinks between two ticks
    # without force_full firing, one stale tail row may persist for
    # ~one refresh cycle until the section's render reaches it.
    if (( force_full )); then
        _tui_render_pane_clear
    fi
    _tui_render_content
    _tui_render_footer
    _tui_render_alert
}

# Wipe rows 3..ROWS-2 in the right pane so shrinking content doesn't
# leave stale lines. Cheap: just one cup + clear-line per row.
_tui_render_pane_clear() {
    local content_start=$(( _TUI_LEFT_W + 2 ))
    local r
    for (( r=3; r<_TUI_ROWS-1; r++ )); do
        _tui_cup "$r" "$content_start"
        printf '\033[K'
    done
}

_tui_render_status() {
    _tui_cup 1 1
    printf '\033[K'
    printf '%s%s Hermod %s %s   %s   %s   %s   %s' \
        "$_TUI_BOLD" "$_TUI_FG_GREEN" "$_TUI_RESET" \
        "$_TUI_DIM" "$_TUI_STATUS_MIMIR" "$_TUI_STATUS_PI" "$_TUI_STATUS_COMPOSE" "$_TUI_STATUS_CLUSTER" \
        2>/dev/null || true
    _tui_cup 2 1
    _tui_hr_with_t "$_TUI_LEFT_W"
}

_tui_hr() {
    local w="${1:-$_TUI_COLS}" i
    printf '%s' "$_TUI_FG_GREEN"
    for (( i=0; i<w; i++ )); do printf '─'; done
    printf '%s\n' "$_TUI_RESET"
}

# Horizontal rule with a T-junction at column $1 so the rule + the
# vertical separator below it meet cleanly. Used on row 2 (top rule)
# and row ROWS-1 (bottom rule, with `┴`).
_tui_hr_with_t() {
    local t_col="$1" i
    printf '%s' "$_TUI_FG_GREEN"
    for (( i=1; i<=_TUI_COLS; i++ )); do
        if (( i == t_col )); then printf '┬'
        else printf '─'
        fi
    done
    printf '%s\n' "$_TUI_RESET"
}

_tui_hr_with_b() {
    local t_col="$1" i
    printf '%s' "$_TUI_FG_GREEN"
    for (( i=1; i<=_TUI_COLS; i++ )); do
        if (( i == t_col )); then printf '┴'
        else printf '─'
        fi
    done
    printf '%s\n' "$_TUI_RESET"
}

_tui_render_sections() {
    local row=3
    local entry key id label
    for entry in "${_TUI_SECTIONS[@]}"; do
        IFS=':' read -r key id label <<<"$entry"
        _tui_cup "$row" 2
        if [[ "$id" == "$_TUI_SECTION" ]]; then
            printf '%s▶[%s] %-14s%s' "$_TUI_FG_GREEN$_TUI_BOLD" "$key" "$label" "$_TUI_RESET"
        else
            printf ' [%s] %-14s' "$key" "$label"
        fi
        row=$((row+1))
    done
    # Vertical separator between left + right pane — brand green so it
    # ties into the top + bottom T-junctions; extends down to one row
    # above the bottom rule so the action grid sits inside the box.
    local r
    for (( r=3; r<_TUI_ROWS-1; r++ )); do
        _tui_cup "$r" "$_TUI_LEFT_W"
        printf '%s│%s' "$_TUI_FG_GREEN" "$_TUI_RESET"
    done
}

_tui_render_content() {
    local handler="_tui_section_${_TUI_SECTION}"
    if type "$handler" >/dev/null 2>&1; then
        "$handler"
    else
        _tui_cup 4 "$((_TUI_LEFT_W+2))"
        printf '%s(no handler for section: %s)%s' "$_TUI_FG_RED" "$_TUI_SECTION" "$_TUI_RESET"
    fi
}

_tui_render_footer() {
    _tui_cup "$((_TUI_ROWS-1))" 1
    _tui_hr_with_b "$_TUI_LEFT_W"
    _tui_cup "$_TUI_ROWS" 1
    printf '\033[K'
    local hint
    if [[ -n "$_TUI_FILTER" ]]; then
        hint=" filter:'$_TUI_FILTER' · ESC:clear · /:edit · ?:help · q:quit"
    else
        hint=" ?:help · /:filter · ::cmd · Tab:next · sidebar:c g p v u n s d · q:quit"
    fi
    printf '%s%s%s' "$_TUI_FG_GREY" "$hint" "$_TUI_RESET"
}

_tui_render_alert() {
    [[ -z "$_TUI_ALERT" ]] && return 0
    local msg="$_TUI_ALERT"
    _TUI_ALERT=""
    # Overlay the footer line (one-shot; next render restores it).
    _tui_cup "$_TUI_ROWS" 1
    _tui_clear_line
    printf '%s %s %s' "$_TUI_FG_YELLOW$_TUI_INV" "$msg" "$_TUI_RESET"
}

_tui_alert() { _TUI_ALERT="$*"; }

# ── content rendering helper ─────────────────────────────────────────
# Writes lines into the right pane starting at row 3, column LEFT_W+2.
# Truncates at pane width so long messages can never wrap into the
# sidebar — that bug looked like the section "title was being eaten"
# when an action description was longer than the visible pane. Honours
# $_TUI_FILTER if set.
_tui_pane_write() {
    local row="$1"; shift
    local content_start=$(( _TUI_LEFT_W + 2 ))
    local pane_w=$(( _TUI_COLS - content_start ))
    (( pane_w < 1 )) && pane_w=1
    # Cup, then clear-to-EOL FIRST so any stale bytes (from a longer
    # previous tick on this same row) are gone before we paint. Doing
    # the clear before the print, not after, avoids the brief blank
    # flash on Production-section refreshes.
    _tui_cup "$row" "$content_start"
    printf '\033[K'
    local raw="$*"
    # Visible-length count: walk the string byte-by-byte, skipping CSI
    # sequences (\033[...<final>) so colored markup doesn't count toward
    # the column budget. Keeps long action descriptions from wrapping
    # into the sidebar.
    local i=0 vis=0 keep=0 ch
    while (( i < ${#raw} )); do
        ch="${raw:$i:1}"
        if [[ "$ch" == $'\033' && "${raw:$((i+1)):1}" == "[" ]]; then
            local j=$((i+2))
            while (( j < ${#raw} )); do
                local c2="${raw:$j:1}"
                if [[ "$c2" =~ [A-Za-z] ]]; then j=$((j+1)); break; fi
                j=$((j+1))
            done
            i=$j
            keep=$j
            continue
        fi
        vis=$((vis+1))
        i=$((i+1))
        keep=$i
        (( vis >= pane_w )) && break
    done
    if (( ${#raw} <= keep )); then
        printf '%s' "$raw"
    else
        printf '%s%s' "${raw:0:keep}" "$_TUI_RESET"
    fi
}

# Filterable list row. Skipped when $_TUI_FILTER is set and the
# string doesn't contain it. Used for pod listings, log lines —
# anything the operator wants to grep down.
_tui_pane_listrow() {
    local row="$1"; shift
    local raw="$*"
    if [[ -n "$_TUI_FILTER" && "$raw" != *"$_TUI_FILTER"* ]]; then
        return 0
    fi
    _tui_pane_write "$row" "$raw"
}

# ── shell-out helper ─────────────────────────────────────────────────
# Drops out of TUI mode for an interactive subcommand (logs tail,
# install run, etc.), runs the command, then prompts before re-rendering.
_tui_shellout() {
    _tui_cleanup
    printf '\n%s── %s ──%s\n' "$_TUI_FG_CYAN" "$*" "$_TUI_RESET"
    "$@"
    local rc=$?
    printf '\n%s[exit %d] press any key to return to TUI…%s' "$_TUI_FG_GREY" "$rc" "$_TUI_RESET"
    read -rsN1 </dev/tty || true
    _tui_init
}

_tui_confirm() {
    local prompt="$1"
    _tui_cup "$((_TUI_ROWS-2))" 1
    _tui_clear_line
    printf '%s%s [y/N] %s' "$_TUI_FG_YELLOW$_TUI_BOLD" "$prompt" "$_TUI_RESET"
    local k
    read -rsN1 k </dev/tty || true
    [[ "$k" == "y" || "$k" == "Y" ]]
}

# Popup overlay: capture the output of <cmd...> and show it in a
# centered scrollable modal. j/k or arrows scroll, q/ESC dismiss,
# Enter dismisses too. Used for short read-only ops (mimir status,
# doctor, secrets viewer, single-pod logs, kubectl describe) so we
# don't need to flip out of the alt-screen and back.
#
# Args:
#   $1   — title (rendered in border)
#   $2.. — command + args; stdout+stderr captured. Leave empty + pipe
#          via `_tui_popup_show "title" < <(cmd)` for inline subshell use.
_tui_popup_show() {
    local title="$1"; shift
    local body
    if (( $# > 0 )); then
        body=$("$@" 2>&1)
    else
        body=$(cat)
    fi
    local -a lines=()
    while IFS= read -r line; do
        lines+=("$line")
    done <<<"$body"
    local total="${#lines[@]}"

    # Box dims — 80% wide; height auto-fits content with sane min/max so
    # short popups (pi-doctor, mimir status) do not leave a giant empty
    # box below the last line.
    local box_w=$(( _TUI_COLS * 80 / 100 ))
    local box_h_max=$(( _TUI_ROWS * 70 / 100 ))
    local box_h=$(( total + 4 ))    # content + 2 borders + title row + footer row
    (( box_w < 60 )) && box_w=60
    (( box_h < 8 )) && box_h=8
    (( box_h > box_h_max )) && box_h=$box_h_max
    (( box_w > _TUI_COLS - 4 )) && box_w=$(( _TUI_COLS - 4 ))
    (( box_h > _TUI_ROWS - 4 )) && box_h=$(( _TUI_ROWS - 4 ))
    local box_x=$(( (_TUI_COLS - box_w) / 2 + 1 ))
    local box_y=$(( (_TUI_ROWS - box_h) / 2 + 1 ))
    local body_h=$(( box_h - 4 ))   # 2 borders + title + footer
    local scroll=0 max_scroll=$(( total - body_h ))
    (( max_scroll < 0 )) && max_scroll=0

    local k
    while true; do
        _tui_popup_paint "$box_y" "$box_x" "$box_h" "$box_w" "$title" "$scroll" "$body_h" lines
        read -rsN1 k </dev/tty || k=q
        case "$k" in
            q|""|$'\x03') break ;;
            $'\x1b')
                local k2; read -rsn1 -t 0.05 k2 </dev/tty || k2=""
                [[ -z "$k2" ]] && break    # bare ESC
                if [[ "$k2" == "[" ]]; then
                    local k3; read -rsn1 -t 0.05 k3 </dev/tty || k3=""
                    case "$k3" in
                        A) (( scroll > 0 )) && scroll=$((scroll-1)) ;;
                        B) (( scroll < max_scroll )) && scroll=$((scroll+1)) ;;
                        '5') read -rsn1 -t 0.05 k3 </dev/tty || true     # PgUp
                             scroll=$(( scroll - body_h )); (( scroll < 0 )) && scroll=0 ;;
                        '6') read -rsn1 -t 0.05 k3 </dev/tty || true     # PgDn
                             scroll=$(( scroll + body_h ))
                             (( scroll > max_scroll )) && scroll=$max_scroll ;;
                    esac
                fi
                ;;
            j) (( scroll < max_scroll )) && scroll=$((scroll+1)) ;;
            k) (( scroll > 0 )) && scroll=$((scroll-1)) ;;
            g) scroll=0 ;;
            G) scroll=$max_scroll ;;
            $'\r') break ;;
        esac
    done
    # Force full redraw of the underlying TUI so the popup's footprint clears.
    _tui_clear
    _tui_render
}

# Paint one frame of the popup. Args: top, left, h, w, title, scroll, body_h, lines-array-name.
_tui_popup_paint() {
    local top="$1" left="$2" h="$3" w="$4" title="$5" scroll="$6" body_h="$7"
    # shellcheck disable=SC2178
    local -n _lines="$8"
    local total="${#_lines[@]}"
    local inner_w=$(( w - 4 ))
    local i row line

    # First, wipe each row of the popup's vertical range edge-to-edge.
    # Without this the section content keeps showing in the columns
    # outside the box (bleed-through past the box edge).
    for (( i=0; i<h; i++ )); do
        _tui_cup "$(( top + i ))" 1
        printf '\033[K'
    done

    # Top border with title.
    _tui_cup "$top" "$left"
    printf '%s┌─ %s ' "$_TUI_FG_GREEN$_TUI_BOLD" "$title"
    local pad=$(( w - 5 - ${#title} ))
    (( pad < 0 )) && pad=0
    local j; for (( j=0; j<pad; j++ )); do printf '─'; done
    printf '┐%s' "$_TUI_RESET"

    # Body lines.
    for (( i=0; i<body_h; i++ )); do
        row=$(( top + 1 + i ))
        _tui_cup "$row" "$left"
        printf '%s│%s ' "$_TUI_FG_GREEN" "$_TUI_RESET"
        local idx=$(( scroll + i ))
        if (( idx < total )); then
            line="${_lines[$idx]}"
            # Truncate to box width.
            (( ${#line} > inner_w )) && line="${line:0:inner_w}"
            printf '%-*s' "$inner_w" "$line"
        else
            printf '%-*s' "$inner_w" ""
        fi
        printf ' %s│%s' "$_TUI_FG_GREEN" "$_TUI_RESET"
    done

    # Footer with scroll indicator + dismiss hint.
    _tui_cup "$(( top + h - 2 ))" "$left"
    printf '%s│%s' "$_TUI_FG_GREEN" "$_TUI_RESET"
    local hint="j/k:scroll  g/G:top/bot  q/ESC/Enter:close"
    if (( total > body_h )); then
        hint="$hint  [$(( scroll + 1 ))-$(( scroll + body_h < total ? scroll + body_h : total ))/$total]"
    fi
    local hpad=$(( w - 4 - ${#hint} ))
    (( hpad < 0 )) && hpad=0
    printf ' %s%s%s' "$_TUI_DIM" "$hint" "$_TUI_RESET"
    for (( j=0; j<hpad; j++ )); do printf ' '; done
    printf ' %s│%s' "$_TUI_FG_GREEN" "$_TUI_RESET"

    # Bottom border.
    _tui_cup "$(( top + h - 1 ))" "$left"
    printf '%s└' "$_TUI_FG_GREEN"
    for (( j=0; j<w-2; j++ )); do printf '─'; done
    printf '┘%s' "$_TUI_RESET"
}

# Render an action grid at the bottom of the right pane. Replaces the
# old single-line grey footer hint. Each entry is "key:label", and the
# grid lays them out 4-per-row by default, in bold so every option
# stays visible at a glance.
_tui_render_actions() {
    local content_start=$(( _TUI_LEFT_W + 2 ))
    local n=$# max_col=4
    local rows_needed=$(( (n + max_col - 1) / max_col ))
    local row=$(( _TUI_ROWS - 2 - rows_needed ))
    (( row < 4 )) && row=4
    _tui_cup "$row" "$content_start"
    printf '%sActions:%s' "$_TUI_BOLD" "$_TUI_RESET"
    row=$((row+1))
    _tui_cup "$row" "$content_start"
    local cell_w=$(( (_TUI_COLS - content_start) / max_col ))
    (( cell_w < 16 )) && cell_w=16
    local col=0 entry key label color
    for entry in "$@"; do
        key="${entry%%:*}"; label="${entry#*:}"
        color="$_TUI_FG_GREEN"
        [[ "$key" =~ ^[A-Z]$ ]] && color="$_TUI_FG_YELLOW"
        [[ "$key" =~ ^[0-9]$ ]] && color="$_TUI_FG_CYAN"
        printf '%s%s[%s]%s %-*s' "$_TUI_BOLD" "$color" "$key" "$_TUI_RESET" "$(( cell_w - 5 ))" "$label"
        col=$((col+1))
        if (( col >= max_col )); then
            col=0; row=$((row+1))
            _tui_cup "$row" "$content_start"
        fi
    done
}

# Centered PIN modal. Char-by-char read so ESC cancels and the input
# is masked with '•' as you type. Empty Enter is intentionally allowed
# (returned as empty string — mimir_* maps that to no-PIN mode). On
# cancel the function exits non-zero so callers can branch. The
# returned PIN goes to stdout; chatter goes to stderr (none currently).
_tui_pin_prompt() {
    local prompt="${1:-PIN}"
    local box_w=56 box_h=7
    (( box_w > _TUI_COLS - 4 )) && box_w=$(( _TUI_COLS - 4 ))
    local box_x=$(( (_TUI_COLS - box_w) / 2 + 1 ))
    local box_y=$(( (_TUI_ROWS - box_h) / 2 + 1 ))

    local pin="" k cancelled=0
    _tui_show_cursor
    # Wipe the popup's row range edge-to-edge once; the input loop
    # only repaints the box itself + the input row.
    local _wi
    for (( _wi=0; _wi<box_h; _wi++ )); do
        _tui_cup "$((box_y + _wi))" 1
        printf '\033[K'
    done
    while true; do
        local masked="" i
        for (( i=0; i<${#pin}; i++ )); do masked+="•"; done
        local input_w=$(( box_w - 6 ))
        (( ${#masked} > input_w )) && masked="${masked: -input_w}"

        _tui_cup "$box_y" "$box_x"
        printf '%s┌─ %s ' "$_TUI_FG_GREEN$_TUI_BOLD" "$prompt"
        local pad=$(( box_w - 5 - ${#prompt} )); (( pad < 0 )) && pad=0
        local j; for (( j=0; j<pad; j++ )); do printf '─'; done
        printf '┐%s' "$_TUI_RESET"

        _tui_cup "$((box_y+1))" "$box_x"
        printf '%s│%*s│%s' "$_TUI_FG_GREEN" "$(( box_w - 2 ))" "" "$_TUI_RESET"
        _tui_cup "$((box_y+2))" "$box_x"
        printf '%s│%s  > %s%-*s%s%s│%s' \
            "$_TUI_FG_GREEN" "$_TUI_RESET" \
            "$_TUI_FG_CYAN$_TUI_BOLD" "$input_w" "$masked" "$_TUI_RESET" \
            "$_TUI_FG_GREEN" "$_TUI_RESET"
        _tui_cup "$((box_y+3))" "$box_x"
        printf '%s│%*s│%s' "$_TUI_FG_GREEN" "$(( box_w - 2 ))" "" "$_TUI_RESET"
        _tui_cup "$((box_y+4))" "$box_x"
        printf '%s│%s  %slength: %s%-*s%s%s│%s' \
            "$_TUI_FG_GREEN" "$_TUI_RESET" \
            "$_TUI_DIM" "${#pin}" "$(( box_w - 14 ))" "" "$_TUI_RESET" \
            "$_TUI_FG_GREEN" "$_TUI_RESET"
        _tui_cup "$((box_y+5))" "$box_x"
        local hint="Enter = submit (empty = no PIN)   ESC = cancel"
        printf '%s│  %s%-*s%s│%s' \
            "$_TUI_FG_GREEN" "$_TUI_DIM" "$(( box_w - 4 ))" "$hint" "$_TUI_RESET" \
            "$_TUI_FG_GREEN" "$_TUI_RESET"
        _tui_cup "$((box_y+6))" "$box_x"
        printf '%s└' "$_TUI_FG_GREEN"
        for (( j=0; j<box_w-2; j++ )); do printf '─'; done
        printf '┘%s' "$_TUI_RESET"
        _tui_cup "$((box_y+2))" "$(( box_x + 5 + ${#masked} ))"

        IFS= read -rsN1 k </dev/tty || { cancelled=1; break; }
        case "$k" in
            $'\x1b')
                local k2; read -rsn1 -t 0.05 k2 </dev/tty || k2=""
                if [[ -z "$k2" ]]; then cancelled=1; break; fi
                if [[ "$k2" == "[" ]]; then
                    read -rsn1 -t 0.05 _k3 </dev/tty || true
                fi
                ;;
            $'\r'|$'\n') break ;;
            $'\x7f'|$'\b') pin="${pin%?}" ;;
            $'\x15') pin="" ;;
            $'\x03') cancelled=1; break ;;
            "") ;;
            *) pin+="$k" ;;
        esac
    done
    _tui_hide_cursor
    _TUI_FORCE_FULL=1
    if (( cancelled )); then _TUI_INPUT=""; return 1; fi
    _TUI_INPUT="$pin"
    return 0
}

# Centered single-field prompt with optional default value + hint line.
# Behaves like a generic text-input popup (visible echo, line edit by
# bash) sitting on top of the current frame. ESC cancels (returns 1),
# Enter submits. Empty submission picks up the default if non-empty.
#
# Usage:
#   answer=$(_tui_prompt_field "Path to config.yaml" "/etc/example.yaml" \
#                              "absolute path; tab-complete in the prompt") || return
# Result protocol for every prompt function:
#   * Sets _TUI_INPUT to the answer (or "" on cancel).
#   * Returns 0 on accept, 1 on cancel.
#   * Painting uses the function's real stdout — no subshell capture,
#     no /dev/tty redirect. Callers MUST NOT use $() to read the answer.
#
# Why globals: bash's $() forks a subshell, and a subshell's stdout is
# the capture pipe — not the controlling terminal. That's why earlier
# `pin=$(_tui_pin_prompt ...)` looked broken: the modal was being
# siphoned into the variable instead of drawn. A proper TUI keeps
# painting on the main process stdout and exposes results via state.
_TUI_INPUT=""

_tui_prompt_field() {
    local label="$1" default="${2:-}" extra_hint="${3:-}" gen_spec="${4:-}"
    # The popup paints two hint rows: the caller's custom hint (if any)
    # on the upper of the two, and the Enter/ESC navigation contract
    # always on the lower one. Earlier versions concatenated them onto
    # one row and any custom hint wider than the box wrapped past the
    # border, making the prompt look frozen.
    #
    # gen_spec is "style:len" (e.g. "base64:32", "alnum:24"); when set,
    # Ctrl-G replaces the input field with a fresh _tui_random_secret
    # of that shape so the operator can re-roll without exiting.
    local nav_hint="Enter = accept   ESC = cancel"
    [[ -n "$gen_spec" ]] && nav_hint+="   Ctrl-G = regen"
    local box_w=72 box_h=9
    (( box_w > _TUI_COLS - 4 )) && box_w=$(( _TUI_COLS - 4 ))
    local box_x=$(( (_TUI_COLS - box_w) / 2 + 1 ))
    local box_y=$(( (_TUI_ROWS - box_h) / 2 + 1 ))
    local input_w=$(( box_w - 6 ))
    local input_row=$(( box_y + box_h / 2 ))
    local input_col=$(( box_x + 4 ))

    # Wipe the popup's vertical range edge-to-edge so section text
    # behind the box doesn't bleed through past the box edge.
    local _wi
    for (( _wi=0; _wi<box_h; _wi++ )); do
        _tui_cup "$((box_y + _wi))" 1
        printf '\033[K'
    done

    _tui_cup "$box_y" "$box_x"
    printf '%s┌─ %s ' "$_TUI_FG_GREEN$_TUI_BOLD" "$label"
    local pad=$(( box_w - 5 - ${#label} )); (( pad < 0 )) && pad=0
    local j; for (( j=0; j<pad; j++ )); do printf '─'; done
    printf '┐%s' "$_TUI_RESET"
    for (( j=1; j<box_h-1; j++ )); do
        _tui_cup "$((box_y+j))" "$box_x"
        printf '%s│%*s│%s' "$_TUI_FG_GREEN" "$(( box_w - 2 ))" "" "$_TUI_RESET"
    done
    _tui_cup "$((box_y+box_h-1))" "$box_x"
    printf '%s└' "$_TUI_FG_GREEN"
    for (( j=0; j<box_w-2; j++ )); do printf '─'; done
    printf '┘%s' "$_TUI_RESET"
    if [[ -n "$default" ]]; then
        _tui_cup "$((box_y+1))" "$((box_x+2))"
        printf '%sdefault: %s%s%s' "$_TUI_DIM" "$_TUI_RESET" "$_TUI_FG_CYAN" "$default"
    fi
    # Custom hint on its own row (truncated to inner width to keep the
    # popup border intact), navigation contract on the row below it so
    # the operator always sees how to submit.
    local _inner_w=$(( box_w - 4 ))
    if [[ -n "$extra_hint" ]]; then
        local _vh="$extra_hint"
        (( ${#_vh} > _inner_w )) && _vh="${_vh:0:$((_inner_w-1))}…"
        _tui_cup "$((box_y+box_h-3))" "$((box_x+2))"
        printf '%s%s%s' "$_TUI_DIM" "$_vh" "$_TUI_RESET"
    fi
    _tui_cup "$((box_y+box_h-2))" "$((box_x+2))"
    printf '%s%s%s' "$_TUI_DIM" "$nav_hint" "$_TUI_RESET"
    _tui_cup "$input_row" "$((box_x+2))"
    printf '> '
    _tui_show_cursor

    local val="" k cancelled=0
    while true; do
        local visible="$val"
        (( ${#visible} > input_w )) && visible="${visible: -input_w}"
        _tui_cup "$input_row" "$input_col"
        printf '%-*s' "$input_w" "$visible"
        _tui_cup "$input_row" "$(( input_col + ${#visible} ))"

        IFS= read -rsN1 k </dev/tty || { cancelled=1; break; }
        case "$k" in
            $'\x1b')
                local k2; read -rsn1 -t 0.05 k2 </dev/tty || k2=""
                if [[ -z "$k2" ]]; then cancelled=1; break; fi
                if [[ "$k2" == "[" ]]; then
                    read -rsn1 -t 0.05 _k3 </dev/tty || true
                fi
                ;;
            $'\r'|$'\n') break ;;
            $'\x7f'|$'\b') val="${val%?}" ;;
            $'\x15') val="" ;;
            $'\x03') cancelled=1; break ;;
            $'\x07')
                if [[ -n "$gen_spec" ]]; then
                    local _gs="${gen_spec%%:*}" _gl="${gen_spec##*:}"
                    val="$(_tui_random_secret "$_gs" "$_gl")"
                fi
                ;;
            "") ;;
            *) val+="$k" ;;
        esac
    done
    _tui_hide_cursor
    _TUI_FORCE_FULL=1
    if (( cancelled )); then _TUI_INPUT=""; return 1; fi
    [[ -z "$val" && -n "$default" ]] && val="$default"
    _TUI_INPUT="$val"
    return 0
}

# Yes/no popup. Returns 0 on yes, 1 on no/cancel. No _TUI_INPUT —
# the rc IS the answer.
_tui_prompt_yesno() {
    local label="$1" default="${2:-n}"
    local hint="press y or n (Enter = $default, ESC = cancel)"
    # Render the prompt frame the same way _tui_prompt_field does, then
    # take a single keystroke instead of a full line. Confirmations are
    # one character of intent; forcing Enter after y/n made the TUI
    # appear frozen on long-running confirmations (e.g. `reset prod-pi`).
    local box_w=70 box_h=8
    (( box_w > _TUI_COLS - 4 )) && box_w=$(( _TUI_COLS - 4 ))
    local box_x=$(( (_TUI_COLS - box_w) / 2 + 1 ))
    local box_y=$(( (_TUI_ROWS - box_h) / 2 + 1 ))
    local _wi; for (( _wi=0; _wi<box_h; _wi++ )); do
        _tui_cup "$((box_y + _wi))" 1; printf '\033[K'
    done
    _tui_cup "$box_y" "$box_x"
    printf '%s┌─ %s ' "$_TUI_FG_GREEN$_TUI_BOLD" "$label"
    local pad=$(( box_w - 5 - ${#label} )); (( pad < 0 )) && pad=0
    local j; for (( j=0; j<pad; j++ )); do printf '─'; done
    printf '┐%s' "$_TUI_RESET"
    for (( j=1; j<box_h-1; j++ )); do
        _tui_cup "$((box_y+j))" "$box_x"
        printf '%s│%*s│%s' "$_TUI_FG_GREEN" "$(( box_w - 2 ))" "" "$_TUI_RESET"
    done
    _tui_cup "$((box_y+box_h-1))" "$box_x"
    printf '%s└' "$_TUI_FG_GREEN"
    for (( j=0; j<box_w-2; j++ )); do printf '─'; done
    printf '┘%s' "$_TUI_RESET"
    _tui_cup "$((box_y+1))" "$((box_x+2))"
    printf '%sdefault: %s%s%s' "$_TUI_DIM" "$_TUI_RESET" "$_TUI_FG_CYAN" "$default"
    _tui_cup "$((box_y+box_h-3))" "$((box_x+2))"
    printf '%s%s%s' "$_TUI_DIM" "$hint" "$_TUI_RESET"
    _tui_cup "$((box_y + box_h/2))" "$((box_x+2))"
    printf '> '
    _tui_show_cursor
    # Three exit states. yes=0 / no=1 / cancel=2. Callers that want to
    # bail the surrounding action on Esc (rather than fall through to a
    # second prompt as if the operator had pressed N) check $? == 2 and
    # short-circuit. Plain `if ! yesno` keeps working: cancel still
    # evaluates as not-yes.
    _TUI_INPUT=""
    local k rc=2
    while true; do
        read -rsN1 k </dev/tty || { rc=2; break; }
        case "$k" in
            y|Y) _TUI_INPUT="y"; rc=0; break ;;
            n|N) _TUI_INPUT="n"; rc=1; break ;;
            $'\r'|$'\n')
                if [[ "$default" =~ ^[yY]$ ]]; then _TUI_INPUT="y"; rc=0; else _TUI_INPUT="n"; rc=1; fi
                break ;;
            $'\x1b'|$'\x03'|q|Q) _TUI_INPUT="cancel"; rc=2; break ;;
        esac
    done
    _tui_hide_cursor
    _TUI_FORCE_FULL=1
    return $rc
}

# Single-choice picker. Pass labels as args. Sets _TUI_INPUT to the
# chosen label and returns 0; ESC sets it empty and returns 1.
_tui_prompt_choice() {
    local label="$1"; shift
    local -a choices=("$@")
    local n=${#choices[@]} i
    [[ $n -eq 0 ]] && { _TUI_INPUT=""; return 1; }

    local box_w=70 box_h=$(( n + 6 ))
    (( box_w > _TUI_COLS - 4 )) && box_w=$(( _TUI_COLS - 4 ))
    (( box_h > _TUI_ROWS - 4 )) && box_h=$(( _TUI_ROWS - 4 ))
    local box_x=$(( (_TUI_COLS - box_w) / 2 + 1 ))
    local box_y=$(( (_TUI_ROWS - box_h) / 2 + 1 ))

    # Wipe popup row range edge-to-edge so background can't bleed.
    local _wi
    for (( _wi=0; _wi<box_h; _wi++ )); do
        _tui_cup "$((box_y + _wi))" 1
        printf '\033[K'
    done

    _tui_cup "$box_y" "$box_x"
    printf '%s┌─ %s ' "$_TUI_FG_GREEN$_TUI_BOLD" "$label"
    local pad=$(( box_w - 5 - ${#label} )); (( pad < 0 )) && pad=0
    local j; for (( j=0; j<pad; j++ )); do printf '─'; done
    printf '┐%s' "$_TUI_RESET"
    for (( j=1; j<box_h-1; j++ )); do
        _tui_cup "$((box_y+j))" "$box_x"
        printf '%s│%*s│%s' "$_TUI_FG_GREEN" "$(( box_w - 2 ))" "" "$_TUI_RESET"
    done
    _tui_cup "$((box_y+box_h-1))" "$box_x"
    printf '%s└' "$_TUI_FG_GREEN"
    for (( j=0; j<box_w-2; j++ )); do printf '─'; done
    printf '┘%s' "$_TUI_RESET"

    for (( i=0; i<n; i++ )); do
        _tui_cup "$((box_y+2+i))" "$((box_x+3))"
        printf '%s%s[%d]%s %s' "$_TUI_BOLD" "$_TUI_FG_CYAN" "$((i+1))" "$_TUI_RESET" "${choices[$i]}"
    done
    _tui_cup "$((box_y+box_h-2))" "$((box_x+2))"
    printf '%s1-%d to pick   ESC = cancel%s' "$_TUI_DIM" "$n" "$_TUI_RESET"

    local k
    while true; do
        IFS= read -rsN1 k </dev/tty || { _TUI_FORCE_FULL=1; _TUI_INPUT=""; return 1; }
        case "$k" in
            $'\x1b') _TUI_FORCE_FULL=1; _TUI_INPUT=""; return 1 ;;
            [1-9])
                local idx=$((k-1))
                if (( idx < n )); then
                    _TUI_FORCE_FULL=1
                    _TUI_INPUT="${choices[$idx]}"
                    return 0
                fi
                ;;
        esac
    done
}

# Enumerate flash-safe block devices on this host. Excludes any NVMe
# (system drives), zram, dm-* and loop devices. Includes:
#   * SD/USB sticks    — TYPE=disk + (RM=1 OR TRAN=usb)
#   * built-in MMC     — name starts with mmcblk
# Output: one line per device, "<size>  <model>  /dev/<name>" (model is
# the lsblk MODEL column, often "USB Flash Disk" / "SD Card Reader" /
# the SD card vendor name; "(removable)" stub if MODEL is blank).
_tui_flash_devices() {
    # `lsblk -P` emits shell-safe `KEY="value"` pairs so the model
    # field is never confused with size by whitespace splitting.
    local line name size model rm tran type
    while IFS= read -r line; do
        # eval is safe here: the operator's lsblk only quotes the
        # column values; it never injects code.
        # shellcheck disable=SC2086
        eval "$line" 2>/dev/null || continue
        [[ "$TYPE" != "disk" ]] && continue
        case "$NAME" in
            nvme*|zram*|loop*|dm-*|sr*) continue ;;
            mmcblk*) ;;
            *) [[ "$RM" == "1" || "$TRAN" == "usb" ]] || continue ;;
        esac
        local m="$MODEL"; [[ -z "$m" ]] && m="(removable)"
        printf '%-8s  %-32s  /dev/%s\n' "$SIZE" "$m" "$NAME"
    done < <(lsblk -d -P -o NAME,SIZE,MODEL,RM,TRAN,TYPE 2>/dev/null)
}

# Pick a flashable device via the choice picker. Asks the operator to
# RE-TYPE the resulting /dev/<name> before returning it; that's the
# kill-switch that prevents an Enter-Enter spam from arming a flash.
# Returns 0 + prints `/dev/foo` on success, non-zero on cancel or
# device list empty.
_tui_pick_flash_device() {
    local -a devs=() ; local line
    while IFS= read -r line; do
        [[ -n "$line" ]] && devs+=("$line")
    done < <(_tui_flash_devices)
    if (( ${#devs[@]} == 0 )); then
        _tui_alert "no removable / SD / USB device found"
        _TUI_INPUT=""
        return 1
    fi

    _tui_prompt_choice "Pick the device to flash (size  model  path)" "${devs[@]}" || { _TUI_INPUT=""; return 1; }
    local picked="$_TUI_INPUT"
    local devpath="${picked##* }"   # last whitespace-separated token = /dev/...
    if [[ "$devpath" != /dev/sd* && "$devpath" != /dev/mmcblk* ]]; then
        _tui_alert "internal device $devpath blocked"
        _TUI_INPUT=""
        return 1
    fi

    # Re-type confirmation: must echo the path exactly to arm flash.
    _tui_prompt_field \
        "Confirm — TYPE the device path exactly to arm flash" \
        "" \
        "Picked: $picked. Type $devpath to confirm, anything else cancels." \
      || { _TUI_INPUT=""; return 1; }
    if [[ "$_TUI_INPUT" != "$devpath" ]]; then
        _tui_alert "confirmation mismatch — flash aborted"
        _TUI_INPUT=""
        return 1
    fi
    _TUI_INPUT="$devpath"
    return 0
}

# Two-prompt variant for init/rekey. Returns 0 + sets _TUI_INPUT to
# the matching PIN; non-zero with an alert when PINs disagree or the
# operator cancels (and _TUI_INPUT="").
_tui_pin_prompt_twice() {
    local label="${1:-Set vault PIN}"
    local p1 p2
    _tui_pin_prompt "$label" || return 1
    p1="$_TUI_INPUT"
    _tui_pin_prompt "Confirm"   || return 1
    p2="$_TUI_INPUT"
    if [[ "$p1" != "$p2" ]]; then
        _tui_alert "PINs did not match"
        _TUI_INPUT=""
        return 1
    fi
    _TUI_INPUT="$p1"
    return 0
}

# ── section: compose ─────────────────────────────────────────────────
_tui_section_compose() {
    local row=3
    _tui_pane_write "$row" "${_TUI_BOLD}Compose — local docker stack${_TUI_RESET}"
    row=$((row+2))
    local compose; compose="$(_compose_cmd 2>/dev/null)"
    if [[ -z "$compose" ]]; then
        _tui_pane_write "$row" "${_TUI_FG_RED}no compose impl on PATH (docker compose / docker-compose)${_TUI_RESET}"
    else
        # `compose ps -a` returns 0 with just the header row when nothing is
        # up. Capture the body separately so we can distinguish "stack is
        # down" from "compose itself broken". Old code printed "unavailable"
        # whenever the stack was down even though compose was fine.
        local _compose_out _compose_rc
        _compose_out="$(cd "$REPO_ROOT" && $compose ps -a --format 'table {{.Name}}\t{{.Status}}' 2>/dev/null)"; _compose_rc=$?
        if (( _compose_rc != 0 )); then
            _tui_pane_write "$row" "${_TUI_FG_YELLOW}compose ps failed (rc=$_compose_rc); is dockerd running?${_TUI_RESET}"
        elif [[ -z "$_compose_out" ]] || [[ "$(printf '%s\n' "$_compose_out" | wc -l)" -le 1 ]]; then
            _tui_pane_write "$row" "${_TUI_DIM}compose stack is down (use [1] to bring it up)${_TUI_RESET}"
        else
            local line
            while IFS= read -r line; do
                _tui_pane_listrow "$row" "$line"
                row=$((row+1))
                [[ "$row" -ge $((_TUI_ROWS-7)) ]] && break
            done <<<"$_compose_out"
        fi
    fi
    _tui_render_actions \
        "1:up" "2:down" "3:restart" "4:logs" \
        "5:creds" "6:status" "7:build" "8:pull" \
        "R:reset"
}

_tui_section_compose_keys() {
    local svc
    case "$1" in
        1) _tui_prompt_yesno "Bring the (UNSUPPORTED) compose stack up?" "y" \
             && _tui_shellout "$REPO_ROOT/hermod.sh" compose up ;;
        2) _tui_prompt_yesno "Stop compose stack? (volumes preserved)" "y" \
             && _tui_shellout "$REPO_ROOT/hermod.sh" compose down ;;
        3) _tui_prompt_field "Service to restart (empty = all)" "" \
                 "exact compose service name; ENTER alone restarts every container" || return 0
           svc="$_TUI_INPUT"
           if [[ -n "$svc" ]]; then
               _tui_shellout "$REPO_ROOT/hermod.sh" compose restart "$svc"
           else
               _tui_shellout "$REPO_ROOT/hermod.sh" compose restart
           fi ;;
        4) _tui_prompt_field "Service to tail logs for (empty = all)" "" "" || return 0
           svc="$_TUI_INPUT"
           if [[ -n "$svc" ]]; then
               _tui_shellout "$REPO_ROOT/hermod.sh" compose logs "$svc"
           else
               _tui_shellout "$REPO_ROOT/hermod.sh" compose logs
           fi ;;
        5) _tui_popup_show "Compose creds" "$REPO_ROOT/hermod.sh" compose creds ;;
        6) local compose; compose="$(_compose_cmd 2>/dev/null)"
           if [[ -n "$compose" ]]; then
               _tui_popup_show "Compose status" bash -c \
                 "cd '$REPO_ROOT' && $compose ps --format 'table {{.Name}}\t{{.Status}}\t{{.Ports}}'"
           else
               _tui_alert "no compose impl on PATH"
           fi ;;
        7) _tui_prompt_field "Service to rebuild (empty = all)" "" "" || return 0
           svc="$_TUI_INPUT"
           if [[ -n "$svc" ]]; then
               _tui_shellout "$REPO_ROOT/hermod.sh" compose build "$svc"
           else
               _tui_shellout "$REPO_ROOT/hermod.sh" compose build
           fi ;;
        8) _tui_prompt_yesno "Pull fresh images for every compose service?" "y" \
             && _tui_shellout "$REPO_ROOT/hermod.sh" compose pull ;;
        R) _tui_prompt_yesno "Compose reset wipes ALL volumes (postgres + nanomq + hermod data). Continue?" "n" \
             && _tui_shellout "$REPO_ROOT/hermod.sh" compose reset ;;
        *) return 1 ;;
    esac
}

# ── section: provisioning (greenfield Pi bring-up) ───────────────────
# Wraps the cloud-init image build / SD-flash / mDNS-wait / ansible
# bootstrap path. cmd_flash + cmd_wait_pi + cmd_provision are
# interactive (sudo prompt for dd, OS-image download progress, yes/no
# confirmation for the device write); _tui_shellout drops to plain
# shell so those prompts work normally and the TUI restores after.
_tui_section_provisioning() {
    local row=3
    _tui_pane_write "$row" "${_TUI_BOLD}Pi greenfield provisioning${_TUI_RESET}"
    row=$((row+2))
    _tui_pane_write "$row" "Greenfield (full SD-flash + bring-up):"
    row=$((row+1))
    _tui_pane_write "$row" "  [1] flash       build cloud-init image + write to SD card"
    row=$((row+1))
    _tui_pane_write "$row" "  [2] wait-pi     wait for first-boot mDNS announce"
    row=$((row+1))
    _tui_pane_write "$row" "  [3] provision   flash + wait + ansible bring-up (full)"
    row=$((row+2))
    _tui_pane_write "$row" "Existing Pi:"
    row=$((row+1))
    _tui_pane_write "$row" "  [4] pi-keys     authorize this host's SSH key on the Pi"
    row=$((row+1))
    _tui_pane_write "$row" "  [5] pi-status   inventory snapshot (uptime, microk8s, services)"
    row=$((row+1))
    _tui_pane_write "$row" "  [6] pi-doctor   deep host-side diagnostics"
    _tui_render_actions \
        "1:flash" "2:wait-pi" "3:provision" "4:pi-keys" \
        "5:pi-status" "6:pi-doctor" "U:pi-uninstall"
}

_tui_section_provisioning_keys() {
    local cfg dev hostname host_default
    case "$1" in
        1) # flash <config> [<device>] [--use-imager]
           _tui_prompt_field "Path to Pi config.yaml" \
                  "$REPO_ROOT/lib/pi-installer/config.example.yaml" \
                  "yaml describing hostname/network; ENTER picks the example" || return 0
           cfg="$_TUI_INPUT"
           [[ -f "$cfg" ]] || { _tui_alert "no such file: $cfg"; return 0; }
           _tui_pick_flash_device || return 0
           dev="$_TUI_INPUT"
           local use_imager_flag=""
           if _tui_prompt_yesno "Hand off to GUI rpi-imager instead of dd?" "n"; then
               use_imager_flag="--use-imager"
           fi
           _tui_shellout "$REPO_ROOT/hermod.sh" flash "$cfg" "$dev" $use_imager_flag
           ;;
        2) # wait-pi <hostname> [timeout] [--arp-only] [--re-pin]
           host_default="$(_tui_default_pi_hostname)"
           _tui_prompt_field "Pi hostname (mDNS / ssh)" "$host_default" \
                      "must match the host: line in your config.yaml; .local suffix optional" || return 0
           hostname="$_TUI_INPUT"
           local wait_extra=()
           if _tui_prompt_yesno "Skip mDNS and use ARP scan only? (use if mDNS doesn't work on your LAN)" "n"; then
               wait_extra+=(--arp-only)
           fi
           if _tui_prompt_yesno "Force re-pin SSH host key? (use after a deliberate reflash)" "n"; then
               wait_extra+=(--re-pin)
           fi
           _tui_shellout "$REPO_ROOT/hermod.sh" wait-pi "$hostname" "${wait_extra[@]}"
           ;;
        3) # provision = ansible host bring-up only (microk8s, snaps, USB,
           # base packages incl. podman + bluez). After this completes the
           # Pi is ready; switch to Production section and press [1] install
           # to deploy the Hermod stack (build images on Pi, kustomize apply,
           # secret seeding, rollout). Splitting the two keeps each tool
           # authoritative for its layer.
           host_default="$(_tui_default_pi_hostname)"
           _tui_prompt_field "Pi hostname for ansible bring-up" "$host_default" \
                      "matches the host: line in your config.yaml" || return 0
           hostname="$_TUI_INPUT"
           _tui_shellout "$REPO_ROOT/lib/pi-installer/hermod-pi" deploy "$hostname"
           ;;
        4) _tui_popup_show "Pi keypairs (~/.hermod-pi/keys/)" "$REPO_ROOT/hermod.sh" pi-keys ;;
        5) host_default="$(_tui_default_pi_hostname)"
           _tui_prompt_field "Pi hostname for status check" "$host_default" "mDNS name without .local" || return 0
           hostname="$_TUI_INPUT"
           _tui_popup_show "Pi status: $hostname" "$REPO_ROOT/hermod.sh" pi-status "$hostname" ;;
        6) _tui_popup_show "Pi installer doctor" "$REPO_ROOT/hermod.sh" pi-doctor ;;
        U) host_default="$(_tui_default_pi_hostname)"
           _tui_prompt_field "Pi hostname to UNINSTALL" "$host_default" \
                      "DESTRUCTIVE — wipes microk8s + Hermod state on the Pi" || return 0
           hostname="$_TUI_INPUT"
           _tui_prompt_yesno "Really wipe Hermod from $hostname? Type Y to confirm." "n" || return 0
           _tui_shellout "$REPO_ROOT/hermod.sh" pi-uninstall "$hostname" ;;
        *) return 1 ;;
    esac
}

# Best-guess default hostname for the Pi prompts. Reads the first
# .hosts.yml in $HOME/.hermod-pi/inventories/ and strips the suffix.
# Returns empty if no inventory exists.
_tui_default_pi_hostname() {
    local inv="$HOME/.hermod-pi/inventories"
    [[ -d "$inv" ]] || return 0
    local f
    for f in "$inv"/*.hosts.yml "$inv"/*.hosts.yaml; do
        [[ -f "$f" ]] || continue
        local base; base="$(basename "$f")"
        printf '%s' "${base%.hosts.*}"
        return 0
    done
}

# ── section: production ──────────────────────────────────────────────
_tui_section_production() {
    local row=3
    _tui_pane_write "$row" "${_TUI_BOLD}Production — ${_TUI_TARGET}${_TUI_RESET}"
    row=$((row+2))
    _TUI_POD_LIST=()
    if [[ "${_TUI_STATUS_PI_OK:-0}" != "1" ]]; then
        _tui_pane_write "$row" "${_TUI_FG_RED}Pi unreachable; cluster query skipped${_TUI_RESET}"
    else
        local line idx=0
        while IFS= read -r line; do
            local pod_name="${line%% *}"
            _TUI_POD_LIST+=("$pod_name")
            local marker="  "
            (( idx == _TUI_POD_INDEX )) && marker="${_TUI_FG_GREEN}${_TUI_BOLD}▶ ${_TUI_RESET}"
            _tui_pane_listrow "$row" "${marker}${line}"
            row=$((row+1))
            idx=$((idx+1))
            [[ "$row" -ge $((_TUI_ROWS-8)) ]] && break
        done < <("$REPO_ROOT/hermod.sh" status "$_TUI_TARGET" 2>/dev/null \
                 | awk '/^pod\// { sub(/pod\//, ""); printf "%-40s %-15s %s\n", $1, $3, $4 }')
        # Clamp selection if pods went away.
        local n=${#_TUI_POD_LIST[@]}
        if (( n > 0 )); then
            (( _TUI_POD_INDEX >= n )) && _TUI_POD_INDEX=$((n-1))
        else
            _TUI_POD_INDEX=0
        fi
        row=$((row+1))
        _tui_pane_write "$row" "${_TUI_FG_GREY}j/k = select pod   Enter = describe + logs (popup)${_TUI_RESET}"
    fi
    _tui_render_actions \
        "1:install" "2:update" "3:status" "4:kick" \
        "5:roll-jwks" "6:logs" "7:ensure-secrets" "T:target" \
        "K:secrets" "t:teardown" "D:redeploy" "P:reset-db" \
        "R:reset"
}

_tui_section_production_keys() {
    local pod deploy
    case "$1" in
        1) # install <target>
           _tui_prompt_yesno "Install Hermod stack on $_TUI_TARGET?" "n" \
             && _tui_shellout "$REPO_ROOT/hermod.sh" install "$_TUI_TARGET" ;;
        2) # update <target> [--rebuild <coord|lora2mqtt|all>]
           # Single-prompt flow. Empty input = plain fast-path update
           # (rsync + apply + roll coord). Otherwise the typed token
           # picks a rebuild scope; the CLI flag does the build before
           # the rollout.
           _tui_prompt_yesno "rsync + apply overlay on $_TUI_TARGET?" "y" || return 0
           _tui_prompt_field "Rebuild image first?" "" \
               "type 'coord', 'lora2mqtt', or 'all' to docker-build before applying; leave empty for plain update" \
               || return 0
           local svc="$_TUI_INPUT"
           if [[ -z "$svc" ]]; then
               _tui_shellout "$REPO_ROOT/hermod.sh" update "$_TUI_TARGET"
           elif [[ "$svc" == "coord" || "$svc" == "lora2mqtt" || "$svc" == "all" ]]; then
               _tui_shellout "$REPO_ROOT/hermod.sh" update "$_TUI_TARGET" --rebuild "$svc"
           else
               _tui_alert "invalid rebuild target '$svc' (use coord, lora2mqtt, or all)"
           fi ;;
        3) _tui_popup_show "Status: $_TUI_TARGET" "$REPO_ROOT/hermod.sh" status "$_TUI_TARGET" ;;
        4) # kick <target> [deployment]  — empty = roll all
           _tui_prompt_field "Deployment to kick" "" \
                    "leave empty to roll-restart EVERY hermod-prod deployment in order" || return 0
           deploy="$_TUI_INPUT"
           _tui_shellout "$REPO_ROOT/hermod.sh" kick "$_TUI_TARGET" "$deploy" ;;
        5) _tui_prompt_yesno "Roll Vault42 JWKS keys on $_TUI_TARGET? Sessions will not invalidate." "y" \
             && _tui_shellout "$REPO_ROOT/hermod.sh" roll-jwks "$_TUI_TARGET" ;;
        6) # logs <target> [pod] — empty pod = tail all. Three branches:
           #   yes (rc 0)    — tail the pod the cursor is on
           #   no  (rc 1)    — operator wants a different pod, prompt for it
           #   cancel (rc 2) — operator hit Esc/q to abandon, return to section
           if (( ${#_TUI_POD_LIST[@]} > 0 )); then
               pod="${_TUI_POD_LIST[$_TUI_POD_INDEX]}"
               _tui_prompt_yesno "Tail logs for selected pod ($pod)? No = pick a different pod / all" "y"
               case $? in
                   0) ;; # keep $pod
                   1) _tui_prompt_field "Pod name (empty = tail all pods)" "" \
                          "exact pod name or empty for the all-pods 30-line tail" || return 0
                      pod="$_TUI_INPUT" ;;
                   *) return 0 ;; # cancelled
               esac
           else
               _tui_prompt_field "Pod name (empty = tail all pods)" "" \
                      "exact pod name or empty for the all-pods 30-line tail" || return 0
               pod="$_TUI_INPUT"
           fi
           if [[ -n "$pod" ]]; then
               _tui_shellout "$REPO_ROOT/hermod.sh" logs "$_TUI_TARGET" "$pod"
           else
               _tui_shellout "$REPO_ROOT/hermod.sh" logs "$_TUI_TARGET"
           fi ;;
        7) _tui_prompt_yesno "Reconcile Secrets on $_TUI_TARGET (keep mode)?" "y" \
             && _tui_shellout "$REPO_ROOT/hermod.sh" ensure-secrets "$_TUI_TARGET" ;;
        T) _tui_target_cycle ;;
        j) _tui_pod_index_move 1 ;;
        k) _tui_pod_index_move -1 ;;
        $'\r'|$'\n') _tui_pod_drill ;;
        K) if mimir_load "${HERMOD_PROD_ENV:-$REPO_ROOT/hermod-prod.env}" --source 2>/dev/null; then
               _tui_popup_show "Secrets: $_TUI_TARGET" "$REPO_ROOT/hermod.sh" secrets "$_TUI_TARGET"
           else
               _tui_alert "Vault unlock failed; secrets not shown"
           fi ;;
        t) _tui_prompt_yesno "Teardown $_TUI_TARGET (cert Secrets preserved)?" "n" \
             && _tui_shellout "$REPO_ROOT/hermod.sh" teardown "$_TUI_TARGET" ;;
        D) _tui_prompt_yesno "Redeploy $_TUI_TARGET (teardown + install)?" "n" \
             && _tui_shellout "$REPO_ROOT/hermod.sh" redeploy "$_TUI_TARGET" ;;
        P) _tui_prompt_yesno "Drop vault + hermod DBs on $_TUI_TARGET? DATA LOSS." "n" \
             && HERMOD_RESET_CONFIRM=YES _tui_shellout "$REPO_ROOT/hermod.sh" reset-db "$_TUI_TARGET" ;;
        R) _tui_prompt_yesno "RESET $_TUI_TARGET — DESTRUCTIVE clean-slate. Continue?" "n" \
             && HERMOD_RESET_CONFIRM=YES _tui_shellout "$REPO_ROOT/hermod.sh" reset "$_TUI_TARGET" ;;
        *) return 1 ;;
    esac
}

_tui_pod_index_move() {
    local dir="$1" n=${#_TUI_POD_LIST[@]}
    (( n == 0 )) && return 0
    _TUI_POD_INDEX=$(( _TUI_POD_INDEX + dir ))
    (( _TUI_POD_INDEX < 0 ))   && _TUI_POD_INDEX=0
    (( _TUI_POD_INDEX >= n ))  && _TUI_POD_INDEX=$((n-1))
    return 0
}

# Drill into the highlighted pod: kubectl describe + last 30 log lines.
# Output is captured then shown in the scrollable popup so the
# operator can pgup/pgdn through 100s of lines without leaving the TUI.
_tui_pod_drill() {
    local n=${#_TUI_POD_LIST[@]}
    if (( n == 0 )); then
        _tui_alert "no pods to drill into"; return 0
    fi
    local pod="${_TUI_POD_LIST[$_TUI_POD_INDEX]}"
    [[ -z "$pod" ]] && { _tui_alert "no pod selected"; return 0; }
    _tui_popup_show "Pod: $pod" "$REPO_ROOT/hermod.sh" logs "$_TUI_TARGET" "$pod"
}

_tui_target_cycle() {
    local targets=(prod-pi prod-kind prod-pi-letsencrypt prod-pi-cloudflare-zero-trust)
    local i n=${#targets[@]} idx=0
    for ((i=0;i<n;i++)); do
        [[ "${targets[$i]}" == "$_TUI_TARGET" ]] && idx=$i
    done
    _TUI_TARGET="${targets[$(( (idx+1) % n ))]}"
    _TUI_FORCE_FULL=1
    _tui_alert "target → $_TUI_TARGET"
}

# ── section: vault (Mímir) ───────────────────────────────────────────
# Three states drive both render + action grid:
#   A "missing"   — no plaintext, no .mimir              → tell operator nothing to do
#   B "plaintext" — plaintext exists, no .mimir          → primary [1] = encrypt
#   C "locked"    — .mimir exists, no warm cache         → primary [1] = unlock
#   D "unlocked"  — .mimir exists, warm cache            → primary [1] = lock now
_tui_vault_state() {
    local file="$1"
    if [[ -f "$file.mimir" ]]; then
        local cache; cache="$(_mimir_cache_path "$file" 2>/dev/null)"
        if [[ -f "$cache" ]]; then
            local mtime; mtime=$(stat -c %Y "$cache" 2>/dev/null || stat -f %m "$cache" 2>/dev/null || echo 0)
            local now; now=$(date +%s)
            if (( now - mtime <= HERMOD_MIMIR_TTL )); then
                printf 'unlocked'; return
            fi
        fi
        printf 'locked'; return
    fi
    if [[ -f "$file" ]]; then
        printf 'plaintext'; return
    fi
    printf 'missing'
}

_tui_section_vault() {
    local env_file="${HERMOD_PROD_ENV:-$REPO_ROOT/hermod-prod.env}"
    local state; state="$(_tui_vault_state "$env_file")"
    local row=3
    _tui_pane_write "$row" "${_TUI_BOLD}Secrets — operator config encryption${_TUI_RESET}"
    row=$((row+2))
    _tui_pane_write "$row" "Target: $env_file"
    row=$((row+2))

    case "$state" in
        plaintext)
            _tui_pane_write "$row" "${_TUI_FG_YELLOW}[!] not encrypted${_TUI_RESET} — plaintext on disk"
            row=$((row+2))
            _tui_pane_write "$row" "Press ${_TUI_BOLD}[1]${_TUI_RESET} to encrypt with a PIN."
            row=$((row+1))
            _tui_pane_write "$row" "${_TUI_FG_GREY}empty PIN is allowed but rekey before any real deployment${_TUI_RESET}"
            row=$((row+1))
            _tui_pane_write "$row" "${_TUI_FG_GREY}the plaintext is shredded after encryption — copy it elsewhere first if you need a backup${_TUI_RESET}"
            _tui_render_actions \
                "1:encrypt" "I:rebuild from example" "5:status"
            ;;
        locked)
            local pin_required; pin_required="$(_mimir_meta_get "$env_file" "pin_required" 2>/dev/null || echo unknown)"
            _tui_pane_write "$row" "${_TUI_FG_GREEN}[+] encrypted${_TUI_RESET} — locked  (pin_required=$pin_required)"
            row=$((row+2))
            _tui_pane_write "$row" "Press ${_TUI_BOLD}[1]${_TUI_RESET} to unlock — keys cached for $((HERMOD_MIMIR_TTL/60)) min after."
            _tui_render_actions \
                "1:unlock" "4:rekey" "5:status" "X:decrypt"
            ;;
        unlocked)
            local cache; cache="$(_mimir_cache_path "$env_file" 2>/dev/null)"
            local mtime; mtime=$(stat -c %Y "$cache" 2>/dev/null || stat -f %m "$cache" 2>/dev/null || echo 0)
            local now; now=$(date +%s)
            local rem=$(( HERMOD_MIMIR_TTL - (now - mtime) ))
            local rem_disp; rem_disp="$((rem/60))m$((rem%60))s"
            _tui_pane_write "$row" "${_TUI_FG_GREEN}[+] unlocked${_TUI_RESET} — cache expires in $rem_disp"
            row=$((row+2))
            _tui_pane_write "$row" "Press ${_TUI_BOLD}[1]${_TUI_RESET} to lock immediately."
            _tui_render_actions \
                "1:lock" "4:rekey" "5:status" "X:decrypt"
            ;;
        missing)
            _tui_pane_write "$row" "${_TUI_FG_RED}[X] $env_file does not exist${_TUI_RESET}"
            row=$((row+2))
            _tui_pane_write "$row" "Press ${_TUI_BOLD}[I]${_TUI_RESET} to walk the hermod-prod.env.example wizard."
            row=$((row+1))
            _tui_pane_write "$row" "${_TUI_FG_GREY}auto-generates strong secrets, prompts for Pi SSH coords + seed passwords${_TUI_RESET}"
            _tui_render_actions "I:init from example" "5:status"
            ;;
    esac
}

_tui_section_vault_keys() {
    local env_file="${HERMOD_PROD_ENV:-$REPO_ROOT/hermod-prod.env}"
    local state; state="$(_tui_vault_state "$env_file")"
    case "$1" in
        1)  case "$state" in
                plaintext) _tui_vault_action_encrypt "$env_file" ;;
                locked)    _tui_vault_action_unlock  "$env_file" ;;
                unlocked)  mimir_lock "$env_file"; _tui_alert "vault locked" ;;
                missing)   _tui_alert "no env file at $env_file" ;;
            esac ;;
        4)  [[ "$state" == "plaintext" || "$state" == "missing" ]] \
                && { _tui_alert "rekey only valid on encrypted vaults"; return 0; }
            _tui_vault_action_rekey "$env_file" ;;
        5)  _tui_popup_show "Vault status" bash -c \
              "source '$REPO_ROOT/lib/mimir.sh' && HERMOD_MIMIR_QUIET=1 mimir_status" ;;
        X)  _tui_vault_action_decrypt "$env_file" "$state" ;;
        I)  [[ "$state" == "locked" || "$state" == "unlocked" ]] \
                && { _tui_alert "decrypt or rekey first; init only valid on missing/plaintext"; return 0; }
            _tui_vault_action_init_from_example "$env_file" ;;
        *) return 1 ;;
    esac
}

# openssl-backed random secret. style ∈ {base64, alnum}; len = byte budget.
# base64 gives raw entropy bytes encoded; alnum strips +/= so postgres /
# bash one-liners that paste the value don't need quoting acrobatics.
_tui_random_secret() {
    local style="${1:-base64}" len="${2:-24}"
    case "$style" in
        base64) openssl rand -base64 "$len" 2>/dev/null | tr -d '\n' ;;
        alnum)  openssl rand -base64 $((len*2)) 2>/dev/null | tr -d '\n+/=' | head -c "$len" ;;
        *) return 1 ;;
    esac
}

# Walk hermod-prod.env.example, prompt for the values that need a human
# decision (Pi SSH coords + seed-account passwords), auto-generate every
# infrastructure secret, write the result with chmod 0600. Offers to
# chain into mimir encryption immediately after.
#
# The schema mirrors hermod-prod.env.example by hand: when a new
# HERMOD_* variable is added there, also add it to the heredoc below.
_tui_vault_action_init_from_example() {
    local env_file="$1"
    local example="$REPO_ROOT/hermod-prod.env.example"

    if [[ ! -f "$example" ]]; then
        _tui_alert "missing $example"; return 0
    fi
    if [[ -f "$env_file" ]]; then
        _tui_prompt_yesno "$env_file already exists — overwrite?" "n" || return 0
    fi

    # Step 1 — Pi SSH coords. Ask for the hostname first because the
    # SSH key file name and the default mDNS reach are both derived from
    # it. Then ask for the SSH host (mDNS name OR IP); the IP can be left
    # at <hostname>.local and let wait-pi resolve it (mDNS or ARP) at
    # runtime, or set to a literal IP if mDNS isn't viable on the LAN.
    _tui_prompt_field "Pi hostname" "hermod-edge-01" \
        "matches the host: line in your Pi config.yaml" \
        || { _tui_alert "init cancelled"; return 0; }
    local pi_hostname="$_TUI_INPUT"

    local key_default="$HOME/.hermod-pi/keys/${pi_hostname}.key"
    _tui_prompt_field "Pi SSH key path" "$key_default" \
        "ed25519 private key written by hermod-pi flash" \
        || { _tui_alert "init cancelled"; return 0; }
    local pi_key="$_TUI_INPUT"

    _tui_prompt_field "Pi SSH host (IP or mDNS .local)" "${pi_hostname}.local" \
        "leave as .local for runtime mDNS/ARP resolve, or paste a literal IP" \
        || { _tui_alert "init cancelled"; return 0; }
    local pi_host="$_TUI_INPUT"

    # Step 2 — secret-generation strategy. Fast path = accept generated
    # randoms for every infra password / Vault42 key. Slow path walks
    # each one so the operator can paste pre-generated values.
    local auto=0
    if _tui_prompt_yesno "Auto-generate every infrastructure secret with strong randoms? Recommended." "y"; then
        auto=1
    fi

    local mqtt_pw pg_pw nanomq_admin_pw v42_master v42_hmac v42_signing v42_db_mig v42_db_app
    if (( auto )); then
        mqtt_pw="$(_tui_random_secret alnum 24)"
        pg_pw="$(_tui_random_secret alnum 24)"
        nanomq_admin_pw="$(_tui_random_secret alnum 24)"
        v42_master="$(_tui_random_secret base64 32)"
        v42_hmac="$(_tui_random_secret base64 32)"
        # Vault42's signing_key is asymmetric (RS256 JWT) — needs a real
        # PEM-encoded RSA private key, NOT random bytes. The vault42
        # binary refuses to start with "no PEM block found" otherwise.
        v42_signing="$(openssl genrsa 2048 2>/dev/null)"
        v42_db_mig="$(_tui_random_secret alnum 24)"
        v42_db_app="$(_tui_random_secret alnum 24)"
    else
        local pairs=(
            "HERMOD_MQTT_PASSWORD|alnum|24|mqtt_pw"
            "HERMOD_NANOMQ_ADMIN_PASSWORD|alnum|24|nanomq_admin_pw"
            "HERMOD_PG_PASSWORD|alnum|24|pg_pw"
            "HERMOD_VAULT42_MASTER_KEY|base64|32|v42_master"
            "HERMOD_VAULT42_HMAC_SECRET|base64|32|v42_hmac"
            "HERMOD_VAULT42_DB_MIG_PASSWORD|alnum|24|v42_db_mig"
            "HERMOD_VAULT42_DB_APP_PASSWORD|alnum|24|v42_db_app"
        )
        local row
        for row in "${pairs[@]}"; do
            IFS='|' read -r vname style blen target <<<"$row"
            local sugg; sugg="$(_tui_random_secret "$style" "$blen")"
            _tui_prompt_field "$vname" "$sugg" "Enter accepts the generated value" "$style:$blen" \
                || { _tui_alert "init cancelled"; return 0; }
            printf -v "$target" '%s' "$_TUI_INPUT"
        done
        # Signing key is asymmetric — auto-generate (multi-line PEM
        # doesn't fit in the prompt UI) and announce so the operator
        # knows it's set even when they walked the slow path.
        v42_signing="$(openssl genrsa 2048 2>/dev/null)"
        _tui_alert "HERMOD_VAULT42_SIGNING_KEY auto-generated as RSA-2048 PEM"
    fi

    # Step 3 — first-login seed account passwords. Default is a random
    # alnum-16 the operator can accept, but they often want something
    # memorable for the first dashboard login (rotate immediately after).
    local viewer_pw user_pw operator_pw
    _tui_prompt_field "viewer@hermod.local password" \
        "$(_tui_random_secret alnum 16)" \
        "first-login only; rotate via dashboard" "alnum:16" \
        || { _tui_alert "init cancelled"; return 0; }
    viewer_pw="$_TUI_INPUT"
    _tui_prompt_field "user@hermod.local password" \
        "$(_tui_random_secret alnum 16)" \
        "first-login only; rotate via dashboard" "alnum:16" \
        || { _tui_alert "init cancelled"; return 0; }
    user_pw="$_TUI_INPUT"
    _tui_prompt_field "operator@hermod.local password" \
        "$(_tui_random_secret alnum 16)" \
        "first-login only (operator+admin); rotate via dashboard" "alnum:16" \
        || { _tui_alert "init cancelled"; return 0; }
    operator_pw="$_TUI_INPUT"

    # Write the env file. Schema mirrors hermod-prod.env.example.
    cat > "$env_file" <<EOF
# Generated by hermod.sh TUI on $(date -u +%Y-%m-%dT%H:%M:%SZ).
# Treat as a credential bundle: chmod 0600, gitignored. Encrypt via the
# Secrets section [1] before backing up to anywhere shared.

# ── Pi SSH coordinates ────────────────────────────────────────────────
HERMOD_PI_SSH_HOST=$pi_host
HERMOD_PI_SSH_KEY=$pi_key

# ── MQTT broker credential (shared by coord + every translator) ───────
HERMOD_MQTT_USERNAME=hermod-service
HERMOD_MQTT_PASSWORD=$mqtt_pw

# ── NanoMQ HTTP admin (broker management API on :8081) ────────────────
HERMOD_NANOMQ_ADMIN_USERNAME=admin
HERMOD_NANOMQ_ADMIN_PASSWORD=$nanomq_admin_pw

# ── Postgres (hermod app DB user) ─────────────────────────────────────
HERMOD_PG_PASSWORD=$pg_pw

# ── Vault42 crypto material (256-bit base64 each) ─────────────────────
HERMOD_VAULT42_MASTER_KEY=$v42_master
HERMOD_VAULT42_HMAC_SECRET=$v42_hmac
HERMOD_VAULT42_SIGNING_KEY=$v42_signing

# ── Vault42 DB role passwords (vault_mig is also postgres master) ─────
HERMOD_VAULT42_DB_MIG_PASSWORD=$v42_db_mig
HERMOD_VAULT42_DB_APP_PASSWORD=$v42_db_app

# ── Vault42 first-boot seed accounts (rotate post-login) ──────────────
HERMOD_VAULT42_VIEWER_PASSWORD=$viewer_pw
HERMOD_VAULT42_USER_PASSWORD=$user_pw
HERMOD_VAULT42_OPERATOR_PASSWORD=$operator_pw
HERMOD_SEED_VIEWER=1
HERMOD_SEED_USER=1
EOF
    chmod 0600 "$env_file"
    _tui_alert "wrote $env_file"

    if _tui_prompt_yesno "Encrypt $env_file with mimir now? Recommended." "y"; then
        _tui_vault_action_encrypt "$env_file"
    fi
}

_tui_vault_action_encrypt() {
    local env_file="$1" pin
    _tui_pin_prompt_twice 'Set Vault PIN' || { _tui_alert "encrypt cancelled"; return 0; }
    pin="$_TUI_INPUT"
    if HERMOD_MIMIR_PIN_NEW="$pin" mimir_init "$env_file" 2>/dev/null; then
        HERMOD_MIMIR_PIN="$pin" mimir_unlock "$env_file" --force >/dev/null 2>&1
        _tui_alert "encrypted + cache warmed"
    else
        _tui_alert "encrypt failed"
    fi
}

_tui_vault_action_unlock() {
    local env_file="$1" pin
    _tui_pin_prompt 'Vault PIN' || { _tui_alert "unlock cancelled"; return 0; }
    pin="$_TUI_INPUT"
    if HERMOD_MIMIR_PIN="$pin" mimir_unlock "$env_file" --force 2>/dev/null; then
        _tui_alert "unlocked"
    else
        _tui_alert "unlock failed (wrong PIN?)"
    fi
}

_tui_vault_action_rekey() {
    local env_file="$1" pin
    _tui_pin_prompt_twice 'New Vault PIN' || { _tui_alert "rekey cancelled"; return 0; }
    pin="$_TUI_INPUT"
    if HERMOD_MIMIR_PIN_NEW="$pin" mimir_rekey "$env_file" 2>/dev/null; then
        _tui_alert "rekeyed"
    else
        _tui_alert "rekey failed (wrong current PIN?)"
    fi
}

# Decrypt back to plaintext + remove .mimir/.meta. The decrypted blob
# lands at the original path; the encrypted file is removed; the meta
# is removed. Operator is back to pre-init state, plaintext on disk.
_tui_vault_action_decrypt() {
    local env_file="$1" state="$2"
    if [[ "$state" != "locked" && "$state" != "unlocked" ]]; then
        _tui_alert "nothing to decrypt"; return 0
    fi
    _tui_confirm "decrypt $env_file back to plaintext (removes .mimir)?" || return 0
    local plain
    if ! plain="$(mimir_load "$env_file" 2>/dev/null)"; then
        _tui_alert "decrypt failed (need PIN unlock first)"; return 0
    fi
    printf '%s' "$plain" > "$env_file"
    chmod 0600 "$env_file"
    rm -f "$env_file.mimir" "$env_file.mimir.meta"
    mimir_lock "$env_file" >/dev/null 2>&1 || true
    _tui_alert "decrypted; .mimir removed"
}

# ── section: users ───────────────────────────────────────────────────
_tui_section_users() {
    local row=3
    _tui_pane_write "$row" "${_TUI_BOLD}Users — ${_TUI_TARGET}${_TUI_RESET}"
    row=$((row+2))
    # Roster from the local seed source (~/.hermod-pi/seed-users.json,
    # mimir-decrypted on the fly). Empty roster string when no seed has
    # been bootstrapped yet, in which case we hint at [I] to init.
    local users_file="${HERMOD_USERS_FILE:-$HOME/.hermod-pi/seed-users.json}"
    if [[ -f "$users_file" || -f "${users_file}.mimir" ]]; then
        _tui_pane_write "$row" "${_TUI_FG_GREY}Local seed file: $users_file${_TUI_RESET}"
        row=$((row+1))
        local line
        while IFS= read -r line; do
            _tui_pane_write "$row" "$line"
            row=$((row+1))
            [[ "$row" -ge $((_TUI_ROWS-9)) ]] && break
        done < <("$REPO_ROOT/hermod.sh" users list 2>/dev/null)
        row=$((row+1))
    else
        _tui_pane_write "$row" "${_TUI_FG_RED}No local seed file yet. Press [I] to bootstrap with 3 defaults.${_TUI_RESET}"
        row=$((row+2))
    fi
    _tui_pane_write "$row" "${_TUI_FG_GREY}Edits to the local seed take effect on the next install/update + first vault42 boot.${_TUI_RESET}"
    row=$((row+1))
    _tui_pane_write "$row" "${_TUI_FG_GREY}Changes to existing users need: reset-db + install (vault42 won't re-import existing).${_TUI_RESET}"
    _tui_render_actions \
        "I:init" "3:add" "4:remove" "5:set-role" "6:set-password" \
        "1:seed-users" "2:view-secrets" "P:change-password"
}

_tui_section_users_keys() {
    local explicit email role display password
    case "$1" in
        I) # users init — bootstrap the local seed with 3 defaults.
           if [[ -f "${HERMOD_USERS_FILE:-$HOME/.hermod-pi/seed-users.json}" \
                 || -f "${HERMOD_USERS_FILE:-$HOME/.hermod-pi/seed-users.json}.mimir" ]]; then
               _tui_alert "seed file already exists; refusing to overwrite"
               return 0
           fi
           _tui_shellout "$REPO_ROOT/hermod.sh" users init ;;
        3) # users add <email> <role> [display]: stdin-fed password.
           _tui_prompt_field "Email" "" "must contain '@', e.g. admin@hermod.local" || return 0
           email="$_TUI_INPUT"
           [[ -z "$email" || "$email" != *@* ]] && { _tui_alert "email empty or missing '@'"; return 0; }
           _tui_prompt_field "Role" "user" "viewer / user / operator / admin" || return 0
           role="$_TUI_INPUT"
           _tui_prompt_field "Display name" "${email%%@*}" "shown on the dashboard greeting" || return 0
           display="$_TUI_INPUT"
           _tui_prompt_field "Password" "" "INPUT VISIBLE; min 12 chars recommended" || return 0
           password="$_TUI_INPUT"
           [[ -z "$password" ]] && { _tui_alert "empty password aborted"; return 0; }
           # cmd_users add reads stdin twice (silent prompt + confirm).
           # Pipe the password twice so the popup is a one-shot.
           printf '%s\n%s\n' "$password" "$password" \
               | _tui_shellout "$REPO_ROOT/hermod.sh" users add "$email" "$role" "$display" ;;
        4) # users remove <email>
           _tui_prompt_field "Email to remove" "" "must match an existing entry" || return 0
           email="$_TUI_INPUT"
           [[ -z "$email" ]] && { _tui_alert "no email given"; return 0; }
           _tui_prompt_yesno "Remove $email from the local seed?" "n" \
               && _tui_shellout "$REPO_ROOT/hermod.sh" users remove "$email" ;;
        5) # users set-role <email> <role>
           _tui_prompt_field "Email" "" "must match an existing entry" || return 0
           email="$_TUI_INPUT"
           [[ -z "$email" ]] && { _tui_alert "no email given"; return 0; }
           _tui_prompt_field "New role" "user" "viewer / user / operator / admin" || return 0
           role="$_TUI_INPUT"
           _tui_shellout "$REPO_ROOT/hermod.sh" users set-role "$email" "$role" ;;
        6) # users set-password <email>: stdin-fed password.
           _tui_prompt_field "Email" "" "must match an existing entry" || return 0
           email="$_TUI_INPUT"
           [[ -z "$email" ]] && { _tui_alert "no email given"; return 0; }
           _tui_prompt_field "New password" "" "INPUT VISIBLE; min 12 chars recommended" || return 0
           password="$_TUI_INPUT"
           [[ -z "$password" ]] && { _tui_alert "empty password aborted"; return 0; }
           printf '%s\n%s\n' "$password" "$password" \
               | _tui_shellout "$REPO_ROOT/hermod.sh" users set-password "$email" ;;
        1) _tui_prompt_yesno "Reseed Vault42 viewer/user/operator on $_TUI_TARGET? Will drop+re-init the vault42 DB." "n" \
             && _tui_shellout "$REPO_ROOT/hermod.sh" seed-users "$_TUI_TARGET" ;;
        2) if mimir_load "${HERMOD_PROD_ENV:-$REPO_ROOT/hermod-prod.env}" --source 2>/dev/null; then
               _tui_popup_show "Secrets: $_TUI_TARGET" "$REPO_ROOT/hermod.sh" secrets "$_TUI_TARGET"
           else
               _tui_alert "Vault unlock failed"
           fi ;;
        P) # change-password <target> [explicit-password]
           if _tui_prompt_yesno "Use the SAME operator-supplied password for all three accounts? (No = generate three random ones)" "n"; then
               _tui_prompt_field "Explicit password to set" "" \
                          "this exact string lands on viewer/user/operator; min 12 chars recommended" || return 0
               explicit="$_TUI_INPUT"
               [[ -z "$explicit" ]] && { _tui_alert "empty password aborted"; return 0; }
               _tui_shellout "$REPO_ROOT/hermod.sh" change-password "$_TUI_TARGET" "$explicit"
           else
               _tui_shellout "$REPO_ROOT/hermod.sh" change-password "$_TUI_TARGET"
           fi ;;
        *) return 1 ;;
    esac
}

# ── section: network/TLS ─────────────────────────────────────────────
_tui_section_network() {
    local row=3
    _tui_pane_write "$row" "${_TUI_BOLD}Network & TLS — ${_TUI_TARGET}${_TUI_RESET}"
    row=$((row+2))
    if [[ "${_TUI_STATUS_PI_OK:-0}" != "1" ]]; then
        _tui_pane_write "$row" "${_TUI_FG_RED}Pi unreachable — TLS state unavailable${_TUI_RESET}"
    else
        local line
        while IFS= read -r line; do
            _tui_pane_write "$row" "$line"
            row=$((row+1))
            [[ "$row" -ge $((_TUI_ROWS-7)) ]] && break
        done < <("$REPO_ROOT/hermod.sh" cert "$_TUI_TARGET" status 2>/dev/null | head -16)
    fi
    _tui_render_actions \
        "1:cert-status" "2:request-cert" "3:tunnel-secret" "4:dns-secret" \
        "5:cert-show" "R:rotate-certs"
}

_tui_section_network_keys() {
    local host name from
    case "$1" in
        1) _tui_popup_show "Cert status: $_TUI_TARGET" \
             "$REPO_ROOT/hermod.sh" cert "$_TUI_TARGET" status ;;
        2) # cert <target> request <hostname>
           _tui_prompt_field "Public hostname for the cert" \
                  "${HERMOD_PUBLIC_HOSTNAME:-}" \
                  "must resolve to your tunnel/ingress; cert-manager DNS-01 needs CF API token applied first" || return 0
           host="$_TUI_INPUT"
           [[ -z "$host" ]] && { _tui_alert "no hostname given"; return 0; }
           _tui_shellout "$REPO_ROOT/hermod.sh" cert "$_TUI_TARGET" request "$host" ;;
        3) # tunnel-secret <target> [--from-file PATH]
           if _tui_prompt_yesno "Read TUNNEL_TOKEN from a file? (otherwise paste at prompt)" "n"; then
               _tui_prompt_field "Path to file containing the cloudflared token" "" \
                       "the file should contain ONLY the JWT or the full 'service install --token …' line" || return 0
               from="$_TUI_INPUT"
               [[ -z "$from" ]] && { _tui_alert "no path given"; return 0; }
               _tui_shellout "$REPO_ROOT/hermod.sh" tunnel-secret "$_TUI_TARGET" --from-file "$from"
           else
               _tui_shellout "$REPO_ROOT/hermod.sh" tunnel-secret "$_TUI_TARGET"
           fi ;;
        4) # dns-secret <target> [--from-file PATH]
           if _tui_prompt_yesno "Read CF DNS API token from a file? (otherwise paste at prompt)" "n"; then
               _tui_prompt_field "Path to file containing the Cloudflare API token" "" \
                       "needs Zone:DNS:Edit + Zone:Zone:Read scopes" || return 0
               from="$_TUI_INPUT"
               [[ -z "$from" ]] && { _tui_alert "no path given"; return 0; }
               _tui_shellout "$REPO_ROOT/hermod.sh" dns-secret "$_TUI_TARGET" --from-file "$from"
           else
               _tui_shellout "$REPO_ROOT/hermod.sh" dns-secret "$_TUI_TARGET"
           fi ;;
        5) # cert <target> show [name]
           _tui_prompt_field "Certificate name to show" "hermod-public-tls" \
                  "ENTER for the default; type a different name to inspect another cert" || return 0
           name="$_TUI_INPUT"
           _tui_popup_show "Cert show: $name" "$REPO_ROOT/hermod.sh" cert "$_TUI_TARGET" show "$name" ;;
        R) _tui_prompt_yesno "Rotate all internal mTLS certs on $_TUI_TARGET? Translators will rolling-restart." "n" \
             && _tui_shellout "$REPO_ROOT/hermod.sh" rotate-certs "$_TUI_TARGET" ;;
        *) return 1 ;;
    esac
}

# ── section: settings ────────────────────────────────────────────────
_tui_section_settings() {
    local row=3
    _tui_pane_write "$row" "${_TUI_BOLD}Settings & secret reconciliation — ${_TUI_TARGET}${_TUI_RESET}"
    row=$((row+2))
    _tui_pane_write "$row" "Compose stack:           local Hermod overlay (no kustomize)"
    row=$((row+1))
    _tui_pane_write "$row" "Active K8s overlay:      $_TUI_TARGET"
    row=$((row+2))
    _tui_pane_write "$row" "[1] env-edit       decrypt→\$EDITOR→re-encrypt hermod-prod.env"
    row=$((row+1))
    _tui_pane_write "$row" "[2] ensure-secrets reconcile cluster Secrets (keep mode)"
    row=$((row+1))
    _tui_pane_write "$row" "[3] config-show    print rendered Hermod config from coord"
    row=$((row+1))
    _tui_pane_write "$row" "[4] image-source   ghcr.io public images vs. local builds"
    row=$((row+1))
    _tui_pane_write "$row" "[5] protocol       enable/disable per-protocol translators"
    row=$((row+1))
    _tui_pane_write "$row" "[6] limiter        view + toggle runtime rate-limit knobs"
    row=$((row+1))
    _tui_pane_write "$row" "[7] update-repo    git pull --ff-only the Hermod source tree"
    _tui_render_actions \
        "1:env-edit" "2:ensure-secrets" "3:config-show" "4:image-source" \
        "5:protocol" "6:limiter" "7:update-repo"
}

_tui_section_settings_keys() {
    local env_file="${HERMOD_PROD_ENV:-$REPO_ROOT/hermod-prod.env}"
    local choice proto state
    case "$1" in
        1) # env-edit: decrypt → $EDITOR → re-encrypt
           _tui_shellout bash -c "
               set -e
               source '$REPO_ROOT/lib/mimir.sh'
               tmp=\$(mktemp)
               mimir_load '$env_file' >\$tmp
               \${EDITOR:-vi} \$tmp
               mv \$tmp '$env_file'
               mimir_init '$env_file' || mimir_rekey '$env_file' || true
           " ;;
        2) _tui_prompt_yesno "Re-run ensure-secrets against $_TUI_TARGET?" "y" \
             && _tui_shellout "$REPO_ROOT/hermod.sh" ensure-secrets "$_TUI_TARGET" ;;
        3) # config-show: shows ~/.config/hermod/config + resolved Pi SSH endpoint
           _tui_popup_show "Operator config" "$REPO_ROOT/hermod.sh" config show ;;
        4) # image-source — pick ghcr.io vs local builds
           _tui_prompt_choice "Image source for prod overlays" \
                    "ghcr.io public images (default for releases)" \
                    "local builds (microk8s ctr import)" \
                    "show current setting only" || return 0
           choice="$_TUI_INPUT"
           case "$choice" in
               ghcr*)  _tui_shellout "$REPO_ROOT/hermod.sh" image-source public ;;
               local*) _tui_shellout "$REPO_ROOT/hermod.sh" image-source local ;;
               *)      _tui_popup_show "image-source" "$REPO_ROOT/hermod.sh" image-source status ;;
           esac ;;
        5) # protocol <on|off|show> <name|target> [<target>]
           _tui_prompt_choice "Translator to toggle" \
                   "lora" "zigbee" "wifi" "ble" "show current state for all" || return 0
           proto="$_TUI_INPUT"
           if [[ "$proto" == show* ]]; then
               _tui_popup_show "Protocol state: $_TUI_TARGET" \
                   "$REPO_ROOT/hermod.sh" protocol show "$_TUI_TARGET"
           else
               _tui_prompt_choice "Set $proto to" "on" "off" || return 0
               state="$_TUI_INPUT"
               _tui_prompt_yesno "$proto on $_TUI_TARGET → $state?" "y" \
                 && _tui_shellout "$REPO_ROOT/hermod.sh" protocol "$state" "$proto" "$_TUI_TARGET"
           fi ;;
        6) # limiter <target> <show|rate|dedup> [on|off]
           local knob action k
           _tui_prompt_choice "Rate-limit knob" \
                  "show current state" \
                  "rate (master rate-limit toggle)" \
                  "dedup (master dedup toggle)" || return 0
           knob="$_TUI_INPUT"
           if [[ "$knob" == show* ]]; then
               _tui_popup_show "Rate limits: $_TUI_TARGET" \
                   "$REPO_ROOT/hermod.sh" limiter "$_TUI_TARGET" show
           else
               k="${knob%% *}"
               _tui_prompt_choice "Set $k to" "on" "off" || return 0
               action="$_TUI_INPUT"
               _tui_prompt_yesno "$k on $_TUI_TARGET → $action?" "y" \
                 && _tui_shellout "$REPO_ROOT/hermod.sh" limiter "$_TUI_TARGET" "$k" "$action"
           fi ;;
        7) # update-repo: safe `git pull --ff-only` on the Hermod source tree
           _tui_prompt_yesno "git pull --ff-only origin in $REPO_ROOT? Aborts if the working tree is dirty or the pull is non-fast-forward." "y" \
             && _tui_shellout "$REPO_ROOT/hermod.sh" update-repo ;;
        *) return 1 ;;
    esac
}

# ── section: diagnostics ─────────────────────────────────────────────
_tui_section_diagnostics() {
    local row=3
    _tui_pane_write "$row" "${_TUI_BOLD}Diagnostics${_TUI_RESET}"
    row=$((row+2))
    _tui_pane_write "$row" "[1] doctor    verify host deps for each deployment path"
    row=$((row+1))
    _tui_pane_write "$row" "[2] metrics   fetch coord /metrics on $_TUI_TARGET"
    row=$((row+1))
    _tui_pane_write "$row" "[3] logs      tail every pod (last 30 lines, all containers)"
    row=$((row+1))
    _tui_pane_write "$row" "[4] health    pod inventory + readiness for $_TUI_TARGET"
    row=$((row+1))
    _tui_pane_write "$row" "[5] pi-doctor deep host-side diagnostics on the Pi"
    row=$((row+1))
    _tui_pane_write "$row" "[6] cleanup   remove old/stale K8s artifacts on $_TUI_TARGET"
    _tui_render_actions \
        "1:doctor" "2:metrics" "3:logs" "4:health" \
        "5:pi-doctor" "6:cleanup"
}

_tui_section_diagnostics_keys() {
    local pod
    case "$1" in
        1) _tui_popup_show "Doctor" "$REPO_ROOT/hermod.sh" doctor ;;
        2) _tui_popup_show "Metrics: $_TUI_TARGET" \
             "$REPO_ROOT/hermod.sh" metrics "$_TUI_TARGET" ;;
        3) # logs <target> [pod] — empty = tail all
           _tui_prompt_field "Pod name (empty = tail all)" "" \
                 "tail -30 every container; or pick one pod by exact name" || return 0
           pod="$_TUI_INPUT"
           if [[ -n "$pod" ]]; then
               _tui_shellout "$REPO_ROOT/hermod.sh" logs "$_TUI_TARGET" "$pod"
           else
               _tui_shellout "$REPO_ROOT/hermod.sh" logs "$_TUI_TARGET"
           fi ;;
        4) _tui_popup_show "Health: $_TUI_TARGET" \
             "$REPO_ROOT/hermod.sh" status "$_TUI_TARGET" ;;
        5) _tui_popup_show "Pi doctor" "$REPO_ROOT/hermod.sh" pi-doctor ;;
        6) _tui_prompt_yesno "Run cluster cleanup on $_TUI_TARGET (prunes stuck pods + ReplicaSets)?" "n" \
             && _tui_shellout "$REPO_ROOT/hermod.sh" cleanup "$_TUI_TARGET" ;;
        *) return 1 ;;
    esac
}

# ── help screen ──────────────────────────────────────────────────────
_tui_help() {
    _tui_popup_show "Hermod TUI — keyboard reference" cat <<'EOF'

  Navigation
    c g p v u n s d       jump to a section by letter (sidebar)
    Tab / Shift-Tab       cycle to next / previous section
    Up / Down arrows      cycle through sections
    /                     filter the current pane (ESC to clear)
    :                     command palette — `:install prod-pi`,
                          `:logs prod-pi vault42-…`, etc.
    ?                     this help popup
    q                     quit (Ctrl-C also works)

  Action keys (per section, shown as a grid in the right pane)
    1-9                   primary actions (read-only / safe)
    Uppercase letters     destructive or secret-revealing; confirm twice
    T                     (Production) cycle target overlay
    j / k                 (Production) move pod selection
    Enter                 (Production) drill into selected pod (logs popup)

  Status row markers
    [+] vault(N)   N file(s) currently unlocked in the session cache
    [+] pi         Pi reachable on TCP 22
    [+] compose    docker-compose stack up & healthy
    [+] target     cluster reachable for the active K8s overlay
    [-] / [X]      neutral / failure
EOF
}

# Inline single-line input on the footer row. Used by both / and :
# so the visual model is identical: prompt char + cursor at column 2,
# bash line editing (echo + icanon), ESC produces empty result.
# Single-line footer prompt. Sets _TUI_INPUT to the typed string; ESC
# (or Ctrl-D EOF) returns 1 and clears _TUI_INPUT. Painting goes to
# the function's real stdout — DO NOT capture this with $().
_tui_input_line() {
    local prompt="$1"
    _tui_show_cursor
    _tui_cup "$_TUI_ROWS" 1
    _tui_clear_line
    printf '%s' "$prompt"
    stty echo icanon 2>/dev/null || true
    local q rc=0
    IFS= read -r q </dev/tty || rc=1
    stty -echo -icanon 2>/dev/null || true
    _tui_hide_cursor
    if (( rc != 0 )); then
        _TUI_INPUT=""
        return 1
    fi
    _TUI_INPUT="$q"
    return 0
}

_tui_filter_input() {
    _tui_input_line '/' || { _TUI_FORCE_FULL=1; return 0; }
    _TUI_FORCE_FULL=1
    _TUI_FILTER="$_TUI_INPUT"
    if [[ -z "$_TUI_INPUT" ]]; then
        _tui_alert "filter cleared"
    else
        _tui_alert "filter: $_TUI_INPUT"
    fi
}

# Command palette. Accepts any hermod.sh subcommand (with args).
# Output is shown in a popup so the operator stays inside the TUI for
# read-only commands, or shells out for interactive ones (logs, install).
_tui_palette() {
    _tui_input_line ':' || { _TUI_FORCE_FULL=1; return 0; }
    _TUI_FORCE_FULL=1
    local cmd="$_TUI_INPUT"
    [[ -z "$cmd" ]] && return 0
    case "$cmd" in
        install*|update*|teardown*|redeploy*|reset*|provision*|flash*|wait-pi*|logs*|kick*)
            # shellcheck disable=SC2086
            _tui_shellout "$REPO_ROOT/hermod.sh" $cmd ;;
        *)
            # shellcheck disable=SC2086
            _tui_popup_show ":$cmd" "$REPO_ROOT/hermod.sh" $cmd ;;
    esac
}

# ── key dispatch ─────────────────────────────────────────────────────
_tui_handle_key() {
    local k="$1"
    # Section letter shortcut?
    local entry key id label
    for entry in "${_TUI_SECTIONS[@]}"; do
        IFS=':' read -r key id label <<<"$entry"
        if [[ "$k" == "$key" ]]; then
            if [[ "$_TUI_SECTION" != "$id" ]]; then
                _TUI_SECTION="$id"
                _TUI_PANE_FOCUS="sidebar"  # always start a section in sidebar focus
                _TUI_FORCE_FULL=1
            fi
            return 0
        fi
    done

    # Per-section action key?
    local handler="_tui_section_${_TUI_SECTION}_keys"
    if type "$handler" >/dev/null 2>&1; then
        if "$handler" "$k"; then return 0; fi
    fi

    case "$k" in
        q|$'\x03') _TUI_RUNNING=0 ;;
        $'\x1b')
            # Escape sequence (arrow / function keys) or a bare ESC.
            # Pull up to two more bytes with tight timeouts — if the
            # terminal sends a CSI prefix `[`, decode the third byte as
            # a direction. Bare ESC is a no-op so an accidental tap
            # doesn't quit (use `q` for that).
            #
            # Focus model: when _TUI_PANE_FOCUS == "sidebar" the arrow
            # keys cycle the section list (the historic behaviour).
            # Right-arrow on a section that exposes a pod list (the
            # only one that does today is Production, _TUI_POD_LIST set
            # by its renderer) flips focus to "pane"; up/down then
            # navigate the pod list, left-arrow returns focus to the
            # sidebar. Enter still drills (handled by the section-keys
            # dispatcher above).
            local k2 k3
            read -rsn1 -t 0.05 k2 </dev/tty || k2=""
            [[ -z "$k2" ]] && return 0
            if [[ "$k2" == "[" ]]; then
                read -rsn1 -t 0.05 k3 </dev/tty || k3=""
                if [[ "$_TUI_PANE_FOCUS" == "pane" ]]; then
                    case "$k3" in
                        A) _tui_pod_index_move -1 ;;
                        B) _tui_pod_index_move 1 ;;
                        C) ;;                                # already inside
                        D) _TUI_PANE_FOCUS="sidebar"; _TUI_FORCE_FULL=1 ;;
                    esac
                else
                    case "$k3" in
                        A) _tui_section_cycle -1 ;;
                        B) _tui_section_cycle 1 ;;
                        C) # right-arrow into the pane — only sections
                           # with a pod list have meaningful pane-mode
                           # nav, so gate on _TUI_POD_LIST having
                           # entries. Other sections ignore right-arrow.
                           if (( ${#_TUI_POD_LIST[@]} > 0 )); then
                               _TUI_PANE_FOCUS="pane"
                               _TUI_FORCE_FULL=1
                           fi ;;
                        D) ;;
                        Z) _tui_section_cycle -1 ;;          # Shift-Tab
                    esac
                fi
            fi
            ;;
        $'\t') _tui_section_cycle 1 ;;
        j)     _tui_section_cycle 1 ;;
        k)     _tui_section_cycle -1 ;;
        '?')   _tui_help ;;
        '/')   _tui_filter_input ;;
        ':')   _tui_palette ;;
        *)     ;;
    esac
}

_tui_section_cycle() {
    local dir="$1" i=0 n=${#_TUI_SECTIONS[@]} idx=0
    for entry in "${_TUI_SECTIONS[@]}"; do
        IFS=':' read -r _ id _ <<<"$entry"
        [[ "$id" == "$_TUI_SECTION" ]] && idx=$i
        i=$((i+1))
    done
    idx=$(( (idx + dir + n) % n ))
    IFS=':' read -r _ _TUI_SECTION _ <<<"${_TUI_SECTIONS[$idx]}"
    _TUI_PANE_FOCUS="sidebar"  # leaving the section drops pane focus
    _TUI_FORCE_FULL=1
}

# ── first-run gate ───────────────────────────────────────────────────
# Stamp file lives next to the per-machine config so it follows the same
# XDG convention. If the stamp is missing we drop the operator into the
# Diagnostics section, run the doctor once, and surface the output in a
# popup. The next launch sees the stamp and goes straight into the UI.
_tui_first_run_marker() {
    local dir="${XDG_CONFIG_HOME:-$HOME/.config}/hermod"
    printf '%s/.tui-first-run-done' "$dir"
}

_tui_first_run_check() {
    local marker; marker="$(_tui_first_run_marker)"
    [[ -f "$marker" ]] && return 0
    _TUI_SECTION="diagnostics"
    _TUI_FORCE_FULL=1
    _tui_render
    _tui_popup_show "First-run doctor" "$REPO_ROOT/hermod.sh" doctor || true
    mkdir -p "$(dirname "$marker")" 2>/dev/null
    : > "$marker" 2>/dev/null || true
}

# Land the operator wherever the next obvious action lives. With no
# hermod-prod.env (and no encrypted .mimir sibling), the only useful
# action is wizarding one into existence — drop into Secrets instead
# of staring at an empty Production pod table.
_tui_choose_initial_section() {
    local env_file="${HERMOD_PROD_ENV:-$REPO_ROOT/hermod-prod.env}"
    if [[ ! -f "$env_file" && ! -f "$env_file.mimir" ]]; then
        _TUI_SECTION="vault"
    fi
}

# ── main loop ────────────────────────────────────────────────────────
_tui_main() {
    # The TUI is a long-running interactive loop; a transient command
    # failure (docker not on PATH, kubectl context missing, ssh probe
    # timeout) must NOT abort the whole UI. hermod.sh runs under
    # `set -euo pipefail` and we inherit that — flip them off for the
    # loop body so individual handlers can fail soft into _tui_alert.
    set +e
    set +o pipefail
    _tui_init
    _tui_first_run_check
    _tui_choose_initial_section
    while [[ "$_TUI_RUNNING" -eq 1 ]]; do
        _tui_render
        local k
        # Block up to refresh interval; redraw on tick.
        if read -rsn1 -t "$_TUI_REFRESH_INTERVAL" k </dev/tty; then
            _tui_handle_key "$k"
        fi
    done
    _tui_cleanup
}
