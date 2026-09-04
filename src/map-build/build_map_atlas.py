#!/usr/bin/env python3
"""Bake the live-map atlas + terrain PNGs from the in-game grid SCREENSHOTS
(heartland.png / ignus.png).

Calibration is pinned to the game's OWN printed grid labels (rows E-L, Heartland cols
4-11, Ignus cols 4-19) via label_calibrate -> exact pixel<->world transform, so blips,
bases and the grid all line up with the real game coordinates. Outputs:
  * <key>_map.png  : clean, detailed terrain (green land / dark water, anti-aliased
                     coastlines; NO grid / labels / markers / ocean specks). The web map
                     draws the grid, coordinate gutters and blips live on top of this.
  * map_atlas.py   : ATLAS dict (name, cols/rows, bounds x0/x1/z0/z1, grid model, bases,
                     and a coarse cell 'terr' grid kept for the legacy TUI command centre).
Re-run after changing the screenshots.
"""
import os
from PIL import Image
from collections import deque
from label_calibrate import calibrate
from analyze_maps import detect, is_yellow, is_purple

# This script lives in map-build/; the SOURCE screenshots live alongside it, and the OUTPUTS
# (map_atlas.py + <key>_map.png) are written to the project ROOT (one level up) where cc_web
# and the bot read them. Works run from anywhere: `python map-build/build_map_atlas.py`.
HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)

XMIN, ZNORTH, CELL = -70000.0, 80000.0, 10000.0

MAPS = {
    # Heartland and Ignus have DIFFERENT x-origins: Heartland's grid starts at col 1 = xmin
    # -70000 (verified by the 12 airbase refs); Ignus is a wider map whose col 1 = xmin -110000
    # (verified live: Broken Atoll "Ki44" is at game-world x=-75778 => col 4 only if xmin=-110000).
    "heartland": dict(img="heartland.png", name="Heartland", ncol=8, xmin=-70000.0, gcols=15,
                      cols=160, rows=160, out_w=1191, thr=34, gfloor=28,
                      # Heartland's screenshot has the printed 10km grid baked in as green lines. The
                      # game's grid is OFFSET from our calibrated world-grid, so a positional band mask
                      # cut gaps in the wrong place and left the green lines. Instead detect the ACTUAL
                      # full-span thin green lines and inpaint them from neighbouring terrain (no gap,
                      # no line). The web map draws its own grid on top. Ignus is clean -> original path.
                      mask_band=0, strip_grid=True, deartifact=False),
    "ignus":     dict(img="ignus.png", name="Ignus Archipelago", ncol=16, xmin=-110000.0, gcols=23,
                      cols=320, rows=160, out_w=1600, thr=44, gfloor=40,
                      drop_refs={"H5", "H18"}),   # two SHIPS mis-detected as airbases -> exclude
}
# gcols = the FULL in-game grid width (cols 1..gcols); rows are always A..P (16). The web map
# lets you pan/zoom out to this full a1..p{gcols} extent; the terrain + pips stay at their exact
# calibrated sub-region (cols 4-11 Heartland / 4-19 Ignus).

# Extra Ignus bases the marker-detector misses, supplied by the admin (ref = [Major][minorRow?]
# [col][minorCol?]). "Neutral" renders grey (a major airfield, neither faction's spawn).
IGNUS_EXTRA = [
    ("Ignus Major West",        "Primeva", "Jb79"),   # NE corner of J7, on the Ignus Major coast
    ("Feldspar International",   "Neutral", "Hg122"),  # was H12 (cell centre = over water); airfield (2 NW-facing runways) is W+S of centre
    ("Camp Westpoint (heli)",   "Primeva", "Ia106"),
    ("Southgate Plaza (heli)",  "Primeva", "Ie106"),
    ("Bridgehead Ops Base (heli)", "Boscali", "Hj138"),
    ("Rivan Beach (heli)",      "Boscali", "Hb137"),
]


def decode_ref(ref, xmin):
    """[Major][minorRow?][col][minorCol?] -> world (x,z). Major-only refs use the cell centre."""
    major = ref[0].upper(); rest = ref[1:]
    minor = None
    if rest and rest[0].isalpha():
        minor = rest[0].lower(); rest = rest[1:]
    if minor is not None and len(rest) >= 2:
        col = int(rest[:-1]); mcol = int(rest[-1])
    else:
        col = int(rest); mcol = None
    ri = ord(major) - 65
    mri = (ord(minor) - 97) if minor else None
    z = ZNORTH - (ri + ((mri + 0.5) / 10 if mri is not None else 0.5)) * CELL
    x = xmin + ((col - 1) + ((mcol + 0.5) / 10 if mcol is not None else 0.5)) * CELL
    return round(x, 1), round(z, 1)


def ref_world(major, minor, col, mcol):
    z = ZNORTH - ((ord(major) - 65) + (ord(minor) - 97 + 0.5) / 10.0) * CELL
    x = XMIN + ((col - 1) + (mcol + 0.5) / 10.0) * CELL
    return round(x, 1), round(z, 1)


# Heartland airbases — exact grid refs supplied by the server admin (ground truth, so we
# place them precisely instead of trusting marker detection / avoiding false specks).
HEART_BASES = [
    ("North Boscali",        'E', 'h', 6, 1, "Boscali"),
    ("Northcoast Enrichment", 'E', 'e', 8, 3, "Boscali"),
    ("Maris Airport",        'G', 'a', 9, 3, "Boscali"),
    ("K92 Highway Strip",    'G', 'h', 9, 9, "Boscali"),
    ("Maris Heliport",       'H', 'a', 8, 6, "Boscali"),
    ("South Boscali Aviation",'I', 'g', 7, 0, "Boscali"),
    ("Vigil Cay Naval",      'J', 'c', 4, 6, "Primeva"),
    ("The Farm",             'J', 'j', 8, 2, "Primeva"),
    ("Dustbowl Highway",     'I', 'h', 9, 3, "Primeva"),
    ("Sanddrift",            'J', 'i', 10, 7, "Primeva"),
    ("Agrapol",              'K', 'g', 7, 8, "Primeva"),
    ("South Coast Enrichment",'L', 'f', 8, 7, "Primeva"),
]


def gref(x, z, xmin=XMIN):
    return f"{chr(97 + int((ZNORTH - z) / CELL)).upper()}{int((x - xmin) / CELL) + 1}"


def greenness(px, W, H, gridx, gridy, gfloor, band=3):
    """per-pixel green-dominance (0..255) = the terrain detail we keep. Non-green pixels
    (black water, grey cursor/UI, yellow/purple markers, the pink cursor arrow) all read 0.
    The drawn 10km grid is BAND-MASKED at its known pixel positions (+/- `band` px) so it never
    shows as a green line (the web map draws its own grid at exactly those positions, hiding the gap).
    band<=0 disables positional masking (used by maps that strip the grid by detection instead)."""
    gxset = set(int(round(lx)) + d for lx in gridx for d in range(-band, band + 1)) if band > 0 else set()
    gyset = set(int(round(ly)) + d for ly in gridy for d in range(-band, band + 1)) if band > 0 else set()
    G = [[0] * W for _ in range(H)]
    for y in range(H):
        if y in gyset:
            continue
        row = G[y]
        for x in range(W):
            if x in gxset:
                continue
            r, g, b = px[x, y]
            if g >= r and g >= b and g > gfloor and (g - min(r, b)) > 10:
                row[x] = g
    return G


def strip_grid_lines(G, W, H, period_x, period_y, band=2):
    """Erase the baked-in map grid by INTERPOLATING smoothly across each line. Positions are found
    from the image: the PERIOD comes from the calibration (label spacing -- reliable), and the PHASE
    is locked empirically to the actual lines using a 'how much brighter than the terrain a few px to
    each side' strength signal (a straight axis-aligned line scores high all along its length; curved
    roads don't). Interpolation across each ~band-px line (not copy/blank) leaves no green line, no
    black gap, and no smear -- every other pixel is identical, so the topography stays crisp."""
    Dd = 3
    vstr = [0] * W
    for x in range(Dd, W - Dd):
        s = 0
        for y in range(H):
            g = G[y][x]; m = G[y][x - Dd] if G[y][x - Dd] > G[y][x + Dd] else G[y][x + Dd]
            if g > m:
                s += g - m
        vstr[x] = s
    hstr = [0] * H
    for y in range(Dd, H - Dd):
        s = 0; ru = G[y - Dd]; rd = G[y + Dd]; row = G[y]
        for x in range(W):
            g = row[x]; m = ru[x] if ru[x] > rd[x] else rd[x]
            if g > m:
                s += g - m
        hstr[y] = s

    def detect(strength, n, period):
        P = int(round(period))
        if P < 6 or P >= n:
            return set()
        def comb(ph):                                          # peak strength near each tooth at this phase
            t = 0.0; x = ph
            while x < n:
                xi = int(round(x)); lo, hi = max(0, xi - 5), min(n, xi + 6)
                t += max(strength[lo:hi]); x += period
            return t
        best = max(range(P), key=comb)
        out = set(); x = best
        while x < n:
            xi = int(round(x)); lo, hi = max(0, xi - 5), min(n, xi + 6)
            j = max(range(lo, hi), key=lambda k: strength[k])  # snap tooth to the actual line
            for d in range(-band, band + 1):
                if 0 <= j + d < n:
                    out.add(j + d)
            x += period
        return out

    cols = detect(vstr, W, period_x)
    rows = detect(hstr, H, period_y)
    for x in sorted(cols):                                      # vertical grid lines: interpolate L<->R
        xl, xr = x - 1, x + 1
        while xl in cols and xl > 0:
            xl -= 1
        while xr in cols and xr < W - 1:
            xr += 1
        span = xr - xl
        t = (x - xl) / span if span else 0.5
        for y in range(H):
            a = G[y][xl] if 0 <= xl < W else 0
            b = G[y][xr] if 0 <= xr < W else 0
            G[y][x] = int(a * (1 - t) + b * t + 0.5)
    for y in sorted(rows):                                      # horizontal grid lines: interpolate up<->down
        yl, yr = y - 1, y + 1
        while yl in rows and yl > 0:
            yl -= 1
        while yr in rows and yr < H - 1:
            yr += 1
        span = yr - yl
        t = (y - yl) / span if span else 0.5
        rl = G[yl] if 0 <= yl < H else None
        rr = G[yr] if 0 <= yr < H else None
        row = G[y]
        for x in range(W):
            a = rl[x] if rl else 0
            b = rr[x] if rr else 0
            row[x] = int(a * (1 - t) + b * t + 0.5)
    nvc = len(cols) // (2 * band + 1) if band else len(cols)
    nrc = len(rows) // (2 * band + 1) if band else len(rows)
    print(f"  strip_grid: interpolated out ~{nvc} vertical + ~{nrc} horizontal grid lines "
          f"(period ~{int(round(period_x))}/{int(round(period_y))}px, phase-locked to the image)")
    return G


def build_terrain(m):
    """returns (atlas_bounds, ws, W2, H2, landm, G, png_image)."""
    key = m["img"].split(".")[0]
    imgpath = os.path.join(HERE, m["img"])
    src = Image.open(imgpath).convert("RGB")
    W, H = src.size
    px = src.load()
    xmin = m["xmin"]
    _, _, ax, bx, az, bz, _, _ = calibrate(imgpath, m["ncol"], xmin)
    x0, x1 = bx, ax * W + bx
    z0, z1 = bz, az * H + bz
    gridx = [(xmin + n * CELL - bx) / ax for n in range(0, 32) if 0 <= (xmin + n * CELL - bx) / ax <= W]
    gridy = [p for p in ((bz - (ZNORTH - n * CELL)) / (-az) for n in range(0, 32)) if 0 <= p <= H]
    G = greenness(px, W, H, gridx, gridy, m["gfloor"], m.get("mask_band", 3))
    if m.get("strip_grid"):
        G = strip_grid_lines(G, W, H, CELL / abs(ax), CELL / abs(az), 2)

    # FAITHFUL render: keep green where the source is green, black where it's black (the
    # in-game map is green topographic detail on a black base — NO solid-filling the interior,
    # which is what turned black water/voids green). Only clean up isolated green noise specks.
    ws = max(1, round(W / 620))
    W2, H2 = W // ws, H // ws
    THR = m["thr"]
    gmax2 = [[0] * W2 for _ in range(H2)]          # brightest green per work cell
    for y2 in range(H2):
        for x2 in range(W2):
            best = 0
            for dy in range(ws):
                yy = y2 * ws + dy
                if yy >= H:
                    break
                gr = G[yy]
                for dx in range(ws):
                    xx = x2 * ws + dx
                    if xx < W and gr[xx] > best:
                        best = gr[xx]
            gmax2[y2][x2] = best
    green2 = [[gmax2[y][x] > THR for x in range(W2)] for y in range(H2)]
    seen = [[False] * W2 for _ in range(H2)]
    kill = [[False] * W2 for _ in range(H2)]
    specks = 0
    if m.get("deartifact"):
        # MORPHOLOGICAL OPENING (Heartland): keep a green work-cell only if its (2R+1)x(2R+1)
        # neighbourhood is mostly green. Thin overlay strokes (grid / road / radar-sweep lines that
        # cross sparse areas) have few green neighbours and drop out; dense topographic terrain
        # survives. Then remove any tiny specks the opening leaves behind. The PNG then renders ONLY
        # the opened cells -> the straight lines cutting across the map vanish, detail stays.
        R = 2
        minnb = m.get("min_density", 9)
        dense = [[False] * W2 for _ in range(H2)]
        for y in range(H2):
            y0, y1 = max(0, y - R), min(H2, y + R + 1)
            drow = dense[y]
            for x in range(W2):
                if not green2[y][x]:
                    continue
                x0, x1 = max(0, x - R), min(W2, x + R + 1)
                c = 0
                for yy in range(y0, y1):
                    g2 = green2[yy]
                    for xx in range(x0, x1):
                        if g2[xx]:
                            c += 1
                if c >= minnb:
                    drow[x] = True
        for sy in range(H2):
            for sx in range(W2):
                if not dense[sy][sx] or seen[sy][sx]:
                    continue
                comp = []; dq = deque([(sy, sx)]); seen[sy][sx] = True
                while dq:
                    y, x = dq.popleft(); comp.append((y, x))
                    for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1)):
                        ny, nx = y + dy, x + dx
                        if 0 <= ny < H2 and 0 <= nx < W2 and dense[ny][nx] and not seen[ny][nx]:
                            seen[ny][nx] = True; dq.append((ny, nx))
                if len(comp) <= 3:
                    specks += 1
                    for (y, x) in comp:
                        kill[y][x] = True
        landm = [[dense[y][x] and not kill[y][x] for x in range(W2)] for y in range(H2)]
        keep = landm                                 # render ONLY the opened (dense terrain) cells
    else:
        # ORIGINAL filter (Ignus + default): drop small components + thin 1-cell-thick straight
        # line-fragments (>=5 long) -> speckle / leftover grid bits, never real terrain. Untouched
        # so the already-perfect Ignus render is preserved byte-for-byte.
        for sy in range(H2):
            for sx in range(W2):
                if not green2[sy][sx] or seen[sy][sx]:
                    continue
                comp = []; dq = deque([(sy, sx)]); seen[sy][sx] = True
                while dq:
                    y, x = dq.popleft(); comp.append((y, x))
                    for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1)):
                        ny, nx = y + dy, x + dx
                        if 0 <= ny < H2 and 0 <= nx < W2 and green2[ny][nx] and not seen[ny][nx]:
                            seen[ny][nx] = True; dq.append((ny, nx))
                ys = [c[0] for c in comp]; xs = [c[1] for c in comp]
                bw = max(xs) - min(xs); bh = max(ys) - min(ys)
                thinline = min(bw, bh) <= 1 and max(bw, bh) >= 5
                if len(comp) <= 3 or thinline:
                    specks += 1
                    for (y, x) in comp:
                        kill[y][x] = True
        landm = [[green2[y][x] and not kill[y][x] for x in range(W2)] for y in range(H2)]
        keep = [[not kill[y][x] for x in range(W2)] for y in range(H2)]   # original gate: zero only kills
    # cleaned native green: render greenness only where kept, black elsewhere
    gimg = Image.new("L", (W, H))
    gimg.putdata([min(255, G[y][x]) if keep[min(H2 - 1, y // ws)][min(W2 - 1, x // ws)] else 0
                  for y in range(H) for x in range(W)])
    out_w = m["out_w"]; out_h = round(out_w * H / W)
    grnB = gimg.resize((out_w, out_h), Image.BILINEAR).load()
    out = Image.new("RGBA", (out_w, out_h)); op = out.load()
    WATER = (6, 13, 22)
    for y in range(out_h):
        for x in range(out_w):
            g = grnB[x, y]
            if g <= 18:                            # black on the source -> black water here
                op[x, y] = (*WATER, 255); continue
            tt = max(0.0, min(1.0, (g - 18) / 150.0))     # green intensity ramp
            op[x, y] = (min(255, 14 + int(tt * 70)), min(255, 60 + int(tt * 165)),
                        min(255, 30 + int(tt * 66)), 255)
    out.save(os.path.join(ROOT, key + "_map.png"))
    print(f"{key}: src {W}x{H} -> work {W2}x{H2} (ws={ws}) png {out_w}x{out_h}; specks removed={specks}")
    return dict(x0=x0, x1=x1, z0=z0, z1=z1), ws, W2, H2, landm, G, (ax, bx, az, bz, W, H)


def coarse_terr(landm, G, W2, H2, ws, cols, rows, W, H):
    """downsample the land mask to a cols x rows cell grid (legacy TUI 'terr')."""
    terr = []
    for r in range(rows):
        line = []
        for c in range(cols):
            x2 = min(W2 - 1, int((c + 0.5) / cols * W2))
            y2 = min(H2 - 1, int((r + 0.5) / rows * H2))
            if not landm[y2][x2]:
                line.append("W")
            else:
                gx = min(W - 1, int((c + 0.5) / cols * W))
                gy = min(H - 1, int((r + 0.5) / rows * H))
                line.append(str(max(2, min(9, G[gy][gx] // 26 + 2))))
        terr.append("".join(line))
    return terr


def bases_for(m, trans):
    key = m["img"].split(".")[0]
    ax, bx, az, bz, W, H = trans
    if key == "heartland":
        out = []
        for nm, mj, mn, col, mc, fac in HEART_BASES:
            wx, wz = ref_world(mj, mn, col, mc)
            out.append((nm, "w", wx, wz, fac))
        return out
    # Ignus: no supplied refs -> detect coloured markers and convert via the transform.
    # Drop any whose grid ref is blocklisted (ships mis-detected as airbases).
    xmin = m["xmin"]; drop = m.get("drop_refs") or set()
    src = Image.open(os.path.join(HERE, key + ".png")).convert("RGB"); px = src.load()
    out = []
    for pred, fac in ((is_yellow, "Primeva"), (is_purple, "Boscali")):
        for size, cx, cy in detect(px, W, H, pred):
            wx = round(ax * cx + bx, 1); wz = round(az * cy + bz, 1)
            if gref(wx, wz, xmin) in drop:
                continue
            out.append(("", "w", wx, wz, fac))
    for nm, fac, ref in IGNUS_EXTRA:                      # admin-supplied bases the detector misses
        wx, wz = decode_ref(ref, xmin)
        out.append((nm, "w", wx, wz, fac))
    return out


def main():
    atlas = {}
    for key, m in MAPS.items():
        bounds, ws, W2, H2, landm, G, trans = build_terrain(m)
        terr = coarse_terr(landm, G, W2, H2, ws, m["cols"], m["rows"], trans[4], trans[5])
        bases = bases_for(m, trans)
        atlas[key] = dict(name=m["name"], cols=m["cols"], rows=m["rows"], gcols=m["gcols"],
                          x0=bounds["x0"], x1=bounds["x1"], z0=bounds["z0"], z1=bounds["z1"],
                          xmin=m["xmin"], cell=CELL, znorth=ZNORTH, bases=bases, terr=terr)
    with open(os.path.join(ROOT, "map_atlas.py"), "w", encoding="utf-8") as f:
        f.write('"""Auto-generated by build_map_atlas.py - do not hand-edit."""\n')
        f.write("ATLAS = {\n")
        for k, d in atlas.items():
            f.write(f"  {k!r}: {{\n")
            for fld in ("name", "cols", "rows", "gcols", "x0", "x1", "z0", "z1", "xmin", "cell", "znorth"):
                f.write(f"    {fld!r}: {d[fld]!r},\n")
            f.write(f"    'bases': {d['bases']!r},\n    'terr': [\n")
            for row in d["terr"]:
                f.write(f"      {row!r},\n")
            f.write("    ],\n  },\n")
        f.write("}\n")
    print("\nwrote map_atlas.py + heartland_map.png + ignus_map.png")
    for k, d in atlas.items():
        print(f"\n{d['name']}: bounds x[{d['x0']:.0f},{d['x1']:.0f}] z[{d['z0']:.0f},{d['z1']:.0f}]  {len(d['bases'])} bases")
        for b in d["bases"]:
            print(f"   {gref(b[2], b[3], d['xmin']):5} {b[4]:8} {b[0]}")


if __name__ == "__main__":
    main()
