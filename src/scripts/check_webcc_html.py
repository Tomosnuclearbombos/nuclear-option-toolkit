#!/usr/bin/env python3
"""Smoke-check webcc.html before ship.

Catches the 1.1.7 RTT ship-killer: `function _mapName(...)` colliding with the
existing atlas title binding `let ..., _mapName=''` in the same <script>
→ SyntaxError → blank/broken WebCC while HTTP 200 still looks fine.

Usage: python check_webcc_html.py [path/to/webcc.html]
Exit 0 = pass, 1 = fail.
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

DEFAULT = Path(__file__).resolve().parents[1] / "webcc.html"


def check(html: str) -> list[str]:
    errs: list[str] = []
    scripts = re.findall(r"<script(?:\s[^>]*)?>(.*?)</script>", html, flags=re.I | re.S)
    if not scripts:
        errs.append("no <script> blocks found")
        return errs
    for i, s in enumerate(scripts):
        if not s.strip():
            continue
        has_fn = bool(re.search(r"\bfunction\s+_mapName\s*\(", s))
        has_let = bool(re.search(r"\b(?:let|const|var)\b[^;]*\b_mapName\b", s))
        if has_fn and has_let:
            errs.append(
                f"script[{i}]: _mapName is both a function and a let/const/var binding "
                "(SyntaxError — use _playerMapLabel for the ping blip helper)"
            )
        if has_fn and not has_let:
            # function alone still stomps if something assigns _mapName= later
            if re.search(r"(?<![\w$])_mapName\s*=", s):
                errs.append(
                    f"script[{i}]: function _mapName overwritten by assignment "
                    "(use _playerMapLabel for the ping blip helper)"
                )
    # Require the fixed helper once Ping column exists
    if re.search(r"<th>\s*Ping\s*</th>", html, flags=re.I) and not re.search(
        r"\bfunction\s+_playerMapLabel\s*\(", html
    ):
        errs.append("Ping column present but _playerMapLabel helper missing")
    # Delayed map v7: tDraw=now−2.0 + linear A→B + pos_trail ingest — NO chase/heartbeat pads
    if re.search(r"\bMAP_DELAY_S\b", html):
        if not re.search(r"\bfunction\s+_posAt\s*\(", html):
            errs.append("MAP_DELAY_S present but _posAt missing")
        if not re.search(r"\bfunction\s+_mapClock\s*\(", html):
            errs.append("MAP_DELAY_S present but _mapClock missing")
        if not re.search(r"\btDraw\b", html):
            errs.append("tDraw missing — must be now−MAP_DELAY_S every RAF")
        if not re.search(r"WEBCC_BUILD\s*=\s*['\"]map-delay-v7", html):
            errs.append("WEBCC_BUILD map-delay-v7 stamp missing (cache-bust marker)")
        if not re.search(r"MAP_DELAY_S\s*=\s*2(?:\.0+)?\s*;", html):
            errs.append("MAP_DELAY_S must be exactly 2.0 (pure delayed playback)")
        if not re.search(r"\bfunction\s+_ingestTrail\s*\(", html):
            errs.append("_ingestTrail missing — v7 needs bot pos_trail ingest")
        if not re.search(r"\bMAP_COAST_S\b", html):
            errs.append("MAP_COAST_S missing — v7 soft coast past tip")
        if re.search(r"\bMAP_HB_DT\b", html):
            errs.append("MAP_HB_DT present — v7 forbids same-pos heartbeat pads")
        if re.search(r"\bfunction\s+_playheadT\s*\(", html) or re.search(
            r"\bMAP_MIN_LEAD_S\b", html
        ):
            errs.append("_playheadT / MAP_MIN_LEAD_S present — tip-clamp hacks forbidden")
        if re.search(r"\bfunction\s+_chasePos\s*\(", html):
            errs.append("_chasePos present — EMA chase forbidden")
        if re.search(r"\bMAP_SMOOTH_TAU_S\b", html) or re.search(r"\bposDisp\b", html) or re.search(
            r"\bentDisp\b", html
        ):
            errs.append("chase/EMA leftovers (MAP_SMOOTH_TAU_S/posDisp/entDisp) — delete")
        if re.search(r"\bGLIDE_EXPECT", html):
            errs.append("GLIDE_EXPECT still present — ease-to-sample pattern forbidden")
        if re.search(r"u\s*=\s*u\s*\*\s*u\s*\*\s*\(\s*3\s*-\s*2\s*\*\s*u\s*\)", html):
            errs.append("ease-to-halt u*u*(3-2*u) still present — stop-start jitter at each sample")
        if re.search(r"last\.ts\s*=\s*now", html) or re.search(r"last\.t\s*=\s*now", html):
            errs.append("heartbeat mutates last.t/ts=now — rewinds delayed timeline")
        if re.search(r"\bfunction\s+_catmull1\s*\(", html):
            errs.append("_catmull1 still present — linear _posAt playhead only")
        if re.search(r"WEBCC_BUILD\s*=\s*['\"]map-delay-v[456]", html):
            errs.append("stale WEBCC_BUILD map-delay-v4/v5/v6 — bump to map-delay-v7")
        # Alive draw must use _posAt → w2cx(x), not raw p.x
        if not re.search(
            r"const g=_posAt\(h,tDraw,MAX_GLIDE_M\).*?const cx=w2cx\(x\)",
            html,
            flags=re.S,
        ):
            errs.append("alive player draw must use _posAt → w2cx(x) (not raw p.x)")
        # Prove lerp uses sample.t (not raw live tip draw for alive)
        if not re.search(r"\(tDraw\s*-\s*a\.t\)", html) and not re.search(
            r"\(tDraw-a\.t\)", html
        ):
            errs.append("_posAt must lerp with u=(tDraw-a.t)/(b.t-a.t)")
    return errs


def main() -> int:
    path = Path(sys.argv[1]) if len(sys.argv) > 1 else DEFAULT
    html = path.read_text(encoding="utf-8")
    errs = check(html)
    if errs:
        print(f"FAIL {path}")
        for e in errs:
            print(" ", e)
        return 1
    n = len(re.findall(r"<script", html, flags=re.I))
    print(f"PASS {path} ({n} script tags)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
