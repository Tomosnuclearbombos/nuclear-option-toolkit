#!/usr/bin/env python3
"""Nuke Option Server Toolkit — local setup wizard (offline-first).

Run this once to set up the toolkit for YOUR server. It starts a tiny web server
bound to localhost only, opens your browser to a guided wizard, and writes a clean
config + a separate secrets file (your credentials never leave this machine and are
never committed). Nothing here phones home — the only optional network calls are the
"Test connection" buttons you click and (later, opt-in) the GitHub updater.

    python setup.py            # opens the wizard in your browser

Design notes:
  * Localhost-only bind + a random per-run token in the URL, so nothing else on the
    LAN can reach the setup API.
  * Secrets (SFTP password, Pterodactyl key, panel URL) are written to secrets.json
    in the user-data dir with 0600 perms where the OS supports it, and are NEVER put
    in config.json (which is the safe-to-share file).
  * The plugin feature catalogue is read from ../settings_catalogue.json — the SAME
    catalogue the web command centre's Settings menu uses (one source of truth).
"""
import http.server
import json
import os
import secrets as _secrets
import socket
import sys
import threading
import webbrowser

for _s in (sys.stdout, sys.stderr):                   # never crash printing on a cp1252 console
    try:
        _s.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError, OSError):
        pass

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)                      # the toolkit root (where the bot/web CC live)
CATALOGUE = os.path.join(ROOT, "settings_catalogue.json")
# User data dir: where the generated config + secrets live. Kept OUT of the repo.
# PER-FOLDER by default (a `.nost-data` inside THIS server's own folder) so two installs
# in sibling folders never share a config — a 2nd install can't overwrite the 1st server's
# config and take it down. Override with NOST_DATA_DIR or --data-dir for explicit control.
USER_DIR = os.environ.get("NOST_DATA_DIR") or os.path.join(ROOT, ".nost-data")
CONFIG = os.path.join(USER_DIR, "config.json")
SECRETS = os.path.join(USER_DIR, "secrets.json")
FORCE = False            # --force: allow overwriting a DIFFERENT server's config in this data dir
TOKEN = _secrets.token_urlsafe(16)                # guards the setup API for this run
_SERVER = None                                    # set in main(); used by /api/shutdown

sys.path.insert(0, HERE)


def _try_import(name):
    """Import an installer module independently — a bundle ships only the modules its type needs
    (e.g. the Pterodactyl bundle has no steamcmd.py), so one missing module must NOT take the
    others down with it."""
    try:
        return __import__(name)
    except Exception:                 # noqa: BLE001
        return None


_fetcher = _try_import("fetcher")          # manifest-driven source resolver/downloader
_detect = _try_import("detect")            # autodetect scenario + connectivity
_steamcmd = _try_import("steamcmd")        # install/locate the dedicated server (SteamCMD) — local only
_serverconfig = _try_import("serverconfig")  # read/write DedicatedServerConfig.json (ports etc.)
_deployer = _try_import("deployer")        # SFTP push of the bundled game-side payload (Pterodactyl)


def _bundle_type():
    """A pre-assembled bundle ships a one-line bundle_type.txt at its root (pterodactyl|local|
    manual). When present the wizard scopes itself to that single type (no hosting chooser).
    Absent (a dev / full-repo run) -> "" -> the original multi-option wizard."""
    for p in (os.path.join(ROOT, "bundle_type.txt"), os.path.join(HERE, "bundle_type.txt")):
        try:
            with open(p, encoding="utf-8") as f:
                t = f.read().strip().lower()
            if t in ("pterodactyl", "local", "manual"):
                return t
        except OSError:
            pass
    return ""


BUNDLE_TYPE = _bundle_type()

try:
    import paramiko                               # optional: only needed for SFTP scenarios
except Exception:                                 # noqa: BLE001
    paramiko = None
try:
    import urllib.request
    import urllib.error
except Exception:                                 # noqa: BLE001
    urllib = None


# ----------------------------- helpers -----------------------------
def _free_port():
    s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    s.bind(("127.0.0.1", 0))
    p = s.getsockname()[1]
    s.close()
    return p


def _suggest_web_port(start=8770):
    """First free local port from `start`. A 2nd install won't default to the port the 1st
    server's web CC is already using (which would leave the 2nd dashboard unable to bind)."""
    for p in range(int(start), int(start) + 50):
        s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        try:
            s.bind(("127.0.0.1", p))
            s.close()
            return p
        except OSError:
            s.close()
    return int(start)


def _load_catalogue():
    try:
        with open(CATALOGUE, encoding="utf-8") as f:
            d = json.load(f)
        return d.get("settings", []) if isinstance(d, dict) else (d if isinstance(d, list) else [])
    except (OSError, ValueError):
        return []


def _read_json(path):
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except (OSError, ValueError):
        return {}


def _write_json_secure(path, data, secret=False):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=2)
    os.replace(tmp, path)
    if secret:
        try:
            os.chmod(path, 0o600)                 # no-op semantics on Windows, real on *nix
        except OSError:
            pass


def _preflight():
    """Green/amber/red checks the wizard shows on the welcome step."""
    out = []
    import sys
    out.append({"name": "Python runtime",
                "ok": sys.version_info >= (3, 8),
                "detail": "Python %d.%d" % sys.version_info[:2]})
    out.append({"name": "paramiko (SFTP)",
                "ok": paramiko is not None,
                "detail": "available" if paramiko else "missing — needed for external (SFTP) servers. Click 'Install Python packages' below."})
    try:
        import flask  # noqa: F401
        _flask_ok = True
    except Exception:                                    # noqa: BLE001
        _flask_ok = False
    out.append({"name": "Flask (web command centre)",
                "ok": _flask_ok,
                "detail": "available" if _flask_ok else "MISSING — the web dashboard won't start without it. Click 'Install Python packages' below."})
    try:
        import requests  # noqa: F401
        _req_ok = True
    except Exception:                                    # noqa: BLE001
        _req_ok = False
    out.append({"name": "requests",
                "ok": _req_ok,
                "detail": "available" if _req_ok else "missing — used by the bot/updater. Click 'Install Python packages' below."})
    out.append({"name": "Settings catalogue",
                "ok": os.path.exists(CATALOGUE),
                "detail": "%d settings" % len(_load_catalogue()) if os.path.exists(CATALOGUE) else "settings_catalogue.json not found next to the toolkit"})
    out.append({"name": "Internet (optional)",
                "ok": True,
                "detail": "the installer runs fully offline; internet is only used for the opt-in GitHub updater later"})
    return out


def _test_sftp(p):
    if paramiko is None:
        return {"ok": False, "error": "paramiko not installed (pip install paramiko)"}
    host = (p.get("sftp_host") or "").strip()
    port = int(p.get("sftp_port") or 22)
    user = (p.get("sftp_user") or "").strip()
    pw = p.get("sftp_pass") or ""
    if not host or not user:
        return {"ok": False, "error": "host and user are required"}
    try:
        import socket as _sock
        _s = _sock.create_connection((host, port), timeout=10)   # fail FAST on a wrong host/port
        t = paramiko.Transport(_s)
        t.banner_timeout = 15
        t.connect(username=user, password=pw)
        sftp = paramiko.SFTPClient.from_transport(t)
        listing = sftp.listdir(".")[:5]
        t.close()
        return {"ok": True, "info": ("connected; sample of remote files: " + ", ".join(listing)) if listing else "connected (home looks empty)"}
    except Exception as e:                         # noqa: BLE001
        return {"ok": False, "error": str(e)}


import re as _re

_PANEL_SCHEME_RE = _re.compile(r'^[a-z][a-z0-9+.-]*://', _re.I)


def _normalize_panel_url(url):
    """Be forgiving about what the user pastes as the panel URL: add https:// if there's no
    scheme, replace a wrong scheme (sftp://, ws://, ...) that they might paste from the SFTP
    field, drop anything from /server/... onward (people paste the full server URL), and strip
    a trailing /api/client. A CORRECT base is returned unchanged. Returns the panel's base."""
    u = (url or "").strip()
    if not u:
        return ""
    m = _PANEL_SCHEME_RE.match(u)
    if m:
        if m.group(0).lower() not in ("http://", "https://"):
            u = "https://" + u[m.end():]
    else:
        u = "https://" + u
    i = u.lower().find("/server/")    # full server URL pasted -> keep only the base
    if i != -1:
        u = u[:i]
    u = u.rstrip("/")
    if u.lower().endswith("/api/client"):   # only the well-known client-API path, never a bare /api
        u = u[:-len("/api/client")].rstrip("/")
    return u


def _test_panel(p):
    if urllib is None:
        return {"ok": False, "error": "urllib unavailable"}
    panel = _normalize_panel_url(p.get("panel_url"))
    key = (p.get("api_key") or "").strip()
    if not panel or not key:
        return {"ok": False, "error": "panel URL and API key are required"}
    base_hint = ("Use just your panel's base address, e.g. https://panel.yourhost.net — "
                 "with no /server/... on the end.")
    req = urllib.request.Request(panel + "/api/client",
                                 headers={"Authorization": "Bearer " + key,
                                          "Accept": "application/json",
                                          "User-Agent": "Mozilla/5.0 NukeOptionToolkit"})
    try:
        with urllib.request.urlopen(req, timeout=12) as r:
            ctype = (r.headers.get("Content-Type") or "")
            body = r.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as e:            # noqa: BLE001
        if e.code in (401, 403):
            return {"ok": False, "error": "HTTP %s — the API key was rejected. It must be a "
                    "CLIENT (account) key starting 'ptlc_', from Account -> API Credentials." % e.code}
        if e.code in (301, 302, 404):
            return {"ok": False, "error": "HTTP %s from %s/api/client. %s" % (e.code, panel, base_hint)}
        return {"ok": False, "error": "HTTP %s from %s/api/client — check the panel URL." % (e.code, panel)}
    except ValueError as e:                        # malformed URL
        return {"ok": False, "error": "that panel URL doesn't look valid (%s). %s" % (e, base_hint)}
    except Exception as e:                         # noqa: BLE001  (DNS, refused, TLS, timeout)
        return {"ok": False, "error": "couldn't reach %s (%s). Check the address is right and "
                "reachable from this PC." % (panel, e)}
    # We got a 200 — but is it actually the API, or an HTML login/Cloudflare page?
    if "json" not in ctype.lower() and not body.lstrip().startswith(("{", "[")):
        return {"ok": False, "error": "the panel URL returned a web page, not the API. %s" % base_hint}
    try:
        data = json.loads(body)
    except ValueError:
        return {"ok": False, "error": "the panel responded with something that isn't JSON. %s" % base_hint}
    servers = [s.get("attributes", {}).get("identifier") for s in (data.get("data") or [])]
    return {"ok": True, "panel_url": panel,
            "info": "panel reachable (%s); %d server(s): %s" % (panel, len(servers),
                    ", ".join(filter(None, servers)) or "(none)"),
            "servers": [{"id": s.get("attributes", {}).get("identifier"),
                         "name": s.get("attributes", {}).get("name")} for s in (data.get("data") or [])]}


_TRUEISH = ("1", "true", "on", "yes")


def _cfg_value(meta, val):
    """Render ONE cfg value the way BepInEx will parse it back.

    The feature payload carries real types — bools for the master switches, numbers for the rate
    limits, strings for the enum modes. This used to be `"true" if on else "false"` for every key,
    which wrote `FleetOrdersPerSec = true` into a fresh cfg: not an int, so the plugin fell back to
    its bind default and the operator's chosen rate was silently discarded.
    """
    typ = str(meta.get("type") or "").strip().lower()
    if typ in ("int", "float"):
        try:
            return str(int(float(val))) if typ == "int" else repr(float(val))
        except (TypeError, ValueError):
            return str(meta.get("default", val))
    if typ in ("string", "enum"):
        return "" if val is None else str(val)
    # toggle (and anything unrecognised): a real bool, or a "true"/"false"-ish string
    if isinstance(val, str):
        return "true" if val.strip().lower() in _TRUEISH else "false"
    return "true" if val else "false"


def _render_plugin_cfg(features):
    """Generate a BepInEx anz.nukestats.cfg body from the selected feature toggles +
    the catalogue defaults. Only ON/OFF feature master-keys are written here; the full
    per-value tuning happens later in the web CC's Settings menu."""
    cat = {s["key"]: s for s in _load_catalogue()}
    lines = ["## anz.nukestats.cfg — generated by the Nuke Option setup wizard.",
             "## Feature on/off below; fine-tune everything live in the web command centre.\n"]
    # group by section
    sections = {}
    for key, on in features.items():
        meta = cat.get(key)
        if not meta or "." not in key:
            continue
        sec, name = key.split(".", 1)
        sections.setdefault(sec, []).append((name, meta, on))
    for sec in sorted(sections):
        lines.append("[%s]" % sec)
        for name, meta, on in sections[sec]:
            val = _cfg_value(meta, on)
            lines.append("## %s" % (meta.get("adminDescription") or ""))
            lines.append("%s = %s\n" % (name, val))
        lines.append("")
    return "\n".join(lines)


def _generate_launch(game_dir, platform, rcmd_port):
    """Own-PC install: create logs/console.log + a StartServer launcher in the game folder so
    the server boots modded (BepInEx) and writes the log the bot reads. Returns (script, log)."""
    logs = os.path.join(game_dir, "logs")
    os.makedirs(logs, exist_ok=True)
    logp = os.path.join(logs, "console.log")
    if not os.path.exists(logp):
        open(logp, "a").close()                          # so the path exists immediately
    if platform == "windows":
        script = os.path.join(game_dir, "StartServer.bat")
        body = ("@echo off\r\n"
                "cd /d \"%~dp0\"\r\n"
                "if not exist logs mkdir logs\r\n"
                "echo Starting Nuclear Option (modded) - logs\\console.log\r\n"
                "NuclearOptionServer.exe -batchmode -nographics -logFile logs\\console.log -ServerRemoteCommands " + str(rcmd_port) + "\r\n"
                "pause\r\n")
    else:
        script = os.path.join(game_dir, "start_server.sh")
        body = ("#!/usr/bin/env bash\n"
                "cd \"$(dirname \"$0\")\"\n"
                "mkdir -p logs\n"
                "chmod +x ./NuclearOptionServer.x86_64 ./libdoorstop.so 2>/dev/null || true\n"
                "export LD_LIBRARY_PATH=\"$(pwd):$(pwd)/linux64:$LD_LIBRARY_PATH\"\n"
                "export DOORSTOP_ENABLED=1\n"
                "export DOORSTOP_TARGET_ASSEMBLY=\"$(pwd)/BepInEx/core/BepInEx.Preloader.dll\"\n"
                "export LD_PRELOAD=\"$(pwd)/libdoorstop.so:$LD_PRELOAD\"\n"
                "./NuclearOptionServer.x86_64 -batchmode -nographics -logFile logs/console.log -ServerRemoteCommands " + str(rcmd_port) + "\n")
    with open(script, "w", encoding="utf-8", newline="") as f:
        f.write(body)
    if platform != "windows":
        try:
            os.chmod(script, 0o755)
        except OSError:
            pass
    return script, logp


# --- Folder-safe launcher templates (per-server, sibling-safe). --------------
# Each launcher: sets a PER-FOLDER NOST_DATA_DIR, kills ONLY this folder's python
# (directory-PREFIX match so 'Server\' can't match 'Server 2\'), launches bot + web CC
# by FULL %~dp0 path (so the folder is in the command line for future folder-scoped
# kills), pins THIS server's web-CC port, and tags every window with the folder name.
# `__P__` is replaced with this server's web port; `__GAMESTART__` with the own-PC
# game-server start (empty for a Pterodactyl/admin install). NO name-blind kills, and
# NO shared 'START EVERYTHING' — that killed every server's processes.

_WIN_RUN = r'''@echo off
cd /d "%~dp0"
REM --- Nuke Option BOT (folder-safe, per-folder data dir) ---
for %%I in ("%~dp0.") do set "NOST_FOLDER=%%~nxI"
title Nuke Option BOT - %NOST_FOLDER%
if not defined NOST_DATA_DIR set "NOST_DATA_DIR=%~dp0.nost-data"
if not exist "%NOST_DATA_DIR%" mkdir "%NOST_DATA_DIR%"
set "PYEXE="
if exist "%LocalAppData%\Programs\Python\Python314\python.exe" set "PYEXE=%LocalAppData%\Programs\Python\Python314\python.exe"
if not defined PYEXE if exist "%LocalAppData%\Programs\Python\Python313\python.exe" set "PYEXE=%LocalAppData%\Programs\Python\Python313\python.exe"
if not defined PYEXE if exist "%LocalAppData%\Programs\Python\Python312\python.exe" set "PYEXE=%LocalAppData%\Programs\Python\Python312\python.exe"
if not defined PYEXE for /f "delims=" %%P in ('where python 2^>nul') do ( set "PYEXE=%%P" & goto :have_py_run )
if not defined PYEXE for /f "delims=" %%P in ('where py 2^>nul') do ( set "PYEXE=%%P" & goto :have_py_run )
:have_py_run
if not defined PYEXE (
  echo Python 3 was not found. Install from python.org ^(tick "Add to PATH"^).
  if not "%~1"=="" exit /b 9009
  pause
  exit /b 9009
)
if not "%~1"=="" goto run
powershell -NoProfile -Command "$d='%~dp0'; if(Get-CimInstance Win32_Process -Filter \"Name='python.exe'\" | Where-Object { $_.CommandLine -like '*no_mapvote_bot.py*' -and $_.CommandLine -like ('*'+$d+'*') }){ exit 7 }" 1>nul 2>nul
if %errorlevel%==7 (
  echo A bot for THIS folder is already running - not starting a second one.
  timeout /t 8 >nul
  exit /b 7
)
:run
"%PYEXE%" -u "%~dp0no_mapvote_bot.py" %*
'''

_WIN_WEBCC = r'''@echo off
cd /d "%~dp0"
for %%I in ("%~dp0.") do set "NOST_FOLDER=%%~nxI"
title Nuke Option WEBCC - %NOST_FOLDER%
if not defined NOST_DATA_DIR set "NOST_DATA_DIR=%~dp0.nost-data"
if not exist "%NOST_DATA_DIR%" mkdir "%NOST_DATA_DIR%"
set "NOCC_PORT=__P__"
echo ============================================
echo   Nuke Option - Web Command Centre
echo   Folder: %NOST_FOLDER%
echo   http://127.0.0.1:__P__
echo ============================================
echo Stopping any old command-centre instances for THIS folder only...
powershell -NoProfile -Command "$d='%~dp0'; if (-not $d.EndsWith([char]92)) { $d=$d+[char]92 }; Get-CimInstance Win32_Process -Filter \"Name='python.exe'\" | Where-Object { $_.CommandLine -like '*cc_web.py*' -and $_.CommandLine -like ('*' + $d + '*') } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }" >nul 2>&1
ping -n 2 127.0.0.1 >nul
set "PYEXE="
if exist "%LocalAppData%\Programs\Python\Python314\python.exe" set "PYEXE=%LocalAppData%\Programs\Python\Python314\python.exe"
if not defined PYEXE if exist "%LocalAppData%\Programs\Python\Python313\python.exe" set "PYEXE=%LocalAppData%\Programs\Python\Python313\python.exe"
if not defined PYEXE if exist "%LocalAppData%\Programs\Python\Python312\python.exe" set "PYEXE=%LocalAppData%\Programs\Python\Python312\python.exe"
if not defined PYEXE for /f "delims=" %%P in ('where python 2^>nul') do ( set "PYEXE=%%P" & goto :have_py_web )
if not defined PYEXE for /f "delims=" %%P in ('where py 2^>nul') do ( set "PYEXE=%%P" & goto :have_py_web )
:have_py_web
if not defined PYEXE (
  echo Python 3 was not found. Install from python.org ^(tick "Add to PATH"^).
  pause
  exit /b 9009
)
echo Opening your browser... (Ctrl+F5 to hard-refresh if it looks stale)
start "" http://127.0.0.1:__P__
"%PYEXE%" -u "%~dp0cc_web.py"
echo.
echo Server stopped.
pause
'''

_WIN_START_THIS = r'''@echo off
REM ===========================================================================
REM  Nuke Option - START THIS SERVER (per-folder, folder-safe)
REM  Kills ONLY this folder's python, opens ONE bot + ONE web CC window tagged
REM  with THIS folder. Starting another server's copy will NOT touch this one.
REM ===========================================================================
for %%I in ("%~dp0..\.") do set "NOST_FOLDER=%%~nxI"
title Nuke Option LAUNCHER - %NOST_FOLDER%
set "NOST_ROOT=%~dp0..\"
echo ============================================
echo   NUKE OPTION - starting THIS server
echo   Folder: %NOST_FOLDER%
echo ============================================
set "NOST_DATA_DIR=%NOST_ROOT%.nost-data"
if not exist "%NOST_DATA_DIR%" mkdir "%NOST_DATA_DIR%"
echo Stopping any old copies for THIS folder only...
powershell -NoProfile -Command "$d=(Resolve-Path '%NOST_ROOT%').Path; if (-not $d.EndsWith([char]92)) { $d=$d+[char]92 }; $me=$PID; Get-CimInstance Win32_Process | Where-Object { $_.ProcessId -ne $me -and $_.CommandLine -and ($_.CommandLine -match 'run_keepalive') -and ($_.CommandLine -like ('*' + $d + '*')) } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }" >nul 2>&1
powershell -NoProfile -Command "$d=(Resolve-Path '%NOST_ROOT%').Path; if (-not $d.EndsWith([char]92)) { $d=$d+[char]92 }; Get-CimInstance Win32_Process -Filter \"Name='python.exe'\" | Where-Object { $_.CommandLine -match 'no_mapvote_bot\.py|cc_web\.py' -and $_.CommandLine -like ('*' + $d + '*') } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }" >nul 2>&1
ping -n 3 127.0.0.1 >nul
__GAMESTART__echo Opening the BOT window...
start "Nuke Option BOT - %NOST_FOLDER%" cmd /k "cd /d "%NOST_ROOT%" & set "NOST_DATA_DIR=%NOST_DATA_DIR%" & call run.bat"
ping -n 3 127.0.0.1 >nul
echo Opening the WEB COMMAND CENTRE window...
start "Nuke Option WEBCC - %NOST_FOLDER%" cmd /k "cd /d "%NOST_ROOT%" & set "NOST_DATA_DIR=%NOST_DATA_DIR%" & set "NOCC_PORT=__P__" & start "" http://127.0.0.1:__P__ & python -u "%NOST_ROOT%cc_web.py""
echo.
echo Done - bot + web CC opened for %NOST_FOLDER%. Leave them OPEN.
ping -n 5 127.0.0.1 >nul
'''

_WIN_GAMESTART = ('echo Starting the game server...\r\n'
                  'tasklist /FI "IMAGENAME eq NuclearOptionServer.exe" | find /I "NuclearOptionServer.exe" >nul '
                  '|| start "Nuclear Option - Server" "__GAMEDIR__StartServer.bat"\r\n'
                  'ping -n 6 127.0.0.1 >nul\r\n')

_WIN_WRAP_BOT = r'''@echo off
for %%I in ("%~dp0..\.") do set "NOST_FOLDER=%%~nxI"
title Nuke Option BOT - %NOST_FOLDER%
echo Folder: %NOST_FOLDER%
set "NOST_DATA_DIR=%~dp0..\.nost-data"
call "%~dp0..\run.bat"
pause >nul
'''

_WIN_WRAP_WEBCC = r'''@echo off
for %%I in ("%~dp0..\.") do set "NOST_FOLDER=%%~nxI"
title Nuke Option WEBCC - %NOST_FOLDER%
echo Folder: %NOST_FOLDER%
set "NOST_DATA_DIR=%~dp0..\.nost-data"
call "%~dp0..\webcc.bat"
pause >nul
'''

_SH_START_THIS = r'''#!/usr/bin/env bash
# Nuke Option - start THIS server (per-folder, folder-safe).
here="$(cd "$(dirname "$0")/.." && pwd)/"
export NOST_DATA_DIR="${here}.nost-data"; mkdir -p "$NOST_DATA_DIR"
export NOCC_PORT=__P__
# Kill ONLY this folder's bot + web CC (match the full folder path in the command line).
for pid in $(pgrep -f "${here}no_mapvote_bot.py") $(pgrep -f "${here}cc_web.py"); do kill "$pid" 2>/dev/null || true; done
__GAMESTART__( cd "$here" && python3 -u "${here}no_mapvote_bot.py" & )
sleep 2
( cd "$here" && NOCC_PORT=__P__ python3 -u "${here}cc_web.py" & )
sleep 3
( xdg-open "http://127.0.0.1:__P__" 2>/dev/null || true )
'''

_SH_GAMESTART = ('pgrep -f "__GAMEDIR__.*NuclearOptionServer" >/dev/null '
                 '|| ( cd "__GAMEDIR__" && bash "__GAMEDIR__start_server.sh" & )\nsleep 5\n')


def _write_bat(path, text):
    with open(path, "w", encoding="utf-8", newline="") as f:
        f.write(text.replace("\r\n", "\n").replace("\n", "\r\n"))


def _folder_safe_launchers(root, platform, web_port, own_pc=False, game_dir=None):
    """Generate the folder-safe, per-server launchers into `root` (where the bot + web CC live)
    and return the primary launcher's path (START THIS SERVER). Replaces the old name-blind
    'START EVERYTHING': every process kill is scoped to THIS folder, so installing/starting a
    2nd server in a sibling folder can never touch the 1st. Ports are pinned per folder. For an
    own-PC install, `game_dir` is where the local game server's StartServer lives."""
    p = str(int(web_port))
    win_gd = os.path.join(game_dir, "") if game_dir else ""            # trailing separator
    sh_gd = (game_dir.rstrip("/") + "/") if game_dir else ""
    start_here = os.path.join(root, "START HERE")
    if platform == "windows":
        os.makedirs(start_here, exist_ok=True)
        _write_bat(os.path.join(root, "run.bat"), _WIN_RUN)
        _write_bat(os.path.join(root, "webcc.bat"), _WIN_WEBCC.replace("__P__", p))
        gamestart = _WIN_GAMESTART.replace("__GAMEDIR__", win_gd) if own_pc else ""
        main = _WIN_START_THIS.replace("__GAMESTART__", gamestart).replace("__P__", p)
        main_path = os.path.join(start_here, "START THIS SERVER.bat")
        _write_bat(main_path, main)
        _write_bat(os.path.join(start_here, "1. Start Bot.bat"), _WIN_WRAP_BOT)
        _write_bat(os.path.join(start_here, "2. Start Web Command Centre.bat"), _WIN_WRAP_WEBCC)
        # remove any legacy name-blind launcher left in the folder
        for legacy in (os.path.join(root, "START EVERYTHING.bat"),
                       os.path.join(start_here, "START EVERYTHING.bat")):
            try:
                os.remove(legacy)
            except OSError:
                pass
        return main_path
    # Linux: a single folder-safe start script (best-effort folder-scoped kill).
    os.makedirs(start_here, exist_ok=True)
    gamestart = _SH_GAMESTART.replace("__GAMEDIR__", sh_gd) if own_pc else ""
    body = _SH_START_THIS.replace("__GAMESTART__", gamestart).replace("__P__", p)
    main_path = os.path.join(start_here, "start_this_server.sh")
    with open(main_path, "w", encoding="utf-8", newline="\n") as f:
        f.write(body)
    try:
        os.chmod(main_path, 0o755)
    except OSError:
        pass
    for legacy in (os.path.join(root, "start_everything.sh"),):
        try:
            os.remove(legacy)
        except OSError:
            pass
    return main_path


def _generate_start_everything(game_dir, platform, web_port):
    """Own-PC: generate the folder-safe per-server launchers in the TOOLKIT ROOT (where the bot +
    web CC live), and start the local game server in `game_dir` too."""
    return _folder_safe_launchers(ROOT, platform, web_port, own_pc=True, game_dir=game_dir)


def _place_local_gameside(game_dir, platform):
    """Local bundle: copy the bundled BepInEx loader + plugin from game-side/ into the
    game folder so the local server boots modded. game-side/common/ ships the shared pieces
    (BepInEx/core, BepInEx/plugins/NukeStats.dll); game-side/<platform>/
    ships the OS loader stub (Windows winhttp.dll+doorstop_config.ini, Linux libdoorstop.so+
    run_bepinex.sh). Best-effort: silently no-ops if this isn't a bundle. Returns placed subdirs."""
    import shutil
    gs = os.path.join(ROOT, "game-side")
    placed = []
    if not os.path.isdir(gs):
        return placed

    def copytree(src, dst):
        for base, _dirs, files in os.walk(src):
            rel = os.path.relpath(base, src)
            outd = dst if rel == "." else os.path.join(dst, rel)
            os.makedirs(outd, exist_ok=True)
            for fn in files:
                shutil.copy2(os.path.join(base, fn), os.path.join(outd, fn))

    for sub in ("common", platform):
        src = os.path.join(gs, sub)
        if os.path.isdir(src):
            try:
                copytree(src, game_dir)
                placed.append(sub)
            except OSError:
                pass
    return placed


def _stamp_versions():
    """Record the installed TOOLKIT version so the opt-in updater reports up-to-date / update-available
    accurately from the first run. A bundle ships bundle_version.txt (the toolkit version, e.g. 1.0);
    we stamp it into deployed_toolkit.json. (deployed_plugin.json is owned by the bot's deploy and holds
    the plugin's own version, for the directory listing — kept separate.)"""
    v = ""
    try:
        with open(os.path.join(ROOT, "bundle_version.txt"), encoding="utf-8") as f:
            v = f.read().strip()
    except OSError:
        return
    if not v:
        return
    try:
        with open(os.path.join(ROOT, "deployed_toolkit.json"), "w", encoding="utf-8") as f:
            json.dump({"version": v.lstrip("v")}, f, indent=2)
    except OSError:
        pass


def _apply_options(config, payload):
    """Map the wizard's 3 simple Options toggles onto config flags. The actual advertise-upload,
    global-leaderboard hook and auto-update pipelines are owned elsewhere (OPS) — this just
    records the user's intent so those pipelines can read it."""
    opts = payload.get("options") or {}
    config.setdefault("update", {})
    config["update"]["auto_check"] = bool(opts.get("auto_update", payload.get("auto_check", False)))
    if not config["update"].get("github_repo"):
        config["update"]["github_repo"] = payload.get("github_repo") or "Tomosnuclearbombos/nuclear-option-toolkit"
    return config


def _guard_config(new_server_id):
    """Refuse to overwrite a DIFFERENT server's config.json. The per-folder USER_DIR default
    already isolates installs; this is defence-in-depth for the case where someone points two
    installs at ONE data dir (via NOST_DATA_DIR / --data-dir). Returns an error string or None."""
    if FORCE or not os.path.exists(CONFIG):
        return None
    try:
        with open(CONFIG, encoding="utf-8") as f:
            existing = json.load(f)
    except (OSError, ValueError):
        return None
    old = ((existing.get("server") or {}).get("server_id") or "").strip()
    new = (new_server_id or "").strip()
    if old and new and old != new:
        return ("This data dir already holds a DIFFERENT server (server_id '%s'); refusing to "
                "overwrite its config. Install into a separate folder, or pass --data-dir for an "
                "explicit location (or --force to override). Data dir: %s" % (old, USER_DIR))
    return None


def _save(payload):
    scenario = payload.get("scenario", "external_linux")
    conn = payload.get("connection", {})
    _err = _guard_config(conn.get("server_id", ""))
    if _err:
        return {"ok": False, "error": _err}
    features = payload.get("features", {})
    srv = payload.get("server", {}) or {}          # the Server step (install/ports/name)
    # config.json — SAFE TO SHARE: no secrets.
    config = {
        "version": 1,
        "scenario": scenario,                      # own_pc | external_linux | external_windows
        "server": {
            "sftp_host": conn.get("sftp_host", ""),
            "sftp_port": int(conn.get("sftp_port") or 22),
            "sftp_user": conn.get("sftp_user", ""),
            "log_path": conn.get("log_path", "console.log"),
            "rcmd_host": conn.get("rcmd_host", ""),
            "rcmd_port": int(conn.get("rcmd_port") or 5550),
            "panel_url": conn.get("panel_url", ""),
            "server_id": conn.get("server_id", ""),
            "local_game_dir": conn.get("local_game_dir", ""),
            "power": payload.get("power", "pterodactyl"),
        },
        "web": {"port": int(payload.get("web_port") or 8770)},
        "features": features,
        "update": {"github_repo": payload.get("github_repo", ""),
                   "channel": payload.get("channel", "stable"),
                   "auto_check": bool(payload.get("auto_check", False))},
    }
    # fold in the Server step (ports / install location / name)
    game_dir = (srv.get("dir") or conn.get("local_game_dir") or "").strip()
    gp = int(srv.get("game_port") or 7777)
    qp = int(srv.get("query_port") or 7778)
    config["server"].update({
        "game_port": gp, "query_port": qp,
        "server_name": srv.get("server_name", ""),
        "max_players": int(srv.get("max_players") or 16),
        "install_mode": srv.get("mode", ""),       # install | existing
        "game_dir": game_dir,
    })
    # own-PC: create the log + a StartServer launcher in the game folder, so the server
    # boots modded and writes the log the bot reads — and the log path exists immediately.
    launch = None
    if scenario == "own_pc" and game_dir and os.path.isdir(game_dir):
        rcmd = int(conn.get("rcmd_port") or 5504)
        plat = "windows" if _platform().startswith("win") else "linux"
        try:
            config["server"]["gameside_placed"] = _place_local_gameside(game_dir, plat)
            script, logp = _generate_launch(game_dir, plat, rcmd)
            se = _generate_start_everything(game_dir, plat, int(payload.get("web_port") or 8770))
            launch = {"script": script, "log": logp, "start_everything": se}
            config["server"]["log_path"] = logp
            config["server"]["local_console_path"] = logp     # the bot tails this locally
            config["server"]["rcmd_host"] = "127.0.0.1"
            config["server"]["rcmd_port"] = rcmd
            config["server"]["power"] = "local"               # web CC controls the local process
            _sid = (srv.get("admin_sid") or "").strip()
            if _sid:
                config["server"]["admin_sids"] = [_sid]
        except OSError:
            launch = None
    # secrets.json — NEVER shared/committed (0600).
    secret = {
        "sftp_pass": conn.get("sftp_pass", ""),
        "api_key": conn.get("api_key", ""),
    }
    _apply_options(config, payload)
    _write_json_secure(CONFIG, config, secret=False)
    _write_json_secure(SECRETS, secret, secret=True)
    # also drop a ready-to-upload BepInEx cfg next to the config
    cfg_path = os.path.join(USER_DIR, "anz.nukestats.cfg")
    try:
        with open(cfg_path, "w", encoding="utf-8") as f:
            f.write(_render_plugin_cfg(features))
    except OSError:
        cfg_path = None
    # write DedicatedServerConfig.json (ports/name/players) — always a ready-to-upload copy
    # in the user dir, and directly into the game dir too when it's a local install.
    dsc = []
    if _serverconfig:
        try:
            p, _b = _serverconfig.write_config(USER_DIR, gp, qp, srv.get("server_name", ""),
                                               srv.get("max_players") or 0, srv.get("password", ""))
            dsc.append(p)
            if game_dir and os.path.isdir(game_dir):
                p2, _b2 = _serverconfig.write_config(game_dir, gp, qp, srv.get("server_name", ""),
                                                     srv.get("max_players") or 0, srv.get("password", ""))
                dsc.append(p2)
        except ValueError as e:
            dsc.append("PORTS INVALID: %s" % e)
    _stamp_versions()
    return {"ok": True, "config_path": CONFIG, "secrets_path": SECRETS, "cfg_path": cfg_path,
            "dedicated_config": dsc, "launch": launch}


def _api_plan(option):
    if not _fetcher:
        return {"error": "fetcher unavailable"}
    m = _fetcher.load_manifest()
    cfg = _read_json(CONFIG)
    out = []
    for dep_id in m["options"].get(option, []):
        dep = m["dependencies"][dep_id]
        if option in (dep.get("provided_by_host") or []):
            out.append({"id": dep_id, "name": dep.get("name"), "method": "host",
                        "version": "host-provided", "url": "", "note": "the panel/egg installs it server-side"})
        else:
            r = _fetcher.resolve(dep, cfg)
            r["id"] = dep_id
            r["name"] = dep.get("name")
            out.append(r)
    return {"option": option, "deps": out}


def _api_offline_list(option):
    if not _fetcher:
        return {"error": "fetcher unavailable"}
    m = _fetcher.load_manifest()
    out = []
    for dep_id in m["options"].get(option, []):
        dep = m["dependencies"][dep_id]
        off = dep.get("offline", {})
        manual = not (option in (dep.get("provided_by_host") or []) or dep["fetch"]["method"] in ("steamcmd", "bundled"))
        out.append({"id": dep_id, "name": dep.get("name"), "filename": off.get("filename"),
                    "url": off.get("official_url"), "instructions": off.get("instructions"), "manual": manual})
    return {"option": option, "items": out}


def _api_fetch(payload):
    if not _fetcher:
        return {"error": "fetcher unavailable"}
    option = payload.get("option", "")
    dest = payload.get("dest") or os.path.join(USER_DIR, "server")
    offline_dir = payload.get("offline_dir") or None
    m = _fetcher.load_manifest()
    results = []
    for dep_id in m["options"].get(option, []):
        try:
            results.append(_fetcher.fetch_one(dep_id, dest, offline_dir))
        except Exception as e:                           # noqa: BLE001
            results.append({"id": dep_id, "ok": False, "note": str(e)})
    return {"option": option, "dest": dest, "results": results}


def _platform():
    if _detect:
        try:
            return _detect.platform_target()
        except Exception:                                # noqa: BLE001
            pass
    return "win_x64" if sys.platform.startswith("win") else (
        "linux_x64" if sys.platform.startswith("linux") else "unknown")


def _pick_folder(initial=""):
    """Open a NATIVE folder chooser on the user's machine (the wizard runs locally).
    Returns {ok, path}. Falls back gracefully if tkinter isn't available (e.g. headless)."""
    try:
        import tkinter as tk
        from tkinter import filedialog
    except Exception:                                    # noqa: BLE001
        return {"ok": False, "error": "no native folder picker on this machine — type the path"}
    try:
        root = tk.Tk()
        root.withdraw()
        root.attributes("-topmost", True)
        path = filedialog.askdirectory(initialdir=(initial or os.path.expanduser("~")),
                                       title="Choose a folder")
        try:
            root.destroy()
        except Exception:                                # noqa: BLE001
            pass
        return {"ok": True, "path": path or ""}
    except Exception as e:                               # noqa: BLE001
        return {"ok": False, "error": str(e)}


def _api_install_server(payload):
    """Install the official dedicated server via SteamCMD into the chosen folder.
    Downloads SteamCMD FIRST if the user doesn't already have it, then launches the
    multi-GB game install detached (new console / logfile) so the wizard never blocks."""
    if not (_steamcmd and _fetcher):
        return {"ok": False, "error": "installer modules unavailable"}
    install_dir = (payload.get("dir") or "").strip()
    if not install_dir:
        return {"ok": False, "error": "choose an install folder first (use Browse...)"}
    plat = str(payload.get("platform") or _platform())
    platform = "windows" if plat.startswith("win") else "linux"
    sc_base = os.path.join(USER_DIR, "steamcmd")
    try:
        import shutil
        sc = (_steamcmd.find_steamcmd(sc_base, platform)
              or shutil.which("steamcmd") or shutil.which("steamcmd.exe") or "")
        had = bool(sc)
        if not sc:
            sc = _steamcmd.ensure_steamcmd(sc_base, platform, _fetcher)   # safety net: auto-download
        if not sc:
            return {"ok": False, "error": "SteamCMD isn't installed yet - click 'Install SteamCMD' first"}
        cmd, where = _steamcmd.launch_install(sc, install_dir)
        steam_note = ("Using SteamCMD. " if had else "Downloaded SteamCMD. ")
        launch_note = ("The game server is now downloading in a new console window. " if where == "console"
                       else "The game server is downloading (log: %s). " % where)
        return {"ok": True, "cmd": " ".join(cmd), "where": where, "downloaded_steamcmd": not had,
                "note": steam_note + launch_note + "It's several GB — when it finishes, click 'Check install'."}
    except Exception as e:                               # noqa: BLE001
        return {"ok": False, "error": "install failed: %s" % e}


def _pick_file(initial=""):
    """Native FILE chooser on the user's machine (the wizard runs locally). Returns {ok, path}."""
    try:
        import tkinter as tk
        from tkinter import filedialog
    except Exception:                                    # noqa: BLE001
        return {"ok": False, "error": "no native file picker on this machine — type the path"}
    try:
        if initial and os.path.isdir(initial):
            initdir = initial
        elif initial:
            initdir = os.path.dirname(initial) or os.path.expanduser("~")
        else:
            initdir = os.path.expanduser("~")
        root = tk.Tk()
        root.withdraw()
        root.attributes("-topmost", True)
        path = filedialog.askopenfilename(initialdir=initdir, title="Choose a file")
        try:
            root.destroy()
        except Exception:                                # noqa: BLE001
            pass
        return {"ok": True, "path": path or ""}
    except Exception as e:                               # noqa: BLE001
        return {"ok": False, "error": str(e)}


def _api_install_steamcmd(payload):
    """Step 1 of the own-PC install: download SteamCMD if missing and run it once so it
    self-updates BEFORE the (separate) server download. Detached, so it never blocks."""
    if not (_steamcmd and _fetcher):
        return {"ok": False, "error": "installer modules unavailable"}
    plat = str(payload.get("platform") or _platform())
    platform = "windows" if plat.startswith("win") else "linux"
    sc_base = os.path.join(USER_DIR, "steamcmd")
    try:
        had = bool(_steamcmd.find_steamcmd(sc_base, platform))
        sc = _steamcmd.ensure_steamcmd(sc_base, platform, _fetcher)
        if not sc:
            return {"ok": False, "error": "could not download SteamCMD (check your internet connection)"}
        cmd, where = _steamcmd.launch_selfupdate(sc)
        note = ("Downloaded SteamCMD; " if not had else "SteamCMD already present; ")
        note += ("it's self-updating in a new window — give it up to a minute, then click 'Check SteamCMD'."
                 if where == "console" else "self-updating (log: %s)." % where)
        return {"ok": True, "note": note}
    except Exception as e:                               # noqa: BLE001
        return {"ok": False, "error": "SteamCMD install failed: %s" % e}


def _api_open_folder(payload):
    """Open a folder in the OS file explorer (the wizard runs locally)."""
    path = (payload.get("path") or "").strip()
    if not path or not os.path.isdir(path):
        return {"ok": False, "error": "folder not found: %s" % (path or "(empty)")}
    try:
        if sys.platform.startswith("win"):
            os.startfile(path)                           # noqa
        else:
            import subprocess
            subprocess.Popen(["open" if sys.platform == "darwin" else "xdg-open", path])
        return {"ok": True}
    except Exception as e:                               # noqa: BLE001
        return {"ok": False, "error": str(e)}


def _api_install_deps(payload):
    """Install the Python packages the toolkit needs (Flask for the web CC, paramiko for
    SFTP, requests for the bot/updater) with pip. One click, so 'no module named flask' can't
    happen."""
    import subprocess
    pkgs = ["flask", "paramiko", "requests"]
    try:
        r = subprocess.run([sys.executable, "-m", "pip", "install", "--upgrade"] + pkgs,
                           capture_output=True, text=True, timeout=300)
        tail = (r.stdout + "\n" + r.stderr).strip().splitlines()[-6:]
        return {"ok": r.returncode == 0, "log": "\n".join(tail)}
    except Exception as e:                               # noqa: BLE001
        return {"ok": False, "error": str(e)}


def _api_run_launcher(payload):
    """Run a generated launcher (e.g. START EVERYTHING) on the user's machine."""
    path = (payload.get("path") or "").strip()
    if not path or not os.path.exists(path):
        return {"ok": False, "error": "launcher not found: %s" % (path or "(empty)")}
    try:
        if sys.platform.startswith("win"):
            os.startfile(path)                           # runs the .bat in its own window(s)
        else:
            import subprocess
            subprocess.Popen(["bash", path], cwd=os.path.dirname(path))
        return {"ok": True}
    except Exception as e:                               # noqa: BLE001
        return {"ok": False, "error": str(e)}


# ---------- Pterodactyl bundle: SFTP deploy + admin-side wiring ----------
def _write_power_files(conn):
    """cc_web/the bot read Pterodactyl power creds from apiKey.txt + panel.txt next to them
    (the bundle root = ROOT). Write them so power control works straight after install."""
    panel = (conn.get("panel_url") or "").strip().rstrip("/")
    key = (conn.get("api_key") or "").strip()
    sid = (conn.get("server_id") or "").strip()
    try:
        if key:
            kp = os.path.join(ROOT, "apiKey.txt")
            with open(kp, "w", encoding="utf-8") as f:
                f.write(key + "\n")
            try:
                os.chmod(kp, 0o600)
            except OSError:
                pass
        if panel:
            with open(os.path.join(ROOT, "panel.txt"), "w", encoding="utf-8") as f:
                f.write(panel + "\n" + (sid + "\n" if sid else ""))
    except OSError:
        pass


def _generate_admin_start_everything(root, platform, web_port):
    """External (Pterodactyl) bundle: the folder-safe per-server launchers (bot + web CC). The
    game runs in the container; the web CC power button + the bot drive it via the panel API.
    Folder-scoped so it can never touch another server's processes."""
    return _folder_safe_launchers(root, platform, web_port, own_pc=False)


def _write_admin_config(payload, conn, srv, features, gp, qp, relay_port):
    """Write the admin-side config.json + secrets.json + power files + the START EVERYTHING
    launcher for an external (Pterodactyl) install. Returns {start_everything: path}."""
    config = {
        "version": 1,
        "scenario": "external_linux",
        "server": {
            "sftp_host": conn.get("sftp_host", ""),
            "sftp_port": int(conn.get("sftp_port") or 2022),
            "sftp_user": conn.get("sftp_user", ""),
            "log_path": (conn.get("log_path") or "logs/console.log"),
            "rcmd_host": (conn.get("rcmd_host") or conn.get("sftp_host") or ""),
            "rcmd_port": relay_port,
            "panel_url": conn.get("panel_url", ""),
            "server_id": conn.get("server_id", ""),
            "power": "pterodactyl",
            "game_port": gp, "query_port": qp,
            "server_name": srv.get("server_name", ""),
            "max_players": int(srv.get("max_players") or 16),
        },
        "web": {"port": int(payload.get("web_port") or 8770)},
        "features": features,
        "update": {"github_repo": payload.get("github_repo", "Tomosnuclearbombos/nuclear-option-toolkit"),
                   "channel": payload.get("channel", "stable"),
                   "auto_check": bool(payload.get("auto_check", False))},
    }
    _sid = (srv.get("admin_sid") or "").strip()
    if _sid:
        config["server"]["admin_sids"] = [_sid]
    _apply_options(config, payload)
    secret = {"sftp_pass": conn.get("sftp_pass", ""), "api_key": conn.get("api_key", "")}
    _write_json_secure(CONFIG, config, secret=False)
    _write_json_secure(SECRETS, secret, secret=True)
    _write_power_files(conn)
    # also keep a ready copy of the generated plugin cfg next to the config
    try:
        with open(os.path.join(USER_DIR, "anz.nukestats.cfg"), "w", encoding="utf-8") as f:
            f.write(_render_plugin_cfg(features))
    except OSError:
        pass
    plat = "windows" if _platform().startswith("win") else "linux"
    se = _generate_admin_start_everything(ROOT, plat, int(payload.get("web_port") or 8770))
    _stamp_versions()
    return {"start_everything": se}


def _api_deploy(payload):
    """Pterodactyl bundle: push the bundled game-side payload into the container over SFTP,
    install the self-injecting launch wrapper, write the admin config + power creds, and
    generate the admin START EVERYTHING launcher. Returns {ok, log, launch}."""
    if _deployer is None or paramiko is None:
        return {"ok": False, "error": "paramiko isn't installed yet — go back to Welcome and click "
                "'Install Python packages', then retry."}
    if not _serverconfig:
        return {"ok": False, "error": "installer modules unavailable (serverconfig)"}
    conn = dict(payload.get("connection") or {})
    conn["panel_url"] = _normalize_panel_url(conn.get("panel_url"))   # forgiving: add https://, drop /server/...
    srv = payload.get("server", {}) or {}
    features = payload.get("features", {}) or {}
    game_side = os.path.join(ROOT, "game-side")
    if not os.path.isdir(os.path.join(game_side, "container-root")):
        return {"ok": False, "error": "this installer must be run from inside the downloaded "
                "Pterodactyl bundle (game-side/container-root/ not found next to it)."}
    sftp_params = dict(host=(conn.get("sftp_host") or "").strip(),
                       port=int(conn.get("sftp_port") or 2022),
                       user=(conn.get("sftp_user") or "").strip(),
                       password=conn.get("sftp_pass") or "")
    if not (sftp_params["host"] and sftp_params["user"] and sftp_params["password"]):
        return {"ok": False, "error": "SFTP host, username and password are required — fill the "
                "Connection step (your panel's Settings -> SFTP Details; the password is your "
                "panel account password)."}
    gp = int(srv.get("game_port") or 7777)
    qp = int(srv.get("query_port") or 7778)
    err = _serverconfig.validate_ports(gp, qp)
    if err:
        return {"ok": False, "error": err}
    relay_port = int(conn.get("rcmd_port") or 5550)
    server_cfg = dict(game_port=gp, query_port=qp, server_name=srv.get("server_name", ""),
                      max_players=int(srv.get("max_players") or 16),
                      password=srv.get("password", ""), relay_port=relay_port, framerate=60)
    plugin_cfg_text = _render_plugin_cfg(features)
    ptero = None
    panel = conn.get("panel_url") or ""
    key = (conn.get("api_key") or "").strip()
    if panel and key:
        ptero = _deployer.Ptero(panel, key, (conn.get("server_id") or "").strip())
    log = []

    def prog(stage, msg):
        log.append("[%s] %s" % (stage, msg))

    try:
        summary = _deployer.deploy(ROOT, sftp_params, server_cfg, plugin_cfg_text,
                                   ptero=ptero, manage_power=bool(ptero), progress=prog)
    except _deployer.DeployError as e:
        return {"ok": False, "error": str(e), "log": "\n".join(log)}
    except Exception as e:                               # noqa: BLE001
        return {"ok": False, "error": "deploy failed: %s" % e, "log": "\n".join(log)}
    _cfg_err = _guard_config(conn.get("server_id", ""))
    if _cfg_err:
        return {"ok": False, "error": _cfg_err, "log": "\n".join(log)}
    launch = _write_admin_config(payload, conn, srv, features, gp, qp, relay_port)
    return {"ok": True, "log": "\n".join(log), "summary": summary, "launch": launch,
            "powered": bool(ptero)}


# ----------------------------- HTTP server -----------------------------
class Handler(http.server.BaseHTTPRequestHandler):
    def _send(self, code, body, ctype="application/json"):
        b = body.encode("utf-8") if isinstance(body, str) else body
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(b)))
        self.end_headers()
        self.wfile.write(b)

    def _guard(self):
        # token must be present in the query (?t=) or X-Setup-Token header
        from urllib.parse import urlparse, parse_qs
        q = parse_qs(urlparse(self.path).query)
        tok = (q.get("t") or [None])[0] or self.headers.get("X-Setup-Token")
        return tok == TOKEN

    def log_message(self, *a):                     # quiet
        pass

    def do_GET(self):
        from urllib.parse import urlparse
        path = urlparse(self.path).path
        if path in ("/", "/index.html"):
            try:
                with open(os.path.join(HERE, "wizard.html"), encoding="utf-8") as f:
                    html = f.read().replace("__TOKEN__", TOKEN).replace("__BUNDLE_TYPE__", BUNDLE_TYPE)
                return self._send(200, html, "text/html; charset=utf-8")
            except OSError:
                return self._send(500, "wizard.html missing", "text/plain")
        if not self._guard():
            return self._send(403, json.dumps({"error": "bad token"}))
        if path == "/api/preflight":
            return self._send(200, json.dumps({"checks": _preflight(),
                                               "suggested_web_port": _suggest_web_port()}))
        if path == "/api/catalogue":
            return self._send(200, json.dumps({"settings": _load_catalogue()}))
        if path == "/api/current":
            return self._send(200, json.dumps({"config": _read_json(CONFIG),
                                                "has_secrets": os.path.exists(SECRETS),
                                                "user_dir": USER_DIR}))
        if path == "/api/detect":
            return self._send(200, json.dumps(_detect.suggest(_read_json(CONFIG)) if _detect else {"error": "detect unavailable"}))
        if path == "/api/plan":
            from urllib.parse import urlparse, parse_qs
            opt = (parse_qs(urlparse(self.path).query).get("option") or [""])[0]
            return self._send(200, json.dumps(_api_plan(opt)))
        if path == "/api/offline-list":
            from urllib.parse import urlparse, parse_qs
            opt = (parse_qs(urlparse(self.path).query).get("option") or [""])[0]
            return self._send(200, json.dumps(_api_offline_list(opt)))
        if path == "/api/server-status":
            from urllib.parse import urlparse, parse_qs
            d = (parse_qs(urlparse(self.path).query).get("dir") or [""])[0]
            exe = _steamcmd.server_exe(d) if _steamcmd else ""
            return self._send(200, json.dumps({"dir": d, "present": bool(exe), "exe": exe}))
        if path == "/api/pick-folder":
            from urllib.parse import urlparse, parse_qs
            ini = (parse_qs(urlparse(self.path).query).get("initial") or [""])[0]
            return self._send(200, json.dumps(_pick_folder(ini)))
        if path == "/api/pick-file":
            from urllib.parse import urlparse, parse_qs
            ini = (parse_qs(urlparse(self.path).query).get("initial") or [""])[0]
            return self._send(200, json.dumps(_pick_file(ini)))
        if path == "/api/steamcmd-status":
            from urllib.parse import urlparse, parse_qs
            plat = (parse_qs(urlparse(self.path).query).get("platform") or [""])[0] or _platform()
            platform = "windows" if str(plat).startswith("win") else "linux"
            present = bool(_steamcmd.find_steamcmd(os.path.join(USER_DIR, "steamcmd"), platform)) if _steamcmd else False
            return self._send(200, json.dumps({"present": present}))
        if path == "/api/bundle-info":
            game_side = os.path.join(ROOT, "game-side", "container-root")
            return self._send(200, json.dumps({"bundle_type": BUNDLE_TYPE,
                                                "root": ROOT,
                                                "game_side_present": os.path.isdir(game_side)}))
        return self._send(404, json.dumps({"error": "not found"}))

    def do_POST(self):
        from urllib.parse import urlparse
        path = urlparse(self.path).path
        if not self._guard():
            return self._send(403, json.dumps({"error": "bad token"}))
        try:
            n = int(self.headers.get("Content-Length") or 0)
            payload = json.loads(self.rfile.read(n).decode("utf-8")) if n else {}
        except (ValueError, OSError):
            payload = {}
        if path == "/api/test-sftp":
            return self._send(200, json.dumps(_test_sftp(payload)))
        if path == "/api/test-panel":
            return self._send(200, json.dumps(_test_panel(payload)))
        if path == "/api/save":
            return self._send(200, json.dumps(_save(payload)))
        if path == "/api/fetch":
            return self._send(200, json.dumps(_api_fetch(payload)))
        if path == "/api/deploy":
            return self._send(200, json.dumps(_api_deploy(payload)))
        if path == "/api/install-server":
            return self._send(200, json.dumps(_api_install_server(payload)))
        if path == "/api/install-steamcmd":
            return self._send(200, json.dumps(_api_install_steamcmd(payload)))
        if path == "/api/open-folder":
            return self._send(200, json.dumps(_api_open_folder(payload)))
        if path == "/api/run-launcher":
            return self._send(200, json.dumps(_api_run_launcher(payload)))
        if path == "/api/install-deps":
            return self._send(200, json.dumps(_api_install_deps(payload)))
        if path == "/api/shutdown":
            self._send(200, json.dumps({"ok": True}))
            if _SERVER is not None:
                threading.Thread(target=_SERVER.shutdown, daemon=True).start()
            return
        return self._send(404, json.dumps({"error": "not found"}))


def main():
    import argparse
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=0, help="bind a fixed port (default: a free one)")
    ap.add_argument("--no-browser", action="store_true", help="don't auto-open the browser")
    ap.add_argument("--data-dir", "--target", dest="data_dir", default="",
                    help="where this server's config/secrets live (default: <this folder>\\.nost-data)")
    ap.add_argument("--force", action="store_true",
                    help="allow overwriting a DIFFERENT server's config in the data dir")
    a, _ = ap.parse_known_args()
    if a.data_dir or a.force:
        global USER_DIR, CONFIG, SECRETS, FORCE
        FORCE = bool(a.force)
        if a.data_dir:
            USER_DIR = os.path.abspath(a.data_dir)
            CONFIG = os.path.join(USER_DIR, "config.json")
            SECRETS = os.path.join(USER_DIR, "secrets.json")
    port = a.port or int(os.environ.get("NOST_PORT") or 0) or _free_port()
    url = "http://127.0.0.1:%d/?t=%s" % (port, TOKEN)
    httpd = http.server.HTTPServer(("127.0.0.1", port), Handler)
    global _SERVER
    _SERVER = httpd
    print("Nuke Option setup wizard — open this in your browser if it doesn't pop up:")
    print("   " + url)
    print("(Ctrl+C to stop.)  User data dir: " + USER_DIR)
    if not a.no_browser:
        threading.Timer(0.6, lambda: webbrowser.open(url)).start()
    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        print("\nsetup wizard stopped.")


if __name__ == "__main__":
    main()
