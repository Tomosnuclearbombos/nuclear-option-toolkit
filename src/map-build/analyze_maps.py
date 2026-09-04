#!/usr/bin/env python3
"""Calibration analysis for the map screenshots. Prints image size, detected colour-
marker pixel centroids (yellow=Pala, purple=BDF), and a grid-line strength profile so
we can pin the pixel->world transform to GROUND TRUTH (known airbase grid refs)."""
from PIL import Image

XMIN, ZNORTH, CELL = -70000.0, 80000.0, 10000.0


def is_yellow(r, g, b):
    return r > 165 and g > 135 and b < 125 and (r - b) > 65 and abs(r - g) < 75


def is_purple(r, g, b):
    return r > 120 and b > 115 and g < r - 22 and g < b - 18 and (r + b) > 290


def detect(px, W, H, pred):
    sc = 2
    cw, ch = W // sc, H // sc
    hit = [[pred(*px[c * sc, r * sc]) for c in range(cw)] for r in range(ch)]
    seen = [[False] * cw for _ in range(ch)]
    raw = []
    for r in range(ch):
        for c in range(cw):
            if hit[r][c] and not seen[r][c]:
                st, cells = [(r, c)], []
                seen[r][c] = True
                while st:
                    y, x = st.pop(); cells.append((y, x))
                    for dy, dx in ((1,0),(-1,0),(0,1),(0,-1),(1,1),(1,-1),(-1,1),(-1,-1)):
                        ny, nx = y + dy, x + dx
                        if 0 <= ny < ch and 0 <= nx < cw and not seen[ny][nx] and hit[ny][nx]:
                            seen[ny][nx] = True; st.append((ny, nx))
                if len(cells) >= 4:
                    ys = [p[0] for p in cells]; xs = [p[1] for p in cells]
                    raw.append([len(cells), sum(xs)/len(xs)*sc, sum(ys)/len(ys)*sc])
    raw.sort(reverse=True)
    merged = []
    for s, x, y in raw:
        for mm in merged:
            if abs(mm[1]-x) < 28 and abs(mm[2]-y) < 28:
                mm[0] += s; break
        else:
            if s >= 8:
                merged.append([s, x, y])
    return merged


def grid_profile(px, W, H):
    """Faint grid lines are dim, low-saturation, regular. Score each column/row by how
    many pixels look like a thin grey/green grid line over black water."""
    def gridish(r, g, b):
        mx, mn = max(r, g, b), min(r, g, b)
        return 1 if (14 < mx < 120 and (mx - mn) < 40) else 0
    cs = [sum(gridish(*px[x, y]) for y in range(0, H, 2)) for x in range(W)]
    rs = [sum(gridish(*px[x, y]) for x in range(0, W, 2)) for y in range(H)]
    return cs, rs


def peaks(prof, n, minsep):
    idx = sorted(range(len(prof)), key=lambda i: prof[i], reverse=True)
    out = []
    for i in idx:
        if all(abs(i - j) >= minsep for j in out):
            out.append(i)
        if len(out) >= n:
            break
    return sorted(out)


def _demo(key):
    img = Image.open(key + ".png").convert("RGB")
    W, H = img.size
    px = img.load()
    print(f"\n===== {key}  {W}x{H} =====")
    yel = detect(px, W, H, is_yellow)
    pur = detect(px, W, H, is_purple)
    print(f"YELLOW/Pala markers ({len(yel)}):")
    for s, x, y in sorted(yel, key=lambda m: (m[2], m[1])):
        print(f"   px=({x:6.1f},{y:6.1f})  size={s}")
    print(f"PURPLE/BDF markers ({len(pur)}):")
    for s, x, y in sorted(pur, key=lambda m: (m[2], m[1])):
        print(f"   px=({x:6.1f},{y:6.1f})  size={s}")
    ncx = 8 if key == "heartland" else 16
    cs, rs = grid_profile(px, W, H)
    vp = peaks(cs, ncx + 1, W // (ncx + 2))
    hp = peaks(rs, 9, H // 11)
    print(f"vertical grid-line px candidates ({ncx+1}): {vp}")
    if len(vp) >= 2:
        diffs = [vp[i+1]-vp[i] for i in range(len(vp)-1)]
        print(f"   spacings: {diffs}")
    print(f"horizontal grid-line px candidates (9): {hp}")
    if len(hp) >= 2:
        diffs = [hp[i+1]-hp[i] for i in range(len(hp)-1)]
        print(f"   spacings: {diffs}")


if __name__ == "__main__":
    for k in ("heartland", "ignus"):
        _demo(k)
