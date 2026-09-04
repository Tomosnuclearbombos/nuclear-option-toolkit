#!/usr/bin/env python3
"""Offline-install validation for the setup installer.

For users who don't want the installer to download anything, they pre-download each
file from its OFFICIAL upstream (GitHub / Valve / the egg repo) into one folder; this
module checks the folder has everything that hosting option needs, verifies SHA-256
where a hash is known/pinned, and prints exactly what's missing + where to get it. The
real fetch/extract then runs from those local bytes (same code path as online).

    python offline.py validate <option> --dir <folder>
    python offline.py urls     <option>            # the official download list (copy to a phone)
"""
import argparse
import glob
import hashlib
import json
import os
import sys

for _s in (sys.stdout, sys.stderr):                   # never crash printing on a cp1252 console
    try:
        _s.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError, OSError):
        pass

HERE = os.path.dirname(os.path.abspath(__file__))
MANIFEST = os.path.join(HERE, "sources.json")
LOCKFILE = os.path.join(HERE, "sources.lock.json")


def _manifest():
    with open(MANIFEST, encoding="utf-8") as f:
        return json.load(f)


def _lock():
    try:
        with open(LOCKFILE, encoding="utf-8") as f:
            return json.load(f)
    except (OSError, ValueError):
        return {}


def _deps_for(option):
    m = _manifest()
    if option not in m["options"]:
        sys.exit("unknown option '%s'. Choose: %s" % (option, ", ".join(m["options"])))
    return m, m["options"][option]


def urls(option):
    m, deps = _deps_for(option)
    print("Offline download list for '%s' — get each from its OFFICIAL source:\n" % option)
    for dep_id in deps:
        dep = m["dependencies"][dep_id]
        if option in (dep.get("provided_by_host") or []):
            print("  • %-22s provided by the host (the panel/egg installs it) — nothing to download" % dep_id)
            continue
        off = dep.get("offline", {})
        print("  • %-22s %s" % (dep_id, dep.get("name", "")))
        print("        file: %s" % off.get("filename", "?"))
        print("        from: %s" % off.get("official_url", "?"))
        if off.get("instructions"):
            print("        note: %s" % off["instructions"])
    print("\nThen:  python offline.py validate %s --dir <that folder>" % option)


def validate(option, folder):
    m, deps = _deps_for(option)
    lock = _lock()
    if not os.path.isdir(folder):
        sys.exit("folder not found: %s" % folder)
    print("Validating offline folder '%s' for '%s':\n" % (folder, option))
    red = 0
    for dep_id in deps:
        dep = m["dependencies"][dep_id]
        if option in (dep.get("provided_by_host") or []) or dep["fetch"]["method"] in ("steamcmd", "bundled"):
            tag = "host/bundled" if dep["fetch"]["method"] != "steamcmd" else "host/SteamCMD"
            print("  [ ok ] %-22s %s (not a manual download)" % (dep_id, tag))
            continue
        pat = dep.get("offline", {}).get("filename", "*")
        hits = glob.glob(os.path.join(folder, pat))
        if not hits:
            red += 1
            print("  [MISS] %-22s need %-28s  <- %s" % (dep_id, pat, dep.get("offline", {}).get("official_url", "?")))
            continue
        data = open(hits[0], "rb").read()
        sha = hashlib.sha256(data).hexdigest()
        known = (dep.get("integrity", {}).get("sha256") or lock.get(dep_id, {}).get("sha256") or "")
        if known and known.lower() != sha.lower():
            red += 1
            print("  [BAD ] %-22s %s SHA-256 mismatch (expected %s…)" % (dep_id, os.path.basename(hits[0]), known[:16]))
        elif known:
            print("  [ ok ] %-22s %s  sha matches pinned %s…" % (dep_id, os.path.basename(hits[0]), sha[:12]))
        else:
            print("  [ ok?] %-22s %s  present (no pinned hash — first use, sha %s…)" % (dep_id, os.path.basename(hits[0]), sha[:12]))
    print("\n%s" % ("ALL PRESENT — ready for an offline install." if not red
                     else "%d item(s) missing/invalid — download them from the URLs above, then re-validate." % red))
    return red == 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("cmd", choices=["validate", "urls"])
    ap.add_argument("option")
    ap.add_argument("--dir", default=None)
    a = ap.parse_args()
    if a.cmd == "urls":
        urls(a.option)
    else:
        if not a.dir:
            sys.exit("--dir <folder> required")
        validate(a.option, a.dir)


if __name__ == "__main__":
    main()
