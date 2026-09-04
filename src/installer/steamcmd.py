#!/usr/bin/env python3
"""Install / locate the Nuclear Option dedicated server via SteamCMD (Steam app 3930080).

Used by the setup wizard's Server step. Either:
  * install for the user — download SteamCMD, then run `app_update 3930080 validate` in a
    NEW console window (Windows) / a logfile (Linux) so the long multi-GB download never
    blocks the wizard; the wizard polls server-status until the executable appears.
  * point at an existing install — just verify the server executable is present.

There is NO GitHub mirror of the binaries; SteamCMD is the only supported source.
"""
import os
import subprocess
import sys

APPID = 3930080
EXE_NAMES = ("NuclearOptionServer.exe", "NuclearOptionServer.x86_64")
_CREATE_NEW_CONSOLE = 0x00000010


def server_exe(install_dir):
    """Return the dedicated-server executable path under install_dir, or ''."""
    if not install_dir:
        return ""
    for n in EXE_NAMES:
        p = os.path.join(install_dir, n)
        if os.path.exists(p):
            return p
    return ""


def _steamcmd_name(platform):
    return "steamcmd.exe" if platform == "windows" else "steamcmd.sh"


def find_steamcmd(base_dir, platform):
    name = _steamcmd_name(platform)
    if not os.path.isdir(base_dir):
        return ""
    for root, _dirs, files in os.walk(base_dir):
        if name in files:
            return os.path.join(root, name)
    return ""


def ensure_steamcmd(base_dir, platform, fetcher):
    """Locate steamcmd under base_dir, downloading + extracting the bootstrap if needed."""
    exe = find_steamcmd(base_dir, platform)
    if exe:
        return exe
    dep = "steamcmd-bootstrap-win" if platform == "windows" else "steamcmd-bootstrap-linux"
    os.makedirs(base_dir, exist_ok=True)
    fetcher.fetch_one(dep, base_dir)
    return find_steamcmd(base_dir, platform)


def update_cmd(steamcmd_exe, install_dir, appid=APPID, validate=True):
    cmd = [steamcmd_exe, "+force_install_dir", install_dir,
           "+login", "anonymous", "+app_update", str(appid)]
    if validate:
        cmd.append("validate")
    cmd.append("+quit")
    return cmd


def launch_install(steamcmd_exe, install_dir, appid=APPID):
    """Start the (long) download detached so the wizard never blocks.
    Returns (cmd, where): where == 'console' on Windows, else the logfile path."""
    os.makedirs(install_dir, exist_ok=True)
    cmd = update_cmd(steamcmd_exe, install_dir, appid)
    if sys.platform.startswith("win"):
        subprocess.Popen(cmd, creationflags=_CREATE_NEW_CONSOLE)
        return cmd, "console"
    logf = os.path.join(install_dir, "steamcmd_install.log")
    subprocess.Popen(cmd, stdout=open(logf, "ab"), stderr=subprocess.STDOUT)
    return cmd, logf


def launch_selfupdate(steamcmd_exe):
    """Run steamcmd once (`+quit`) so it bootstraps/self-updates BEFORE the real install.
    SteamCMD's first run downloads itself and then exits, which is why a single combined
    run looks like it 'closes and does nothing'. Returns (cmd, where)."""
    cmd = [steamcmd_exe, "+quit"]
    if sys.platform.startswith("win"):
        subprocess.Popen(cmd, creationflags=_CREATE_NEW_CONSOLE)
        return cmd, "console"
    logf = os.path.join(os.path.dirname(steamcmd_exe) or ".", "steamcmd_selfupdate.log")
    subprocess.Popen(cmd, stdout=open(logf, "ab"), stderr=subprocess.STDOUT)
    return cmd, logf
