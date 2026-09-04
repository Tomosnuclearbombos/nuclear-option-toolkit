#!/usr/bin/env python3
"""Calibrate pixel<->world from the game's OWN printed grid labels (the most reliable
ground truth). Row letters sit in the left margin, column numbers in the top margin,
each centred in its cell. Detect their centroids, fit a line per axis, emit bounds.
Validated on Heartland against the RANSAC ground-truth fit."""
from PIL import Image

XMIN, ZNORTH, CELL = -70000.0, 80000.0, 10000.0


def text_runs(coords_strength, gap, minlen):
    """cluster 1-D indices with strength>0 into runs separated by >=gap blanks."""
    runs, cur = [], []
    blank = 0
    for i, s in enumerate(coords_strength):
        if s > 0:
            if blank >= gap and cur:
                runs.append(cur); cur = []
            cur.append((i, s)); blank = 0
        else:
            blank += 1
    if cur:
        runs.append(cur)
    out = []
    for run in runs:
        if run[-1][0] - run[0][0] + 1 >= minlen:
            wsum = sum(s for _, s in run)
            c = sum(i * s for i, s in run) / wsum
            out.append(c)
    return out


def near_white(r, g, b):
    mx, mn = max(r, g, b), min(r, g, b)
    return mx > 135 and (mx - mn) < 80          # light text, not saturated marker/coast


def detect_labels(path, ncol, label_band=52):
    im = Image.open(path).convert("RGB")
    W, H = im.size
    px = im.load()
    # row letters: left margin, strength per y = count of light pixels across the band
    rstr = [sum(1 for x in range(2, label_band) if near_white(*px[x, y])) for y in range(H)]
    # col numbers: top margin, strength per x
    cstr = [sum(1 for y in range(2, label_band) if near_white(*px[x, y])) for x in range(W)]
    rows = text_runs(rstr, gap=8, minlen=4)
    cols = text_runs(cstr, gap=8, minlen=3)
    return W, H, rows, cols


def lsq(xs, ys):
    n = len(xs); sx = sum(xs); sy = sum(ys); sxx = sum(a*a for a in xs); sxy = sum(a*b for a, b in zip(xs, ys))
    a = (n*sxy - sx*sy) / (n*sxx - sx*sx); b = (sy - a*sx) / n
    return a, b


def calibrate(path, ncol, xmin=XMIN):
    W, H, rows, cols = detect_labels(path, ncol)
    print(f"  detected {len(rows)} row labels, {len(cols)} col labels  (expect 8, {ncol})")
    print(f"  row centres py: {[round(r,1) for r in rows]}")
    print(f"  col centres px: {[round(c,1) for c in cols]}")
    # rows are E..L (world-z centres); E is grid row index 4
    if len(rows) >= 6:
        zc = [ZNORTH - (4 + i + 0.5) * CELL for i in range(len(rows))]
        az, bz = lsq(rows, zc)            # z = az*py + bz
    else:
        az = bz = None
    if len(cols) >= 4:
        xc = [xmin + ((4 + i) - 1 + 0.5) * CELL for i in range(len(cols))]  # cols 4..
        ax, bx = lsq(cols, xc)
    else:
        ax = bx = None
    return W, H, ax, bx, az, bz, rows, cols


if __name__ == "__main__":
    for key, ncol in (("heartland", 8), ("ignus", 16)):
        print(f"\n===== {key} =====")
        W, H, ax, bx, az, bz, rows, cols = calibrate(key + ".png", ncol)
        if ax and az:
            x0, x1 = bx, ax * W + bx
            z0, z1 = bz, az * H + bz
            print(f"  ax={ax:.4f} bx={bx:.1f}  az={az:.4f} bz={bz:.1f}")
            print(f"  BOUNDS x0={x0:.1f} x1={x1:.1f} z0={z0:.1f} z1={z1:.1f}")
            print(f"  px/cell: step_x={CELL/ax:.2f}  step_y={CELL/-az:.2f}")
    print("\n(Heartland RANSAC ground truth: x0=-40254 x1=41027 z0=40802 z1=-39633)")
