#!/usr/bin/env python3
"""Pin the Heartland pixel->world transform to GROUND TRUTH (the 12 known airbase grid
refs). 2D RANSAC: seed an axis-aligned transform from a colour-matched marker-pair <->
ref-pair, score by one-to-one inliers, keep best, refine by least-squares. Robust to
false specks and missed bases. Transform: x = ax*px + bx ; z = az*py + bz."""
from PIL import Image
from analyze_maps import detect, is_yellow, is_purple
from itertools import permutations

XMIN, ZNORTH, CELL = -70000.0, 80000.0, 10000.0


def ref_world(major, minor, col, mcol):
    z = ZNORTH - ((ord(major) - 65) + (ord(minor) - 97 + 0.5) / 10.0) * CELL
    x = XMIN + ((col - 1) + (mcol + 0.5) / 10.0) * CELL
    return x, z


HEART = [  # name, faction, major,minor,col,minorcol  (user-supplied refs)
    ("North Boscali",   "B", 'E', 'h', 6, 1), ("Northcoast Enr",  "B", 'E', 'e', 8, 3),
    ("Maris Airport",   "B", 'G', 'a', 9, 3), ("K92 Highway",     "B", 'G', 'h', 9, 9),
    ("Maris Heliport",  "B", 'H', 'a', 8, 6), ("South Boscali",   "B", 'I', 'g', 7, 0),
    ("Vigil Cay Naval", "Y", 'J', 'c', 4, 6), ("The Farm",        "Y", 'J', 'j', 8, 2),
    ("Dustbowl Hwy",    "Y", 'I', 'h', 9, 3), ("Sanddrift",       "Y", 'J', 'i', 10, 7),
    ("Agrapol",         "Y", 'K', 'g', 7, 8), ("South Coast Enr", "Y", 'L', 'f', 8, 7),
]


def gref(x, z):
    return f"{chr(97 + int((ZNORTH - z) / CELL)).upper()}{int((x - XMIN) / CELL) + 1}"


def score(T, markers, refs, tol=4000.0):
    ax, bx, az, bz = T
    used, inl, err = set(), 0, 0.0
    for (pxv, pyv, fac) in markers:
        wx, wz = ax * pxv + bx, az * pyv + bz
        best = None
        for i, (rx, rz, rf) in enumerate(refs):
            if rf != fac or i in used:
                continue
            d = ((wx - rx) ** 2 + (wz - rz) ** 2) ** 0.5
            if best is None or d < best[1]:
                best = (i, d)
        if best and best[1] < tol:
            used.add(best[0]); inl += 1; err += best[1]
    return inl, err


img = Image.open("heartland.png").convert("RGB")
W, H = img.size
px = img.load()
markers = ([(m[1], m[2], "Y") for m in detect(px, W, H, is_yellow)] +
           [(m[1], m[2], "B") for m in detect(px, W, H, is_purple)])
refs = [(*ref_world(*r[2:]), r[1]) for r in HEART]

best = None
mk_by = {"Y": [m for m in markers if m[2] == "Y"], "B": [m for m in markers if m[2] == "B"]}
rf_by = {"Y": [r for r in refs if r[2] == "Y"], "B": [r for r in refs if r[2] == "B"]}
for fac in ("Y", "B"):
    mk, rf = mk_by[fac], rf_by[fac]
    for i in range(len(mk)):
        for j in range(len(mk)):
            if abs(mk[i][0] - mk[j][0]) < 80 or abs(mk[i][1] - mk[j][1]) < 80:
                continue
            for p, q in permutations(range(len(rf)), 2):
                ax = (rf[p][0] - rf[q][0]) / (mk[i][0] - mk[j][0])
                az = (rf[p][1] - rf[q][1]) / (mk[i][1] - mk[j][1])
                if not (45 < ax < 110 and -110 < az < -45):   # plausible 10km/~150px scale
                    continue
                bx = rf[p][0] - ax * mk[i][0]
                bz = rf[p][1] - az * mk[i][1]
                T = (ax, bx, az, bz)
                s = score(T, markers, refs)
                if best is None or (s[0], -s[1]) > (best[0][0], -best[0][1]):
                    best = (s, T)

# least-squares refine on the inlier set
ax, bx, az, bz = best[1]
xp, xw, yp, zw = [], [], [], []
used = set()
for (pxv, pyv, fac) in markers:
    wx, wz = ax * pxv + bx, az * pyv + bz
    cand = [(i, ((wx - r[0])**2 + (wz - r[1])**2)**0.5) for i, r in enumerate(refs)
            if r[2] == fac and i not in used]
    if not cand:
        continue
    i, d = min(cand, key=lambda t: t[1])
    if d < 4000:
        used.add(i); xp.append(pxv); xw.append(refs[i][0]); yp.append(pyv); zw.append(refs[i][1])


def lsq(p, w):
    n = len(p); sp = sum(p); sw = sum(w); spp = sum(a*a for a in p); spw = sum(a*b for a, b in zip(p, w))
    a = (n*spw - sp*sw) / (n*spp - sp*sp); b = (sw - a*sp) / n
    return a, b
ax, bx = lsq(xp, xw)
az, bz = lsq(yp, zw)

x0, x1 = bx, ax * W + bx
z0, z1 = bz, az * H + bz
print(f"image {W}x{H}   inliers used for fit: {len(xp)}")
print(f"ax={ax:.4f} bx={bx:.2f}   az={az:.4f} bz={bz:.2f}")
print(f"BOUNDS  x0={x0:.1f}  x1={x1:.1f}  z0={z0:.1f}  z1={z1:.1f}")
print(f"   canonical target  x0=-40000 x1=40000 z0=40000 z1=-40000")
print(f"   grid extent px: left={(-40000-bx)/ax:.1f} right={(40000-bx)/ax:.1f} "
      f"top={(40000-bz)/az:.1f} bot={(-40000-bz)/az:.1f}")
print("\nmarker -> world via fitted transform (gref):")
for m in sorted(markers, key=lambda m: (m[1], m[0])):
    wx, wz = ax * m[0] + bx, az * m[1] + bz
    print(f"  {m[2]} px=({m[0]:6.1f},{m[1]:6.1f}) -> ({wx:7.0f},{wz:7.0f})  {gref(wx, wz)}")
print("\nground-truth refs:")
for nm, fac, *r in HEART:
    wx, wz = ref_world(*r)
    print(f"  {gref(wx, wz):5} {fac} {nm:16} ({wx:7.0f},{wz:7.0f})")
