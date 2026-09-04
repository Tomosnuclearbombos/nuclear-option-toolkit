#!/usr/bin/env python3
"""Autodetect for the setup installer: suggest the preferred hosting option, the
OS/arch target, and whether an online install is possible. All checks are local +
offline-safe (registry/filesystem scans need no network); the connectivity probe is
the only network call and fails soft.

    python detect.py            # print a detection summary (JSON)
"""
import glob
import json
import os
import platform
import socket
import ssl
import sys
import urllib.request

STEAM_APPID = "3930080"
SERVER_EXES = ("NuclearOptionServer.exe", "NuclearOptionServer.x86_64")


def _steam_roots():
    """Best-effort list of Steam library roots across OSes."""
    roots = []
    sysname = platform.system()
    if sysname == "Windows":
        try:
            import winreg
            for hive, key, val in (
                (winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam", "SteamPath"),
                (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
                (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\Valve\Steam", "InstallPath"),
            ):
                try:
                    with winreg.OpenKey(hive, key) as k:
                        roots.append(winreg.QueryValueEx(k, val)[0])
                except OSError:
                    pass
        except Exception:                                  # noqa: BLE001
            pass
    else:
        for p in ("~/.steam/steam", "~/.local/share/Steam", "~/Library/Application Support/Steam"):
            ep = os.path.expanduser(p)
            if os.path.isdir(ep):
                roots.append(ep)
    # add extra libraries from libraryfolders.vdf
    extra = []
    for r in list(roots):
        vdf = os.path.join(r, "steamapps", "libraryfolders.vdf")
        if os.path.exists(vdf):
            try:
                txt = open(vdf, encoding="utf-8", errors="ignore").read()
                import re
                for m in re.finditer(r'"path"\s*"([^"]+)"', txt):
                    extra.append(m.group(1).replace("\\\\", "\\"))
            except OSError:
                pass
    seen, out = set(), []
    for r in roots + extra:
        rn = os.path.normpath(r)
        if rn not in seen and os.path.isdir(rn):
            seen.add(rn)
            out.append(rn)
    return out


def detect_local_server():
    """Look for a locally-installed Nuclear Option dedicated server (own-PC scenario)."""
    for root in _steam_roots():
        sa = os.path.join(root, "steamapps")
        if os.path.exists(os.path.join(sa, "appmanifest_%s.acf" % STEAM_APPID)):
            # find the install dir from the manifest
            try:
                txt = open(os.path.join(sa, "appmanifest_%s.acf" % STEAM_APPID), encoding="utf-8", errors="ignore").read()
                import re
                m = re.search(r'"installdir"\s*"([^"]+)"', txt)
                if m:
                    d = os.path.join(sa, "common", m.group(1))
                    if os.path.isdir(d):
                        return d
            except OSError:
                pass
        # also scan common/ for the exe directly
        for exe in SERVER_EXES:
            hits = glob.glob(os.path.join(sa, "common", "*", exe))
            if hits:
                return os.path.dirname(hits[0])
    return None


def connectivity():
    """Can we reach the upstreams? (decides whether ONLINE install is offered.)"""
    out = {}
    for name, host in (("github", "api.github.com"), ("steam_cdn", "steamcdn-a.akamaihd.net")):
        try:
            req = urllib.request.Request("https://%s/" % host, method="HEAD",
                                         headers={"User-Agent": "NukeOptionToolkit"})
            urllib.request.urlopen(req, timeout=6, context=ssl.create_default_context())
            out[name] = True
        except Exception:                                  # noqa: BLE001
            # a bare TCP connect still counts as "some connectivity"
            try:
                socket.create_connection((host, 443), timeout=5).close()
                out[name] = True
            except Exception:                              # noqa: BLE001
                out[name] = False
    out["online_ok"] = any(out.values())
    return out


def platform_target():
    sysname = platform.system()
    if sysname == "Windows":
        return "win_x64"
    if sysname == "Linux":
        return "linux_x64"
    if sysname == "Darwin":
        return "macos"            # admin OS only — no server target
    return "unknown"


def suggest(cfg=None):
    cfg = cfg or {}
    local = detect_local_server()
    has_panel = bool((cfg.get("server", {}) or {}).get("panel_url") or (cfg.get("update", {})))
    tgt = platform_target()
    if local:
        scenario = "own_pc_windows" if tgt == "win_x64" else "own_pc_linux"
        reason = "found a local Nuclear Option dedicated server at %s" % local
    elif has_panel:
        scenario = "external_linux_ptero"
        reason = "a Pterodactyl panel is already configured"
    else:
        scenario = "external_linux_ptero"
        reason = "no local server found — defaulting to external (Pterodactyl); change if you host elsewhere"
    return {"suggested_option": scenario, "reason": reason,
            "local_game_dir": local, "platform_target": tgt,
            "connectivity": connectivity(),
            "note": "macOS can't host the server (no macOS depot) — drive an external Linux/Windows server instead" if tgt == "macos" else ""}


if __name__ == "__main__":
    cfg = {}
    try:
        ud = os.environ.get("NOST_DATA_DIR") or os.path.join(os.path.expanduser("~"), ".nuke-option-toolkit")
        cfg = json.load(open(os.path.join(ud, "config.json")))
    except Exception:                                      # noqa: BLE001
        pass
    print(json.dumps(suggest(cfg), indent=2))
