@echo off
setlocal EnableExtensions EnableDelayedExpansion
REM ============================================================================
REM hermod.bat — compose-only Hermod CLI for Windows.
REM
REM Subcommands:
REM   compose <up^|down^|restart^|logs^|status^|build^|pull> [svc]
REM   doctor                          Verify host deps
REM   menu  (or no args)              Interactive TUI menu
REM   help                            Usage
REM
REM Linux/macOS use hermod.sh, which also handles k8s install/update/status
REM for the pi5-live and kind-hermod targets. Those targets need rsync,
REM kubectl, and ssh — easiest under WSL2 on Windows.
REM
REM ----------------------------------------------------------------------------
REM SECURITY WARNING — DEV / EVALUATION DEPLOYMENT, NOT PRODUCTION-HARDENED
REM ----------------------------------------------------------------------------
REM This compose stack runs with:
REM   * NanoMQ MQTT broker:    allow_anonymous=true, no_match=allow, plaintext.
REM   * Mosquitto wifi bridge: anonymous, plaintext.
REM   * Coordinator + vault42: HTTP only, no TLS.
REM   * Default DB / MQTT passwords baked into docker-compose.yaml.
REM Bind these ports ONLY to host loopback or a trusted LAN segment.
REM Do NOT expose 1883 / 1884 / 8080 / 8081 / 8083 / 42069 to the public
REM internet. Production hardening (mTLS, per-service ACLs, real Vault42)
REM is documented in docs\TODO.md and is out of scope for the compose path.
REM
REM ZIGBEE — NOT enabled by default. Adapter family (ember/zstack/deconz/
REM ezsp), device path (/dev/ttyACM0, /dev/ttyAMA0, /dev/ttyUSB0), and host-
REM side USB passthrough (Linux native; Windows needs WSL2 + usbipd) all
REM vary too much to ship one default. See docker-compose.yaml + INSTALL.md.
REM ============================================================================

cd /d "%~dp0"
set "REPO_ROOT=%CD%"

if "%~1"=="" goto :menu
if /i "%~1"=="menu"    goto :menu
if /i "%~1"=="compose" ( shift & call :do_compose %1 %2 %3 & exit /b !errorlevel! )
if /i "%~1"=="doctor"  ( call :do_doctor & exit /b !errorlevel! )
if /i "%~1"=="help"    goto :usage
if /i "%~1"=="-h"      goto :usage
if /i "%~1"=="--help"  goto :usage
echo Unknown command: %~1
echo Run "%~nx0 help" for usage.
exit /b 2

REM ── compose dispatcher ────────────────────────────────────────────────────
:do_compose
    set "ACTION=%~1"
    set "SVC=%~2"
    if "%ACTION%"=="" set "ACTION=help"

    where docker >nul 2>&1
    if errorlevel 1 (
        echo [hermod ERR] docker not on PATH. Install Docker Desktop.
        exit /b 1
    )

    if /i "%ACTION%"=="up" (
        call :compose_warn_banner
        docker compose up -d --build
        if errorlevel 1 exit /b 1
        echo.
        echo [hermod OK] Hermod is up. Coordinator: http://localhost:42069
        call :compose_creds
        call :compose_warn_banner
        exit /b 0
    )
    if /i "%ACTION%"=="creds" ( call :compose_creds & exit /b 0 )
    if /i "%ACTION%"=="down"    ( docker compose down                           & exit /b !errorlevel! )
    if /i "%ACTION%"=="reset" (
        echo [hermod !!] destroying ALL compose volumes ^(postgres + nanomq + hermod data^)
        docker compose down -v
        if errorlevel 1 exit /b 1
        echo [hermod OK] stack stopped + volumes removed; "compose up" starts a fresh install
        exit /b 0
    )
    if /i "%ACTION%"=="restart" ( docker compose restart %SVC%                  & exit /b !errorlevel! )
    if /i "%ACTION%"=="logs"    ( docker compose logs -f %SVC%                  & exit /b !errorlevel! )
    if /i "%ACTION%"=="status"  ( docker compose ps                             & exit /b !errorlevel! )
    if /i "%ACTION%"=="build"   ( docker compose build %SVC%                    & exit /b !errorlevel! )
    if /i "%ACTION%"=="pull"    ( docker compose pull                           & exit /b !errorlevel! )
    if /i "%ACTION%"=="help" (
        echo hermod.bat compose ^<action^> [svc]
        echo.
        echo   *** UNSUPPORTED, MOCK-VERIFICATION ONLY ***
        echo   Not for production. Baked-in dev passwords, self-signed CA,
        echo   AuthBypass on lora2mqtt. Use the Linux/Pi install path for
        echo   any real deployment.
        echo.
        echo   up         create + start ^(builds images first^)
        echo   down       stop + remove containers ^(volumes preserved^)
        echo   reset      down + wipe ALL volumes ^(destroys data^)
        echo   restart    restart [service]
        echo   logs       tail logs [service]
        echo   status     ps
        echo   build      rebuild [service]
        echo   pull       refresh images
        echo   creds      print baked-in dev usernames + passwords ^(also printed on `up`^)
        exit /b 0
    )
    echo [hermod ERR] compose: unknown action "%ACTION%"
    exit /b 2

REM ── compose unsupported banner ───────────────────────────────────────────
REM Mirror of `_compose_warn_banner` in hermod.sh — printed at the top
REM of every state-changing compose action so an operator can't lose
REM track of which deployment path they are on.
:compose_warn_banner
    echo.
    echo =================================================================
    echo   *** COMPOSE STACK — UNSUPPORTED, MOCK-VERIFICATION ONLY ***
    echo =================================================================
    echo   This path is NOT a production deployment and is NOT secure:
    echo     - Baked-in dev passwords in docker-compose.yaml
    echo     - Self-signed CA regenerated on every `compose reset`
    echo     - LoRa2MQTT runs with Hermod__Security__AuthBypass=true so
    echo       the dashboard works without a real session JWT
    echo     - Coordinator listens HTTP only ^(no Kestrel TLS^)
    echo     - z2m disabled ^(no USB adapter on the compose host^)
    echo     - omg-ble runs with `-b 0` ^(no Bluetooth scan^)
    echo   Use it to smoke-test code changes locally. For anything real,
    echo   install on the Pi: hermod.sh install prod-pi
    echo =================================================================
    echo.
    exit /b 0

REM ── compose dev credentials ──────────────────────────────────────────────
REM Mirror of `_compose_print_creds` in hermod.sh — surface the baked-in
REM seed users + service passwords so an operator doesn't have to grep
REM lib\compose\vault42-seed.json + docker-compose.yaml.
:compose_creds
    echo.
    echo ─────────────────────────────────────────────────────────────────
    echo   Hermod compose dev credentials  (mock verification only)
    echo ─────────────────────────────────────────────────────────────────
    echo.
    echo   Vault42 seed users — log in at http://localhost:42069/login:
    echo     viewer    viewer@hermod.local    asdfghjklVIEWER123
    echo     user      user@hermod.local      asdfghjklUSER123
    echo     operator  operator@hermod.local  asdfghjklOPER123
    echo.
    echo   MQTT service credential ^(translators + bridges^):
    echo     user            hermod-service
    echo     pass            change-me-mqtt
    echo.
    echo   Postgres app password ^(hermod_app role^):
    echo     pass            change-me-hermod-app
    echo.
    echo   Endpoints:
    echo     Coord dashboard       http://localhost:42069
    echo     Vault42 ^(internal^)  https://vault42:8443  ^(container DNS only^)
    echo     NanoMQ MQTT           mqtt://localhost:1883
    echo     NanoMQ admin          http://localhost:8081
    echo     NanoMQ websocket      ws://localhost:18083
    echo     Wifi2MQTT bridge      mqtt://localhost:1884
    echo.
    exit /b 0

REM ── doctor ────────────────────────────────────────────────────────────────
:do_doctor
    set "FAIL=0"
    echo Hermod doctor (Windows)
    echo.
    echo ## compose path
    where docker >nul 2>&1
    if errorlevel 1 (
        echo   [X] docker not found — install Docker Desktop
        set "FAIL=1"
    ) else (
        for /f "tokens=*" %%V in ('docker --version 2^>nul') do echo   [OK] %%V
    )
    docker compose version >nul 2>&1
    if errorlevel 1 (
        where docker-compose >nul 2>&1
        if errorlevel 1 (
            echo   [X] no docker compose found — install compose plugin
            set "FAIL=1"
        ) else (
            echo   [OK] docker-compose ^(legacy^)
        )
    ) else (
        echo   [OK] docker compose plugin
    )
    echo.
    echo ## k8s paths ^(optional, use WSL2 + hermod.sh for these^)
    where kubectl >nul 2>&1 && (echo   [OK] kubectl) || (echo   [!] kubectl not found ^(needed only for k8s targets^))
    where ssh     >nul 2>&1 && (echo   [OK] ssh)     || (echo   [!] ssh not found ^(needed only for Pi target^))
    where rsync   >nul 2>&1 && (echo   [OK] rsync)   || (echo   [!] rsync not found ^(needed only for Pi target^))
    echo.
    if "%FAIL%"=="0" (
        echo [hermod OK] compose path ready
    ) else (
        echo [hermod !!] compose path missing dependencies ^(see above^)
    )
    exit /b %FAIL%

REM ── interactive TUI ───────────────────────────────────────────────────────
:menu
    cls
    echo ==========================================================
    echo   Hermod IoT Translator — Ops CLI
    echo   Windows ^(compose only — use WSL2 + hermod.sh for k8s^)
    echo ==========================================================
    echo.
    echo   COMPOSE
    echo     1) Start the stack ^(up + build^)
    echo     2) Stop the stack ^(down^)
    echo     3) Show status
    echo     4) Tail logs ^(all services^)
    echo     5) Tail logs ^(one service^)
    echo     6) Restart a service
    echo     7) Pull/rebuild images
    echo     r) Reset ^(down + WIPE all volumes^)
    echo.
    echo     d) Doctor ^(verify deps^)
    echo     h) Help text
    echo     q) Quit
    echo.
    echo ==========================================================
    echo.
    set "CHOICE="
    set /p "CHOICE=Choose: "
    echo.
    if /i "!CHOICE!"=="1"  ( call :do_compose up                          & goto :menu_pause )
    if /i "!CHOICE!"=="2"  ( call :do_compose down                        & goto :menu_pause )
    if /i "!CHOICE!"=="3"  ( call :do_compose status                      & goto :menu_pause )
    if /i "!CHOICE!"=="4"  ( call :do_compose logs                        & goto :menu_pause )
    if /i "!CHOICE!"=="5"  ( set /p "SVC=service name: " & call :do_compose logs    !SVC! & goto :menu_pause )
    if /i "!CHOICE!"=="6"  ( set /p "SVC=service to restart: " & call :do_compose restart !SVC! & goto :menu_pause )
    if /i "!CHOICE!"=="7"  ( call :do_compose pull & call :do_compose build & goto :menu_pause )
    if /i "!CHOICE!"=="r" (
        set /p "CONFIRM=wipe ALL compose volumes? [y/N] "
        if /i "!CONFIRM!"=="y" ( call :do_compose reset ) else ( echo cancelled )
        goto :menu_pause
    )
    if /i "!CHOICE!"=="d"  ( call :do_doctor                              & goto :menu_pause )
    if /i "!CHOICE!"=="h"  ( call :usage                                  & goto :menu_pause )
    if /i "!CHOICE!"=="q" ( echo bye & exit /b 0 )
    if "!CHOICE!"==""     ( goto :menu )
    echo Unknown choice: !CHOICE!
    goto :menu_pause

:menu_pause
    echo.
    pause
    goto :menu

REM ── usage ─────────────────────────────────────────────────────────────────
:usage
    echo hermod.bat — compose-only Hermod CLI for Windows
    echo.
    echo Usage:
    echo   %~nx0                              Interactive menu ^(TUI^)
    echo   %~nx0 compose ^<action^> [svc]       Single-host docker compose
    echo   %~nx0 doctor                       Verify deps
    echo   %~nx0 help                         This text
    echo.
    echo Compose actions: up ^| down ^| reset ^| restart ^| logs ^| status ^| build ^| pull
    echo.
    echo Examples:
    echo   %~nx0
    echo   %~nx0 compose up
    echo   %~nx0 compose logs coordinator
    echo   %~nx0 doctor
    echo.
    echo For k8s install/update/status against pi5-live or kind-hermod targets,
    echo run hermod.sh under WSL2 ^(those need rsync/kubectl/ssh^).
    exit /b 0
