#!/usr/bin/env python3
"""Vendor the Windows BepInEx 5.4.x (Unity Mono) loader stub into the repo.

The repo already vendors the LINUX BepInEx pack (NukeStats/bepinex_pack/), but the Local and
Manual bundles also need the WINDOWS loader (winhttp.dll + doorstop_config.ini) so they can
inject BepInEx on a Windows server fully offline. This fetches the latest BepInEx 5.4.x
win_x64 release, verifies it's the Mono line, and extracts just the loader stub into
NukeStats/bepinex_pack_win/. Run once (and re-run to update). Requires internet.

    python scripts/vendor_bepinex_win.py
"""
import io
import json
import os
import re
import sys
import urllib.request
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEST = os.path.join(ROOT, "NukeStats", "bepinex_pack_win")
API = "https://api.github.com/repos/BepInEx/BepInEx/releases?per_page=30"
WANT = re.compile(r"^BepInEx_win_x64_.*\.zip$")
KEEP = ("winhttp.dll", "doorstop_config.ini", ".doorstop_version", "changelog.txt")


def _get(url, accept="application/vnd.github+json"):
    req = urllib.request.Request(url, headers={"Accept": accept,
                                               "User-Agent": "nuke-toolkit-vendor"})
    tok = os.environ.get("GITHUB_TOKEN")
    if tok:
        req.add_header("Authorization", "Bearer " + tok)
    with urllib.request.urlopen(req, timeout=30) as r:
        return r.read()


def main():
    print("[vendor] querying BepInEx releases...")
    rels = json.loads(_get(API))
    asset = tag = None
    for rel in rels:
        if not re.match(r"^v?5\.4\.", rel.get("tag_name", "")):
            continue
        for a in rel.get("assets", []):
            if WANT.match(a.get("name", "")):
                asset, tag = a, rel["tag_name"]
                break
        if asset:
            break
    if not asset:
        raise SystemExit("no BepInEx_win_x64 5.4.x asset found")
    print("[vendor] %s  (%s, %.1f MB)" % (asset["name"], tag, asset["size"] / 1e6))
    blob = _get(asset["browser_download_url"], accept="application/octet-stream")
    z = zipfile.ZipFile(io.BytesIO(blob))
    names = z.namelist()
    if not any(n.endswith("BepInEx/core/BepInEx.Preloader.dll") for n in names):
        raise SystemExit("downloaded pack has no BepInEx.Preloader.dll — wrong/IL2CPP build?")
    os.makedirs(DEST, exist_ok=True)
    got = []
    for n in names:
        base = n.rsplit("/", 1)[-1]
        if base in KEEP and not n.endswith("/"):
            data = z.read(n)
            with open(os.path.join(DEST, base), "wb") as f:
                f.write(data)
            got.append(base)
    # make sure doorstop is enabled + points at the preloader
    ini = os.path.join(DEST, "doorstop_config.ini")
    if os.path.exists(ini):
        with open(ini, encoding="utf-8", errors="replace") as f:
            txt = f.read()
        txt = re.sub(r"(?im)^enabled\s*=.*$", "enabled=true", txt)
        with open(ini, "w", encoding="utf-8") as f:
            f.write(txt)
    with open(os.path.join(DEST, "SOURCE.txt"), "w", encoding="utf-8") as f:
        f.write("BepInEx %s win_x64 (Unity Mono)\n%s\n" % (tag, asset["browser_download_url"]))
    print("[vendor] wrote %s -> %s" % (got, DEST))
    if "winhttp.dll" not in got:
        raise SystemExit("winhttp.dll not found in the pack")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
