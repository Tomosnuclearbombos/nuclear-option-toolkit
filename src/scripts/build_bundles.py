#!/usr/bin/env python3
"""Build the per-server-type BUNDLES for the Nuclear Option toolkit.

A "bundle" is a pre-assembled, ready-to-download folder for ONE hosting type. The user
downloads the bundle for their type (it already contains everything), runs the installer
inside it, fills in their details (each ask explains what it is + where to find it), and
it just works:
  * pterodactyl - the installer pushes BepInEx + the plugin + config into the container
    over SFTP and installs a self-injecting wrapper (boots modded, no panel edits). The
    bot + web command centre run on the admin PC.
  * local       - everything on one PC; SteamCMD installs the game binaries, the installer
    copies the bundled BepInEx + plugin into the game folder and wires it up.
  * manual      - for hand-hosting: ships both BepInEx packs + the plugin + detailed
    drag-drop instructions; the installer just authors the config.

HOW IT STAYS SAFE
  This reuses scripts/build_public_repo.py as a library: it first builds the CLEAN public
  tree (whitelist + scrub + secret scan), then stages each bundle from that CLEAN tree PLUS
  the binary assets the public tree deliberately excludes (the BepInEx pack(s) + the built
  NukeStats.dll), and RE-RUNS the secret scanner over every assembled bundle. The build
  aborts (exit 2) on any hard secret/PII hit in the clean tree OR any bundle.

Usage:
    python scripts/build_bundles.py --out ../dist
    python scripts/build_bundles.py --out ../dist --types pterodactyl --force
    python scripts/build_bundles.py --out ../dist --check        # stage + scan, no zip
"""
import argparse
import io
import json
import os
import shutil
import sys
import zipfile

for _s in (sys.stdout, sys.stderr):
    try:
        _s.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError, OSError):
        pass

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, HERE)
sys.path.insert(0, os.path.join(ROOT, "installer"))
import build_public_repo as bpr        # the clean-room builder/scrubber/scanner (reused)
import serverconfig                     # DedicatedServerConfig template


def _toolkit_version():
    """The version used to tag releases + name the bundle folders. Defaults to the plugin version
    (e.g. 0.9.15) — that's the marker the project uses. An optional VERSION file at the repo root
    can override it, but by default there isn't one."""
    try:
        with open(os.path.join(ROOT, "VERSION"), encoding="utf-8") as f:
            v = f.read().strip().lstrip("v")
        if v:
            return v
    except OSError:
        pass
    return bpr._plugin_version()          # default: the plugin version (0.9.1x)

# Binary assets the CLEAN public tree excludes — taken from the SOURCE repo.
SRC_BEPINEX_LINUX = os.path.join(ROOT, "NukeStats", "bepinex_pack")          # libdoorstop.so + core
SRC_BEPINEX_WIN = os.path.join(ROOT, "NukeStats", "bepinex_pack_win")        # winhttp.dll + doorstop_config.ini (vendored)
SRC_DLL = os.path.join(ROOT, "NukeStats", "bin", "Release", "NukeStats.dll")
SRC_RELAY = os.path.join(ROOT, "relay")


def dll_version_ok(src_dll=None, version=None):
    """Does the compiled DLL actually carry the plugin `Version` const that is in SOURCE?

    Every ship path (bundles, updater asset, GitHub release) copies whatever binary happens to be
    sitting in bin/Release. Bump the const, forget the rebuild, and the release is tagged v1.2.0
    while the plugin's own load banner — which the panel header prefers over deployed_plugin.json —
    still says the old number. The const is embedded twice: UTF-8 in the [BepInPlugin] attribute
    blob and UTF-16 in the "NukeStats <ver> loaded" log literal, so we look for both.
    """
    v = version or bpr._plugin_version()
    try:
        with open(src_dll or SRC_DLL, "rb") as f:
            blob = f.read()
    except OSError:
        return False
    return v.encode("utf-8") in blob or v.encode("utf-16-le") in blob

# Admin-side files (live at the bundle root; the bot/web CC + their launchers + templates).
ADMIN_FILES = [
    "no_mapvote_bot.py", "cc_web.py", "webcc.html", "command_centre.py", "map_atlas.py",
    "settings_catalogue.json", "heartland_map.png", "ignus_map.png",
    "config.example.json", "anz.nukestats.cfg.example",
    "webcc.bat", "commandcentre.bat", "status.bat", "say.bat",
]
INSTALLER_BASE = ["setup.py", "wizard.html", "serverconfig.py", "detect.py", "sources.json"]

BUNDLES = {
    "pterodactyl": {
        "title": "Pterodactyl (hosted Linux panel)",
        "installer": INSTALLER_BASE + ["deployer.py", "fetcher.py", "offline.py", "updater.py"],
        "power_templates": ["apiKey.txt.example", "panel.txt.example"],
        "needs_win_pack": False,
    },
    "local": {
        "title": "Local (your own PC)",
        "installer": INSTALLER_BASE + ["steamcmd.py", "fetcher.py", "offline.py", "updater.py"],
        "power_templates": [],
        "needs_win_pack": True,
    },
    "manual": {
        "title": "Manual (host it by hand)",
        "installer": INSTALLER_BASE + ["offline.py"],
        "power_templates": ["apiKey.txt.example", "panel.txt.example"],
        "needs_win_pack": True,
    },
}


# ---------------------------------------------------------------------------
def _copytree(src, dst):
    for base, _dirs, files in os.walk(src):
        rel = os.path.relpath(base, src)
        outd = dst if rel == "." else os.path.join(dst, rel)
        os.makedirs(outd, exist_ok=True)
        for fn in files:
            shutil.copy2(os.path.join(base, fn), os.path.join(outd, fn))


def _copy(src, dst):
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    shutil.copy2(src, dst)


def _write(path, text):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)


def build_clean(out):
    """Build the CLEAN public tree at out/_clean via build_public_repo (abort on hard hits)."""
    clean = os.path.join(out, "_clean")
    bpr.REAL_SID, bpr.REAL_IP, bpr.REAL_HOST = bpr._load_targets()
    bpr.PLUGIN_VERSION = bpr._plugin_version()
    if os.path.exists(clean):
        shutil.rmtree(clean)
    os.makedirs(clean)
    copied = []
    bpr.copy_root_files(clean, copied)
    bpr.copy_trees(clean, copied)
    bpr.apply_scrubs(clean)
    bpr.harden_gitignore(clean)
    hard, warn = bpr.scan(clean)
    if hard:
        print("FAIL: clean tree has %d HARD secret hit(s):" % len(hard))
        for rel, ln, kind, val in hard:
            print("  !! %-22s %s:%d %s" % (kind, rel, ln, val))
        raise SystemExit(2)
    print("[clean] built %d files, scan CLEAN, plugin v%s" % (len(copied), bpr.PLUGIN_VERSION))
    return clean


def _dsc_template():
    cfg = serverconfig.build_config({}, 7777, 7778, modded=True)
    return json.dumps(cfg, indent=2)


def _stage_admin(clean, bundle, spec):
    for name in ADMIN_FILES + spec.get("power_templates", []):
        src = os.path.join(clean, name)
        if os.path.exists(src):
            _copy(src, os.path.join(bundle, name))
    for name in spec["installer"]:
        src = os.path.join(clean, "installer", name)
        if os.path.exists(src):
            _copy(src, os.path.join(bundle, "installer", name))
        else:
            print("  [warn] installer/%s not in clean tree" % name)
    # the updater's trust root (minisign public key) must ship in EVERY bundle so the opt-in
    # updater can verify signed releases before applying them.
    tp = os.path.join(clean, "installer", "trusted.pub")
    if os.path.exists(tp):
        _copy(tp, os.path.join(bundle, "installer", "trusted.pub"))
    else:
        print("  [warn] installer/trusted.pub missing — bundled updater can't verify signatures")
    # license + a couple of reference docs
    if os.path.exists(os.path.join(clean, "LICENSE")):
        _copy(os.path.join(clean, "LICENSE"), os.path.join(bundle, "LICENSE"))
    for d in ("FEATURES.md", "COMMANDS.md", "MODERATION.md", "ARCHITECTURE.md"):
        s = os.path.join(clean, "docs", d)
        if os.path.exists(s):
            _copy(s, os.path.join(bundle, "docs", d))


def _stage_bepinex_core(dst_bepinex):
    """Copy BepInEx/core (the shared, platform-neutral managed assemblies)."""
    core_src = os.path.join(SRC_BEPINEX_LINUX, "BepInEx", "core")
    _copytree(core_src, os.path.join(dst_bepinex, "core"))


def stage_pterodactyl(clean, bundle):
    cr = os.path.join(bundle, "game-side", "container-root")
    # whole linux BepInEx pack (libdoorstop.so, run_bepinex.sh, .doorstop_version, BepInEx/core)
    for fn in os.listdir(SRC_BEPINEX_LINUX):
        sp = os.path.join(SRC_BEPINEX_LINUX, fn)
        if os.path.isfile(sp):
            _copy(sp, os.path.join(cr, fn))
    _stage_bepinex_core(os.path.join(cr, "BepInEx"))
    _copy(SRC_DLL, os.path.join(cr, "BepInEx", "plugins", "NukeStats.dll"))
    for fn in ("no_relay.py", "no_relay.pl"):
        _copy(os.path.join(SRC_RELAY, fn), os.path.join(cr, fn))
    _copy(os.path.join(clean, "anz.nukestats.cfg.example"),
          os.path.join(bundle, "game-side", "anz.nukestats.cfg.example"))
    _write(os.path.join(bundle, "game-side", "DedicatedServerConfig.template.json"), _dsc_template())


def _stage_common_gameside(bundle, with_relay=False):
    common = os.path.join(bundle, "game-side", "common")
    _stage_bepinex_core(os.path.join(common, "BepInEx"))
    _copy(SRC_DLL, os.path.join(common, "BepInEx", "plugins", "NukeStats.dll"))
    if with_relay:
        for fn in ("no_relay.py", "no_relay.pl"):
            _copy(os.path.join(SRC_RELAY, fn), os.path.join(common, fn))


def _stage_linux_loader(bundle):
    lin = os.path.join(bundle, "game-side", "linux")
    for fn in ("libdoorstop.so", "run_bepinex.sh", ".doorstop_version"):
        sp = os.path.join(SRC_BEPINEX_LINUX, fn)
        if os.path.exists(sp):
            _copy(sp, os.path.join(lin, fn))


def _stage_win_loader(bundle, warnings):
    win = os.path.join(bundle, "game-side", "windows")
    if not os.path.isdir(SRC_BEPINEX_WIN):
        warnings.append("Windows BepInEx loader not vendored (NukeStats/bepinex_pack_win/ absent) "
                        "- the Windows path of this bundle ships without winhttp.dll. "
                        "Run scripts/vendor_bepinex_win.py, or use the Linux path.")
        return False
    for fn in ("winhttp.dll", "doorstop_config.ini", ".doorstop_version"):
        sp = os.path.join(SRC_BEPINEX_WIN, fn)
        if os.path.exists(sp):
            _copy(sp, os.path.join(win, fn))
    return True


def stage_local(clean, bundle, warnings):
    _stage_common_gameside(bundle, with_relay=False)
    _stage_linux_loader(bundle)
    _stage_win_loader(bundle, warnings)


def stage_manual(clean, bundle, warnings):
    _stage_common_gameside(bundle, with_relay=True)
    _stage_linux_loader(bundle)
    _stage_win_loader(bundle, warnings)
    _write(os.path.join(bundle, "game-side", "DedicatedServerConfig.snippet.json"), _dsc_template())


# ---------------------------------------------------------------------------
def _entrypoints(bundle, btype):
    _write(os.path.join(bundle, "bundle_type.txt"), btype + "\n")
    _write(os.path.join(bundle, "install.bat"),
           "@echo off\r\n"
           "cd /d \"%~dp0\"\r\n"
           "echo Starting the Nuclear Option toolkit installer...\r\n"
           "where python >nul 2>nul && (python installer\\setup.py) || (py installer\\setup.py)\r\n"
           "pause\r\n".replace("\n", "\r\n").replace("\r\r\n", "\r\n"))
    _write(os.path.join(bundle, "install.sh"),
           "#!/usr/bin/env bash\ncd \"$(dirname \"$0\")\"\n"
           "python3 installer/setup.py || python installer/setup.py\n")
    try:
        os.chmod(os.path.join(bundle, "install.sh"), 0o755)
    except OSError:
        pass
    _write(os.path.join(bundle, "INSTALL.html"), _install_html(btype))


def _install_html(btype):
    title = {"pterodactyl": "Pterodactyl", "local": "Local PC", "manual": "Manual"}[btype]
    run = ("Windows: double-click <code>install.bat</code>"
           " &nbsp;·&nbsp; macOS/Linux: run <code>./install.sh</code> in a terminal")
    return ("<!doctype html><meta charset=utf-8><title>Install - %s</title>"
            "<body style='font:15px/1.6 system-ui;max-width:640px;margin:48px auto;"
            "background:#0a0e14;color:#d6e2f0;padding:24px'>"
            "<h1 style='color:#36d0ff'>Nuclear Option Toolkit - %s bundle</h1>"
            "<p>This folder contains everything. To install:</p>"
            "<ol><li>Make sure you have <b>Python 3.8+</b> (python.org; tick "
            "“Add to PATH”).</li>"
            "<li>%s</li>"
            "<li>A setup wizard opens in your browser - follow it; each field explains "
            "what it is and where to find it.</li></ol>"
            "<p style='color:#8aa0b8'>Full steps + troubleshooting are in "
            "<b>README.md</b> next to this file.</p></body>" % (title, title, run))


# ---------------------------------------------------------------------------
def zip_bundle(bundle_dir, zip_path, top):
    """Deterministic zip (sorted entries, fixed mtime) so .sha256 is stable across rebuilds."""
    entries = []
    for base, _dirs, files in os.walk(bundle_dir):
        for fn in files:
            full = os.path.join(base, fn)
            arc = top + "/" + os.path.relpath(full, bundle_dir).replace("\\", "/")
            entries.append((arc, full))
    entries.sort()
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
        for arc, full in entries:
            zi = zipfile.ZipInfo(arc, date_time=(1980, 1, 1, 0, 0, 0))
            zi.external_attr = 0o644 << 16
            if arc.endswith((".sh", ".so")) or arc.endswith("install.sh"):
                zi.external_attr = 0o755 << 16
            zi.compress_type = zipfile.ZIP_DEFLATED
            with open(full, "rb") as f:
                z.writestr(zi, f.read())
    return zip_path


def _sha256(path):
    import hashlib
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(65536), b""):
            h.update(chunk)
    return h.hexdigest()


def _readme(btype, version):
    fn = os.path.join(HERE, "bundle_readmes", "README.%s.md" % btype)
    with open(fn, encoding="utf-8") as f:
        return f.read().replace("{{VERSION}}", version)


# ---------------------------------------------------------------------------
def main(argv=None):
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True, help="output dir (must be OUTSIDE the repo)")
    ap.add_argument("--types", default="pterodactyl")
    ap.add_argument("--version", default=None)
    ap.add_argument("--force", action="store_true")
    ap.add_argument("--check", action="store_true", help="stage + scan, skip zipping")
    ap.add_argument("--reuse-clean", action="store_true")
    ap.add_argument("--allow-stale-dll", action="store_true",
                    help="ship bin/Release/NukeStats.dll even if it wasn't built from the current source version")
    a = ap.parse_args(argv)

    out = os.path.abspath(a.out)
    repo = os.path.abspath(os.path.dirname(ROOT))   # ROOT = src; guard the whole repo
    if out == repo or out.startswith(repo + os.sep):
        raise SystemExit("--out must be OUTSIDE the source repo")
    version = a.version or _toolkit_version()
    types = [t.strip() for t in a.types.split(",") if t.strip()]
    for t in types:
        if t not in BUNDLES:
            raise SystemExit("unknown bundle type: %s" % t)

    if not os.path.exists(SRC_DLL):
        raise SystemExit("MISSING plugin DLL: %s (build NukeStats Release first)" % SRC_DLL)
    if not a.allow_stale_dll and not dll_version_ok():
        raise SystemExit(
            "STALE plugin DLL: %s was not built from the current source version (%s).\n"
            "  -> run NukeStats/build.bat (Release) and rebuild, or pass --allow-stale-dll if you\n"
            "     really mean to ship a binary whose load banner disagrees with the release tag."
            % (SRC_DLL, bpr._plugin_version()))

    if os.path.exists(out) and a.force and not a.reuse_clean:
        shutil.rmtree(out)
    os.makedirs(out, exist_ok=True)

    clean = os.path.join(out, "_clean")
    if not (a.reuse_clean and os.path.isdir(clean)):
        clean = build_clean(out)

    manifest = {"version": version, "bundles": {}}
    warnings = []
    for btype in types:
        spec = BUNDLES[btype]
        top = "nuclear-option-toolkit-%s-%s" % (btype, version)   # versioned folder
        bundle = os.path.join(out, top)
        if os.path.exists(bundle):
            shutil.rmtree(bundle)
        os.makedirs(bundle)
        print("[%s] staging -> %s" % (btype, bundle))
        _stage_admin(clean, bundle, spec)
        if btype == "pterodactyl":
            stage_pterodactyl(clean, bundle)
        elif btype == "local":
            stage_local(clean, bundle, warnings)
        elif btype == "manual":
            stage_manual(clean, bundle, warnings)
        _entrypoints(bundle, btype)
        _write(os.path.join(bundle, "bundle_version.txt"), version + "\n")   # install-time baseline for the updater
        _write(os.path.join(bundle, "README.md"), _readme(btype, version))

        hard, warn = bpr.scan(bundle)
        if hard:
            print("FAIL: bundle %s has %d HARD secret hit(s):" % (btype, len(hard)))
            for rel, ln, kind, val in hard:
                print("  !! %-22s %s:%d %s" % (kind, rel, ln, val))
            raise SystemExit(2)
        nfiles = sum(len(f) for _b, _d, f in os.walk(bundle))
        info = {"scan": "CLEAN", "files": nfiles, "warn": len(warn)}
        print("  scan CLEAN, %d files" % nfiles)

        if not a.check:
            # `top` (set above) is the versioned in-zip folder, e.g. nuclear-option-toolkit-pterodactyl-1.0;
            # the ZIP NAME stays version-less so the README can use a stable /releases/latest/download/ link.
            zp = os.path.join(out, "nuclear-option-toolkit-%s.zip" % btype)
            zip_bundle(bundle, zp, top)
            sha = _sha256(zp)
            _write(zp + ".sha256", sha + "  " + os.path.basename(zp) + "\n")
            info.update({"zip": os.path.basename(zp), "bytes": os.path.getsize(zp), "sha256": sha})
            print("  zip -> %s (%.1f MB) sha256 %s..." % (os.path.basename(zp),
                  os.path.getsize(zp) / 1e6, sha[:12]))
        manifest["bundles"][btype] = info

    _write(os.path.join(out, "bundles.manifest.json"), json.dumps(manifest, indent=2))
    print("=" * 60)
    for w in warnings:
        print("WARN  " + w)
    print("DONE. %d bundle(s) -> %s" % (len(types), out))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
