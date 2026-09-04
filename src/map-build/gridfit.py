#!/usr/bin/env python3
"""Robust grid-line detector: the drawn 10km grid is a set of THIN, full-span, faint
ridges over near-black water. Score each row/col by a thin-ridge metric (brighter than
its perpendicular neighbours), then fit a regular comb of (ncells+1) lines. Validated
against Heartland's ground-truth RANSAC bounds, then used for Ignus."""
from PIL import Image

XMIN, ZNORTH, CELL = -70000.0, 80000.0, 10000.0


def ridge_v(px, W, H, d=4):
    """vertical-line strength per column x (thin bright ridge vs left/right neighbours)."""
    out = [0.0] * W
    for x in range(d, W - d):
        s = 0
        for y in range(0, H, 2):
            g = px[x, y][1]
            nb = max(px[x - d, y][1], px[x + d, y][1])
            v = g - nb
            if v > 6 and g < 150:                 # faint line, not bright coastline blob
                s += v
        out[x] = s
    return out


def ridge_h(px, W, H, d=4):
    out = [0.0] * H
    for y in range(d, H - d):
        s = 0
        for x in range(0, W, 2):
            g = px[x, y][1]
            nb = max(px[x, y - d][1], px[x, y + d][1])
            v = g - nb
            if v > 6 and g < 150:
                s += v
        out[y] = s
    return out


def comb_fit(score, nlines, length, fixed_step=None):
    """best (first_px, step) for a regular comb of nlines lines within [0,length).
    If fixed_step is given, only the phase is fitted (cells are square -> row step == col step)."""
    ncells = nlines - 1
    base = length / ncells               # cell size if grid filled the image; margins shrink it
    steps = [fixed_step] if fixed_step else [s / 100.0 for s in
             range(int(base * 100 * 0.80), int(base * 100 * 1.04))]
    best = None
    for s in steps:
        for f in range(-int(s), int(length) + 1):        # grid may be cropped (lines off-frame)
            tot = 0.0
            seen = 0
            for k in range(nlines):
                ip = int(f + k * s)
                if 0 <= ip < length:
                    tot += max(score[ip], score[min(length - 1, ip + 1)], score[max(0, ip - 1)])
                    seen += 1
            if seen >= nlines - 2 and (best is None or tot > best[0]):   # most lines must be visible
                best = (tot, f, s)
    return best[1], best[2]


def calibrate(img_path, ncx, ncz):
    img = Image.open(img_path).convert("RGB")
    W, H = img.size
    px = img.load()
    rv = ridge_v(px, W, H)
    rh = ridge_h(px, W, H)
    fx, sx = comb_fit(rv, ncx + 1, W)      # ncx cells -> ncx+1 vertical lines
    fy, sy = comb_fit(rh, ncz + 1, H, fixed_step=sx)   # square cells: row step == col step
    # line fx = left grid line = world x for col 4 left edge = -40000; step sx = 1 cell
    x0 = -40000.0 - fx * CELL / sx
    x1 = x0 + W * CELL / sx
    z0 = 40000.0 + fy * CELL / sy
    z1 = z0 - H * CELL / sy
    return dict(W=W, H=H, fx=fx, sx=sx, fy=fy, sy=sy, x0=x0, x1=x1, z0=z0, z1=z1)


if __name__ == "__main__":
    print("HEARTLAND (validate vs RANSAC ground-truth x0=-40254 x1=41027 z0=40802 z1=-39633):")
    h = calibrate("heartland.png", 8, 8)
    print(f"  lines: vert first={h['fx']:.1f} step={h['sx']:.2f}  horz first={h['fy']:.1f} step={h['sy']:.2f}")
    print(f"  BOUNDS x0={h['x0']:.1f} x1={h['x1']:.1f} z0={h['z0']:.1f} z1={h['z1']:.1f}")
    print(f"  grid extent px: x[{h['fx']:.1f}..{h['fx']+8*h['sx']:.1f}] y[{h['fy']:.1f}..{h['fy']+8*h['sy']:.1f}]")
    print("\nIGNUS (16x8):")
    g = calibrate("ignus.png", 16, 8)
    print(f"  lines: vert first={g['fx']:.1f} step={g['sx']:.2f}  horz first={g['fy']:.1f} step={g['sy']:.2f}")
    print(f"  BOUNDS x0={g['x0']:.1f} x1={g['x1']:.1f} z0={g['z0']:.1f} z1={g['z1']:.1f}")
    print(f"  grid extent px: x[{g['fx']:.1f}..{g['fx']+16*g['sx']:.1f}] y[{g['fy']:.1f}..{g['fy']+8*g['sy']:.1f}]")
