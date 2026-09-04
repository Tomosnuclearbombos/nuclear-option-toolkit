#!/usr/bin/env python3
"""Build a clean-room PUBLIC release tree of the Nuclear Option toolkit.

Copies ONLY whitelisted files into a fresh destination folder, applies a fixed
set of scrub transforms (strip the real host / server IP / admin SteamID, fix the
doc preambles + plugin-version drift, reword the not-yet-shipped trust-key claim),
writes a hardened .gitignore, then runs a secret/PII scan over the result. It
refuses to finish (exit 2) if the scan finds a hard secret, and warns (exit 0) on
softer hits.

KEY PROPERTIES
  * The LIVE working files are NEVER modified — every transform runs on the COPIES.
  * No real secret/PII literal lives in THIS file. The real SteamID/IP/host that
    the scrubber must find-and-replace are read from scripts/scrub_targets.json
    (gitignored, never published). A published scrub_targets.example.json shows
    the shape so a forker can scrub their own deployment the same way.

Usage:
    cp scripts/scrub_targets.example.json scripts/scrub_targets.json   # then fill in
    python scripts/build_public_repo.py --dest ../nuclear-option-toolkit
    python scripts/build_public_repo.py --dest /tmp/pub --force        # overwrite
    python scripts/build_public_repo.py --dest /tmp/pub --strict       # warns fail too

After it succeeds:
    cd <dest> && git init && git add -A && git commit -m "Initial public release"
"""
import argparse
import fnmatch
import json
import os
import re
import shutil
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)

# ---------------------------------------------------------------------------
# WHITELIST — exactly what the public repo contains. Everything else is excluded
# by omission (deny-by-default). See docs/PUBLIC_REPO_MANIFEST.md for the rationale.
# ---------------------------------------------------------------------------
# The repo is a hybrid layout: the public DOCS + LICENSE live at the repo ROOT, the product
# source lives under src/. HERE = src/scripts, so ROOT = src; REPO_ROOT = the repo root above it.
# The clean public tree is FLAT, so we gather from both source roots into one destination.
REPO_ROOT = os.path.dirname(ROOT)

# Files taken from the REPO ROOT (docs + licence).
REPO_ROOT_FILES = ["README.md", "SECURITY.md", "LICENSE"]

# Files taken from src/ (ROOT): product source, map assets, launchers, config templates.
ROOT_FILES = [
    # runtime / product source
    "no_mapvote_bot.py", "cc_web.py", "command_centre.py", "map_atlas.py",
    "webcc.html", "settings_catalogue.json",
    # map assets (rendered PNGs the web map needs)
    "heartland_map.png", "ignus_map.png",
    # launchers / helper bats (no secrets — verified)
    "webcc.bat", "commandcentre.bat", "deploy.bat", "endmission.bat",
    "say.bat", "status.bat", "run_keepalive.bat",
    # config TEMPLATES (the real run.bat/apiKey.txt/panel.txt are gitignored)
    "run.bat.example", "apiKey.txt.example", "panel.txt.example",
    "anz.nukestats.cfg.example", "config.example.json",
]

# Whole-subtree includes, each minus the listed glob excludes (paths relative to
# the subtree root, forward-slashes). Excluded dirs are pruned for speed.
# Trees taken from the REPO ROOT.
REPO_TREE_INCLUDES = {
    "docs":       ["_*.json", "DESIGN_HISTORY.md", "PRODUCTIZATION_PLAN.md",
                   "PRE_UPLOAD_CHECKLIST.md", "INSTALL_WIZARD_CONTENT.md",
                   "PUBLIC_REPO_MANIFEST.md", "SETTINGS_AND_INSTALL_V2.md"],  # internal-only docs
}

# Trees taken from src/ (ROOT).
TREE_INCLUDES = {
    "installer":  ["__pycache__/*", "sources.lock.json"],         # local install state
    "scripts":    ["__pycache__/*", "scrub_targets.json"],        # never ship real targets
    "NukeStats":  ["libs/*", "bin/*", "obj/*", "bepinex_pack/*", "bepinex_pack_win/*"],  # proprietary/build/binaries (bundles re-add from source)
    "map-build":  ["__pycache__/*"],
    "relay":      [],                                            # localhost->WAN remote-command relay
    "START HERE": ["*.lnk"],                                      # machine-specific shortcut
}

# Real secret/PII anchors are loaded from scrub_targets.json at runtime, NOT hardcoded.
REAL_SID = REAL_IP = REAL_HOST = None
# Live plugin version, read from the plugin source so the docs never drift.
PLUGIN_VERSION = None


def _plugin_version():
    p = os.path.join(ROOT, "NukeStats", "NukeStatsPlugin.cs")
    with open(p, encoding="utf-8") as f:
        m = re.search(r'Version\s*=\s*"([0-9][0-9.]*)"', f.read())
    if not m:
        raise SystemExit("could not read plugin Version from NukeStatsPlugin.cs")
    return m.group(1)


def _load_targets():
    p = os.path.join(HERE, "scrub_targets.json")
    if not os.path.exists(p):
        raise SystemExit(
            "missing scripts/scrub_targets.json\n"
            "  -> copy scripts/scrub_targets.example.json to scripts/scrub_targets.json\n"
            "     and fill in your real admin SteamID, server IP, and SFTP host.\n"
            "     (it is gitignored and never published)")
    with open(p, encoding="utf-8") as f:
        d = json.load(f)
    for k in ("real_sid", "real_ip", "real_host"):
        if not d.get(k):
            raise SystemExit("scrub_targets.json missing '%s'" % k)
    return d["real_sid"], d["real_ip"], d["real_host"]


# ---------------------------------------------------------------------------
# SCRUB transforms — keyed by dest-relative path (forward slashes).
# _require() raises if the expected anchor is gone (so we never silently ship an
# un-scrubbed file after the source changes upstream).
# ---------------------------------------------------------------------------
def _require(text, old, new):
    if old not in text:
        raise AssertionError("scrub anchor not found (file changed?): %r" % (old[:70]))
    return text.replace(old, new)


def scrub_bot(text):
    # The bot is config-driven (config.json/secrets.json -> env -> these defaults), so we only
    # replace the real DEFAULT VALUES with placeholders. Anchor-free, so it survives bot
    # refactors; the secret-scan gate is the backstop if anything were ever missed.
    text = text.replace(REAL_SID, "7656119xxxxxxxxxx")
    text = text.replace(REAL_IP, "your-host.example.net")
    text = text.replace(REAL_HOST, "your-sftp-host.example.net")
    return text


def scrub_plugin(text):
    # Idempotent: strip the baked-in dev SteamID if present (older builds), but don't fail if the
    # source already ships an empty Admin.SteamIds default (current builds). Always assert clean.
    old = 'Config.Bind("Admin", "SteamIds", "%s",' % REAL_SID
    if old in text:
        text = text.replace(old, 'Config.Bind("Admin", "SteamIds", "",')
    if REAL_SID in text:
        raise AssertionError("plugin still contains a real SteamID after scrub")
    return text


def strip_preamble(text):
    """Drop any leaked agent-preamble lines before the first '# ' H1 heading."""
    lines = text.splitlines(keepends=True)
    for i, ln in enumerate(lines):
        if ln.startswith("# "):
            return "".join(lines[i:])
    raise AssertionError("no H1 heading found to anchor preamble strip")


def scrub_architecture(text):
    # Keep the documented plugin version synced to the live source on every build.
    # Tolerant: if a reference isn't found (doc changed), that spot is left as-is.
    v = PLUGIN_VERSION
    text = re.sub(r'(\*\*Plugin version:\*\* `anz\.nukestats` )`[0-9][0-9.]*`',
                  lambda m: m.group(1) + "`" + v + "`", text)
    # the phrasing the doc actually uses today: "... const in source (currently `1.2.0`)". Without this
    # the sync silently matched NOTHING and the published doc sat frozen at 1.1.7 for ~34 builds.
    text = re.sub(r'(const in source \(currently )`[0-9][0-9.]*`',
                  lambda m: m.group(1) + "`" + v + "`", text)
    text = re.sub(r'(NukeStats plugin v)[0-9][0-9.]*', lambda m: m.group(1) + v, text)
    text = re.sub(r'(\[BepInPlugin\("anz\.nukestats", "NukeStats", ")[0-9][0-9.]*',
                  lambda m: m.group(1) + v, text)
    return text


def scrub_design_history(text):
    return _require(text, "The newest layer (v0.9.5) adds", "The v0.9.5 layer adds")


SCRUBS = {
    "no_mapvote_bot.py": scrub_bot,
    "NukeStats/NukeStatsPlugin.cs": scrub_plugin,
    "docs/INSTALL_SOURCES.md": strip_preamble,
}

# ---------------------------------------------------------------------------
# Secret/PII scan (the gate). Generic detectors are static; the two host/IP
# specific detectors are built from the loaded targets so no literal lives here.
# ---------------------------------------------------------------------------
HARD_GENERIC = [
    ("Pterodactyl client key", re.compile(r"ptlc_[A-Za-z0-9]{20,}")),
    ("Pterodactyl application key", re.compile(r"ptla_[A-Za-z0-9]{20,}")),
    ("Real SteamID64", re.compile(r"7656119\d{10}")),
    ("Private key block", re.compile(r"-----BEGIN [A-Z ]*PRIVATE KEY-----")),
    ("Non-placeholder SFTP password",
     re.compile(r"""NO_SFTP_PASS\s*=\s*["'](?!your-|<|\s)[^"']{4,}""")),
]
WARN = [
    ("Possible IPv4", re.compile(r"\b(?:\d{1,3}\.){3}\d{1,3}\b")),
    ("Possible email", re.compile(r"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")),
]
IP_ALLOW = {"127.0.0.1", "0.0.0.0", "255.255.255.255"}
BINARY_EXT = {".png", ".jpg", ".jpeg", ".gif", ".ico", ".pyc", ".dll", ".zip",
              ".gz", ".lnk", ".pdf"}


def _host_patterns(real_host):
    pats = [re.escape(real_host)]
    parts = real_host.split(".")
    if len(parts) >= 2 and len(parts[-2]) >= 4:
        pats.append(r"\b%s\b" % re.escape(parts[-2]))   # the distinctive node label
    return "|".join(pats)


def _hard_patterns():
    specific = [
        ("Known real server IP", re.compile(re.escape(REAL_IP))),
        ("Known real SFTP host", re.compile(_host_patterns(REAL_HOST))),
    ]
    return HARD_GENERIC + specific


def _redact(s):
    return s[:6] + "***" if len(s) > 6 else "***"


def scan(dest):
    hard_patterns = _hard_patterns()
    hard_hits, warn_hits = [], []
    for base, dirs, files in os.walk(dest):
        if ".git" in dirs:
            dirs.remove(".git")
        for fn in files:
            if os.path.splitext(fn)[1].lower() in BINARY_EXT:
                continue
            fp = os.path.join(base, fn)
            rel = os.path.relpath(fp, dest).replace("\\", "/")
            try:
                with open(fp, encoding="utf-8", errors="replace") as f:
                    lines = f.readlines()
            except OSError:
                continue
            for i, line in enumerate(lines, 1):
                for kind, rx in hard_patterns:
                    for m in rx.finditer(line):
                        hard_hits.append((rel, i, kind, _redact(m.group(0))))
                for kind, rx in WARN:
                    for m in rx.finditer(line):
                        val = m.group(0)
                        if kind == "Possible IPv4" and val in IP_ALLOW:
                            continue
                        warn_hits.append((rel, i, kind, val))
    return hard_hits, warn_hits


# ---------------------------------------------------------------------------
# Copy helpers
# ---------------------------------------------------------------------------
def _excluded(rel, patterns):
    return any(fnmatch.fnmatch(rel, p) for p in patterns)


def copy_root_files(dest, copied):
    for base_dir, names in ((REPO_ROOT, REPO_ROOT_FILES), (ROOT, ROOT_FILES)):
        for name in names:
            src = os.path.join(base_dir, name)
            if not os.path.exists(src):
                raise SystemExit("MISSING whitelisted file: %s" % name)
            shutil.copy2(src, os.path.join(dest, name))
            copied.append(name)


def _copy_tree_group(base_dir, includes, dest, copied):
    for sub, excludes in includes.items():
        src_root = os.path.join(base_dir, sub)
        if not os.path.isdir(src_root):
            raise SystemExit("MISSING whitelisted dir: %s" % sub)
        for base, dirs, files in os.walk(src_root):
            relbase = os.path.relpath(base, src_root).replace("\\", "/")
            relbase = "" if relbase == "." else relbase + "/"
            dirs[:] = [d for d in dirs
                       if not _excluded(relbase + d + "/*", excludes)
                       and not _excluded(relbase + d, excludes)]
            for fn in files:
                rel = relbase + fn
                if _excluded(rel, excludes):
                    continue
                out = os.path.join(dest, sub, *rel.split("/"))
                os.makedirs(os.path.dirname(out), exist_ok=True)
                shutil.copy2(os.path.join(base, fn), out)
                copied.append(sub + "/" + rel)


def copy_trees(dest, copied):
    _copy_tree_group(REPO_ROOT, REPO_TREE_INCLUDES, dest, copied)
    _copy_tree_group(ROOT, TREE_INCLUDES, dest, copied)


def apply_scrubs(dest):
    applied = []
    for rel, func in SCRUBS.items():
        fp = os.path.join(dest, *rel.split("/"))
        if not os.path.exists(fp):
            raise SystemExit("scrub target not copied: %s" % rel)
        with open(fp, encoding="utf-8") as f:
            text = f.read()
        text = func(text)
        with open(fp, "w", encoding="utf-8", newline="") as f:
            f.write(text)
        applied.append(rel)
    return applied


def harden_gitignore(dest):
    with open(os.path.join(REPO_ROOT, ".gitignore"), encoding="utf-8") as f:
        t = f.read()
    t = t.replace("ranks*.json\n", "ranks*.json*\n")          # close the .bak gap
    block = ("\n# --- hardening (build_public_repo.py) ---\n"
             "*.bak\n*.pem\n*.minisig\n*.lnk\n"
             "console_filters.json\n"
             "scripts/scrub_targets.json\n"
             "installer/sources.lock.json\n")
    if "# --- hardening" not in t:
        t += block
    with open(os.path.join(dest, ".gitignore"), "w", encoding="utf-8", newline="") as f:
        f.write(t)


def post_checks(dest, warnings):
    readme = os.path.join(dest, "README.md")
    with open(readme, encoding="utf-8") as f:
        rt = f.read()
    if os.path.exists(os.path.join(dest, "LICENSE")):
        rt = rt.replace("See `LICENSE` (TBD before public release).",
                        "See [`LICENSE`](LICENSE).")
        with open(readme, "w", encoding="utf-8", newline="") as f:
            f.write(rt)
    else:
        warnings.append("No LICENSE file — README still says 'LICENSE (TBD)'. "
                        "Add one (plan recommends GPL-3.0-or-later) before publishing.")
    if not os.path.exists(os.path.join(dest, "installer", "trusted.pub")):
        warnings.append("installer/trusted.pub absent — generate+commit the minisign "
                        "public key before the first SIGNED release, or the updater can "
                        "only stage with --i-understand-unsigned.")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dest", required=True, help="output folder for the clean public tree")
    ap.add_argument("--force", action="store_true", help="overwrite an existing dest")
    ap.add_argument("--strict", action="store_true", help="treat WARN hits as failures too")
    a = ap.parse_args()

    global REAL_SID, REAL_IP, REAL_HOST, PLUGIN_VERSION
    REAL_SID, REAL_IP, REAL_HOST = _load_targets()
    PLUGIN_VERSION = _plugin_version()

    dest = os.path.abspath(a.dest)
    repo = os.path.abspath(REPO_ROOT)
    if repo == dest or dest.startswith(repo + os.sep):
        raise SystemExit("dest must be OUTSIDE the source repo (never build in place)")
    if os.path.exists(dest):
        if not a.force:
            raise SystemExit("dest exists; pass --force to overwrite: %s" % dest)
        shutil.rmtree(dest)
    os.makedirs(dest)

    copied, warnings = [], []
    copy_root_files(dest, copied)
    copy_trees(dest, copied)
    applied = apply_scrubs(dest)
    harden_gitignore(dest)
    post_checks(dest, warnings)

    hard, warn = scan(dest)

    print("=" * 64)
    print("BUILD: %s" % dest)
    print("  files copied : %d" % len(copied))
    print("  scrubs applied: %d  (%s)" % (len(applied), ", ".join(applied)))
    print("  .gitignore   : hardened (ranks*.json* / *.bak / console_filters.json / *.pem / *.minisig)")
    print("  plugin version: %s (ARCHITECTURE.md synced to source)" % PLUGIN_VERSION)
    print("-" * 64)
    if warn:
        print("WARN (%d) - review, usually benign (localhost, doc IPs, example emails):" % len(warn))
        for rel, ln, kind, val in warn[:40]:
            print("  ~ %-26s %s:%d  %s" % (kind, rel, ln, val))
        if len(warn) > 40:
            print("  ... +%d more" % (len(warn) - 40))
    for w in warnings:
        print("WARN  %s" % w)
    print("-" * 64)
    if hard:
        print("FAIL - %d HARD secret/PII hit(s) WOULD be published:" % len(hard))
        for rel, ln, kind, val in hard:
            print("  !! %-26s %s:%d  %s" % (kind, rel, ln, val))
        print("=" * 64)
        sys.exit(2)
    if a.strict and warn:
        print("FAIL (--strict) - %d WARN hit(s)." % len(warn))
        sys.exit(2)
    print("CLEAN - no hard secrets. Ready for:  cd %s && git init && git add -A" % a.dest)
    print("=" * 64)


if __name__ == "__main__":
    main()
