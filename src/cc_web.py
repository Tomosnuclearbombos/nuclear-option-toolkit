#!/usr/bin/env python3
"""Nuke Option — Web Command Centre (backend).

A modern browser dashboard replacing the Textual TUI. Serves webcc.html + a JSON
API that reuses the bot's RemoteCommand relay, the baked map atlas, ranks.json,
the admin_commands.jsonl queue (so grant/team flow through the running bot, which
owns ranks + SFTP), and the Pterodactyl client API for real power control.

Run:  python cc_web.py   then open  http://127.0.0.1:8770
Config: apiKey.txt (Pterodactyl client key) + panel.txt (panel URL).
"""
import json
import math
import os
import re
import ssl
import threading
import time
import urllib.error
import urllib.request

from flask import Flask, jsonify, request, send_from_directory

import no_mapvote_bot as bot
try:
    from map_atlas import ATLAS as _ATLAS
except Exception:                                        # noqa: BLE001
    _ATLAS = {}

HERE = os.path.dirname(os.path.abspath(__file__))
DASHBOARD = os.path.join(HERE, "dashboard_state.json")
ACTIVITY = os.path.join(HERE, "activity.log")
CONSOLE = os.path.join(HERE, "console_mirror.log")
RANK_FILE = getattr(bot, "RANK_FILE", os.path.join(HERE, "ranks.json"))
SCHEDULE_FILE = os.path.join(HERE, "schedule.json")   # scheduled restarts/updates (UI here, executed by the bot)
PENDING_DLL  = os.path.join(HERE, "pending_plugin.dll")     # a plugin update waiting for the next deploy
PENDING_META = os.path.join(HERE, "pending_plugin.json")    # sidecar: {version, note, sha256, staged_at}
DEPLOYED_SHA = os.path.join(HERE, "deployed_plugin.sha256") # sha of the plugin currently deployed/live
DEPLOYED_META = os.path.join(HERE, "deployed_plugin.json")  # {version, sha, deployed_at} written by the deploy job
PORT = int(((getattr(bot, "_TK_CFG", {}) or {}).get("web", {}) or {}).get("port") or os.environ.get("PORT") or os.environ.get("NOCC_PORT") or 8770)  # config web.port -> env -> 8770
# Bind interface: default 127.0.0.1 (host-only, safer). Override for LAN with
# web.host="0.0.0.0" in config.json or NOCC_HOST=0.0.0.0 (env). Optional shared-secret
# auth: web.auth_token / NOCC_AUTH_TOKEN — when set, mutating POSTs require header X-NOCC-Token.
HOST = (((getattr(bot, "_TK_CFG", {}) or {}).get("web", {}) or {}).get("host")) or os.environ.get("NOCC_HOST") or "127.0.0.1"
AUTH_TOKEN = str((((getattr(bot, "_TK_CFG", {}) or {}).get("web", {}) or {}).get("auth_token"))
                 or os.environ.get("NOCC_AUTH_TOKEN") or "").strip()
# Extra hostnames this panel may legitimately be reached as, for a reverse proxy or a DNS alias.
# Comma-separated, host or full URL: web.allowed_origins / NOCC_ALLOWED_ORIGINS. Without this, a proxy
# deployment 403s every write because the browser's Origin is the public name and request.host is the
# backend's. X-Forwarded-Host is honoured automatically. (round-3 audit 2026-08-01)
ALLOWED_ORIGINS = [s.strip() for s in str(
    (((getattr(bot, "_TK_CFG", {}) or {}).get("web", {}) or {}).get("allowed_origins"))
    or os.environ.get("NOCC_ALLOWED_ORIGINS") or "").split(",") if s.strip()]


def _static_allowed_hosts():
    """The host:port values this panel serves itself on, computed from OUR config - never from a request
    header. A cross-origin check whose allowlist is seeded by the caller approves the caller."""
    hosts = {f"127.0.0.1:{PORT}", f"localhost:{PORT}", f"[::1]:{PORT}"}
    h = str(HOST or "").strip()
    if h and h not in ("0.0.0.0", "::"):                 # a real bind address is also a valid name
        hosts.add(f"{h}:{PORT}")
    else:
        # bound to every interface: accept this machine's own addresses, resolved once at startup
        try:
            import socket
            for info in socket.getaddrinfo(socket.gethostname(), None):
                ip = info[4][0]
                hosts.add(f"{ip}:{PORT}")
            hosts.add(f"{socket.gethostname().lower()}:{PORT}")
        except Exception:                                # noqa: BLE001
            pass
    return {x.lower() for x in hosts}


_STATIC_ALLOWED_HOSTS = _static_allowed_hosts()
SETTINGS_CATALOGUE = os.path.join(HERE, "settings_catalogue.json")  # static metadata for the settings menu
BOT_OVERRIDES = os.path.join(HERE, "bot_overrides.json")            # bot-owned setting overrides (current values)
ADMIN_RESULTS = os.path.join(HERE, "admin_results.jsonl")           # bot setcfg ack lines (webcc polls)
_last_dump_nudge = 0.0                                              # throttle the "ask the plugin to dump" nudge

# ── settings-catalogue transforms (all applied in code so ONLY cc_web.py changes) ──────────────
# Item 2: the radar / net-coalesce LIMITER rows go away with the plugin removal — strip them here so
#         the settings menu never offers a knob whose plugin bind no longer exists.
_CATALOGUE_REMOVE = {"Net.TrackingCoalesceMs", "Net.RadarWarnCoalesceMs",
                     # FIX 3: the raw PostMissionDelay is DERIVED (vote + delay) and bot-managed, never an
                     # operator input — the two coherent knobs below (MAP_VOTE_DURATION +
                     # POST_VOTE_MAP_CHANGE_DELAY) replace it. (1.2.0: VOTE_DURATION / APPROVAL_DURATION are
                     # gone from settings_catalogue.json entirely — they are derived aliases in the bot — so
                     # they no longer need stripping here.)
                     "PostMissionDelay",
                     # Owner request (1.2.0 settings pass): the settings menu must only ever show LIVE
                     # plugin/bot knobs. These three are mission-FILE values — they are stamped into the
                     # co-op mission files by `run.bat --set-ai-limits` and do nothing until that command
                     # is run, so they never belonged in a live-settings panel. The CLI keeps them.
                     "AI_OPP_LIMIT", "AI_OPP_ADDAI", "AI_PLR_LIMIT",
                     # Same pass: the two AILimit CAPS (32 AI/faction, 64 aircraft total) sit far above the
                     # AI density the mission files actually set, so they never bite. AILimit.Enforce +
                     # StuckSeconds + StuckRadiusMetres stay — that tick is the stuck-AI runway cleaner.
                     "AILimit.PerTeamAICap", "AILimit.TotalAircraftCap"}
# Item 10: Damage Calibration is a teamkill-floor diagnostic, not a chat/feed toggle — regroup it.
_CATALOGUE_DESC_OVERRIDE = {
    # EMPTY ON PURPOSE (1.3.10). This map existed to correct catalogue text that had gone stale;
    # by 1.3.9 the relationship had inverted: the shipped catalogue was the CURRENT copy and
    # these strings were the stale ones, so every catalogue rewrite silently did nothing while
    # an entry lived here.
    # Fix settings_catalogue.json instead; only add an entry here for something the shipped
    # catalogue genuinely cannot express, and delete it the moment that stops being true.
}
_CATALOGUE_GROUP_OVERRIDE = {
    "Teamkill.DamageCalibration": "Moderation",
    "Stats.DamageCalibration": "Moderation",  # legacy key alias if an old catalogue still ships it
    # (1.2.0: the five "Rank + Fund catch-up" overrides are gone. Mission.PvpStartingRank +
    #  Scoring.RankFunds* now carry that group IN settings_catalogue.json, and the two
    #  Mission.PvpRankCatchup* rows live only in _CATALOGUE_EXTRA below — which is appended
    #  verbatim and never passed through this map — so the overrides did nothing for them.)
    # Anti-Grief consolidate: merge former Flood Guard rows into the single Anti-Grief tab
    # (belt-and-suspenders if a stale catalogue still says "Flood Guard").
    "Flood.LogDrops": "Anti-Grief",
    "Flood.DropDeadNetIdRpcs": "Anti-Grief",
    "Command.Policy": "Anti-Grief",
    "Command.AllowedJsonKeys": "Anti-Grief",
    "Command.DiagLog": "Anti-Grief",
    "Mirage.RaiseReliableSendBuffer": "Anti-Grief",
    "Mirage.ReliableSendBufferLimit": "Anti-Grief",
}
# Whole-GROUP merges (applied by group name, so any row in the group moves — present or future).
# Owner request (1.2.0 settings pass): the PvE tab held a single row (PvE.TimeoutForceDefeat) and read
# as an orphan in the category rail. It is a "mission timer ran out -> what happens" switch, which is
# exactly what the Match tab already covers (PvP.TimeoutResult, Match.TimeoutLeadSeconds, the
# Annihilate/forced-defeat rows), so the two belong on one tab.
_CATALOGUE_GROUP_MERGE = {"PvE": "Match"}
# Knobs the shipped catalogue may not carry yet (vote timing, force_pvp_*, rank funds/catch-up, award
# toggles). Injected only when absent (keyed by "key"), so a catalogue that already defines them wins.
# Shapes match the catalogue rows (key/owner/type/…).
_CATALOGUE_EXTRA = [
    # FIX 3 — the TWO coherent vote-timing knobs that replace VOTE_DURATION/APPROVAL_DURATION/PostMissionDelay.
    # owner="bot" so they ride the bot's set-cfg branch, which intercepts them (set_vote_timing): persists to
    # the deploy-protected .nost-data/votemap_timing.json AND re-derives + pushes PostMissionDelay = vote+delay
    # in one op. Defaults are 30/15 (NOT 60) so a fresh/missing config surfaces 30/15, never the old 60.
    {"key": "MAP_VOTE_DURATION", "friendlyName": "Map vote length (s)", "group": "End of Match & Votes",
     "owner": "bot", "type": "int", "default": "30", "live": "live", "min": 10, "max": 300, "commonlyChanged": True,
     "adminDescription": "How long the map-vote ballot stays open — used for BOTH the end-of-match vote and the "
                         "player !votemap ballot. The map then changes (vote length + post-vote delay) seconds "
                         "after the mission ends; the server's PostMissionDelay is derived from these two, so it "
                         "can never be shorter than the vote."},
    {"key": "POST_VOTE_MAP_CHANGE_DELAY", "friendlyName": "Delay after vote before map change (s)",
     "group": "End of Match & Votes", "owner": "bot", "type": "int", "default": "15", "live": "live",
     "min": 5, "max": 300, "commonlyChanged": True,
     "adminDescription": "Seconds AFTER the ballot closes before the winning map loads. The effective "
                         "post-mission delay is DERIVED = vote length + this (e.g. 30 + 15 = 45s) and pushed to "
                         "the server automatically — you never set a raw post-mission delay, so the old broken "
                         "combination (delay shorter than the vote) is impossible."},
    # Force-PvP-at-high-pop — edited in Game Settings (Map Pool tip points here). owner=votemap so
    # /api/settings GET reads votemap_config.json and POST routes set_cfg_dispatch → set_votemap_cfg
    # (same writer as /api/votemap). Keys must stay in _VOTEMAP_KEYS / bot _VOTEMAP_DEFAULTS.
    {"key": "force_pvp_enabled", "friendlyName": "Force PvP at high population",
     "group": "End of Match & Votes", "owner": "votemap", "type": "toggle", "default": "true",
     "live": "live", "commonlyChanged": True,
     "adminDescription": "When enough players are online, the end-of-match ballot becomes PvP-heavy "
                         "(uses the three settings below). Applies from the next ballot."},
    {"key": "force_pvp_players", "friendlyName": "Force PvP: player count",
     "group": "End of Match & Votes", "owner": "votemap", "type": "int", "default": "24",
     "min": 1, "max": 200, "live": "live", "commonlyChanged": True,
     "adminDescription": "Force the PvP-heavy ballot once at least this many players are online."},
    {"key": "force_pvp_coop", "friendlyName": "Force PvP: PvE maps on ballot",
     "group": "End of Match & Votes", "owner": "votemap", "type": "int", "default": "0",
     "min": 0, "max": 12, "live": "live", "commonlyChanged": False,
     "adminDescription": "How many PvE/co-op maps stay on the ballot while PvP is being forced "
                         "(0 = PvP-only ballot)."},
    {"key": "force_pvp_pvp", "friendlyName": "Force PvP: PvP modes on ballot",
     "group": "End of Match & Votes", "owner": "votemap", "type": "int", "default": "6",
     "min": 0, "max": 6, "live": "live", "commonlyChanged": False,
     "adminDescription": "How many PvP modes go on the ballot while PvP is being forced "
                         "(capped by how many built-in PvP modes are enabled in the Mission Pool)."},
    # (1.2.0: Scoring.RankFundsPerRank / Scoring.RankFundsMode moved OUT of here and INTO
    #  settings_catalogue.json — the file is what the installer counts and what anyone reading the
    #  repo treats as the inventory, and a code-only copy made the file lie about the panel.)
    # 11 — rank CATCH-UP (plugin). Injected only if the shipped catalogue lacks them.
    {"key": "Mission.PvpRankCatchupMinutes", "friendlyName": "Rank Catch-up: Minutes per +1", "group": "Rank + Fund catch-up",
     "owner": "plugin", "type": "int", "default": "0", "live": "live", "min": 0, "commonlyChanged": False,
     "adminDescription": "Raise the PvP start-rank floor by +1 every N minutes of match time so latecomers are not "
                         "stuck at the bottom. 0 = off.", "gameplay": True},
    {"key": "Mission.PvpRankCatchupMaxRank", "friendlyName": "Rank Catch-up: Max Rank", "group": "Rank + Fund catch-up",
     "owner": "plugin", "type": "int", "default": "5", "live": "live", "min": 0, "max": 5, "commonlyChanged": False,
     "adminDescription": "The rising catch-up floor stops at this in-game rank. Capped at 5, which is the "
                         "game's highest rank - it shipped defaulting to 6, a rank nothing can ever reach, so "
                         "the floor never registered as \"arrived\" and the catch-up tick re-raised every player "
                         "every 15s for the whole match.", "gameplay": True},
    # 12 — VANILLA-ABLE PVP award toggle (bot-owned). Defaults ON; turning it off never stops rank
    #      DISPLAY or cross-server carry. It does NOT ride bot_overrides.json / _BOT_OVERRIDE_KEYS: POST
    #      routes it to the bot's `awardtoggle` action (_AWARD_TOGGLE_MAP below) and set_award_toggle()
    #      mutates the RUNNING bot's _award_cfg + saves award_config.json, so it is "live": "live" —
    #      flagging it "restart" told the operator to bounce the bot for nothing (1.2.0 fix).
    {"key": "Award.WIN_POINTS_ON", "friendlyName": "Award: Win / Placement Points", "group": "Scoring & Ranks",
     "owner": "bot", "type": "toggle", "default": "1", "live": "live", "commonlyChanged": False,
     "adminDescription": "Master on/off for win + placement points. Off = vanilla: Win Points and the 1st/2nd/3rd "
                         "bonuses are emitted and discarded; ranks still show + carry.",
     "gameplay": True},
]


def _load_catalogue():
    """The shipped settings catalogue (friendly names / groups / types / defaults / ranges) AFTER the
    code-side transforms: item 2 removes the radar/net-coalesce limiter rows, item 10 regroups Damage
    Calibration, the group-merge map folds one-row orphan tabs (PvE -> Match) into the tab they belong
    on, and items 11/12 inject the rank-funds/catch-up + vanilla award toggles when the shipped
    catalogue does not already define them."""
    try:
        with open(SETTINGS_CATALOGUE, encoding="utf-8") as f:
            d = json.load(f)
        base = d.get("settings", []) if isinstance(d, dict) else (d if isinstance(d, list) else [])
    except (OSError, ValueError):
        base = []
    out, seen = [], set()
    for s in base:
        if not isinstance(s, dict):
            continue
        key = s.get("key", "")
        if key in _CATALOGUE_REMOVE:                     # item 2: drop the vetoed limiter knobs
            continue
        if key in _CATALOGUE_GROUP_OVERRIDE:             # item 10: move to a sensible group
            s = dict(s)
            s["group"] = _CATALOGUE_GROUP_OVERRIDE[key]
        if s.get("group") in _CATALOGUE_GROUP_MERGE:     # fold one-row orphan tabs into their real tab
            s = dict(s)
            s["group"] = _CATALOGUE_GROUP_MERGE[s["group"]]
        if key in _CATALOGUE_DESC_OVERRIDE:              # correct stale shipped-catalogue text
            s = dict(s)
            s["adminDescription"] = _CATALOGUE_DESC_OVERRIDE[key]
        out.append(s)
        seen.add(key)
    for s in _CATALOGUE_EXTRA:                            # items 11/12: add only what the catalogue lacks
        if s.get("key") not in seen:
            out.append(dict(s))
            seen.add(s.get("key"))
    return out


# ── FIX 3 [AWARD TOGGLES]: bot-owned vanilla award switches ────────────────────────────────────
# The Award.*_ON rows are injected into the settings catalogue as owner="bot", but the bot REJECTS them
# through setcfg (unknown bot setting). The bot instead owns them in award_config.json and exposes a
# dedicated `awardtoggle` admin action (bot funcs set_award_toggle / award_toggles_state). Map each
# catalogue key -> that file's short key so POST routes to awardtoggle and GET reflects the live on/off.
_AWARD_TOGGLE_MAP = {
    "Award.WIN_POINTS_ON":    "win_points",
}


def _award_state():
    """Current award-toggle on/off keyed by award_config.json's short keys. Read FRESH from disk each call:
    the BOT process (not cc_web) writes award_config.json, so cc_web's import-time copy goes stale. Fail-open
    to {} (the GET loop then leaves the catalogue default) on any error."""
    try:
        bot.load_award_cfg()                             # refresh cc_web's in-process copy from disk
        rows = (bot.award_toggles_state() or {}).get("awards", [])
        return {r.get("key"): bool(r.get("on")) for r in rows if r.get("key")}
    except Exception:                                    # noqa: BLE001
        return {}


# ── optimistic "pending" overlay so a just-saved setting is NOT reverted to a stale value ──────
# Item 9 (SETTINGS PERSISTENCE): a plugin setcfg only APPLIES / re-dumps when a player is online, so the
# next /api/settings poll would otherwise read back the catalogue default and the panel would "revert".
# We remember what was queued and overlay it until the live value confirms it (or a TTL elapses).
# This is purely webcc-side.
_pending_settings = {}                                             # key -> {"val": <str>, "ts": float}
_pending_lock = threading.Lock()
_PENDING_TTL = 180.0                                               # hold the optimistic value up to 3 min


def _pending_set(key, sval):
    with _pending_lock:
        _pending_settings[key] = {"val": str(sval), "ts": time.time()}


def _pending_get(key):
    with _pending_lock:
        p = _pending_settings.get(key)
        if not p:
            return None
        if time.time() - p["ts"] > _PENDING_TTL:
            _pending_settings.pop(key, None)
            return None
        return p["val"]


def _pending_clear(key):
    with _pending_lock:
        _pending_settings.pop(key, None)


def _norm_setting_val(v):
    """Canonical form for comparing a typed LIVE value against a queued STRING (bool/1/0/int/float)."""
    if isinstance(v, bool):
        return "true" if v else "false"
    s = str(v).strip().lower()
    if s in ("1", "true", "on", "yes"):
        return "true"
    if s in ("0", "false", "off", "no"):
        return "false"
    try:
        f = float(s)
        return str(int(f)) if f.is_integer() else str(f)
    except ValueError:
        return s


def _coerce_pending(typ, sval):
    """Present a queued string in the display type the frontend expects for this setting."""
    if typ == "toggle":
        return _norm_setting_val(sval) == "true"
    if typ in ("int", "float"):
        try:
            f = float(sval)
            return int(f) if typ == "int" else f
        except (TypeError, ValueError):
            return sval
    return sval


# ── PvP classifier (contract [PVP LABEL]: one shared is_pvp) + frametime extraction (contract [FRAMETIME]) ──
def _is_pvp(name):
    """True for a PvP mission (bare PvP base name or a weather/time variant of one). Co-op variants that
    merely start with a PvP base name (e.g. 'Escalation Co-op as BDF - Dawn') are NOT PvP."""
    n = (name or "").strip().lower()
    if not n or "co-op" in n or "coop" in n:
        return False
    bases = {m.strip().lower() for m in getattr(bot, "PVP_MISSIONS", [])}
    return n in bases or n.split(" - ")[0].strip() in bases


def _extract_frametime(st):
    """Pull the plugin's smoothed server frame time (ms) from wherever the bot snapshot carries it.
    Returns {"ms": float, "ts": ...} or None so the panel can hide/placeholder when there's no data."""
    net = st.get("net") if isinstance(st.get("net"), dict) else {}
    srv = net.get("srv") if isinstance(net.get("srv"), dict) else {}
    ts = net.get("ts") or st.get("ts")
    for c in (st.get("frametime_ms"), net.get("frametime_ms"), net.get("frame_ms"),
              net.get("frame"), srv.get("frame"), srv.get("frametime_ms")):
        try:
            if c is None:
                continue
            v = float(c)
            if math.isfinite(v) and v > 0:
                return {"ms": round(v, 1), "ts": ts}
        except (TypeError, ValueError):
            continue
    return None


def _frametime_ms(st):
    """FIX 1 [FRAMETIME]: the webcc's _frametimeMs() reads the TOP-LEVEL contract field st.frametime_ms as
    a plain NUMBER (not the {ms,ts} object). Prefer dashboard_state's own top-level frametime_ms (the bot
    now writes it); fall back to the plugin net line's frametime_ms. Returns a float (rounded) or None so
    the panel shows its '—' placeholder when there is no reading. Call BEFORE st.pop('net')."""
    net = st.get("net") if isinstance(st.get("net"), dict) else {}
    for c in (st.get("frametime_ms"), net.get("frametime_ms")):
        try:
            if c is None:
                continue
            v = float(c)
            if math.isfinite(v) and v > 0:
                return round(v, 1)
        except (TypeError, ValueError):
            continue
    return None


def _deploy_status():
    """Describe the plugin update (if any) STAGED for the next deploy, so the web CC can show
    'update good to go' at a glance. Reads pending_plugin.dll (+ its .json sidecar) and compares
    its sha to deployed_plugin.sha256. `new` => something genuinely different from what's live."""
    out = {"staged": False}
    try:                                                  # the LIVE deployed version (recorded at deploy time)
        with open(DEPLOYED_META, encoding="utf-8") as f:
            dm = json.load(f)
        out["deployed_version"] = dm.get("version")
        out["deployed_at"] = dm.get("deployed_at")
    except Exception:                                     # noqa: BLE001 - not recorded yet / no deploy
        pass
    try:
        if not os.path.exists(PENDING_DLL):
            return out                                    # no pending update -> still reports deployed_version
        import hashlib
        h = hashlib.sha256()
        with open(PENDING_DLL, "rb") as f:
            for chunk in iter(lambda: f.read(65536), b""):
                h.update(chunk)
        sha = h.hexdigest()
        out.update({"staged": True, "size": os.path.getsize(PENDING_DLL), "sha": sha[:12]})
        try:                                              # optional human-readable metadata
            # utf-8-sig: a sidecar staged from PowerShell 5.1 (Set-Content/Out-File) carries a BOM
            # that plain utf-8 json.load rejects — the card then silently lost version/note/staged_at
            # (the '1.4.0 blanked header' bug; the bot's reader was fixed in a341e4e, this one wasn't).
            # Reads BOM-less python-written sidecars identically. (fix 2026-08-15)
            with open(PENDING_META, encoding="utf-8-sig") as f:
                meta = json.load(f)
            out["version"] = meta.get("version")
            out["note"] = meta.get("note")
            out["staged_at"] = meta.get("staged_at")
            out["meta_ok"] = (str(meta.get("sha256", ""))[:12] == sha[:12])   # sidecar matches the real DLL?
        except Exception:                                 # noqa: BLE001
            out["meta_ok"] = None
        deployed = ""
        try:
            with open(DEPLOYED_SHA, encoding="utf-8") as f:
                deployed = f.read().strip()
        except Exception:                                 # noqa: BLE001
            pass
        out["deployed_sha"] = deployed[:12]
        out["new"] = (not deployed) or deployed[:12] != sha[:12]   # differs from live -> a real update
    except Exception as e:                                # noqa: BLE001
        out["error"] = str(e)
    return out

app = Flask(__name__, static_folder=None)


class _QueueWriteError(Exception):
    """A queue/sidecar write failed (disk full, AV/OneDrive lock, permissions after a restore).
    Raised by the file-writing helpers (_queue_admin/_write_schedule/_save_console_filters); the
    handler below turns it into the standard {"ok": False} JSON, so the many routes that queue work
    don't each need a try/except and jpost() never has to parse an HTML 500. Ledger writes must be
    OSError-guarded — an unguarded open() here silently lost the operator's action. (fix 2026-08-15)"""


@app.errorhandler(_QueueWriteError)
def _queue_write_failed(e):
    return jsonify({"ok": False, "error": str(e)})


@app.before_request
def _nocc_auth_gate():
    """Same-origin enforcement for every mutating request, plus optional shared-secret auth.

    CSRF (round-2 audit 2026-08-01): the token is OFF by default, and every POST route parses its body
    with get_json(force=True), which ignores Content-Type. A plain cross-site HTML form therefore needs
    no preflight and no CORS grant — any page the operator had open in another tab could drive the
    whole command centre: change settings, ban players, switch the map. The origin check below runs
    UNCONDITIONALLY, not only when a token is configured, because the default install is the one that
    needs protecting.

    A same-origin fetch from the panel sends Origin (or at least Referer). A request carrying neither
    is not a browser form post, so it is allowed — that keeps curl and the installer's own scripted
    calls working, which cannot be CSRF vectors."""
    if request.method in ("GET", "HEAD", "OPTIONS"):
        return None

    from urllib.parse import urlsplit

    def _host_of(v):
        try:
            return (urlsplit(v).netloc or "").lower()
        except Exception:                                # noqa: BLE001
            return ""

    # Hosts this panel may legitimately be reached as, derived from OUR OWN CONFIG.
    #
    # Deliberately NOT seeded from request.host or X-Forwarded-Host: both are supplied by the client.
    # Seeding the allowlist from them made the check self-approving - an attacker who controls a
    # hostname that resolves to this machine (classic DNS rebinding) simply sends a matching Host and
    # Origin, and the gate compares the attacker's value against itself and passes. The allowlist has
    # to come from something the attacker cannot set. (round-4 audit 2026-08-01)
    #
    # A reverse proxy is still supported - the operator lists its public name in web.allowed_origins,
    # which is a deliberate act rather than something a request can assert.
    allowed = set(_STATIC_ALLOWED_HOSTS)
    for extra in ALLOWED_ORIGINS:
        h = _host_of(extra) or extra.strip().lower()
        if h:
            allowed.add(h)
    allowed.discard("")

    origin = request.headers.get("Origin")
    if origin is not None:
        # FAIL CLOSED on a present Origin. The previous version only blocked when the value PARSED to a
        # non-empty host, so the literal "Origin: null" - which browsers send for a sandboxed iframe, a
        # form with referrerpolicy=no-referrer, or an https->http downgrade - parsed to "" and sailed
        # straight through. That is the ordinary cross-site case, so the gate was a no-op exactly where
        # it mattered. A present Origin we cannot match is now refused. (round-3 audit 2026-08-01)
        oh = _host_of(origin)
        if not oh or oh not in allowed:
            print(f"[web] BLOCKED cross-origin {request.method} {request.path} "
                  f"origin={origin!r} (allowed: {sorted(allowed)})")
            return jsonify({"ok": False, "error": "cross-origin request refused"}), 403
    else:
        # No Origin at all. Browsers always send it on a cross-site POST, so this is a scripted caller
        # (curl, the installer, a watchdog). Check Referer if one is present, otherwise allow.
        ref = request.headers.get("Referer")
        if ref:
            rh = _host_of(ref)
            if rh and rh not in allowed:
                print(f"[web] BLOCKED cross-origin {request.method} {request.path} referer={ref!r}")
                return jsonify({"ok": False, "error": "cross-origin request refused"}), 403

    if not AUTH_TOKEN:
        return None
    got = (request.headers.get("X-NOCC-Token") or "").strip()
    if got != AUTH_TOKEN:
        return jsonify({"ok": False, "error": "unauthorized"}), 401
    return None


@app.route("/api/admin_results")
def api_admin_results():
    """Poll recent bot admin_results.jsonl acks (setcfg feedback for the settings UI)."""
    since = 0.0
    try:
        since = float(request.args.get("since") or 0)
    except (TypeError, ValueError):
        since = 0.0
    rows = []
    try:
        with open(ADMIN_RESULTS, encoding="utf-8", errors="replace") as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    rec = json.loads(line)
                except ValueError:
                    continue
                if not isinstance(rec, dict):
                    continue
                ts = rec.get("ts") or 0
                try:
                    ts = float(ts)
                except (TypeError, ValueError):
                    continue
                if ts > since:
                    rows.append(rec)
    except OSError:
        pass
    return jsonify({"ok": True, "results": rows[-50:]})


# ── game remote-command relay (reuse the bot's client; serialise access) ──────
_rc = bot.RemoteCommand(bot.RCMD_HOST, bot.RCMD_PORT)
_rc_lock = threading.Lock()
_STATUS = getattr(bot, "STATUS_CODES", {})


def _send_cmd(name, args):
    with _rc_lock:
        return _rc.send(name, *args)


def _tail(path, n):
    """Last n non-empty lines. Reads only the file's last 256KB — activity.log is never trimmed and
    console_mirror.log can be 2MB, and this runs on EVERY ~1s /api/state poll per open tab."""
    try:
        window = 262144
        with open(path, "rb") as f:
            f.seek(0, 2)
            size = f.tell()
            f.seek(max(0, size - window))
            data = f.read().decode("utf-8", errors="replace")
        lines = data.splitlines()
        if size > window and lines:
            lines = lines[1:]                            # drop the first line (likely cut mid-way by the seek)
        return [ln for ln in lines if ln.strip()][-n:]
    except Exception:                                    # noqa: BLE001
        return []


# ── user console filters ("filter messages like this") ───────────────────────
CONSOLE_FILTERS = os.path.join(HERE, "console_filters.json")   # user-added patterns (normalised)


def _norm_console(s):
    """Normalise a console line so 'messages like this' match despite varying numbers:
    drop digit runs (timestamps, netIds, counts) and lowercase."""
    return re.sub(r"\d+", "#", str(s)).strip().lower()


def _load_console_filters():
    try:
        with open(CONSOLE_FILTERS, encoding="utf-8") as f:
            d = json.load(f)
        return [p for p in d if p] if isinstance(d, list) else []
    except (OSError, ValueError):
        return []


def _save_console_filters(lst):
    tmp = CONSOLE_FILTERS + ".tmp"
    try:
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(lst, f, indent=1)
        os.replace(tmp, CONSOLE_FILTERS)
    except OSError as e:
        raise _QueueWriteError(f"filter write failed: {e}") from e


# ── console noise filter (ported from the TUI) ────────────────────────────────
_ERR_TOKENS = ("Exception", "NullReference", "Traceback", "stack trace")
_ERR_LOW = ("error", "failed", "fatal", " denied", "could not patch")
NOISE_LABELS = {"remote": "remote-cmd", "weapon": "weapon-mgr", "ai": "AI-units",
                "nostats": "NOSTATS", "blast": "blast",
                "engine": "engine-warn", "steam": "Steam-net"}   # kinematic-vel lines classify as "engine"
_ENGINE_NOISE = ("linear velocity of a kinematic", "boxcollider does not support negative",
                 "the effective box size has been forced", "if you absolutely need to use negative s",
                 "did you use #pragma only_renderers", "if subshaders removal was intentional",
                 "fallback handler could not load library", "particle system is trying to spawn")


def _is_err(line):
    low = line.lower()
    return any(k in line for k in _ERR_TOKENS) or any(k in low for k in _ERR_LOW)


def _classify(line):
    low = line.lower()
    err = _is_err(line)
    if "[serverremotecommands]" in low:
        return "error" if (err or ("response:" in low and "success" not in low)) else "remote"
    if "[weaponmanager]" in low:
        return "error" if err else "weapon"
    if "[aihelo]" in low or "[aiplane]" in low or "[aiground]" in low or "aipilot" in low:
        return "error" if err else "ai"
    if "[nostats]" in low:
        return "error" if err else "nostats"
    if "[blastmanager]" in low or "blast manager" in low:
        return "error" if err else "blast"
    if "[steammanager]" in low:
        return "error" if (err or "unable to communicate with any" in low or "no route" in low) else "steam"
    if any(p in low for p in _ENGINE_NOISE):
        return "engine"
    return "error" if err else "show"


def _console_view(lines, raw):
    if raw:
        return [{"t": ln, "k": "err" if _classify(ln) == "error" else "show"} for ln in lines]
    user = _load_console_filters()
    out, supp, ucount = [], {}, 0
    for ln in lines:
        if user:
            nl = _norm_console(ln)
            if any(p in nl for p in user):       # user "filter messages like this"
                ucount += 1
                continue
        c = _classify(ln)
        if c in ("show", "error"):
            out.append({"t": ln, "k": "err" if c == "error" else "show"})
        else:
            supp[c] = supp.get(c, 0) + 1
    if supp or ucount:
        parts = [f"{supp[k]} {NOISE_LABELS[k]}" for k in NOISE_LABELS if supp.get(k)]
        if ucount:
            parts.append(f"{ucount} custom")
        out.append({"t": f"— filtered  {'  ·  '.join(parts)} —", "k": "sum"})
    return out


# ── command catalog (server aliases + bot/local) for the palette + autocomplete ─
_LOCAL_CMDS = [
    ("say",         "<message>",            "broadcast an [Admin] message to chat",       False, "message"),
    ("nextmap",     "<mission>",            "queue the next mission",                     False, "mission"),
    ("changemap",   "<mission>",            "END the current match + switch to a chosen map NOW", False, "mission"),
    ("endmission",  "",                     "end the match via a ~30s map vote, then cut over", True,  ""),
    ("leaderboard", "",                     "top pilots by points",                       False, ""),
    ("ranks",       "",                     "all saved ranks, best first",                False, ""),
    ("rankpreview", "",                     "post the rank ladder into in-game chat",     False, ""),
    ("aircraftlist", "",                    "dump the live aircraft catalogue to the plugin log", False, ""),
    ("grant",       "<player> <points>",    "add / remove rank points (use -N to remove)", False, "pn"),
    ("move",        "<player> <faction>",   "move a player to a team",                    False, "pf"),
    ("join",        "<player> <faction>",   "join a player to a team",                    False, "pf"),
    ("spec",        "<player>",             "move a player to spectator",                 False, "player"),
    ("setrank",     "<player> <rank>",      "set a player's IN-GAME rank (number)",       False, "pn"),
    ("setfunds",    "<player> <amount>",    "set a player's IN-GAME funds",               False, "pn"),
    ("addfunds",    "<player> <amount>",    "add/remove IN-GAME funds (use -N to remove)", False, "pn"),
    ("balance",     "",                     "run a PvP team-balance pass",                False, ""),
    ("swapteam",    "<player>",             "move a player to the OTHER team (brief Cricket + eject)",  False, "player"),
    ("forceteamswap","<player>",            "force a player to the other team (even when balanced)",    True,  "player"),
]


_HIDDEN_VERBS = {"updateready", "update-ready", "banreload", "banlist-reload", "banclear",
                 "banlist-clear", "clearkicks", "clear-kicked-players"}   # raw ops verbs: hidden from the palette AND rejected by /api/cmd


def _catalog():
    out = []
    for alias, wire, args, desc, danger in getattr(bot, "CENTRE_SERVER_CMDS", []):
        if wire == "send-chat-message":   # drop the raw server 'say' - the local 'say' below
            continue                      # covers it (adds the [Admin] prefix + mirrors to activity)
        if alias in _HIDDEN_VERBS or wire in _HIDDEN_VERBS:   # public ship: don't surface raw operational verbs
            continue
        ac = ("message" if wire == "send-chat-message" else
              "steamid" if wire in ("kick-player", "unkick-player", "banlist-add", "banlist-remove") else "")
        out.append({"name": alias, "wire": wire, "args": args, "desc": desc,
                    "danger": danger, "ac": ac, "group": "server"})
    for name, args, desc, danger, ac in _LOCAL_CMDS:
        out.append({"name": name, "wire": name, "args": args, "desc": desc,
                    "danger": danger, "ac": ac, "group": "bot"})
    return out


def _missions():
    base = (list(getattr(bot, "PVP_MISSIONS", [])) + list(getattr(bot, "BUILTIN_COOP_MISSIONS", []))
            + list(getattr(bot, "ESCALATION_MISSIONS", []))
            + list(getattr(bot, "TERMINAL_CONTROL_MISSIONS", [])))
    # + the bot's live votable universe (enabled custom/uploaded USER missions) from the dashboard,
    # so the Change-map picker and nextmap autocomplete can reach missions the static lists can't know
    try:
        with open(DASHBOARD, encoding="utf-8") as f:
            votable = (json.load(f).get("votemap") or {}).get("votable") or []
        for v in votable:
            n = v.get("name") if isinstance(v, dict) else None
            if n and n not in base:
                base.append(n)
    except Exception:                                    # noqa: BLE001
        pass
    return base


def _resolve_mission(q):
    q = (q or "").strip().lower()
    if not q:
        return None
    ms = _missions()
    for m in ms:
        if m.lower() == q:
            return m
    for m in ms:
        if m.lower().startswith(q):
            return m
    for m in ms:
        if q in m.lower():
            return m
    return None


def _players():
    try:
        with open(DASHBOARD, encoding="utf-8") as f:
            return json.load(f).get("players", [])
    except Exception:                                    # noqa: BLE001
        return []


def _resolve_player(query):
    """name/partial/sid -> sid, using the live roster. Returns (sid, label) or (None, msg)."""
    q = (query or "").strip()
    if not q:
        return None, "no player given"
    ps = _players()
    if q.isdigit():
        for p in ps:
            if str(p.get("sid")) == q:
                return q, p.get("name", q)
        return q, q                                      # trust a raw SteamID
    ql = q.lower()
    hits = [p for p in ps if ql in (p.get("name", "").lower())]
    exact = [p for p in ps if p.get("name", "").lower() == ql]
    if exact:
        hits = exact
    if not hits:
        return None, f"no online player matches '{q}'"
    if len(hits) > 1:
        return None, f"'{q}' matches {len(hits)} players - be more specific"
    return str(hits[0].get("sid")), hits[0].get("name", q)


def _queue_admin(rec):
    rec["ts"] = time.time()
    try:
        with open(bot.ADMIN_CMD_FILE, "a", encoding="utf-8") as f:
            f.write(json.dumps(rec) + "\n")
    except OSError as e:
        raise _QueueWriteError(f"queue write failed: {e}") from e


def _read_schedule():
    try:
        with open(SCHEDULE_FILE, encoding="utf-8") as f:
            d = json.load(f)
        return d if isinstance(d, list) else []
    except (OSError, json.JSONDecodeError):
        return []


def _write_schedule(items):
    tmp = SCHEDULE_FILE + ".tmp"
    try:
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(items, f, indent=2)
        os.replace(tmp, SCHEDULE_FILE)
    except OSError as e:
        raise _QueueWriteError(f"schedule write failed: {e}") from e


def _faction_norm(f):
    f = (f or "").lower()
    if f in ("boscali", "bdf", "bosc", "blue"):
        return "boscali"
    if f in ("primeva", "pala", "prim", "red"):
        return "primeva"
    return None


# ── ranks / leaderboard (read-only from ranks.json) ───────────────────────────
_RANKS_CACHE = {"mtime": -1.0, "size": -1, "data": {}}


def _read_ranks():
    """ranks.json, mtime+size gated so repeated calls inside one request are a stat(), not a parse.

    This used to open and json.load() the WHOLE file on every call. That was tolerable while callers
    read it once per request, but _cycle_for_tier now needs a player's points per ROW, so a single
    /api/ranks on this fleet (~1858 ranked players) performed ~1859 full parses of a multi-megabyte
    file - seconds of CPU and hundreds of MB of reads for one page load, on the same process that
    serves the live dashboard. (round-3 audit 2026-08-01)

    Gated on (mtime, size) rather than mtime alone: the bot writes via tmp + os.replace, and two writes
    inside one filesystem timestamp tick are possible on Windows."""
    try:
        st = os.stat(RANK_FILE)
        if st.st_mtime == _RANKS_CACHE["mtime"] and st.st_size == _RANKS_CACHE["size"]:
            return _RANKS_CACHE["data"]
    except OSError:
        return _RANKS_CACHE["data"] or {}
    try:
        with open(RANK_FILE, encoding="utf-8") as f:
            d = json.load(f)
    except Exception:                                    # noqa: BLE001 - keep the last good copy
        return _RANKS_CACHE["data"] or {}
    if isinstance(d, dict):
        _RANKS_CACHE.update({"mtime": st.st_mtime, "size": st.st_size, "data": d})
        return d
    return _RANKS_CACHE["data"] or {}


def _pts_i(n):
    """Whole-number points for WebCC rank/score columns (no float artifacts)."""
    try:
        return int(bot._pts_i(n))
    except Exception:                                    # noqa: BLE001
        try:
            return int(round(float(n)))
        except (TypeError, ValueError):
            return 0


_PRESTIGE_MTIME = [0.0]


def _refresh_prestige():
    """Keep the bot module's PRESTIGE_DATA fresh IN THIS PROCESS.

    bot.PRESTIGE_DATA is filled by a module-level load_prestige() when cc_web imports the bot, and after
    that only do_prestige() — which runs in the BOT process — ever mutates it. So a prestige banked in game
    never reached the panel: prestige_base stayed 0, _cycle_for_tier returned the player's full lifetime
    total, and the Ranks table / leaderboard / "post top 5 to chat" all rendered them at the TOP tier with
    no star, contradicting their own !rank and their in-game tag. It never self-corrected until cc_web was
    restarted. ranks.json is already re-read per request (_read_ranks); this does the same for prestige.json,
    gated on mtime so it is a stat() per call, not a parse.

    A FAILED reload must never replace good data, and must never mark itself done (audit 2026-08-01).
    The bot writes prestige.json as tmp + os.replace, so a read landing in that window can see a missing
    or partial file. load_prestige() swallows that internally and assigns PRESTIGE_DATA = {} — and since
    the mtime marker was previously advanced BEFORE the load, the empty dict then stuck until the file
    happened to change again. Every prestiged player rendered at the wrong tier in the meantime, which is
    worse than the staleness this function exists to fix. So: advance the marker only after a load that
    actually produced data, and restore the previous dict if it did not."""
    try:
        m = os.path.getmtime(bot.PRESTIGE_FILE)
    except OSError:
        return                                           # mid-replace or absent: keep what we have
    if m <= _PRESTIGE_MTIME[0]:
        return
    prev = getattr(bot, "PRESTIGE_DATA", None)
    try:
        bot.load_prestige()
    except Exception:                                    # noqa: BLE001 - stale beats crashing the panel
        return                                           # marker NOT advanced -> retried next call
    now = getattr(bot, "PRESTIGE_DATA", None)
    if not now and prev:
        bot.PRESTIGE_DATA = prev                         # empty result on a non-empty file = a bad read
        return                                           # marker NOT advanced -> retried next call
    _PRESTIGE_MTIME[0] = m


_LADDER_MTIME = [0.0]


def _refresh_ladder():
    """Keep bot.RANKS (the rank ladder) live IN THIS PROCESS.

    load_rank_ladder() runs once when cc_web imports the bot, and only rank_ladder_apply() rebuilds
    RANKS - which runs exclusively in the BOT process off the admin queue. So after an operator edits
    the ladder, the bot rebuilds its copy and re-pushes every player's in-game tag, while cc_web keeps
    computing tiers from the ladder as it was at startup: the Ranks table, the leaderboard and the
    cross-server board all show the OLD tier names, colours and thresholds, and 'post top pilots to
    chat' broadcasts those stale tags into live game chat where they contradict the tag baked into the
    very same players' names. Nothing self-corrects until cc_web is restarted.

    Same fail-safe shape as _refresh_prestige: the marker only advances after a reload that actually
    produced a ladder, so a read landing inside the bot's tmp+os.replace window cannot install the
    built-in default over the operator's ladder. (round-5 audit 2026-08-01)"""
    try:
        m = os.path.getmtime(bot.RANK_LADDER_FILE)
    except OSError:
        return
    if m <= _LADDER_MTIME[0]:
        return
    prev = list(getattr(bot, "RANKS", []) or [])
    try:
        bot.load_rank_ladder()
    except Exception:                                    # noqa: BLE001 - stale beats crashing the panel
        return
    now = list(getattr(bot, "RANKS", []) or [])
    if not now and prev:
        bot.RANKS = prev
        return
    _LADDER_MTIME[0] = m


def _rank_tier(pts, sid=""):
    """Return (abbr_label, color) for display. Prestige >=1 → 'OFFCDT - 1*' plain
    (WebCC shows without outer brackets; in-game plugin wraps [ ]). Never a *P name suffix."""
    _refresh_prestige()
    _refresh_ladder()                                    # see _refresh_ladder: tiers go stale otherwise
    RANKS = getattr(bot, "RANKS", [])
    try:
        _, name, abbr, color = RANKS[bot.rank_index_for(pts)]
        pn = 0
        if sid:
            try:
                pn = int(bot.prestige_count(sid) or 0)
            except Exception:                            # noqa: BLE001
                pn = 0
        try:
            label = bot.prestige_label(abbr, name, pn)
        except Exception:                                # noqa: BLE001
            label = f"{abbr} - {pn}*" if pn > 0 else abbr
        return label, color
    except Exception:                                    # noqa: BLE001
        return "", "#aaa"


_SHARED_CFG_AT = [0.0]


def _refresh_shared_cfg():
    """Keep bot.SHARED_RANKS_ENABLED / SHARED_RANKS_DIR live IN THIS PROCESS.

    The shared-ranks daemon does run here (the bot starts it at import), but it only refreshes
    _OTHER_RANKS_CACHE inside `if SHARED_RANKS_ENABLED:`. cc_web read that flag once at import and never
    again, so switching sharing on in the panel left _other_ranks() returning {} for the life of the web
    process - and _cycle_for_tier then subtracted a CROSS-SERVER prestige base from points with no peer
    contribution, under-tiering every prestiged player. (round-3 audit 2026-08-01)"""
    now = time.time()
    if now - _SHARED_CFG_AT[0] < 30.0:
        return
    _SHARED_CFG_AT[0] = now
    try:
        bot.load_shared_ranks_cfg()
    except Exception:                                    # noqa: BLE001 - sharing is optional
        pass


def _cycle_for_tier(sid, total_pts=None, combined=False):
    """Points that drive the ladder [ABBR]: cycle = CROSS-SERVER total − prestige_base.

    Computed LOCALLY in this process. It must NOT call bot.cycle_points(): that reads bot.RANK_DATA,
    which is bot-process state. cc_web imports the bot as a module and never runs main(), so
    load_ranks() is never called here and RANK_DATA stays {} for the life of the web process — every
    tier would come out as the BOTTOM rank while the adjacent Score column showed the real points, and
    the operator's "post top 5 to chat" would broadcast that wrong tag into live game chat.
    (Round-2 audit 2026-08-01, regression from the round-1 fix.)

    The defect the round-1 fix was aiming at is still fixed here: prestige_base is a CROSS-SERVER
    figure, so the total it is subtracted from must be cross-server too. Local points come from
    ranks.json (re-read per request); peer points come from bot._other_ranks(), which is refreshed in
    this process by the shared-ranks worker."""
    _refresh_prestige()                                  # see _refresh_prestige: base is stale otherwise
    _refresh_shared_cfg()                                # see _refresh_shared_cfg: peers are invisible otherwise
    try:
        local = float(total_pts) if total_pts is not None else float(
            (_read_ranks().get(sid) or {}).get("points", 0) or 0)
    except (TypeError, ValueError):
        local = 0.0
    # combined=True means the caller already handed us a CROSS-SERVER total (the shared_ranks board),
    # so adding peers again would double-count every point a player has on another server.
    peer = 0.0
    if sid and not combined:
        try:
            peer = float((bot._other_ranks() or {}).get(sid, 0) or 0)
        except Exception:                                # noqa: BLE001 - peers are optional
            peer = 0.0
    try:
        base = float(bot.prestige_base(sid)) if sid else 0.0
    except Exception:                                    # noqa: BLE001
        base = 0.0
    return max(0.0, local + peer - base)


def _leaderboard():
    d = _read_ranks()
    # Points board: when cross-server sharing is ON, use the COMBINED board the bot writes into the
    # dashboard (authoritative across the host's servers) so a server with few LOCAL players still
    # shows everyone's carried-over ranks -- fixes the "leaderboard had no ranks" case on a fresh
    # server. Falls back to local ranks.json when sharing is off or the board isn't ready.
    # Tier abbr from CYCLE points; score stays lifetime.
    pboard = None
    try:
        with open(DASHBOARD, encoding="utf-8") as f:
            sr = (json.load(f) or {}).get("shared_ranks", {}) or {}
        if sr.get("enabled") and sr.get("board"):
            pboard = []
            for r in sr["board"][:8]:
                pv = r.get("points", 0) or 0
                # board rows may omit sid; prefer cycle via name lookup is unavailable — use rank idx if present
                sid = r.get("sid") or ""
                if sid:
                    # pv comes from the shared_ranks board and is ALREADY the cross-server sum
                    ab, co = _rank_tier(_cycle_for_tier(sid, pv, combined=True), sid)
                elif "rank" in r:
                    RANKS = getattr(bot, "RANKS", [])
                    try:
                        _, _, ab, co = RANKS[int(r["rank"])]
                    except Exception:                    # noqa: BLE001
                        ab, co = _rank_tier(pv)
                else:
                    ab, co = _rank_tier(pv)
                pboard.append({"name": r.get("name", ""), "pts": _pts_i(pv), "abbr": ab, "color": co})
    except Exception:                                    # noqa: BLE001
        pboard = None
    if pboard is None:
        pts = sorted(((s, r) for s, r in d.items() if r.get("points", 0) > 0),
                     key=lambda kv: -kv[1].get("points", 0))[:8]
        pboard = []
        for sid, r in pts:
            # cross-server cycle for the tier; see _ranks_table for why the local total is not used
            ab, co = _rank_tier(_cycle_for_tier(sid), sid)
            pboard.append({"name": r.get("name", sid), "pts": _pts_i(r.get("points", 0)),
                           "abbr": ab, "color": co})
    return {"points": pboard}


def _ranks_table():
    d = _read_ranks()
    rows = sorted(d.items(), key=lambda kv: -kv[1].get("points", 0))
    out = []
    for sid, r in rows:
        # Lifetime pts for the score column; [ABBR - n*] from cycle (matches chat/name tags)
        # Pass NO total: _cycle_for_tier then uses bot.cycle_points(sid), which is cross-server aware.
        # r["points"] is this server's LOCAL total, but the prestige base subtracted from it is a
        # CROSS-SERVER figure (local + every peer), so mixing them under-reports every prestiged
        # player's tier here while their in-game tag shows the right one. (audit 2026-08-01)
        ab, co = _rank_tier(_cycle_for_tier(sid), sid)
        out.append({"name": r.get("name", sid), "pts": _pts_i(r.get("points", 0)),
                    "abbr": ab, "color": co, "wins": r.get("wins", 0), "losses": r.get("losses", 0)})
    return out


# ── Pterodactyl client API (Cloudflare-aware) ─────────────────────────────────
_PT_UA = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
          "(KHTML, like Gecko) Chrome/124.0 Safari/537.36")
_pt = {"key": None, "base": None, "server": None, "err": None, "loaded": 0.0}
_pt_lock = threading.Lock()


def _pt_load():
    with _pt_lock:
        if time.time() - _pt["loaded"] < 30 and _pt["server"]:
            return _pt
        _pt["loaded"] = time.time()
        try:
            # utf-8-sig: a PS-5.1-repaired apiKey.txt carries a BOM the locale codec renders as
            # 'ï»¿<key>' (str.strip() does NOT remove U+FEFF) -> every panel call 401s. (fix 2026-08-15)
            _pt["key"] = open(os.path.join(HERE, "apiKey.txt"), encoding="utf-8-sig").read().strip() or None
        except Exception:                                # noqa: BLE001
            _pt["key"] = None
        cfg = _tail(os.path.join(HERE, "panel.txt"), 2)
        raw = (cfg[0].strip().lstrip("﻿") if cfg else "") or ""   # same BOM trap on line 1 of panel.txt
        want = cfg[1].strip() if len(cfg) > 1 else None
        if "/server/" in raw and not want:               # accept the full browser URL form
            want = raw.partition("/server/")[2].split("/")[0] or None
        _pt["base"] = bot.normalize_panel_url(raw) or None
        _pt["err"] = None
        if not _pt["key"]:
            _pt["err"] = "no apiKey.txt"
        elif not _pt["base"]:
            _pt["err"] = "no panel.txt"
        elif want:
            _pt["server"] = want
        else:
            try:
                d = _pt_call("GET", "/api/client", None)
                s = d.get("data", [])
                _pt["server"] = s[0]["attributes"]["identifier"] if s else None
                if not _pt["server"]:
                    _pt["err"] = "API key sees no servers"
            except Exception as e:                       # noqa: BLE001
                _pt["err"] = f"discover failed: {e}"
        return _pt


def _pt_call(method, path, body):
    ctx = ssl.create_default_context()
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(_pt["base"] + path, data=data, method=method, headers={
        "Authorization": "Bearer " + _pt["key"], "Accept": "application/json",
        "Content-Type": "application/json", "User-Agent": _PT_UA})
    with urllib.request.urlopen(req, context=ctx, timeout=12) as r:
        ctype = r.headers.get("Content-Type", "")
        raw = r.read()
    return bot._pt_friendly_json(raw, ctype)


def _pt_power(signal):
    _pt_load()
    if not _pt.get("server"):
        return False, _pt.get("err") or "pterodactyl not configured"
    if signal not in ("start", "stop", "restart", "kill"):
        return False, "bad signal"
    try:
        _pt_call("POST", f"/api/client/servers/{_pt['server']}/power", {"signal": signal})
        return True, f"sent {signal}"
    except Exception as e:                               # noqa: BLE001
        return False, str(e)


def _pt_resources():
    _pt_load()
    if not _pt.get("server"):
        return {"configured": False, "err": _pt.get("err")}
    try:
        a = _pt_call("GET", f"/api/client/servers/{_pt['server']}/resources", None).get("attributes", {})
        u = a.get("resources", {})
        return {"configured": True, "state": a.get("current_state"),
                "cpu": round(u.get("cpu_absolute", 0), 1),
                "mem_mb": round(u.get("memory_bytes", 0) / 1048576),
                "uptime_s": round(u.get("uptime", 0) / 1000)}
    except Exception as e:                               # noqa: BLE001
        return {"configured": True, "err": str(e)}


def _pt_safe_restart():
    """Harden the webcc 'Restart' for a panel (Pterodactyl) server.

    xgamingserver slow-stop hazard: bare panel `restart` / graceful `stop` can hang in
    `stopping` (or weird `starting`) with the game process still alive — START is then
    rejected. Always: if already stuck -> KILL immediately; else STOP -> wait offline ->
    KILL if hung -> wait offline -> START. Background thread so HTTP returns at once.
    Logs to panel_safe_restart.log (no secrets). START always runs — never leave down.
    """
    import threading
    import time as _t

    _stuck = frozenset({"stopping", "starting"})
    _log_path = os.path.join(HERE, "panel_safe_restart.log")

    def _L(msg):
        line = time.strftime("%Y-%m-%d %H:%M:%S") + "  [webcc] " + msg
        try:
            with open(_log_path, "a", encoding="utf-8") as f:
                f.write(line + "\n")
        except OSError:
            pass

    def _wait_offline(seconds):
        deadline = _t.time() + seconds
        while _t.time() < deadline:
            try:
                if _pt_resources().get("state") == "offline":
                    return True
            except Exception:                            # noqa: BLE001
                pass
            _t.sleep(3)
        return False

    def _worker():
        try:
            st0 = None
            try:
                st0 = _pt_resources().get("state")
            except Exception:                            # noqa: BLE001
                st0 = "?"
            _L(f"safe_restart begin state={st0}")
            if st0 in _stuck:
                # Already mid-transition — do NOT wait 90s on a dead STOP.
                _L(f"stuck state={st0} -> KILL immediately")
                _pt_power("kill")
                _wait_offline(60)
            else:
                _pt_power("stop")
                offline = _wait_offline(90)
                _L("offline after STOP" if offline else "STOP hung -> KILL")
                if not offline:
                    _pt_power("kill")
                    _wait_offline(60)
            _L("sending START")
            _pt_power("start")
            _L("safe_restart START sent")
        except Exception as e:                           # noqa: BLE001
            _L(f"CRIT {type(e).__name__} -> force START")
            try:
                _pt_power("start")
            except Exception:                            # noqa: BLE001
                pass

    threading.Thread(target=_worker, daemon=True).start()
    return True, "restart initiated (stop -> kill-if-stuck -> start)"


# ── local (own-PC) power: start/stop the dedicated server process ───────────────
_local_proc = {"p": None}


def _is_local_power():
    return (((getattr(bot, "_TK_CFG", {}) or {}).get("server", {}) or {}).get("power") == "local")


def _local_game_dir():
    sv = (getattr(bot, "_TK_CFG", {}) or {}).get("server", {}) or {}
    return sv.get("game_dir") or sv.get("local_game_dir") or ""


def _server_alive():
    import subprocess
    import sys
    try:
        if sys.platform.startswith("win"):
            out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq NuclearOptionServer.exe"],
                                 capture_output=True, text=True, timeout=8).stdout
            return "NuclearOptionServer.exe" in out
        return subprocess.run(["pgrep", "-f", "NuclearOptionServer"], capture_output=True, timeout=8).returncode == 0
    except Exception:                                    # noqa: BLE001
        p = _local_proc["p"]
        return bool(p and p.poll() is None)


def _local_power(signal):
    import subprocess
    import sys
    if signal not in ("start", "stop", "restart", "kill"):   # unlike _pt_power this had NO guard; an unknown signal skipped the kill branch and launched a 2nd server
        return False, "bad signal"
    gd = _local_game_dir()
    if not gd or not os.path.isdir(gd):
        return False, "no local game dir configured"
    if signal in ("stop", "kill", "restart"):
        try:
            # Folder-scoped: only kill NuclearOptionServer whose ExecutablePath is under this
            # game_dir (trailing \). Never name-blind taskkill /IM — that hits every local server.
            if sys.platform.startswith("win"):
                gd_ps = gd.replace("'", "''")
                if not gd_ps.endswith("\\"):
                    gd_ps += "\\"
                ps = (
                    "$gd='" + gd_ps + "'; "
                    "Get-CimInstance Win32_Process -Filter \"Name='NuclearOptionServer.exe'\" | "
                    "Where-Object { $_.ExecutablePath -and ($_.ExecutablePath -like ($gd + '*')) } | "
                    "ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }"
                )
                subprocess.run(
                    ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", ps],
                    capture_output=True, timeout=15,
                )
            else:
                # Match only processes whose cwd/cmdline contains this game dir.
                subprocess.run(
                    ["pkill", "-f", re.escape(gd.rstrip("/")) + ".*NuclearOptionServer"],
                    capture_output=True, timeout=10,
                )
        except Exception as e:                           # noqa: BLE001
            if signal != "restart":
                return False, str(e)
        if signal != "restart":
            return True, "server stopped"
        time.sleep(2)
    starter = os.path.join(gd, "StartServer.bat" if sys.platform.startswith("win") else "start_server.sh")
    try:
        if os.path.exists(starter):
            _local_proc["p"] = (subprocess.Popen([starter], cwd=gd, creationflags=0x00000010)
                                if sys.platform.startswith("win") else
                                subprocess.Popen(["bash", starter], cwd=gd))
        else:
            exe = ""
            for n in ("NuclearOptionServer.exe", "NuclearOptionServer.x86_64"):
                if os.path.exists(os.path.join(gd, n)):
                    exe = os.path.join(gd, n)
                    break
            if not exe:
                return False, "server executable not found in " + gd
            _local_proc["p"] = subprocess.Popen([exe, "-batchmode", "-nographics"], cwd=gd)
        return True, "server started"
    except Exception as e:                               # noqa: BLE001
        return False, str(e)


def _local_resources():
    return {"configured": True, "local": True, "state": "running" if _server_alive() else "offline"}


# ── Toolkit version + GitHub updater (github/productization fork's installer/updater.py) ──────
# We READ deployed_toolkit.json + the toolkit config and CALL the fork's updater (never edit installer/).
# Inert in a dev checkout (no deployed_toolkit.json / no ~/.nuke-option-toolkit/config.json -> "not configured").
TOOLKIT_META = os.path.join(HERE, "deployed_toolkit.json")


def _toolkit_user_dir():
    """Folder-safe config dir, matching installer/updater.py: env pin > this folder's
    .nost-data > legacy shared dir. The legacy-first fallback silently read the WRONG
    config (wrong channel) on per-folder installs when launched without the wrapper."""
    env = os.environ.get("NOST_DATA_DIR")
    if env:
        return env
    local = os.path.join(HERE, ".nost-data")
    if os.path.isdir(local):
        return local
    return os.path.join(os.path.expanduser("~"), ".nuke-option-toolkit")


_USER_DIR    = _toolkit_user_dir()
_TOOLKIT_CFG = os.path.join(_USER_DIR, "config.json")
_toolkit_chk = {"ts": 0.0, "data": None}   # cached result of the last (network) update check


def _json_version(path):
    try:
        with open(path, encoding="utf-8") as f:
            return str((json.load(f) or {}).get("version", "") or "")
    except (OSError, ValueError):
        return ""


def _toolkit_cfg():
    try:
        with open(_TOOLKIT_CFG, encoding="utf-8") as f:
            d = json.load(f)
        return d if isinstance(d, dict) else {}
    except (OSError, ValueError):
        return {}


def _updater_mod():
    import importlib
    import sys as _sys
    idir = os.path.join(HERE, "installer")
    if idir not in _sys.path:
        _sys.path.insert(0, idir)
    import updater
    # reload each call (it's on-demand only): a self-updated installer/updater.py must take
    # effect without restarting the web CC
    return importlib.reload(updater)


# ── routes ────────────────────────────────────────────────────────────────────
@app.route("/")
def index():
    # Force fresh webcc.html: no-store + ETag from mtime/size. Clients that ignore Cache-Control
    # still revalidate via ETag; open with ?v=<anything> to bust stubborn caches.
    html_path = os.path.join(HERE, "webcc.html")
    try:
        st = os.stat(html_path)
        etag = f'W/"webcc-{int(st.st_mtime)}-{st.st_size}"'
    except OSError:
        etag = 'W/"webcc-unknown"'
    if request.headers.get("If-None-Match") == etag:
        return ("", 304)
    resp = send_from_directory(HERE, "webcc.html")
    resp.headers["Cache-Control"] = "no-cache, no-store, must-revalidate, max-age=0"
    resp.headers["Pragma"] = "no-cache"
    resp.headers["Expires"] = "0"
    resp.headers["ETag"] = etag
    resp.headers["Vary"] = "Accept-Encoding"
    return resp


@app.route("/api/toolkit")
def api_toolkit():
    """Fast/local: installed toolkit + plugin versions and the last cached check."""
    upd = (_toolkit_cfg().get("update") or {})
    return jsonify({
        "toolkit_version": _json_version(TOOLKIT_META) or None,
        "plugin_version":  _json_version(DEPLOYED_META) or None,
        "configured":      bool((upd.get("github_repo") or "").strip()),
        "check":           _toolkit_chk["data"],
        "checked_age":     (round(time.time() - _toolkit_chk["ts"], 1) if _toolkit_chk["ts"] else None),
    })


@app.route("/api/toolkit/check", methods=["POST"])
def api_toolkit_check():
    """On-demand: ask GitHub (via the fork's updater.check) whether a newer release exists."""
    upd = (_toolkit_cfg().get("update") or {})
    try:
        mod = _updater_mod()
        comps = getattr(mod, "ALL_COMPONENTS", ("plugin", "bot", "webcc", "installer"))
        info = mod.check(comps, verbose=False)           # ALL components — a web-CC-only update must show
    except Exception as e:                               # noqa: BLE001
        return jsonify({"ok": False, "error": str(e)})
    if not info:                                         # no repo configured or GitHub unreachable
        d = {"configured": bool((upd.get("github_repo") or "").strip()),
             "installed": _json_version(TOOLKIT_META) or None, "latest": None, "newer": None,
             "note": "updater not configured or GitHub unreachable"}
    else:
        rel = info.get("release") or {}
        d = {"configured": True, "installed": info.get("installed") or None, "latest": info.get("latest") or None,
             "newer": bool(info.get("newer")), "repo": info.get("repo"),
             "url": rel.get("html_url"), "components": info.get("components")}
    _toolkit_chk.update(ts=time.time(), data=d)
    return jsonify({"ok": True, **d})


@app.route("/api/toolkit/update", methods=["POST"])
def api_toolkit_update():
    """Download + VERIFY + INSTALL the latest. Bot / web CC / installer are applied immediately
    (every replaced file is backed up; a bot / web-CC restart loads them). The PLUGIN is only
    STAGED — it deploys via the normal Schedule / --deploy-plugin flow, so clicking Update can
    never surprise-restart the match."""
    import subprocess
    import sys as _sys
    upy = os.path.join(HERE, "installer", "updater.py")
    if not os.path.exists(upy):
        return jsonify({"ok": False, "error": "installer/updater.py not present"})
    try:
        env = dict(os.environ)
        env.setdefault("NOST_DATA_DIR", _USER_DIR)       # same config the web CC itself resolved
        r = subprocess.run([_sys.executable, upy, "update", "--component", "all"],
                           cwd=HERE, capture_output=True, text=True, timeout=300, env=env)
        out = r.stdout or ""
        summary = out.split("================ UPDATE SUMMARY ================")[-1].strip() \
            if "UPDATE SUMMARY" in out else None
        return jsonify({"ok": r.returncode == 0,
                        "applied": "APPLIED" in out,      # bot/webcc/installer installed now
                        "staged": "STAGED" in out,        # plugin downloaded, awaiting its deploy step
                        "summary": summary,
                        "output": out[-4000:], "error": ((r.stderr or "").strip()[-1000:] or None)})
    except Exception as e:                               # noqa: BLE001
        return jsonify({"ok": False, "error": str(e)})


# Last-good dashboard snapshot. A single torn/locked read of dashboard_state.json must not
# wipe the payload to {} (that made server_age null and flapped WebCC offline/stale). Age still
# climbs from the cached ts when the bot truly stops writing.
_last_dashboard = {}
_last_dashboard_lock = threading.Lock()


@app.before_request
def _localhost_redirect():
    # Windows resolves "localhost" to ::1 first and burns ~2s per NEW connection before
    # falling back to 127.0.0.1 (we bind IPv4 only). That starved the live-map playback
    # buffer. Redirect the initial page load; API fetches then inherit 127.0.0.1.
    from flask import request, redirect
    if request.method == "GET" and request.host.lower().startswith("localhost") and request.path in ("/", "/index.html"):
        return redirect(request.url.replace("//localhost", "//127.0.0.1", 1), code=302)
    return None


@app.route("/api/state")
def api_state():
    global _last_dashboard
    st = None
    try:
        with open(DASHBOARD, encoding="utf-8") as f:
            st = json.load(f)
        if isinstance(st, dict) and st.get("ts"):
            with _last_dashboard_lock:
                # cache a COPY: the overlays below keep mutating st AFTER the lock is released, so
                # caching the live reference both raced a concurrent dict(_last_dashboard) (dict
                # changed size during iteration -> HTML 500) and persisted the stale-overlay's
                # zeroed online_count / server_up=None into the "last good" snapshot. (fix 2026-08-15)
                _last_dashboard = dict(st)
    except Exception:                                    # noqa: BLE001
        st = None
    if not isinstance(st, dict) or not st:
        with _last_dashboard_lock:
            st = dict(_last_dashboard) if _last_dashboard else {}
    raw = request.args.get("raw") == "1"
    # 110, not 80: webcc now drops the duplicate [BOT] chat echoes (one event = one feed line), which
    # measured ~16% of the log — sending the old 80 would quietly shrink the visible feed by that much.
    st["activity"] = _tail(ACTIVITY, 110)
    st["console"] = _console_view(_tail(CONSOLE, 400), raw)
    m = (st.get("mission") or "").lower()
    # mission -> atlas terrain. Every stock Large Operation runs on Heartland EXCEPT Terminal
    # Control (Ignus Archipelago); Carrier Duel is on Ignus; scenario 13. Reprisal is on
    # Heartland (wiki + owner-confirmed 2026-07-02). Ignus keywords are checked FIRST so
    # "Terminal Control ..." never falls through to a heartland keyword. Unknown missions stay
    # None (no map is better than the wrong map).
    if any(k in m for k in ("ignus", "terminal", "carrier duel")):
        st["map_key"] = "ignus"
    elif any(k in m for k in ("heartland", "escalation", "altercation", "confrontation",
                              "domination", "breakout", "reprisal")):
        st["map_key"] = "heartland"
    else:
        st["map_key"] = None
    st["server_age"] = round(time.time() - st.get("ts", 0), 1) if st.get("ts") else None
    # STALE-DATA HONESTY (2026-07-27): a bot that stopped writing must not keep claiming
    # live players — the last snapshot once served server_up=True online_count=14 for hours
    # while the server was offline. When the snapshot itself is old, overlay the same
    # contract the bot writes for a dead relay: is_stale + zeroed online_count (last-known
    # kept in online_count_last). Threshold matches the frontend's 60s stale-banner enter
    # point (the bot's blocking SFTP/RCMD stalls can hit 30s+).
    #
    # BOT FEED LOST != GAME SERVER DOWN (2026-07-28): the overlay used to also assert
    # server_up=False here. cc_web has NO independent view of the game — it only reads the
    # bot's snapshot — so a dead/hung bot means the game state is UNKNOWN, not down. Asserting
    # False mislabelled a dead BOT as "Server offline" (sending the operator at the game host
    # instead of the bot) and made webcc's "the bot stopped updating" banner unreachable, since
    # that branch needs server_up truthy while this branch fires at the same 60s point.
    # Now: server_up -> None (unknown) + feed_lost=True + the last-known value in server_up_last.
    # Fail-open: every existing consumer tests truthiness (`!!st.server_up`, `st.get("server_up")`)
    # and None is falsy there, so an un-synced webcc/TUI/ops script behaves exactly as it does today.
    try:
        _age = st.get("server_age")
        if isinstance(_age, (int, float)) and _age > 60:
            st["is_stale"] = True
            if st.get("online_count"):
                st["online_count_last"] = st.get("online_count")
                st["online_count"] = 0
            st["feed_lost"] = True
            st["server_up_last"] = bool(st.get("server_up"))   # read BEFORE the overwrite below
            st["server_up"] = None                             # unknown — cc_web cannot see the game without the bot
        st.setdefault("is_stale", False)      # older bots don't write the flag at all
        st.setdefault("feed_lost", False)     # True only when the bot's snapshot itself went cold
        st.setdefault("server_up_last", None)
    except Exception:                                    # noqa: BLE001 - the overlay must never break /api/state
        pass
    # Item 1 [FRAMETIME]: expose the plugin's smoothed frame time for the panel, then DROP the NET monitor
    # payload (the box is replaced by the Frametime readout). Extract BEFORE popping "net".
    st["frametime"] = _extract_frametime(st)
    # FIX 1 [FRAMETIME]: the frontend reads the TOP-LEVEL contract field st.frametime_ms (a plain number),
    # NOT the {ms,ts} object above. Expose it from dashboard_state's top-level frametime_ms (the bot now
    # writes it), falling back to the plugin net line's frametime_ms. Number or None. Also before pop('net').
    st["frametime_ms"] = _frametime_ms(st)
    st.pop("net", None)
    # Items 6/7 [PVP LABEL]: the bot now writes the display-ready, [PVP]-prefixed mission name AND the correct
    # mission_pvp flag straight into dashboard_state. FIX 2: do NOT recompute mission_pvp here — _is_pvp() on
    # the already-prefixed "[PVP] Escalation" returns False and would CLOBBER the bot's True. Trust the bot's
    # mission_pvp; mission_label just mirrors the bot's already-prefixed mission (never re-prefixed).
    st["mission_label"] = st.get("mission") or ""
    st["deploy"] = _deploy_status()
    st["toolkit_version"] = _json_version(TOOLKIT_META) or None   # header chip; None in a dev checkout
    return jsonify(st)


@app.route("/api/missionpool", methods=["POST"])
def api_missionpool():
    """webcc Mission Pool modal: toggle a mission in/out of the votemap pool (routed to the bot)."""
    b = request.get_json(force=True, silent=True) or {}
    mission = str(b.get("mission", "")).strip()
    if not mission:
        return jsonify({"ok": False, "error": "no mission"})
    _queue_admin({"action": "missionpool", "mission": mission, "on": _truthy(b.get("on", True))})
    return jsonify({"ok": True})


_SID_RE = re.compile(r"^\d{6,20}$")


def _truthy(v):
    """Robust flag parse for JSON bodies. A STRING 'false'/'0'/'no'/'off'/'' is False (raw bool() makes any
    non-empty string True, which is how a stringy {all:'false'} silently wiped ALL reports and a stringy
    {unban:'false'} re-banned). Real booleans + real numbers pass straight through."""
    if isinstance(v, str):
        return v.strip().lower() in ("1", "true", "on", "yes")
    return bool(v)


def _finite(s):
    """Parse a user-supplied number; None unless finite ('nan'/'inf' pass float() but would
    corrupt ranks/funds downstream)."""
    try:
        v = float(s)
    except (TypeError, ValueError):
        return None
    return v if math.isfinite(v) else None


@app.route("/api/reports/ban", methods=["POST"])
def api_reports_ban():
    """webcc Reports tab: ban (default) or unban a SteamID (routed to the bot -> plugin)."""
    b = request.get_json(force=True, silent=True) or {}
    sid = str(b.get("sid", "")).strip()
    if not _SID_RE.match(sid):
        return jsonify({"ok": False, "error": "bad steamid"})
    # Item 4(a): unban must UNBAN. _truthy() so a stringy {unban:"false"} can't route to ban_steamid.
    action = "unban_steamid" if _truthy(b.get("unban")) else "ban_steamid"
    payload = {"action": action, "sid": sid}
    name = str(b.get("name") or "").strip()[:64]
    reason = re.sub(r"[\x00-\x1f|]+", " ", str(b.get("reason") or "")).strip()[:160]
    if name:
        payload["name"] = name
    if reason:
        payload["reason"] = reason
    _queue_admin(payload)
    return jsonify({"ok": True, "banned": action == "ban_steamid"})


@app.route("/api/reports/clear", methods=["POST"])
def api_reports_clear():
    """webcc Reports tab: clear ONE report (by unique seq) or ALL. Routed to the bot, the single
    writer of plugin_reports.json, so cleared reports don't reappear on the next /api/state push."""
    b = request.get_json(force=True, silent=True) or {}
    # Item 4(c): clearing ONE report must clear only that seq. _truthy() so a stringy {all:"false"} can't
    # fall into the clear-ALL branch and wipe every report.
    if _truthy(b.get("all")):
        _queue_admin({"action": "clear_reports"})
        return jsonify({"ok": True, "scope": "all"})
    try:
        seq = int(b.get("seq"))
    except (TypeError, ValueError):
        return jsonify({"ok": False, "error": "bad seq"})
    if seq <= 0:
        return jsonify({"ok": False, "error": "bad seq"})
    _queue_admin({"action": "clear_report", "seq": seq})
    return jsonify({"ok": True, "scope": "one", "seq": seq})


@app.route("/api/serverconfig/refresh", methods=["POST"])
def api_serverconfig_refresh():
    """webcc Server Settings tab: ask the bot to re-read DedicatedServerConfig.json (data arrives via /api/state)."""
    _queue_admin({"action": "dumpserverconfig"})
    return jsonify({"ok": True})


@app.route("/api/missionaudit", methods=["POST"])
def api_missionaudit():
    """webcc Mission Pool: re-scan the mission list (data via /api/state).

    Pass {"deep": true} for the INTEGRITY scan, which hashes every official mission against the
    trust-on-first-use baseline. That downloads every official mission (~15 MB here) and runs on the
    bot's single-threaded main loop, so it is opt-in and the bot refuses it while players are online.
    Without this the deep scan became unreachable and the panel kept asserting an integrity verdict no
    scan had computed. (round-4 audit 2026-08-01)"""
    b = request.get_json(force=True, silent=True) or {}
    _queue_admin({"action": "missionaudit", "deep": _truthy(b.get("deep"))})
    return jsonify({"ok": True})


@app.route("/api/mission/toggle", methods=["POST"])
def api_mission_toggle():
    """webcc Mission Pool: enable/disable a mission in the live MissionRotation (routed to the bot)."""
    b = request.get_json(force=True, silent=True) or {}
    name = str(b.get("name", "")).strip()
    if not name:
        return jsonify({"ok": False, "error": "no mission name"})
    _queue_admin({"action": "missiontoggle", "group": str(b.get("group", "User")), "name": name, "on": _truthy(b.get("on"))})
    return jsonify({"ok": True})


@app.route("/api/mission/workshop", methods=["POST"])
def api_mission_workshop():
    """webcc Mission Pool: add a Steam Workshop mission by published-file id (auto-downloads on restart)."""
    b = request.get_json(force=True, silent=True) or {}
    wid = str(b.get("id", "")).strip()
    if not re.fullmatch(r"\d{5,20}", wid):
        return jsonify({"ok": False, "error": "workshop id must be numeric"})
    _queue_admin({"action": "missionworkshop", "id": wid})
    return jsonify({"ok": True})


@app.route("/api/mission/upload", methods=["POST"])
def api_mission_upload():
    """webcc Mission Pool: upload a custom mission folder (staged locally, then SFTP'd by the bot, added OFF)."""
    b = request.get_json(force=True, silent=True) or {}
    name = str(b.get("name", "")).strip()
    files = b.get("files") or []
    if not name or not isinstance(files, list) or not files:
        return jsonify({"ok": False, "error": "need a mission name + at least one file"})
    if len(files) > 30:
        return jsonify({"ok": False, "error": "too many files (max 30)"})
    try:
        sdir = os.path.join(HERE, "mission_uploads")
        os.makedirs(sdir, exist_ok=True)
        sid = str(int(time.time() * 1000))
        with open(os.path.join(sdir, sid + ".json"), "w", encoding="utf-8") as f:
            json.dump({"name": name, "files": files}, f)
    except OSError as e:
        return jsonify({"ok": False, "error": f"stage failed: {e}"})
    _queue_admin({"action": "missionupload", "staging": os.path.join("mission_uploads", sid + ".json")})
    return jsonify({"ok": True})


_VOTEMAP_KEYS = {
    "enabled", "coop_count", "pvp_count", "coop_mode", "pvp_mode", "include_pvp", "include_custom",
    "no_repeat",                                 # PvE server: missions never repeat within a cycle (2026-08-22)
    "coop_weights", "pvp_weights", "mission_weights", "guaranteed", "avoid_recent",
    "force_pvp_enabled", "force_pvp_players", "force_pvp_coop", "force_pvp_pvp",
    "coop_minutes", "builtin_minutes",
    "boot_map",                                  # FIX 4: default/boot mission the server rotates to
    "ballot_size", "mode",                       # legacy aliases (bot maps them); harmless to keep
}


@app.route("/api/votemap", methods=["POST"])
def api_votemap():
    """webcc Votemap settings: set one vote-pool config key. The bot is the sole validator/writer; the
    weight keys carry a {name: number} object as their value."""
    b = request.get_json(force=True, silent=True) or {}
    key = str(b.get("key", "")).strip()
    if key not in _VOTEMAP_KEYS:
        return jsonify({"ok": False, "error": "unknown key"})
    _queue_admin({"action": "setvotemap", "key": key, "value": b.get("value")})
    return jsonify({"ok": True})


@app.route("/api/banaudit", methods=["POST"])
def api_banaudit():
    """webcc Moderation 'Banned' tab: ask the bot to re-read plugin_bans.txt (data via /api/state)."""
    _queue_admin({"action": "banaudit"})
    return jsonify({"ok": True})


@app.route("/api/logban", methods=["POST"])
def api_logban():
    """webcc Reports 'Log ban' button: record a ban in the persistent ban-log (repeat-offender tracking)."""
    b = request.get_json(force=True, silent=True) or {}
    sid = str(b.get("sid", "")).strip()
    if not re.fullmatch(r"\d{6,20}", sid):
        return jsonify({"ok": False, "error": "bad steamid"})
    # optional what-happened detail from the source report (victim/method/weapon/dmg/nc/ts) so the
    # ban log keeps the same expandable card the report had; whitelisted + trimmed here
    detail = None
    d = b.get("detail")
    if isinstance(d, dict):
        detail = {}
        for k in ("victim", "method", "weapon", "munition", "nc"):
            if d.get(k):
                detail[k] = str(d[k])[:120]
        for k in ("dmg", "ts"):
            try:
                v = float(d.get(k) or 0)
                if math.isfinite(v):
                    detail[k] = v
            except (TypeError, ValueError):
                pass
        # 0.9.43: the per-blast unit list rides along so the ban-log card keeps the
        # 'Killed in this blast' row (audit fix: this whitelist silently dropped it)
        units = d.get("units")
        if isinstance(units, list) and units:
            clean = []
            for u in units[:24]:
                if not isinstance(u, dict):
                    continue
                try:
                    ud = float(u.get("d") or 0)
                except (TypeError, ValueError):
                    ud = 0.0
                if not math.isfinite(ud):
                    ud = 0.0
                clean.append({"n": str(u.get("n") or "?")[:80], "f": str(u.get("f") or "?")[:2], "d": ud})
            if clean:
                detail["units"] = clean
    _queue_admin({"action": "logban", "sid": sid, "name": str(b.get("name", ""))[:64],
                  "reason": str(b.get("reason", ""))[:200], "detail": detail})
    return jsonify({"ok": True})


@app.route("/api/banlog/remove", methods=["POST"])
def api_banlog_remove():
    """webcc Ban log 🗑 button: delete one player's logged-ban history. Separate from clearing reports."""
    b = request.get_json(force=True, silent=True) or {}
    sid = str(b.get("sid", "")).strip()
    if not re.fullmatch(r"\d{6,20}", sid):
        return jsonify({"ok": False, "error": "bad steamid"})
    _queue_admin({"action": "rmbanlog", "sid": sid, "name": str(b.get("name", ""))[:64]})
    return jsonify({"ok": True})


@app.route("/api/serverconfig", methods=["POST"])
def api_serverconfig_set():
    """webcc Server Settings tab: edit one config field (routed to the bot -> SFTP + gpanel mirror).
    Rejects unknown fields and empty numeric values HERE so obvious mistakes fail fast; a true 'saved'
    is only ever reported by the bot after its verify-after-write (queued != applied)."""
    b = request.get_json(force=True, silent=True) or {}
    key = str(b.get("key", "")).strip()
    if not key:
        return jsonify({"ok": False, "error": "no key"})
    srv_map = getattr(bot, "_SRVCFG_MAP", None)
    if isinstance(srv_map, dict) and srv_map and key not in srv_map:
        return jsonify({"ok": False, "error": f"unknown field {key}"})
    # Fail fast on bot-managed (derived/hidden) fields — PostMissionDelay is derived from the vote timing and
    # must never be settable directly (the bot enforces this authoritatively too, in set_server_config).
    hidden = getattr(bot, "_SRVCFG_HIDDEN_FIELDS", None) or set()
    if key in hidden:
        return jsonify({"ok": False, "error": f"{key} is derived from the vote timing and cannot be set directly"})
    if isinstance(srv_map, dict) and key in srv_map:
        typ = srv_map[key][1]
        if typ in ("int", "float") and str(b.get("value", "")).strip() == "":
            return jsonify({"ok": False, "error": "enter a value"})
    _queue_admin({"action": "setserverconfig", "key": key, "value": b.get("value")})
    return jsonify({"ok": True, "queued": True})


@app.route("/api/serverconfig/restart", methods=["POST"])
def api_serverconfig_restart():
    """webcc Server Settings tab: restart the game server to apply restart-only config changes."""
    try:
        ok, msg = (_local_power("restart") if _is_local_power() else _pt_safe_restart())   # safe escalation, never a bare panel 'restart' that can hang
        return jsonify({"ok": bool(ok), "error": None if ok else msg})
    except Exception as e:                                  # noqa: BLE001
        return jsonify({"ok": False, "error": str(e)})


@app.route("/api/sysmessages", methods=["POST"])
def api_sysmessages():
    """webcc Messages tab: edit a built-in automated message (enable / text / interval / delay)."""
    b = request.get_json(force=True, silent=True) or {}
    key = str(b.get("key", "")).strip()
    if not key:
        return jsonify({"ok": False, "error": "no key"})
    fields = {}
    if "enabled" in b:
        fields["enabled"] = _truthy(b.get("enabled"))
    if "text" in b:
        fields["text"] = str(b.get("text", ""))[:240]
    for nk in ("interval", "delay"):
        if nk in b:
            try:
                fields[nk] = float(b.get(nk))
            except (TypeError, ValueError):
                return jsonify({"ok": False, "error": f"{nk} must be a number"})
    _queue_admin({"action": "sysmsg", "key": key, "fields": fields})
    return jsonify({"ok": True})


@app.route("/api/helpcfg", methods=["POST"])
def api_helpcfg():
    """webcc Help editor: show/hide a command in the dynamic !help list. The bot owns help_config.json;
    command TEXT edits reuse /api/sysmessages (key 'help_<cmd>')."""
    b = request.get_json(force=True, silent=True) or {}
    cmd = str(b.get("cmd", "")).strip()
    if not re.fullmatch(r"[a-z]{2,16}", cmd):
        return jsonify({"ok": False, "error": "bad cmd"})
    _queue_admin({"action": "helpcfg", "cmd": cmd, "on": _truthy(b.get("on", True))})
    return jsonify({"ok": True})


def _sync_bot_ranks():
    """Point the bot module's RANK_DATA at the ranks file THIS process reads.

    bot.RANK_DATA is only ever filled by load_ranks(), which runs inside the bot's main(). cc_web
    imports the bot as a module, so in this process it stays {} forever - that is exactly the trap that
    made every panel rank tier render as the bottom rank once. Anything here that calls a bot helper
    reading RANK_DATA must seed it first. _read_ranks() is mtime-cached, so this is a dict assignment,
    not a re-parse."""
    try:
        bot.RANK_DATA = _read_ranks() or {}
    except Exception:                                    # noqa: BLE001
        pass
    _refresh_prestige()
    _refresh_ladder()


@app.route("/api/player/search")
def api_player_search():
    """Leaderboard search box: partial, case-insensitive, matches current AND last-known names."""
    q = (request.args.get("q") or "").strip()
    if len(q) < 2:
        return jsonify({"ok": True, "results": []})      # 2 chars minimum: 1 matches most of 1858 rows
    _sync_bot_ranks()
    try:
        return jsonify({"ok": True, "results": bot.search_players(q, 12)})
    except Exception as e:                               # noqa: BLE001
        return jsonify({"ok": False, "error": str(e), "results": []})


@app.route("/api/player/card")
def api_player_card():
    """Full stat card for one player. Same derivation the in-game !stats uses, so they cannot differ."""
    sid = (request.args.get("sid") or "").strip()
    if not re.fullmatch(r"\d{6,20}", sid):
        return jsonify({"ok": False, "error": "bad steamid"})
    _sync_bot_ranks()
    try:
        # The running match's MODE comes from the live bot's dashboard snapshot - this process's
        # own CUR_MATCH is a module default and would pin every card to PvE. (audit 2026-08-14)
        _mode, _dash_players = "pvp", []
        try:
            with open(DASHBOARD, encoding="utf-8") as _f:
                _dash = json.load(_f) or {}
            _mode = "pvp" if _dash.get("mission_pvp") else "pve"
            _dash_players = _dash.get("players") or []
        except Exception:                                    # noqa: BLE001
            pass
        c = bot.player_stat_card(sid, mode=_mode)
        # Unknown-sid guard. The old `total == 0 and ...` AND-chain was unreachable (name falls back
        # to the sid and total is the FLEET-wide ranked count), so any 6-20 digit number returned a
        # fabricated all-zero card. Unknown = no points and not in this server's ranks.json.
        if not c.get("name") or (c.get("points", 0) == 0 and sid not in (_read_ranks() or {})):
            return jsonify({"ok": False, "error": "no such player"})
        # "online" from the bot's card is `sid in ROSTER_BY_SID`, which only the BOT process's main
        # loop fills - in this interpreter it is permanently {} and the ONLINE pill could never show.
        # Overlay it from the dashboard roster this route already reads. (fix 2026-08-15)
        c["online"] = any(str(p.get("sid")) == sid for p in _dash_players if isinstance(p, dict))
        return jsonify({"ok": True, "card": c})
    except Exception as e:                               # noqa: BLE001
        return jsonify({"ok": False, "error": str(e)})


@app.route("/api/rankladder", methods=["POST"])
def api_rankladder():
    """webcc Ranks modal: replace the whole rank ladder + rank-up template. The bot owns
    rank_ladder.json and is the SOLE validator; this does cheap shape checks and queues."""
    b = request.get_json(force=True, silent=True) or {}
    if str(b.get("op", "save")).strip().lower() != "save":
        return jsonify({"ok": False, "error": "bad op"})
    ranks = b.get("ranks")
    if not isinstance(ranks, list):
        return jsonify({"ok": False, "error": "ranks must be a list"})
    # an EMPTY list is valid: it turns the rank ladder feature off (the shipped default)
    if len(ranks) > 40:
        return jsonify({"ok": False, "error": "too many ranks (max 40)"})
    clean = []
    for r in ranks:
        if not isinstance(r, dict):
            return jsonify({"ok": False, "error": "bad rank row"})
        try:
            th = int(float(r.get("threshold", 0)))
        except (TypeError, ValueError, OverflowError):
            return jsonify({"ok": False, "error": "threshold must be a number"})
        clean.append({"threshold": th,
                      "name": str(r.get("name", ""))[:40],
                      "abbr": str(r.get("abbr", ""))[:12],
                      "color": str(r.get("color", ""))[:7]})
    tmpl = str(b.get("rankup_template", ""))[:240]
    pt_raw = b.get("prestige_template")
    ptmpl = str(pt_raw)[:48] if pt_raw not in (None, "") else None   # absent/blank -> None so the bot fills its default (fail-open)
    _queue_admin({"action": "rankladder", "payload": {"ranks": clean, "rankup_template": tmpl,
                                                       "prestige_template": ptmpl}})
    return jsonify({"ok": True})


@app.route("/api/sharedranks", methods=["POST"])
def api_sharedranks():
    """webcc Shared Ranks card: enable/disable cross-server rank sharing + set the shared dir.
    The bot owns shared_ranks.json and does the publish/read; this just queues."""
    b = request.get_json(force=True, silent=True) or {}
    enabled = _truthy(b.get("enabled"))
    dir_ = str(b.get("dir", "") or "").strip()[:500]
    if enabled and not dir_:
        return jsonify({"ok": False, "error": "enter the shared folder path"})
    _queue_admin({"action": "sharedranks", "enabled": enabled, "dir": dir_})
    return jsonify({"ok": True})


@app.route("/api/sharedranks/validate", methods=["POST"])
def api_sharedranks_validate():
    """Advisory server-side path check for the Shared Ranks card. NOTE: cc_web writability is
    not the bot's writability (separate processes) - the bot publisher's success is the real signal."""
    b = request.get_json(force=True, silent=True) or {}
    dir_ = str(b.get("dir", "") or "").strip()
    if not dir_:
        return jsonify({"ok": False, "error": "no path"})
    import glob as _glob
    exists = os.path.isdir(dir_)
    writable = bool(exists and os.access(dir_, os.W_OK))
    network = dir_.startswith("\\\\") or dir_.startswith("//")
    peers = len(_glob.glob(os.path.join(dir_, "rankshare_*.json"))) if exists else 0
    return jsonify({"ok": True, "exists": exists, "writable": writable,
                    "network": bool(network), "peer_files": peers})


_MSG_TRIGGERS = ("interval", "clock", "match_start", "match_end")
_MSG_HHMM_RE = re.compile(r"^([01]?\d|2[0-3]):[0-5]\d$")
_MSG_HEX_RE = re.compile(r"^#[0-9A-Fa-f]{6}$")


@app.route("/api/messages", methods=["POST"])
def api_messages():
    """webcc Messages modal: CRUD automated server messages (routed to the bot, which owns the file
    and re-validates). op = add | update | delete | toggle."""
    b = request.get_json(force=True, silent=True) or {}
    op = str(b.get("op", "")).strip().lower()
    if op not in ("add", "update", "delete", "toggle"):
        return jsonify({"ok": False, "error": "bad op"})
    if op in ("delete", "toggle"):
        mid = str(b.get("id", "")).strip()
        if not mid:
            return jsonify({"ok": False, "error": "no id"})
        rec = {"action": "servermsg", "op": op, "msg": {"id": mid}}
        if op == "toggle":
            rec["msg"]["on"] = _truthy(b.get("on", True))
        _queue_admin(rec)
        return jsonify({"ok": True})
    # add / update -> validate the message fields
    text = str(b.get("text", "")).strip()
    if op == "add" and not text:
        return jsonify({"ok": False, "error": "message text is required"})
    trig = str(b.get("trigger", "interval")).strip()
    if trig not in _MSG_TRIGGERS:
        return jsonify({"ok": False, "error": "trigger must be one of: " + ", ".join(_MSG_TRIGGERS)})
    msg = {"text": text[:240], "trigger": trig}
    try:
        msg["interval_min"] = max(1, min(1440, int(float(b.get("interval_min", 30)))))
    except (TypeError, ValueError):
        return jsonify({"ok": False, "error": "interval must be a whole number of minutes"})
    at = str(b.get("at", "")).strip()
    if trig == "clock" and not _MSG_HHMM_RE.match(at):
        return jsonify({"ok": False, "error": "time must be HH:MM (24-hour)"})
    msg["at"] = at
    color = str(b.get("color", "")).strip()
    if color and not _MSG_HEX_RE.match(color):
        return jsonify({"ok": False, "error": "colour must be a #RRGGBB hex value"})
    msg["color"] = color
    if "enabled" in b:
        msg["enabled"] = bool(b.get("enabled"))
    if op == "update":
        mid = str(b.get("id", "")).strip()
        if not mid:
            return jsonify({"ok": False, "error": "no id"})
        msg["id"] = mid
    _queue_admin({"action": "servermsg", "op": op, "msg": msg})
    return jsonify({"ok": True})


@app.route("/api/settings")
def api_settings():
    """Merge the static catalogue with LIVE values (plugin cfg from the dashboard; bot overrides
    from bot_overrides.json) so the settings menu shows real current values."""
    cat = _load_catalogue()
    try:
        with open(DASHBOARD, encoding="utf-8") as f:
            _dash = json.load(f) or {}
    except Exception:                                    # noqa: BLE001
        _dash = {}
    live = _dash.get("plugin_cfg") or {}
    # votemap-owned settings (force-PvP etc.) read the same config the Mission Pool edits
    try:
        vmcfg = bot._votemap_cfg()
    except Exception:                                    # noqa: BLE001
        vmcfg = {}
    # FIX 3: the two vote-timing knobs are bot GLOBALS persisted in .nost-data (not votemap_config.json /
    # bot_overrides.json), so read their LIVE values from the dashboard votemap block the running bot writes.
    vmstate = _dash.get("votemap") or {}
    # game-owned settings read the bot's DedicatedServerConfig mirror (dashboard server_config)
    scvals = {}
    try:
        for f_ in ((_dash.get("server_config") or {}).get("fields") or []):
            if f_.get("key") is not None and f_.get("value") not in (None, ""):
                scvals[f_["key"]] = f_["value"]
    except Exception:                                    # noqa: BLE001
        scvals = {}
    try:
        with open(BOT_OVERRIDES, encoding="utf-8") as f:
            bov = json.load(f) or {}
    except (OSError, ValueError):
        bov = {}
    awardst = _award_state()                             # FIX 3: current Award.*_ON on/off from award_config.json
    have_live = bool(live)
    # (public-listing overlay removed with the server-directory feature - the Global.* rows are
    #  dropped from the catalogue entirely via _CATALOGUE_REMOVE)
    out, groups = [], []
    for s in cat:
        key = s.get("key", "")
        owner = s.get("owner", "plugin")
        val = s.get("default")
        if key in _AWARD_TOGGLE_MAP:                     # FIX 3: reflect the bot's award_config on/off, not the setcfg default
            mk = _AWARD_TOGGLE_MAP[key]
            if mk in awardst:
                val = bool(awardst[mk])
        elif key == "MAP_VOTE_DURATION" and vmstate.get("map_vote_duration") is not None:
            val = vmstate["map_vote_duration"]           # FIX 3: live value from the running bot (not 60)
        elif key == "POST_VOTE_MAP_CHANGE_DELAY" and vmstate.get("post_vote_change_delay") is not None:
            val = vmstate["post_vote_change_delay"]       # FIX 3: live value from the running bot
        elif owner == "plugin" and key in live:
            val = live[key]
        elif owner == "bot":
            short = key.split(".")[-1].split(":")[-1]
            if short in bov:
                val = bov[short]
        elif owner == "votemap" and key in vmcfg:
            val = vmcfg[key]
        elif owner == "game" and key in scvals:
            val = scvals[key]
        # item 9 [SETTINGS PERSISTENCE]: overlay a just-queued value until the live source confirms it
        # (or the TTL lapses), so the panel is never told a stale value between save and the next dump.
        pend = _pending_get(key)
        if pend is not None:
            if _norm_setting_val(val) == _norm_setting_val(pend):
                _pending_clear(key)                      # live now matches -> confirmed, stop overlaying
            else:
                val = _coerce_pending(s.get("type", "string"), pend)
        row = dict(s)
        row["value"] = val
        out.append(row)
        g = s.get("group", "Other")
        if g not in groups:
            groups.append(g)
    if not have_live:                                    # nudge the bot to ask the plugin for a fresh dump (throttled)
        global _last_dump_nudge
        if time.time() - _last_dump_nudge > 10:
            _last_dump_nudge = time.time()
            try:
                _queue_admin({"action": "dumpcfg"})
            except Exception:                            # noqa: BLE001
                pass
    simple = [s["key"] for s in out if s.get("commonlyChanged")]
    return jsonify({"settings": out, "groups": groups, "simpleKeys": simple, "live": have_live})


# ── string-setting validation ───────────────────────────────────────────────────────────────────
# type="string" rows used to fall straight through to `str(val)`: ANY text was accepted and pushed
# to setcfg unchecked. Admin.SteamIds is the row that grants IN-GAME admin, and the same class of
# mistake that once concatenated two SteamIDs into one unusable ban_list entry would silently write
# a garbage allow-list here (the plugin just never matches it — admins are locked out with no error).
# Validate + NORMALISE the string rows where a typo has real teeth; the rest stay permissive.
_STEAMID64_LEN = 17          # every SteamID64 is 17 digits and starts with the 76561 prefix


def _validate_string_setting(key, val):
    """(normalised_value, None) when accepted, (None, "reason") when rejected."""
    s = str("" if val is None else val).strip()
    if key == "Admin.SteamIds":
        # IsAdmin() splits on ',' ' ' and ';' — accept all three, re-emit canonical comma-separated.
        # Empty is ALLOWED: that is how an operator deliberately clears the in-game admin list.
        toks = [t for t in re.split(r"[,;\s]+", s) if t]
        bad = [t for t in toks if not (t.isdigit() and len(t) == _STEAMID64_LEN and t.startswith("76561"))]
        if bad:
            return None, ("not a SteamID64: " + ", ".join(bad[:3])
                          + " — each ID must be 17 digits starting 76561"
                          + (" (a 34-digit value means two IDs were joined together)"
                             if any(t.isdigit() and len(t) > _STEAMID64_LEN for t in bad) else ""))
        if len(toks) > 64:
            return None, "too many SteamIDs (max 64)"
        out = []
        for t in toks:                                   # dedupe, keep the operator's order
            if t not in out:
                out.append(t)
        return ",".join(out), None
    if key.startswith("Admin.SkyDrop"):
        # "x,z" map coordinates (plugin FactionDropPos/ParseXZ). A malformed pair silently falls back
        # to the ocean corner — which is then where EVERY autobalance move drops the player.
        parts = [p for p in re.split(r"[,;\s]+", s) if p]
        try:
            if len(parts) != 2:
                raise ValueError("need two numbers")
            x, z = float(parts[0]), float(parts[1])
            if not (math.isfinite(x) and math.isfinite(z)):   # "nan,inf" parses as float — reject it
                raise ValueError("not finite")
        except ValueError:
            return None, "must be two numbers 'x,z' (e.g. -5000,-60000)"
        if abs(x) > 200000 or abs(z) > 200000:
            return None, "x and z must be within +/-200000 of map centre"
        return f"{x:g},{z:g}", None
    return s, None


@app.route("/api/settings", methods=["POST"])
def api_settings_set():
    """Validate a single setting change against the catalogue, then queue it to the bot."""
    b = request.get_json(force=True, silent=True) or {}
    key = str(b.get("key", "")).strip()
    if not key:
        return jsonify({"ok": False, "error": "no key"})
    meta = {s.get("key"): s for s in _load_catalogue()}.get(key)
    if not meta:
        return jsonify({"ok": False, "error": f"unknown setting {key}"})
    owner = meta.get("owner", "plugin")
    typ = meta.get("type", "string")
    val = b.get("value")
    # FIX 3 [AWARD TOGGLES]: the Award.*_ON toggles are NOT setcfg settings — the bot rejects them as an
    # unknown bot setting. Route them to the bot's dedicated `awardtoggle` action (via the same admin relay
    # as sysmsg/servermsg) with the award_config.json short key + a real boolean, matching the bot's handler
    # exactly: set_award_toggle(cmd["key"], bool(cmd["on"])). NOT setcfg.
    if key in _AWARD_TOGGLE_MAP:
        on = (val is True or str(val).lower() in ("1", "true", "on", "yes"))
        _queue_admin({"action": "awardtoggle", "key": _AWARD_TOGGLE_MAP[key], "on": on})
        _pending_set(key, "1" if on else "0")            # hold the optimistic on/off until the live state confirms
        return jsonify({"ok": True, "queued": on, "owner": "bot", "action": "awardtoggle",
                        "needs_restart": meta.get("live") == "restart"})
    if typ == "toggle":
        on = (val is True or str(val).lower() in ("1", "true", "on", "yes"))
        # bot-owned toggles ride the bot's numeric override branch (float()-parsed, stored 1/0) — so a
        # boolean "true"/"false" would be rejected. Encode them as 1/0; plugin/game toggles stay true/false.
        sval = ("1" if on else "0") if owner == "bot" else ("true" if on else "false")
    elif typ in ("int", "float"):
        try:
            num = float(val)
        except (TypeError, ValueError):
            return jsonify({"ok": False, "error": "must be a number"})
        if num != num or num in (float("inf"), float("-inf")):   # reject NaN / Infinity (else int() 500s)
            return jsonify({"ok": False, "error": "must be a finite number"})
        try:
            if meta.get("min") not in (None, ""):
                num = max(num, float(meta["min"]))
            if meta.get("max") not in (None, ""):
                num = min(num, float(meta["max"]))
        except (TypeError, ValueError):
            pass
        sval = str(int(num) if typ == "int" else num)
    elif typ == "enum":
        opts = [str(o) for o in (meta.get("options") or [])]
        if str(val) not in opts:
            return jsonify({"ok": False, "error": "must be one of: " + ", ".join(opts)})
        sval = str(val)
    else:
        sval, verr = _validate_string_setting(key, val)
        if verr:
            return jsonify({"ok": False, "error": verr})
    _queue_admin({"action": "setcfg", "key": key, "value": sval, "owner": owner})
    _pending_set(key, sval)                              # item 9: hold this value so the panel doesn't revert
    return jsonify({"ok": True, "queued": sval, "owner": owner,
                    "needs_restart": meta.get("live") == "restart"})


@app.route("/api/console-filter", methods=["GET", "POST"])
def api_console_filter():
    """The webcc's 'filter messages like this' list. POST {action:add, pattern:<a console line>}
    normalises the line (digits -> #) and adds it; lines matching any pattern are hidden."""
    if request.method == "GET":
        return jsonify({"filters": _load_console_filters()})
    b = request.get_json(force=True, silent=True) or {}
    action = b.get("action", "add")
    lst = _load_console_filters()
    if action == "add":
        pat = _norm_console(b.get("pattern", ""))
        if pat and pat not in lst:
            lst.append(pat)
    elif action == "remove":
        pat = str(b.get("pattern", "")).strip().lower()
        lst = [p for p in lst if p != pat]
    elif action == "clear":
        lst = []
    _save_console_filters(lst)
    return jsonify({"ok": True, "filters": lst})


@app.route("/api/commands")
def api_commands():
    # Item 5: `commands` is the real, whitelisted command list the frontend autocompletes from (server
    # aliases + bot/local verbs). Item 6: `missions_pvp` flags which missions carry the [PVP] tag.
    ms = _missions()
    return jsonify({"commands": _catalog(), "missions": ms,
                    "missions_pvp": [m for m in ms if _is_pvp(m)],
                    "factions": ["boscali", "primeva"]})


@app.route("/api/map")
def api_map():
    key = request.args.get("key", "")
    d = _ATLAS.get(key)
    if not d:
        return jsonify({"error": "no atlas"}), 404
    has_img = os.path.exists(os.path.join(HERE, key + "_map.png"))
    out = {k: d[k] for k in ("name", "cols", "rows", "x0", "x1", "z0", "z1",
                             "xmin", "cell", "znorth", "bases")}
    out["gcols"] = d.get("gcols", round((d["x1"] - d["x0"]) / d["cell"]) + 6)
    out["img"] = key + "_map.png" if has_img else None
    return jsonify(out)


@app.route("/api/mapimg")
def api_mapimg():
    key = request.args.get("key", "")
    fn = key + "_map.png"
    if key in _ATLAS and os.path.exists(os.path.join(HERE, fn)):
        return send_from_directory(HERE, fn, mimetype="image/png")
    return ("", 404)


@app.route("/api/sharedleaderboard")
def api_sharedleaderboard():
    """Full COMBINED cross-server board (ALL players) for the webcc Leaderboard 'Shared' column.
    Reads the shared dir from the dashboard (authoritative — cc_web's in-process bot copy can be stale
    because sharing may have been toggled AFTER cc_web started) and aggregates every rankshare_*.json.
    Read-only; tolerant of a peer file mid-write."""
    import glob
    out = {"enabled": False, "rows": [], "peers": 0, "server_id": None}
    try:
        with open(DASHBOARD, encoding="utf-8") as f:
            sr = (json.load(f) or {}).get("shared_ranks", {}) or {}
    except Exception:                                    # noqa: BLE001
        sr = {}
    out["enabled"] = bool(sr.get("enabled"))
    out["server_id"] = sr.get("server_id")
    sdir = sr.get("dir") or ""
    if not (out["enabled"] and sdir and os.path.isdir(sdir)):
        return jsonify(out)
    agg = {}
    try:
        files = glob.glob(os.path.join(sdir, "rankshare_*.json"))
        out["peers"] = len(files)
        for path in files:
            try:
                with open(path, encoding="utf-8") as f:
                    d = json.load(f)
            except Exception:                            # noqa: BLE001 - tolerate a file mid-write
                continue
            ranks = d.get("ranks", {}) if isinstance(d, dict) else {}
            for psid, rec in (ranks.items() if isinstance(ranks, dict) else []):
                if not isinstance(rec, dict):
                    continue
                a = agg.setdefault(psid, {"name": "", "points": 0.0, "wins": 0, "losses": 0})
                try:
                    a["points"] += float(rec.get("points", 0) or 0)
                    a["wins"] += int(rec.get("wins", 0) or 0)
                    a["losses"] += int(rec.get("losses", 0) or 0)
                except (TypeError, ValueError):
                    pass
                if rec.get("name"):
                    a["name"] = rec["name"]
    except OSError:
        pass
    rows = sorted(agg.items(), key=lambda kv: -kv[1]["points"])
    board = []
    for psid, v in rows:                                 # ALL players across every server (the owner wants the full shared board)
        ab, co = _rank_tier(_cycle_for_tier(psid), psid)   # cross-server cycle; see _ranks_table
        board.append({"name": v["name"] or psid, "pts": _pts_i(v["points"]),
                      "abbr": ab, "color": co, "w": v["wins"], "l": v["losses"]})
    out["rows"] = board
    return jsonify(out)


@app.route("/api/cmd", methods=["POST"])
def api_cmd():
    b = request.get_json(force=True, silent=True) or {}
    name = str(b.get("name") or "").strip()              # str(): a non-string JSON value must not 500 outside the try
    raw_args = b.get("args", [])
    if not isinstance(raw_args, (list, tuple)):
        raw_args = [raw_args] if raw_args not in (None, "") else []
    args = [str(a) for a in raw_args]
    sid = str(b.get("sid", "")).strip()                  # set by the player popup
    if sid and not _SID_RE.match(sid):                   # sid reaches pipe-framed plugin_cmd files -- digits only
        return jsonify({"ok": False, "error": "bad SteamID"})
    text = " ".join(args).strip()
    try:
        if name in ("leaderboard", "lb", "top"):
            return jsonify({"ok": True, "board": _leaderboard()})
        if name == "ranks":
            return jsonify({"ok": True, "ranks": _ranks_table()})
        if name == "say":
            text = re.sub(r"[\x00-\x1f]+", " ", text).strip()   # control chars would spoof extra activity-log lines
            if not text:
                return jsonify({"ok": False, "error": "usage: say <message>"})
            res = _send_cmd("send-chat-message", [f"<color=#FF8C00>[Admin] {text}</color>"])
            try:    # mirror to the activity feed: admin broadcasts are server RPCs the bot can't parse as chat
                with open(ACTIVITY, "a", encoding="utf-8") as f:
                    f.write(f"{time.strftime('%I:%M:%S %p')}  [ADMIN] {text}\n")
            except OSError:
                pass
            return jsonify({"ok": True, "result": res, "info": f"said: {text}"})
        if name == "rankpreview":
            d = _read_ranks()
            top = sorted(((s, r) for s, r in d.items() if r.get("points", 0) > 0),
                         key=lambda kv: -kv[1].get("points", 0))[:5]
            _send_cmd("send-chat-message", ["<color=#FFD200>== TOP PILOTS ==</color>"])
            for i, (s, r) in enumerate(top, 1):
                ab, co = _rank_tier(_cycle_for_tier(s), s)   # cross-server cycle; see _ranks_table
                # rankup_line's rule: strip < > so a hostile display name can't hijack the colour tags
                nm = str(r.get("name", s)).replace("<", "").replace(">", "")
                tag = f"<color={co}>[{ab}]</color> " if ab else ""   # no tier tag while the ladder is off
                _send_cmd("send-chat-message",
                          [f"{i}. {tag}{nm} - {r.get('points', 0):.0f} pts"])
            return jsonify({"ok": True, "info": f"posted top {len(top)} to chat"})
        if name == "nextmap":
            full = _resolve_mission(text)
            if not full:
                return jsonify({"ok": False, "error": f"no mission matches '{text}'"})
            grp = bot.mission_group(full)      # was hardcoded "User": stock BuiltIn missions silently no-opped with a success toast
            res = _send_cmd("set-next-mission", [grp, full, "7200"])
            return jsonify({"ok": True, "result": res, "info": f"next map -> {full}"})
        if name == "endmission":
            # Relay through the BOT, exactly like changemap below, instead of the old raw
            # set-time-remaining=5. That raw version dropped the mission clock straight into the
            # PLUGIN's timeout window with the scores level, which announced "it's a DRAW" - and
            # re-announced it every tick, because the game had no draw state to enter and so never
            # looked "ended". The bot owns the end now: it banks the match and opens a map vote, and
            # the vote's winner is what actually moves the mission on.
            _queue_admin({"action": "endmatch"})
            return jsonify({"ok": True, "info": "ending the match - opening the map vote"})
        if name == "changemap":                               # END current match + cut over to a chosen map NOW
            full = _resolve_mission(text)
            if not full:
                return jsonify({"ok": False, "error": f"no mission matches '{text}'"})
            # relay through the BOT (not _send_cmd) so it owns the cut-over + suppresses the auto map-vote
            _queue_admin({"action": "changemap", "name": full})
            return jsonify({"ok": True, "info": f"changing map -> {full} now"})
        if name == "grant":
            if sid:
                who, pts_s = sid, str(b.get("points", "")).strip()
            else:
                who, _, pts_s = text.rpartition(" ")
                who, pts_s = who.strip(), pts_s.strip()
            if not who or not pts_s:
                return jsonify({"ok": False, "error": "usage: grant <player> <points>"})
            pts = _finite(pts_s)
            if pts is None:
                return jsonify({"ok": False, "error": f"'{pts_s}' is not a number"})
            _queue_admin({"action": "grant", "query": who, "points": pts})
            return jsonify({"ok": True, "info": f"queued grant {pts:+g} -> {who}"})
        if name == "balance":
            _queue_admin({"action": "team", "verb": "balance", "sid": "", "faction": ""})
            return jsonify({"ok": True, "info": "queued team-balance pass"})
        if name == "aircraftlist":
            # The plugin, the bot relay and admin_team all accept this verb already - it simply had
            # no producer, so nothing could trigger it, including the panel note telling operators
            # to run it after a game update to refresh the aircraft catalogue.
            _queue_admin({"action": "team", "verb": "aircraftlist", "sid": "", "faction": ""})
            return jsonify({"ok": True, "info": "queued aircraftlist - result goes to the plugin log"})
        if name in ("setrank", "setfunds", "addfunds"):       # in-game rank / funds (relayed to the plugin)
            if sid:
                rsid, label = sid, b.get("name", sid)
                num_s = str(b.get("amount", b.get("points", ""))).strip()
            else:
                who, _, num_s = text.rpartition(" ")
                rsid, label = _resolve_player(who.strip())
                num_s = num_s.strip()
            if not rsid:
                return jsonify({"ok": False, "error": label if isinstance(label, str) else "no such player"})
            if not num_s:
                return jsonify({"ok": False, "error": f"usage: {name} <player> <number>"})
            if _finite(num_s) is None:
                return jsonify({"ok": False, "error": f"'{num_s}' is not a number"})
            _queue_admin({"action": "team", "verb": name, "sid": rsid, "faction": num_s})   # plugin reads the number from field 3
            return jsonify({"ok": True, "info": f"queued {name} {label} -> {num_s}"})
        if name in ("move", "join", "spec", "spectate", "team"):
            if name in ("spec", "spectate"):
                rsid, label = (sid, b.get("name", sid)) if sid else _resolve_player(text)
                if not rsid:
                    return jsonify({"ok": False, "error": label})
                _queue_admin({"action": "team", "verb": "spec", "sid": rsid, "faction": ""})
                return jsonify({"ok": True, "info": f"queued: {label} -> spectate"})
            if sid:
                fac = _faction_norm(b.get("faction", "")) or _faction_norm(text)
                rsid, label = sid, b.get("name", sid)
            else:
                toks = text.split()
                if len(toks) < 2:
                    return jsonify({"ok": False, "error": f"usage: {name} <player> <boscali|primeva>"})
                fac = _faction_norm(toks[-1])
                rsid, label = _resolve_player(" ".join(toks[:-1]))
            if not fac:
                return jsonify({"ok": False, "error": "faction must be boscali or primeva"})
            if not rsid:
                return jsonify({"ok": False, "error": label})
            _queue_admin({"action": "team", "verb": "move" if name in ("move", "team") else "join",
                          "sid": rsid, "faction": fac})
            return jsonify({"ok": True, "info": f"queued: {label} -> {fac}"})
        if name in ("swapteam", "forceteamswap"):   # admin: relayed to the plugin (targets a player)
            rsid, label = (sid, b.get("name", sid)) if sid else _resolve_player(text)
            if not rsid:
                return jsonify({"ok": False, "error": label if isinstance(label, str) else "no such player"})
            _queue_admin({"action": "team", "verb": name, "sid": rsid, "faction": ""})
            return jsonify({"ok": True, "info": f"queued {name} -> {label}"})
        if name == "copysid":
            return jsonify({"ok": True, "sid": sid})
        # Admin/manual kick: TellPlayer reason then RCON kick-player via the bot. Session kick
        # list STAYS (no auto-unkick). Game has no disconnect-dialog reason string — whisper only.
        if name in ("kick", "kick-player"):
            ksid = (args[0] if args else sid) or ""
            if not _SID_RE.match(ksid):
                return jsonify({"ok": False, "error": "usage: kick <steamId> [reason]"})
            reason = str(b.get("reason") or "").strip()
            if not reason and len(args) > 1:
                reason = " ".join(args[1:]).strip()
            reason = re.sub(r"[\x00-\x1f|]+", " ", reason).strip()[:160] or "kicked by admin"
            _queue_admin({"action": "admin_kick", "sid": ksid, "reason": reason,
                          "name": str(b.get("name") or "")[:64]})
            return jsonify({"ok": True,
                            "info": f"kick queued — in-game whisper + session-block ({reason}); no auto-unkick"})
        # Admin ban via bot so Moderation Reports gets a row (plugin + game ban list).
        if name in ("ban", "banlist-add"):
            bsid = (args[0] if args else sid) or ""
            if not _SID_RE.match(bsid):
                return jsonify({"ok": False, "error": "usage: ban <steamId> [reason]"})
            reason = str(b.get("reason") or "").strip()
            if not reason and len(args) > 1:
                reason = " ".join(args[1:]).strip()
            reason = re.sub(r"[\x00-\x1f|]+", " ", reason).strip()[:160] or "banned by admin"
            _queue_admin({"action": "ban_steamid", "sid": bsid, "reason": reason,
                          "name": str(b.get("name") or "")[:64]})
            return jsonify({"ok": True, "info": f"ban queued — Moderation log + plugin/game lists ({reason})"})
        # server wire command: WHITELIST to the palette-exposed CENTRE_SERVER_CMDS verbs (no raw
        # passthrough, no hidden ops verbs, no raw send-chat-message -- the 'say' branch owns chat).
        entry = next((e for e in bot.CENTRE_SERVER_CMDS if e[0] == name or e[1] == name), None)
        if (not entry or entry[0] in _HIDDEN_VERBS or entry[1] in _HIDDEN_VERBS
                or entry[1] == "send-chat-message"):
            return jsonify({"ok": False, "error": f"unknown command '{name}'"})
        wire = entry[1]
        # arg-shape gate: no control/newline/null chars (relay + downstream file framing), a real
        # SteamID / finite number where the verb takes one; zero-arg verbs drop stray palette text
        if any(any(ord(ch) < 0x20 for ch in a) for a in args):
            return jsonify({"ok": False, "error": "command arguments contain invalid characters"})
        if wire in ("kick-player", "unkick-player", "banlist-add", "banlist-remove"):
            if not args or not _SID_RE.match(args[0]):
                return jsonify({"ok": False, "error": f"usage: {entry[0]} <steamId>"})
            if wire != "banlist-add":                     # only ban takes a free-text reason after the sid
                args = args[:1]
        elif wire == "set-time-remaining":
            if len(args) != 1 or _finite(args[0]) is None:
                return jsonify({"ok": False, "error": "usage: settime <seconds>"})
        elif wire == "set-next-mission":
            if len(args) < 2 or (len(args) >= 3 and _finite(args[2]) is None):
                return jsonify({"ok": False, "error": "usage: nextmap <group> <name> <maxTime>"})
        elif not entry[2]:                                # verb takes no arguments
            args = []
        res = _send_cmd(wire, args)
        ok = True
        info = None
        if isinstance(res, dict) and "code" in res:
            ok = res.get("code") == 2000
        return jsonify({"ok": ok, "result": res, "info": info})
    except Exception as e:                               # noqa: BLE001
        return jsonify({"ok": False, "error": str(e)}), 500


@app.route("/api/power", methods=["POST"])
def api_power():
    sig = str((request.get_json(force=True, silent=True) or {}).get("signal") or "").strip()
    if sig not in ("start", "stop", "restart", "kill"):   # gate BOTH power paths; an unknown sig fell through _local_power -> launched a duplicate server
        return jsonify({"ok": False, "message": "bad signal"})
    if sig == "restart" and not _is_local_power():        # panel restart -> safe stop->kill-if-hung->start (never a bare 'restart' that can hang)
        ok, msg = _pt_safe_restart()
    else:
        ok, msg = (_local_power(sig) if _is_local_power() else _pt_power(sig))
    return jsonify({"ok": ok, "message": msg})


@app.route("/api/resources")
def api_resources():
    return jsonify(_local_resources() if _is_local_power() else _pt_resources())


@app.route("/api/schedule")
def api_schedule_get():
    return jsonify({"items": sorted(_read_schedule(), key=lambda i: i.get("when", ""))})


@app.route("/api/schedule", methods=["POST"])
def api_schedule_add():
    """Add a scheduled restart/update. The BOT polls schedule.json and executes at `when`
    (a server restart via the guarded deploy pipeline), warning players beforehand."""
    b = request.get_json(force=True, silent=True) or {}
    typ = str(b.get("type") or "").strip().lower()
    when = str(b.get("when") or "").strip().replace("T", " ")
    desc = str(b.get("desc") or "").strip()[:200]
    if typ not in ("restart", "update"):
        return jsonify({"ok": False, "error": "type must be 'restart' or 'update'"})
    try:
        t = time.strptime(when[:16], "%Y-%m-%d %H:%M")
        when = time.strftime("%Y-%m-%d %H:%M", t)
    except ValueError:
        return jsonify({"ok": False, "error": "pick a valid date & time"})
    if time.mktime(t) < time.time() - 60:
        return jsonify({"ok": False, "error": "that time is in the past"})
    if typ == "update" and not desc:
        return jsonify({"ok": False, "error": "add a note of what's being updated"})
    items = _read_schedule()
    item = {"id": "sch_" + format(int(time.time() * 1000), "x"), "type": typ,
            "when": when, "desc": desc, "status": "pending",
            "created": time.strftime("%Y-%m-%d %H:%M")}
    items.append(item)
    _write_schedule(items)
    return jsonify({"ok": True, "item": item})


@app.route("/api/schedule/delete", methods=["POST"])
def api_schedule_del():
    iid = ((request.get_json(force=True, silent=True) or {}).get("id") or "").strip()
    _write_schedule([i for i in _read_schedule() if i.get("id") != iid])
    return jsonify({"ok": True})


if __name__ == "__main__":
    # SINGLE-INSTANCE GUARD (2026-07-27): mirrors the bot's - see no_mapvote_bot.py.
    try:
        import msvcrt as _msvcrt
    except ImportError:
        _msvcrt = None
    if _msvcrt is not None:
        # The lock belongs to THIS INSTALL. Never fall back to the shared
        # ~/.nuke-option-toolkit: launched without NOST_DATA_DIR (ops scripts do this
        # when the data dir isn't found, and a fresh install can too), two DIFFERENT
        # servers would contend for one webcc.lock and the second would refuse to start.
        _lock_dir = os.environ.get("NOST_DATA_DIR") or os.path.join(HERE, ".nost-data")
        _lock_fh = None
        try:
            os.makedirs(_lock_dir, exist_ok=True)
            _lock_fh = open(os.path.join(_lock_dir, "webcc.lock"), "a")
            _lock_fh.seek(0)
        except OSError as _lock_err:
            # FAIL OPEN: not being able to OPEN the lock file (permissions, full disk,
            # AV/OneDrive holding it, unreachable data dir) is not evidence of a second
            # web CC. Refusing here is a silent non-start with exit 0. Warn and run; a
            # real duplicate still fails loudly when app.run() cannot bind the port.
            print(f"[webcc] singleton lock file unavailable ({_lock_err}) - starting anyway "
                  f"(guard degraded to the port bind).")
            _lock_fh = None
        if _lock_fh is not None:
            try:
                # Only a failed LOCK means someone else holds it.
                _msvcrt.locking(_lock_fh.fileno(), _msvcrt.LK_NBLCK, 1)
            except OSError:
                print("[webcc] another web command centre is already running for this install - exiting.")
                raise SystemExit(0)
    _shown = "127.0.0.1" if HOST in ("127.0.0.1", "localhost") else HOST
    print(f"[webcc] Nuke Option web command centre -> http://127.0.0.1:{PORT}"
          + (f"  (bound {HOST})" if HOST not in ("127.0.0.1", "localhost") else "  (loopback only; set web.host/NOCC_HOST=0.0.0.0 for LAN)"))
    if AUTH_TOKEN:
        print("[webcc] auth: ON (X-NOCC-Token required for POST)")
    else:
        print("[webcc] auth: off (optional: web.auth_token / NOCC_AUTH_TOKEN)")
    _pt_load()
    print(f"[webcc] pterodactyl: {'ready (' + (_pt.get('server') or '') + ')' if _pt.get('server') else 'NOT configured - ' + str(_pt.get('err'))}")
    app.run(host=HOST, port=PORT, threaded=True)
