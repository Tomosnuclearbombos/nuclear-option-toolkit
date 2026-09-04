#!/usr/bin/env python3
"""Nuke Option Server Toolkit — opt-in GitHub updater (plugin/bot/web CC/installer, stable/nightly).

The install itself is fully local/offline. THIS tool is the separate, by-choice way a
server owner connects to GitHub to pull fixes when they're released.

    python updater.py check                          # what's available on your channel?
    python updater.py update                          # download + verify + INSTALL everything newer
    python updater.py update --component bot           # just one component
    python updater.py update --channel nightly          # one-off channel override
    python updater.py update --stage-only               # download + verify only, install later
    python updater.py update --deploy                    # also run the guarded PLUGIN deploy
                                                         # (the plugin restarts the match, so it
                                                         #  always needs the explicit --deploy)

`update` installs by default: every replaced file is backed up first (*.bak-<version>),
the run ends with a plain UPDATE SUMMARY of exactly what changed, and every run is
recorded in <data-dir>/update.log. Bot/web-CC changes need a process restart to load.

Channels (config update.channel, or --channel): `stable` = latest full release;
`nightly` = latest release INCLUDING pre-releases. Opt in once; it sticks.

Verify-before-apply (mandatory, identical for plugin AND bot):
  * SHA-256 of the download is checked against the release's published <asset>.sha256.
  * If a minisign <asset>.minisig + the bundled trusted.pub are present, the Ed25519
    signature is verified (minisign CLI, else pynacl/cryptography). If NO verifier is
    available it REFUSES to stage unless run with --i-understand-unsigned.
Plugin stages pending_plugin.dll (+ .json) for run.bat --deploy-plugin. Bot stages
pending_bot.py (+ .json); --apply backs up + replaces no_mapvote_bot.py (no auto-restart).
"""
import hashlib
import json
import os
import shutil
import ssl
import subprocess
import sys
import time
import urllib.request
import urllib.error

for _s in (sys.stdout, sys.stderr):                   # never crash printing on a cp1252 console
    try:
        _s.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError, OSError):
        pass

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)


def _default_user_dir():
    """Config dir resolution, folder-safe: env pin > this folder's .nost-data > legacy shared dir.
    The legacy-last order matters: falling back to the SHARED ~/.nuke-option-toolkit config from a
    per-folder install silently used the wrong channel ('I ran the update and nothing happened')."""
    env = os.environ.get("NOST_DATA_DIR")
    if env:
        return env
    local = os.path.join(ROOT, ".nost-data")            # per-folder install (installer v2)
    if os.path.isdir(local):
        return local
    return os.path.join(os.path.expanduser("~"), ".nuke-option-toolkit")


USER_DIR = _default_user_dir()
CONFIG = os.path.join(USER_DIR, "config.json")
UPDATE_LOG = os.path.join(USER_DIR, "update.log")
PUBKEY = os.path.join(HERE, "trusted.pub")          # bundled minisign public key (ships with the toolkit)


def _audit(line):
    """Append one line to update.log — EVERY run leaves a trace, so 'what did the update actually
    do' is always answerable from the log instead of guessed from file timestamps."""
    try:
        import datetime
        os.makedirs(USER_DIR, exist_ok=True)
        with open(UPDATE_LOG, "a", encoding="utf-8") as f:
            f.write("%s  %s\n" % (datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S"), line))
    except OSError:
        pass

# A release ships a matched set; both components carry the release tag as their version.
COMPONENTS = {
    "plugin": {
        "asset": "NukeStats.dll",
        "pending": os.path.join(ROOT, "pending_plugin.dll"),
        "meta": os.path.join(ROOT, "pending_plugin.json"),
        "deployed": os.path.join(ROOT, "deployed_plugin.json"),
        "apply": "deploy",          # applied via run.bat --deploy-plugin
    },
    "bot": {
        "asset": "no_mapvote_bot.py",
        "pending": os.path.join(ROOT, "pending_bot.py"),
        "meta": os.path.join(ROOT, "pending_bot.json"),
        "deployed": os.path.join(ROOT, "deployed_bot.json"),
        "target": os.path.join(ROOT, "no_mapvote_bot.py"),
        "apply": "replace",         # applied by backing up + replacing the file
    },
    "webcc": {                      # the web command centre (dashboard) — cc_web.py + webcc.html + deps
        "asset": "command-centre.zip",
        "pending": os.path.join(ROOT, "pending_webcc.zip"),
        "meta": os.path.join(ROOT, "pending_webcc.json"),
        "deployed": os.path.join(ROOT, "deployed_webcc.json"),
        "apply": "extract",         # applied by backing up + extracting the files into ROOT
    },
    "installer": {                  # the installer/updater tooling itself — WITHOUT this, updater
        "asset": "installer.zip",   # fixes never reach installed servers short of a full reinstall
        "pending": os.path.join(ROOT, "pending_installer.zip"),
        "meta": os.path.join(ROOT, "pending_installer.json"),
        "deployed": os.path.join(ROOT, "deployed_installer.json"),
        "apply": "extract-installer",   # extract into installer/ — NEVER touches trusted.pub (trust root)
    },
}


def _cfg():
    try:
        with open(CONFIG, encoding="utf-8") as f:
            return json.load(f)
    except (OSError, ValueError):
        return {}


def _deployed_version(comp):
    try:
        with open(COMPONENTS[comp]["deployed"], encoding="utf-8") as f:
            return (json.load(f) or {}).get("version", "")
    except (OSError, ValueError):
        return ""


TOOLKIT_DEPLOYED = os.path.join(ROOT, "deployed_toolkit.json")


def _toolkit_installed():
    """The installed TOOLKIT version — stamped at install + bumped on apply. The single version the
    updater compares against the latest release tag (releases are tagged by the toolkit version, e.g.
    v1.0). Separate from deployed_plugin.json, which the bot owns for the plugin's own version."""
    try:
        with open(TOOLKIT_DEPLOYED, encoding="utf-8") as f:
            return str((json.load(f) or {}).get("version", "") or "")
    except (OSError, ValueError):
        return ""


def _set_toolkit_installed(version):
    try:
        with open(TOOLKIT_DEPLOYED, "w", encoding="utf-8") as f:
            json.dump({"version": str(version).lstrip("v")}, f, indent=2)
    except OSError:
        pass


def _vt(v):
    """Loose semver tuple for comparison; non-numeric parts ignored."""
    out = []
    for part in str(v).lstrip("v").split("."):
        n = "".join(ch for ch in part if ch.isdigit())
        out.append(int(n) if n else 0)
    return tuple(out) or (0,)


def _get(url, token=None, raw=False):
    headers = {"User-Agent": "NukeOptionToolkit-Updater", "Accept": "application/vnd.github+json"}
    if token:
        headers["Authorization"] = "Bearer " + token
    req = urllib.request.Request(url, headers=headers)
    ctx = ssl.create_default_context()
    with urllib.request.urlopen(req, timeout=20, context=ctx) as r:
        data = r.read()
    return data if raw else json.loads(data.decode("utf-8", "replace"))


def _latest_release(repo, channel="stable", token=None):
    rels = _get("https://api.github.com/repos/%s/releases" % repo, token=token)
    if not isinstance(rels, list):
        return None
    eligible = [r for r in rels if not r.get("draft")
                and not (channel == "stable" and r.get("prerelease"))]  # stable skips pre-releases
    if not eligible:
        return None
    # Pick by highest (version, date) parsed from the tag — NOT by GitHub's release ordering.
    # Defence in depth: if a version-bumped run ever leaves an OLDER same-date nightly live, the
    # updater must never mistake the older one for "latest" just because of publish timing.
    return max(eligible, key=lambda r: _vt(r.get("tag_name") or r.get("name") or ""))


def _asset(rel, name):
    for a in rel.get("assets", []):
        if a.get("name") == name:
            return a.get("browser_download_url")
    return None


def _repo_and_channel(channel_override=None):
    cfg = _cfg()
    upd = cfg.get("update", {}) or {}
    repo = (upd.get("github_repo") or "").strip()
    channel = channel_override or upd.get("channel", "stable")
    return repo, channel


def check(components=("plugin", "bot"), channel_override=None, verbose=True):
    bad = [c for c in components if c not in COMPONENTS]
    if bad:                                            # friendly message + audit, not a raw KeyError
        print("Unknown component(s): %s. Valid: %s" % (", ".join(bad), ", ".join(COMPONENTS)))
        _audit("check REJECTED unknown component(s): %s" % ", ".join(bad))
        return None
    _reconcile_stale_pending()                        # local self-heal: drop staged files already installed
    repo, channel = _repo_and_channel(channel_override)
    if not repo:
        print("No GitHub repo configured. Re-run setup (or set update.github_repo in %s)." % CONFIG)
        return None
    token = os.environ.get("GITHUB_TOKEN")            # optional, for private repos / rate limits
    try:
        rel = _latest_release(repo, channel, token)
    except urllib.error.HTTPError as e:
        print("GitHub error %s for repo '%s' (private repo? set GITHUB_TOKEN)." % (e.code, repo))
        return None
    except Exception as e:                            # noqa: BLE001
        print("Could not reach GitHub: %s" % e)
        return None
    if not rel:
        print("No %s release found for %s." % (channel, repo))
        return None
    latest = rel.get("tag_name") or rel.get("name") or ""
    installed = _toolkit_installed()
    # PER-COMPONENT state: each component tracks its OWN deployed version, so a component that was
    # never applied (e.g. the web CC) is still seen as out of date even when the toolkit-level
    # marker was bumped by another component's deploy.
    out = {"repo": repo, "channel": channel, "release": rel, "latest": latest,
           "installed": installed, "components": {}}
    for comp in components:
        # A component with no per-component marker yet falls back to the bundle-stamped toolkit
        # baseline, so a FRESH install doesn't report every component as "needs update (have none)"
        # for the very version it just installed (which `update` would then reinstall as a no-op).
        c_have = _deployed_version(comp) or installed
        out["components"][comp] = {"in_release": _asset(rel, COMPONENTS[comp]["asset"]) is not None,
                                   "installed": c_have, "newer": _vt(latest) > _vt(c_have)}
    out["newer"] = any(c["in_release"] and c["newer"] for c in out["components"].values())
    if verbose:
        print("Repo:    %s  (%s channel)" % (repo, channel))
        print("Config:  %s" % CONFIG)
        print("Latest:  %s" % latest)
        for comp in components:
            st = out["components"][comp]
            tag = ("(not in this release)" if not st["in_release"]
                   else "<-- UPDATE (have %s)" % (st["installed"] or "none") if st["newer"]
                   else "up to date (%s)" % (st["installed"] or "?"))
            print("  %-10s %s" % (comp, tag))
        if out["newer"] and rel.get("body"):
            print("\nRelease notes:\n" + "\n".join("  " + ln for ln in rel["body"].splitlines()[:25]))
            print("\nRun `python updater.py update` to download + verify + install (backups kept).")
        _audit("check channel=%s latest=%s config=%s :: %s"
               % (channel, latest, CONFIG,
                  "; ".join("%s=%s" % (c, "update-available"
                                       if (out["components"][c]["newer"] and out["components"][c]["in_release"])
                                       else "not-in-release" if not out["components"][c]["in_release"]
                                       else "up-to-date") for c in components)))
    return out


def _verify(asset, data, rel, allow_unsigned):
    """SHA-256 + minisign verify-before-apply. Returns True to proceed, False to refuse."""
    sha = hashlib.sha256(data).hexdigest()
    sha_url = _asset(rel, asset + ".sha256")
    published = None
    if sha_url:
        try:
            published = _get(sha_url, raw=True).decode().split()[0].strip()
        except Exception:                            # noqa: BLE001
            published = None
    if published and published.lower() != sha.lower():
        print("  [FAIL] SHA-256 MISMATCH — refusing (download corrupt or tampered).")
        print("    expected %s\n    got      %s" % (published, sha))
        return False, sha
    print("  [ok] SHA-256 %s... (%s)" % (sha[:16], "matches published" if published else "no published hash"))

    sig_url = _asset(rel, asset + ".minisig")
    verified, how = (None, "no .minisig asset in the release")
    if sig_url:
        try:
            verified, how = _verify_minisig(data, _get(sig_url, raw=True))
        except Exception as e:                       # noqa: BLE001
            verified, how = (False, "could not fetch/verify signature: %s" % e)
    if verified is True:
        print("  [ok] signature %s" % how)
    elif verified is False:
        print("  [FAIL] signature verification FAILED (%s) — refusing." % how)
        return False, sha
    else:
        print("  [warn] signature NOT verified: %s" % how)
        if not allow_unsigned:
            print("    Refusing an unverified download. Re-run with --i-understand-unsigned to override,")
            print("    or install a verifier (`minisign` CLI, or `pip install pynacl`).")
            return False, sha
        print("    --i-understand-unsigned given: proceeding WITHOUT signature verification.")
    return True, sha


def _verify_minisig(data, sig_bytes):
    """Best-effort Ed25519 minisign verification of arbitrary bytes. Returns (verified, how)."""
    if not os.path.exists(PUBKEY):
        return (None, "no bundled public key (trusted.pub) — signature check skipped")
    try:
        import tempfile
        with tempfile.TemporaryDirectory() as td:
            dp = os.path.join(td, "asset.bin")
            sp = dp + ".minisig"
            open(dp, "wb").write(data)
            open(sp, "wb").write(sig_bytes)
            r = subprocess.run(["minisign", "-V", "-p", PUBKEY, "-m", dp],
                               capture_output=True, timeout=30)
            if r.returncode == 0:
                return (True, "verified with minisign CLI")
            return (False, "minisign CLI rejected the signature")
    except FileNotFoundError:
        pass
    except Exception as e:                            # noqa: BLE001
        return (False, "minisign CLI error: %s" % e)
    try:
        import base64
        pub_lines = [l for l in open(PUBKEY).read().splitlines() if l and not l.startswith("untrusted")]
        pub_raw = base64.b64decode(pub_lines[0])      # 2-byte alg + 8-byte keyid + 32-byte key
        pubkey = pub_raw[10:42]
        sig_lines = [l for l in sig_bytes.decode("utf-8", "replace").splitlines() if l and not l.startswith("untrusted")]
        sig_raw = base64.b64decode(sig_lines[0])
        alg, sig = sig_raw[:2], sig_raw[10:74]
        signed = data if alg == b"Ed" else hashlib.blake2b(data).digest()
        try:
            from nacl.signing import VerifyKey
            VerifyKey(pubkey).verify(signed, sig)
            return (True, "verified with pynacl (Ed25519)")
        except ImportError:
            from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PublicKey
            Ed25519PublicKey.from_public_bytes(pubkey).verify(sig, signed)
            return (True, "verified with cryptography (Ed25519)")
    except Exception as e:                            # noqa: BLE001
        return (False, "no signature verifier available / verify failed (%s)" % e)


def _stage(comp, rel, latest, data, sha, repo):
    c = COMPONENTS[comp]
    with open(c["pending"], "wb") as f:
        f.write(data)
    meta = {"version": str(latest).lstrip("v"), "size": len(data), "sha256": sha,
            "component": comp,
            "note": "Fetched from GitHub %s release %s by the opt-in updater." % (repo, latest)}
    with open(c["meta"], "w", encoding="utf-8") as f:
        json.dump(meta, f, indent=2)
    print("  [ok] staged %s %s (%d bytes) -> %s" % (comp, latest, len(data), os.path.basename(c["pending"])))


def _clear_pending(comp):
    """A consumed staged update must not linger: leftover pending_* files made the web CC say
    'update staged' forever after everything was already applied (field bug 2026-07-03)."""
    c = COMPONENTS[comp]
    for p in (c["pending"], c["meta"]):
        try:
            os.remove(p)
        except OSError:
            pass


def _reconcile_stale_pending():
    """Remove pending_* files that are already covered by what's deployed (staged version <= the
    component's deployed version). Heals installs that updated BEFORE applies cleaned up after
    themselves, and the plugin's .json sidecar that run.bat --deploy-plugin leaves behind (it
    archives only the .dll). Runs on every check/update, so existing servers self-heal. A pending
    file we can't date (no meta) or that is genuinely newer than what's deployed is left alone."""
    removed = []
    for comp, c in COMPONENTS.items():
        if not (os.path.exists(c["pending"]) or os.path.exists(c["meta"])):
            continue
        try:
            with open(c["meta"], encoding="utf-8") as f:
                meta_v = str((json.load(f) or {}).get("version", "") or "")
        except (OSError, ValueError):
            meta_v = ""
        dep_v = _deployed_version(comp)
        if not (meta_v and dep_v and _vt(dep_v) >= _vt(meta_v)):
            continue                                   # can't PROVE it's stale -> leave it
        for p in (c["pending"], c["meta"]):
            try:
                os.remove(p)
                removed.append(os.path.basename(p))
            except OSError:
                pass
    if removed:
        _audit("reconcile: removed stale staged files already deployed: %s" % ", ".join(removed))
        print("Cleaned up stale staged files (already installed): %s" % ", ".join(removed))
    return removed


def _apply_bot(latest):
    c = COMPONENTS["bot"]
    if not os.path.exists(c["pending"]):
        print("  nothing staged to apply.")
        return
    target = c["target"]
    if os.path.exists(target):
        bak = target + ".bak-" + str(latest).lstrip("v")
        shutil.copy2(target, bak)
        print("  backed up current bot -> %s" % os.path.basename(bak))
    shutil.copy2(c["pending"], target)
    with open(c["deployed"], "w", encoding="utf-8") as f:
        json.dump({"version": str(latest).lstrip("v")}, f, indent=2)
    _clear_pending("bot")
    print("  [ok] applied bot %s -> %s   (restart the bot to load it)" % (latest, os.path.basename(target)))


def _apply_webcc(latest):
    c = COMPONENTS["webcc"]
    if not os.path.exists(c["pending"]):
        print("  nothing staged to apply.")
        return
    import zipfile
    applied = []
    with zipfile.ZipFile(c["pending"]) as z:
        for name in z.namelist():
            base = os.path.basename(name)
            if name.endswith("/") or not base:
                continue
            target = os.path.join(ROOT, base)
            if os.path.exists(target):
                shutil.copy2(target, target + ".bak-" + str(latest).lstrip("v"))
            with z.open(name) as src, open(target, "wb") as dst:
                shutil.copyfileobj(src, dst)
            applied.append(base)
    with open(c["deployed"], "w", encoding="utf-8") as f:
        json.dump({"version": str(latest).lstrip("v")}, f, indent=2)
    _clear_pending("webcc")
    print("  [ok] applied web command centre %s: %s   (restart the web CC to load it)"
          % (latest, ", ".join(applied) or "(empty)"))


def _apply_installer(latest):
    """Extract installer.zip into installer/ (self-update of the tooling). The trust root
    (trusted.pub) is NEVER written from a download — a compromised release must not be able
    to rotate the key that verifies releases."""
    c = COMPONENTS["installer"]
    if not os.path.exists(c["pending"]):
        print("  nothing staged to apply.")
        return
    import zipfile
    applied = []
    with zipfile.ZipFile(c["pending"]) as z:
        for name in z.namelist():
            base = os.path.basename(name)
            # Normalise before the trust-root check: Windows is case-insensitive and strips
            # trailing dots/spaces, so an exact `== "trusted.pub"` compare would let a crafted
            # zip entry ("Trusted.pub", "trusted.pub.", "trusted.pub ", "trusted.pub::$DATA")
            # overwrite the key that verifies every future release. Reject any alias.
            norm = os.path.normcase(base).split(":")[0].rstrip(". ")
            if name.endswith("/") or not base or norm == "trusted.pub":
                continue
            target = os.path.join(HERE, base)
            if os.path.exists(target):
                shutil.copy2(target, target + ".bak-" + str(latest).lstrip("v"))
            with z.open(name) as src, open(target, "wb") as dst:
                shutil.copyfileobj(src, dst)
            applied.append(base)
    with open(c["deployed"], "w", encoding="utf-8") as f:
        json.dump({"version": str(latest).lstrip("v")}, f, indent=2)
    _clear_pending("installer")
    print("  [ok] applied installer tooling %s: %s" % (latest, ", ".join(applied) or "(empty)"))


# ---- daily auto-apply of staged updates -------------------------------------------------------
# Field gap (2026-07-03): Server 2 + Server 3 sat with STAGED updates overnight because the 05:00
# apply task was OPT-IN (a Y/N prompt in the deploy launcher nobody had answered yet) and older
# installs predate that launcher entirely. Auto-deploy is now the DEFAULT: installs register the
# folder-scoped task, and every successful `update` run self-heals a missing launcher/task and
# hardens the task settings (a missed 05:00 fires at the next opportunity instead of silently
# skipping the day). Opt out with `updater.py disable-autodeploy`; the choice is remembered.

def _autodeploy_task_name(root):
    return "NukeOption AutoUpdate - %s" % os.path.basename(os.path.normpath(root))


def _config_dir_for(root):
    """The config dir the updater will resolve FOR THIS root — mirrors _default_user_dir's order
    (env pin > this folder's existing .nost-data > legacy shared dir) WITHOUT creating anything.
    The opt-out marker must live where config lives, or a legacy (shared-config) install would
    write it where its own runs never look, and creating a stray .nost-data would flip a legacy
    install onto an empty local config."""
    env = os.environ.get("NOST_DATA_DIR")
    if env:
        return env
    local = os.path.join(root, ".nost-data")
    if os.path.isdir(local):
        return local
    return os.path.join(os.path.expanduser("~"), ".nuke-option-toolkit")


def _autodeploy_optout(root):
    return os.path.join(_config_dir_for(root), "no_autodeploy")


def _has_foreign_deploy_task(root, own_task):
    """True if some OTHER scheduled task already drives a daily DEPLOY for THIS folder (e.g. the
    Server-1-style NukeOption_DailyPluginDeploy -> deploy.bat). Adding our own 05:00 task alongside
    it would run two unsynchronised kill/restart pipelines against the same bot — the documented
    dup-bot / ranks-wipe class. Matched CONSERVATIVELY on two conditions so we neither miss a real
    deploy task nor over-skip: the action must reference this folder AT A PATH BOUNDARY (root + a
    separator, so 'Server' never matches 'Server2'), AND it must invoke a deploy/updater command
    (deploy.bat / run.bat --deploy-plugin / updater.py / the /auto launcher) — a plain backup, log
    rotation, or the nightly-publish task that merely mentions the folder must NOT count."""
    rootsep = (os.path.normpath(root) + os.sep).lower()
    try:
        r = _ps("$ErrorActionPreference='SilentlyContinue';"
                "Get-ScheduledTask | ForEach-Object { $n=$_.TaskName;"
                " foreach($a in $_.Actions){"
                "  $c=((([string]$a.Execute)+' '+([string]$a.Arguments))).ToLower();"
                "  if($c.Contains($env:NO_ROOTSEP) -and ("
                "     $c.Contains('deploy.bat') -or $c.Contains('--deploy-plugin')"
                "     -or $c.Contains('updater.py') -or $c.Contains('/auto'))){ $n; break } } }",
                extra_env={"NO_ROOTSEP": rootsep})
        names = [ln.strip() for ln in (r.stdout or "").splitlines() if ln.strip()]
    except (subprocess.SubprocessError, OSError):
        return False
    return any(n and n != own_task for n in names)


def plugin_deploy_guard(root=None):
    """Exit-code gate for the unattended :auto path: is it SAFE to run the match-restarting plugin
    deploy right now? Exit 0 = yes (a plugin is staged AND the bot's FRESH dashboard_state reports
    an empty server). Any non-zero = defer: 1 = nothing staged, 3 = can't confirm empty (no/stale
    state, or players online). Conservative: unattended runs never restart a populated match."""
    root = root or ROOT
    if not os.path.exists(COMPONENTS["plugin"]["pending"]):
        return 1
    state = os.path.join(root, "dashboard_state.json")
    try:
        if time.time() - os.path.getmtime(state) > 120:   # bot not currently updating it -> unknown
            return 3
        with open(state, encoding="utf-8") as f:
            data = json.load(f)
        online = data.get("online_count")
        # is_stale => online_count was ZEROED because the relay went silent; a zero we cannot
        # trust must never green-light a match-restarting deploy on a populated server.
        if data.get("is_stale"):
            return 3
    except (OSError, ValueError):
        return 3
    return 0 if online == 0 else 3


def _web_port(root):
    """Best-effort NOCC_PORT for the deploy-launcher template: read the port that was pinned into
    this folder's launchers at install time; default 8770 only if none can be found."""
    import re
    for cand in ("webcc.bat",
                 os.path.join("START HERE", "START THIS SERVER.bat"),
                 os.path.join("START HERE", "start_this_server.sh")):
        try:
            with open(os.path.join(root, cand), encoding="utf-8", errors="ignore") as f:
                m = re.search(r"NOCC_PORT=(\d{2,5})", f.read())
            if m:
                return m.group(1)
        except OSError:
            pass
    return "8770"


def _ps(script, timeout=60, extra_env=None):
    e = dict(os.environ)
    if extra_env:
        e.update(extra_env)
    return subprocess.run(["powershell", "-NoProfile", "-NonInteractive", "-Command", script],
                          capture_output=True, text=True, timeout=timeout, env=e)


def _task_status(task, bat):
    """State of the scheduled task: 'MISSING' | 'WRONG' (registered but its action does NOT point at
    THIS folder's bat — basename clash or a moved folder) | 'EXTERNAL' (the daily task exists but is
    driven by the ops morning_0500 job, which OWNS the 05:00 window) | 'OK_UNHARDENED' | 'OK'. Uses
    Get-ScheduledTask (structured — no XML-escaping pitfalls with '&' in a folder name) and passes
    the task name + bat path via env vars so they never need quoting into the PS command line."""
    try:
        r = _ps("$ErrorActionPreference='SilentlyContinue';"
                "$t=Get-ScheduledTask -TaskName $env:NO_TN;"
                "if(-not $t){'MISSING';return};"
                "$a=@($t.Actions)[0];"
                "$c=(([string]$a.Execute)+' '+([string]$a.Arguments)).ToLower();"
                "if($c.Contains('morning_0500')){'EXTERNAL';return};"
                "if(-not $c.Contains($env:NO_BAT.ToLower())){'WRONG';return};"
                "if($t.Settings.StartWhenAvailable -and -not $t.Settings.DisallowStartIfOnBatteries)"
                "{'OK'}else{'OK_UNHARDENED'}",
                extra_env={"NO_TN": task, "NO_BAT": bat})
    except (subprocess.SubprocessError, OSError):
        return "MISSING"
    s = (r.stdout or "").strip()
    return s.splitlines()[-1].strip() if s else "MISSING"


def _harden_task(task):
    """Flip ONLY the catch-up/battery flags, preserving any operator customization of the task's
    other settings (a full New-ScheduledTaskSettingsSet would wipe them). Idempotent."""
    _ps("$ErrorActionPreference='SilentlyContinue';"
        "$t=Get-ScheduledTask -TaskName $env:NO_TN; if(-not $t){return};"
        "$s=$t.Settings; $s.StartWhenAvailable=$true; $s.DisallowStartIfOnBatteries=$false;"
        "$s.StopIfGoingOnBatteries=$false;"
        "Set-ScheduledTask -TaskName $env:NO_TN -Settings $s | Out-Null",
        extra_env={"NO_TN": task})


def ensure_autodeploy(root=None, force=False, quiet=False):
    """Make staged updates apply themselves: write START HERE\\3. Deploy Staged Update.bat if this
    (older) install lacks it, register the folder-scoped daily 05:00 task pointing at THIS folder,
    and harden its settings (catch up a missed run; ignore battery state). Windows-only (on Linux
    `enable-autodeploy` prints the cron line instead). Respects a prior opt-out unless force=True.
    Never raises; returns True when auto-deploy is in place.
    quiet suppresses the no-change/informational chatter — state CHANGES always print."""
    root = root or ROOT
    if os.name != "nt":
        if not quiet:
            print("Auto-deploy scheduling is built in on Windows. On Linux add this cron entry:")
            print('  0 5 * * *  bash "%s" --auto'
                  % os.path.join(root, "START HERE", "deploy_staged_update.sh"))
        return False
    optout = _autodeploy_optout(root)
    if force:
        try:
            os.remove(optout)
        except OSError:
            pass
    elif os.path.exists(optout):
        if not quiet:
            print("Auto-deploy is opted out — re-enable with: updater.py enable-autodeploy")
        return False
    bat = os.path.join(root, "START HERE", "3. Deploy Staged Update.bat")
    task = _autodeploy_task_name(root)
    try:
        if _has_foreign_deploy_task(root, task):
            # This folder is already driven by another daily deploy task (e.g. Server-1's
            # NukeOption_DailyPluginDeploy). Do NOT stack a second 05:00 pipeline on the same bot.
            if not quiet:
                print("This folder already has a daily deploy task — not registering a second one "
                      "(two 05:00 pipelines would race the same bot). Remove the other task first "
                      "if you want the updater to manage deploys.")
            _audit("autodeploy: skipped — folder already has another deploy task")
            return True
        if not os.path.exists(bat):                    # older install predates the launcher — heal it
            if HERE not in sys.path:
                sys.path.insert(0, HERE)
            import setup as _setup                     # sibling module: ships in this installer/ dir
            os.makedirs(os.path.dirname(bat), exist_ok=True)
            _setup._write_bat(bat, _setup._WIN_DEPLOY_UPDATE.replace("__P__", _web_port(root)))
            _audit("autodeploy: wrote missing launcher %s" % bat)
            print("Created START HERE\\3. Deploy Staged Update.bat (this install predated it).")
        status = _task_status(task, bat)
        if status == "EXTERNAL":
            # The daily 05:00 task for this folder is already wired to the ops morning_0500 job
            # (which itself runs `updater.py update` first, then always game-restarts, with
            # catch-up deliberately OFF). That wiring is AUTHORITATIVE and must survive an operator
            # answering Y to "Keep the daily automatic 05:00 update ON": re-registering would
            # repoint the task at this launcher and _harden_task would switch StartWhenAvailable
            # back ON — the 2026-07-27 case where a missed 05:00 fired at 21:04 and relaunched the
            # stack on top of the live one. So honour it even under force.
            if not quiet:
                print("The daily 05:00 task is already wired to the ops morning_0500 job "
                      "(it runs this update, then restarts the game) — left exactly as it is.")
            _audit("autodeploy: kept external morning_0500 wiring on %r (force=%s)" % (task, force))
            return True
        # (Re)register when missing, when the registered action points ELSEWHERE (basename clash or
        # a moved folder — a name-only 'it exists' check used to report healthy while pointing at a
        # dead path, re-opening the very gap this closes), or on an explicit repair (force).
        if status in ("MISSING", "WRONG") or force:
            c = subprocess.run(["schtasks", "/Create", "/F", "/TN", task,
                                "/TR", '"%s" /auto' % bat, "/SC", "DAILY", "/ST", "05:00"],
                               capture_output=True, text=True, timeout=60)
            if c.returncode != 0:
                msg = (c.stderr or c.stdout or "").strip()
                _audit("autodeploy: task registration FAILED: %s" % msg)
                print("[warn] could not register the daily 05:00 update task: %s" % msg)
                return False
            _audit("autodeploy: %s daily 05:00 task %r"
                   % ("re-pointed" if status == "WRONG" else "registered", task))
            if status == "WRONG":
                print("Re-pointed the daily auto-update task at this folder: %s" % task)
            else:
                print("Registered the daily 05:00 auto-update task: %s" % task)
                print("  (turn off any time with: updater.py disable-autodeploy)")
            _harden_task(task)
        elif status == "OK_UNHARDENED":
            # Pre-existing task (e.g. from the old launcher) — bring catch-up/battery up to spec
            # ONCE, preserving its other settings. Next run sees 'OK' and does nothing.
            _harden_task(task)
            _audit("autodeploy: hardened pre-existing task %r" % task)
        # status == 'OK' -> correctly registered + hardened: nothing to do.
        return True
    except Exception as e:                             # noqa: BLE001  scheduling must never fail an update
        _audit("autodeploy: ensure failed: %s" % e)
        print("[warn] auto-deploy setup failed (updates still work manually): %s" % e)
        return False


def disable_autodeploy(root=None):
    """Remove the daily apply task and remember the choice. Returns True only if BOTH the opt-out
    marker was written AND the task is gone — a silent half-disable that keeps firing the 05:00
    bot/web-CC restart is worse than a clear failure, so each leg is checked and warns loudly."""
    root = root or ROOT
    optout = _autodeploy_optout(root)
    marked = True
    try:
        os.makedirs(os.path.dirname(optout), exist_ok=True)
        with open(optout, "w", encoding="utf-8") as f:
            f.write("operator opted out of daily auto-deploy\n")
    except OSError as e:
        marked = False
        print("[warn] could not write the opt-out marker (%s) — an update run may re-enable it." % e)
    deleted = True
    if os.name == "nt":
        try:
            d = subprocess.run(["schtasks", "/Delete", "/F", "/TN", _autodeploy_task_name(root)],
                               capture_output=True, text=True, timeout=60)
            out = (d.stderr or d.stdout or "").lower()
            deleted = d.returncode == 0 or "cannot find" in out or "does not exist" in out
            if not deleted:
                print("[warn] could not remove the 05:00 task (%s) — it may keep running."
                      % (d.stderr or d.stdout or "").strip())
        except (subprocess.SubprocessError, OSError) as e:
            deleted = False
            print("[warn] could not remove the 05:00 task: %s" % e)
    ok = marked and deleted
    _audit("autodeploy: disable by operator (marker=%s task_removed=%s)" % (marked, deleted))
    if ok:
        print("Daily auto-deploy disabled (re-enable with: updater.py enable-autodeploy).")
    return ok


def update(components=("plugin", "bot", "webcc", "installer"), channel_override=None,
           do_deploy=False, do_apply=True, allow_unsigned=False):
    info = check(components, channel_override, verbose=False)
    if not info:
        _audit("update FAILED before start (bad component / no repo / GitHub unreachable) argv=%r"
               % (sys.argv[1:],))
        return False
    rel, latest, repo = info["release"], info["latest"], info["repo"]
    did, result, failed = [], {}, False
    for comp in components:
        st = info["components"].get(comp, {})
        if not st.get("in_release"):
            print("- %s: not in the latest %s release — skipping." % (comp, info["channel"]))
            result[comp] = "not in this release"
            continue
        if not st.get("newer"):
            print("- %s: already up to date (%s)." % (comp, st.get("installed") or "?"))
            result[comp] = "already up to date (%s)" % (st.get("installed") or "?")
            continue
        asset = COMPONENTS[comp]["asset"]
        url = _asset(rel, asset)
        print("%s: downloading %s ..." % (comp, asset))
        try:
            data = _get(url, raw=True)
        except Exception as e:                          # noqa: BLE001  transient net error -> one component fails, run still summarises
            print("  [FAIL] download error: %s" % e)
            result[comp] = "DOWNLOAD FAILED (%s)" % e
            failed = True
            continue
        ok, sha = _verify(asset, data, rel, allow_unsigned)
        if not ok:
            result[comp] = "FAILED verification — not staged"
            failed = True
            continue
        _stage(comp, rel, latest, data, sha, repo)
        did.append(comp)
        result[comp] = "staged"

    if "plugin" in did:
        # --stage-only (do_apply False) means "download + verify only": it must win over --deploy,
        # never surprise-restart the live match.
        if do_deploy and do_apply:
            runbat = os.path.join(ROOT, "run.bat")
            if os.path.exists(runbat):
                subprocess.Popen(["cmd", "/c", runbat, "--deploy-plugin"], cwd=ROOT)
                result["plugin"] = "DEPLOY LAUNCHED via run.bat --deploy-plugin"
            else:
                result["plugin"] = "STAGED -- run.bat not found, run your deploy command to install"
        else:
            result["plugin"] = ("STAGED -- install with: run.bat --deploy-plugin, the web CC's "
                                "⏱ Schedule, or START HERE\\3. Deploy Staged Update (restarts the match)")
    if "bot" in did and do_apply:
        _apply_bot(latest)
        result["bot"] = "APPLIED %s -- restart the bot to load it" % latest
    if "webcc" in did and do_apply:
        _apply_webcc(latest)
        result["webcc"] = "APPLIED %s -- restart the web command centre to load it" % latest
    if "installer" in did and do_apply:
        _apply_installer(latest)
        result["installer"] = "APPLIED %s (takes effect next updater run)" % latest

    # ---- plain-English summary: what is on disk NOW. An update run must never end ambiguously.
    applied_any = any(r.startswith(("APPLIED", "DEPLOY")) for r in result.values())
    staged_any = any(r.startswith("STAGED") for r in result.values())
    if applied_any or (staged_any and do_deploy and do_apply):
        _set_toolkit_installed(latest)               # only bump the marker when something INSTALLED
    print("\n================ UPDATE SUMMARY ================")
    print("Channel: %-8s Latest: %s" % (info["channel"], latest))
    print("Config:  %s" % CONFIG)
    for comp in components:
        print("  %-10s %s" % (comp, result.get(comp, "?")))
    if failed:
        print(">> ONE OR MORE COMPONENTS FAILED (see above) — nothing broken was installed. <<")
    if not applied_any:
        if staged_any and not do_apply:
            print(">> Downloads were STAGED only (--stage-only). Re-run without it to install. <<")
        elif staged_any:
            print(">> Nothing was INSTALLED yet -- the staged plugin needs its deploy step (see above). <<")
        elif not failed:
            print(">> NO FILES WERE CHANGED BY THIS RUN. <<")
            if info["channel"] == "stable":
                print("   (You are on the STABLE channel -- for the latest nightly add: --channel nightly)")
    print("================================================")
    _audit("update channel=%s latest=%s config=%s :: %s"
           % (info["channel"], latest, CONFIG,
              "; ".join("%s=%s" % (c, result.get(c, "?")) for c in components)))
    if do_apply and not failed:
        ensure_autodeploy(quiet=True)                # default-ON self-heal: registers/hardens the daily
    return not failed                                # False if any requested component failed


ALL_COMPONENTS = ("plugin", "bot", "webcc", "installer")


def _parse(argv):
    comp = "all"                                     # default = everything; "run the update" updates
    channel = None
    for i, a in enumerate(argv):
        if a == "--component" and i + 1 < len(argv):
            comp = argv[i + 1]
        elif a == "--channel" and i + 1 < len(argv):
            channel = argv[i + 1]
    comps = ALL_COMPONENTS if comp == "all" else (comp,)
    return comps, channel


if __name__ == "__main__":
    args = sys.argv[1:]
    cmd = args[0] if args else "check"
    comps, channel = _parse(args)
    if cmd == "check":
        # non-zero when GitHub can't be reached / repo misconfigured, so scripts can tell
        info = check(ALL_COMPONENTS if "--component" not in args else comps, channel)
        sys.exit(0 if info is not None else 2)
    elif cmd == "update":
        # `update` INSTALLS by default (with backups). --stage-only restores download-only.
        # (--apply is still accepted for backward compatibility; it is now the default.)
        # Exit non-zero if any requested component failed (verify/download) or the check failed,
        # so `updater.py update && restart-bot` can't proceed on a refused/failed update.
        ok = update(comps, channel,
                    do_deploy=("--deploy" in args),
                    do_apply=("--stage-only" not in args),
                    allow_unsigned=("--i-understand-unsigned" in args))
        sys.exit(0 if ok else 1)
    elif cmd == "enable-autodeploy":
        # register (or re-point + harden) the daily 05:00 apply task for THIS folder.
        # On Linux ensure_autodeploy prints the cron guidance and returns False BY DESIGN — that is
        # success for this deliberate path, so only a real Windows registration failure exits 1.
        ok = ensure_autodeploy(force=True)
        sys.exit(0 if (ok or os.name != "nt") else 1)
    elif cmd == "disable-autodeploy":
        sys.exit(0 if disable_autodeploy() else 1)
    elif cmd == "plugin-deploy-guard":
        # 0 = safe to deploy the staged plugin now (empty server); non-zero = defer. Used by the
        # unattended :auto launcher so a daily catch-up never restarts a populated match.
        sys.exit(plugin_deploy_guard())
    else:
        print(__doc__)
