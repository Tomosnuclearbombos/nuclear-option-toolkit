#!/usr/bin/env python3
"""Builds the two portable downloads of the Nuclear Option Server Tester.

    python3 src/ServerTester/package.py [--out DIR]

1. NukeStats from src/NukeStats, compiled with TESTER_BUILD (pretend players, a fixed AI load, a measurement every 15 s).
2. payload/payload.zip: BepInEx 5 (src/NukeStats/bepinex_pack), the doorstop loader for Linux and for Windows
   (bepinex_pack_win), that NukeStats build and the Stress Test mission (missions/). The program carries it inside
   itself and puts it on its test server.
3. dotnet publish for linux-x64 and win-x64: one self-contained file each.
4. Nuclear-Option-Server-Tester-<version>-windows.zip and -linux.zip in --out (default src/ServerTester/dist).
Needs the .NET SDK and the game/BepInEx libs in src/NukeStats/libs (see src/NukeStats/NukeStats.csproj)."""
import argparse, os, re, shutil, stat, subprocess, sys, zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.dirname(HERE)
NS = os.path.join(SRC, "NukeStats")
PACK = os.path.join(NS, "bepinex_pack")
PACK_WIN = os.path.join(NS, "bepinex_pack_win")


def run(cmd, **kw):
    print("+", " ".join(cmd), flush=True)
    subprocess.run(cmd, check=True, **kw)


def version():
    m = re.search(r'Version = "([^"]+)"', open(os.path.join(HERE, "App.cs"), encoding="utf-8").read())
    return m.group(1)


def build_plugin(tmp):
    out = os.path.join(tmp, "nukestats-tester")
    run(["dotnet", "build", os.path.join(NS, "NukeStats.csproj"), "-c", "Release", "-p:DefineConstants=TESTER_BUILD", "-o", out])
    return os.path.join(out, "NukeStats.dll")


def make_payload(dll, tmp):
    os.makedirs(os.path.join(HERE, "payload"), exist_ok=True)
    path = os.path.join(HERE, "payload", "payload.zip")
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
        for root, _, files in os.walk(os.path.join(PACK, "BepInEx", "core")):
            for f in files:
                full = os.path.join(root, f)
                z.write(full, "common/" + os.path.relpath(full, PACK).replace(os.sep, "/"))
        z.writestr("common/BepInEx/config/.keep", "")
        z.write(dll, "common/BepInEx/plugins/NukeStats.dll")
        for root, _, files in os.walk(os.path.join(HERE, "missions")):
            for f in files:
                full = os.path.join(root, f)
                z.write(full, "common/missions/" + os.path.relpath(full, os.path.join(HERE, "missions")).replace(os.sep, "/"))
        for f in ("run_bepinex.sh", "libdoorstop.so", ".doorstop_version"):
            if os.path.exists(os.path.join(PACK, f)):
                z.write(os.path.join(PACK, f), "linux/" + f)
        for f in ("winhttp.dll", "doorstop_config.ini"):
            z.write(os.path.join(PACK_WIN, f), "windows/" + f)
        for f in (os.path.join(PACK, "changelog.txt"), os.path.join(PACK_WIN, "SOURCE.txt")):
            if os.path.exists(f):
                z.write(f, "common/BepInEx/" + ("BepInEx-" + os.path.basename(f)))
    print("payload", path, os.path.getsize(path) // 1024, "KB")
    return path


README = """Nuclear Option Server Tester {version}

Finds the cores, core count, physics workers and frame target a Nuclear Option dedicated server needs on this PC.

How to use it
1. Extract this zip anywhere and run {exe}. Nothing is installed: everything it keeps goes in a "data" folder next
   to it. The first time, it explains how it works and offers to take a baseline: the Stress Test on the server as
   installed, which the core count and physics worker tests are measured against.
2. Press INSTALL (or TAKE A BASELINE, which installs it too). It downloads its own copy of the dedicated server
   from Steam (about 1 GB) and never touches a server you already run.
3. Press TEST CORE SPEEDS (about half a minute) so it knows your fastest cores.
4. Pick the test mission (Stress Test to find your settings, Realistic Benchmark to check them in a full battle),
   then the automated test (the baseline, every core count with every worker count, the frame target and the AI
   load, each round on the best of the last) or the tests you tick, and press START. Leave the PC alone while it
   runs: anything else busy takes time from the test server.
5. When it finishes, the Comparison tab shows the baseline, then each setting as a row with its average, and
   "Apply results" shows what to set. Hover any ? for what an option does.
   With the NukeStats dashboard, Settings > "Apply Server Tester results" takes its results.json.

The test server is hidden, has a random password and uses ports 7790 and 27030, so it can run next to a real
server, although each slows the other.

Without the window (over SSH, say): {exe} --cli runs the same test in a terminal and prints the report
(--automated runs the automated test, --tests baseline,cores,workers,target,ai picks the tests, --mission realistic
the Realistic Benchmark, --ai 80 the AI aircraft, --ai-start 40 --ai-step 10 --ai-max 120 the AI load test,
--repeats 3 the runs). On Windows a shell doesn't wait for it: start "" /wait ServerTester.exe --cli

The results compare settings on an empty server with a fixed AI load. Use them to pick the cores and workers; a
full match with players costs more than the test.

More detail: docs/SERVER_TUNING.md in https://github.com/Tomosnuclearbombos/nuclear-option-toolkit
"""

THIRD = """Third-party parts inside this program
- BepInEx 5.4.23.5 (LGPL-2.1), https://github.com/BepInEx/BepInEx , put on the test server only
- Unity Doorstop (LGPL-2.1), https://github.com/NeighTools/UnityDoorstop , put on the test server only
- Avalonia UI (MIT), https://avaloniaui.net
- SkiaSharp (MIT), https://github.com/mono/SkiaSharp , with Skia (BSD-3-Clause), https://skia.org
- HarfBuzzSharp (MIT), https://github.com/mono/SkiaSharp , with HarfBuzz (Old MIT), https://harfbuzz.github.io
- ANGLE (BSD-3-Clause), https://chromium.googlesource.com/angle/angle , Windows build only
- Tmds.DBus.Protocol (MIT), https://github.com/tmds/Tmds.DBus , and MicroCom.Runtime (MIT), https://github.com/kekekeks/MicroCom
- .NET runtime (MIT), https://dotnet.microsoft.com
- Inter font (SIL Open Font License 1.1), https://rsms.me/inter
- The Stress Test and Realistic Benchmark missions are made from the game's own Escalation, put on the test server
  only
- SteamCMD is downloaded from Valve on first use; the Nuclear Option dedicated server comes from Steam (app 3930080)
"""


def publish(rid, out_dir):
    pub = os.path.join(HERE, "dist", "publish-" + rid)
    if os.path.isdir(pub):
        shutil.rmtree(pub)
    run(["dotnet", "publish", os.path.join(HERE, "ServerTester.csproj"), "-c", "Release", "-r", rid, "-o", pub])
    exe = "ServerTester.exe" if rid.startswith("win") else "ServerTester"
    ver = version()
    name = "Nuclear-Option-Server-Tester-{}-{}.zip".format(ver, "windows" if rid.startswith("win") else "linux")
    zpath = os.path.join(out_dir, name)
    folder = "Nuclear-Option-Server-Tester/"
    with zipfile.ZipFile(zpath, "w", zipfile.ZIP_DEFLATED) as z:
        # the program and the graphics libraries it loads from beside it (Skia, HarfBuzz, and ANGLE on Windows)
        for f in sorted(os.listdir(pub)):
            if f.endswith((".pdb", ".dbg")) or not os.path.isfile(os.path.join(pub, f)):
                continue
            info = zipfile.ZipInfo(folder + f)
            info.external_attr = ((0o755 if f == exe else 0o644) | stat.S_IFREG) << 16
            info.compress_type = zipfile.ZIP_DEFLATED
            with open(os.path.join(pub, f), "rb") as fh:
                z.writestr(info, fh.read())
        nl = "\r\n" if rid.startswith("win") else "\n"
        for name, text in (("README.txt", README.format(version=ver, exe=exe)), ("THIRD-PARTY.txt", THIRD)):
            info = zipfile.ZipInfo(folder + name)
            info.external_attr = (0o644 | stat.S_IFREG) << 16
            info.compress_type = zipfile.ZIP_DEFLATED
            z.writestr(info, text.replace("\n", nl))
    print("made", zpath, os.path.getsize(zpath) // (1024 * 1024), "MB")
    return zpath


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default=os.path.join(HERE, "dist"))
    ap.add_argument("--only", choices=["linux-x64", "win-x64"])
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)
    tmp = os.path.join(HERE, "dist", "tmp")
    os.makedirs(tmp, exist_ok=True)
    make_payload(build_plugin(tmp), tmp)
    for rid in ([a.only] if a.only else ["win-x64", "linux-x64"]):
        publish(rid, a.out)


if __name__ == "__main__":
    main()
