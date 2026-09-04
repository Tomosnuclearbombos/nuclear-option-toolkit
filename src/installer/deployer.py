#!/usr/bin/env python3
"""SFTP deployer for the Pterodactyl bundle — pushes the game-side payload into a
running Pterodactyl container and makes it boot MODDED with zero panel edits.

Why this exists
---------------
A Pterodactyl client API key can power a server on/off but CANNOT edit the raw
Startup command (that is an egg/admin-level setting). The egg's Startup just runs
the game binary by name. So to make the server load BepInEx + the NukeStats plugin
we use the proven "wrapper-rename" trick: rename the real ELF (drop the .x86_64
extension so Unity still finds NuclearOptionServer_Data) and drop a tiny POSIX
wrapper AT the binary's original name. The panel keeps launching that name, but now
it is our wrapper, which:

  * self-injects BepInEx via Doorstop env vars (DOORSTOP_* + LD_PRELOAD=libdoorstop.so)
    -> the plugin loads even though the panel Startup was never touched,
  * starts a localhost->WAN relay so the off-box bot can reach the game's
    localhost-only -ServerRemoteCommands port,
  * writes a stable logs/console.log the bot tails (and mirrors it to the panel),
  * exec's the real game so it stays PID 1.

This module is self-contained (paramiko + urllib only) and safe to re-run
(idempotent; aborts loudly if the container layout is unexpected — it never
guesses). The live production server's setup is NOT affected by this file.

Run standalone for a dry connectivity check:
    python deployer.py --check --host H --port 2022 --user U --password P
"""
import argparse
import io
import os
import re
import sys
import time

# stdout/stderr can be redirected to a cp1252 pipe on Windows; keep prints from ever
# crashing the deploy on a stray non-ASCII byte.
for _s in (sys.stdout, sys.stderr):
    try:
        _s.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError, OSError):
        pass

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import serverconfig  # noqa: E402  (same installer/ dir)

REMOTE_CMD_PORT = 5504           # in-container localhost port the game opens (-ServerRemoteCommands)
LAUNCH = "NuclearOptionServer.x86_64"   # what the panel runs -> becomes our wrapper
REAL   = "NuclearOptionServer"          # real ELF, extension dropped -> same _Data folder
DATA   = "NuclearOptionServer_Data"


class DeployError(Exception):
    """Raised on any condition that means we must NOT proceed (fail loud, never guess)."""


def _noop(_stage, _msg):
    pass


# ---------------------------------------------------------------------------
# The self-injecting launch wrapper (POSIX sh). {relay_port}/{framerate} filled in.
# This is a strict superset of the live wrapper: same relay/log/exec, PLUS the
# Doorstop exports so a fresh container boots modded without any panel Startup edit.
# ---------------------------------------------------------------------------
WRAPPER_TEMPLATE = (
    "#!/bin/sh\n"
    "# Nuclear Option toolkit launch wrapper. Installed by the installer.\n"
    "# Self-injects BepInEx (Doorstop), starts the localhost->WAN command relay,\n"
    "# writes a stable console log the bot tails, mirrors it to the panel, then\n"
    "# exec's the real game so it stays PID 1.\n"
    "# Undo: delete this file and rename '" + REAL + "' back to '" + LAUNCH + "'.\n"
    'HERE="$(pwd)"\n'
    'export LD_LIBRARY_PATH="$HERE:$HERE/linux64:$LD_LIBRARY_PATH"\n'
    "# --- BepInEx / Doorstop injection (idempotent; harmless if already set) ---\n"
    "export DOORSTOP_ENABLED=1\n"
    "export DOORSTOP_ENABLE=TRUE\n"
    'export DOORSTOP_TARGET_ASSEMBLY="$HERE/BepInEx/core/BepInEx.Preloader.dll"\n'
    'if [ -z "$LD_PRELOAD" ]; then export LD_PRELOAD="$HERE/libdoorstop.so"; '
    'else export LD_PRELOAD="$HERE/libdoorstop.so:$LD_PRELOAD"; fi\n'
    "mkdir -p ./logs\n"
    ": > ./logs/console.log\n"
    ": > ./logs/relay.log\n"
    "# --- relay: expose the localhost-only command port on 0.0.0.0:{relay_port} ---\n"
    '{{ for t in python3 python perl ncat socat nc busybox; do '
    'p=$(command -v "$t" 2>/dev/null) && echo "[probe] FOUND $t -> $p" '
    '|| echo "[probe] no $t"; done; }} >> ./logs/relay.log 2>&1\n'
    "if command -v python3 >/dev/null 2>&1; then\n"
    "  echo '[relay] using python3' >> ./logs/relay.log\n"
    "  python3 ./no_relay.py 0.0.0.0:{relay_port} 127.0.0.1:" + str(REMOTE_CMD_PORT)
    + " >> ./logs/relay.log 2>&1 &\n"
    "elif command -v perl >/dev/null 2>&1; then\n"
    "  echo '[relay] using perl' >> ./logs/relay.log\n"
    "  perl ./no_relay.pl 0.0.0.0:{relay_port} 127.0.0.1:" + str(REMOTE_CMD_PORT)
    + " >> ./logs/relay.log 2>&1 &\n"
    "elif command -v socat >/dev/null 2>&1; then\n"
    "  echo '[relay] using socat' >> ./logs/relay.log\n"
    "  socat TCP-LISTEN:{relay_port},fork,reuseaddr TCP:127.0.0.1:" + str(REMOTE_CMD_PORT)
    + " >> ./logs/relay.log 2>&1 &\n"
    "elif command -v ncat >/dev/null 2>&1; then\n"
    "  echo '[relay] using ncat' >> ./logs/relay.log\n"
    "  ncat -l 0.0.0.0 {relay_port} -k -c 'ncat 127.0.0.1 " + str(REMOTE_CMD_PORT)
    + "' >> ./logs/relay.log 2>&1 &\n"
    "else\n"
    "  echo '[relay] NO RELAY TOOL found in container' >> ./logs/relay.log\n"
    "fi\n"
    "tail -n +1 -F ./logs/console.log 2>/dev/null &\n"
    "exec ./" + REAL + " -logFile ./logs/console.log -limitframerate {framerate}"
    " -ServerRemoteCommands " + str(REMOTE_CMD_PORT) + ' "$@"\n'
)


def render_wrapper(relay_port=5550, framerate=60):
    return WRAPPER_TEMPLATE.format(relay_port=int(relay_port), framerate=int(framerate))


# ---------------------------------------------------------------------------
# Pterodactyl client API (power only) — urllib, no third-party deps.
# ---------------------------------------------------------------------------
_PANEL_SCHEME_RE = re.compile(r'^[a-z][a-z0-9+.-]*://', re.I)


def normalize_panel_url(url):
    """Forgiving Pterodactyl panel base: add https:// if no scheme, replace a wrong scheme
    (sftp://, ws://...), drop a pasted /server/... path and a trailing /api/client. A correct
    base is returned unchanged."""
    u = (url or "").strip()
    if not u:
        return ""
    m = _PANEL_SCHEME_RE.match(u)
    if m:
        if m.group(0).lower() not in ("http://", "https://"):
            u = "https://" + u[m.end():]
    else:
        u = "https://" + u
    i = u.lower().find("/server/")
    if i != -1:
        u = u[:i]
    u = u.rstrip("/")
    if u.lower().endswith("/api/client"):
        u = u[:-len("/api/client")].rstrip("/")
    return u


def _friendly_json(raw, ctype):
    body = raw.decode("utf-8", "replace") if isinstance(raw, (bytes, bytearray)) else (raw or "")
    if not body:
        return {}
    if "json" not in (ctype or "").lower() and body.lstrip()[:1] not in ("{", "["):
        raise DeployError("the panel URL returned a web page, not the API — check the panel URL "
                          "is your panel's base address (e.g. https://panel.host.net), no /server/... path")
    import json as _json
    return _json.loads(body)


class Ptero:
    UA = "nuclear-option-toolkit-installer"

    def __init__(self, base, key, server_id):
        self.base = normalize_panel_url(base)
        self.key = key
        self.server = server_id

    def _api(self, method, path, body=None):
        import json
        import ssl
        import urllib.request
        data = json.dumps(body).encode() if body is not None else None
        req = urllib.request.Request(self.base + path, data=data, method=method, headers={
            "Authorization": "Bearer " + self.key, "Accept": "application/json",
            "Content-Type": "application/json", "User-Agent": self.UA})
        with urllib.request.urlopen(req, context=ssl.create_default_context(), timeout=20) as r:
            ctype = r.headers.get("Content-Type", "")
            raw = r.read()
        return _friendly_json(raw, ctype)

    def discover(self):
        """Fill self.server from the key if not given. Returns the id or None."""
        if self.server:
            return self.server
        d = self._api("GET", "/api/client", None).get("data", [])
        self.server = d[0]["attributes"]["identifier"] if d else None
        return self.server

    def state(self):
        a = self._api("GET", "/api/client/servers/%s/resources" % self.server, None)
        return a.get("attributes", {}).get("current_state")

    def power(self, signal):
        self._api("POST", "/api/client/servers/%s/power" % self.server, {"signal": signal})

    def wait_state(self, want, timeout=120, progress=_noop):
        deadline = time.time() + timeout
        last = None
        while time.time() < deadline:
            try:
                st = self.state()
            except Exception as e:                       # noqa: BLE001
                st = "?(%s)" % e
            if st != last:
                progress("power", "server state: %s (waiting for '%s')" % (st, want))
                last = st
            if st == want:
                return True
            time.sleep(3)
        return False


# ---------------------------------------------------------------------------
# SFTP layer
# ---------------------------------------------------------------------------
class SFTPDeploy:
    def __init__(self, host, port, user, password, progress=_noop):
        self.host, self.port, self.user, self.password = host, int(port), user, password
        self.progress = progress
        self.ssh = self.sftp = None
        self._made = set()

    def connect(self):
        import paramiko
        self.ssh = paramiko.SSHClient()
        self.ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
        try:
            self.ssh.connect(self.host, port=self.port, username=self.user,
                             password=self.password, timeout=20,
                             look_for_keys=False, allow_agent=False)
        except Exception as e:                           # noqa: BLE001
            raise DeployError(
                "Could not connect over SFTP to %s:%s as %s.\n"
                "  -> Check the host/port/username/password from your panel's "
                "Settings -> SFTP Details. On Pterodactyl the SFTP password is your "
                "panel ACCOUNT password.\n  (%s)" % (self.host, self.port, self.user, e))
        self.sftp = self.ssh.open_sftp()
        return self

    def close(self):
        try:
            if self.ssh:
                self.ssh.close()
        except Exception:                                # noqa: BLE001
            pass

    def __enter__(self):
        return self.connect()

    def __exit__(self, *a):
        self.close()

    def mkremote(self, rpath):
        cur = ""
        for part in rpath.strip("/").split("/"):
            if not part:
                continue
            cur = cur + "/" + part if cur else part
            if cur in self._made:
                continue
            try:
                self.sftp.stat(cur)
            except IOError:
                try:
                    self.sftp.mkdir(cur)
                except IOError:
                    pass
            self._made.add(cur)

    def put_file(self, local, remote):
        d = os.path.dirname(remote)
        if d:
            self.mkremote(d)
        self.sftp.put(local, remote)

    def put_bytes(self, data, remote):
        if isinstance(data, str):
            data = data.encode("utf-8")
        d = os.path.dirname(remote)
        if d:
            self.mkremote(d)
        with self.sftp.open(remote, "wb") as f:
            f.write(data)

    def read_bytes(self, remote, maxb=5_000_000):
        with self.sftp.open(remote, "rb") as f:
            return f.read(maxb)

    def exists(self, remote):
        try:
            self.sftp.stat(remote)
            return True
        except IOError:
            return False

    def listdir(self, path="."):
        return set(self.sftp.listdir(path))


# ---------------------------------------------------------------------------
# Deploy steps
# ---------------------------------------------------------------------------
def _upload_tree(dep, local_root, remote_root="", progress=_noop):
    """Upload every file under local_root, mirroring structure to remote_root."""
    count = 0
    for base, _dirs, files in os.walk(local_root):
        rel = os.path.relpath(base, local_root).replace("\\", "/")
        rdir = "" if rel == "." else rel
        if remote_root:
            rdir = remote_root + "/" + rdir if rdir else remote_root
        for fn in files:
            rp = rdir + "/" + fn if rdir else fn
            dep.put_file(os.path.join(base, fn), rp)
            count += 1
            if count % 5 == 0 or count == 1:
                progress("files", "uploaded %d file(s)... (%s)" % (count, rp))
    return count


def setup_wrapper(dep, relay_port=5550, framerate=60, progress=_noop):
    """Install the self-injecting launch wrapper (the rename trick). Idempotent + safe."""
    names = dep.listdir(".")
    if LAUNCH not in names:
        raise DeployError(
            "'%s' was not found in the SFTP root. This installer expects a standard "
            "Nuclear Option dedicated-server container. (Found: %s ...)\n"
            "  -> Make sure you created the server with the Nuclear Option egg and that "
            "SFTP is pointed at the server root." % (LAUNCH, sorted(names)[:8]))
    if DATA not in names:
        raise DeployError(
            "'%s' was not found beside the game binary; aborting to be safe (this does "
            "not look like a Nuclear Option server root)." % DATA)

    with dep.sftp.open(LAUNCH, "rb") as f:
        magic = f.read(4)
    is_elf = magic == b"\x7fELF"

    if REAL in names:
        if is_elf:
            raise DeployError(
                "Inconsistent state: '%s' exists but '%s' is still a real binary. "
                "Not touching anything — inspect the container manually." % (REAL, LAUNCH))
        progress("wrapper", "wrapper already installed; rewriting it.")
    else:
        if not is_elf:
            raise DeployError(
                "'%s' is not a game binary and '%s' is missing — unexpected layout, "
                "aborting." % (LAUNCH, REAL))
        progress("wrapper", "renaming real launcher %s -> %s (keeps %s valid)" % (LAUNCH, REAL, DATA))
        try:
            dep.sftp.posix_rename(LAUNCH, REAL)
        except (IOError, OSError):
            dep.sftp.rename(LAUNCH, REAL)

    dep.put_bytes(render_wrapper(relay_port, framerate), LAUNCH)
    dep.sftp.chmod(LAUNCH, 0o755)
    try:
        dep.sftp.chmod(REAL, 0o755)
    except IOError:
        pass

    # verify
    with dep.sftp.open(LAUNCH, "rb") as f:
        head = f.read(9)
    with dep.sftp.open(REAL, "rb") as f:
        rmagic = f.read(4)
    if not (head.startswith(b"#!/bin/sh") and rmagic == b"\x7fELF"):
        raise DeployError(
            "Wrapper verification failed (wrapper head=%r, real magic=%r). Do NOT start "
            "the server; re-run or inspect manually." % (head, rmagic))
    progress("wrapper", "launch wrapper installed and verified.")


def merge_server_config(dep, game_port, query_port, server_name="", max_players=0,
                        password="", progress=_noop):
    """Download the container's DedicatedServerConfig.json, merge the toolkit fields
    (ports, ModdedServer=true, mission rotation, name/players/password), upload it back.
    Backs up the original to a .bak-<ts> in the container first."""
    import json
    err = serverconfig.validate_ports(game_port, query_port)
    if err:
        raise DeployError("Invalid ports: %s" % err)
    existing = None
    if dep.exists(serverconfig.CONFIG_NAME):
        try:
            raw = dep.read_bytes(serverconfig.CONFIG_NAME)
            existing = json.loads(raw.decode("utf-8"))
            bak = serverconfig.CONFIG_NAME + ".bak-" + time.strftime("%Y%m%d-%H%M%S")
            dep.put_bytes(raw, bak)
            progress("config", "backed up existing config -> %s" % bak)
        except Exception as e:                           # noqa: BLE001
            progress("config", "could not read existing config (%s); writing a fresh one." % e)
            existing = None
    cfg = serverconfig.build_config(existing, game_port, query_port, server_name,
                                    max_players, password, modded=True)
    dep.put_bytes(json.dumps(cfg, indent=2), serverconfig.CONFIG_NAME)
    progress("config", "wrote DedicatedServerConfig.json (ModdedServer=true, %d missions)."
             % len(cfg.get("MissionRotation", [])))
    return cfg


def deploy(bundle_dir, sftp_params, server_cfg, plugin_cfg_text=None,
           ptero=None, manage_power=True, progress=None):
    """Full Pterodactyl game-side deploy.

    bundle_dir    : the unzipped bundle root (contains game-side/).
    sftp_params   : dict(host, port, user, password).
    server_cfg    : dict(game_port, query_port, server_name, max_players, password,
                         relay_port, framerate).
    plugin_cfg_text: rendered anz.nukestats.cfg text (or None to ship the example).
    ptero         : a Ptero instance for stop->push->start, or None to skip power.
    manage_power  : if True and ptero given, stop before push + start after.

    Raises DeployError on any unsafe condition. Returns a summary dict.
    """
    progress = progress or _noop
    game_side = os.path.join(bundle_dir, "game-side")
    container_root = os.path.join(game_side, "container-root")
    if not os.path.isdir(container_root):
        raise DeployError(
            "Bundle is missing game-side/container-root/. This installer must be run from "
            "inside the downloaded bundle folder (got bundle_dir=%s)." % bundle_dir)

    relay_port = int(server_cfg.get("relay_port") or 5550)
    framerate = int(server_cfg.get("framerate") or 60)

    summary = {"files": 0, "powered": False}
    dep = SFTPDeploy(progress=progress, **sftp_params)
    dep.connect()
    try:
        # 1. stop the server (writing into a running install can corrupt it)
        if ptero and manage_power:
            try:
                ptero.discover()
                progress("power", "stopping the server before pushing files...")
                ptero.power("stop")
                if not ptero.wait_state("offline", timeout=120, progress=progress):
                    progress("power", "WARNING: server did not report 'offline' in time; "
                             "continuing, but if files look locked, stop it in the panel and re-run.")
                summary["powered"] = True
            except DeployError:
                raise
            except Exception as e:                       # noqa: BLE001
                progress("power", "WARNING: could not power the server off automatically (%s).\n"
                         "  -> Stop the server in the panel, then re-run. Continuing anyway." % e)

        # 2. push the container-root tree (BepInEx pack + plugin + relay)
        progress("files", "uploading BepInEx, the plugin and the relay...")
        summary["files"] = _upload_tree(dep, container_root, "", progress)

        # 3. plugin config
        cfg_remote = "BepInEx/config/anz.nukestats.cfg"
        if plugin_cfg_text:
            dep.put_bytes(plugin_cfg_text, cfg_remote)
            progress("files", "wrote BepInEx/config/anz.nukestats.cfg")
        else:
            example = os.path.join(game_side, "anz.nukestats.cfg.example")
            if os.path.exists(example) and not dep.exists(cfg_remote):
                dep.put_file(example, cfg_remote)
                progress("files", "wrote default BepInEx/config/anz.nukestats.cfg")

        # 4. server config (merge)
        merge_server_config(dep, server_cfg["game_port"], server_cfg["query_port"],
                            server_cfg.get("server_name", ""),
                            server_cfg.get("max_players", 0),
                            server_cfg.get("password", ""), progress)

        # 5. the self-injecting wrapper (the critical step)
        setup_wrapper(dep, relay_port, framerate, progress)

        # 6. restart
        if ptero and manage_power:
            try:
                progress("power", "starting the server...")
                ptero.power("start")
                summary["powered"] = True
            except Exception as e:                       # noqa: BLE001
                progress("power", "WARNING: could not start the server automatically (%s).\n"
                         "  -> Start it from the panel." % e)
    finally:
        dep.close()

    progress("done", "deploy complete: %d files pushed. On first boot, watch the panel "
             "console for 'NukeStats loaded'." % summary["files"])
    return summary


# ---------------------------------------------------------------------------
# Standalone CLI (connectivity / dry checks; the wizard imports the functions)
# ---------------------------------------------------------------------------
def main(argv=None):
    ap = argparse.ArgumentParser(description="Pterodactyl SFTP deployer (toolkit)")
    ap.add_argument("--check", action="store_true",
                    help="connect over SFTP and report the container layout, then exit")
    ap.add_argument("--host"); ap.add_argument("--port", default="2022")
    ap.add_argument("--user"); ap.add_argument("--password")
    ap.add_argument("--print-wrapper", action="store_true",
                    help="print the launch wrapper that would be installed")
    ap.add_argument("--relay-port", default="5550")
    ap.add_argument("--framerate", default="60")
    a = ap.parse_args(argv)

    if a.print_wrapper:
        sys.stdout.write(render_wrapper(a.relay_port, a.framerate))
        return 0

    if a.check:
        if not (a.host and a.user and a.password):
            print("--check needs --host --user --password")
            return 2

        def prog(stage, msg):
            print("[%s] %s" % (stage, msg))
        dep = SFTPDeploy(a.host, a.port, a.user, a.password, progress=prog)
        try:
            dep.connect()
            names = dep.listdir(".")
            print("[check] connected. SFTP root has %d entries." % len(names))
            for k in (LAUNCH, REAL, DATA, "BepInEx", "NuclearOption-Missions"):
                print("  %-26s %s" % (k, "present" if k in names else "absent"))
            with dep.sftp.open(LAUNCH, "rb") as f:
                magic = f.read(4)
            print("  %s magic=%r (%s)" % (LAUNCH, magic,
                  "ELF/real" if magic == b"\x7fELF" else "wrapper/other"))
        except DeployError as e:
            print("[check] FAILED:\n%s" % e)
            return 1
        finally:
            dep.close()
        return 0

    ap.print_help()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
