#!/usr/bin/env python3
"""Cut a signed, stable Nuclear Option toolkit release.

Builds the clean tree + the 3 bundles (via build_bundles) + the updater assets (NukeStats.dll,
the scrubbed no_mapvote_bot.py), **minisign-signs every asset**, then publishes a GitHub release
and uploads them (via publish_bundles' token+REST helpers). The matching public key ships in the
toolkit as installer/trusted.pub, so each server's opt-in updater verifies before applying.

    # dry run — build + sign locally, don't publish:
    python scripts/publish_release.py --out ../dist --key <minisign.key> --dry-run

    # publish (token comes from git credential manager, like publish_bundles):
    python scripts/publish_release.py --out ../dist --key <minisign.key>

Releases are tagged v<version> — a full release; the opt-in updater only ever offers full releases.

Signing key / minisign binary come from --key/--minisign or the NO_SIGN_KEY / NO_MINISIGN env
vars (no personal paths baked into this file). The secret key is never printed.
"""
import argparse
import hashlib
import os
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, HERE)
import build_bundles as bb
import publish_bundles as pb
import build_public_repo as bpr

for _s in (sys.stdout, sys.stderr):
    try:
        _s.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError, OSError):
        pass


def _which_minisign(override):
    return override or os.environ.get("NO_MINISIGN") or shutil.which("minisign") or "minisign"


def _sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(65536), b""):
            h.update(chunk)
    digest = h.hexdigest()
    with open(path + ".sha256", "w", encoding="utf-8", newline="\n") as f:
        f.write(digest + "  " + os.path.basename(path) + "\n")
    return digest


def _sign(path, key, minisign):
    """minisign-sign a file -> <path>.minisig. Uses a no-password key (NO_SIGN_KEY). Never logs the key."""
    sig = path + ".minisig"
    if os.path.exists(sig):
        os.remove(sig)
    try:
        r = subprocess.run([minisign, "-S", "-s", key, "-m", path, "-x", sig],
                           capture_output=True, text=True, timeout=60)
    except FileNotFoundError:
        raise SystemExit("minisign not found — install it or pass --minisign / set NO_MINISIGN.")
    if r.returncode != 0 or not os.path.exists(sig):
        # never echo the key path's contents; stderr from minisign is safe (no secret material)
        raise SystemExit("minisign signing failed for %s:\n%s" % (os.path.basename(path),
                         (r.stderr or r.stdout or "").strip()[:300]))
    return sig


def _tag(version):
    v = version.lstrip("v")
    return "v" + v, "v" + v, False


def _changelog_section(version):
    """Release-notes body pulled from the [<version>] section of CHANGELOG.md, or '' if none."""
    import re as _re
    path = os.path.join(os.path.dirname(ROOT), "CHANGELOG.md")   # CHANGELOG lives at the repo root, not src/
    if not os.path.exists(path):
        return ""
    with open(path, encoding="utf-8") as f:
        text = f.read()
    base = version.lstrip("v")
    m = _re.search(r"(?m)^##\s*\[%s\][^\n]*\n" % _re.escape(base), text)
    if not m:
        return ""
    start = m.end()
    nxt = _re.search(r"(?m)^##\s+", text[start:])
    body = (text[start: start + nxt.start()] if nxt else text[start:]).strip()
    # trim a trailing horizontal-rule/footer that belongs after the last section
    body = _re.split(r"(?m)^---\s*$", body)[0].strip()
    return body


def _notes(version, signed):
    lines = ["Automated release of the Nuclear Option community toolkit.",
             "", "- Plugin + bot version: **%s**" % version.lstrip("v"),
             "- Bundles: Pterodactyl / Local / Manual (each a full self-contained install).",
             "- Updater assets: NukeStats.dll, no_mapvote_bot.py.",
             "- %s" % ("All assets are **minisign-signed**; the public key ships as `installer/trusted.pub`."
                       if signed else "**Unsigned build** (testing).")]
    note = "\n".join(lines)
    changes = _changelog_section(version)
    if changes:
        note += "\n\n---\n\n### What's changed\n\n" + changes
    else:
        # never silent: a missing section means the release page ships with no "What's changed" at all
        print("[release] WARNING: CHANGELOG.md has no [%s] section — release notes will have no "
              "'What's changed' body." % version.lstrip("v"))
    return note


def main(argv=None):
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True, help="build dir (OUTSIDE the repo)")
    ap.add_argument("--version", default=None, help="default: plugin version from source")
    ap.add_argument("--key", default=None, help="minisign secret key (or NO_SIGN_KEY env)")
    ap.add_argument("--minisign", default=None, help="minisign binary (or NO_MINISIGN env / PATH)")
    ap.add_argument("--no-sign", action="store_true", help="skip signing (testing only)")
    ap.add_argument("--dry-run", action="store_true", help="build + sign, do NOT publish")
    ap.add_argument("--allow-stale-dll", action="store_true",
                    help="publish even if bin/Release/NukeStats.dll wasn't built from the current source version")
    a = ap.parse_args(argv)

    out = os.path.abspath(a.out)
    if out == ROOT or out.startswith(ROOT + os.sep):
        raise SystemExit("--out must be OUTSIDE the source repo")
    version = (a.version or bb._toolkit_version()).lstrip("v")   # TOOLKIT version (1.0+), not the plugin's
    tag, name, prerelease = _tag(version)

    key = a.key or os.environ.get("NO_SIGN_KEY")
    minisign = _which_minisign(a.minisign)
    sign = not a.no_sign
    if sign and not key:
        raise SystemExit("signing needs a key: pass --key or set NO_SIGN_KEY (or --no-sign to skip).")

    # 0. the DLL must be a REBUILD of the current source. Steps 1 and 2 both copy whatever binary is
    # sitting in bin/Release, so a version bump with no rebuild tags v<new> around a plugin whose own
    # load banner still says the old number — and the panel header trusts the banner.
    if not a.allow_stale_dll and not bb.dll_version_ok():
        raise SystemExit(
            "STALE plugin DLL: %s was not built from the current source version (%s).\n"
            "  -> rebuild NukeStats (Release) first, or pass --allow-stale-dll."
            % (bb.SRC_DLL, bpr._plugin_version()))

    # 1. build the 3 bundles + the clean tree
    print("[release] building bundles (%s) ..." % tag)
    bb_argv = ["--out", out, "--force", "--version", version]
    if a.allow_stale_dll:
        bb_argv.append("--allow-stale-dll")
    rc = bb.main(bb_argv)
    if rc:
        raise SystemExit("bundle build failed")

    # 1b. smoke gate: never publish a syntactically broken build
    import py_compile
    for rel in ("no_mapvote_bot.py", "cc_web.py", "command_centre.py", "map_atlas.py",
                "installer/setup.py", "installer/updater.py", "installer/deployer.py"):
        p = os.path.join(out, "_clean", *rel.split("/"))
        if os.path.exists(p):
            try:
                py_compile.compile(p, doraise=True)
            except py_compile.PyCompileError as e:
                raise SystemExit("smoke check FAILED — refusing to publish a broken build (%s): %s" % (rel, e))
    print("[release] smoke check OK (key modules compile)")

    # 2. assemble the updater assets (plugin DLL + the scrubbed bot)
    assets = [os.path.join(out, "nuclear-option-toolkit-%s.zip" % t)
              for t in ("pterodactyl", "local", "manual")]
    dll = os.path.join(out, "NukeStats.dll")
    shutil.copy2(bb.SRC_DLL, dll)
    bot_src = os.path.join(out, "_clean", "no_mapvote_bot.py")     # the scrubbed bot
    bot = os.path.join(out, "no_mapvote_bot.py")
    shutil.copy2(bot_src, bot)
    assets += [dll, bot]

    # web command centre (dashboard) as ONE signed zip, so the updater can deliver the UI +
    # backend the same verify-before-apply way it delivers the plugin/bot. WITHOUT this, every
    # web-CC feature (rank editor, cross-server ranks, ...) is unreachable by update.
    import zipfile
    webcc_zip = os.path.join(out, "command-centre.zip")
    webcc_members = ["cc_web.py", "webcc.html", "map_atlas.py", "command_centre.py",
                     "settings_catalogue.json"]
    with zipfile.ZipFile(webcc_zip, "w", zipfile.ZIP_DEFLATED) as z:
        for m in webcc_members:
            src = os.path.join(out, "_clean", m)
            if os.path.exists(src):
                z.write(src, m)
    if not zipfile.ZipFile(webcc_zip).namelist():
        raise SystemExit("command-centre.zip is empty — web-CC files missing from the clean tree")
    assets += [webcc_zip]

    # 3. sha256 + sign every asset
    final = []
    for ap_ in assets:
        if not os.path.exists(ap_):
            raise SystemExit("missing built asset: %s" % ap_)
        if not os.path.exists(ap_ + ".sha256"):
            _sha256_file(ap_)            # bundles already have one; dll/bot get one here
        final.append(ap_)
        final.append(ap_ + ".sha256")
        if sign:
            final.append(_sign(ap_, key, minisign))
    print("[release] %d asset file(s) ready (%s)" % (len(final), "signed" if sign else "UNSIGNED"))
    for f in final:
        print("   " + os.path.basename(f))

    if a.dry_run:
        print("[release] --dry-run: not publishing. Tag would be %s (prerelease=%s)." % (tag, prerelease))
        return 0

    # 4. publish
    token = pb._token()
    rel = pb.get_or_create(token, tag, name, _notes(version, sign), prerelease)
    for f in final:
        pb.upload_asset(token, rel, f)
    print("DONE. https://github.com/%s/releases/tag/%s" % (pb.REPO, tag))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
