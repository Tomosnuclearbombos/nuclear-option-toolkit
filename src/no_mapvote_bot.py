#!/usr/bin/env python3
"""
Nuclear Option - automated map-vote bot (mod-free).

Two channels, because the console and the remote-command port are separate:
  ACTIONS -> native TCP remote-command port (-ServerRemoteCommands <port>):
             send-chat-message, get-mission-time, set-next-mission, set-time-remaining
  VOTES   -> read player chat out of the GPanel console output and tally it

Flow (log-driven, not time-polled):
  IDLE   : tail the console; when a "[DedicatedServerManager] Mission complete"
           line appears (MISSION_END_RE) -- which also covers missions that end
           early -- post the rank roster and open the next-map vote. Players can
           also start a vote any time with !votemap.
  VOTING : read chat, record each player's choice (last vote wins). When the
           window closes: pick the winner, queue it as the next mission, cut the
           current mission short to roll over, and announce the result.

The ONLY piece you must wire to your setup is ConsoleSource.poll() -- how the bot
gets new console lines. A local-file tail is provided (good for testing or if you
can run the bot where the log lives). For remote reading over SFTP or a panel
websocket, swap poll() -- see the note on that class.

Quick check with no setup:   python no_mapvote_bot.py --selftest
Run for real:                python no_mapvote_bot.py
Command centre (unified):    commandcentre.bat  (single-window TUI: live console +
                             players table + activity feed + a command console;
                             reads the feed this bot publishes - see the
                             "command-centre dashboard feed" section below)
Command centre (legacy):     python no_mapvote_bot.py --centre   (or centre.bat)
"""

import collections
import json
import math
import os
import random
import re
import shlex
import shutil
import socket
import html as _html
import subprocess
import sys
import threading
import time
import traceback
import urllib.request
from collections import Counter

# Windows std streams default to cp1252, which raises UnicodeEncodeError on player
# names with non-Latin-1 glyphs (e.g. □ U+25A1) and mis-decodes piped/pasted UTF-8
# input. A failed print would otherwise crash main(). Force UTF-8 + replacement so
# logging can never take the bot down and command-centre input decodes cleanly.
for _stream in (sys.stdout, sys.stderr, sys.stdin):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError, OSError):
        pass

# ----------------------------------------------------------------------------
# CONFIG  -- adjust these
# ----------------------------------------------------------------------------

# Optional config written by the installer (~/.nuke-option-toolkit/). If a value is present
# there it wins; otherwise we fall back to the existing env var, then the default — so a
# classic run.bat (env-var) setup is completely unaffected. Set NOST_DATA_DIR to relocate.
import json as _json
_TK_DIR = os.environ.get("NOST_DATA_DIR") or os.path.join(os.path.expanduser("~"), ".nuke-option-toolkit")
def _tk_load(_name):
    try:
        with open(os.path.join(_TK_DIR, _name), encoding="utf-8") as _f:
            return _json.load(_f)
    except (OSError, ValueError):
        return {}
_TK_CFG = _tk_load("config.json")
_TK_SEC = _tk_load("secrets.json")
def _cfg(dotted, env=None, default=""):
    """ENV wins (the live run.bat setup), then config.json/secrets.json, then default — so a
    classic env-var install is NEVER overridden by a stray config file."""
    if env:
        _v = os.environ.get(env)
        if _v not in (None, ""):
            return _v
    for _src in (_TK_SEC, _TK_CFG):
        _cur = _src
        for _k in dotted.split("."):
            _cur = _cur.get(_k) if isinstance(_cur, dict) else None
        if _cur not in (None, ""):
            return _cur
    return default

RCMD_HOST = _cfg("server.rcmd_host", "NO_RCMD_HOST", "your-host.example.net")   # relay/server host
RCMD_PORT = int(_cfg("server.rcmd_port", "NO_RCMD_PORT", "5550") or 5550)

# Your community's Discord invite (e.g. "discord.gg/yourserver"). Shown by !discord and !link.
# Empty (the default) = !discord answers honestly that no Discord is configured.
DISCORD_INVITE = str(_cfg("discord.invite", "NO_DISCORD_INVITE", "") or "").strip()

# Mission pool. These two lists are for CUSTOM weather/time EDITS of the co-op ops
# (Group "User" mission files you upload yourself, named "<Op> Co-op as <side> - <variant>").
# They ship EMPTY: the stock ballot draws from BUILTIN_COOP_MISSIONS + the PvP modes below.
# Add your own variant names here (or upload them via the webcc Missions modal) to feed the
# co-op half of the ballot with them. Players vote by typing the number shown for each option.
MISSION_GROUP    = "User"
MISSION_MAX_TIME = 10800         # seconds (3h) -- matches the server's MissionRotation

ESCALATION_MISSIONS = []
TERMINAL_CONTROL_MISSIONS = []
# Base PvP missions (group "User", verified on the server 2026-06-23). Kept SEPARATE
# from the coop lists above so the random co-op map-vote pool is unchanged, but the
# command centre's `nextmap` autocomplete/exact-match can reach them (so `nextmap
# escalation` loads the PvP "Escalation", not "Escalation Co-op as BDF ..."). The
# upcoming 30+-player PvP-only vote will draw from this list. (Bare "Terminal Control"
# does NOT exist on the server - only its co-op variants.)
PVP_MISSIONS = [
    "Escalation",
    "Terminal Control",
    "Altercation",
    "Confrontation",
    "Domination",
    "Carrier Duel",              # stock naval mode; Key CONFIRMED live 2026-07-02 (console: set-next-mission [BuiltIn,Carrier Duel] -> loaded)
]

# Stock CO-OP missions (Tomo 2026-07-02 + wiki). The game ships co-op faction variants of the
# big ops as their own missions — "<Op> Co-op as BDF/PALA" — and custom User missions are
# weather/time EDITS of these (name + " - Dawn" etc.), so the bare names here are BUILT-IN and
# must never be confused with the User variants. Breakout is a CO-OP mission (wiki: "Challenging
# co-op mission where PALA provokes BDF navy"), NOT PvP. "13. Reprisal" is the only
# multiplayer-capable numbered Scenario.
BUILTIN_COOP_MISSIONS = [
    "Escalation Co-op as BDF",
    "Escalation Co-op as PALA",
    "Terminal Control Co-op as BDF",
    "Terminal Control Co-op as PALA",
    "Breakout",
    "13. Reprisal",
]

# Candidate rotation Keys for missions whose exact server identity is unconfirmed, best guess
# first (in-game display name == wire Name for every confirmed mission so far, e.g. Carrier Duel).
# _resolve_mission_key() tries these in order against the live server (set -> read back the
# override; an invalid Key never 'takes') and caches the accepted one in mission_keys.json.
# Until a mission's Key is verified it is LISTED in the pool but kept OFF auto-ballots, so a
# map vote can never silently no-op on it. (Breakout is exempt: same BuiltIn group as the other
# long-offered ops.)
MISSION_KEY_CANDIDATES = {
    "13. Reprisal": [("BuiltIn", "13. Reprisal"), ("Default", "13. Reprisal"),
                     ("BuiltIn", "Reprisal"),     ("Default", "Reprisal")],
    "Escalation Co-op as BDF":          [("BuiltIn", "Escalation Co-op as BDF"),          ("Default", "Escalation Co-op as BDF")],
    "Escalation Co-op as PALA":         [("BuiltIn", "Escalation Co-op as PALA"),         ("Default", "Escalation Co-op as PALA")],
    "Terminal Control Co-op as BDF":    [("BuiltIn", "Terminal Control Co-op as BDF"),    ("Default", "Terminal Control Co-op as BDF")],
    "Terminal Control Co-op as PALA":   [("BuiltIn", "Terminal Control Co-op as PALA"),   ("Default", "Terminal Control Co-op as PALA")],
}

# The curated OFFICIAL mission pool this server ships (every mission in the stock MissionRotation). Any
# mission present/enabled BEYOND this set = unofficial (uploaded or Steam Workshop). The mission audit flags
# unofficial-enabled or edited-official missions so owners can see when the pool diverges from stock.
OFFICIAL_MISSIONS = (set(ESCALATION_MISSIONS) | set(TERMINAL_CONTROL_MISSIONS)
                     | set(PVP_MISSIONS) | set(BUILTIN_COOP_MISSIONS))

# Weather/time variants treated as "dark". A single ballot may contain at most
# MAX_DARK_PER_VOTE of these, so at least one of the four options is always a
# brighter map (Afternoon / Clear Skies / Day / Dawn). Note: Dawn is NOT dark.
DARK_VARIANTS     = ("Night", "Thunderstorm", "Overcast", "Dusk")
MAX_DARK_PER_VOTE = 3

# Two FIXED extra options appended to every ballot (keys 5-6): the stock built-in
# PvP Escalation / Terminal Control missions (Group "BuiltIn"). These are always
# the same regular mission. Chat surfaces outside the vote use the shared "[PVP] "
# prefix via pvp_prefix()/mission_display(). Vote ballots use a short kind suffix
# only ([PvE]/pastel blue / [PvP] red) — no flavor descriptors (dogfight / etc.).
PVP_OPTIONS = [
    ("BuiltIn", "Escalation",       "Escalation"),
    ("BuiltIn", "Terminal Control", "Terminal Control"),
    ("BuiltIn", "Altercation",      "Altercation"),
    ("BuiltIn", "Confrontation",    "Confrontation"),
    ("BuiltIn", "Domination",       "Domination"),
    ("BuiltIn", "Carrier Duel",     "Carrier Duel"),
]

# PvP FAMILIES. A family is a base mode plus its time-of-day variants ("Escalation - Dawn",
# "Escalation - Night", ...). pvp_mode "family" round-robins the slots across the families and draws
# a DIFFERENT member for each, so two Escalation slots are two different times of day, never a repeat.
# SPLIT 2026-08-22 (owner, the S1 PvP ballot): Carrier Duel and Domination each get their OWN family
# now - with pvp_count=6 the round-robin yields exactly 2 Escalation + 2 Terminal Control +
# 1 Carrier Duel + 1 Domination. (The old shared "Carrier / Domination" bucket was the 07-31 S2
# layout and could produce 2 Carrier + 0 Domination.)
PVP_FAMILY_ORDER = ("Escalation", "Terminal Control", "Carrier Duel", "Domination")
PVP_FAMILY_BASES = {
    "Escalation":       ("Escalation",),
    "Terminal Control": ("Terminal Control",),
    "Carrier Duel":     ("Carrier Duel",),
    "Domination":       ("Domination",),
}
# The time-of-day suffixes we generate. A variant only counts if it is actually registered AND enabled,
# so this list can safely name variants that do not exist yet on a given server.
PVP_VARIANT_SUFFIXES = ("", " - Dawn", " - Dusk", " - Clear Skies", " - Night", " - Thunderstorm")


def pvp_family_members(family, exclude=()):
    """Every enabled, votable mission in a family: each base plus its registered variants."""
    out = []
    for base in PVP_FAMILY_BASES.get(family, ()):
        for suffix in PVP_VARIANT_SUFFIXES:
            name = base + suffix
            if name in exclude or name in out:
                continue
            if mission_enabled(name) and name in _votable_names():
                out.append(name)
    return out


# Current ballot, rebuilt each vote by open_vote(). Keys "1".."6" map to
#   (group, mission_name, max_time_seconds, friendly_label)
# 1-2 = random Escalation co-op, 3-4 = random Terminal Control co-op, 5-6 = PvP.
VOTE_OPTIONS = {}

# ── FIX 3: TWO source-of-truth vote-timing knobs (baked defaults; the live values are loaded from the
# deploy-protected .nost-data/votemap_timing.json below). VOTE_DURATION and APPROVAL_DURATION are ALIASES
# derived from MAP_VOTE_DURATION — there is no independent 60 any more. The effective post-mission delay is
# DERIVED = MAP_VOTE_DURATION + POST_VOTE_MAP_CHANGE_DELAY, so the map change fires exactly that many seconds
# after the ballot closes and the raw delay can never be shorter than the vote (the owner's breakage).
MAP_VOTE_DURATION          = 30   # single ballot length (s) for BOTH the end-of-match vote AND !votemap
POST_VOTE_MAP_CHANGE_DELAY = 15   # seconds AFTER the ballot closes before the winning map loads (floor >=5)
VOTE_DURATION        = MAP_VOTE_DURATION   # alias (player-initiated !votemap ballot length)
APPROVAL_DURATION    = MAP_VOTE_DURATION   # alias (!votemap yes/no accept-poll length)
ROLLOVER_SECONDS     = 10    # cut current mission to this many seconds after a vote
POST_VOTE_COOLDOWN   = 90    # don't open another vote for this long after applying one
CONSOLE_POLL_INTERVAL = 1.5  # how often to read new console lines (SFTP-friendly)

# --- Console source: SFTP tail. Credentials come from environment variables so
# no secrets live in this file. Set them in your shell before running:
#   export NO_SFTP_HOST=your-sftp-host.example.net
#   export NO_SFTP_PORT=2022
#   export NO_SFTP_USER=your-username
#   export NO_SFTP_PASS='your-new-password'      # rotate the one you pasted!
#   export NO_SFTP_LOGPATH=/path/to/remote/console.log
SFTP_HOST     = _cfg("server.sftp_host", "NO_SFTP_HOST", "")
try:
    SFTP_PORT = int(str(_cfg("server.sftp_port", "NO_SFTP_PORT", "2022")).strip())
except ValueError:
    print("[bot] sftp port is not a number; falling back to 2022")
    SFTP_PORT = 2022
SFTP_USER     = _cfg("server.sftp_user", "NO_SFTP_USER", "")
SFTP_PASS     = _cfg("sftp_pass", "NO_SFTP_PASS", "")           # secrets.json
SFTP_LOG_PATH = _cfg("server.log_path", "NO_SFTP_LOGPATH", "")  # remote path to the console log

# Own-PC installs set a LOCAL console path; if present the bot tails it directly instead of
# over SFTP (and points commands at 127.0.0.1). Empty => classic remote/SFTP behaviour.
LOCAL_CONSOLE_PATH = _cfg("server.local_console_path", "NO_LOCAL_CONSOLE", "")
CONSOLE_LOG_PATH = LOCAL_CONSOLE_PATH or "console.log"


# get-mission-time response field that holds the seconds remaining. Leave None to
# auto-search for a key containing "remain". Run once with DEBUG=True, look at the
# printed response, and set this to the exact field name if auto-detect misses.
MISSION_TIME_KEY = None      # e.g. "remaining" or "timeLeft"

DEBUG = True                 # print raw command responses (confirm field names)

# ----------------------------------------------------------------------------
# Custom server-rank system
# ----------------------------------------------------------------------------
SHOW_RANK_ON_CHAT  = False   # plugin shows [Name - Rank] inline now; no separate rank tag
RANK_CHAT_THROTTLE = 0       # min seconds between rank lines for the same player (0 = every message)
JOIN_POLL_INTERVAL = 5       # how often to refresh players + announce new joiners (seconds)
LOG_CONVERSATION   = True    # show player chat ([CHAT]) and bot replies ([BOT]) in activity.log
                             # set False for just the curated events (joins/votes/captures/wins)

# Real per-player score from the NukeStats BepInEx plugin (see NukeStats/README.md).
# The plugin emits "[NOSTATS] {json}" lines into console.log carrying each player's
# REAL in-game score; ranks track the accumulated real score, and win/place points
# come from plugin `award` frames. False = stop banking plugin score/awards into
# ranks (kill/win/meta ingest still flows for the feed and W/L tallies).
USE_PLUGIN_SCORE   = True
PLUGIN_RANK_PUSH_INTERVAL = 120   # how often to push the chat-rank file to the container (s)

# Rank ladder rows: (points needed, full name, abbreviation, colour). SHIPS EMPTY = the rank
# ladder feature is OFF: no rank tags, no rank-up announcements, and !rank/!ranks report that
# no ladder is configured (points, W/L and the leaderboard still accrue and work). Add ranks in
# the webcc "Ranks" modal (Settings menu) to turn the feature on - a non-empty ladder IS the
# on-switch. The live ladder is persisted to rank_ladder.json; load_rank_ladder() rebuilds
# RANKS from it at startup (fail-open to this empty DEFAULT).
RANKS = []
DEFAULT_RANKS           = list(RANKS)
# Multi-color Tomo format: white header/arrow, old ladder colour on name+old abbr,
# new colour on "Full Rank - ABBR". Example: ** RANK UP ** Dez Tag OFFCDT → Pilot Officer - PLTOFF
# Placeholders: {name} {rank} {abbr} {color} {old_abbr} {old_color}. Prestige public announce unchanged.
# 1.3.15: second person, because the line is delivered privately to the player who ranked up.
# {name} is still substituted (and validate still REQUIRES it) so an owner-authored template can put
# the name back; the default simply does not need it.
DEFAULT_RANKUP_TEMPLATE_V2 = (
    "<color=#FFD200>You've ranked up!</color> "
    "<color={old_color}>{old_abbr}</color> "
    "<color=#FFFFFF>-></color> "
    "<color={color}>{rank} - {abbr}</color>"
)
DEFAULT_RANKUP_TEMPLATE = (
    "<color=#FFFFFF>** RANK UP **</color> "
    "<color={old_color}>{name} {old_abbr}</color> "
    "<color=#FFFFFF>-></color> "
    "<color={color}>{rank} - {abbr}</color>"
)
_LEGACY_RANKUP_TEMPLATES = (
    DEFAULT_RANKUP_TEMPLATE,   # 1.3.15: the third-person default migrates to the second-person V2
    "<color={color}>** RANK UP ** {name} is now {rank} ({abbr})!</color>",
    # pre-dash multi-color (was `{rank} {abbr}` without " - ")
    "<color=#FFFFFF>** RANK UP **</color> "
    "<color={old_color}>{name} {old_abbr}</color> "
    "<color=#FFFFFF>-></color> "
    "<color={color}>{rank} {abbr}</color>",
)
DEFAULT_RANKUP_TEMPLATE = DEFAULT_RANKUP_TEMPLATE_V2   # 1.3.15: private line, so second person
RANKUP_TEMPLATE         = DEFAULT_RANKUP_TEMPLATE
DEFAULT_PRESTIGE_TEMPLATE = "[{abbr} - {n}*]"   # rank-tag inner for a prestiged player: {abbr} {rank} {n} (n = prestige count, >=1)
PRESTIGE_TEMPLATE         = DEFAULT_PRESTIGE_TEMPLATE
RANK_LADDER_FILE        = os.path.join(os.path.dirname(os.path.abspath(__file__)), "rank_ladder.json")
RANK_FILE    = os.path.join(os.path.dirname(os.path.abspath(__file__)), "ranks.json")
RANK_DATA    = {}            # steamid -> {"name": str, "points": int}
PLAYER_NAMES = {}            # steamid -> last-seen display name (for chat rank lines)
WELCOMED        = set()      # sids welcomed this session (cleared on leave) - dedups the join welcome
WELCOME_QUEUE   = {}         # sid -> (deadline_ts, name, attempts): delayed welcomes, dropped if they leave first
WELCOME_MAX_ATTEMPTS = 3     # give up after this many relay failures for one player (see the drain in main())
# How recently the plugin must have reported a player for them to be counted in the match-end W/L tally.
# Generous on purpose: missing a real player's win is worse than including someone who left minutes ago.
EOM_PRESENCE_WINDOW_S = 300.0
WELCOME_DRAIN_PER_TICK = 3   # cap the blocking relay round-trips one loop pass may spend on welcomes
WELCOME_DELAY   = 5.0        # seconds to wait after first-seen before welcoming (let their client load)
WELCOME_FALLBACK_NAME = "Pilot"   # display-only stand-in when Steam will never name a player (see _persona_failed)
ADMIN_SIDS      = set(os.environ.get("NO_ADMIN_SIDS", "").split()) or set(_TK_CFG.get("server", {}).get("admin_sids") or _TK_CFG.get("admin_sids") or []) or {"76500000000000000"}   # NO_ADMIN_SIDS env -> config -> placeholder (matches no real SteamID)

# Per-match tracking: a match_history.json (one record per match: mission, result,
# duration, per-player points/captures/won) and an append-only points_ledger.jsonl
# (one line per point award - the audit trail). ranks.json (lifetime totals) is the
# source of truth and is unchanged; these are additive.
_BASE_DIR          = os.path.dirname(os.path.abspath(__file__))
MATCH_HISTORY_FILE = os.path.join(_BASE_DIR, "match_history.json")
LEDGER_FILE        = os.path.join(_BASE_DIR, "points_ledger.jsonl")
SCHEDULE_FILE      = os.path.join(_BASE_DIR, "schedule.json")        # web-CC scheduled restarts/updates (this bot executes them)
SCHED_WARN         = [300, 60]    # warn players in-chat this many seconds before a scheduled restart/update
_sched_warned      = {}           # item id -> set(thresholds already announced) (in-memory; ok to forget on restart)
CUR_MATCH          = None    # active match accumulator (see match_*), None between matches
SCORE_ACCUM        = {}       # sid -> [name, total in-game score gained this match]; one ledger
                             # "score" line per player flushed at match_finalize (snaps are too
                             # frequent to ledger individually). See ledger_award / _flush_score_accum.
GAIN_CLAMP_MAX     = 1000.0   # hard upper clamp on a single snap's credited gain (defence-in-depth vs the
                             # 2026-06-24 score-explosion class): the SPIKE alert still fires on the RAW gain,
                             # but never more than this is actually banked into points in one tick.
SPIKE_THRESHOLD    = 1000.0   # a single snap gain above this is logged + flagged live (exploit tripwire,
                             # cf. the 2026-06-24 score-explosion). Informational only (pts:0 in ledger).
CURRENT_MISSION    = "(unknown)"  # name of the mission currently running (for match records)
# Mission-time warnings: announce when remaining time crosses these thresholds (once each per mission).
WARN_THRESHOLDS = [3600, 1200, 600, 300, 60]   # 60 / 20 / 10 / 5 / 1 min remaining
_warnings_fired = set()                          # thresholds already announced this mission
_warn_mission   = None                           # mission name the fired-set belongs to (reset on change)

# 'Stay for the next match' reminders (keyed to mission elapsed time, mtime[0]). All
# per-mission state resets when a new mission starts (detected by the elapsed clock
# jumping back to ~0). See check_match_milestones().
STAY_MARKS         = [6300, 7500, 8700]           # 105 / 125 / 145 min ELAPSED -> 'stay for next match'
_ms_mission        = None                         # mission the milestone state belongs to
_ms_last_elapsed   = 0.0                          # previous elapsed reading (detect the reset to ~0)
_ms_cycle_at       = 0.0                          # wall-time a milestone cycle last opened (anti-double)
_ms_stay_fired     = set()                        # which STAY_MARKS have fired this mission

# ── VANILLA-ABLE PvP: per-source bonus-award toggles (webcc flips + persists) ────────────────────────
# PRIMARY DESIGN GOAL (owner): run the PvP server as close to vanilla as possible. EVERY bonus-point
# source gets its OWN independent on/off toggle, each defaulting ON. When a source is OFF the bot neither
# GRANTS that award nor posts its announce line. CRITICAL: turning awards off must NOT affect rank
# DISPLAY, !rank / !leaderboard, rank-ups from ALREADY-earned points, or cross-server carry -- those read
# the accumulated RANK_DATA / shared rank files, independent of whether NEW points are being granted. So
# all-awards-off + all-messages-off == a vanilla server that still shows + carries ranks.
#   file: award_config.json   shape: {"win_points":bool}
AWARD_CONFIG_FILE = os.path.join(_BASE_DIR, "award_config.json")
_AWARD_DEFAULTS = {
    "win_points":     True,   # the plugin's match-end win + placement points (t=="award")
}
_award_cfg = dict(_AWARD_DEFAULTS)


def load_award_cfg():
    global _award_cfg
    cfg = dict(_AWARD_DEFAULTS)
    try:
        with open(AWARD_CONFIG_FILE, encoding="utf-8") as f:
            raw = json.load(f)
        if isinstance(raw, dict):
            for k in _AWARD_DEFAULTS:
                if k in raw:
                    cfg[k] = bool(raw[k])
    except (OSError, ValueError):
        pass
    _award_cfg = cfg


def save_award_cfg():
    try:
        tmp = AWARD_CONFIG_FILE + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(_award_cfg, f, indent=1)
        os.replace(tmp, AWARD_CONFIG_FILE)
    except OSError as e:
        print(f"[awards] save failed: {e}")


def award_on(key):
    """Is this bonus-point source currently enabled? Unknown key -> True (fail-open: never silently
    suppress an award the config doesn't know about)."""
    return bool(_award_cfg.get(key, True))


def set_award_toggle(key, on):
    """webcc setter for one bonus-source toggle. Returns True on success."""
    if key not in _AWARD_DEFAULTS:
        return False
    _award_cfg[key] = bool(on)
    save_award_cfg()
    return True


def award_toggles_state():
    """Rows for the webcc 'Vanilla / awards' card: each source's key + current on/off + a label."""
    labels = {
        "win_points":     "Win / placement points",
    }
    return {"awards": [{"key": k, "label": labels[k], "on": award_on(k)} for k in _AWARD_DEFAULTS]}


load_award_cfg()

# Real-score ingest (from the NukeStats plugin's [NOSTATS] lines). Lifetime points now
# come from the plugin's match-end AWARD events (win + placement bonuses), applied to
# ranks.json. LIVE_SCORE/STATS_META are per-match caches for the feed + W/L tally only.
LIVE_SCORE         = {}      # steamid -> latest in-match PlayerScore (display only)
STATS_META         = {}      # steamid -> {"name","faction","rank","teamkills"} (this match)
# Vanilla faction.color hexes sampled by the plugin (faction_colours NOSTATS).
# RAW loud tint for WebCC map CSS only — NOT join paint.
# Live samples: PALA #FFB800 / BDF #A76BFF.
FACTION_COLOURS    = {"pala": "#FFB800", "bdf": "#A76BFF"}
# ChatNameColour (= GetTextColor allChat) — muted join paint.
# Live samples from console joins (S2): PALA #FFDC80 / BDF #D3B5FF.
CHAT_FACTION_COLOURS = {"pala": "#FFDC80", "bdf": "#D3B5FF"}
FACTION_COLOURS_FILE = os.path.join(_BASE_DIR, "faction_colours.json")

def _load_faction_colours():
    """Restore last-sampled raw + chat-tint hexes across restarts before the next NOSTATS."""
    global FACTION_COLOURS, CHAT_FACTION_COLOURS
    try:
        if not os.path.isfile(FACTION_COLOURS_FILE):
            return
        with open(FACTION_COLOURS_FILE, "r", encoding="utf-8") as f:
            d = json.load(f)
        if not isinstance(d, dict):
            return
        for key, store in (("pala", FACTION_COLOURS), ("bdf", FACTION_COLOURS),
                           ("pala_chat", CHAT_FACTION_COLOURS), ("bdf_chat", CHAT_FACTION_COLOURS)):
            v = (d.get(key) or "").strip()
            if not (v.startswith("#") and len(v) == 7):
                continue
            dest_key = "pala" if key.startswith("pala") else "bdf"
            store[dest_key] = v
    except Exception:
        pass

def _save_faction_colours():
    try:
        tmp = FACTION_COLOURS_FILE + ".tmp"
        payload = {
            "pala": FACTION_COLOURS.get("pala"),
            "bdf": FACTION_COLOURS.get("bdf"),
            "pala_chat": CHAT_FACTION_COLOURS.get("pala"),
            "bdf_chat": CHAT_FACTION_COLOURS.get("bdf"),
        }
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(payload, f, separators=(",", ":"))
        os.replace(tmp, FACTION_COLOURS_FILE)
    except Exception as e:
        print(f"[colour] save faction_colours failed: {e}")

def apply_faction_colours(obj):
    """Plugin samples: raw faction.color → WebCC; pala_chat/bdf_chat → join tint."""
    global FACTION_COLOURS, CHAT_FACTION_COLOURS
    changed = False
    for key in ("pala", "bdf"):
        v = (obj.get(key) or "").strip()
        if v.startswith("#") and len(v) == 7 and FACTION_COLOURS.get(key) != v:
            FACTION_COLOURS[key] = v
            changed = True
    for src, key in (("pala_chat", "pala"), ("bdf_chat", "bdf")):
        v = (obj.get(src) or "").strip()
        if v.startswith("#") and len(v) == 7 and CHAT_FACTION_COLOURS.get(key) != v:
            CHAT_FACTION_COLOURS[key] = v
            changed = True
    if changed:
        print(f"[colour] raw PALA={FACTION_COLOURS.get('pala')} BDF={FACTION_COLOURS.get('bdf')}; "
              f"chat-tint (KF/join) PALA={CHAT_FACTION_COLOURS.get('pala')} "
              f"BDF={CHAT_FACTION_COLOURS.get('bdf')}")
        _save_faction_colours()

_load_faction_colours()
POS                = {}      # steamid -> (x, z, ts, kind, heading|None, ...): Occupied airframe only (plugin PosTick ~0.5s)
POS_TRAIL          = {}      # steamid -> deque[(ts,x,z,h)] last ~24 PosTick samples (emit unix ts) for WebCC delayed lerp
POS_TRAIL_MAX      = 24      # ~12s at 0.5Hz — enough for MAP_DELAY_S=2 + keep window
DOWNED             = {}      # steamid -> death ts: set on down/life death/eject → map ✝; sticky until far-jump respawn (never time-expire)
DEATH_POS          = {}      # steamid -> (x, z) at death: near-wreck POS must not clear DOWNED / revive corpse
_DOWNED_LOCKOUT_S  = 1.0     # ignore all POS briefly so ✝ sticks on the death frame
_DOWNED_NEAR_M     = 500.0   # within this of DEATH_POS = still dead (wreck / same pad); >= = new sortie
_POS_INWORLD_S     = 5.5     # POS older than this (and not DOWNED) = out of aircraft -> hide blip (plugin emits every 2s post-throttle; 2.5 flapped live blips hidden)
_recent_kill       = {}      # victim sid -> {weapon,munition,ts}: what downed them (from kill/down events; feeds the [KILL] activity line's weapon naming)
_life_dedup           = {}   # victim sid -> ts of the last COUNTED life frame: replay guard (mirrors _splash_dedup for kills)

# ── naming a kill honestly ────────────────────────────────────────────────────────────────────────
# The plugin sends TWO name fields on a kill and they are NOT interchangeable:
#   "w"      = the damaging UNIT's name  -> the AEROPLANE ("Alkyon AB-4"), always present
#   "weapon" = killWeapon                -> the MUNITION ("GPO-500"), but it FALLS BACK to the
#              aeroplane name whenever the launch-tracking map has no recent match for that killer.
# So a bare weapon string cannot be trusted to be a weapon. Every airframe the game ships is listed
# here (from the live `aircraftlist` dump, same source as the Web CC catalogue), so a value that
# matches one is known to be the plane and the report can say "weapon not identified" instead of
# quietly presenting a bomber as if it were the bomb. That single distinction is the whole reason
# the moderation feed has been reading "Alkyon direct" - it was naming the aircraft, not the weapon.
AIRFRAME_NAMES = frozenset(n.casefold() for n in (
    "CI-22 Cricket", "T/A-30 Compass", "VT-7 Vagrant", "UH-90 Ibis", "SAH-46 Chicane",
    "A-19 Brawler", "FS-12 Revoker", "FS-20 Vortex", "VL-49 Tarantula", "KR-67 Ifrit",
    "EW-25 Medusa", "SFB-81 Darkreach", "Alkyon AB-4", "??? (UFO)",
    # The short CODES as well, from the Web CC catalogue's `ins` column. Deriving them from the labels
    # does not work for these two: the prefix rule takes the FIRST word, which is "Alkyon" (not AB-4)
    # and "???" (not UFO) - so the Alkyon, the very aircraft this whole change is named after, was
    # being missed when the plugin reported it by its bare code.
    "AB-4", "UFO",
    # And the BARE NICKNAMES. The wire carries the full "CODE Nickname" label today - verified against
    # the real logs, which show VT-7 Vagrant / A-19 Brawler / CI-22 Cricket / SFB-81 Darkreach - so these
    # are belt-and-braces, not a fix. They cost nothing and make the test correct whichever of the
    # plugin's two name fields reaches it, instead of depending on which one the caller happened to pick.
    "Cricket", "Compass", "Vagrant", "Ibis", "Chicane", "Brawler", "Revoker",
    "Vortex", "Tarantula", "Ifrit", "Medusa", "Darkreach", "Alkyon",
))


def is_airframe(name):
    """True when `name` is one of the game's aircraft rather than a munition. Prefix-matched as well
    as exact, because the plugin sometimes carries a bare code ("FS-20") where the catalogue holds
    the full label ("FS-20 Vortex"). Unknown names are treated as NOT an airframe: a munition we do
    not recognise should still be printed, whereas mislabelling a real weapon as a plane would hide
    the one fact the report exists to convey."""
    n = str(name or "").strip().casefold()
    if not n:
        return False
    if n in AIRFRAME_NAMES:
        return True
    return any(a.startswith(n) or n.startswith(a.split()[0]) for a in AIRFRAME_NAMES)


def _a_or_an(name):
    """"a" or "an" for a unit name. The damaging unit is not always an aircraft - it may be a ship,
    a SAM site or a launcher - so the rule has to cope with both CODES and WORDS, which disagree:

        "EW-25 Medusa"   -> code:  E is said "ee"  -> AN EW-25
        "Medusa"         -> word:  M is a consonant -> A Medusa
        "Linebreaker SAM"-> word:  L is a consonant -> A Linebreaker  (a letter rule gives "an", wrong)
        "Alkyon AB-4"    -> word:  A is a vowel     -> AN Alkyon

    So: if the first token reads as an initialism (has a digit, or is all-caps), use the letter's
    SOUND; otherwise use the ordinary vowel test."""
    tok = str(name or "").strip().split(" ")[0]
    if not tok:
        return "a"
    c = tok[0].upper()
    looks_code = any(ch.isdigit() for ch in tok) or (tok.isupper() and len(tok) > 1)
    return "an" if (c in "AEFHILMNORSX" if looks_code else c in "AEIOU") else "a"


def describe_kill_weapon(munition, plane):
    """Human phrase for what did the killing, never claiming more than is known.

    `munition` is the plugin's killWeapon and `plane` its damaging-unit name. When the two are equal,
    or the munition is really an airframe, the launch match failed and the weapon is genuinely unknown
    - say so rather than printing the aircraft as though it were the ordnance."""
    munition, plane = str(munition or "").strip(), str(plane or "").strip()
    if munition and munition != plane and not is_airframe(munition):
        return f"{munition} (from {_a_or_an(plane)} {plane})" if plane else munition
    if plane:
        # Same three-way rule as describe_tk_cause and both panel renderers: only an AIRCRAFT gets
        # the "weapon not identified" disclaimer. Anything else IS the answer - a SAM, a ship, or
        # the CARRIER MISSILE the damage credit landed on once its 120s launch window expired - and
        # stamping the disclaimer on it made the KILL-line guard throw the name away entirely, so
        # the activity feed showed nothing even when the ordnance was known. (audit 13)
        return f"{plane}, weapon not identified" if is_airframe(plane) else plane
    return "weapon not identified"


def describe_tk_cause(rec):
    """The 'how' clause for a teamkill line, built only from fields the plugin actually sends.

    A teamkill report carries TWO name fields: `weapon` is the DAMAGING UNIT (aircraft / SAM / ship,
    populated whenever the game recorded one) and `munition` is the ordnance resolved by the launch
    tracker, empty when it did not resolve. is_airframe exists only to label the fallback case where
    no munition resolved, so an aeroplane is never presented as though it were the weapon.

    `method` is deliberately NOT printed when it reads "direct". That value is the fail-open default
    of the plugin's ClassifyTkMethod (`return string.IsNullOrEmpty(unitName) ? "" : "direct"`), i.e.
    it means no more than "the damaging unit had a name" - it is not a measurement of anything, and
    printing it is what made these lines read "via direct" while conveying nothing. "auto" and
    "splash" ARE real classifications and are kept."""
    nc = str(rec.get("nc") or "").strip().lower()
    bits = []
    w = str(rec.get("weapon") or "").strip()          # the damaging UNIT (aircraft, SAM, ship)
    mun = str(rec.get("munition") or "").strip()      # the ordnance, when the launch tracker matched
    if mun and mun != w and not is_airframe(mun):
        # The is_airframe guard is defence in depth: `munition` is only ever filled from a resolved
        # launch/gun record today, but the SAME rule is asserted in describe_kill_weapon and the two
        # panel renderers, and a rule that holds in one of four places is not a rule.
        bits.append(f"{mun} (from {_a_or_an(w)} {w})" if w else mun)
    elif not w:
        if nc != "no-weapon":          # the caller already prints "not counted - no weapon recorded"
            bits.append("weapon not recorded")
    elif is_airframe(w):
        # An AIRCRAFT name with no munition resolved: the launch match failed, so say so rather than
        # presenting the aeroplane as though it were the ordnance.
        bits.append(f"{w}, weapon not identified")
    else:
        # Anything that is not an aircraft IS the answer: an auto-defence emplacement (AFV6 AA,
        # Hexhound SAM), a ship, or a munition name the credit landed on directly. Print it bare.
        # Appending "weapon not identified" to "AFV6 AA" denied the one fact the line carries, and
        # disagreed with the Web CC, which renders the same record without the tail. (audit 10)
        bits.append(w)

    method = str(rec.get("method") or "").strip().lower()
    if method == "auto" and nc != "auto":   # ditto: don't restate "not counted - auto-defence"
        bits.append("auto-defence, fired automatically")
    elif method == "splash":
        bits.append("splash")
    # "direct" and "" add nothing - see the docstring

    units = rec.get("units")
    if isinstance(units, list) and units:
        en = sum(1 for u in units if u.get("f") == "e")
        fr = sum(1 for u in units if u.get("f") == "f")
        bits.append(f"blast hit {en} enemy / {fr} friendly")

    try:
        dmg = float(rec.get("dmg") or 0)
        if dmg > 0:
            bits.append(f"{dmg:,.0f} damage")
    except (TypeError, ValueError):
        pass
    return " - ".join(bits)
_splash_dedup      = {}      # (kid,vid) or vid -> ts; blocks assist/re-read/replay double posts (~10s)
AIR                = None     # latest AI/player aircraft counts from the plugin's "air" line (perf panel)
AIR_TS             = 0.0      # when AIR was last updated (stale => hide the panel)
NET                = None     # latest connection-health/RTT-probe telemetry from the plugin's "net" line (Connection Stress panel)
NET_TS             = 0.0      # when NET was last updated (stale => omit from state)
LAST_FRAMETIME_MS  = None     # latest smoothed server frametime (ms) from the plugin's "net" line (webcc frametime box); None until seen
PLAYER_RTT_MS      = {}       # steamid -> int RTT ms (plugin: Steam m_nPing preferred, Notify ACK fallback)
ENT                = None     # latest {"a":[AI aircraft],"s":[ships]} from the plugin's "ent" line (live map; ~5s)
ENT_TS             = 0.0      # when ENT was last updated (stale => omit from state)

# webcc settings menu: live plugin config snapshot (from the plugin's [NOSTATS] {"t":"cfg"} line)
PLUGIN_CFG         = {}       # "Section.Key" -> current value, reported live by the plugin
PLUGIN_CFG_TS      = 0.0      # when PLUGIN_CFG was last refreshed
# Last-known plugin cfg PERSISTED across bot restarts (Tomo 2026-07-05: the settings menu used to show
# catalogue DEFAULTS after a bot restart until the next cfg frame arrived - real values "ticked over"
# seconds later). On start we (1) preload this cache so the webcc shows the real last-known values
# immediately, and (2) drop a dumpcfg plugin_cmd so a FRESH frame re-confirms within seconds (the
# plugin's Ticker processes commands even on an empty server).
PLUGIN_CFG_CACHE_FILE = os.path.join(_BASE_DIR, "plugin_cfg_cache.json")
_FRAME_ERR_AT = 0.0   # throttle for the frame-handler error printer (see the poll loop)
# After plugin AiLimit culls, mute USE_PLUGIN_SCORE banking briefly (culls can still
# move PlayerScore via residual RewardPlayer paths / race). Wall-clock deadline.
_AILIMIT_SCORE_MUTE_UNTIL = 0.0
_LAST_NOSTATS_AT = 0.0
_LAST_CONSOLE_AT = 0.0
_CONSOLE_LIVE_S = 45.0   # console/NOSTATS heartbeat window for server_up when RCMD is down


def save_plugin_cfg_cache():
    """Persist the live plugin cfg snapshot (atomic tmp+replace, best-effort)."""
    try:
        tmp = PLUGIN_CFG_CACHE_FILE + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump({"ts": PLUGIN_CFG_TS, "cfg": PLUGIN_CFG}, f)
        os.replace(tmp, PLUGIN_CFG_CACHE_FILE)
    except Exception as e:  # noqa: BLE001 - a display cache must never break the frame handler
        print(f"[cfg-cache] save skipped: {e}")


def load_plugin_cfg_cache():
    """Seed PLUGIN_CFG from the persisted cache at startup (values refresh via dumpcfg seconds later)."""
    global PLUGIN_CFG, PLUGIN_CFG_TS
    try:
        with open(PLUGIN_CFG_CACHE_FILE, encoding="utf-8") as f:
            d = json.load(f)
        cfg = d.get("cfg")
        if isinstance(cfg, dict) and cfg:
            PLUGIN_CFG = {str(k): cfg[k] for k in cfg}
            PLUGIN_CFG_TS = float(d.get("ts") or 0.0)
            print(f"[cfg-cache] seeded {len(PLUGIN_CFG)} plugin settings from cache")
    except FileNotFoundError:
        pass
    except Exception as e:  # noqa: BLE001
        print(f"[cfg-cache] load skipped: {e}")
# bot-owned settings the bot reads at startup (a bot restart fully applies them). Overrides set via the
# settings menu are persisted to bot_overrides.json and re-applied here on the next start.
TICK_RATE = 60   # server engine frame/tick rate (Hz). 30-120. Applied by the launch wrapper on the next
                 # SERVER (re)start, NOT by a bot restart. The wrapper generator reads _read_tick_rate().
# AI density stamped into the PvE co-op mission FILES by run.bat --set-ai-limits (the rewriter reads
# these at run time). Editing them here / from the settings menu changes what the NEXT --set-ai-limits
# run writes; missions already on the server keep their stamped numbers until it is re-run.
AI_OPP_LIMIT    = 8       # opposing (AI, preventJoin) team AIAircraftLimit (start count)
AI_OPP_ADDAI    = 0.75    # opposing team addAIPerEnemyPlayer (+per enemy player)
AI_PLR_LIMIT    = 6       # player (preventJoin==false) team AIAircraftLimit (AI allies)

# ANY constant listed in _BOT_OVERRIDE_KEYS MUST be defined ABOVE this block. The loader below runs at
# import; a definition further down the module would execute afterwards and silently clobber the
# operator's saved value (a definition-order bug class that has bitten before: the panel accepts and
# persists a new value while the bot keeps paying the baked default forever).
# NB: VOTE_DURATION / APPROVAL_DURATION are NO LONGER here — they're derived aliases of MAP_VOTE_DURATION
# (FIX 3), so a stale bot_overrides.json {"VOTE_DURATION": 60} left over from the old build is now IGNORED.
_BOT_OVERRIDE_KEYS = ("MISSION_MAX_TIME",
                      "TICK_RATE",
                      # 1.2.0: bot knobs that previously had NO panel path at all
                      "AI_OPP_LIMIT", "AI_OPP_ADDAI", "AI_PLR_LIMIT",
                      "ROLLOVER_SECONDS", "POST_VOTE_COOLDOWN", "MAX_DARK_PER_VOTE",
                      "GAIN_CLAMP_MAX", "SPIKE_THRESHOLD")
try:
    with open(os.path.join(_BASE_DIR, "bot_overrides.json"), "r", encoding="utf-8") as _bof:
        _bo = json.load(_bof)
    for _k in _BOT_OVERRIDE_KEYS:
        if _k in _bo and isinstance(_bo[_k], (int, float)) and not isinstance(_bo[_k], bool):
            globals()[_k] = int(_bo[_k]) if float(_bo[_k]).is_integer() else _bo[_k]
except (OSError, ValueError):
    pass


# ── FIX 3: vote-timing persistence in the DEPLOY-PROTECTED .nost-data dir ────────────────────────────
# MAP_VOTE_DURATION / POST_VOTE_MAP_CHANGE_DELAY persist here (NOT bot_overrides.json in this ROOT folder,
# which a code deploy / a webcc-zip extract can clobber). The updater only replaces code + extracts the
# webcc zip into ROOT — it never writes into .nost-data — so these knobs survive deploys. A MISSING file
# keeps the baked defaults (30 / 15) => derived VOTE=30, APPROVAL=30, PMD=45. It can NEVER become 60/60/80.
def _nost_data_dir():
    """Toolkit config dir, resolved exactly like cc_web + the installer: NOST_DATA_DIR env pin >
    this install's .nost-data > legacy ~/.nuke-option-toolkit."""
    env = os.environ.get("NOST_DATA_DIR")
    if env:
        return env
    local = os.path.join(_BASE_DIR, ".nost-data")
    if os.path.isdir(local):
        return local
    return _TK_DIR


VOTEMAP_TIMING_FILE = os.path.join(_nost_data_dir(), "votemap_timing.json")


def _clamp_vote_timing(mv, pv):
    """Enforce the invariant: vote 10..300s, post-vote delay 5..300s. The >=5 floor makes the broken
    combination (a delay that would put derived PMD below the vote) UNREPRESENTABLE."""
    try:
        mv = int(round(float(mv)))
    except (TypeError, ValueError):
        mv = 30
    try:
        pv = int(round(float(pv)))
    except (TypeError, ValueError):
        pv = 15
    return max(10, min(300, mv)), max(5, min(300, pv))


def _load_vote_timing():
    """Load the two knobs from .nost-data and re-derive the VOTE_DURATION / APPROVAL_DURATION aliases.
    Missing/corrupt file => keep the baked 30/15 defaults (never the old 60)."""
    global MAP_VOTE_DURATION, POST_VOTE_MAP_CHANGE_DELAY, VOTE_DURATION, APPROVAL_DURATION
    mv, pv = MAP_VOTE_DURATION, POST_VOTE_MAP_CHANGE_DELAY
    try:
        with open(VOTEMAP_TIMING_FILE, encoding="utf-8") as f:
            d = json.load(f)
        if isinstance(d, dict):
            mv = d.get("map_vote_duration", mv)
            pv = d.get("post_vote_change_delay", pv)
    except (OSError, ValueError):
        pass
    MAP_VOTE_DURATION, POST_VOTE_MAP_CHANGE_DELAY = _clamp_vote_timing(mv, pv)
    VOTE_DURATION = APPROVAL_DURATION = MAP_VOTE_DURATION


def _save_vote_timing():
    """Atomically persist the two knobs to .nost-data (creating the dir if needed)."""
    try:
        os.makedirs(_nost_data_dir(), exist_ok=True)
        tmp = VOTEMAP_TIMING_FILE + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump({"map_vote_duration": int(MAP_VOTE_DURATION),
                       "post_vote_change_delay": int(POST_VOTE_MAP_CHANGE_DELAY)}, f, indent=1)
        os.replace(tmp, VOTEMAP_TIMING_FILE)
        return True
    except OSError as e:
        print(f"[vote-timing] save failed: {e}")
        return False


_load_vote_timing()

# Human-readable activity feed (the "watch" screen tails this). One tidy line per
# meaningful event, so the user sees plain English instead of raw rcmd JSON.
ACTIVITY_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "activity.log")


def _plain(text):
    """Strip TMP <color> tags so a chat label reads cleanly in the plain feed."""
    return re.sub(r"</?color[^>]*>", "", text)


# ── Font-safe chat ──────────────────────────────────────────────────────────────
# The in-game TMP font has no glyph for several typographic characters we like to
# write in source (arrows, en/em dashes, the middle dot, ellipsis, math signs):
# the client draws a white square instead. Tomo saw squares on the rank-up arrow
# (2026-07-27) and on the awards middle-dot separator. Swap them for ASCII at the
# DELIVERY choke-points (RemoteCommand.send + _drop_plugin_cmd) so no template --
# stored, default or future -- and no whisper/broadcast path can put squares in
# chat.
#
# Deliberately a TABLE, never "strip everything non-ASCII": player names carry
# accents / Cyrillic / CJK that the game DOES render, and mangling a name would be
# a worse bug than a square. Only pure typographic punctuation is listed, so a name
# is untouched unless it contains one of these (in which case it was already a
# square and a hyphen is an improvement).
_FONT_SAFE_TABLE = str.maketrans({
    "→": "->",   "⇒": "=>",  "←": "<-",  "⇐": "<=",
    "—": "-",    "–": "-",   "−": "-",   "·": "-",
    "•": "*",    "…": "...",
    "≥": ">=",   "≤": "<=",  "≠": "!=",  "≈": "~",
    "“": '"',    "”": '"',   "‘": "'",   "’": "'",
})


def font_safe(text):
    """ASCII-ise the glyphs the game font cannot draw. Fail-open: on any surprise
    the original text goes out unchanged (a square beats a dropped message)."""
    try:
        return str(text).translate(_FONT_SAFE_TABLE)
    except Exception:                                    # noqa: BLE001
        return text


def activity(msg, tag=""):
    """Append a timestamped, human-readable line to activity.log and echo it to the
    raw log too. `tag` (e.g. "MAP", "WIN") is padded to a fixed column so every line
    lines up in the watch window. Never raises -- logging must never crash the bot."""
    line = f"[{tag}]".ljust(8) + msg if tag else msg
    try:
        print(f"[activity] {line}")
    except Exception:        # noqa: BLE001
        pass
    try:
        with open(ACTIVITY_FILE, "a", encoding="utf-8") as f:
            f.write(f"{time.strftime('%I:%M:%S %p')}  {line}\n")
    except OSError:
        pass


_COLOR_RE = re.compile(r"</?color[^>]*>")


def _strip_color(s):
    """Drop <color=..> tags so a chat line reads cleanly in the activity feed."""
    return _COLOR_RE.sub("", str(s))

# ----------------------------------------------------------------------------
# Command-centre dashboard feed. The single-window command centre
# (command_centre.py) is a separate VIEWER process; the bot publishes everything
# it needs to local files so the dashboard needs no SFTP/relay creds of its own:
#   * console_mirror.log   - every raw server-console line the bot reads, so the
#                            dashboard can show the live BepInEx/server console.
#   * dashboard_state.json - a periodic snapshot of the mission/vote header and
#                            the player table (server rank, in-game rank, plane,
#                            match points). Written atomically.
# Publishing must NEVER crash the bot -> everything here is best-effort.
# ----------------------------------------------------------------------------
CONSOLE_MIRROR_FILE  = os.path.join(_BASE_DIR, "console_mirror.log")
DASHBOARD_STATE_FILE = os.path.join(_BASE_DIR, "dashboard_state.json")
ADMIN_CMD_FILE       = os.path.join(_BASE_DIR, "admin_commands.jsonl")  # command-centre admin queue (e.g. grant points)
ADMIN_CMD_OFFSET_FILE = os.path.join(_BASE_DIR, "admin_commands.offset")  # persisted consume-offset: queue survives bot restarts
ADMIN_CMD_MAX_AGE    = 900   # skip queued commands older than this (s) — replay-safety if the offset file is lost
STATE_WRITE_INTERVAL = 0.5        # rewrite dashboard_state.json every 0.5s; plugin PosTick is 0.5 Hz
                                 # (every 2s) — webcc interpolates trail anchors at 60fps regardless.
# STALE-DATA HONESTY (2026-07-27): with the relay unreachable the roster can only be
# LAST-KNOWN — that night /api/state served server_up=True online_count=14 while the
# panel showed the server OFFLINE. After STALE_RELAY_S without a positive
# get-player-list reply the dashboard says so explicitly (is_stale) and stops
# presenting the stale headcount as live.
STALE_RELAY_S        = 30.0       # relay silent this long -> roster/headcount is last-known, not live
_RELAY_LAST_OK       = [time.time()]   # last positive get-player-list reply (roster poll stamps it)
_CONSOLE_MIRROR_MAX  = 2_000_000  # bytes; past this the mirror is trimmed to the last N lines
_MIRROR_KEEP         = 3000
ROSTER_BY_SID        = {}         # sid -> last get-player-list entry (faction for the table)


def mirror_console_batch(lines):
    """Append a whole poll's worth of console lines in ONE open/write/close. The plugin
    emits [NOSTATS] snapshots many times/sec, so a single poll can carry dozens of lines;
    a per-line open+close was the costliest syscall in the poll loop on Windows. Bytes on
    disk are identical to the per-line writes. Best-effort; never affects parsing."""
    if not lines:
        return
    try:
        with open(CONSOLE_MIRROR_FILE, "a", encoding="utf-8") as f:
            f.write("".join(l.rstrip("\r\n") + "\n" for l in lines))
    except OSError:
        pass


def trim_console_mirror():
    """Keep console_mirror.log bounded so it can never grow without limit."""
    try:
        if os.path.getsize(CONSOLE_MIRROR_FILE) <= _CONSOLE_MIRROR_MAX:
            return
        with open(CONSOLE_MIRROR_FILE, "r", encoding="utf-8", errors="replace") as f:
            tail = f.readlines()[-_MIRROR_KEEP:]
        tmp = CONSOLE_MIRROR_FILE + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            f.writelines(tail)
        os.replace(tmp, CONSOLE_MIRROR_FILE)
    except OSError:
        pass


def trim_activity_log():
    """Keep activity.log bounded (it was never trimmed and cc_web re-reads its tail every ~1s).
    5000 lines ≈ weeks of history; the webcc only ever shows the last 80."""
    try:
        if os.path.getsize(ACTIVITY_FILE) <= 1_500_000:
            return
        with open(ACTIVITY_FILE, "r", encoding="utf-8", errors="replace") as f:
            tail = f.readlines()[-5000:]
        tmp = ACTIVITY_FILE + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            f.writelines(tail)
        os.replace(tmp, ACTIVITY_FILE)
    except OSError:
        pass


def _sanef(v, default=0.0):
    """Tolerant finite float: never raises, never returns NaN/inf. For plugin-frame numerics —
    a raising int()/float() at ingest silently drops the WHOLE record (warn/kick/BAN events
    included) via the poll loop's broad except (audit round 2)."""
    try:
        f = float(v or 0)
        return f if math.isfinite(f) else default
    except (TypeError, ValueError):
        return default


def _sanei(v, default=0):
    try:
        return int(_sanef(v, default))
    except (TypeError, ValueError, OverflowError):
        return default


def _json_sane(o, _depth=0):
    """Deep-replace non-finite floats (NaN/Infinity) with 0 before persisting/serving JSON.
    AUDIT 2026-07-05 (round 2): any NaN/inf that reaches json.dump is written as a bare token
    (allow_nan default) which the webcc's strict JSON.parse rejects -> the ENTIRE command centre
    bricks on every poll until the file is hand-edited. Plugin frames carry floats from Unity
    physics (positions, RTT math, damage) - one blown-up value must never take the panel down.
    Applied at the CHOKE POINTS (dashboard write, reports save) so every ingest path is covered."""
    if _depth > 12:
        return o
    if isinstance(o, float):
        return o if math.isfinite(o) else 0
    if isinstance(o, dict):
        return {k: _json_sane(v, _depth + 1) for k, v in o.items()}
    if isinstance(o, (list, tuple)):
        return [_json_sane(v, _depth + 1) for v in o]
    return o


def write_dashboard_state(*, state, server_up, online, votes, vote_ends_at,
                          vote_context, approval, mtime, rcmd_up=None, console_live=None):
    """Atomically write dashboard_state.json: the mission/vote header plus the
    per-player table (server rank, in-game rank, plane, match points)."""
    try:
        now = time.time()
        players = []
        for sid in online:
            rec  = RANK_DATA.get(sid, {})
            meta = STATS_META.get(sid, {})
            ros  = ROSTER_BY_SID.get(sid, {})
            pts  = player_points(sid)                          # lifetime COMBINED total (score column)
            # Ladder label from CYCLE points; prestige >=1 → 'OFFCDT - 1*' (plain; WebCC does NOT wrap [ ])
            # Empty RANKS = the rank ladder is off -> no rank column.
            if RANKS:
                _, rname, abbr, color = RANKS[rank_index_for(cycle_points(sid))]
                abbr = prestige_label(abbr, rname, prestige_count(sid))
            else:
                rname, abbr, color = "", "", "#FFFFFF"
            # Map icon decision (x/z + grounded):
            #   DEAD  ✝  — sid in DOWNED (life death/eject or down). Sticky; never from landed g
            #              or stale POS. Coords = DEATH_POS (frozen wreck).
            #   ALIVE    — fresh Occupied POS (still in an airframe: flying OR landed taxiing).
            #   HIDE     — no icon: spectating / safe dismount / left aircraft / no world pos.
            #              (x=z=None). Safe exit ≠ death — distinct from corpse ✝.
            _pp = POS.get(sid)
            _have = bool(_pp) and _pp[0] is not None
            _grounded = sid in DOWNED
            _px = _pz = _kls = _hdg = None
            if _grounded:
                _dp = DEATH_POS.get(sid)
                if _dp:
                    _px, _pz = _dp[0], _dp[1]
                elif _have:
                    _px, _pz = _pp[0], _pp[1]
            elif _have:
                try:
                    _age = now - float(_pp[2])
                except (TypeError, ValueError, IndexError):
                    _age = 999.0
                if _age <= _POS_INWORLD_S:
                    _px, _pz = _pp[0], _pp[1]
                    _kls = _pp[3] if len(_pp) > 3 else None
                    _hdg = _pp[4] if len(_pp) > 4 else None
                # else: POS stale → hide (safe exit / not in world)
            players.append({
                "sid":          sid,
                "name":         (_storable_name(sid, ros.get("displayName")) or PLAYER_NAMES.get(sid)
                                 or _storable_name(sid, rec.get("name"))
                                 or _storable_name(sid, meta.get("name")) or sid),
                "faction":      ros.get("faction") or meta.get("faction") or "",
                "aircraft":     meta.get("aircraft") or "",
                "rank_abbr":    abbr,
                "rank_name":    rname,
                "rank_color":   color,
                "points":       _pts_i(pts),
                "ingame_rank":  meta.get("rank"),
                "match_points": _pts_i(LIVE_SCORE.get(sid, 0.0)),
                "teamkills":    meta.get("teamkills"),
                "wins":         rec.get("wins", 0),
                "losses":       rec.get("losses", 0),
                "x":            _px,                        # None = hide blip; set only alive-in-ac or dead marker
                "z":            _pz,
                "grounded":     _grounded,                  # True => death/eject only (map dead icon); NOT landed/dismount
                "klass":        _kls,                       # "h" => heli glyph (+), else plane (▲)
                "h":            _hdg,                       # heading deg 0..359 (None if unknown)
                "fresh":        bool(meta) and (now - meta.get("t", 0) < 30),
                "rtt_ms":       PLAYER_RTT_MS.get(sid),     # Steam/HostPing-class RTT ms (None until sampled)
                # PosTick trail (emit unix ts) so WebCC delayed lerp keeps real 0.5s spacing across
                # bot/webcc poll jitter — not just one tip per /api/state poll.
                "pos_trail":    (
                    [{"t": round(_tr[0], 3), "x": _tr[1], "z": _tr[2], "h": _tr[3]}
                     for _tr in POS_TRAIL.get(sid, ())]
                    if (not _grounded and _px is not None and POS_TRAIL.get(sid)) else None
                ),
            })
        players.sort(key=lambda p: (-p["match_points"], -p["points"], p["name"].lower()))

        vote = None
        if state == "VOTING":
            counts = Counter(votes.values())
            vote = {
                "context": vote_context,
                "ends_in": max(0, int(vote_ends_at - now)),
                "options": [{"key": k, "label": _plain(v[3]), "votes": counts.get(k, 0)}
                            for k, v in sorted(VOTE_OPTIONS.items())],
            }
        # STALE-DATA HONESTY: relay silent > STALE_RELAY_S means `online` is last-known.
        # Flag it, zero the headline count (kept in online_count_last) and only keep
        # claiming server_up on live console/NOSTATS evidence — a relay-only outage with
        # a live console stays "up" (webcc shows its amber rcmd-down banner for that).
        relay_age = max(0.0, now - float(_RELAY_LAST_OK[0] or 0))
        is_stale = relay_age > STALE_RELAY_S
        data = {
            "ts":           now,
            "bot_pid":      os.getpid(),
            # server_up = game live (RCMD OR recent console/NOSTATS). Not RCMD-alone.
            "server_up":    (bool(console_live) if is_stale else server_up),
            "is_stale":     is_stale,                        # roster/headcount is last-known, not live
            "relay_age_s":  round(relay_age, 1),             # seconds since the last positive relay read
            "rcmd_up":      (bool(rcmd_up) if rcmd_up is not None else bool(server_up)),
            "console_live": (bool(console_live) if console_live is not None else False),
            "plugin_version": _live_plugin_version(),        # FIX 1: live plugin build for the webcc header (load line, else deployed_plugin.json)
            "mission":      (PVP_TAG_PLAIN + CURRENT_MISSION) if is_pvp(CURRENT_MISSION) else CURRENT_MISSION,  # shared [PVP] label (panel / live-map header)
            "mission_pvp":  is_pvp(CURRENT_MISSION),          # so the webcc can style the [PVP] tag if it wants
            "state":        state,
            "online_count": (0 if is_stale else len(online)),           # honest: 0 when last-known
            "online_count_last": (len(online) if is_stale else None),   # the stale last-known headcount
            "time_current": mtime[0],
            "time_max":     mtime[1],
            "time_at":      mtime[2],
            "plugin_live":  any(p["fresh"] for p in players),
            "vote":         vote,
            "approval":     approval,
            "players":      players,
            "faction_colours": dict(FACTION_COLOURS),   # vanilla faction.color hexes (plugin-sampled) for WebCC --bdf/--pala
            "air":          AIR if (AIR and now - AIR_TS < 15) else None,   # AI/player aircraft counts (perf panel)
            "net":          ({**NET, "ts": round(NET_TS, 2)} if (NET and now - NET_TS < 15) else None),   # connection-health telemetry + reading timestamp (so the webcc NET graph samples once per reading, not per poll)
            "frametime_ms": LAST_FRAMETIME_MS,               # smoothed server frametime (ms) from the plugin's "net" line (webcc frametime box); None until seen
            "entities":     ENT if (ENT and now - ENT_TS < 15) else None,   # AI aircraft + ships for the live map
            "plugin_cfg":   (dict(PLUGIN_CFG) if PLUGIN_CFG else None),   # live plugin config (public-listing overlay removed with the directory feature)
            "mission_pool": mission_pool_state(),                # votemap pool toggles for the webcc Mission Pool modal
            "server_messages": server_messages_state(),          # automated chat messages for the webcc Messages modal
            "rank_ladder": rank_ladder_state(),                  # editable rank ladder (titles/points/colours/template) for the webcc Ranks modal
            "shared_ranks": shared_ranks_state(),                # cross-server shared-rank status + combined board for the webcc Shared Ranks card
            "reports": reports_state(),                          # anti-grief auto-kick/flag reports for the webcc Reports tab
            "ban_log": ban_log_state(),                          # persistent per-SteamID ban log (repeat-offender tracking) for the webcc Reports tab
            "server_config": server_config_state(),              # DedicatedServerConfig.json fields for the webcc Server Settings tab
            "sys_messages": sysmsg_state(),                       # built-in automated-message overrides for the webcc Messages tab
            "help_config": help_state(),                          # !help command list (text + show/hide gates) for the webcc Help editor
            "mission_audit": mission_audit_state(),               # official vs custom/workshop missions + integrity + eligibility (webcc Mission Pool)
            "votemap": votemap_cfg_state(),                       # dynamic vote-pool config (ballot size/mode/includes) for the webcc Votemap settings
            "banned_players": banned_players_state(),             # plugin_bans.txt -> webcc Moderation 'Banned' tab
        }
        tmp = DASHBOARD_STATE_FILE + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(_json_sane(data), f)   # non-finite floats -> 0 (a single NaN would brick the webcc's JSON.parse)
        # os.replace can hit WinError 5 (Access denied) when a reader (the command
        # centre TUI) has the file open at that instant; retry briefly before giving up
        # so a transient lock doesn't drop the update (and leave a stale .tmp behind).
        for _attempt in range(5):
            try:
                os.replace(tmp, DASHBOARD_STATE_FILE)
                break
            except PermissionError:
                if _attempt == 4:
                    raise
                time.sleep(0.04)
    except Exception as e:   # noqa: BLE001 - publishing must never take the bot down
        try:
            print(f"[dashboard] state write failed: {e}")
        except Exception:    # noqa: BLE001
            pass

# ----------------------------------------------------------------------------
# Chat parser  -- derived from your sample console line:
# 81587.130: [ChatManager] CmdSendChatMessage allChat:True
#            connection(SteamConnection(7656119xxxxxxxxxx)) Player(Clone) test
# ----------------------------------------------------------------------------

CHAT_RE = re.compile(
    r"\[ChatManager\]\s+CmdSendChatMessage\s+"
    r"allChat:(?P<allchat>True|False)\s+"
    r"connection\(SteamConnection\((?P<steamid>\d+)\)\)\s+"
    r"(?P<obj>\S+)\s+"
    r"(?P<msg>.*)"
)

# A mission ending (for any reason) logs e.g.:
#   [DedicatedServerManager] Mission complete. Waiting 60 seconds before closing...
# We open the next vote on this, which also covers missions that end early.
MISSION_END_RE = re.compile(r"\[DedicatedServerManager\].*Mission complete", re.IGNORECASE)

# NukeStats plugin lines: "[NOSTATS] {json}" (see NukeStats/). Carries real per-player score.
NOSTATS_RE     = re.compile(r"\[NOSTATS\]\s*(\{.*\})\s*$")

THANKS_INTERVAL = 900        # "thanks for playing" cadence (seconds) - was 600 (10->15 min)
LEADERBOARD_INTERVAL = 1800  # auto-post the leaderboard to chat every 30 min during a match
SPECTIP_INTERVAL = 1020      # post spectator / team-switch help (seconds) - was 720 (12->17 min)
OTHERSERVER_INTERVAL = 1200  # "N players on the other server" cadence (20 min) - offset from the other
                             # periodic lines above so the three don't stack up in one burst of chat


def parse_chat_line(line):
    """Return {'steamid','allchat','message'} for a player chat line, else None.

    The name field in the log is just the Unity object ('Player(Clone)'), not the
    player's display name, so we key votes on SteamID -- which is unique anyway.
    """
    m = CHAT_RE.search(line)
    if not m:
        return None
    return {
        "steamid": m.group("steamid"),
        "allchat": m.group("allchat") == "True",
        "message": m.group("msg").strip(),
    }


def extract_vote(message):
    """Map a chat message to a VOTE_OPTIONS key, or None. Votes must be '!'-prefixed
    (e.g. !1, or !vote 1) so a bare number typed in normal chat isn't counted."""
    msg = message.strip()
    if msg.lower().startswith("!vote"):
        msg = msg[len("!vote"):].strip()
    elif msg.startswith("!"):
        msg = msg[1:].strip()            # !1 -> 1
    else:
        return None                      # bare text/number is ordinary chat, not a vote
    parts = msg.split()
    token = parts[0] if parts else ""
    return token if token in VOTE_OPTIONS else None


# ----------------------------------------------------------------------------
# Remote-command client  -- JSON over TCP, 4-byte little-endian length prefix
# ----------------------------------------------------------------------------

class RemoteCommand:
    def __init__(self, host, port, timeout=5):
        self.host, self.port, self.timeout = host, port, timeout
        self.sock = None

    def _connect(self):
        self.sock = socket.create_connection((self.host, self.port), self.timeout)
        self.sock.settimeout(self.timeout)

    def _recv_exact(self, n):
        buf = b""
        while len(buf) < n:
            chunk = self.sock.recv(n - len(buf))
            if not chunk:
                raise ConnectionError("remote-command socket closed")
            buf += chunk
        return buf

    def send(self, name, *args, return_code=False):
        """Send one command; return the decoded JSON response (or raw text/None).
        With return_code=True, return (status_code, response) instead -- the command
        centre uses that to show Success vs an error code. status_code is None on a
        connection failure."""
        # NAME GUARD + FONT GUARD choke-point: every in-game chat line leaves through here
        # (say / broadcast / whisper's chat fallback / the webcc admin Say + rankpreview, which
        # share this RemoteCommand), so a placeholder name can never reach players and no
        # unrenderable glyph can reach the game font, regardless of which path composed the line.
        if name == "send-chat-message" and args:
            args = (font_safe(chat_name_safe(args[0])),) + tuple(args[1:])
        payload = json.dumps(
            {"name": name, "arguments": [str(a) for a in args]}
        ).encode("utf-8")
        frame = len(payload).to_bytes(4, "little") + payload
        for attempt in (1, 2):  # reconnect once on a dead socket
            try:
                if self.sock is None:
                    self._connect()
                self.sock.sendall(frame)
                # Response framing: 4-byte status code (2000 = Success), then a
                # 4-byte body length, then the JSON body.
                code = int.from_bytes(self._recv_exact(4), "little")
                length = int.from_bytes(self._recv_exact(4), "little")
                if not 0 <= length <= 8_000_000:   # desynced/garbage frame -> reconnect & resync
                    raise ConnectionError(f"implausible reply length {length}")
                body = self._recv_exact(length).decode("utf-8", "replace")
                try:
                    resp = json.loads(body)
                except json.JSONDecodeError:
                    resp = body
                if DEBUG:
                    print(f"[rcmd] {name}{args} -> code={code} {resp}")
                return (code, resp) if return_code else resp
            except (OSError, ConnectionError) as e:
                print(f"[rcmd] {name} failed ({e})"
                      + ("; reconnecting" if attempt == 1 else " again"))
                try:
                    if self.sock:
                        self.sock.close()
                finally:
                    self.sock = None
        return (None, None) if return_code else None

    # convenience wrappers
    def say(self, message):
        # Font-safe chat (Tomo 2026-07-27): the game font renders U+2192, U+00B7 and
        # friends as squares. send() re-applies both guards for every OTHER chat path
        # (whisper / broadcast / webcc Say); do it here too so the [BOT] activity line
        # below logs exactly the text players saw.
        message = font_safe(chat_name_safe(str(message)))
        if LOG_CONVERSATION:
            activity(_plain(message), "BOT")   # feed [BOT] line == what players actually saw
        return self.send("send-chat-message", message)

    def set_next_mission(self, group, name, max_time):
        return self.send("set-next-mission", group, name, max_time)

    def set_time_remaining(self, seconds):
        return self.send("set-time-remaining", seconds)

    def get_mission_time(self):
        return self.send("get-mission-time")

    def get_player_list(self):
        return self.send("get-player-list")


def find_number(obj, key_hint):
    """Recursively find a numeric value whose key contains key_hint (case-insensitive).
    Lets us read the mission-time response without knowing its exact schema."""
    hint = key_hint.lower()
    if isinstance(obj, dict):
        for k, v in obj.items():
            if isinstance(k, str) and hint in k.lower() and isinstance(v, (int, float)):
                return float(v)
        for v in obj.values():
            r = find_number(v, key_hint)
            if r is not None:
                return r
    elif isinstance(obj, list):
        for v in obj:
            r = find_number(v, key_hint)
            if r is not None:
                return r
    return None


# ----------------------------------------------------------------------------
# Console source  -- HOW the bot reads new console lines.
#
# Provided: tail a LOCAL file. Use this for testing, or if you run the bot on a
# box where the console log is accessible.
#
# For your real setup, replace poll() with one of:
#   * panel websocket  -- if GPanel exposes an API/console websocket, run a small
#                         background thread that pushes lines into a queue and have
#                         poll() drain that queue. (Real-time, best option.)
#   * SFTP tail        -- keep an SFTP/SSH session open to the remote log and read
#                         new bytes each tick (paramiko). (Polling, always works.)
# Tell me which you have and I'll write that adapter.
# ----------------------------------------------------------------------------

def _console_shrink_is_real(prev_pos, new_size, prev_mtime, new_mtime):
    """True when size drop is a real truncate/rotate (safe to jump to EOF).

    Flaky SFTP/stat can report a transient smaller size with unchanged mtime —
    moving pos then would skip bytes. Ambiguous shrink → keep pos, retry next poll.
    """
    try:
        prev_pos = int(prev_pos or 0)
        new_size = int(new_size or 0)
    except (TypeError, ValueError):
        return True
    if new_size >= prev_pos:
        return False
    if new_size < 64 and prev_pos > 4096:
        return True
    try:
        if prev_mtime is None or new_mtime is None:
            return True
        if float(new_mtime) > float(prev_mtime) + 0.05:
            return True
    except (TypeError, ValueError):
        return True
    if (prev_pos - new_size) < max(4096, prev_pos // 4):
        return False
    return True


class ConsoleSource:
    def __init__(self, path):
        self.path = path
        self.pos = 0
        self._buf = ""
        self._mtime = None
        try:
            st = os.stat(self.path)
            self._mtime = st.st_mtime
            with open(self.path, "r", encoding="utf-8", errors="replace") as f:
                f.seek(0, 2)          # start at end: skip old backlog
                self.pos = f.tell()
        except FileNotFoundError:
            print(f"[console] log not found yet: {self.path}")

    def poll(self):
        """Return a list of new complete lines since the last call."""
        try:
            st = os.stat(self.path)
            new_mtime = st.st_mtime
            with open(self.path, "r", encoding="utf-8", errors="replace") as f:
                f.seek(0, 2)
                size = f.tell()
                if size < self.pos:
                    if not _console_shrink_is_real(self.pos, size, self._mtime, new_mtime):
                        print(f"[console] ambiguous shrink {self.pos}->{size} (mtime sticky); "
                              f"keeping offset (no replay)")
                        return []
                    # Rotated/truncated. NEVER replay from byte 0 — replaying [NOSTATS]
                    # snap/score/kill lines rebaselines ms then re-credits the whole match
                    # (P0 2026-07-26 reed multi-pay). Jump to new EOF; skip backlog.
                    print(f"[console] log shrank {self.pos}->{size}; skipping to EOF (no replay)")
                    self.pos = size
                    self._buf = ""
                    self._mtime = new_mtime
                    return []
                f.seek(self.pos)
                self._buf += f.read()
                self.pos = f.tell()
                self._mtime = new_mtime
        except FileNotFoundError:
            return []
        *complete, self._buf = self._buf.split("\n")
        return complete


class SFTPConsoleSource:
    """Reads new console lines from a remote log file over SFTP (paramiko).

    Keeps the SSH/SFTP session open and tails by byte offset; reconnects on
    failure. Requires:  pip install paramiko
    Point SFTP_LOG_PATH at the remote console log (the file in the SFTP / File
    Manager that grows as players chat -- usually a .log in the server root or a
    logs/ folder).
    """

    def __init__(self, host, port, user, password, remote_path):
        self.host, self.port = host, port
        self.user, self.password = user, password
        self.remote_path = remote_path
        self.pos = None          # byte offset; established on first poll
        self._buf = ""
        self._mtime = None
        self._ssh = None
        self._sftp = None
        self._shrink_ambiguous = 0

    def _connect(self):
        import paramiko
        self._ssh = paramiko.SSHClient()
        self._ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
        # connect timeout alone only covers the HANDSHAKE. Once established, a half-open TCP - the
        # container host silently dropping the flow, a NAT idle-timeout, a hung sshd - leaves every
        # later read blocking FOREVER, and this runs on the bot's single-threaded main loop: the
        # console tail, the vote timer, the roster poll and the welcome drain all stop, with no
        # traceback and nothing to recover it short of killing the bot. banner_timeout/auth_timeout
        # bound the rest of the handshake; the keepalive makes a dead peer surface as an exception;
        # and the channel timeout bounds each individual read. (round-4 audit 2026-08-01)
        self._ssh.connect(self.host, port=self.port, username=self.user,
                          password=self.password, timeout=10,
                          banner_timeout=15, auth_timeout=15,
                          look_for_keys=False, allow_agent=False)
        try:
            tr = self._ssh.get_transport()
            if tr is not None:
                tr.set_keepalive(15)
        except Exception:                                  # noqa: BLE001 - keepalive is best-effort
            pass
        self._sftp = self._ssh.open_sftp()
        try:
            self._sftp.get_channel().settimeout(25)        # no single read may block indefinitely
        except Exception:                                  # noqa: BLE001
            pass
        print(f"[sftp] connected to {self.host}:{self.port}")

    def _close(self):
        try:
            if self._sftp:
                self._sftp.close()
            if self._ssh:
                self._ssh.close()
        finally:
            self._sftp = self._ssh = None

    def poll(self):
        try:
            if self._sftp is None:
                self._connect()
            st = self._sftp.stat(self.remote_path)
            size = st.st_size
            new_mtime = getattr(st, "st_mtime", None)
            if self.pos is None:          # first read: start at end, skip backlog
                self.pos = size
                self._mtime = new_mtime
                return []
            if size < self.pos:
                if not _console_shrink_is_real(self.pos, size, self._mtime, new_mtime):
                    self._shrink_ambiguous += 1
                    if self._shrink_ambiguous <= 3 or self._shrink_ambiguous % 30 == 0:
                        print(f"[sftp] ambiguous shrink {self.pos}->{size} (mtime sticky); "
                              f"keeping offset (no replay) n={self._shrink_ambiguous}")
                    return []
                # Rotated/truncated. NEVER replay from 0 — see ConsoleSource.poll.
                print(f"[sftp] log shrank {self.pos}->{size}; skipping to EOF (no replay)")
                self.pos = size
                self._buf = ""
                self._mtime = new_mtime
                self._shrink_ambiguous = 0
                return []
            self._shrink_ambiguous = 0
            if size == self.pos:
                self._mtime = new_mtime
                return []
            with self._sftp.open(self.remote_path, "r") as f:
                f.seek(self.pos)
                data = f.read(size - self.pos)
                self.pos = size
            self._mtime = new_mtime
            if isinstance(data, bytes):
                data = data.decode("utf-8", "replace")
            self._buf += data
        except Exception as e:            # noqa: BLE001 - reconnect on any failure
            print(f"[sftp] poll error ({e}); will reconnect next tick")
            self._close()
            return []
        *complete, self._buf = self._buf.split("\n")
        return complete


# ----------------------------------------------------------------------------
# Plugin chat-log tail (ordinary chat -> activity feed, no game restart needed)
# ----------------------------------------------------------------------------
# The plugin's rerouted-chat record lives in its own log, not the console the bot tails.
PLUGIN_LOG_PATH   = "BepInEx/LogOutput.log"
PLUGIN_CHAT_RE    = re.compile(r"\[chat\]\s+(?P<scope>all|ally)\s+(?P<name>.*?)\s+\((?P<sid>\d{17})\):\s(?P<msg>.*)$")
_CHAT_FRAME_SEEN  = [False]     # set by the t:"chat" handler; retires this tail
_chat_tail        = [None]      # SFTPConsoleSource instance (lazy)
_chat_tail_next   = [0.0]
CHAT_TAIL_EVERY   = 6.0         # seconds between polls (reads only new bytes)
_chat_recent      = {}          # (sid, msg) -> ts, so a frame and a log line never double-log


def chat_seen_recently(sid, msg, ttl=20.0):
    """True if this exact message was already surfaced (either source) within ttl."""
    now = time.monotonic()
    for k, ts in list(_chat_recent.items()):
        if now - ts > ttl:
            _chat_recent.pop(k, None)
    key = (str(sid), str(msg))
    if key in _chat_recent:
        return True
    _chat_recent[key] = now
    return False


def chat_tail_tick():
    """Surface ordinary chat from the plugin's own log. No-op once the plugin emits the
    chat telemetry frame itself, or when SFTP isn't configured. Never raises."""
    try:
        if _CHAT_FRAME_SEEN[0]:
            return
        now = time.monotonic()
        if now < _chat_tail_next[0]:
            return
        _chat_tail_next[0] = now + CHAT_TAIL_EVERY
        if _chat_tail[0] is None:
            host, port = SFTP_HOST, SFTP_PORT
            if not (host and SFTP_USER and SFTP_PASS):
                _chat_tail_next[0] = now + 300      # unconfigured: stop hammering
                return
            _chat_tail[0] = SFTPConsoleSource(host, port, SFTP_USER, SFTP_PASS, PLUGIN_LOG_PATH)
        for line in _chat_tail[0].poll():
            m = PLUGIN_CHAT_RE.search(line)
            if not m:
                continue
            sid, msg = m.group("sid"), m.group("msg").strip()
            if not msg or chat_seen_recently(sid, msg):
                continue
            nm = display_name(sid, m.group("name"))
            if LOG_CONVERSATION:
                ally = "" if m.group("scope") == "all" else "(ally) "
                activity(f"{ally}{nm}: {msg}", "CHAT")
    except Exception as e:                          # noqa: BLE001 - never break the loop
        print(f"[chat-tail] {e}")


# ----------------------------------------------------------------------------
# Vote logic
# ----------------------------------------------------------------------------

def mission_variant(name):
    """The trailing weather/time tag, e.g. 'Night' from '... - Night'."""
    return name.rsplit(" - ", 1)[-1].strip()


def is_dark(name):
    """True if this map's variant is one we cap with MAX_DARK_PER_VOTE."""
    return mission_variant(name) in DARK_VARIANTS


def friendly_label(name):
    """Shorter label for chat, e.g. 'Escalation BDF - Night'. Kept CLEAN (no [PVP] tag) so it stays a
    stable dedup/verify key; the [PVP] tag is added at OUTPUT surfaces by mission_display()/pvp_prefix()."""
    return name.replace(" Co-op as ", " ")


# ── PVP LABEL (shared contract): one classifier; a "[PVP] " prefix on EVERY mission name the bot shows
# outside the vote ballot (next-mission line, match summary, dashboard + leaderboard). Vote lines use
# a kind suffix instead — see ballot_kind_suffix().
_PVP_NAME_SET = set(PVP_MISSIONS)
PVP_TAG_COLOURED = "<color=#FF5555>[PVP]</color> "
PVP_TAG_PLAIN    = "[PVP] "
# Vote-only kind tags (after the mission name). PvE = pastel blue; PvP = same red as PVP_TAG_COLOURED.
PVE_KIND_COLOURED = "<color=#9AD1FF>[PvE]</color>"
PVP_KIND_COLOURED = "<color=#FF5555>[PvP]</color>"
# UPPERCASE bracket tags for the cross-server status line, in the same two colours as everywhere else
# (PvP red, PvE pastel blue) so a player reads the same signal there as on a ballot.
PVP_TAG_ONLY_COLOURED = "<color=#FF5555>[PVP]</color>"
PVE_TAG_ONLY_COLOURED = "<color=#9AD1FF>[PVE]</color>"


def is_pvp(name):
    """True iff `name` is a PvP mode - a built-in one, OR one of its time-of-day variants.

    2026-07-31: the variants ("Escalation - Dawn", "Carrier Duel - Night", ...) arrive through the
    CUSTOM-mission path, so this classifier - which only knew the six base names - called every one of
    them PvE. Consequences, all on the very first ballot: the vote line tagged a PvP map "[PvE]" in
    pastel blue, mission_display dropped the "[PVP]" prefix everywhere else, and open_vote's
    g_coop/g_pvp split put a pinned PvP variant in the CO-OP bucket, where it ate a PvE slot, was
    dropped entirely under force-PvP, and could still be drawn a second time by its own family.
    One classifier, so fixing it here fixes every surface at once."""
    if name in _PVP_NAME_SET:
        return True
    base = str(name or "").split(" - ", 1)[0].strip()    # "<mode> - <variant>" -> "<mode>"
    return bool(base) and base in _PVP_NAME_SET


def pvp_prefix(name, coloured=True):
    """The '[PVP] ' prefix for a mission name (empty for non-PvP). Coloured for chat, plain for logs/keys."""
    if not is_pvp(name):
        return ""
    return PVP_TAG_COLOURED if coloured else PVP_TAG_PLAIN


def ballot_kind_suffix(name, coloured=True):
    """Vote-line kind tag after the mission name: [PvE] or [PvP] only (no flavor descriptors)."""
    if is_pvp(name):
        return (" " + PVP_KIND_COLOURED) if coloured else " [PvP]"
    return (" " + PVE_KIND_COLOURED) if coloured else " [PvE]"


def mission_display(name, coloured=True):
    """A mission's friendly label with the shared [PVP] prefix applied when it's a PvP mode."""
    return pvp_prefix(name, coloured) + friendly_label(name)


# --- Mission pool (votemap): owners toggle which missions appear in the vote (e.g. PvP-only, no Terminal).
# Stored in mission_pool.json as the DISABLED set. Server flavour, not a gameplay-locked setting, so it's
# owner=missionpool.
MISSION_POOL_FILE = os.path.join(_BASE_DIR, "mission_pool.json")
_mission_disabled = set()


def _all_pool_missions():
    """[(name, category)] for every toggleable mission: the co-op variants + the stock modes/scenarios."""
    out = [(m, "Escalation Co-op") for m in ESCALATION_MISSIONS]
    out += [(m, "Terminal Control Co-op") for m in TERMINAL_CONTROL_MISSIONS]
    out += [(p[1], "PvP") for p in PVP_OPTIONS]
    out += [(m, "Built-in Co-op") for m in BUILTIN_COOP_MISSIONS]
    return out


def load_mission_pool():
    global _mission_disabled
    try:
        with open(MISSION_POOL_FILE, encoding="utf-8") as f:
            _mission_disabled = set(json.load(f).get("disabled", []))
    except (OSError, ValueError):
        _mission_disabled = set()


def save_mission_pool():
    try:
        tmp = MISSION_POOL_FILE + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump({"disabled": sorted(_mission_disabled)}, f, indent=1)
        os.replace(tmp, MISSION_POOL_FILE)
    except OSError:
        pass


def mission_family(name):
    """A co-op mission WITHOUT its time-of-day variant: the scenario AND the side, together.

        "Terminal Control Co-op as BDF - Clear Skies"  ->  "Terminal Control Co-op as BDF"
        "Terminal Control Co-op as PALA - Dawn"        ->  "Terminal Control Co-op as PALA"
        "Escalation Co-op as BDF - Dusk"               ->  "Escalation Co-op as BDF"

    Owner's rule (2026-08-01), verbatim: "if a mission was bdf terminal pve, you just don't show any
    bdf terminal missions on the pool... there are 4 pala terminal pve missions that could appear."

    So the exclusion is scoped to THIS scenario on THIS side - the 4 time-of-day variants of it.
    Terminal PALA is unaffected, and so is Escalation BDF.

    PvE ONLY. Callers must gate on `not is_pvp(...)`. Splitting a PvP name on " - " the same way
    yields "[PVP] Terminal Control", and treating THAT as a family banned every PvP Terminal variant
    from the following ballot - which is exactly what happened live on Server 2 on 2026-08-01: a PvP
    vote came up with no Terminal options at all. Consecutive PvP repeats are allowed by design."""
    s = str(name or "")
    i = s.find(" - ")
    return s[:i] if i > 0 else s


def mission_enabled(name):
    return name not in _mission_disabled


def set_mission_enabled(name, on):
    if name not in {n for n, _ in _all_pool_missions()}:
        return False
    if on:
        _mission_disabled.discard(name)
    else:
        _mission_disabled.add(name)
    save_mission_pool()
    return True


def mission_pool_state():
    return [{"name": n, "label": friendly_label(n), "cat": c, "on": mission_enabled(n)}
            for n, c in _all_pool_missions()]


# ── Votemap (dynamic vote pool) configuration ─────────────────────────────────────────────────────
# The end-of-mission / !votemap ballot is sized from TWO pools INDEPENDENTLY so the count of each map
# TYPE is explicit (the old single "ballot_size" only counted the co-op maps, which was confusing):
#   * coop_count  PvE co-op (+ enabled custom) maps  — drawn from _votemap_pool()
#   * pvp_count   PvP built-in modes                 — drawn from the ENABLED PVP_OPTIONS only
# Default 4 + 2 = the regular 6-option ballot. Each pool has a selection MODE that controls the
# likelihood mix (balanced/random/weighted for co-op; fixed/random/weighted for PvP) and an optional
# per-category / per-mode weight table for "weighted". A high-population rule can override the split
# into a PvP-heavy ballot once enough players are online (force_pvp_*). Decoupling pvp_count from the
# pool toggles is deliberate: enabling extra built-in modes in the Mission Pool enlarges what the PvP
# slots can draw from WITHOUT growing the ballot (so the regular 6 stays 6).
VOTEMAP_CONFIG_FILE = os.path.join(_BASE_DIR, "votemap_config.json")
_VOTEMAP_DEFAULTS = {
    "enabled":           True,       # master kill-switch: off => no auto map-vote (server rotation advances)
    "coop_count":        4,          # PvE co-op (+custom) maps on the ballot
    "pvp_count":         2,          # PvP built-in modes on the ballot
    "coop_mode":         "balanced", # balanced (even round-robin) | random (uniform) | weighted
    "pvp_mode":          "fixed",    # fixed (PVP_OPTIONS order) | random | weighted | family
                                     # family = round-robin the slots across PVP_FAMILY_ORDER, drawing a
                                     # DIFFERENT time-of-day variant per slot (2+2+2 on a 6-slot ballot)
    "include_pvp":       True,       # master toggle for the PvP slots
    "include_custom":    True,       # let enabled custom USER missions into the co-op pool
    "coop_weights":      {},         # {category: relative_likelihood} for coop_mode == weighted
    "pvp_weights":       {},         # {pvp_mission_name: relative_likelihood} for pvp_mode == weighted
    "mission_weights":   {},         # {mission_name: relative_likelihood} PER-MAP appearance chance for the
                                     # random fill slots (all coop modes' within-category pick, random flat
                                     # pick, PvP random, and multiplied into PvP weighted). 1 = normal,
                                     # 0 = never offered (unless pinned), 2 = twice as likely. Webcc shows
                                     # these normalized as a percentage. Guaranteed pins bypass weights.
    "guaranteed":        [],         # mission NAMES always pinned onto every ballot (they count toward the
                                     # relevant type's slot count; like the always-on PvP pair, generalised)
    "no_repeat":         False,      # PvE server rule (owner 2026-08-22): a co-op mission that has played
                                 # cannot appear on another ballot until EVERY enabled co-op mission has
                                 # played once (the cycle then restarts, still barring a back-to-back).
    "avoid_recent":      0,          # don't re-offer the last N winning maps (0 = off; only the exact-ballot
                                     # anti-repeat applies). Guaranteed missions are exempt.
    "force_pvp_enabled": True,       # high-pop override: force a PvP-heavy ballot (Tomo wants this ON)
    "force_pvp_players": 24,         # ... once at least this many players are online
    "force_pvp_coop":    0,          # co-op maps while forcing (0 = PvP-only)
    "force_pvp_pvp":     6,          # PvP modes while forcing (capped by how many are enabled)
    "coop_minutes":      180,        # match length (min) the bot assigns to co-op / custom maps (10800s=180 was fixed)
    "builtin_minutes":   180,        # match length (min) the bot assigns to BUILT-IN ops/scenarios — set this
                                     # to ~180 so a built-in isn't stuck on its 2h server default, leaving the
                                     # bot room to end a timed-out match and open the next-map vote.
    "boot_map":          "",         # FIX 4: mission NAME the server rotates to on (re)start / when a vote makes
                                     # no pick. "" = leave the next mission to the server's own rotation (default).
    # NB: the vote-TIMING knobs (MAP_VOTE_DURATION / POST_VOTE_MAP_CHANGE_DELAY) are deliberately NOT here —
    # they persist in the deploy-protected .nost-data/votemap_timing.json (see _load_vote_timing), so a code
    # deploy that overwrites this ROOT folder can't reset them (FIX 3).
}
_COOP_CATEGORIES = ("Escalation", "Terminal Control", "Built-in Co-op", "Custom")   # weightable co-op pool keys


def _vm_int(v, default, lo, hi):
    try:
        return max(lo, min(hi, int(v)))
    except (TypeError, ValueError):
        return default


def _vm_weights(v):
    """Normalize a {name: number>=0} weight table; drop junk. Empty dict == all-equal."""
    out = {}
    if isinstance(v, dict):
        for k, w in v.items():
            try:
                w = float(w)
            except (TypeError, ValueError):
                continue
            # w >= 0 already rejects NaN; also reject +inf so it can't dominate weighted sampling
            if isinstance(k, str) and 0 <= w != float("inf"):
                out[k] = w
    return out


def _vm_strlist(v):
    """Normalize a list of mission-name strings (dedup, preserve order, drop blanks/junk)."""
    out, seen = [], set()
    if isinstance(v, list):
        for x in v:
            if isinstance(x, str) and x.strip() and x not in seen:
                seen.add(x)
                out.append(x)
    return out


# the dashboard tick calls _votemap_cfg() several times per pass, so cache the parsed config keyed on
# the file's (mtime_ns, size) and only re-read when the file actually changes. set_votemap_cfg() writes
# via os.replace and then drops the cache so its own save is always seen next call.
_vm_cfg_cache = None                       # ((mtime_ns, size), cfg) | None


def _votemap_cfg():
    global _vm_cfg_cache
    try:
        st = os.stat(VOTEMAP_CONFIG_FILE)
        stamp = (st.st_mtime_ns, st.st_size)
    except OSError:
        stamp = None
    if _vm_cfg_cache is not None and _vm_cfg_cache[0] == stamp:
        # copy nested containers too: callers decorate/replace values in place (votemap_cfg_state,
        # set_votemap_cfg) and must never leak those mutations back into the cache
        return {k: (dict(v) if isinstance(v, dict) else list(v) if isinstance(v, list) else v)
                for k, v in _vm_cfg_cache[1].items()}
    cfg = dict(_VOTEMAP_DEFAULTS)
    raw = {}
    try:
        with open(VOTEMAP_CONFIG_FILE, encoding="utf-8") as f:
            j = json.load(f)
        if isinstance(j, dict):
            raw = dict(j)
    except (OSError, ValueError):
        pass
    # migrate the v1 schema (ballot_size -> coop_count, mode -> coop_mode)
    if "coop_count" not in raw and "ballot_size" in raw:
        raw["coop_count"] = raw["ballot_size"]
    if "coop_mode" not in raw and "mode" in raw:
        raw["coop_mode"] = raw["mode"]
    for k in cfg:
        if k in raw:
            cfg[k] = raw[k]
    _np = len(PVP_OPTIONS)
    cfg["coop_count"]        = _vm_int(cfg["coop_count"], 4, 0, 12)
    cfg["pvp_count"]         = _vm_int(cfg["pvp_count"], 2, 0, _np)
    cfg["force_pvp_players"] = _vm_int(cfg["force_pvp_players"], 24, 1, 200)
    cfg["force_pvp_coop"]    = _vm_int(cfg["force_pvp_coop"], 0, 0, 12)
    cfg["force_pvp_pvp"]     = _vm_int(cfg["force_pvp_pvp"], 6, 0, _np)
    # clamp the per-type match-length timers HERE too (not just in mission_max_time), so votemap_cfg_state
    # shows the SAME value the bot actually assigns -> the webcc timer field can never disagree with reality.
    cfg["coop_minutes"]      = _vm_int(cfg["coop_minutes"], 180, 10, 600)
    cfg["builtin_minutes"]   = _vm_int(cfg["builtin_minutes"], 180, 10, 600)
    cfg["boot_map"]          = cfg["boot_map"] if isinstance(cfg.get("boot_map"), str) else ""   # FIX 4
    if cfg["coop_mode"] not in ("balanced", "random", "weighted"):
        cfg["coop_mode"] = "balanced"
    if cfg["pvp_mode"] not in ("fixed", "random", "weighted", "family", "each"):
        cfg["pvp_mode"] = "fixed"
    cfg["include_pvp"]    = bool(cfg["include_pvp"])
    cfg["include_custom"] = bool(cfg["include_custom"])
    cfg["no_repeat"]      = bool(cfg.get("no_repeat", False))
    cfg["enabled"]        = bool(cfg["enabled"])
    cfg["coop_weights"]   = _vm_weights(cfg["coop_weights"])
    cfg["pvp_weights"]    = _vm_weights(cfg["pvp_weights"])
    cfg["mission_weights"] = {k: min(100.0, w) for k, w in _vm_weights(cfg["mission_weights"]).items()}
    cfg["guaranteed"]     = _vm_strlist(cfg["guaranteed"])
    cfg["avoid_recent"]   = _vm_int(cfg["avoid_recent"], 0, 0, 10)
    # never let the NORMAL split collapse to an empty ballot via config alone (guaranteed missions also
    # backstop this, but a 0/0 split with nothing pinned would otherwise fall through to the safety net)
    if cfg["coop_count"] + (cfg["pvp_count"] if cfg["include_pvp"] else 0) < 1 and not cfg["guaranteed"]:
        cfg["coop_count"] = 1
    _vm_cfg_cache = (stamp, {k: (dict(v) if isinstance(v, dict) else list(v) if isinstance(v, list) else v)
                             for k, v in cfg.items()})
    return cfg


# per-key integer bounds (lo, hi); clamp at the SOURCE so the file never stores out-of-range values
_VOTEMAP_INT_BOUNDS = {
    "coop_count":        (0, 12),
    # >= len(PVP_OPTIONS) so the flat modes still fit, and >= 6 so a family ballot (2 per family across
    # three families) is always expressible even if PVP_OPTIONS is trimmed later.
    "pvp_count":         (0, max(6, len(PVP_OPTIONS))),
    "avoid_recent":      (0, 10),
    "force_pvp_players": (1, 200),
    "force_pvp_coop":    (0, 12),
    "force_pvp_pvp":     (0, len(PVP_OPTIONS)),
    "coop_minutes":      (10, 600),
    "builtin_minutes":   (10, 600),
}
_VOTEMAP_BOOL_KEYS = ("enabled", "include_pvp", "include_custom", "force_pvp_enabled", "no_repeat")
_VOTEMAP_ALIASES   = {"ballot_size": "coop_count", "mode": "coop_mode"}


def set_votemap_cfg(key, value):
    key = _VOTEMAP_ALIASES.get(key, key)            # accept v1 keys from an un-refreshed webcc
    if key not in _VOTEMAP_DEFAULTS:
        return False
    cfg = _votemap_cfg()
    if key in _VOTEMAP_INT_BOUNDS:
        lo, hi = _VOTEMAP_INT_BOUNDS[key]
        v = _vm_int(value, None, lo, hi)
        if v is None:
            return False
        cfg[key] = v
    elif key == "coop_mode":
        if str(value) not in ("balanced", "random", "weighted"):
            return False
        cfg[key] = str(value)
    elif key == "pvp_mode":
        if str(value) not in ("fixed", "random", "weighted", "family", "each"):
            return False
        cfg[key] = str(value)
    elif key == "coop_weights":
        allow = set(_COOP_CATEGORIES)
        cfg[key] = {k: w for k, w in _vm_weights(value).items() if k in allow}   # whitelist pool categories
    elif key == "pvp_weights":
        allow = {p[1] for p in PVP_OPTIONS}
        cfg[key] = {k: w for k, w in _vm_weights(value).items() if k in allow}   # whitelist built-in modes
    elif key == "mission_weights":
        w = {k: min(100.0, v) for k, v in _vm_weights(value).items()}
        if mission_audit_state().get("loaded"):
            allow = _votable_names()
            cfg[key] = {k: v for k, v in w.items() if k in allow}                # whitelist real votable maps
        else:
            # audit not loaded yet (every restart until the first SFTP scan): _votable_names() is missing
            # the custom USER missions; filtering now would drop their weights. Unknown names are inert.
            cfg[key] = w
    elif key == "guaranteed":
        allow = _votable_names()
        if mission_audit_state().get("loaded"):
            cfg[key] = [n for n in _vm_strlist(value) if n in allow]             # only pin real votable maps
        else:
            # audit not loaded yet (every bot restart until the first SFTP scan): _votable_names() is
            # missing the custom USER missions, so filtering now would PERMANENTLY drop those pins.
            # Keep unknown names; open_vote() re-validates per-ballot anyway.
            cfg[key] = _vm_strlist(value)
    elif key == "boot_map":
        # FIX 4: default/boot mission NAME. "" clears it. When the mission audit is loaded, only accept a
        # real votable map (reject unknown so a stale UI value can't silently persist); before the first
        # audit scan, keep it as-is (apply_boot_map_rotation pins whatever is stored once it runs).
        nm = str(value or "").strip()
        if nm and mission_audit_state().get("loaded") and nm not in _votable_names():
            return False
        cfg[key] = nm
    elif key in _VOTEMAP_BOOL_KEYS:
        cfg[key] = value if isinstance(value, bool) else str(value).lower() in ("1", "true", "on", "yes")
    else:
        return False
    cfg.pop("ballot_size", None)            # strip any legacy v1 keys so the file converges to clean v2
    cfg.pop("mode", None)
    try:
        tmp = VOTEMAP_CONFIG_FILE + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(cfg, f, indent=1)
        os.replace(tmp, VOTEMAP_CONFIG_FILE)
    except OSError:
        return False
    global _vm_cfg_cache
    _vm_cfg_cache = None                    # drop the mtime cache so this save is read back immediately
    return True


def mission_max_time(name):
    """Match length (SECONDS) the bot assigns to a mission NAME when it queues it. Built-in ops/
    scenarios and co-op/custom maps have INDEPENDENT operator-set timers (votemap_config
    builtin_minutes / coop_minutes), so a built-in can be given the same ~3h the co-op maps get
    instead of running on the server's 2h default. Clamped 10min..10h; falls back to MISSION_MAX_TIME."""
    is_builtin = name in PVP_MISSIONS or name in BUILTIN_COOP_MISSIONS or name in MISSION_KEY_CANDIDATES
    mins = _votemap_cfg().get("builtin_minutes" if is_builtin else "coop_minutes")
    try:
        return max(10, min(600, int(mins))) * 60
    except (TypeError, ValueError):
        return MISSION_MAX_TIME


# ── FIX 3: ONE coherent vote-timing model ──────────────────────────────────────────────────────
# The operator sets exactly two knobs: MAP_VOTE_DURATION and POST_VOTE_MAP_CHANGE_DELAY (persisted in the
# deploy-protected .nost-data/votemap_timing.json). The effective post-mission delay is DERIVED = vote +
# delay, so the map change always fires POST_VOTE_MAP_CHANGE_DELAY seconds AFTER the ballot closes and can
# NEVER be scheduled before the vote ends. VOTE_DURATION / APPROVAL_DURATION are aliases of the vote knob.
def vote_duration():
    """Single ballot length (s) for BOTH the end-of-match map vote AND the !votemap ballot."""
    return MAP_VOTE_DURATION


def post_vote_delay():
    """Seconds after the ballot closes before the winning map actually loads."""
    return POST_VOTE_MAP_CHANGE_DELAY


def _effective_pmd():
    """Derived DedicatedServerConfig PostMissionDelay = vote length + post-vote delay. This is the value
    the bot keeps the server's real PostMissionDelay synced to (sync_effective_pmd), so the raw delay can
    never be shorter than the vote (the exact breakage the owner hit: vote=60 but mission-delay=45)."""
    return int(MAP_VOTE_DURATION) + int(POST_VOTE_MAP_CHANGE_DELAY)


def votemap_cfg_state():
    c = _votemap_cfg()
    # FIX 3: surface the two live timing knobs (bot-owned, persisted in .nost-data) + the DERIVED effective
    # delay so the webcc/settings render the real current values (and cc_web reads them back, never a stale
    # catalogue 60). The live server PostMissionDelay is exposed too, purely for a drift display.
    c["map_vote_duration"] = int(MAP_VOTE_DURATION)
    c["post_vote_change_delay"] = int(POST_VOTE_MAP_CHANGE_DELAY)
    c["effective_pmd"] = _effective_pmd()
    try:
        pmd = (_srvcfg_cache.get("values") or {}).get("PostMissionDelay")
        c["post_mission_delay"] = float(pmd) if pmd not in (None, "") else None
    except (TypeError, ValueError):
        c["post_mission_delay"] = None
    c["boot_map_label"] = friendly_label(c["boot_map"]) if c.get("boot_map") else ""   # FIX 4
    # convenience for the webcc: live totals + the rows the weight/force/guaranteed UI needs
    pvp_n = c["pvp_count"] if c["include_pvp"] else 0
    c["total_normal"] = c["coop_count"] + pvp_n
    c["total_forced"] = c["force_pvp_coop"] + (c["force_pvp_pvp"] if c["include_pvp"] else 0)
    c["pvp_options"]  = [{"name": p[1], "on": mission_enabled(p[1])} for p in PVP_OPTIONS]
    c["pvp_enabled_count"] = sum(1 for p in PVP_OPTIONS if mission_enabled(p[1]))
    pool = _votemap_pool()
    c["coop_categories"]   = [cat for cat in _COOP_CATEGORIES if cat in pool]
    c["coop_available"]    = sum(len(ms) for ms in pool.values())     # enabled co-op/custom maps in the pool
    # the full votable universe (for the "add guaranteed" picker) + friendly labels for the current pins
    votable = [{"name": n, "label": friendly_label(n), "cat": cat} for n, cat in _all_pool_missions()]
    votable += [{"name": n, "label": friendly_label(n), "cat": "Custom"} for n in _enabled_custom_names()]
    c["votable"] = votable
    c["guaranteed_labels"] = [{"name": n, "label": friendly_label(n),
                               "pvp": is_pvp(n), "on": mission_enabled(n)} for n in c["guaranteed"]]
    return c


def _enabled_custom_names():
    """Enabled custom USER missions (from the mission audit) -> votable. Workshop missions are excluded
    from the in-game vote (numeric id / Workshop group); they still cycle via the server rotation."""
    a = mission_audit_state() or {}
    return [u.get("name") for u in (a.get("unofficial") or [])
            if u.get("enabled") and u.get("name") and u.get("group") != "Workshop"]


load_mission_pool()


# --- Server message manager: owner-defined automated chat messages with triggers. Stored in
# server_messages.json. The webcc Messages modal queues "servermsg" CRUD ops; the BOT owns the file
# (single writer) and reflects state in the dashboard, exactly like the mission pool. Triggers:
#   interval    -> every N minutes while players are online (and the server is idle, not mid-vote)
#   clock       -> once daily at HH:MM (server local time)
#   match_start -> when a genuinely new match begins
#   match_end   -> when a match ends
SERVER_MESSAGES_FILE = os.path.join(_BASE_DIR, "server_messages.json")
_server_messages = []            # list of {id,text,trigger,interval_min,at,color,enabled}
_msg_last_fired = {}             # id -> epoch  (interval throttle; runtime only, not persisted)
_msg_last_day = {}               # id -> "YYYY-MM-DD" already-fired marker for clock triggers
_msg_id_seq = 0
MSG_TRIGGERS = ("interval", "clock", "match_start", "match_end")
MSG_TEXT_MAX = 240
MSG_MAX_COUNT = 40
_MSG_HEX_RE = re.compile(r"^#[0-9A-Fa-f]{6}$")
_MSG_HHMM_RE = re.compile(r"^([01]?\d|2[0-3]):[0-5]\d$")
_MSG_ID_RE = re.compile(r"^msg_[0-9a-z]+$")


def _new_msg_id():
    global _msg_id_seq
    _msg_id_seq += 1
    return "msg_" + format(int(time.time() * 1000), "x") + format(_msg_id_seq, "x")


def _balance_color_tags(t):
    """Drop a trailing unterminated tag (a hard length-cap can cut a <color=#hex> in half,
    which would corrupt every following chat line) and auto-close any dangling <color> tags."""
    t = re.sub(r"</?c(?:o(?:l(?:o(?:r(?:=#?[0-9A-Fa-f]{0,6})?)?)?)?)?$", "", t)   # strip a trailing cut color-tag prefix (<c.. or </c..); a bare '<' or real text is kept
    opens = len(re.findall(r"<color=#[0-9A-Fa-f]{6}>", t))
    closes = len(re.findall(r"</color>", t))
    if opens > closes:
        t += "</color>" * (opens - closes)
    return t


def _msg_sanitize_text(text):
    """One-line, control-char-free, length-capped chat text (the message goes straight to rc.say).
    Tag-aware: the length cap never leaves a half-cut <color> tag, and dangling tags auto-close."""
    t = re.sub(r"[\x00-\x1f\x7f]", " ", str(text if text is not None else ""))
    t = re.sub(r"\s+", " ", t).strip()
    return _balance_color_tags(t[:MSG_TEXT_MAX])


def _msg_clean(m):
    """Coerce one raw message dict into a validated record, or None if it has no usable text."""
    if not isinstance(m, dict):
        return None
    text = _msg_sanitize_text(m.get("text"))
    if not text:
        return None
    trig = str(m.get("trigger") or "interval")
    if trig not in MSG_TRIGGERS:
        trig = "interval"
    try:
        iv = int(float(m.get("interval_min", 30)))
    except (TypeError, ValueError):
        iv = 30
    iv = max(1, min(1440, iv))
    at = str(m.get("at") or "").strip()
    if _MSG_HHMM_RE.match(at):
        hh, mm = at.split(":")
        at = f"{int(hh):02d}:{mm}"
    else:
        at = "12:00"
    color = str(m.get("color") or "").strip()
    if not _MSG_HEX_RE.match(color):
        color = ""
    if re.search(r"<color=#[0-9A-Fa-f]{6}>", text):    # per-word colours already in the text -> no outer wrap (avoid bleed)
        color = ""
    mid = str(m.get("id") or "")
    if not _MSG_ID_RE.match(mid):
        mid = _new_msg_id()
    return {"id": mid, "text": text, "trigger": trig, "interval_min": iv,
            "at": at, "color": color, "enabled": bool(m.get("enabled", True))}


def load_server_messages():
    global _server_messages
    try:
        with open(SERVER_MESSAGES_FILE, encoding="utf-8") as f:
            raw = json.load(f).get("messages", [])
    except (OSError, ValueError):
        raw = []
    out = []
    for m in raw if isinstance(raw, list) else []:
        c = _msg_clean(m)
        if c:
            out.append(c)
    _server_messages = out


def save_server_messages():
    try:
        tmp = SERVER_MESSAGES_FILE + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump({"messages": _server_messages}, f, indent=1)
        os.replace(tmp, SERVER_MESSAGES_FILE)
    except OSError:
        pass


def server_messages_state():
    return [dict(m) for m in _server_messages]


# ── editable rank ladder (webcc "Ranks" modal) ─────────────────────────────────────
def _rank_ladder_validate(ranks, template, prestige_template=None):
    """Validate + normalise a proposed ladder. Returns (rows_tuples, template, prestige_template, warnings)
    or raises ValueError. rows = list of (threshold, name, abbr, colour). An EMPTY list is valid:
    it means the rank ladder feature is OFF (the shipped default)."""
    if not isinstance(ranks, list):
        raise ValueError("ranks must be a list")
    rows = []
    for r in ranks:
        if not isinstance(r, dict):
            raise ValueError("bad rank row")
        try:
            th = int(float(r.get("threshold", 0)))
        except (TypeError, ValueError):
            raise ValueError("threshold must be a number")
        if th < 0:
            th = 0
        name = str(r.get("name") or "").strip()
        abbr = str(r.get("abbr") or "").strip()
        color = str(r.get("color") or "").strip()
        if not name or any(c in name for c in "|\n\r"):
            raise ValueError("a rank name is required and cannot contain | or newlines")
        name = name[:40]
        if not abbr or any(c in abbr for c in "|[]\n\r \t"):
            raise ValueError("an abbreviation is required and cannot contain spaces, [, ], | or newlines")
        abbr = abbr[:12]
        if not _MSG_HEX_RE.match(color):
            raise ValueError(f"the colour for '{name}' must be #RRGGBB")
        rows.append([th, name, abbr, color])
    rows.sort(key=lambda x: x[0])
    if rows:
        rows[0][0] = 0                                   # the lowest rank is always the floor (0 points)
    for i in range(1, len(rows)):
        if rows[i][0] <= rows[i - 1][0]:
            raise ValueError("thresholds must be strictly ascending and unique")
    if len({r[1] for r in rows}) != len(rows):
        raise ValueError("rank names must be unique")
    if len({r[2] for r in rows}) != len(rows):
        raise ValueError("abbreviations must be unique")
    warnings = []
    if any(len(r[2]) <= 2 for r in rows):
        warnings.append("a very short abbreviation can be mistaken for a clan tag in chat")
    tmpl = str(template if template is not None else DEFAULT_RANKUP_TEMPLATE).strip()
    if not tmpl:
        raise ValueError("the rank-up template cannot be empty")
    # {name} is only REQUIRED while rank-ups are broadcast: a public line has to say who ranked up.
    # Since 1.3.15 the default is delivered privately to that player, so the name is redundant there -
    # and the second-person default ("You've ranked up! X -> Y") legitimately has no {name}. Keeping the
    # hard requirement made the shipped default fail its own validator, which would have rejected every
    # ladder save and, on a boot that needed to seed or migrate rank_ladder.json, thrown inside the
    # loader. Enforced only where it actually matters.
    # NOTE the ordering trap: load_rank_ladder() runs at IMPORT time, well before _sysmsg_rec is
    # defined further down the module, so calling it here unguarded raised NameError and killed the bot
    # on startup (did exactly that on 2026-07-31 - ast.parse passes, because it is a RUNTIME error).
    # Fail open to "private", which is the shipped default and the case that needs no {name}.
    _rankup_public = False
    try:
        _rankup_public = bool(_sysmsg_rec("rankup").get("public", False))
    except Exception:                                   # noqa: BLE001 - incl. NameError at import time
        _rankup_public = False
    if "{name}" not in tmpl and _rankup_public:
        raise ValueError("a PUBLIC rank-up template must include {name} so players can tell who ranked up")
    if tmpl.count("<color") != tmpl.count("</color>"):
        raise ValueError("the rank-up template has unbalanced <color> tags")
    if len(tmpl) > 240:
        raise ValueError("the rank-up template is too long")
    ptmpl = str(prestige_template if prestige_template is not None else DEFAULT_PRESTIGE_TEMPLATE).strip()
    if not ptmpl:
        raise ValueError("the prestige tag cannot be empty")
    if "{n}" not in ptmpl:
        raise ValueError("the prestige tag must include {n}")
    if len(ptmpl) > 48:
        raise ValueError("the prestige tag is too long")
    if ptmpl.count("<color") != ptmpl.count("</color>"):
        raise ValueError("the prestige tag has unbalanced <color> tags")
    return [tuple(r) for r in rows], tmpl, ptmpl, warnings


def save_rank_ladder(ranks, template, prestige_template=None):
    """Atomic write of the ladder (+ .bak). ranks = list of (threshold, name, abbr, colour)."""
    try:
        payload = {"version": 1, "rankup_template": template,
                   "prestige_template": prestige_template if prestige_template is not None else PRESTIGE_TEMPLATE,
                   "ranks": [{"threshold": r[0], "name": r[1], "abbr": r[2], "color": r[3]} for r in ranks]}
        if os.path.exists(RANK_LADDER_FILE):
            try:
                with open(RANK_LADDER_FILE, "rb") as _src, open(RANK_LADDER_FILE + ".bak", "wb") as _dst:
                    _dst.write(_src.read())
            except OSError:
                pass
        tmp = RANK_LADDER_FILE + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(payload, f, indent=1)
        os.replace(tmp, RANK_LADDER_FILE)
    except OSError:
        pass


def load_rank_ladder():
    """Load rank_ladder.json into RANKS + RANKUP_TEMPLATE (fail-open to the built-in default).
    Seeds the file with today's ladder on first run. Resets the rank-tag regex cache so a
    renamed abbr cannot leak its old tag into PLAYER_NAMES / ranks.json via _strip_rank_tag."""
    global RANKS, RANKUP_TEMPLATE, PRESTIGE_TEMPLATE, _RANK_TAG_RE
    try:
        with open(RANK_LADDER_FILE, encoding="utf-8") as f:
            data = json.load(f)
        if not isinstance(data, dict):
            raise ValueError("rank_ladder.json root must be an object")
        rows, tmpl, ptmpl, _ = _rank_ladder_validate(data.get("ranks"), data.get("rankup_template"),
                                                      data.get("prestige_template"))  # missing key -> default (fail-open)
        RANKS = rows
        # One-shot migrate pre-multi-color default so live installs pick up Tomo's format without a manual Ranks save.
        if tmpl in _LEGACY_RANKUP_TEMPLATES:
            tmpl = DEFAULT_RANKUP_TEMPLATE
            save_rank_ladder(rows, tmpl, ptmpl)
        RANKUP_TEMPLATE = tmpl
        PRESTIGE_TEMPLATE = ptmpl
    except FileNotFoundError:
        RANKS = list(DEFAULT_RANKS)
        RANKUP_TEMPLATE = DEFAULT_RANKUP_TEMPLATE
        PRESTIGE_TEMPLATE = DEFAULT_PRESTIGE_TEMPLATE
        save_rank_ladder(RANKS, RANKUP_TEMPLATE, PRESTIGE_TEMPLATE)   # seed today's ladder verbatim (no visible change)
    except (OSError, ValueError, TypeError, AttributeError) as e:
        print(f"[rank-ladder] using the default ladder ({e})")
        RANKS = list(DEFAULT_RANKS)
        RANKUP_TEMPLATE = DEFAULT_RANKUP_TEMPLATE
        PRESTIGE_TEMPLATE = DEFAULT_PRESTIGE_TEMPLATE
    _RANK_TAG_RE = None


def rank_ladder_state():
    return {"rankup_template": RANKUP_TEMPLATE,
            "prestige_template": PRESTIGE_TEMPLATE,
            "ranks": [{"threshold": r[0], "name": r[1], "abbr": r[2], "color": r[3]} for r in RANKS]}


def rank_ladder_apply(payload):
    """Validate + persist + rebuild RANKS in place. Returns {ok, error?, warnings?}. The caller
    pushes plugin_ranks + logs activity on success (cc_web 'ok' means queued, not yet applied)."""
    global RANKS, RANKUP_TEMPLATE, PRESTIGE_TEMPLATE, _RANK_TAG_RE
    try:
        rows, tmpl, ptmpl, warnings = _rank_ladder_validate((payload or {}).get("ranks"),
                                                             (payload or {}).get("rankup_template"),
                                                             (payload or {}).get("prestige_template"))
    except (ValueError, TypeError, AttributeError) as e:
        return {"ok": False, "error": str(e)}
    RANKS = rows
    RANKUP_TEMPLATE = tmpl
    PRESTIGE_TEMPLATE = ptmpl
    _RANK_TAG_RE = None
    save_rank_ladder(RANKS, RANKUP_TEMPLATE, PRESTIGE_TEMPLATE)
    return {"ok": True, "warnings": warnings}


def rankup_line(name, rname, abbr, color, old_abbr="", old_color="#FFFFFF"):
    """Render the configurable rank-up announcement. Strips < > from the player name so a
    hostile display name cannot hijack the surrounding colour tags.
    Substitutes {old_color}/{old_abbr} before {color}/{abbr} so multi-color templates stay intact."""
    safe = str(name).replace("<", "").replace(">", "")
    tmpl = RANKUP_TEMPLATE or DEFAULT_RANKUP_TEMPLATE     # never broadcast a blank line
    oc = old_color or "#FFFFFF"
    oa = str(old_abbr or "")
    try:
        return (tmpl.replace("{old_color}", oc).replace("{old_abbr}", oa)
                .replace("{color}", color).replace("{name}", safe)
                .replace("{rank}", rname).replace("{abbr}", abbr))
    except Exception:                                    # noqa: BLE001 - never break a rank-up
        return (f"<color=#FFFFFF>** RANK UP **</color> "
                f"<color={oc}>{safe} {oa}</color> "
                f"<color=#FFFFFF>-></color> "
                f"<color={color}>{rname} - {abbr}</color>")


def announce_rankup(rc, sid, name, idx, old_idx=None):   # uses tell_player(), defined further down
    """Post the configurable rank-up chat line for RANKS[idx], UNLESS
    rank-up announcements are switched off (webcc Messages tab). Prestige lives in the
    {abbr} tag (e.g. OFFCDT - 1*), never a name suffix. old_idx is the rank left behind
    (from combined_rankup before the write); defaults to idx-1. Only the chat line is gated --
    callers still record the [RANK] activity line + push the updated name tag. Never raises."""
    if not sysmsg_on("rankup"):
        return
    try:
        _, rname, abbr, color = RANKS[idx]
        if old_idx is None:
            old_idx = max(0, int(idx) - 1)
        old_idx = max(0, min(int(old_idx), len(RANKS) - 1))
        _, old_rname, old_abbr_raw, old_color = RANKS[old_idx]
        pc = prestige_count(sid)
        new_label = prestige_label(abbr, rname, pc)
        old_label = prestige_label(old_abbr_raw, old_rname, pc)
        line = rankup_line(str(name), rname, new_label, color, old_label, old_color)
        # PRIVATE by default (owner, 2026-07-30): a promotion is that player's business, and a busy
        # server was pushing everyone else's rank-ups through everyone's chat. Set the sysmsg 'rankup'
        # record's "public" field to true to broadcast it again.
        if bool(_sysmsg_rec("rankup").get("public", False)):
            rc.say(line)
        else:
            tell_player(sid, line)
    except Exception as e:                               # noqa: BLE001
        print(f"[rankup] announce error: {e}")


# ── cross-server shared ranks (write-own-file aggregate; display only) ───────────────
# A host running several of these servers can point them all at one shared directory; each
# bot keeps writing its OWN local ranks.json unchanged (the ms-baseline math, ledger and
# --audit invariant are NEVER touched) and additionally publishes a copy as ranks_<id>.json
# into the share. A combined leaderboard sums points per SteamID across those files at READ
# time only. No lock, no merge, no foreign-file mutation -> zero concurrent-writer hazard.
SHARED_RANKS_FILE    = os.path.join(_BASE_DIR, "shared_ranks.json")
SHARED_RANKS_ENABLED = False
SHARED_RANKS_DIR     = ""
SERVER_INSTANCE_ID   = ""
_SHARED_PUB_AT       = 0.0           # last aggregate publish (throttle)
_SHARED_BOARD_CACHE  = ([], 0.0)     # (rows, computed_at): cache the combined board off the 1Hz dashboard

# #2 daemon-thread shared I/O: the publish into the (possibly slow/locked/network) shared dir runs
# on a background daemon, NEVER on the bot's main loop. maybe_publish_aggregate()/enable just set a
# pending flag; the worker drains it. Concurrency-safe by construction (write-own-file + atomic replace).
_SHARED_PUB_PENDING  = False         # set by the throttle / enable; cleared by the daemon after a publish


def _shared_pub_worker():
    """Daemon: publishes this server's rankshare file off the main loop so a slow/locked shared
    folder can never stall the bot tick. publish_ranks_aggregate() is already OSError-fail-open;
    this loop additionally swallows everything so the daemon can never die."""
    global _SHARED_PUB_PENDING, _OTHER_RANKS_CACHE
    while True:
        try:
            pending = _SHARED_PUB_PENDING
            _SHARED_PUB_PENDING = False
            if pending:
                publish_ranks_aggregate()
            publish_presence()                # cheap + on its OWN throttle: presence must stay fresh (~20s)
                                              # even when ranks are quiet, or the peer line goes stale/silent
            if SHARED_RANKS_ENABLED:          # #XSRV-2: keep the READ caches warm OFF the main loop so a
                _OTHER_RANKS_CACHE = (_compute_other_ranks(), time.time())   # rank display/award never globs the share inline
                globals()["_OTHER_PRESTIGE_CACHE"] = (_compute_other_prestige(), time.time())
                try:
                    shared_ranks_state()      # warms the board (30s) + peer-count (30s) caches off-loop too
                except Exception:             # noqa: BLE001
                    pass
        except Exception:                     # noqa: BLE001 - a publish failure must never kill the daemon
            pass
        time.sleep(2)


def _start_shared_pub_worker():
    """Start the publish daemon once (idempotent-ish; only called at load)."""
    try:
        import threading
        threading.Thread(target=_shared_pub_worker, name="shared-ranks-pub", daemon=True).start()
    except Exception as e:                     # noqa: BLE001 - sharing stays off rather than crash boot
        print(f"[shared-ranks] worker start failed: {e}")


def _gen_instance_id():
    """Deterministic per (host, install dir): two server folders -- even a verbatim clone -- get DIFFERENT
    ids, so they never publish the same rankshare_<id>.json and clobber each other (the folder-clone
    collision that silently breaks carry-over). Stable across restarts (same host+dir -> same id)."""
    import hashlib, socket
    seed = (socket.gethostname() + "|" + os.path.abspath(_BASE_DIR)).encode("utf-8", "replace")
    return hashlib.sha1(seed).hexdigest()[:12]


def save_shared_ranks_cfg(enabled, dir_, instance_id=None):
    try:
        iid = instance_id if instance_id is not None else (SERVER_INSTANCE_ID or _gen_instance_id())
        rec = {"enabled": bool(enabled), "dir": str(dir_ or ""), "instance_id": iid}
        # PRESERVE any key this writer does not own. _short_server_label() documents "label" as an
        # always-wins override that the owner sets by hand; a rewrite that dropped it would silently
        # revert the peer-server name the next time sharing was toggled in the panel.
        # utf-8-sig: a PowerShell 5.1 Set-Content hand-edit (how "label" gets set) writes a BOM,
        # and plain utf-8 json.load rejects it - which made this read fail and drop the label.
        try:
            with open(SHARED_RANKS_FILE, encoding="utf-8-sig") as f:
                old = json.load(f) or {}
            for k, v in old.items():
                if k not in rec:
                    rec[k] = v
        except (OSError, ValueError):
            pass
        tmp = SHARED_RANKS_FILE + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(rec, f, indent=1)
        os.replace(tmp, SHARED_RANKS_FILE)
    except OSError:
        pass


def load_shared_ranks_cfg():
    global SHARED_RANKS_ENABLED, SHARED_RANKS_DIR, SERVER_INSTANCE_ID
    data, unreadable = {}, False
    try:
        # utf-8-sig: a PowerShell 5.1 Set-Content hand-edit (how "label" gets set) writes a BOM,
        # and plain utf-8 json.load rejects it ("Unexpected UTF-8 BOM").
        with open(SHARED_RANKS_FILE, encoding="utf-8-sig") as f:
            data = json.load(f) or {}
    except FileNotFoundError:
        pass                                     # first boot: the persist below may create it fresh
    except (OSError, ValueError) as e:
        # EXISTING but unreadable file: sharing stays OFF for this session, but the file must be
        # LEFT ALONE. Falling through to the persist below used to overwrite the real config with
        # enabled=false/dir='' (and drop the hand-set 'label') over one bad read - silently killing
        # rankshare, link codes and presence on both servers.
        unreadable = True
        print(f"[shared-ranks] {SHARED_RANKS_FILE} unreadable ({e}) - sharing off this session, file left untouched")
    SHARED_RANKS_ENABLED = bool(data.get("enabled", False))
    SHARED_RANKS_DIR = str(data.get("dir", "") or "")
    # ALWAYS derive the id from host+dir (don't trust a persisted/copied value) so a folder clone can't
    # inherit another instance's id and collide on the shared rankshare_<id>.json. Persist if it changed.
    iid = _gen_instance_id()
    SERVER_INSTANCE_ID = iid
    if not unreadable and str(data.get("instance_id", "") or "").strip() != iid:
        save_shared_ranks_cfg(SHARED_RANKS_ENABLED, SHARED_RANKS_DIR, iid)


def publish_ranks_aggregate():
    """Write THIS server's lifetime ranks into the shared dir as ranks_<id>.json (atomic,
    write-own-file only). Best-effort; a failure logs and never blocks the bot."""
    if not (SHARED_RANKS_ENABLED and SHARED_RANKS_DIR and SERVER_INSTANCE_ID):
        return
    try:
        if not os.path.isdir(SHARED_RANKS_DIR):
            return
        # list() snapshots under the GIL so the MAIN loop mutating RANK_DATA (award/snap) can't raise
        # "dictionary changed size during iteration" on this daemon thread (#XSRV-1).
        snap = {sid: {"name": rec.get("name", ""), "points": rec.get("points", 0),
                      "wins": rec.get("wins", 0), "losses": rec.get("losses", 0)}
                for sid, rec in list(RANK_DATA.items()) if isinstance(rec, dict)}
        # prestige overlay (base + star count) so a prestige done here shows its star/cycle on peers too
        pres = {sid: {"count": rec.get("count", 0), "base": rec.get("base", 0)}
                for sid, rec in list(PRESTIGE_DATA.items()) if isinstance(rec, dict)}
        dest = os.path.join(SHARED_RANKS_DIR, f"rankshare_{SERVER_INSTANCE_ID}.json")
        tmp = dest + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump({"server": SERVER_INSTANCE_ID, "updated": int(time.time()),
                       "ranks": snap, "prestige": pres}, f)
        os.replace(tmp, dest)
    except OSError as e:                          # noqa: BLE001
        print(f"[shared-ranks] publish failed: {e}")


DISCORD_LINK_TTL_S = 900     # a !link code is redeemable in Discord for 15 minutes


def _save_discord_link_code(code, sid):
    """Bank a !link code for the Discord bot to redeem: load-merge-save of this server's
    discord_link_codes_<id>.json in the shared ranks dir (write-own-file + tmp/os.replace,
    the rankshare idiom). Prunes expired codes and any older code for the same pilot.
    Returns False on ANY failure so the caller can say 'try again' instead of raising."""
    if not (SHARED_RANKS_ENABLED and SHARED_RANKS_DIR and SERVER_INSTANCE_ID):
        return False
    try:
        if not os.path.isdir(SHARED_RANKS_DIR):
            return False
        dest = os.path.join(SHARED_RANKS_DIR, f"discord_link_codes_{SERVER_INSTANCE_ID}.json")
        try:
            with open(dest, encoding="utf-8") as f:
                old = json.load(f)
        except (OSError, ValueError):
            old = {}                              # missing/corrupt file -> start fresh
        now = time.time()
        sid = str(sid)
        codes = {}
        if isinstance(old, dict):
            for c, rec in old.items():
                if not isinstance(rec, dict) or str(rec.get("sid", "")) == sid:
                    continue                      # one live code per pilot - newest wins
                try:
                    fresh = now - float(rec.get("ts", 0)) < DISCORD_LINK_TTL_S
                except (TypeError, ValueError):
                    continue
                if fresh:
                    codes[str(c)] = rec
        codes[code] = {"sid": sid, "name": PLAYER_NAMES.get(sid, sid), "ts": int(now)}
        tmp = dest + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(codes, f)
        os.replace(tmp, dest)
        return True
    except OSError as e:                          # noqa: BLE001
        print(f"[discord-link] code save failed: {e}")
        return False


# ── cross-server PRESENCE: "N players are on <other server>, playing <mission>" ────────────────────
# Rides the shared-ranks folder that both servers already use, as presence_<instance_id>.json. Separate
# file from rankshare_*.json on purpose: presence is small and rewritten every ~20s, ranks are large and
# every ~45s, and a peer that stops publishing must go quiet on its own without touching rank carry-over.
#
# Written from the shared-publish DAEMON, never the main loop - the shared dir can be slow or locked.
PRESENCE_PUB_INTERVAL = 20.0     # how often we republish OUR state
PRESENCE_STALE_AFTER  = 90.0     # a peer file older than this is treated as "that server is down"
_PRESENCE_PUB_AT = [0.0]
_PRESENCE_CACHE = ([], 0.0)      # (peers, computed_at) - read cache, refreshed on demand
# Set by main(). cc_web imports this module and therefore also runs the shared-ranks daemon, so
# WRITING has to be claimed by one process; READING is fine in both (the panel may want peers too).
_IS_BOT_PROCESS = [False]


def _short_server_label():
    """A name short enough to sit inside a chat line, e.g. "Vanilla" or "AI+".

    Source order matters. The explicit "label" in shared_ranks.json wins, because a hand-picked short
    name ("AI+") advertises far better than any slice of the full ServerName. Only if that is unset do
    we fall back to the advertised name's first segment.

    NOTE the fallback reads the LIVE server name, not `.nost-data/config.json`'s `server_name`. Those
    two can disagree - seen live 2026-08-02: the local file still held an old test name while the
    server advertised the real public name, so the peer line would have named a server nobody could
    find in the browser. The live name is cached because this runs from the publish daemon every 20s
    and the read is a file/SFTP hop.
    """
    try:
        with open(SHARED_RANKS_FILE, encoding="utf-8") as f:
            lab = str((json.load(f) or {}).get("label", "") or "").strip()
        if lab:
            return lab[:48]
    except (OSError, ValueError):
        pass
    name = _live_server_name() or str(_cfg("server.server_name", "NO_SERVER_NAME", "") or "")
    for sep in ("|", " - ", "  "):
        if sep in name:
            name = name.split(sep, 1)[0]
            break
    name = name.strip()
    return (name or "the other server")[:48]


_LIVE_NAME_CACHE = ["", 0.0]


def _live_server_name(max_age=600.0):
    """The ServerName the game is actually advertising, from DedicatedServerConfig.json. Cached, and
    fail-quiet: any problem returns "" so the caller falls back rather than raising in the daemon."""
    now = time.time()
    if _LIVE_NAME_CACHE[0] and (now - _LIVE_NAME_CACHE[1]) < max_age:
        return _LIVE_NAME_CACHE[0]
    try:
        srv = _read_server_config()
        nm = str((srv or {}).get("ServerName", "") or "").strip()
        if nm:
            _LIVE_NAME_CACHE[0], _LIVE_NAME_CACHE[1] = nm, now
            return nm
    except Exception:                                  # noqa: BLE001 - never break presence on this
        pass
    _LIVE_NAME_CACHE[1] = now                          # don't retry a failing read every 20s
    return _LIVE_NAME_CACHE[0]


def publish_presence():
    """Write THIS server's live presence into the shared dir. Throttled, best-effort, never raises."""
    if not _IS_BOT_PROCESS[0]:
        return                                         # cc_web also runs this daemon - bot owns the write
    if not (SHARED_RANKS_ENABLED and SHARED_RANKS_DIR and SERVER_INSTANCE_ID):
        return
    now = time.time()
    if now - _PRESENCE_PUB_AT[0] < PRESENCE_PUB_INTERVAL:
        return
    _PRESENCE_PUB_AT[0] = now
    try:
        if not os.path.isdir(SHARED_RANKS_DIR):
            return
        # Read the same dashboard snapshot the panel uses rather than re-deriving: it is written every
        # tick by the main loop and is already the authority for "who is online" and "what is loaded".
        online, mission = 0, ""
        try:
            # The snapshot must be FRESH. This daemon outlives a wedged main loop, so without this check
            # it would keep stamping a new timestamp onto a frozen player count - peers would read a
            # dead server as live forever and PRESENCE_STALE_AFTER could never fire. Publishing nothing
            # is the honest failure: peers time us out and go quiet.
            if time.time() - os.path.getmtime(DASHBOARD_STATE_FILE) > PRESENCE_STALE_AFTER:
                return
            with open(DASHBOARD_STATE_FILE, encoding="utf-8") as f:
                st = json.load(f) or {}
            online = int(st.get("online_count") or 0)
            mission = str(st.get("mission") or st.get("current_mission") or "")
            # "(unknown)" is the dashboard's own placeholder while a mission is loading. Publishing it
            # verbatim put the literal string into the peer's chat line ("...playing (unknown)"), so it
            # is normalised to empty here and peer_presence_line() words around it.
            if mission.strip() in ("", "(unknown)", "unknown", "-"):
                mission = ""
        except (OSError, ValueError, TypeError):
            return                                     # no readable state -> publish NOTHING, so peers
                                                       # show us as stale instead of as an empty server
        dest = os.path.join(SHARED_RANKS_DIR, f"presence_{SERVER_INSTANCE_ID}.json")
        # PER-PROCESS temp name. cc_web does `import no_mapvote_bot`, and the shared-ranks daemon starts
        # at import - so this function runs in the WEB process as well as the bot, both deriving the same
        # SERVER_INSTANCE_ID from (host, dir). A shared "<dest>.tmp" would let one process's os.replace
        # consume the other's half-written file. The pid makes the temp private; the replace onto `dest`
        # stays atomic, and last writer wins with a complete record either way.
        tmp = f"{dest}.{os.getpid()}.tmp"
        try:
            with open(tmp, "w", encoding="utf-8") as f:
                json.dump({"server": SERVER_INSTANCE_ID, "label": _short_server_label(),
                           "online": online, "mission": mission, "updated": int(now)}, f)
            os.replace(tmp, dest)
        finally:
            try:
                if os.path.exists(tmp):
                    os.remove(tmp)            # a failed write must not litter the shared folder
            except OSError:
                pass
    except OSError as e:                               # noqa: BLE001
        print(f"[presence] publish failed: {e}")


def read_peer_presence(max_age=PRESENCE_STALE_AFTER):
    """Every OTHER server publishing into the shared dir, freshest first. Excludes this server and
    anything stale. Cached 10s so a chat command or a message tick never globs the share inline."""
    global _PRESENCE_CACHE
    cached, at = _PRESENCE_CACHE
    now = time.time()
    if now - at < 10.0:
        return cached
    import glob
    out = []
    # Gated on ENABLED, not just on the directory: turning cross-server sharing off in the panel has to
    # silence the peer line too. Without this we stop PUBLISHING but keep READING, so the owner disables
    # sharing and this server still announces the other one.
    if SHARED_RANKS_ENABLED and SHARED_RANKS_DIR and os.path.isdir(SHARED_RANKS_DIR):
        for path in glob.glob(os.path.join(SHARED_RANKS_DIR, "presence_*.json")):
            try:
                with open(path, encoding="utf-8") as f:
                    d = json.load(f)
            except (OSError, ValueError):
                continue                               # tolerate a file caught mid-replace
            if not isinstance(d, dict):
                continue
            if str(d.get("server", "")) == str(SERVER_INSTANCE_ID or ""):
                continue                               # never announce ourselves
            try:
                age = now - float(d.get("updated") or 0)
            except (TypeError, ValueError):
                continue
            if age > max_age:
                continue                               # that server is down / not publishing
            try:
                d["online"] = int(d.get("online") or 0)
            except (TypeError, ValueError):
                d["online"] = 0
            d["age"] = age
            out.append(d)
    out.sort(key=lambda d: d.get("age", 1e9))
    _PRESENCE_CACHE = (out, now)
    return out


def peer_presence_line():
    """The chat line, or "" when there is nothing worth saying. Empty peers are skipped deliberately:
    '0 players on the other server' is an advert against yourself."""
    peers = [p for p in read_peer_presence() if p.get("online", 0) > 0]
    if not peers:
        return ""
    tmpl = sysmsg_text("otherserver", _SYSMSG_OTHERSERVER_DEFAULT)
    parts = []
    for p in peers[:2]:                                # 2 keeps it to one readable line on a big fleet
        n = int(p.get("online") or 0)
        _raw = str(p.get("mission") or "").strip()
        # A peer may publish the name ALREADY carrying a "[PVP] " prefix (that is what its own dashboard
        # shows). Strip any leading kind tag first, or the line ends up "[PVP] [PVP] Escalation".
        _raw = re.sub(r"^\s*\[(?:pvp|pve)\]\s*", "", _raw, flags=re.IGNORECASE)
        # Defensive: a peer running an older build can still publish the "(unknown)" placeholder, and
        # "playing (unknown)" reads like a bug to players. Fall back to neutral wording instead.
        # "a mission" - not "right now", which would read "...right now, playing right now" against the
        # default template. It has to stay grammatical inside whatever sentence the owner has written.
        if _raw.lower() in ("", "(unknown)", "unknown", "-"):
            mission, kind = "a mission", ""
        else:
            mission = friendly_label(_raw) or "a mission"
            # Tag from OUR classifier, not from whatever the peer happened to prefix - the two servers
            # run the same build, so is_pvp() is authoritative and consistent across the fleet.
            kind = PVP_TAG_ONLY_COLOURED if is_pvp(_raw) else PVE_TAG_ONLY_COLOURED
        # With no kind tag, drop the placeholder AND the space after it - otherwise the line renders
        # with a visible double gap where the tag would have been.
        line = tmpl.replace("{kind} ", "").replace("{kind}", "") if not kind else tmpl.replace("{kind}", kind)
        parts.append(line.replace("{n}", str(n))
                         .replace("{s}", "" if n == 1 else "s")
                         .replace("{server}", str(p.get("label") or "the other server"))
                         .replace("{mission}", mission))
    return "  ".join(parts)




def maybe_publish_aggregate():
    """Throttled (>=45s) request, called from save_ranks(). NON-BLOCKING: only flags the daemon
    publisher (#2), so a slow/locked shared folder can never stall the bot's main loop. Never raises."""
    global _SHARED_PUB_AT, _SHARED_PUB_PENDING
    if not SHARED_RANKS_ENABLED:
        return
    now = time.time()
    if now - _SHARED_PUB_AT < 45:
        return
    _SHARED_PUB_AT = now
    _SHARED_PUB_PENDING = True


def read_aggregate_ranks():
    """Sum points (+ W/L) per SteamID across every ranks_*.json in the shared dir. DISPLAY ONLY;
    never folded back into RANK_DATA or the ms baseline. Tolerant of a peer file mid-replace."""
    import glob
    agg = {}
    if not (SHARED_RANKS_DIR and os.path.isdir(SHARED_RANKS_DIR)):
        return agg
    for path in glob.glob(os.path.join(SHARED_RANKS_DIR, "rankshare_*.json")):
        try:
            with open(path, encoding="utf-8") as f:
                d = json.load(f)
        except (OSError, ValueError):
            continue
        ranks = d.get("ranks", {}) if isinstance(d, dict) else {}
        for sid, rec in (ranks.items() if isinstance(ranks, dict) else []):
            if not isinstance(rec, dict):
                continue
            a = agg.setdefault(sid, {"name": "", "points": 0.0, "wins": 0, "losses": 0})
            try:
                a["points"] += float(rec.get("points", 0) or 0)
                a["wins"] += int(rec.get("wins", 0) or 0)
                a["losses"] += int(rec.get("losses", 0) or 0)
            except (TypeError, ValueError):
                pass
            if rec.get("name"):
                a["name"] = rec["name"]
    return agg


_OTHER_RANKS_CACHE = ({}, 0.0)
_SHARED_PEERS_CACHE = (0, 0.0)       # (count, computed_at): cache the peer-file glob so the 1Hz dashboard doesn't list the share each tick (#XSRV-2)


def _compute_other_ranks():
    """Glob + sum the OTHER servers' rankshare files (excludes our own). This does the file I/O; it is
    called OFF the main loop by the shared-ranks daemon (#XSRV-2) so a slow/locked share never stalls a
    rank display/award. Tolerant of a peer file mid-replace. Empty unless sharing is enabled."""
    out = {}
    if SHARED_RANKS_ENABLED and SHARED_RANKS_DIR and os.path.isdir(SHARED_RANKS_DIR):
        import glob
        mine = f"rankshare_{SERVER_INSTANCE_ID}.json"
        for path in glob.glob(os.path.join(SHARED_RANKS_DIR, "rankshare_*.json")):
            if os.path.basename(path) == mine:
                continue
            try:
                with open(path, encoding="utf-8") as f:
                    d = json.load(f)
                ranks = d.get("ranks", {}) if isinstance(d, dict) else {}
            except (OSError, ValueError):
                continue
            for sid, rec in (ranks.items() if isinstance(ranks, dict) else []):
                if isinstance(rec, dict):
                    try:
                        out[sid] = out.get(sid, 0.0) + float(rec.get("points", 0) or 0)
                    except (TypeError, ValueError):
                        pass
    return out


def _other_ranks():
    """Cached {sid: points} summed across the OTHER servers (excludes our own file). The shared-ranks
    daemon keeps this cache warm every ~2s, so a rank display/award on the MAIN loop reads the cache and
    never globs the (possibly slow) share inline (#XSRV-2). The inline refresh below is only a fallback
    if the daemon hasn't updated in 60s (e.g. not started). Empty unless sharing is enabled."""
    global _OTHER_RANKS_CACHE
    cached, at = _OTHER_RANKS_CACHE
    now = time.time()
    if now - at < 60:
        return cached
    out = _compute_other_ranks()
    _OTHER_RANKS_CACHE = (out, now)
    return out


def shared_ranks_state():
    """Status (+ a cached combined top-12) for the webcc Shared Ranks card."""
    global _SHARED_BOARD_CACHE
    global _SHARED_PEERS_CACHE
    exists = bool(SHARED_RANKS_DIR and os.path.isdir(SHARED_RANKS_DIR))
    peers, board = 0, []
    if SHARED_RANKS_ENABLED and exists:
        pcached, pat = _SHARED_PEERS_CACHE          # 30s-cached so the 1Hz dashboard never lists a slow share each tick (#XSRV-2)
        if time.time() - pat < 30:
            peers = pcached
        else:
            try:
                import glob
                peers = len(glob.glob(os.path.join(SHARED_RANKS_DIR, "rankshare_*.json")))
            except OSError:
                peers = 0
            _SHARED_PEERS_CACHE = (peers, time.time())
        cached, at = _SHARED_BOARD_CACHE
        now = time.time()
        if now - at < 30:
            board = cached
        else:
            agg = read_aggregate_ranks()
            rows = sorted(agg.items(), key=lambda kv: kv[1]["points"], reverse=True)[:12]
            board = []
            for sid, v in rows:
                try:
                    cyc = max(0.0, float(v["points"]) - float(prestige_base(sid)))
                except Exception:                    # noqa: BLE001 - prestige may not be loaded yet on early warm
                    cyc = float(v["points"])
                board.append({"sid": sid, "name": v["name"] or sid, "points": _pts_i(v["points"]),
                              "wins": v["wins"], "losses": v["losses"],
                              "rank": rank_index_for(cyc)})
            _SHARED_BOARD_CACHE = (board, now)
    return {"enabled": SHARED_RANKS_ENABLED, "dir": SHARED_RANKS_DIR, "server_id": SERVER_INSTANCE_ID,
            "exists": exists, "peer_files": peers, "board": board}


def set_shared_ranks(enabled, dir_):
    global SHARED_RANKS_ENABLED, SHARED_RANKS_DIR, _SHARED_PUB_AT, _SHARED_BOARD_CACHE, _OTHER_RANKS_CACHE
    global _SHARED_PUB_PENDING
    SHARED_RANKS_ENABLED = bool(enabled)
    SHARED_RANKS_DIR = str(dir_ or "").strip()
    save_shared_ranks_cfg(SHARED_RANKS_ENABLED, SHARED_RANKS_DIR)
    _SHARED_BOARD_CACHE = ([], 0.0)
    if SHARED_RANKS_ENABLED:
        _SHARED_PUB_AT = 0.0
        _SHARED_PUB_PENDING = True               # #2: flag the daemon to publish OUR file (off the main loop)
        # Warm the peer cache NOW (synchronously -- this runs on the admin-command handler, not the hot
        # loop) so the immediate rank re-push below bakes the COMBINED rank into every player's name tag,
        # not the local-only rank. Without this, a player joining right after you toggle sharing on gets
        # their LOCAL rank baked (the plugin bakes the name ONCE at connect) until the daemon warms the
        # cache ~2s later -- the "cross-server rank didn't show" symptom.
        try:
            _OTHER_RANKS_CACHE = (_compute_other_ranks(), time.time())
        except Exception:                        # noqa: BLE001 - enabling must never raise
            pass
    else:
        _OTHER_RANKS_CACHE = ({}, 0.0)           # sharing off -> ranks revert to local immediately
    _RANK_PUSH_FLAG[0] = True                     # re-push plugin_ranks.txt (combined ranks + peer lines) on the very next loop
    return {"ok": True}


def _msg_find(mid):
    for m in _server_messages:
        if m["id"] == mid:
            return m
    return None


def server_msg_apply(op, payload):
    """Apply one CRUD op queued by the webcc Messages modal. Returns (ok, info)."""
    op = str(op or "")
    payload = payload if isinstance(payload, dict) else {}
    if op == "add":
        if len(_server_messages) >= MSG_MAX_COUNT:
            return False, f"message limit reached ({MSG_MAX_COUNT})"
        rec = _msg_clean(payload)
        if not rec:
            return False, "empty message text"
        rec["id"] = _new_msg_id()
        _server_messages.append(rec)
        save_server_messages()
        return True, f"added ({rec['trigger']})"
    mid = str(payload.get("id") or "")
    m = _msg_find(mid)
    if op == "delete":
        if not m:
            return False, "not found"
        _server_messages.remove(m)
        _msg_last_fired.pop(mid, None)
        _msg_last_day.pop(mid, None)
        save_server_messages()
        return True, "deleted"
    if op == "toggle":
        if not m:
            return False, "not found"
        m["enabled"] = bool(payload.get("on", not m["enabled"]))
        save_server_messages()
        return True, ("enabled" if m["enabled"] else "disabled")
    if op == "update":
        if not m:
            return False, "not found"
        merged = dict(m)
        for k in ("text", "trigger", "interval_min", "at", "color", "enabled"):
            if k in payload:
                merged[k] = payload[k]
        rec = _msg_clean(merged)
        if not rec:
            return False, "empty message text"
        rec["id"] = m["id"]
        _server_messages[_server_messages.index(m)] = rec
        save_server_messages()
        return True, "updated"
    return False, f"unknown op {op}"


def _msg_fire(rc, m):
    text = m.get("text") or ""
    color = m.get("color") or ""
    line = f"<color={color}>{text}</color>" if color else text
    try:
        rc.say(line)
    except Exception as e:                           # noqa: BLE001  (never break the loop on a chat hiccup)
        print(f"[servermsg] say error: {e}")


def check_server_messages(rc, now, online, state):
    """Time-based triggers (interval + daily clock). Call each loop tick while players are online."""
    if not online or not _server_messages:
        return
    lt = time.localtime(now)
    today = time.strftime("%Y-%m-%d", lt)
    hhmm = time.strftime("%H:%M", lt)
    for m in _server_messages:
        if not m.get("enabled"):
            continue
        trig = m.get("trigger")
        if trig == "interval":
            if m["id"] not in _msg_last_fired:
                _msg_last_fired[m["id"]] = now       # seed: first fire is one full interval after creation/boot
                continue
            iv = max(1, int(m.get("interval_min", 30))) * 60
            if state == "IDLE" and now - _msg_last_fired[m["id"]] >= iv:
                _msg_last_fired[m["id"]] = now
                _msg_fire(rc, m)
        elif trig == "clock":
            if hhmm == m.get("at") and _msg_last_day.get(m["id"]) != today:
                _msg_last_day[m["id"]] = today
                _msg_fire(rc, m)


def fire_event_messages(rc, event):
    """Event triggers (match_start / match_end). Fires only while players are present."""
    if not ROSTER_BY_SID or not _server_messages:
        return
    for m in _server_messages:
        if m.get("enabled") and m.get("trigger") == event:
            _msg_fire(rc, m)


load_server_messages()
load_rank_ladder()
load_shared_ranks_cfg()
_start_shared_pub_worker()                       # #2: start the off-loop shared-ranks publisher daemon


# --- Anti-grief reports: the plugin emits "[NOSTATS] {t:report}" when it auto-kicks/flags a single
# connection flooding unit-commands to brick the server. The bot records them (plugin_reports.json) for the
# webcc Reports tab + a one-click Ban (which drops a plugin ban| command -> immediate _tkBanned + kick).
REPORTS_FILE = os.path.join(_BASE_DIR, "plugin_reports.json")
REPORTS_MAX = 200
_reports = []          # [{seq,id,name,reason,count,rate,action,ts,banned}]
_report_seq = 0


def load_reports():
    global _reports, _report_seq
    try:
        with open(REPORTS_FILE, encoding="utf-8") as f:
            _reports = json.load(f).get("reports", [])
    except (OSError, ValueError):
        _reports = []
    _report_seq = max([r.get("seq", 0) for r in _reports], default=0)


def save_reports():
    try:
        tmp = REPORTS_FILE + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(_json_sane({"reports": _reports[-REPORTS_MAX:]}), f, indent=1)   # NaN-proof (webcc brick class)
        os.replace(tmp, REPORTS_FILE)
    except OSError:
        pass


# --- Ban log: a persistent record of every ban an operator logs from the Reports tab, keyed by SteamID,
# so REPEAT offenders (banned more than once) are visible across matches/restarts. This is the audit trail,
# separate from the live plugin/game enforcement ban lists.
BAN_LOG_FILE = os.path.join(_BASE_DIR, "ban_log.json")
_ban_log = {}          # sid -> {"name": str, "entries": [{"ts": int, "reason": str}]}


def load_ban_log():
    global _ban_log
    try:
        with open(BAN_LOG_FILE, encoding="utf-8") as f:
            j = json.load(f)
        _ban_log = j if isinstance(j, dict) else {}
    except (OSError, ValueError):
        _ban_log = {}


def save_ban_log():
    try:
        tmp = BAN_LOG_FILE + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(_ban_log, f, indent=1)
        os.replace(tmp, BAN_LOG_FILE)
    except OSError:
        pass


def log_ban(sid, name, reason, detail=None):
    """Append a ban event under this SteamID; returns the player's total logged-ban count.
    `detail` (optional dict from the source report: victim/method/weapon/dmg/nc/ts) is kept with the
    entry so the webcc ban log can show the same what-happened card as the report it came from."""
    sid = str(sid or "").strip()
    if not sid:
        return 0
    rec = _ban_log.setdefault(sid, {"name": "", "entries": []})
    if name:
        rec["name"] = str(name)
    ent = {"ts": int(time.time()), "reason": str(reason or "")[:200]}
    if isinstance(detail, dict):
        d = {}
        for k in ("victim", "method", "weapon", "munition", "nc"):
            v = detail.get(k)
            if v:
                d[k] = str(v)[:120]
        # 0.9.43: carry the per-blast unit list into the ban-log card too (same what-happened view)
        if isinstance(detail.get("units"), list) and detail["units"]:
            d["units"] = detail["units"][:24]
        try:
            if float(detail.get("dmg") or 0) > 0:
                d["dmg"] = float(detail["dmg"])
        except (TypeError, ValueError):
            pass
        try:
            if float(detail.get("ts") or 0) > 1e9:            # the offence time from the report
                ent["ts"] = int(float(detail["ts"]))
        except (TypeError, ValueError):
            pass
        if d:
            ent["detail"] = d
    rec["entries"].append(ent)
    rec["entries"] = rec["entries"][-50:]      # cap per-player history
    save_ban_log()
    return len(rec["entries"])


def ban_log_state():
    """Summary for the webcc, repeat offenders first (incl. the last entry's what-happened detail)."""
    out = []
    for sid, rec in _ban_log.items():
        ents = rec.get("entries", []) if isinstance(rec, dict) else []
        if not ents:
            continue
        out.append({"id": sid, "name": rec.get("name", "") or sid, "count": len(ents),
                    "last_ts": ents[-1].get("ts", 0), "last_reason": ents[-1].get("reason", ""),
                    "last_detail": ents[-1].get("detail") or None})
    out.sort(key=lambda x: (x["count"], x["last_ts"]), reverse=True)
    return out


def remove_ban_log(sid):
    """Delete a player's whole ban-log history (the webcc 🗑 button). Returns True if anything was removed.
    This is SEPARATE from clearing reports -- the moderation 'Clear all' only touches reports, never this log."""
    sid = str(sid or "").strip()
    if sid and sid in _ban_log:
        _ban_log.pop(sid, None)
        save_ban_log()
        return True
    return False


load_ban_log()


def add_report(rec):
    global _report_seq, _reports
    _report_seq += 1
    rec["seq"] = _report_seq
    rec.setdefault("banned", False)
    _reports.append(rec)
    _reports = _reports[-REPORTS_MAX:]
    save_reports()


def reports_state():
    return list(reversed(_reports[-REPORTS_MAX:]))   # newest first for the webcc


def _recent_report_for(sid, *, action=None, reason=None, within=45.0):
    """True if a matching Moderation report was filed recently (dedupe / enrich path)."""
    sid = str(sid or "")
    if not sid:
        return False
    now = time.time()
    for r in reversed(_reports):
        try:
            age = now - float(r.get("ts") or 0)
        except (TypeError, ValueError):
            continue
        if age > within:
            break
        if str(r.get("id") or "") != sid:
            continue
        if action is not None and str(r.get("action") or "") != str(action):
            continue
        if reason is not None and str(r.get("reason") or "") != str(reason):
            continue
        return True
    return False


def note_moderation_action(sid, name, action, reason, *, method="admin", source="admin",
                           banned=None, within=45.0):
    """File a Moderation Reports row for eject/kick/ban when an ingest path does not already.
    Returns True if a new row was added. Dedupes identical (sid, action, reason) within `within`s
    so queue+apply / Ban-on-existing-report paths do not double-spam."""
    sid = str(sid or "").strip()
    action = str(action or "report").lower()
    if action not in ("warn", "kick", "ban", "report"):
        action = "report"
    reason = re.sub(r"[\x00-\x1f|]+", " ", str(reason or "")).strip()[:200] or action
    name = display_name(sid, name)
    if _recent_report_for(sid, action=action, reason=reason, within=within):
        return False
    if banned is None:
        banned = (action == "ban")
    add_report({
        "id": sid, "name": name, "reason": reason,
        "count": 0, "rate": 0, "action": action,
        "method": str(method or ""), "weapon": "", "source": str(source or ""),
        "ts": time.time(), "banned": bool(banned),
    })
    return True


def set_report_banned(sid, banned):
    changed = False
    for r in _reports:
        if r.get("id") == sid:
            r["banned"] = bool(banned)
            changed = True
    if changed:
        save_reports()
    return changed


_banned_cache = {"ts": 0.0, "players": []}


def refresh_banned_players():
    """Merge the PLUGIN ban list (plugin_bans.txt) + the GAME-native ban list (ban_list.txt) into
    [{id,name,lists}] for the webcc Moderation 'Banned' tab. Read-only over SFTP; cached. NOTE: an
    in-memory-only game ban (e.g. a fresh votekick not yet written to file) may not appear here -- the
    'Unban by SteamID' box handles those (it sends banlist-remove regardless)."""
    raw = {"plugin": [], "game": []}

    def _read(sftp, path):
        try:
            with sftp.open(path, "rb") as f:
                return [ln.strip() for ln in f.read().decode("utf-8", "replace").splitlines() if ln.strip()]
        except IOError:
            return []

    def _op(sftp):
        raw["plugin"] = _read(sftp, "plugin_bans.txt")
        raw["game"] = _read(sftp, "ban_list.txt")     # game-native; lines may be "<sid> [reason]"

    try:
        _sftp_op(_op)
    except Exception:                              # noqa: BLE001
        pass
    pset = {ln.split()[0] for ln in raw["plugin"] if ln.split()}
    gset = {ln.split()[0] for ln in raw["game"] if ln.split()}
    players = []
    for sid in sorted(pset | gset):
        nm = display_name(sid) if sid else ""
        lists = (["plugin"] if sid in pset else []) + (["game"] if sid in gset else [])
        players.append({"id": sid, "name": nm, "lists": lists})
    _banned_cache.update({"ts": time.time(), "players": players})
    return players


def banned_players_state():
    return list(_banned_cache.get("players", []))


def clear_report(seq):
    """Remove ONE report by its unique seq (webcc Reports 'Clear'). The bot is the single writer of
    plugin_reports.json, so removing it here means /api/state stops re-serving it on the next poll."""
    global _reports
    before = len(_reports)
    _reports = [r for r in _reports if r.get("seq") != seq]
    if len(_reports) != before:
        save_reports()
    return before - len(_reports)


def clear_all_reports():
    """Clear ALL reports (webcc Reports 'Clear all')."""
    global _reports
    n = len(_reports)
    if n:
        _reports = []
        save_reports()
    return n


load_reports()


def _mark_map_downed(sid, now=None):
    """Map ✝: sticky dead until far-jump respawn. Captures DEATH_POS from last POS."""
    if not sid:
        return
    _t = float(now if now is not None else time.time())
    DOWNED[sid] = _t
    _pp = POS.get(sid)
    if _pp and _pp[0] is not None and _pp[1] is not None:
        try:
            DEATH_POS[sid] = (float(_pp[0]), float(_pp[1]))
        except (TypeError, ValueError):
            pass
    _pos_trail_clear(sid)  # ✝ is prompt — no delayed trail past death


def _pos_trail_clear(sid):
    """Drop delayed-playback trail (death / leave / hide)."""
    if sid:
        POS_TRAIL.pop(sid, None)


def _pos_trail_push(sid, ts, px, pz, h):
    """Append a PosTick sample for WebCC. Skip same-pos dups (no freeze-padding heartbeats)."""
    if not sid or px is None or pz is None:
        return
    try:
        fx, fz = float(px), float(pz)
        tsf = float(ts)
    except (TypeError, ValueError):
        return
    if not math.isfinite(fx) or not math.isfinite(fz) or not math.isfinite(tsf):
        return
    dq = POS_TRAIL.get(sid)
    if dq is None:
        dq = collections.deque(maxlen=POS_TRAIL_MAX)
        POS_TRAIL[sid] = dq
    if dq and dq[-1][1] == fx and dq[-1][2] == fz:
        return  # integer-meter hold — do not pad timeline with stationary samples
    dq.append((tsf, fx, fz, h))


def _clear_map_downed(sid):
    """Respawn / leave: drop ✝ + death anchor."""
    if not sid:
        return
    DOWNED.pop(sid, None)
    DEATH_POS.pop(sid, None)


# (The old bot-side command-flood detector — RATELIMIT_DROP_RE over game [RateLimitAttribute]
# console lines — is DELETED: the game no longer emits that line. As of NukeStats 1.2.4 there is
# NO order-rate kick path anywhere: the plugin's own fleet-order limiter (layer A) was removed as
# redundant with vanilla Mirage, which caps CmdSetDestination at ~5 accepted RPCs/s burst 20 per
# player inside HandleRpc. The plugin kick paths that remain are the dead-unit exploit strike kick,
# the inbound-RPC flood kick and the send-buffer overflow-source kick — none of them rate-related.)

# Nuclear Option KickPlayer / kick-player puts the SteamID on a SESSION kick list — they cannot
# rejoin until server restart OR unkick-player.
# POLICY SPLIT:
#   • Automated kick-only (flood / grief rejoin:true, teamkill 2nd offense) → auto unkick ~2s.
#   • Admin / WebCC / manual kick → STAY blocked for the session (no auto-unkick).
# Ban / HardBan never queues unkick. [{sid, name, due}]
_pending_session_unkicks = []
# MUST exceed the plugin's own kick delay. The plugin emits the tk frame IMMEDIATELY but queues
# KickPlayer for +2.5s (NukeStatsPlugin.cs _tkKicks). At the old 2.0s the unkick raced AHEAD of the
# kick whenever the console tail delivered the frame promptly: the kick then re-populated the session
# kick list at +2.5s with nothing left to clear it, so a 2nd-offence teamkiller - who is supposed to be
# kicked and allowed straight back - was locked out until a server restart or a manual unban. That
# silently turned the middle rung of the enforcement ladder into the harshest one.
# A second unkick follows _SESSION_UNKICK_RESEND later, so a slow frame cannot strand someone either.
_SESSION_UNKICK_DELAY = 4.0   # > the plugin's 2.5s kick delay (round-3 audit 2026-08-01)
_SESSION_UNKICK_RESEND = 3.5  # belt-and-braces repeat, in case the first landed before the kick
# Admin kicks: TellPlayer first, then RCON kick-player after a short delay (no unkick).
_pending_admin_kicks = []     # [{sid, name, reason, due}]
_ADMIN_KICK_DELAY = 1.2


def _queue_session_unkick(sid, name=""):
    """Schedule unkick-player for AUTOMATED kick-only paths (flood/TK). Not for admin kicks."""
    sid = str(sid or "").strip()
    if not re.fullmatch(r"\d{6,20}", sid):
        return
    due = time.time() + _SESSION_UNKICK_DELAY
    for e in _pending_session_unkicks:
        if e.get("sid") == sid:
            e["due"] = max(e.get("due", 0), due)
            if name:
                e["name"] = name
            return
    # TWO sends: the first once the plugin's own kick has certainly landed, the second a little later
    # so an unusually slow frame cannot leave the player stranded on the session kick list.
    _pending_session_unkicks.append({"sid": sid, "name": name or "", "due": due})
    _pending_session_unkicks.append({"sid": sid, "name": name or "",
                                     "due": due + _SESSION_UNKICK_RESEND})


def drain_session_unkicks(rc):
    """Send due unkick-player RCON commands (automated kick-only rejoin)."""
    if not _pending_session_unkicks or rc is None:
        return
    now = time.time()
    remain = []
    for e in _pending_session_unkicks:
        if now < float(e.get("due") or 0):
            remain.append(e)
            continue
        sid = str(e.get("sid") or "")
        who = e.get("name") or sid
        try:
            # CHECK THE RESULT. send() returns None (it does NOT raise) when the relay is down or the
            # command is rejected, so the bare call below always looked like success: the retry branch
            # was unreachable and a player the plugin kicked stayed on the session kick list until a
            # server restart - the harshest outcome, silently, for an automated kick that is supposed to
            # let them straight back in. Bounded retries so a long outage cannot queue forever.
            code, _resp = rc.send("unkick-player", sid, return_code=True)
            if code == 2000:
                activity(f"Session-unkick {who} (auto kick-only — rejoin allowed)", "BOT")
                print(f"[unkick] session unkick-player {sid} ({who})")
            else:
                tries = int(e.get("tries") or 0) + 1
                if tries >= 6:
                    activity(f"Session-unkick FAILED for {who} after {tries} attempts - they may be "
                             f"stuck on the kick list until a restart (use unkick in the panel)", "!")
                else:
                    print(f"[unkick] {sid} returned code={code!r}; retry {tries}/6")
                    remain.append({**e, "due": now + 5.0, "tries": tries})
        except Exception as ex:   # noqa: BLE001
            tries = int(e.get("tries") or 0) + 1
            print(f"[unkick] failed for {sid}: {ex} (retry {tries}/6)")
            if tries < 6:
                remain.append({**e, "due": now + 5.0, "tries": tries})
    _pending_session_unkicks[:] = remain


def drain_admin_kicks(rc):
    """Apply due admin RCON kicks after TellPlayer had time to land. No auto-unkick."""
    if not _pending_admin_kicks or rc is None:
        return
    now = time.time()
    remain = []
    for e in _pending_admin_kicks:
        if now < float(e.get("due") or 0):
            remain.append(e)
            continue
        sid = str(e.get("sid") or "")
        who = e.get("name") or sid
        reason = e.get("reason") or "kicked by admin"
        try:
            rc.send("kick-player", sid)
            activity(f"ADMIN KICK {who}: {reason} (session-blocked until unkick)", "ADMIN")
            print(f"[admin-kick] kick-player {sid} ({who}): {reason}")
        except Exception as ex:   # noqa: BLE001
            print(f"[admin-kick] failed for {sid}: {ex}")
            remain.append({**e, "due": now + 3.0})
    _pending_admin_kicks[:] = remain


# --- System messages: owner overrides (enable / text / interval / delay) for the BUILT-IN automated
# messages (join/welcome, the periodic "thanks", the auto leaderboard post, the spectate tip). Stored in
# system_messages.json; the webcc Messages tab edits them. Defaults preserve current behaviour.
# SEASON 1 (owner 2026-08-14): mode pools restarted from zero at the 1.4.0 deploy. The first-week
# notices below all gate on this date so they retire themselves; the panel toggle works before then.
SEASON_LABEL        = "Season 1"
SEASON_NOTICE_UNTIL = "2026-08-22"          # inclusive; notices stop the day after


def season_notice_live():
    try:
        return time.strftime("%Y-%m-%d") <= SEASON_NOTICE_UNTIL
    except Exception:                        # noqa: BLE001
        return False


SYSMSG_FILE = os.path.join(_BASE_DIR, "system_messages.json")
_sysmsg = {}
# default texts for the new toggleable built-in messages (used when the owner hasn't set a custom text)
_SYSMSG_TESTING_DEFAULT  = ("<color=#FFC857>Heads up: this server is actively being tested - features and "
                            "scoring may change. Thanks for flying!</color>")
_SYSMSG_STAY_DEFAULT     = "<color=#FFC83D>** Make sure you stay for the next match! **</color>"
_SYSMSG_RANKFUNDS_DEFAULT = "<color=#8FE388>+{funds} funds</color> for reaching rank {rank}!"
_SYSMSG_OTHERSERVER_DEFAULT = (
    "<color=#8FE388>Server Status</color> <color=#55FF55>>> {server}</color>"
    "  <color=#2E7D46>|</color>  <color=#8FE388>{n} player{s} online</color>"
    "  <color=#2E7D46>|</color>  {kind} <color=#DCE4EE>{mission}</color>")
# (key, label, has_text, default_text, has_interval, default_interval, has_delay, default_delay, note)
_SYSMSG_SEASON_DEFAULT = (
    "<color=#FFD700>Welcome to Season 1!</color> <color=#CFE0F5>A new ladder season has begun.</color>")

_SYSMSG_DEFS = [
    ("season", "Season 1 notice", True,
     "", False, 0, True, 1500,
     "Broadcast every ~25 min for the first week of a season: a new season has begun. "
     "Auto-expires after " + SEASON_NOTICE_UNTIL + " even if left enabled."),
    ("welcome", "Join / welcome message", True,
     "", False, 0, True, WELCOME_DELAY,
     "Posted ~delay seconds after a player joins (shows their rank + points). A custom text REPLACES the "
     "default line; placeholders {name} {rank} {pts} are filled in "
     "(prestige is inside {rank}, e.g. [OFFCDT - 1*]; {star} is ignored/empty)."),
    ("testing", "Join “server is testing” notice", True,
     _SYSMSG_TESTING_DEFAULT, False, 0, False, 0,
     "An extra one-line notice posted to a player right after their welcome (e.g. that the server is being "
     "tested). Turn OFF to hide it entirely."),
    ("thanks", "“Thanks for playing” reminder", True,
     "<color=#FFD200>Thanks for playing!</color> For a list of commands type <color=#55FF55>!help</color>",
     True, THANKS_INTERVAL, False, 0, "A periodic friendly nudge to all players while the server is active."),
    ("leaderboard", "Auto leaderboard post", False, "", True, LEADERBOARD_INTERVAL, False, 0,
     "Posts the top-5 by points to chat on this interval."),
    ("spectip", "Spectate / team-switch tip", False, "", True, SPECTIP_INTERVAL, False, 0,
     "Shows how to spectate / switch to the smaller team (PvP matches only)."),
    ("rankup", "Rank-up announcements", False, "", False, 0, False, 0,
     "The ** RANK UP ** line posted when a player crosses into a new rank. Turn OFF to silence all rank-up "
     "chat (players still rank up; only the announcement is hidden). The wording is edited in the Ranks modal."),
    ("stay", "End-of-match “stay” reminder", True, _SYSMSG_STAY_DEFAULT, False, 0, False, 0,
     "The late-match reminder to stay for the next match (fired at 105/125/145 min elapsed)."),
    ("matchend", "End-of-match summary", False, "", False, 0, False, 0,
     "The '== Match over - <mission> - <result> - <n> min ==' summary + per-player points line posted when a "
     "match ends. Turn OFF to hide it."),
    ("rankfunds", "Rank-funds grant announce", True, _SYSMSG_RANKFUNDS_DEFAULT, False, 0, False, 0,
     "Surfaced when the plugin grants in-game money for a rank increase (catch-up / rank-up funds). "
     "Placeholders {funds} {rank} {name} are filled in. Turn OFF to hide it (the plugin still grants the funds)."),
    ("otherserver", "Other-server player count", True, _SYSMSG_OTHERSERVER_DEFAULT, True, OTHERSERVER_INTERVAL, False, 0,
     "Periodically tells this server's players how busy the OTHER server in the fleet is and what it is "
     "running, so a quiet server can point people at the busy one. Needs cross-server rank sharing to be "
     "ON (it rides the same shared folder). Placeholders: {server} = its short label (set as \"label\" in "
     "shared_ranks.json - currently \"PvP\" and \"PvE\"), {n} = player count, {s} = the plural 's', "
     "{kind} = the coloured [PVP]/[PVE] tag, {mission} = the mission name. A server with nobody on it is "
     "never announced."),
    ("helpcmd", "Enable the !help command", False, "", False, 0, False, 0,
     "When OFF, the bot ignores !help entirely (no command list is posted). The per-command Help editor below "
     "still controls what shows WHEN !help is enabled."),
    ("timewarn", "Mission-time remaining warnings", False, "", False, 0, False, 0,
     "The 'Mission time: N minutes remaining' countdown lines (60/20/10/5/1 min). Turn OFF to silence them."),
    ("victory", "Victory announcement", False, "", False, 0, False, 0,
     "The 'VICTORY! <faction> wins the mission!' line posted when a match is won. Turn OFF to hide it "
     "(win/placement POINTS are controlled separately by the award toggles)."),
]

# ── !help command editor (#6) ──────────────────────────────────────────────────────────────────────
# The in-game !help list is built from this registry. Each command's LINE TEXT is editable (stored in the
# sysmsg store under "help_<id>", but deliberately NOT in _SYSMSG_DEFS so it doesn't clutter the automated
# Messages list) and each command can be SHOWN/HIDDEN. "Auto-hide when a feature is off" is authoritative
# for votemap (reads the votemap kill-switch); plugin-owned commands (spec/swapteam/forfeit) can be
# hidden from the LIST here, but the plugin still answers them until a future plugin flag (display-only).
#   entry = (id, group, color_hex, label_default, gate_default, gate_kind)
#   gate_kind: "bot" enforced toggle | "votemap" -> _votemap_cfg()["enabled"] | "plugin" display-only
#              | "always_on" (help) | the label_default carries its own <color> tags (verbatim current text)
HELP_CFG_FILE = os.path.join(_BASE_DIR, "help_config.json")
_HELP_REGISTRY = [
    ("rank",        "stats", "#55FF55", "<color=#55FF55>!rank</color> - rank & points",                        True, "bot"),
    ("points",      "stats", "#55FF55", "<color=#55FF55>!points</color> - your points",                        True, "bot"),
    ("leaderboard", "stats", "#55FF55", "<color=#55FF55>!leaderboard</color> - top pilots",                    True, "bot"),
    ("prestige",    "stats", "#55FF55", "<color=#55FF55>!prestige</color> - reset cycle & earn a star",        True, "bot"),
    ("ranks",       "stats", "#55FF55", "<color=#55FF55>!ranks</color> - the full rank ladder",                True, "bot"),
    ("stats",       "stats", "#55FF55", "<color=#55FF55>!stats</color> - all your stats in one place",         True, "bot"),
    ("spec",        "teams", "#36FFD0", "<color=#36FFD0>!spec</color> - spectate",                             True, "plugin"),
    ("swapteam",    "teams", "#36FFD0", "<color=#36FFD0>!swapteam</color> - switch to the smaller team",       True, "plugin"),
    ("votemap",     "match", "#FFC857", "<color=#FFC857>!votemap</color> - vote a new map",                    True, "votemap"),
    ("forfeit",     "match", "#FFC857", "<color=#FFC857>!forfeit</color> / <color=#FFC857>!f</color> - surrender (PvP)", True, "plugin"),
    ("notk",        "info",  "#cfd8e3", "<color=#cfd8e3>!notk</color> - no team-killing",                      True, "bot"),
    ("discord",     "info",  "#cfd8e3", "<color=#cfd8e3>!discord</color> - our Discord invite",                True, "bot"),
    ("link",        "info",  "#cfd8e3", "<color=#cfd8e3>!link</color> - link your Discord account",            True, "bot"),
    ("help",        "info",  "#cfd8e3", "<color=#cfd8e3>!help</color> - this list",                            True, "always_on"),
]
_HELP_GROUP_ORDER   = ("stats", "teams", "match", "info")
_HELP_DEFAULT_GATES = {e[0]: e[4] for e in _HELP_REGISTRY}
# editable text keys (live in the sysmsg store, not the automated Messages list)
_HELP_TEXT_DEFAULTS = {("help_" + e[0]): e[3] for e in _HELP_REGISTRY}
_HELP_TEXT_DEFAULTS["help_header"] = "<color=#FFFF00>=== SERVER COMMANDS ===</color>"

_SYSMSG_KEYS = {d[0] for d in _SYSMSG_DEFS} | set(_HELP_TEXT_DEFAULTS)

# Messages that ship DISABLED by default (everything else defaults enabled=True / fail-open). The owner can
# still turn these ON in the webcc Messages tab; once they save an explicit enabled=... the stored record
# wins over this default. rankfunds: the owner does not want a "gave funds" chat line out of the box (the
# plugin still grants the funds; only the announce is hidden). NOTE: this is the real default-enabled
# control -- the _SYSMSG_DEFS tuple's 3rd field is has_text, not a default-enabled flag.
_SYSMSG_DEFAULT_OFF = {"rankfunds"}


def _sysmsg_default_enabled(key):
    return key not in _SYSMSG_DEFAULT_OFF


def load_sysmsg():
    global _sysmsg
    try:
        with open(SYSMSG_FILE, encoding="utf-8") as f:
            _sysmsg = json.load(f)
        if not isinstance(_sysmsg, dict):
            _sysmsg = {}
    except (OSError, ValueError):
        _sysmsg = {}


def save_sysmsg():
    try:
        tmp = SYSMSG_FILE + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(_sysmsg, f, indent=1)
        os.replace(tmp, SYSMSG_FILE)
    except OSError:
        pass


def _sysmsg_rec(key):
    v = _sysmsg.get(key)
    return v if isinstance(v, dict) else {}


def sysmsg_on(key):
    return bool(_sysmsg_rec(key).get("enabled", _sysmsg_default_enabled(key)))


def sysmsg_text(key, default):
    t = _sysmsg_rec(key).get("text")
    return t if isinstance(t, str) and t.strip() else default


def sysmsg_interval(key, default):
    try:
        i = float(_sysmsg_rec(key).get("interval"))
        return i if i > 0 else default
    except (TypeError, ValueError):
        return default


def sysmsg_delay(key, default):
    try:
        d = float(_sysmsg_rec(key).get("delay"))
        return d if d >= 0 else default
    except (TypeError, ValueError):
        return default


def sysmsg_set(key, fields):
    if key not in _SYSMSG_KEYS:
        return False
    v = dict(_sysmsg_rec(key))
    if "enabled" in fields:
        v["enabled"] = bool(fields["enabled"])
    if "text" in fields:
        v["text"] = _msg_sanitize_text(str(fields["text"] or ""))   # tag-safe trim (don't slice a <color=> tag)
    if "interval" in fields:
        try:
            v["interval"] = max(10.0, float(fields["interval"]))
        except (TypeError, ValueError):
            pass
    if "delay" in fields:
        try:
            v["delay"] = max(0.0, min(120.0, float(fields["delay"])))
        except (TypeError, ValueError):
            pass
    _sysmsg[key] = v
    save_sysmsg()
    return True


def sysmsg_state():
    out = []
    for (key, label, has_text, dtext, has_int, dint, has_delay, ddelay, note) in _SYSMSG_DEFS:
        v = _sysmsg_rec(key)
        out.append({"key": key, "label": label, "enabled": bool(v.get("enabled", _sysmsg_default_enabled(key))),
                    "has_text": has_text, "text": v.get("text", "") if has_text else "", "default_text": dtext,
                    "has_interval": has_int, "interval": sysmsg_interval(key, dint) if has_int else 0,
                    "has_delay": has_delay, "delay": sysmsg_delay(key, ddelay) if has_delay else 0,
                    "note": note})
    return out


load_sysmsg()


def _help_cfg():
    gates = dict(_HELP_DEFAULT_GATES)
    try:
        with open(HELP_CFG_FILE, encoding="utf-8") as f:
            j = json.load(f)
        if isinstance(j, dict) and isinstance(j.get("gates"), dict):
            for k, val in j["gates"].items():
                if k in gates:
                    gates[k] = bool(val)
    except (OSError, ValueError):
        pass
    return {"gates": gates}


def set_help_gate(cmd_id, on):
    if cmd_id not in _HELP_DEFAULT_GATES or cmd_id in ("help", "votemap"):
        return False                                    # help is always shown; votemap follows its kill-switch
    cfg = _help_cfg()
    cfg["gates"][cmd_id] = bool(on)
    try:
        tmp = HELP_CFG_FILE + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump({"gates": cfg["gates"]}, f, indent=1)
        os.replace(tmp, HELP_CFG_FILE)
    except OSError:
        return False
    return True


def _help_gate_open(entry, hcfg, vm_enabled=None):
    """Is this command currently shown in the !help list? vm_enabled lets the caller pass the votemap
    kill-switch once (avoids a votemap_config.json read per command on the ~1Hz dashboard path)."""
    cmd_id, kind = entry[0], entry[5]
    if kind == "always_on":
        return True
    if kind == "votemap":                                # authoritative: track the votemap kill-switch
        return bool(_votemap_cfg()["enabled"] if vm_enabled is None else vm_enabled)
    return bool(hcfg["gates"].get(cmd_id, True))         # bot-enforced + plugin display-only toggles


def help_state():
    """Rows for the webcc Help editor: each command's group/colour, editable text (raw; empty == default),
    and whether it's currently shown. The header line is editable too."""
    hcfg = _help_cfg()
    vm_enabled = _votemap_cfg()["enabled"]
    rows = []
    for e in _HELP_REGISTRY:
        cmd_id, grp, col, lbl_default, _gd, kind = e
        rows.append({"cmd": cmd_id, "group": grp, "color": col, "kind": kind,
                     "sysmsg_key": "help_" + cmd_id, "label_default": lbl_default,
                     "text": _sysmsg_rec("help_" + cmd_id).get("text", ""),
                     "shown": _help_gate_open(e, hcfg, vm_enabled),
                     "gate_locked": cmd_id in ("help", "votemap")})
    return {"rows": rows, "order": list(_HELP_GROUP_ORDER),
            "header": _sysmsg_rec("help_header").get("text", ""),
            "header_default": _HELP_TEXT_DEFAULTS["help_header"]}


# Remember the previous ballot's mission set so we don't offer the exact same maps twice in a row.
_prev_ballot_set = None


def _votemap_pool():
    """The dynamic vote pool grouped by category: enabled co-op missions + (per config) enabled custom
    USER missions. PvP options are appended separately by open_vote."""
    cfg = _votemap_cfg()
    pool = {}
    e = [m for m in ESCALATION_MISSIONS if mission_enabled(m)]
    t = [m for m in TERMINAL_CONTROL_MISSIONS if mission_enabled(m)]
    if e:
        pool["Escalation"] = e
    if t:
        pool["Terminal Control"] = t
    # Stock co-op scenarios: ballot-eligible only once their rotation Key is LIVE-VERIFIED
    # (first successful admin "Change map" or --probe-missions), so a vote can never no-op.
    b = [m for m in BUILTIN_COOP_MISSIONS if mission_enabled(m) and mission_key_verified(m)]
    if b:
        pool["Built-in Co-op"] = b
    if cfg["include_custom"]:
        # The "Custom" category feeds the CO-OP (PvE) half of the ballot, so PvP customs must be
        # excluded here or the 16 time-of-day PvP variants compete for the 4 PvE slots and the ballot
        # comes out 3+3 instead of 4+2. The PvP half is drawn separately by _pick_pvp(); the same
        # is_pvp() classifier open_vote already applies to pins is the one used here.
        c = [n for n in _enabled_custom_names() if mission_enabled(n) and not is_pvp(n)]
        if c:
            pool["Custom"] = c
    return pool


def _weighted_sample(items, weights, keyfn, n):
    """Pick up to n of items WITHOUT replacement by relative weight. A missing key defaults to 1.0; an
    explicit 0 excludes (unless every remaining item is 0, then it falls back to uniform among them)."""
    pool = list(items)
    n = min(n, len(pool))
    out = []
    while len(out) < n and pool:
        ws = []
        for it in pool:
            w = weights.get(keyfn(it), 1.0)
            ws.append(w if (isinstance(w, (int, float)) and w > 0) else 0.0)
        tot = sum(ws)
        if tot <= 0:
            pick = random.choice(pool)
        else:
            r = random.uniform(0, tot); acc = 0.0; pick = pool[-1]
            for it, w in zip(pool, ws):
                acc += w
                if r <= acc:
                    pick = it
                    break
        out.append(pick)
        pool.remove(pick)
    return out


# Friendly base labels for the built-in PvP modes (clean names only; kind tag added in _ballot_entry).
_PVP_LABEL = {p[1]: p[2] for p in PVP_OPTIONS}

# Recent winning maps (newest last); open_vote keeps the last avoid_recent of them off the co-op fill.
_recent_winners = []


def note_mission_played(name):
    """Record the mission that is now LOADING as the one that just played.

    The no-repeat rule below reads _recent_winners[-1] as "the mission that just played", but the only
    other writer is apply_winner - so any other route to a new map (an admin Change map, an empty
    ballot handing over to the server rotation) left it holding a mission from two or more matches
    back. The next ballot then banned a stale family AND freely re-offered the family that actually
    just played, which is precisely the repeat the rule exists to stop.

    Idempotent at the tail: re-recording the same mission is a no-op, so a cut that also produces a
    vote-applied append cannot double-count it out of the 12-entry window.
    """
    name = str(name or "").strip()
    if not name:
        return
    if _recent_winners and _recent_winners[-1] == name:
        return
    _recent_winners.append(name)
    del _recent_winners[:-12]
    _no_repeat_note(name)


# no_repeat (owner 2026-08-22, the PvE server): "missions never repeat". A co-op mission that has
# played is off every later ballot until the whole enabled co-op pool has been played once. The
# cycle is FILE-BACKED because the bot restarts every morning at 05:00 - an in-memory set would
# forget the cycle daily and quietly start repeating.
NO_REPEAT_FILE = os.path.join(_BASE_DIR, "votemap_cycle.json")


def _no_repeat_played():
    try:
        with open(NO_REPEAT_FILE, encoding="utf-8") as f:
            return {str(x) for x in (json.load(f) or {}).get("played", []) if x}
    except (OSError, ValueError):
        return set()


def _no_repeat_save(played):
    try:
        with open(NO_REPEAT_FILE, "w", encoding="utf-8") as f:
            json.dump({"played": sorted(played)}, f, indent=1)
    except OSError:
        pass


def _no_repeat_note(name):
    """The mission now loading joins the cycle. PvP is exempt - the rule is the PvE server's."""
    try:
        if not _votemap_cfg().get("no_repeat") or is_pvp(name):
            return
        played = _no_repeat_played()
        if name not in played:
            played.add(name)
            _no_repeat_save(played)
    except Exception:                                 # noqa: BLE001 - bookkeeping must never break a map change
        pass


def _no_repeat_avoid(pool_names):
    """Ballot-time exclusion set. When every enabled co-op mission has been played the cycle resets -
    collapsing to just the most recent winner, so the fresh cycle still cannot open with an immediate
    back-to-back repeat. Missions REMOVED from the pool mid-cycle simply stop counting toward it."""
    played = _no_repeat_played()
    pool = {n for n in pool_names if not is_pvp(n)}
    if pool and not (pool - played):
        last = _recent_winners[-1] if _recent_winners else None
        played = {last} if last in pool else set()
        _no_repeat_save(played)
        activity(f"Votemap: no-repeat cycle complete - all {len(pool)} co-op missions have played; "
                 f"starting a fresh cycle", "MAP")
    return played


def _votable_names():
    """Every mission NAME that can legitimately appear on a ballot: the co-op variants + the PvP modes +
    enabled custom USER missions. Used to validate guaranteed pins."""
    names = {n for n, _ in _all_pool_missions()}
    names.update(_enabled_custom_names())
    return names


def _ballot_entry(name):
    """(group, name, max_time, label) for a votable mission name (co-op variant, custom, or PvP mode).
    Vote label = clean mission name + [PvE]/[PvP] kind suffix (no flavor descriptors)."""
    base = _PVP_LABEL.get(name) or friendly_label(name)
    label = base + ballot_kind_suffix(name)
    if name in MISSION_KEY_CANDIDATES:
        g, n = _mission_key(name)        # verified wire Key (or best guess for an unverified pin)
        return (g, n, mission_max_time(name), label)
    return (mission_group(name), name, mission_max_time(name), label)


def _coop_cat(name):
    """Which co-op category a mission name belongs to (matches _votemap_pool() keys)."""
    if name in ESCALATION_MISSIONS:
        return "Escalation"
    if name in TERMINAL_CONTROL_MISSIONS:
        return "Terminal Control"
    if name in BUILTIN_COOP_MISSIONS:
        return "Built-in Co-op"
    return "Custom"


def build_coop(prev_set, target, cfg, exclude):
    """Return (names, chosen_set) for the random CO-OP/custom portion: `target` maps from _votemap_pool()
    minus `exclude` (guaranteed maps already placed + recently-played maps), honouring coop_mode:
        balanced -> even round-robin across categories (Escalation / Terminal Control / Custom)
        random   -> uniform across the flat pool
        weighted -> pick a category per slot by coop_weights, then a random map from it
    Keeps at most MAX_DARK_PER_VOTE 'dark' maps and avoids the exact previous set when possible. Returns
    ([], frozenset()) when there's nothing to pick. open_vote() assembles the full ordered ballot."""
    mode = cfg["coop_mode"]
    weights = cfg["coop_weights"]
    mw = cfg.get("mission_weights") or {}            # per-MAP appearance chance (1.0 when absent)
    pool = {c: [n for n in ms if n not in exclude] for c, ms in _votemap_pool().items()}
    pool = {c: ms for c, ms in pool.items() if ms}
    flat = [n for ms in pool.values() for n in ms]
    if target <= 0 or not flat:
        return [], frozenset()
    cats = list(pool.keys())
    target = min(target, len(flat))

    def _wshuffle(names):
        # weighted shuffle: a full weighted sample-without-replacement, REVERSED so that .pop()
        # (which takes from the end) yields the strongest-weighted picks first
        return list(reversed(_weighted_sample(names, mw, lambda x: x, len(names))))

    def _pick():
        if mode == "random" or len(cats) <= 1:
            return _weighted_sample(flat, mw, lambda x: x, target)
        bins = {c: _wshuffle(pool[c]) for c in cats}   # per-map-weighted order per category
        chosen = []
        if mode == "weighted":
            while len(chosen) < target:
                live = [c for c in cats if bins[c]]
                if not live:
                    break
                cat = _weighted_sample(live, weights, lambda c: c, 1)[0]
                chosen.append(bins[cat].pop())
            return chosen
        i = 0                                                            # balanced round-robin
        while len(chosen) < target:
            b = bins[cats[i % len(cats)]]; i += 1
            if b:
                chosen.append(b.pop())
            if all(not bins[c] for c in cats):
                break
        return chosen

    # Previous ballot's per-category subsets (>=2 maps): avoid re-offering a whole family pair two votes
    # in a row, which the exact-full-set check alone misses (it only rejects when ALL slots repeat).
    prev_by_cat = {}
    if prev_set:
        for nm in prev_set:
            prev_by_cat.setdefault(_coop_cat(nm), set()).add(nm)
        prev_by_cat = {c: frozenset(s) for c, s in prev_by_cat.items() if len(s) >= 2}

    def _family_repeat(chosen):
        by_cat = {}
        for nm in chosen:
            by_cat.setdefault(_coop_cat(nm), set()).add(nm)
        return any(frozenset(by_cat.get(c, ())) == sub for c, sub in prev_by_cat.items())

    best = None
    for _ in range(400):
        chosen = _pick()
        if sum(is_dark(n) for n in chosen) > MAX_DARK_PER_VOTE:
            continue
        best = chosen                                          # a dark-cap-valid fallback if we can't do better
        if prev_set is None or (frozenset(chosen) != prev_set and not _family_repeat(chosen)):
            return chosen, frozenset(chosen)
    chosen = best if best is not None else _pick()   # over the dark cap with no alternative, or a forced repeat
    return chosen, frozenset(chosen)


def _pick_pvp(n, cfg, exclude=()):
    """Pick up to n PvP built-in mode NAMES, only from those toggled ON in the mission pool and not in
    `exclude`. Decoupled from how many modes are enabled: 'fixed' keeps the historical leading pair
    (Escalation + Terminal Control) regardless of how many extra modes are enabled."""
    enabled = [p[1] for p in PVP_OPTIONS if mission_enabled(p[1]) and p[1] not in exclude]
    mode = cfg["pvp_mode"]
    if n <= 0:
        return []
    # FAMILY MODE DRAWS FROM THE REGISTERED VARIANTS, NOT THE VANILLA MODES. Gating on `enabled` here
    # made the family branch below UNREACHABLE for the intended setup: a server that runs the custom
    # time-of-day missions turns all six built-in PvP modes OFF, so `enabled` is empty and this returned
    # an empty PvP half - a ballot with zero PvP options. The other modes genuinely do need a built-in
    # to pick from, so they keep the guard.
    if mode not in ("family", "each") and not enabled:
        return []
    mw = cfg.get("mission_weights") or {}            # per-MAP appearance chance (1.0 when absent)
    if mode == "family":
        # Round-robin the slots across the families, then draw a DIFFERENT member of each family per
        # slot. A family with nothing enabled is skipped and its slots spill to the others, so a
        # half-registered pool yields a shorter ballot rather than a broken one.
        pools = {}
        for fam in PVP_FAMILY_ORDER:
            members = [m for m in pvp_family_members(fam, exclude=exclude)]
            # A per-map weight of 0 means NEVER OFFERED. _weighted_sample falls back to a uniform pick
            # when every remaining candidate weighs 0 - harmless over a 30-map co-op pool, but here the
            # sample runs over ONE family's members, so a family the operator had zeroed out entirely
            # was still drawn. Drop zero-weight members up front and let the family be skipped if that
            # empties it, matching what the panel says a 0 does.
            members = [m for m in members if mw.get(m, 1.0) != 0]
            if members:
                pools[fam] = members
        if not pools:
            return []
        picked, order = [], [f for f in PVP_FAMILY_ORDER if f in pools]
        while len(picked) < n and order:
            progressed = False
            for fam in list(order):
                if len(picked) >= n:
                    break
                remaining = [m for m in pools[fam] if m not in picked]
                if not remaining:
                    order.remove(fam)            # family exhausted - stop asking it
                    continue
                picked.append(_weighted_sample(remaining, mw, lambda x: x, 1)[0])
                progressed = True
            if not progressed:
                break                            # every family exhausted; ballot is simply shorter
        return picked
    if mode == "each":
        # ONE VARIANT OF EACH MODE (owner 2026-08-22, the PvP server's ballot): every PvP mode with
        # anything enabled gets exactly one slot, filled by ONE randomly-drawn member of that mode's
        # family - the base mission or any registered time-of-day variant. A mode with nothing
        # enabled is skipped and its slot is NOT redistributed (the ballot is simply shorter), so
        # "one of each" can never quietly become "two of one". Modes without variant files
        # (Altercation, Confrontation) are naturally represented by their base mission.
        votable = _votable_names()
        picked = []
        for _grp, base, _lbl in PVP_OPTIONS:
            if len(picked) >= n:
                break
            members = []
            for suffix in PVP_VARIANT_SUFFIXES:
                m = base + suffix
                if m in exclude or m in picked:
                    continue
                if mission_enabled(m) and m in votable and mw.get(m, 1.0) != 0:
                    members.append(m)
            if members:
                picked.append(_weighted_sample(members, mw, lambda x: x, 1)[0])
        return picked
    if mode == "random":
        return _weighted_sample(enabled, mw, lambda x: x, n)
    if mode == "weighted":
        # legacy per-mode table times the per-map chance (both default 1.0, so old setups are unchanged)
        eff = {x: cfg["pvp_weights"].get(x, 1.0) * mw.get(x, 1.0) for x in enabled}
        return _weighted_sample(enabled, eff, lambda x: x, n)
    return enabled[:n]                               # fixed: PVP_OPTIONS order (Escalation, Terminal Control, ...)


def open_vote(online_count=0):
    """Build a fresh ballot into VOTE_OPTIONS. Layout: [guaranteed co-op][random co-op][guaranteed PvP]
    [random PvP], numbered 1..N. coop_count + pvp_count size the two pools independently (default 4 + 2 =
    the regular 6). Guaranteed missions are always pinned and count toward their type's slot count (a
    generalisation of the always-on PvP pair). A high-population rule can override the split into a
    PvP-heavy ballot; avoid_recent keeps the last N winners off the random co-op fill."""
    global VOTE_OPTIONS, _prev_ballot_set
    cfg = _votemap_cfg()
    coop_n = cfg["coop_count"]
    pvp_n  = cfg["pvp_count"] if cfg["include_pvp"] else 0
    if cfg["force_pvp_enabled"] and online_count >= cfg["force_pvp_players"]:
        coop_n = cfg["force_pvp_coop"]
        pvp_n  = cfg["force_pvp_pvp"] if cfg["include_pvp"] else 0

    # guaranteed pins: keep only those still enabled + valid, deduped, in config order. A pinned
    # stock mission whose rotation Key is UNVERIFIED is skipped (a ballot must never offer a map
    # the server might reject) — it becomes pinnable the moment a first admin map change verifies it.
    votable = _votable_names()
    guaranteed = []
    for n in cfg["guaranteed"]:
        if not (mission_enabled(n) and n in votable):
            continue
        if not mission_key_verified(n):
            activity(f"Votemap: pinned '{friendly_label(n)}' left off this ballot - its mission key is "
                     f"unverified (load it once via Change map to arm it)", "MAP")
            continue
        guaranteed.append(n)
    # is_pvp(), not "in PVP_MISSIONS": the latter only knows the six BASE names, so a pinned
    # time-of-day variant ("Escalation - Dawn") was bucketed as CO-OP - it ate a PvE slot, was dropped
    # entirely under force-PvP, and could still be drawn a second time by its own family.
    g_coop = [n for n in guaranteed if not is_pvp(n)]
    g_pvp  = [n for n in guaranteed if is_pvp(n)]
    # Pins COUNT TOWARD their type's slot count -- so they must never exceed it either. Without this
    # truncation, force-PvP (coop slots = 0) still put every pinned co-op map on the ballot: with 2
    # co-op pins + only 4 PvP modes enabled, an over-threshold ballot came out 2 PvE + 4 PvP instead
    # of the configured 0 + 5 (live report 2026-07-05).
    # A pin that exceeds its type's slot count is DISCARDED here. Say so: the operator pinned it
    # deliberately and otherwise gets no feedback at all (contrast the unverified-key drop above, which
    # does log). Most common cause is a PvP pin while include_pvp is off, which makes pvp_n zero.
    for _n in g_coop[coop_n:]:
        activity(f"Votemap: pinned '{friendly_label(_n)}' left off - only {coop_n} co-op slot(s) "
                 f"on this ballot", "MAP")
    for _n in g_pvp[pvp_n:]:
        activity(f"Votemap: pinned '{friendly_label(_n)}' left off - only {pvp_n} PvP slot(s) "
                 f"on this ballot" + (" (include_pvp is OFF)" if not cfg["include_pvp"] else ""), "MAP")
    g_coop = g_coop[:coop_n]
    g_pvp  = g_pvp[:pvp_n]

    avoid = set(_recent_winners[-cfg["avoid_recent"]:]) if cfg["avoid_recent"] else set()
    if cfg["no_repeat"]:
        # never-repeat cycle (PvE server): everything already played this cycle is off the ballot.
        # Pins stay exempt, like avoid_recent - a deliberate pin outranks the cycle.
        _pool_all = [n for ms in _votemap_pool().values() for n in ms]
        avoid |= (_no_repeat_avoid(_pool_all) - set(guaranteed))

    # DON'T PUT PLAYERS ON THE SAME SIDE TWICE RUNNING (owner, 2026-08-01):
    # "if the last one was bdf, then no bdf ones can appear on the pool... pala missions can still
    # appear." Just played BDF -> the whole co-op half is drawn from PALA, whatever the scenario or
    # the time of day. avoid_recent cannot express this (it matches exact names) and defaults to 0.
    # Always on, and it only ever looks at the mission that just played.
    #
    # PvE ONLY, both ends of the test - owner, 2026-08-01, after a live Server 2 PvP vote came up with
    # no Terminal options: "it was only meant to change that pool thing for pve missions... make sure
    # next map vote it lets a consecutive terminal mission appear for pvp". So a PvP winner suppresses
    # nothing, and a PvE winner can never suppress a PvP map even if the names collapse to the same
    # family string. Consecutive PvP repeats are intended behaviour.
    #
    # Guarded: if excluding that side leaves nothing, it is dropped rather than shortening the ballot.
    # A guaranteed pin still overrides it.
    if _recent_winners and not is_pvp(_recent_winners[-1]):
        last_family = mission_family(_recent_winners[-1])
        same_family = {n for n in _votable_names()
                       if not is_pvp(n) and mission_family(n) == last_family}
        remaining = [n for ms in _votemap_pool().values() for n in ms if n not in same_family]
        if remaining:
            avoid |= (same_family - set(guaranteed))
        else:
            activity(f"Votemap: allowing '{friendly_label(last_family)}' again - nothing else is "
                     f"enabled to offer instead", "MAP")

    coop_fill = max(0, coop_n - len(g_coop))
    coop_names, _prev_ballot_set = build_coop(_prev_ballot_set, coop_fill, cfg, set(g_coop) | avoid)
    # Exclude everything the co-op half already placed. The variants are CUSTOM missions, so with
    # include_custom on they are candidates for the co-op fill AND members of a PvP family - without
    # this the same mission could take two slots on one ballot and split the vote between them.
    # `avoid` (the recent-winners set) is applied here TOO: the PvP time-of-day variants used to sit in
    # pool["Custom"], where build_coop's exclude suppressed them, but they are now only reachable
    # through _pick_pvp - so without this the map that just won could be re-offered immediately.
    # Pins stay exempt from avoid_recent by design.
    pvp_want = max(0, pvp_n - len(g_pvp))
    pvp_exclude = set(g_pvp) | set(g_coop) | set(coop_names) | (avoid - set(guaranteed))

    # DARK-MAP CAP AT BALLOT LEVEL. The cap used to be enforced only inside build_coop, which was
    # sufficient while the time-of-day variants lived in the co-op pool. They are now drawn by
    # _pick_pvp, which has no dark logic - so on a PvP-heavy ballot every option could be Night/Dusk.
    # Re-draw a bounded number of times and keep the best attempt rather than looping forever.
    dark_budget = MAX_DARK_PER_VOTE - sum(1 for n in (g_coop + coop_names + g_pvp) if is_dark(n))
    pvp_names, _best_dark = None, None
    for _try in range(24):
        cand = _pick_pvp(pvp_want, cfg, exclude=pvp_exclude)
        nd = sum(1 for n in cand if is_dark(n))
        if _best_dark is None or nd < _best_dark:
            pvp_names, _best_dark = cand, nd
        if nd <= max(0, dark_budget):
            break
    if pvp_names is None:
        pvp_names = []

    if pvp_want and len(pvp_names) < pvp_want:
        activity(f"Votemap: PvP half came up short - wanted {pvp_want}, drew {len(pvp_names)} "
                 f"(mode={cfg['pvp_mode']}; check the PvP families / enabled PvP missions)", "!")

    ordered = g_coop + coop_names + g_pvp + pvp_names
    if not ordered:
        # Safety net. It must honour the CONFIGURED SHAPE: under force-PvP the co-op count is 0, so
        # rebuilding from co-op missions turned a high-population PvP ballot into an all-PvE one with no
        # diagnostic. Try the PvP side first whenever PvP slots were asked for.
        activity("Votemap: ballot came out EMPTY - falling back "
                 f"(coop_n={coop_n} pvp_n={pvp_n} mode={cfg['pvp_mode']})", "!")
        if pvp_n > 0:
            fallback = _pick_pvp(pvp_n, cfg, exclude=set())
            if not fallback:
                fallback = [m for m in PVP_MISSIONS if mission_enabled(m)][:pvp_n]
            ordered = fallback
        if not ordered:                              # fill from ENABLED co-op missions only (never strand the map on a removed one)
            coop_pool = [m for m in (ESCALATION_MISSIONS + TERMINAL_CONTROL_MISSIONS) if mission_enabled(m)]
            if not coop_pool:                        # nothing enabled at all -> leave the ballot empty; server rotation advances
                activity("Votemap: nothing enabled at all - no ballot; the server rotation advances", "!")
                VOTE_OPTIONS = {}
                return VOTE_OPTIONS
            ordered = random.sample(coop_pool, min(4, len(coop_pool)))
    VOTE_OPTIONS = {str(i): _ballot_entry(n) for i, n in enumerate(ordered, start=1)}
    return VOTE_OPTIONS


def recompute_approval(current_online, frozen_threshold, frozen_players):
    """!votemap approval bar at poll CLOSE: a majority of the CURRENT headcount, not the count frozen
    at poll open (leavers made the frozen bar unreachable; late joiners could vote without raising it).
    0 online (or an unreadable player list) keeps the frozen values -- nobody is left to satisfy
    either bar, so the frozen numbers only shape the log line."""
    if current_online > 0:
        return current_online // 2 + 1, current_online
    return frozen_threshold, frozen_players


def announce_options(rc, duration=None, left_note=None):
    """Post the ballot: header + Option A paired lines (`!1 = … | !2 = …`).
    left_note: optional header suffix instead of `(Ns)` — used for the once-per-vote 15s rebroadcast."""
    duration = int(duration if duration is not None else vote_duration())
    keys = list(VOTE_OPTIONS.keys())
    n = len(keys)
    time_bit = left_note if left_note else f"{duration}s"
    rc.say(f"<color=#FFFF00>=== NEXT MAP VOTE ===</color> "
           f"type <color=#55FF55>!1</color>-<color=#55FF55>!{n}</color> in chat ({time_bit})")
    for i in range(0, n, 2):
        k1 = keys[i]
        left = f"<color=#55FF55>!{k1}</color> = {VOTE_OPTIONS[k1][3]}"
        if i + 1 < n:
            k2 = keys[i + 1]
            right = f"<color=#55FF55>!{k2}</color> = {VOTE_OPTIONS[k2][3]}"
            rc.say(f"  {left}  |  {right}")
            activity(f"!{k1} = {_plain(VOTE_OPTIONS[k1][3])}  |  !{k2} = {_plain(VOTE_OPTIONS[k2][3])}", "VOTE")
        else:
            rc.say(f"  {left}")
            activity(f"!{k1} = {_plain(VOTE_OPTIONS[k1][3])}", "VOTE")


# ── Empty-server forced-cut fallback ────────────────────────────────────────────
# A forced cut (set-next-mission + set-time-remaining) only rolls the mission over
# while the match clock is TICKING. With NOBODY online the game pauses (new-game
# NetworkPause), so a cut fired at an empty server silently never happens: the
# next-mission override just sits queued and the bot believes the map changed.
# Record such cuts here; the main-loop roster poll re-fires set-time-remaining on
# the next join (clock resumed), so the queued map loads ~ROLLOVER_SECONDS later.
EMPTY_FORCED_CUT = {"pending": False, "label": "", "at": 0.0}
EMPTY_CUT_MAX_AGE = 3600.0        # give up on a queued empty-cut this long after it was armed


def _server_confirmably_empty(rc):
    """True ONLY on a positive get-player-list reply showing zero players (the same bar
    boot_map_safety_net uses). Any error/None -> False: never assume empty."""
    try:
        code, resp = rc.send("get-player-list", return_code=True)
        players = (resp.get("Players") or resp.get("players") or []) if isinstance(resp, dict) else None
        return code == 2000 and players is not None and len(players) == 0
    except Exception:                                # noqa: BLE001
        return False


def note_forced_cut(rc, label):
    """Called right after a forced cut. If the server is confirmably empty, arm the
    on-join re-fire and say so in the activity feed instead of letting the cut vanish."""
    try:
        if _server_confirmably_empty(rc):
            EMPTY_FORCED_CUT.update(pending=True, label=str(label or ""), at=time.time())
            activity(f"Server is empty (mission clock paused) - {label} will load shortly "
                     f"after the next player joins", "MAP")
            print(f"[map] forced cut on an empty server -> on-join re-fire armed for {label!r}")
    except Exception as e:                           # noqa: BLE001 - the fallback must never break a map change
        print(f"[map] empty-cut check failed: {e}")


def apply_winner(rc, votes, first_vote_at, force_switch=False):
    global CURRENT_MISSION
    if votes:
        tally = Counter(votes.values())
        top = max(tally.values())
        tied = [k for k, c in tally.items() if c == top]
        if len(tied) == 1:
            winner_key, source = tied[0], "vote"
        else:
            # tie-breaker: whichever tied map received its first vote earliest
            winner_key = min(tied, key=lambda k: first_vote_at.get(k, float("inf")))
            source = "vote (tie -> first voted)"
    else:
        if not VOTE_OPTIONS:                         # empty ballot (all missions disabled) -> no vote pick
            # (boot map is game-side now: pinned at rotation[0]+Sequence, so the server rotation continues
            #  in order - never a bot-side queue, per the owner)
            rc.say("<color=#FFC83D>No eligible maps to vote on - the server rotation will pick the next mission.</color>")
            activity("Map vote had no eligible missions; left the next map to the server rotation", "MAP")
            # A FORCED context (admin End match, !votemap) asked for the match to END. There is no winner
            # to queue, but the cut must still happen or End match silently does nothing whenever every
            # mission is disabled - the button would report success and the match would play on.
            if force_switch:
                rc.set_time_remaining(ROLLOVER_SECONDS)
                note_forced_cut(rc, "the next map in the server rotation")
                activity("Ending the match anyway - the server rotation picks what loads next", "MAP")
            return None
        winner_key = random.choice(list(VOTE_OPTIONS))
        source = "random (no votes)"
    group, name, max_time, label = VOTE_OPTIONS[winner_key]
    rc.set_next_mission(group, name, max_time)
    note_mission_played(name)                # feed avoid_recent + the no-repeat rule (rolling window)
    # Use the SAME canonical form refresh_current_mission() will settle on (friendly_label of the mission
    # name), NOT _plain(label) -- the ballot label carries a "[PvE]"/"[PvP]" kind suffix, so _plain(label)
    # differs from the refreshed value and the changing key would reset the mission-time-warning dedupe set,
    # double-firing the "Mission time: X remaining" line. Keeping the key stable across the refresh prevents that.
    CURRENT_MISSION = friendly_label(name)   # the mission the next match will run

    if force_switch:
        # mid-mission (!votemap) vote: cut the current mission over to the winner now.
        rc.set_time_remaining(ROLLOVER_SECONDS)
        note_forced_cut(rc, CURRENT_MISSION)   # empty server = paused clock -> arm the on-join re-fire
    summary = ", ".join(
        f"{VOTE_OPTIONS[k][3]}:{c}" for k, c in Counter(votes.values()).most_common()
    ) or "-"
    rc.say(f"<color=#55FF55>Winner: {label}</color> ({source}). Tally: {summary}")
    print(f"[vote] winner={label} via {source} tally={dict(Counter(votes.values()))}")
    if votes:
        activity(f"Next map: {_plain(label)}   (votes: {_plain(summary)})", "MAP")
    else:
        activity(f"No votes cast - picked {_plain(label)} at random", "MAP")
    return {
        "group": group,
        "name": name,
        "max_time": max_time,
        "label": label,
        "expected": friendly_label(name),
    }   # for the post-apply verification (did OUR winner actually load?)


def apply_boot_map_rotation(reason=""):
    """FIX 4 (reworked): make the boot map the mission the GAME ITSELF loads at boot. Decompiled-game
    fact: at startup DedicatedServerManager builds MissionRotation(config.MissionRotation,
    config.RotationType) and with RotationType Sequence (enum 0) the first pick IS rotation[0];
    set-next-mission is an IN-MEMORY override that can never survive a restart (why the old queue
    approach did nothing at boot). So: force RotationType -> Sequence and move/insert the boot map at
    MissionRotation[0]. Idempotent (no write when already pinned). Re-asserted on bot startup, on
    boot-map change, and after every real server restart (a re-templating boot can rewrite the file)."""
    name = (_votemap_cfg().get("boot_map") or "").strip()
    if not name:
        return False
    grp, mt = mission_group(name), mission_max_time(name)

    def _m(cfg):
        changed = False
        if cfg.get("RotationType") != 0:                 # 0 = Sequence (int enum in the config JSON)
            cfg["RotationType"] = 0
            changed = True
        rot = cfg.setdefault("MissionRotation", [])

        def _match(e):
            k = e.get("Key", {}) if isinstance(e, dict) else {}
            return k.get("Name") == name
        hit = next((i for i, e in enumerate(rot) if _match(e)), None)
        if hit is None:
            rot.insert(0, {"Key": {"Group": grp, "Name": name}, "MaxTime": float(mt)})
            changed = True
        elif hit != 0:
            rot.insert(0, rot.pop(hit))
            changed = True
        return changed
    r = _mission_rotation_mutate(_m)
    if isinstance(r, dict) and r.get("ok"):
        if not r.get("nochange"):
            activity(f"Boot map: {friendly_label(name)} pinned as the boot mission "
                     f"(rotation slot 1 + Sequence rotation) ({reason})", "MAP")
            print(f"[boot-map] rotation[0]={name} RotationType=Sequence ({reason})")
        return True
    activity(f"Boot map rotation write FAILED ({(r or {}).get('error', '?')}) ({reason})", "!")
    return False


def boot_map_safety_net(rc):
    """After a real server restart with NOBODY on: if the game somehow booted a different mission
    (a startup override or a skipped rotation entry), LOAD the boot map right now via the same live
    cutover the admin Change-map button uses. An actual load, never a queue - and only ever while the
    server is confirmably EMPTY, so no player ever gets cut over."""
    name = (_votemap_cfg().get("boot_map") or "").strip()
    if not name:
        return
    try:
        refresh_current_mission(rc)
        want = friendly_label(name)
        if CURRENT_MISSION == want:
            print("[boot-map] server booted on the boot map (no action)")
            return
        code, resp = rc.send("get-player-list", return_code=True)
        players = (resp.get("Players") or resp.get("players") or []) if isinstance(resp, dict) else None
        if code != 2000 or players is None or len(players) != 0:
            print(f"[boot-map] not forcing the boot map: server not confirmably empty (code={code})")
            return
        if force_change_map(rc, name):
            activity(f"Boot map: server booted on {CURRENT_MISSION} - loaded {want} instead (server was empty)", "MAP")
    except Exception as e:                                # noqa: BLE001
        print(f"[boot-map] safety net failed: {e}")


def mission_group(name):
    """Server-side group for a mission NAME: BuiltIn for stock ops/co-op, User for the custom maps."""
    if name in MISSION_KEY_CANDIDATES:
        return _mission_key(name)[0]
    if name in PVP_MISSIONS or name in BUILTIN_COOP_MISSIONS:
        return "BuiltIn"
    return MISSION_GROUP


# ── Rotation-Key resolution for missions whose exact server identity is unconfirmed ─────────────
# mission_keys.json caches the (Group, Name) the live server actually ACCEPTED, per pool name.
MISSION_KEYS_FILE = os.path.join(_BASE_DIR, "mission_keys.json")


def _load_mission_keys():
    try:
        with open(MISSION_KEYS_FILE, encoding="utf-8") as f:
            d = json.load(f)
        return d if isinstance(d, dict) else {}
    except (OSError, ValueError):
        return {}


def _mission_key(name):
    """Best-known rotation (group, name) for a candidate mission: the live-verified cached Key
    when we have one, else the first (best-guess) candidate."""
    k = _load_mission_keys().get(name)
    if isinstance(k, list) and len(k) == 2:
        return (str(k[0]), str(k[1]))
    return MISSION_KEY_CANDIDATES[name][0]


def mission_key_verified(name):
    """True once a candidate mission's rotation Key has been confirmed against the live server
    (always True for missions that never needed resolving)."""
    if name not in MISSION_KEY_CANDIDATES:
        return True
    k = _load_mission_keys().get(name)
    return isinstance(k, list) and len(k) == 2


def _resolve_mission_key(rc, name):
    """Find the rotation Key the server actually accepts for a MISSION_KEY_CANDIDATES mission.
    set-next-mission always replies 2000 but only changes the override for a VALID mission (the
    --probe-missions mechanism), so try each candidate and read back the override. A rejected
    candidate leaves the override untouched; the accepted one leaves it QUEUED (which is what the
    callers want anyway). Returns (group, name) and caches it, or None if nothing was accepted."""
    if mission_key_verified(name):
        return _mission_key(name)
    for g, n in MISSION_KEY_CANDIDATES[name]:
        try:
            rc.set_next_mission(g, n, mission_max_time(name))
            r = rc.send("get-mission-rotation")
            k = {}
            if isinstance(r, dict) and r.get("hasNextOverride"):
                k = (r.get("nextOverride") or {}).get("Key") or {}
            if (k.get("Group"), k.get("Name")) == (g, n):
                d = _load_mission_keys()
                d[name] = [g, n]
                try:
                    tmp = MISSION_KEYS_FILE + ".tmp"
                    with open(tmp, "w", encoding="utf-8") as f:
                        json.dump(d, f, indent=1)
                    os.replace(tmp, MISSION_KEYS_FILE)
                except OSError:
                    pass
                activity(f"Verified mission key for {name} -> {g}/{n} (now eligible for ballots)", "MAP")
                print(f"[mission-key] {name} resolved to {g}/{n}")
                return (g, n)
        except Exception as e:                             # noqa: BLE001
            print(f"[mission-key] resolve {name} failed mid-probe: {e}")
            return None
    print(f"[mission-key] {name}: NO candidate key accepted by the server")
    return None


def force_change_map(rc, name):
    """Admin (web CC 'Change map'): cut the LIVE match over to an explicit mission NOW. The caller
    (main loop) then suppresses the automatic mission-end vote so this choice sticks (no ballot override).
    Missions with an unconfirmed rotation Key are resolved (readback-verified) first, so a bad Key
    fails LOUDLY here instead of silently keeping the current mission after the rollover cut."""
    global CURRENT_MISSION
    if not name:
        return False
    if name in MISSION_KEY_CANDIDATES:
        key = _resolve_mission_key(rc, name)
        if key is None:
            rc.say(f"<color=#FF5555>Couldn't load {friendly_label(name)} - the server did not accept "
                   f"any known mission key. Map unchanged.</color>")
            activity(f"ADMIN map change to {friendly_label(name)} FAILED - server rejected all "
                     f"{len(MISSION_KEY_CANDIDATES[name])} candidate keys", "MAP")
            return False
        group, wire = key
    else:
        group, wire = mission_group(name), name
    rc.set_next_mission(group, wire, mission_max_time(name))  # queue it (configured per-type length)
    CURRENT_MISSION = friendly_label(wire)                   # keep the warn-dedupe key stable
    note_mission_played(wire)                                # an admin map change IS the map that just played -
                                                             # without this the next ballot's no-repeat rule looks
                                                             # at the previous VOTE winner instead
    rc.set_time_remaining(ROLLOVER_SECONDS)                  # force the cut now (same as a !votemap force-switch)
    note_forced_cut(rc, mission_display(name, coloured=False))  # empty server = paused clock -> re-fire on next join
    rc.say(f"<color=#55FF55>Admin changed the map -> {mission_display(name)}</color>")
    activity(f"ADMIN changed map -> {mission_display(name, coloured=False)}", "MAP")
    print(f"[admin] force-change map -> {group}/{wire}")
    return True


_RANK_TAG_RE = None


def _strip_rank_tag(name):
    """Remove a leading '[ABBR] ' / '[ABBR - n*] ' rank tag that the NukeStats plugin
    (custom chat) embeds into the in-game name. The dedicated-server roster
    (get-player-list displayName) reports that tagged name, so without this the tag
    would leak into PLAYER_NAMES and ranks.json and break welcome/!rank/resolve_player.
    Only strips tags that START with a known rank abbreviation so a real bracketed
    name (e.g. a clan tag) is left untouched. Prestige forms like [OFFCDT - 1*] match."""
    global _RANK_TAG_RE
    if not name:
        return name
    if _RANK_TAG_RE is None:
        # match either the short abbr (kill-feed) OR the full rank name (chat tag), longest first;
        # optional prestige suffix inside the brackets (anything after the known abbr until ']').
        # No ladder = no tags to strip: use a never-matching regex so a real bracketed clan tag
        # is never mistaken for a rank tag (an empty alternation would match ANY [..] prefix).
        tags = sorted({str(r[2]) for r in RANKS} | {str(r[1]) for r in RANKS}, key=len, reverse=True)
        if tags:
            abbr_alt = "|".join(re.escape(a) for a in tags)
            _RANK_TAG_RE = re.compile(r"^\[(?:" + abbr_alt + r")[^\]]*\]\s(.+)$")
        else:
            _RANK_TAG_RE = re.compile(r"(?!)")
    m = _RANK_TAG_RE.match(name)
    return m.group(1) if m else name


def _extract_players(resp):
    """Pull the player-dict list out of a get-player-list reply, caching display
    names. Filters to dicts so a malformed reply can't crash downstream p.get()."""
    if isinstance(resp, dict):
        raw = resp.get("Players") or resp.get("players")
        if isinstance(raw, list):
            players = [p for p in raw if isinstance(p, dict)]
            for p in players:
                nm = _strip_rank_tag(p.get("displayName"))
                if nm is not None:
                    p["displayName"] = nm          # clean the dict so ROSTER_BY_SID/tables match
                sid = str(p.get("steamId") or "")
                if sid and nm:
                    _g = _storable_name(sid, nm)
                    if _g:
                        PLAYER_NAMES[sid] = _g
            return players
    return []


def get_players(rc):
    """Return the list of in-game player dicts (or []), caching display names."""
    return _extract_players(rc.get_player_list())


# ----------------------------------------------------------------------------
# Server-rank tracking (persisted in ranks.json, keyed by SteamID)
# ----------------------------------------------------------------------------

def _try_load_ranks_dict(path):
    """Return a non-empty ranks dict from path, or None if unusable."""
    with open(path, "r", encoding="utf-8") as f:
        data = json.load(f)
    if isinstance(data, dict) and data:
        return data
    return None


def load_ranks():
    """Load ranks.json. On corrupt/empty primary: restore from .bak, then newest
    ranks_backup_*.json. Never save an empty wipe over a recoverable backup."""
    global RANK_DATA
    import glob
    candidates = [RANK_FILE, RANK_FILE + ".bak"]
    backup_dir = os.path.dirname(RANK_FILE) or "."
    candidates.extend(sorted(glob.glob(os.path.join(backup_dir, "ranks_backup_*.json")), reverse=True))
    seen = set()
    for path in candidates:
        ap = os.path.abspath(path)
        if ap in seen:
            continue
        seen.add(ap)
        try:
            data = _try_load_ranks_dict(path)
        except FileNotFoundError:
            continue
        except (json.JSONDecodeError, OSError, TypeError, ValueError) as e:
            print(f"[ranks] skip unusable {path}: {e}")
            continue
        if data is None:
            print(f"[ranks] skip empty/non-object {path}")
            continue
        RANK_DATA = data
        if os.path.abspath(path) != os.path.abspath(RANK_FILE):
            print(f"[ranks] restored {len(RANK_DATA)} record(s) from {path}")
            try:                                      # rewrite primary from recovered copy (do not leave corrupt on disk)
                tmp = RANK_FILE + ".tmp"
                with open(tmp, "w", encoding="utf-8") as f:
                    json.dump(RANK_DATA, f, indent=2)
                os.replace(tmp, RANK_FILE)
                print(f"[ranks] rewrote primary {RANK_FILE} from recovery")
            except OSError as e:
                print(f"[ranks] recovery loaded but could not rewrite primary: {e}")
        else:
            print(f"[ranks] loaded {len(RANK_DATA)} record(s) from {RANK_FILE}")
        return
    print("[ranks] no recoverable ranks file; starting empty (NOT wiping backups)")
    RANK_DATA = {}


PENDING_ADJUST_FILE = os.path.join(_BASE_DIR, "pending_rank_adjust.json")


def apply_pending_adjust():
    """Apply a queued one-shot correction to RANK_DATA at startup, then retire the file.

    WHY THIS EXISTS: ranks.json is written FROM memory, so editing it while the bot runs is silently
    undone at the next save - and there is no bot-down window to exploit (the 05:00 job restarts the
    GAME, not the bot: the same bot pid survives straight through it). Rather than kill the bot from a
    scheduled task - which would relaunch it windowless, the one thing the owner has ruled out - the
    correction is queued in a file and the bot applies it itself the next time it starts, whatever
    causes that restart.

    Format (all deltas, so a double-apply is impossible once the file is retired):
      {"note": "...", "adjust": [{"sid": "...", "points_delta": 0, "reason": "..."}]}

    Only the numeric points field is honoured; nothing else in a record can be touched from here.
    Values are floored at 0 - a delta can never drive a counter negative. The file is RENAMED to
    .applied-<timestamp> rather than deleted, so there is an audit trail of what ran and when, and so
    a crash mid-apply cannot silently re-run it (the rename is the commit point... which is why the
    save happens FIRST: a crash between save and rename re-applies, so keep deltas idempotent-safe by
    checking the log). Never raises - a bad adjust file must not stop the bot from starting."""
    if not os.path.exists(PENDING_ADJUST_FILE):
        return
    try:
        with open(PENDING_ADJUST_FILE, encoding="utf-8") as f:
            spec = json.load(f) or {}
        rows = spec.get("adjust") or []
        if not isinstance(rows, list):
            raise ValueError("'adjust' must be a list")
        applied = 0
        for row in rows:
            if not isinstance(row, dict):
                continue
            sid = str(row.get("sid") or "")
            rec = RANK_DATA.get(sid)
            if not sid or not isinstance(rec, dict):
                activity(f"pending adjust SKIPPED - no record for {sid or '(blank sid)'}", "!")
                continue
            who = rec.get("name") or sid
            for field, key in (("points", "points_delta"),):
                try:
                    delta = float(row.get(key) or 0)
                except (TypeError, ValueError):
                    continue
                if not delta:
                    continue
                before = float(rec.get(field) or 0)
                after = max(0.0, before + delta)
                rec[field] = round(after, 1)
                activity(f"pending adjust: {who} {field} {before:g} -> {rec[field]:g} "
                         f"({delta:+g}; {row.get('reason') or 'no reason given'})", "RANK")
                applied += 1
        if applied:
            save_ranks()
        os.replace(PENDING_ADJUST_FILE, PENDING_ADJUST_FILE + ".applied-" + time.strftime("%Y%m%d-%H%M%S"))
        activity(f"pending rank adjust applied ({applied} change(s)) - {spec.get('note') or ''}", "INFO")
    except Exception as e:                        # noqa: BLE001 - never block startup on this
        activity(f"pending rank adjust FAILED, left in place: {e}", "!")


def save_ranks():
    # NAME-INTEGRITY BACKSTOP (2026-07-27): whatever path wrote it, an "ID: <steam64>"
    # sentinel must never persist as a player's name - normalise to the bare sid (the
    # renderers treat that as unnamed) and queue a Steam lookup to fill the real name.
    try:
        for _sid, _rec in RANK_DATA.items():
            _n = str((_rec or {}).get("name") or "").strip()
            if _ID_SENTINEL_RE.match(_n):
                _rec["name"] = _sid
                maybe_fetch_persona(_sid)
    except Exception:                                  # noqa: BLE001 - backstop must never block a save
        pass
    tmp = None
    try:
        # Before overwriting, keep a one-step undo (.bak) of the last known-good,
        # non-empty file plus a once-a-day snapshot. ranks.json is the lifetime
        # standings, so a bad/empty overwrite must never be silently unrecoverable.
        if os.path.exists(RANK_FILE):
            try:
                with open(RANK_FILE, encoding="utf-8") as f:
                    cur = json.load(f)
            except (OSError, json.JSONDecodeError):
                cur = None
            if isinstance(cur, dict) and cur:
                shutil.copyfile(RANK_FILE, RANK_FILE + ".bak")
                snap = os.path.join(os.path.dirname(RANK_FILE) or ".",
                                    f"ranks_backup_{time.strftime('%Y-%m-%d')}.json")
                if not os.path.exists(snap):
                    shutil.copyfile(RANK_FILE, snap)
        tmp = RANK_FILE + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(RANK_DATA, f, indent=2)
        for _attempt in range(5):              # Windows: dest may be briefly locked by a reader
            try:
                os.replace(tmp, RANK_FILE)
                break
            except PermissionError:
                if _attempt == 4:
                    raise
                time.sleep(0.04)
    except OSError as e:
        if tmp:
            try:
                os.remove(tmp)                  # don't leave a stale ranks.json.tmp behind
            except OSError:
                pass
        print(f"[ranks] save failed: {e}")
    maybe_publish_aggregate()                    # cross-server share: best-effort throttled publish (display only)


_LAST_RANK_SAVE = 0.0
def _maybe_save_ranks():
    """Throttle ranks.json writes from frequent score-accumulation events (>=5s apart).
    Important events (rank-ups, match end, awards) still call save_ranks() directly."""
    global _LAST_RANK_SAVE
    now = time.time()
    if now - _LAST_RANK_SAVE >= 5:
        save_ranks()
        _LAST_RANK_SAVE = now


def rank_index_for(points):
    idx = 0
    for i, r in enumerate(RANKS):
        if points >= r[0]:
            idx = i
        else:
            break
    return idx


def points_to_next(points):
    idx = rank_index_for(points)
    if idx + 1 >= len(RANKS):
        return None
    return _pts_i(RANKS[idx + 1][0] - points)


def prestige_tag_inner(abbr, rank_name, n):
    """The full rank tag, prestige-aware. For n>0 renders the configurable
    PRESTIGE_TEMPLATE (default '[{abbr} - {n}*]', e.g. '[ACE - 2*]'); for n<=0 the plain
    '[ABBR]'. Never raises -> falls back to the built-in prestige format."""
    if n > 0:
        try:
            return PRESTIGE_TEMPLATE.format(abbr=abbr, rank=rank_name, n=n)
        except Exception:                                # noqa: BLE001 - a bad template must never break a tag
            return f"[{abbr} - {n}*]"
    return f"[{abbr}]"


def prestige_label(abbr, rank_name, n):
    """Plain rank label for plugin Prefixed / RankNameTag (plugin wraps '['+label+']')
    and WebCC players/leaderboard (plain ABBR / 'OFFCDT - 1*' — NO outer brackets on WebCC).
    Prestige >=1 → template inner without outer brackets; else plain abbr. Never a name suffix."""
    if n <= 0:
        return abbr
    tag = prestige_tag_inner(abbr, rank_name, n)
    if len(tag) >= 2 and tag[0] == "[" and tag[-1] == "]":
        return tag[1:-1]
    return tag


def rank_tag(points, pn=0):
    if not RANKS:                                        # ladder off -> no tag (callers skip empty)
        return ""
    _, name, abbr, color = RANKS[rank_index_for(points)]
    return f"<color={color}>{prestige_tag_inner(abbr, name, pn)}</color>"


def _pts_i(n):
    """Whole-number points for every player-facing display (no float artifacts)."""
    try:
        return int(round(float(n)))
    except (TypeError, ValueError):
        return 0


def _pts(n):
    """Points label for chat (e.g. '31 pts'). Always a whole number."""
    return f"{_pts_i(n)} pts"


def rank_progress(points, pn=0):
    """Return (label, colour, tail) for a point total, e.g.
    ('[FLGOFF] Flying Officer', '#4C84E4', '3 pts to Flight Lieutenant').
    Prestige >=1 → tag is prestige_tag_inner (e.g. '[OFFCDT - 1*] Officer Cadet').
    tail is 'top rank!' once the player is maxed out. Shared by !rank + joins.
    Ladder off (empty RANKS) -> ('', '#FFFFFF', '') so callers render a rank-less line."""
    if not RANKS:
        return "", "#FFFFFF", ""
    idx = rank_index_for(points)
    _, rname, abbr, color = RANKS[idx]
    nxt = points_to_next(points)
    tail = "top rank!" if nxt is None else f"{_pts(nxt)} to {RANKS[idx + 1][1]}"
    return f"{prestige_tag_inner(abbr, rname, pn)} {rname}", color, tail


def local_points(steamid):
    """This server's OWN lifetime points for the player (what ranks.json / the ledger hold)."""
    return RANK_DATA.get(str(steamid), {}).get("points", 0)


def player_points(steamid):
    """Points used for RANK DISPLAY: local points PLUS, when cross-server sharing is on, the points the
    player earned on the host's OTHER servers -> the SAME combined rank/points show on every server.
    Display only; the award + ledger path uses local_points() so ranks.json and --audit stay per-server."""
    sid = str(steamid)
    pts = RANK_DATA.get(sid, {}).get("points", 0)
    if SHARED_RANKS_ENABLED:
        try:
            pts = pts + _other_ranks().get(sid, 0)
        except Exception:        # noqa: BLE001 - rank display must never raise
            pass
    return pts


# ── PRESTIGE (bot-side; cross-server aware) ─────────────────────────────────────────────────────────
# prestige.json (next to ranks.json) banks each player's prestige base + star count. NEVER edits
# ranks.json: the star system is a pure OVERLAY on top of the (cross-server) point total.
#   file shape: { "<steamid>": {"count": int, "base": float, "ts": float}, ... }
# cycle points = (cross-server TOTAL that !rank uses) - base. A player prestiges when their cycle
# reaches the top rank threshold; !yes banks base += cycle (base becomes their current total, so the
# cycle resets to ~0) and count += 1. The displayed RANK TIER is driven by CYCLE points, so after a
# prestige the player drops to Officer Cadet again but the rank TAG shows a star
# (default '[OFFCDT - 1*]'). Never a name suffix. With NO prestige data
# base=0/count=0 -> cycle == total and star == "" -> every display path is byte-identical to before.
PRESTIGE_FILE   = os.path.join(_BASE_DIR, "prestige.json")
PRESTIGE_DATA   = {}             # steamid -> {"count": int, "base": float, "ts": float}
def prestige_top():
    """Cycle points needed to prestige = the TOP rank's threshold, read live.

    This was a module-level constant frozen at import, so editing the ladder in the Web CC Ranks modal
    left every prestige check comparing against the OLD top threshold until the bot restarted - !prestige
    would refuse an eligible player, or accept an ineligible one, with no sign anything was stale."""
    try:
        return RANKS[-1][0] if RANKS else 100000
    except Exception:                                    # noqa: BLE001 - never break a prestige check
        return 100000
_PRESTIGE_PENDING = {}           # sid -> deadline_ts: awaiting a "!yes" confirm (60s window)
PRESTIGE_CONFIRM_WINDOW = 60


def load_prestige():
    global PRESTIGE_DATA
    try:
        with open(PRESTIGE_FILE, encoding="utf-8") as f:
            d = json.load(f)
        PRESTIGE_DATA = d if isinstance(d, dict) else {}
    except (OSError, ValueError):
        PRESTIGE_DATA = {}


def save_prestige():
    try:
        tmp = PRESTIGE_FILE + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(PRESTIGE_DATA, f, indent=1)
        os.replace(tmp, PRESTIGE_FILE)
    except OSError as e:
        print(f"[prestige] save failed: {e}")


def _compute_other_prestige():
    """Merge peers' published prestige (max count / max base per sid) so a prestige done on ANY server
    shows its star + cycle everywhere. Reads the SAME rankshare_*.json files as _compute_other_ranks;
    empty unless sharing is enabled. Never raises."""
    out = {}
    if not (SHARED_RANKS_ENABLED and SHARED_RANKS_DIR and os.path.isdir(SHARED_RANKS_DIR)):
        return out
    try:
        import glob
        mine = f"rankshare_{SERVER_INSTANCE_ID}.json"
        for path in glob.glob(os.path.join(SHARED_RANKS_DIR, "rankshare_*.json")):
            if os.path.basename(path) == mine:
                continue
            try:
                with open(path, encoding="utf-8") as f:
                    d = json.load(f)
            except (OSError, ValueError):
                continue
            pres = d.get("prestige", {}) if isinstance(d, dict) else {}
            for sid, rec in (pres.items() if isinstance(pres, dict) else []):
                if not isinstance(rec, dict):
                    continue
                try:
                    c = int(rec.get("count", 0) or 0)
                    b = float(rec.get("base", 0) or 0)
                except (TypeError, ValueError):
                    continue
                cur = out.setdefault(sid, {"count": 0, "base": 0.0})
                if c > cur["count"]:
                    cur["count"] = c
                if b > cur["base"]:
                    cur["base"] = b
    except Exception:                # noqa: BLE001 - display merge must never raise
        pass
    return out


_OTHER_PRESTIGE_CACHE = ({}, 0.0)


def _other_prestige():
    """Cached peer-prestige merge; kept warm by the shared-ranks daemon (60s inline fallback)."""
    global _OTHER_PRESTIGE_CACHE
    cached, at = _OTHER_PRESTIGE_CACHE
    now = time.time()
    if now - at < 60:
        return cached
    out = _compute_other_prestige()
    _OTHER_PRESTIGE_CACHE = (out, now)
    return out


def _prestige_merged(sid):
    """Effective (count, base) for a sid = max of our local record and every peer's (both grow
    monotonically at each prestige, so max is the most-recent authoritative value)."""
    sid = str(sid)
    rec = PRESTIGE_DATA.get(sid) if isinstance(PRESTIGE_DATA.get(sid), dict) else {}
    try:
        count = int(rec.get("count", 0) or 0)
    except (TypeError, ValueError):
        count = 0
    try:
        base = float(rec.get("base", 0) or 0)
    except (TypeError, ValueError):
        base = 0.0
    if SHARED_RANKS_ENABLED:
        try:
            peer = _other_prestige().get(sid)
            if peer:
                count = max(count, int(peer.get("count", 0)))
                base = max(base, float(peer.get("base", 0)))
        except Exception:            # noqa: BLE001
            pass
    return count, base


def prestige_count(sid):
    return _prestige_merged(sid)[0]


def prestige_base(sid):
    return _prestige_merged(sid)[1]


def cycle_points(steamid):
    """Points that drive the DISPLAYED rank tier: the cross-server total minus the prestige base.
    Equals player_points() when the player has never prestiged (base 0)."""
    return max(0.0, player_points(steamid) - prestige_base(steamid))


def do_prestige(sid):
    """Bank a prestige: base += current cycle (base becomes the current total), count += 1. Returns the
    new count, or None if no longer eligible. NEVER touches ranks.json."""
    sid = str(sid)
    cyc = cycle_points(sid)
    if cyc < prestige_top():
        return None
    count, base = _prestige_merged(sid)
    new_base = base + cyc                     # == player_points(sid) at this moment -> cycle resets to ~0
    PRESTIGE_DATA[sid] = {"count": count + 1, "base": round(new_base, 1), "ts": time.time()}
    save_prestige()
    return count + 1


load_prestige()


def combined_rankup(steamid, new_local_pts, delta):
    """#4 annIdx: gate rank-up announcements on the COMBINED (this server + the host's other servers)
    total when cross-server sharing is ON, so the announced rank matches the combined rank the player
    actually shows. Prestige-aware: the rank tier is driven by CYCLE points (total - prestige base), so
    a player climbing back up after a prestige re-announces each rank. Returns (crossed, old_idx, new_idx)
    where both index RANKS (old = rank left, new = announced). With sharing OFF + no prestige it matches
    the plain local old_idx/new_idx gate. Never raises."""
    try:
        other = _other_ranks().get(str(steamid), 0) if SHARED_RANKS_ENABLED else 0
    except Exception:            # noqa: BLE001 - a rank-up gate must never raise into the hot path
        other = 0
    base = prestige_base(steamid)
    old_idx = rank_index_for(max(0.0, (new_local_pts - delta) + other - base))
    new_idx = rank_index_for(max(0.0, (new_local_pts + other) - base))
    return (new_idx > old_idx), old_idx, new_idx


_ID_SENTINEL_RE = re.compile(r"^ID:\s*\d{17}$")
def _storable_name(sid, name):
    """Name safe to store, or None. Never store the plugin's 'ID: <steam64>' placeholder (emitted
    while the server's Steam hasn't resolved a persona) - storing it would poison the bot->plugin
    name-fallback loop and mask the real name forever."""
    n = str(name or "").strip()
    if not n or n == str(sid) or _ID_SENTINEL_RE.match(n):
        return None
    # ...and never store OUR OWN stand-in either. display_name() returns WELCOME_FALLBACK_NAME
    # ("Pilot") when nothing resolves, and that string passed every test above, so it got written to
    # ranks.json - after which display_name found a "good" stored name and returned "Pilot" forever,
    # masking the real one exactly as the ID sentinel would have. Found live 2026-07-31: a player with
    # a PRIVATE Steam profile ("no persona in XML") was stuck as Pilot permanently.
    # Trade accepted: a player genuinely named "Pilot" will not have it stored. Vanishingly rare
    # against poisoning every unresolvable account forever.
    if n.casefold() == WELCOME_FALLBACK_NAME.casefold():
        return None
    return n


# CHAT NAME GUARD (2026-07-28): _storable_name keeps placeholders out of STORAGE, but a dozen
# announce paths compose their chat line straight from the plugin's telemetry "n"/"kn" field or
# an RCON displayName, both of which fall back to the game's own "ID: <steam64>" sentinel while
# Steam hasn't resolved that account (private/deleted profiles + web-lookup 429s never resolve).
# Guard at the DELIVERY choke-point instead of at each site, exactly like the font-safe arrow
# swap: no in-game line - present or future - can then carry a placeholder name.
_ID_SENTINEL_ANY_RE = re.compile(r"ID:\s*(\d{17})")
_BARE_SID_RE = re.compile(r"(?<!\d)(\d{17})(?!\d)")
CHAT_UNKNOWN_NAME = "a pilot"


def _known_name(sid):
    """Best real display name we hold for this sid, or None (never a placeholder)."""
    return (_storable_name(sid, PLAYER_NAMES.get(sid))
            or _storable_name(sid, (RANK_DATA.get(sid, {}) or {}).get("name")))


def chat_name_safe(text):
    """Scrub placeholder player names out of one outgoing in-game chat line.
      'ID: <steam64>'  -> the real name if we know it, else CHAT_UNKNOWN_NAME (+ queue a lookup)
      bare <steam64>   -> the real name if we know it, else left alone (it is the only id we have)
    Never raises: on any error the original text is delivered unchanged (fail-open)."""
    try:
        s = str(text)
        if "ID:" not in s and not _BARE_SID_RE.search(s):
            return s                                   # fast path: no sid-shaped token at all

        def _sentinel(m):
            sid = m.group(1)
            good = _known_name(sid)
            if good:
                return good
            try:
                maybe_fetch_persona(sid)               # can't name them -> ask Steam now
            except Exception:                          # noqa: BLE001 - chat must never raise
                pass
            return CHAT_UNKNOWN_NAME

        def _bare(m):
            return _known_name(m.group(1)) or m.group(1)

        return _BARE_SID_RE.sub(_bare, _ID_SENTINEL_ANY_RE.sub(_sentinel, s))
    except Exception:                                  # noqa: BLE001 - fail-open
        return str(text)


# STEAM NAME LOOKUP (Tomo 2026-07-27): "how do players know what the names are? do the exact
# same thing." Game clients resolve names from Steam themselves; when the SERVER's own async
# resolution stalls (some accounts never resolve server-side post-update), the bot asks Steam
# directly via the public-profile XML (no API key) the moment it sees a player it can't name.
# The fetched name lands in ranks.json + PLAYER_NAMES and re-pushes plugin_ranks.txt, so the
# plugin's LastKnownName fallback replaces the "ID: <steam64>" placeholder within seconds of
# the join instead of never.
_PERSONA_RESULTS = []          # worker threads append (sid, name); main loop consumes
_PERSONA_TRIED = {}            # sid -> monotonic ts of last attempt (600s retry cooldown)
_PERSONA_FAILS = {}            # sid -> consecutive failed lookups (bounds the fast-retry loop)
_PERSONA_MAX_TRIES = 3         # after this many misses: stop the ~60s retry AND welcome anyway
_PERSONA_NAME_RE = re.compile(r"<steamID><!\[CDATA\[(.*?)\]\]></steamID>", re.S)


# Anchored to _BASE_DIR like every other bot-owned file (a cwd-relative path lands wherever the
# launcher happened to start us) and size-capped: this is a debug trail, not a record we keep.
_PERSONA_LOG     = os.path.join(_BASE_DIR, "persona_debug.log")
_PERSONA_LOG_MAX = 512 * 1024      # roll to .1 past this; one previous generation is kept


def _persona_log(msg):
    try:
        try:
            if os.path.getsize(_PERSONA_LOG) > _PERSONA_LOG_MAX:
                os.replace(_PERSONA_LOG, _PERSONA_LOG + ".1")
        except OSError:
            pass
        with open(_PERSONA_LOG, "a", encoding="utf-8") as f:
            f.write(time.strftime("%H:%M:%S") + "  " + str(msg) + chr(10))
    except OSError:
        pass


def _persona_failed(sid):
    """Record a failed lookup. The first few misses retry fast (~60s) because the name usually
    lands on the 2nd/3rd try; after _PERSONA_MAX_TRIES the profile is almost certainly
    unresolvable (deleted / limited account with no community profile) so we fall back to the
    normal 600s cooldown. Unbounded ~60s retries burned ~55 Steam lookups an HOUR on one stuck
    sid (107 in one session, live 2026-07-28) and rate-limited (HTTP 429) the FIRST lookup of
    players whose names would have resolved - leaving them unnamed and unwelcomed too."""
    _PERSONA_FAILS[sid] = n = _PERSONA_FAILS.get(sid, 0) + 1
    if n < _PERSONA_MAX_TRIES:
        _PERSONA_TRIED[sid] = time.monotonic() - 540   # failed lookup -> retry in ~60s, not 600


_PERSONA_UA = {"User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64)"}
# g_rgProfileData is the profile page's own JSON blob - the most reliable of the three places the
# name appears in that HTML, and the only one that is unambiguously the persona (the <title> is
# "Steam Community :: <name>" but is also "Steam Community :: Error" on a bad fetch).
_PERSONA_HTML_BLOB = re.compile(r"g_rgProfileData\s*=\s*(\{.*?\});", re.S)
_PERSONA_HTML_JSON = re.compile(r'"personaname"\s*:\s*"([^"]+)"')


def _steam_persona_html(sid):
    """Fallback name lookup off the ordinary profile PAGE. Returns "" if it can't find one.

    WHY: the ?xml=1 endpoint returns an EMPTY <steamID> for anyone who has never set up a Steam
    Community profile - it answers 400 bytes ending in "This user has not yet set up their Steam
    Community profile". Those players still HAVE a persona name; it is simply absent from that feed.
    The game server's own GetPlayerName() hits the same wall and falls back to "ID: <sid>", so
    without this the pilot can never be named by anything we control. Measured 2026-08-09: 5 of 787
    records on S1 and 17 of 2535 on S2 were stuck this way, and every one of them resolved here.

    Deliberately NOT the Steam Web API: that needs an API key, and this needs nothing. The cost is
    one ~30KB GET instead of 400B, paid only for the ~0.7% of lookups the cheap endpoint failed."""
    try:
        req = urllib.request.Request("https://steamcommunity.com/profiles/%s" % sid,
                                     headers=_PERSONA_UA)
        html = urllib.request.urlopen(req, timeout=10).read().decode("utf-8", "replace")
    except Exception as e:                         # noqa: BLE001 - still best-effort
        _persona_log(f"...{sid[-4:]} html FAIL {type(e).__name__}: {e}")
        return ""
    m = _PERSONA_HTML_BLOB.search(html)
    if m:
        try:
            nm = str(json.loads(m.group(1)).get("personaname") or "").strip()
            if nm:
                return nm
        except (ValueError, TypeError):
            pass                                   # fall through to the looser match
    m = _PERSONA_HTML_JSON.search(html)
    if m:
        # JSON string escapes (é, \") survive the regex - decode them rather than storing raw
        try:
            return str(json.loads('"' + m.group(1) + '"')).strip()
        except ValueError:
            return m.group(1).strip()
    _persona_log(f"...{sid[-4:]} html had no persona either ({len(html)}B)")
    return ""


def _steam_persona_worker(sid):
    try:
        xml = urllib.request.urlopen(
            "https://steamcommunity.com/profiles/%s/?xml=1" % sid, timeout=8
        ).read().decode("utf-8", "replace")
        m = _PERSONA_NAME_RE.search(xml)
        # the XML CDATA carries HTML entities (&lt; &amp; ...) - unescape or names like
        # "XiJinPing<3" get stored as "XiJinPing&lt;3" and never self-heal
        name = _html.unescape(m.group(1).strip()) if m else ""
        if not name:
            # empty CDATA = profile never set up; the HTML page still knows them
            _persona_log(f"...{sid[-4:]} no persona in XML ({len(xml)}B) - trying the profile page")
            name = _steam_persona_html(sid)
        if name:
            _PERSONA_RESULTS.append((sid, name))   # list.append is GIL-atomic
            _PERSONA_FAILS.pop(sid, None)
            _persona_log(f"...{sid[-4:]} fetched OK")
        else:
            _persona_failed(sid)
    except Exception as e:                         # noqa: BLE001 - lookup is best-effort
        _persona_log(f"...{sid[-4:]} FAIL {type(e).__name__}: {e}")
        _persona_failed(sid)


def maybe_fetch_persona(sid):
    """Kick off a background Steam name lookup for a player we can't name yet. Cheap to call
    anywhere a nameless sid is seen; dedupes + cools down internally, never blocks the loop."""
    sid = str(sid or "")
    if len(sid) != 17 or not sid.isdigit():
        return
    known = str((RANK_DATA.get(sid) or {}).get("name") or "").strip()
    if known and known != sid and not _ID_SENTINEL_RE.match(known):
        return                                     # already properly named
    now = time.monotonic()
    if now - _PERSONA_TRIED.get(sid, -1e9) < 600:
        return
    _PERSONA_TRIED[sid] = now
    _persona_log(f"...{sid[-4:]} fetch queued")
    threading.Thread(target=_steam_persona_worker, args=(sid,), daemon=True).start()


def pump_persona_results():
    """Main-loop consumer: apply fetched names and trigger a rank-file push so the plugin's
    name fallback updates in the same loop. Returns True if anything was applied."""
    applied = False
    while _PERSONA_RESULTS:
        sid, name = _PERSONA_RESULTS.pop()
        good = _storable_name(sid, name)
        if not good:
            continue
        rec = RANK_DATA.get(sid)
        if rec is None:
            rec = RANK_DATA[sid] = {"name": good, "points": 0.0}
        else:
            cn = str(rec.get("name") or "").strip()
            if cn and cn != sid and not _ID_SENTINEL_RE.match(cn):
                continue                           # a real name arrived some other way - keep it
            rec["name"] = good
        PLAYER_NAMES[sid] = good
        applied = True
        activity(f"Steam lookup named a placeholder player: {good}", "NAME")
        print(f"[persona] ...{sid[-4:]} -> {good}")
    if applied:
        save_ranks()
        _RANK_PUSH_FLAG[0] = True
    return applied


def display_name(sid, raw=None):
    """Name safe to SHOW a player. Never renders the 'ID: <steam64>' placeholder or a bare
    SteamID: falls back through the layers that might know a real name, then to a neutral
    stand-in, and kicks a Steam lookup so the next line can use the real one."""
    sid = str(sid or "")
    good = _storable_name(sid, raw)
    if good:
        return good
    for cand in (PLAYER_NAMES.get(sid), (RANK_DATA.get(sid) or {}).get("name")):
        good = _storable_name(sid, cand)
        if good:
            return good
    if sid:
        maybe_fetch_persona(sid)
    return WELCOME_FALLBACK_NAME


# Rank-funds announces are frequently the FIRST sighting of a player, so the name often
# lands a fraction of a second later (see persona_debug.log: queued + fetched OK in the same
# second). Printing the stand-in immediately is what produced "Pilot: +120 funds ...". Hold
# the line for a moment and re-resolve at send time; only fall back if it truly never comes.
_PENDING_FUNDS = []                 # [{sid, funds, rank, at}]
FUNDS_NAME_GRACE = 8.0              # seconds to wait for a real name before giving up


def queue_funds_announce(sid, funds, rank_n):
    _PENDING_FUNDS.append({"sid": str(sid or ""), "funds": funds, "rank": rank_n,
                           "at": time.monotonic()})


def pump_funds_announces(rc):
    """Emit any held rank-funds line whose name has resolved, or whose grace has expired."""
    if not _PENDING_FUNDS:
        return
    now = time.monotonic()
    keep = []
    for item in _PENDING_FUNDS:
        sid = item["sid"]
        known = _storable_name(sid, PLAYER_NAMES.get(sid)) or \
                _storable_name(sid, (RANK_DATA.get(sid) or {}).get("name"))
        expired = (now - item["at"]) >= FUNDS_NAME_GRACE
        if not known and not expired:
            keep.append(item)
            continue
        nm = known or display_name(sid)          # display_name supplies the stand-in + a lookup
        funds_str = f"{item['funds']:,}"
        try:
            activity(f"{nm}: +{funds_str} funds for reaching rank {item['rank']}", "RANK")
            if sysmsg_on("rankfunds"):
                tmpl = sysmsg_text("rankfunds", _SYSMSG_RANKFUNDS_DEFAULT)
                rc.say(_render_template(tmpl, funds=funds_str, rank=item["rank"], name=nm))
        except Exception as e:                   # noqa: BLE001 - an announce must never break the loop
            print(f"[rankfunds] announce failed: {e}")
    _PENDING_FUNDS[:] = keep


def award_points(steamid, name, n):
    """Add n points to a player; return (old_rank_idx, new_rank_idx, new_points)."""
    sid = str(steamid)
    good = _storable_name(sid, name)
    if good is None:
        maybe_fetch_persona(sid)                   # server can't name them -> ask Steam directly
    rec = RANK_DATA.setdefault(sid, {"name": good or sid, "points": 0})
    if good:
        rec["name"] = good
    old_idx = rank_index_for(rec.get("points", 0))
    rec["points"] = max(0.0, round(rec.get("points", 0) + n, 1))   # one decimal (real score is fractional); never negative
    return old_idx, rank_index_for(rec["points"]), rec["points"]


# ── Time played ───────────────────────────────────────────────────────────────────────────────────
# Seconds each player has been ON this server, accumulated from the roster poll that already runs.
# No plugin change needed. Persisted in ranks.json as rec["timePlayed"], so it survives bot restarts.
_TIME_LAST_TICK = [0.0]
# The poll is nominally a few seconds. Anything longer than this means the loop stalled, the relay
# blipped, or the bot was restarted - crediting that whole gap would invent hours nobody played, so the
# interval is capped rather than trusted. A stalled poll therefore UNDER-counts slightly, which is the
# right way to be wrong for a persistent stat.
_TIME_MAX_STEP = 30.0


def accrue_time_played(sids, now):
    """Credit the elapsed interval to everyone currently on the roster.

    Driven by the roster poll rather than a join/leave pairing on purpose: a missed leave event (a
    crash, a relay outage, a bot restart) would otherwise leave a session open forever and credit a
    player days of playtime. Sampling can only ever be wrong by one interval."""
    prev = _TIME_LAST_TICK[0]
    _TIME_LAST_TICK[0] = now
    if not prev:
        return                                            # first tick after startup: no interval yet
    step = now - prev
    if step <= 0 or step > _TIME_MAX_STEP:
        return                                            # stall / clock jump / restart gap - do not credit
    changed = False
    for sid in sids:
        if not sid:
            continue
        rec = RANK_DATA.get(sid)
        if rec is None:
            continue                                      # unknown player: ensure_player runs elsewhere
        rec["timePlayed"] = float(rec.get("timePlayed") or 0.0) + step
        changed = True
    if changed:
        _RANKS_DIRTY[0] = True                            # coalesced save; never a write per poll


def player_stat_card(sid, mode=None):
    # mode: retained for caller compatibility (cc_web passes the dashboard's match mode); the card
    # itself is now mode-agnostic.
    """Everything known about one player, derived once so !stats and the Web CC card cannot disagree.

    Points are CYCLE points for the tier (what the in-game name tag shows) but LIFETIME for the score,
    which is the same split the leaderboard uses. Returns plain values - no colour markup - so each
    surface formats them its own way."""
    sid = str(sid or "")
    rec = RANK_DATA.get(sid) or {}
    # POOLED lifetime points (local + peers when sharing is on) - the same figure !rank, the welcome
    # line and the leaderboard show. rec['points'] alone put a local score next to a pooled tier tag.
    pts = float(player_points(sid) or 0.0)
    wins, losses = int(rec.get("wins") or 0), int(rec.get("losses") or 0)

    cyc = cycle_points(sid)
    idx = rank_index_for(cyc)
    pn = prestige_count(sid)
    nxt_label, to_next = "", 0.0
    if RANKS:
        _thr, rname, abbr, colour = RANKS[idx]
        label = prestige_label(abbr, rname, pn)
        if idx + 1 < len(RANKS):
            nxt_thr, _n2, nxt_abbr, _c2 = RANKS[idx + 1]
            nxt_label, to_next = nxt_abbr, max(0.0, float(nxt_thr) - cyc)
    else:                                                # ladder off -> rank-less card
        rname, abbr, colour, label = "", "", "#FFFFFF", ""

    # leaderboard position by lifetime points, ties broken deterministically by sid so a refresh
    # cannot make somebody's rank appear to move on its own. Ranked over the SAME source
    # leaderboard_lines/webcc use - the fleet aggregate when sharing is on (local fallback) - so
    # the card's '#N of M' agrees with the board it points at; W/L follow the aggregate too.
    src = RANK_DATA
    if SHARED_RANKS_ENABLED:
        try:
            agg = read_aggregate_ranks()          # {sid: {name, points, wins, losses}} summed across servers
            if agg:
                src = agg
        except Exception:                         # noqa: BLE001 - a stat card must never raise
            src = RANK_DATA
    if src is not RANK_DATA and isinstance(src.get(sid), dict):
        wins = int(src[sid].get("wins") or 0)
        losses = int(src[sid].get("losses") or 0)
    ranked = sorted(((float(r.get("points") or 0.0), s) for s, r in src.items()
                     if float(r.get("points") or 0.0) > 0), key=lambda t: (-t[0], t[1]))
    position = next((i for i, (_p, s) in enumerate(ranked, start=1) if s == sid), 0)

    return {
        "sid": sid,
        "name": rec.get("name") or PLAYER_NAMES.get(sid) or sid,
        "rank_label": label, "rank_name": rname, "abbr": abbr, "colour": colour,
        "prestige": pn,
        "points": pts, "cycle_points": cyc,
        "next_label": nxt_label, "to_next": to_next,
        "wins": wins, "losses": losses,
        "winrate": (100.0 * wins / (wins + losses)) if (wins + losses) else 0.0,
        "time_played_s": float(rec.get("timePlayed") or 0.0),
        "time_played": fmt_time_played(rec.get("timePlayed")),
        "position": position, "total": len(ranked),
        "online": sid in ROSTER_BY_SID,
    }


def search_players(query, limit=12):
    """Partial, case-insensitive name search over every stored player.

    Matches the CURRENT name and the last-known name, so a player who renamed is still findable by the
    name the operator remembers. Exact and prefix matches sort first - with ~1858 records a substring
    search alone buries the obvious answer."""
    q = str(query or "").strip().lower()
    if not q:
        return []
    hits = []
    for sid, rec in RANK_DATA.items():
        names = {str(rec.get("name") or ""), str(PLAYER_NAMES.get(sid) or ""),
                 str(rec.get("lastName") or "")}
        names.discard("")
        best = None
        for n in names:
            nl = n.lower()
            if nl == q:
                best = 0
            elif nl.startswith(q) and (best is None or best > 1):
                best = 1
            elif q in nl and (best is None or best > 2):
                best = 2
        if best is None:
            continue
        hits.append((best, -float(rec.get("points") or 0.0), sid,
                     rec.get("name") or PLAYER_NAMES.get(sid) or sid))
    hits.sort()
    return [{"sid": s, "name": n} for _r, _p, s, n in hits[:max(1, int(limit))]]


def fmt_time_played(seconds):
    """Human playtime: 45m / 3h 20m / 128h."""
    try:
        s = int(float(seconds or 0))
    except (TypeError, ValueError):
        return "0m"
    if s < 60:
        return f"{s}s"
    m, h = (s // 60) % 60, s // 3600
    if h <= 0:
        return f"{m}m"
    if h < 24:
        return f"{h}h {m}m" if m else f"{h}h"
    return f"{h}h"


def ensure_player(steamid, name):
    """Make sure every player who's seen online has a record (rank 0 = Officer
    Cadet if they've never scored), so the roster isn't limited to point-earners.
    Returns True if RANK_DATA changed (new record or updated name) -> caller saves."""
    sid = str(steamid)
    if not sid:
        return False
    good = _storable_name(sid, name)
    if good is None:
        maybe_fetch_persona(sid)                   # server can't name them -> ask Steam directly
    rec = RANK_DATA.get(sid)
    if rec is None:
        RANK_DATA[sid] = {"name": good or sid, "points": 0.0}
        return True
    if good and rec.get("name") != good:
        rec["name"] = good
        return True
    return False


# ----------------------------------------------------------------------------
# Per-match tracking (match_history.json + points_ledger.jsonl)
# ----------------------------------------------------------------------------

def _match_player(sid, name, faction):
    """Get-or-create this match's record for a player."""
    p = CUR_MATCH["players"].setdefault(
        sid, {"name": name or sid, "faction": faction or "", "points": 0, "captures": 0, "won": False})
    if name:
        p["name"] = name
    if faction:
        p["faction"] = faction
    return p


def match_ensure(mission=None):
    """Lazily start a match accumulator if none is open (matches are created on the
    first award/result and finalised on Mission complete)."""
    global CUR_MATCH
    if CUR_MATCH is None:
        CUR_MATCH = {
            "match_id": time.strftime("%Y-%m-%d %H:%M:%S"),
            "mission": mission or CURRENT_MISSION,
            "started": time.strftime("%Y-%m-%d %H:%M"),
            "started_mono": time.time(),
            "result": None,
            "players": {},
        }
    return CUR_MATCH


def ledger_award(sid, name, pts, category, reason, balance, match=None):
    """Append one discrete points event to points_ledger.jsonl for the admin audit / !why.
    category in {score, score-spike, win, place_1st, place_2nd, place_3rd, grant}.
    NOTE for --audit: only categories that actually moved lifetime points carry a real `pts`;
    purely informational lines (score-spike) carry pts:0 with the value in `reason`, so
    summing `pts` across the ledger still equals the points awarded (ledger <= ranks invariant)."""
    try:
        with open(LEDGER_FILE, "a", encoding="utf-8") as f:
            f.write(json.dumps({
                "ts": time.strftime("%Y-%m-%d %H:%M:%S"),
                "match": match,
                "steamid": str(sid), "name": str(name),
                "pts": round(float(pts), 1), "category": str(category),
                "reason": str(reason), "balance": round(float(balance), 1),
            }) + "\n")
    except OSError as e:
        print(f"[ledger] {category} append failed: {e}")


def _flush_score_accum(match_id):
    """Write ONE aggregated 'score' ledger line per player for the in-game score they
    accumulated this match (snaps are far too frequent to ledger one-by-one), then reset."""
    for _sid, (_nm, _gain) in SCORE_ACCUM.items():
        if _gain:
            ledger_award(_sid, _nm, _gain, "score", "in-game score (match total)",
                         RANK_DATA.get(_sid, {}).get("points", 0), match=match_id)
    SCORE_ACCUM.clear()


def match_award(sid, name, faction, pts, reason, kind, balance):
    """Record one point award into the current match + append to the audit ledger."""
    match_ensure()
    p = _match_player(sid, name, faction)
    p["points"] += pts
    if kind == "capture":
        p["captures"] += 1
    elif kind == "win":
        p["won"] = True
    category = kind if kind in ("capture", "win") else "score"
    ledger_award(sid, name, pts, category, reason, balance,
                 match=CUR_MATCH["match_id"] if CUR_MATCH else None)


def match_set_result(result_str):
    """Record the match outcome (before Mission complete finalises the record)."""
    match_ensure()
    CUR_MATCH["result"] = result_str


def match_finalize(rc, online_players):
    """Mission ended -> stamp result/duration, fold in online (0-pt) participants,
    persist to match_history.json, announce a summary, and clear the accumulator."""
    global CUR_MATCH
    if CUR_MATCH is None:
        return       # no captures and no result tracked this round -> nothing to record
    match_ensure()
    m = CUR_MATCH
    _flush_score_accum(m["match_id"])              # one aggregated "score" ledger line per player
    for p in online_players:                       # count present players who didn't score
        sid = str(p.get("steamId") or "")
        if sid:
            _match_player(sid, p.get("displayName"), p.get("faction"))
    if not m["players"]:
        CUR_MATCH = None
        return
    record = {
        "match_id": m["match_id"], "mission": m["mission"],
        "started": m["started"], "ended": time.strftime("%Y-%m-%d %H:%M"),
        "duration_min": max(0, round((time.time() - m["started_mono"]) / 60)),
        "result": m["result"] or "ended early (vote)",
        "players": {sid: {k: pv[k] for k in ("name", "faction", "points", "captures", "won")}
                    for sid, pv in m["players"].items()},
    }
    # Load existing history, recovering from corruption the way load_ranks() does:
    # if the file is unreadable / not a list, set it aside (.corrupt) and start
    # fresh so future matches still record instead of being silently dropped forever.
    hist = []
    if os.path.exists(MATCH_HISTORY_FILE):
        try:
            with open(MATCH_HISTORY_FILE, encoding="utf-8") as f:
                hist = json.load(f)
            if not isinstance(hist, list):
                raise ValueError("match history is not a list")
        except (OSError, ValueError, json.JSONDecodeError) as e:
            print(f"[match] history unreadable ({e}); backing up to .corrupt and starting fresh")
            try:
                os.replace(MATCH_HISTORY_FILE, MATCH_HISTORY_FILE + ".corrupt")
            except OSError:
                pass
            hist = []
    hist.append(record)
    try:
        tmp = MATCH_HISTORY_FILE + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(hist, f, indent=2)
        os.replace(tmp, MATCH_HISTORY_FILE)
    except OSError as e:
        print(f"[match] history save failed: {e}")
    scored = sorted(((pv["name"], pv["points"]) for pv in m["players"].values() if pv["points"] > 0),
                    key=lambda t: -t[1])
    if sysmsg_on("matchend"):                          # owner can hide the end-of-match summary (webcc Messages tab)
        # No {record['result']} here (2026-08-17): the victory line above already named the winner,
        # and this header repeating it was the third statement of the same fact. The result is still
        # RECORDED - match_history.json, the webcc match card and !matches all read it from `record`.
        rc.say(f"<color=#FFD200>== Match over - {_plain(mission_display(record['mission']))} - "
               f"{record['duration_min']} min ==</color>")
        rc.say("This match: " + (", ".join(f"{nm} +{_pts_i(pts)}" for nm, pts in scored[:10])
                                 or "no points scored"))
    print(f"[match] finalised {record['match_id']} ({len(m['players'])} players, {record['result']})")
    CUR_MATCH = None


def fold_match_stats():
    """{steamid: {'matches': n, 'wins': n}} folded from match_history.json."""
    stats = {}
    try:
        with open(MATCH_HISTORY_FILE, encoding="utf-8") as f:
            hist = json.load(f)
    except (OSError, json.JSONDecodeError):
        return stats
    for rec in hist:
        for sid, pv in rec.get("players", {}).items():
            s = stats.setdefault(sid, {"matches": 0, "wins": 0})
            s["matches"] += 1
            if pv.get("won"):
                s["wins"] += 1
    return stats


def player_match_detail(sid):
    """Per-player record: matches, wins, win%, best single-match points, last-5 W/L."""
    sid = str(sid)
    out = {"matches": 0, "wins": 0, "winpct": 0, "best": 0, "last5": ""}
    try:
        with open(MATCH_HISTORY_FILE, encoding="utf-8") as f:
            hist = json.load(f)
    except (OSError, json.JSONDecodeError):
        return out
    seq = []
    for rec in hist:
        pv = rec.get("players", {}).get(sid)
        if not pv:
            continue
        out["matches"] += 1
        out["best"] = max(out["best"], pv.get("points", 0))
        won = bool(pv.get("won"))
        if won:
            out["wins"] += 1
        seq.append("W" if won else "L")
    if out["matches"]:
        out["winpct"] = round(100 * out["wins"] / out["matches"])
        out["last5"] = " ".join(seq[-5:])
    return out


def recent_ledger_for(sid, n=4):
    """Last n ledger awards for a SteamID (most recent last).

    STABLE-AUDIT fix: only the TAIL of the ledger is scanned (deque over the last 4000 lines).
    The ledger grows forever and a full-file JSON parse per !why ran on the main poll loop -
    on a months-old server that's a multi-MB stall for a chat command. 4000 lines is weeks of
    events; anything older than that isn't 'recent'.
    """
    sid = str(sid)
    rows = []
    try:
        with open(LEDGER_FILE, encoding="utf-8") as f:
            tail = collections.deque(f, maxlen=4000)
    except OSError:
        return []
    for line in tail:
        line = line.strip()
        if not line:
            continue
        try:
            e = json.loads(line)
        except json.JSONDecodeError:
            continue
        if str(e.get("steamid")) == sid:
            rows.append(e)
    return rows[-n:]


JOIN_LOGGED = set()          # sids whose [JOIN] feed line has been written this session


def log_join_line(sid):
    """Record a join in the activity feed the moment the roster shows them, whatever the
    in-game welcome does. Never raises."""
    try:
        sid = str(sid or "")
        if not sid or sid in JOIN_LOGGED:
            return
        JOIN_LOGGED.add(sid)
        nm = display_name(sid)                       # real name if known, neutral stand-in otherwise
        # Same split as the welcome line the player is about to receive: tier from the CYCLE, number
        # shown is the lifetime fleet TOTAL. Kept in step deliberately - reading "joined (AVM, 26755)"
        # in the feed while the pilot was greeted with 127153 makes the monitor look wrong.
        label, _color, _tail = rank_progress(cycle_points(sid), prestige_count(sid))
        _who = f"{label}, {_pts(player_points(sid))}" if label else _pts(player_points(sid))
        activity(f"{nm} joined   ({_who})   -  {len(ROSTER_BY_SID)} online", "JOIN")
    except Exception as e:                           # noqa: BLE001
        print(f"[join] feed line failed: {e}")


def queue_welcome(sid, name, delay=None):
    """Schedule a welcome ~`delay`s after first sighting so the player's client/chat has
    loaded enough to actually see it. Deduped via WELCOMED and the queue itself; if the
    player leaves before the deadline the entry is dropped in the roster-poll left-handler,
    so a quick join/leave produces no welcome. Drained from the main loop (single-threaded)."""
    if delay is None:
        delay = sysmsg_delay("welcome", WELCOME_DELAY)   # owner-tunable join delay (webcc Messages tab)
    sid = str(sid)
    if not sid or sid in WELCOMED or sid in WELCOME_QUEUE:
        return
    WELCOME_QUEUE[sid] = (time.time() + delay, name, 0)


def seed_welcomed_on_restart(current):
    """FIRST roster poll after a bot restart: everyone in `current` was already online
    through the restart, so mark the ones whose names we know as welcomed -- WELCOMED
    starts empty each run, and without this the SECOND poll re-welcomes the whole
    server. Unnamed+unranked sids stay unseeded on purpose: a brand-new player who
    joined during the bot's downtime still gets their one welcome once their name
    syncs (same name test as the welcome loop, so the seed suppresses exactly the
    sids that loop would have re-welcomed)."""
    WELCOMED.update(sid for sid in current
                    if _storable_name(sid, PLAYER_NAMES.get(sid))
                    or _storable_name(sid, RANK_DATA.get(sid, {}).get("name")))


def say_welcome(rc, sid, name):
    """Welcome a player ONCE per session (deduped via WELCOMED, cleared when they leave).
    The team is shown by the game's own client-side "X joined [faction]" message, so the
    bot's welcome just carries the rank + points. Prestige is inside the rank tag only."""
    sid = str(sid)
    if not sid or sid in WELCOMED:
        return True
    # NOT marked WELCOMED yet - see the return paths below. Marking here meant a greeting lost to a
    # relay outage was gone for the whole session (the drain has already popped the queue entry).
    # the neutral stand-in is for the chat line only - storing it would overwrite the sid
    # placeholder in ranks.json and permanently stop the Steam lookup from self-healing
    if ensure_player(sid, None if name == WELCOME_FALLBACK_NAME else name):
        save_ranks()
    # TWO different numbers here, on purpose (owner's call, 2026-08-05):
    #   cyc = cross-server total MINUS the prestige base -> drives the RANK TIER, so a prestige still
    #         resets the ladder and the [ABBR - n*] tag keeps meaning "this far since the reset".
    #   pts = the LIFETIME cross-server total -> the number the line actually SHOWS.
    # He wants one recognised score, not a per-server or per-cycle figure: "I really just want players
    # to have one ranking recognised ... the cross server ranking total points". Both are already
    # fleet-wide (player_points pools every server); the split here is prestige, not which server.
    cyc = cycle_points(sid)                                    # prestige-aware: rank tier from CYCLE points
    pts = player_points(sid)                                   # lifetime fleet total: what we display
    pn = prestige_count(sid)
    label, color, tail = rank_progress(cyc, pn)
    # Built once up here because BOTH branches below need them: the testing notice still goes out on
    # its own when the welcome itself is switched off, exactly as it did when it was a separate call.
    public = bool(_sysmsg_rec("welcome").get("public", False))
    tnote = sysmsg_text("testing", _SYSMSG_TESTING_DEFAULT) if sysmsg_on("testing") else ""
    if sysmsg_on("welcome"):                                   # owner can disable the join message (webcc Messages tab)
        custom = sysmsg_text("welcome", "")
        if custom:                                            # custom text REPLACES the default; {name}{rank}{pts}{star}
            # {star} kept for old custom texts but always empty — prestige is in {rank}
            line = (custom.replace("{name}", name).replace("{rank}", label)
                    .replace("{pts}", _pts(pts)).replace("{star}", ""))
        else:
            _lspan = f"<color={color}>{label}</color> " if label else ""   # ladder off -> no rank in the welcome
            line = (f"<color=#36FFD0>Welcome</color> {_lspan}"
                    f"<color=#FFFFFF>{name}</color>  -  {_pts(pts)}")
            if rank_index_for(cyc) == 0:  # OFFCDT (lowest TIER, so cyc not pts) -> nudge them to !help
                line += "  -  <color=#FFD200>type !help for commands</color>"
        # PRIVATE (owner, 2026-07-30): "hide our welcome message so it only says welcome to that
        # individual player and not to everyone on the server". rc.say() is all-chat; tell_player() is
        # the plugin's per-player 'tell' verb, the same channel !spec confirmations use. Set the sysmsg
        # 'welcome' record's "public" field to true to broadcast it again.
        # The optional "server is testing" notice, directly under the welcome. Built BEFORE the
        # send so the whole greeting leaves as one message.
        extra = [x for x in (tnote,) if x]
        if public:
            # ONE all-chat message, not one per line. What overflows a client's reliable-send buffer
            # here is the NUMBER of messages, not their length, and a join wave already sends one per
            # player - so the greeting must never multiply the count. U+2028 is a tell-channel trick,
            # unverified over RCON say, hence the house "  -  " separator instead.
            rc.say("  -  ".join([line] + extra))
        else:
            # ONE tell carrying EVERY line. tell_player is variadic and U+2028-joins its arguments into
            # a single client message, so the whole greeting is one blocking relay round trip (the
            # drain caps players per pass, NOT relay calls) and it is atomic: no line can land without
            # the others, or arrive as a second message the client then fails to render. Separate calls
            # would also break the retry contract - a failure AFTER the welcome had gone would re-send
            # the welcome, up to 3 duplicates 30s apart. (This folds in the 'testing' notice, which was
            # its own best-effort call below and quietly doubled the round trips per join.)
            if not tell_player(sid, line, *extra):
                return False          # relay down - leave un-WELCOMED so the drain can retry
    elif tnote:
        # Welcome switched OFF but the testing notice left ON: it still goes out by itself, as it did
        # when it lived outside this block. Best-effort - there is no welcome for the drain to retry
        # alongside, and burning the 3 welcome attempts on a standalone notice would be wrong.
        if public:
            rc.say(tnote)
        else:
            tell_player(sid, tnote)
    WELCOMED.add(sid)          # greeted for real - safe to dedup from here on
    return True
    # (full help is on-demand via !help now - don't dump 9 lines to all-chat on every join)
    # (the [JOIN] feed line is emitted by the roster poll - see log_join_line - so a quick
    #  join/leave, a suppressed welcome or an unresolved name can never lose it)


def _record_killer(vid, weapon="", munition=""):
    """Remember what downed victim `vid` (from a kill/down event) so the [KILL] activity line can
    name the ordnance (the death `life` event and the kill/down event can arrive in either order).

    `weapon` stays the DAMAGING UNIT (the aeroplane) so every existing reader keeps the value it has
    always had; `munition` is the newly-read weapon name, added alongside rather than replacing it.
    Both are kept because the pair is what makes an honest line possible - see describe_kill_weapon."""
    now = time.time()
    _recent_kill[vid] = {"weapon": weapon, "munition": munition, "ts": now}
    if len(_recent_kill) > 128:                   # prune (same pattern as _splash_dedup): every read
        for _os, _ov in list(_recent_kill.items()):   # is 8s-freshness-gated, so >60s entries are
            if now - float(_ov.get("ts") or 0) > 60:  # pure memory creep over weeks of uptime
                _recent_kill.pop(_os, None)


# ── KILL / splash: the in-game killfeed is the game's own (native RpcKillMessage; the plugin
# never suppresses it). Bot: activity [KILL] only — never rc.say splash.


def _render_template(tmpl, **ph):
    """Fill {placeholder} tokens in a template (rankfunds etc.), then sanitise."""
    out = str(tmpl)
    for k, v in ph.items():
        out = out.replace("{" + k + "}", str(v))
    return _msg_sanitize_text(out)


def handle_stats_line(rc, obj):
    """Ingest one [NOSTATS] object from the NukeStats plugin.
      snap/score -> cache the player's meta + live score (feed/display only)
      win        -> authoritative winner: announce + tally W/L (replaces faction-0 guess)
      award      -> apply the plugin's match-end points (+win / +placement) to ranks.json
      end        -> match boundary: clear the per-match caches
      ailimit    -> mute USE_PLUGIN_SCORE banking briefly after AI culls
    Inert unless the plugin is actually emitting these lines."""
    global _AILIMIT_SCORE_MUTE_UNTIL, _LAST_NOSTATS_AT
    _LAST_NOSTATS_AT = time.time()
    if not isinstance(obj, dict):
        return
    t = obj.get("t")
    if t == "ailimit":
        # Plugin cleared N AI via DisableUnit (credit wiped). Still mute score banking
        # briefly so any residual PlayerScore climb is not banked into ranks.
        try:
            n = int(obj.get("n") or 0)
        except (TypeError, ValueError):
            n = 0
        _AILIMIT_SCORE_MUTE_UNTIL = time.time() + 8.0
        if n > 0:
            print(f"[ailimit] cull n={n}; ranks score-banking muted 8s")
        return
    if t == "faction_colours":
        apply_faction_colours(obj)
        return
    if t == "chat":
        # The plugin reroutes reformatted chat, which suppresses the normal
        # CmdSendChatMessage log line -> the bot can't see those messages. The plugin
        # re-reports each broadcast message here so it still lands in the activity feed.
        # (Commands/votes aren't rerouted, so they keep coming via the normal parse -
        # no double logging.)
        sid = str(obj.get("id") or "")
        if obj.get("n") and sid:
            _g = _storable_name(sid, obj["n"])
            if _g:
                PLAYER_NAMES[sid] = _g
            else:
                maybe_fetch_persona(sid)
        _CHAT_FRAME_SEEN[0] = True      # plugin emits chat itself now -> retire the log tail
        msg = (obj.get("msg") or "").strip()
        if LOG_CONVERSATION and msg and not chat_seen_recently(sid, msg):
            name = display_name(sid, obj.get("n"))
            ally = "" if obj.get("all", True) else "(ally) "
            activity(f"{ally}{name}: {msg}", "CHAT")
        return
    if t == "cfg":
        # webcc settings menu: the plugin's current live config values (one snapshot dict).
        global PLUGIN_CFG, PLUGIN_CFG_TS
        v = obj.get("v")
        if isinstance(v, dict):
            PLUGIN_CFG = {str(k): v[k] for k in v}
            PLUGIN_CFG_TS = time.time()
            save_plugin_cfg_cache()               # persist so a bot restart never shows defaults (Tomo 2026-07-05)
        return
    if t == "report":
        # anti-grief: the plugin auto-kicked/flagged a single connection. Sources since NukeStats 1.2.4:
        # dead-unit commands (the order-a-dead-unit exploit), RPC flood, send-buffer overflow, teamkill.
        # The old "unit-flood" order-rate source is GONE — the plugin no longer rate-limits move orders.
        rid = str(obj.get("id") or "")
        nm = str(obj.get("n") or PLAYER_NAMES.get(rid) or (RANK_DATA.get(rid, {}).get("name") if rid else "") or "?")
        rec = {"id": rid, "name": nm, "reason": str(obj.get("reason") or "?"),
               "count": _sanei(obj.get("count")), "rate": _sanei(obj.get("rate")),
               "action": str(obj.get("action") or "report"),
               # kill detail (teamkill-class reports): the damaging unit/weapon name + how it was
               # delivered ("direct" | "splash" | "auto"); flood reports leave both "" -> webcc Method column
               "weapon": str(obj.get("weapon") or ""), "method": str(obj.get("method") or ""),
               # the MUNITION, resolved plugin-side from the launch tracker. Separate from "weapon"
               # (the damaging UNIT) so an auto-defence row keeps naming the SAM that fired.
               "munition": str(obj.get("munition") or ""),
               "ts": time.time()}          # plugin sends ts:0 -> stamp the real time on ingest
        rec["banned"] = (rec["action"] == "ban")
        add_report(rec)
        # Use the plugin's own reason string. The old hard-coded "unit-flood (owned N, R/s)" wording was
        # layer A's, and layer A no longer exists — it would have announced every dead-unit exploit kick,
        # RPC flood and teamkill as a unit-flood, with A's field meanings attached to another guard's numbers.
        activity(f"AUTO-{rec['action'].upper()}: {nm} - {rec['reason']}", "!")
        # KickPlayer leaves a session kick that blocks rejoin until restart/unkick-player.
        # Kick-only flood (action=kick / rejoin=true) must NOT be a lasting ban — auto-unkick.
        # HardBan reports action=ban + rejoin=false — leave the session kick + plugin_bans in place.
        want_rejoin = bool(obj.get("rejoin")) if "rejoin" in obj else (rec["action"] == "kick")
        if want_rejoin and rid and re.fullmatch(r"\d{6,20}", rid):
            _queue_session_unkick(rid, nm)
        return
    if t == "limbo":
        # LIMBO watch retired 2026-08-15 (owner: "not needed") - the bot no longer logs these.
        # The 1.2.1 rejoin-limbo watchdog itself lives PLUGIN-side (NukeStatsPlugin.cs): it still
        # emits these frames and still auto-releases wedged sessions with a clean transport close.
        # The frame MUST still be swallowed here: it carries "id"/"n", so falling through would
        # reach the snap/score ingest below and clobber that player's STATS_META with blanks.
        return
    if t == "forfeit":
        # 1.2.5: forfeit (surrender) votes. These were invisible to the feed until now - the plugin
        # only whispered the forfeiting team and wrote a BepInEx log line the bot does not tail.
        # In game the vote is kept from the enemy on purpose; the operator's log shows everything.
        # Tagged MATCH, not VOTE: the VOTE tag routes to the "Ranks & map" tab (map votes live there),
        # which would split a forfeit vote from its own outcome across two tabs.
        sid = str(obj.get("id") or "")
        nm = display_name(sid, obj.get("n"))
        fac = str(obj.get("f") or "their team")
        yes, need = _sanei(obj.get("yes")), _sanei(obj.get("need"))
        if obj.get("passed"):
            foe = str(obj.get("foe") or "the other side")
            activity(f"FORFEIT: {fac} voted to surrender ({yes}/{need}) - {foe} wins the match", "MATCH")
        elif obj.get("started"):
            activity(f"FORFEIT VOTE: {nm} called a surrender vote for {fac} - {yes}/{need} needed", "MATCH")
        else:
            activity(f"FORFEIT VOTE: {nm} voted to surrender ({fac}) - now {yes}/{need}", "MATCH")
        return
    if t == "recon":
        # 1.2.3: radar spotting pays vanilla again, guarded by a PER-PLAYER breaker. This frame
        # means one player's spotting earnings went abnormal and THAT player stops earning recon
        # score+funds for the rest of the match. It is not a punishment and nobody is kicked, so it
        # is never announced in chat - naming a player publicly for what may be a game bug would be
        # unfair. Operator-visible only.
        sid = str(obj.get("id") or "")
        nm = display_name(sid, obj.get("n"))
        win = _sanei(obj.get("win"))
        total = _sanei(obj.get("total"))
        activity(f"SPOTTING BREAKER: {nm} stopped earning radar-spotting points for the rest of this "
                 f"match - {win} in the last minute, {total} this match (everyone else is unaffected; "
                 f"raise Recon.RatePerWindow if this was a genuine player)", "!")
        return
    if t == "errkick":
        # 1.2.1 guard F: the GAME error-kicked a player (post-07-27-update error flags, e.g.
        # InvalidTransformSnapshot after a death). lifted=True = the plugin cleared the
        # TimeoutManager rejoin lockout, so the player can reconnect immediately.
        sid = str(obj.get("id") or "")
        nm = display_name(sid, obj.get("n"))
        fl = str(obj.get("flags") or "?")
        secs = _sanei(obj.get("secs"))
        if obj.get("ban"):
            activity(f"ERROR-KICK ESCALATION: {nm} hit the game's error auto-BAN path (flags {fl}) - "
                     f"check ban_list and unban if legitimate!", "!")
        elif obj.get("lifted"):
            activity(f"ERROR-KICK: {nm} error-kicked by the game (flags {fl}); {secs}s rejoin lockout "
                     f"LIFTED by plugin - can rejoin at once", "!")
        else:
            activity(f"ERROR-KICK: {nm} error-kicked by the game (flags {fl}); {secs}s rejoin lockout "
                     f"ACTIVE - rejoins silently refused until it expires", "!")
        return
    if t == "joinblock":
        # 1.2.1 guard F: a join attempt was SILENTLY refused by an active game timeout (the
        # client only sees "Local client stopped"). Rate-limited plugin-side (~30s per sid).
        sid = str(obj.get("id") or "")
        nm = display_name(sid, obj.get("n"))
        activity(f"JOIN REFUSED: {nm} blocked by an active game timeout, ~{_sanei(obj.get('secs'))}s left "
                 f"(client shows 'Local client stopped')", "!")
        return
    if t == "tk":
        # teamkill enforcement escalation (warn = eject / kick / ban) -> moderation log + the webcc Moderation tab.
        # Records WHAT caused it: the teammate killed + the offense number.
        rid = str(obj.get("id") or "")
        nm = str(obj.get("n") or PLAYER_NAMES.get(rid) or (RANK_DATA.get(rid, {}).get("name") if rid else "") or "?")
        victim = str(obj.get("victim") or "a teammate")
        method = str(obj.get("method") or "")        # delivery tag: "direct" | "splash" | "auto" | "" (unknown/none recorded)
        weapon = str(obj.get("weapon") or "")        # the damaging unit's name (aircraft/SAM/launcher); "" when the game recorded no weapon (often an environmental/collision death)
        # 1.3.28: the plugin resolves the MUNITION separately and sends it alongside. Without this
        # read the teamkill rows silently fall back to the unit name - which is the whole reason
        # the feed said "Alkyon AB-4" where a bomb name belonged.
        munition = str(obj.get("munition") or "")
        nc = str(obj.get("nc") or "")                # not-counted reason ("auto"/"no-weapon"/"below-floor"/"collateral"/"big-unit"); set only on report-only (action=="report") events

        # _sanef/_sanei everywhere: a NaN/inf would PERSIST into reports.json and brick the webcc's
        # strict JSON.parse on every poll; a raising int()/float() would drop the whole tk record
        # (a warn/kick/BAN event) via the outer broad-except. Never either (audit rounds 1+2).
        dmg = _sanef(obj.get("dmg"))                 # the killer's credited damage to the victim (webcc detail shows it)
        count = _sanei(obj.get("count"))
        action = str(obj.get("action") or "warn")
        if action not in ("warn", "kick", "ban", "report"):
            action = "warn"
        ordn = {1: "1st", 2: "2nd", 3: "3rd"}.get(count, f"{count}th")
        if action == "report":
            # REPORT-ONLY: the plugin flagged a friendly kill it did NOT trust enough to punish (auto-defence,
            # no weapon recorded / environmental, or below the min-damage floor). Shown in Moderation as a
            # flagged 'not counted' entry — never a warn/kick/ban.
            reason = f"flagged: friendly kill on {victim} — not counted"
        else:
            reason = f"team-killed {victim} ({ordn} offense)"
        # victim + weapon + nc are stored as their OWN fields (not just baked into `reason`) so the webcc
        # Moderation detail view can show the full breakdown — who was killed, how, with what, and (if not
        # counted) WHY it wasn't punished. ts: the plugin back-dates it to the OFFENCE moment (the collateral
        # verdict + tail lag add seconds); only stamp ingest time for old plugins that still send ts<=0.
        pts = _sanef(obj.get("ts"))                  # finite-gated: ts:Infinity passed >1e9 and bricked the webcc (audit r2)
        rec = {"id": rid, "name": nm, "reason": reason, "victim": victim, "method": method, "weapon": weapon,
               "munition": munition,
               "nc": nc, "dmg": dmg, "count": count, "rate": 0, "action": action,
               "ts": (pts if pts > 1e9 else time.time()), "banned": (action == "ban")}
        # 0.9.43 (Tomo's spec): the plugin now lists every unit that died in the same blast window
        # [{n: name, f: 'e'|'f', d: dmg}, ...] so the Moderation detail can show the full picture.
        # Old plugins simply don't send it; old bots ignored it — both directions stay compatible.
        units = obj.get("units")
        if isinstance(units, list) and units:
            rec["units"] = [{"n": str(u.get("n") or "?"), "f": str(u.get("f") or "?"), "d": _sanef(u.get("d"))}
                            for u in units if isinstance(u, dict)][:24]
            more = _sanei(obj.get("unitsMore"))
            if more > 0:
                rec["unitsMore"] = more
        add_report(rec)
        if action == "report":
            why = {"auto": "auto-defence", "no-weapon": "no weapon recorded", "below-floor": "below lethal-damage floor",
                   "collateral": "collateral - strike killed more enemies", "big-unit": "collateral - killed a major enemy unit"}.get(nc, nc or "not counted")
            cause = describe_tk_cause(rec)
            activity(f"TEAMKILL FLAGGED (not counted - {why}): {nm} - friendly kill on {victim}"
                     + (f" [{cause}]" if cause else ""), "!")
        else:
            verb = {"warn": "warned + ejected", "kick": "kicked", "ban": "BANNED"}[action]
            cause = describe_tk_cause(rec)
            activity(f"TEAMKILL - {nm} {verb}: team-killed {victim}"
                     + (f" - {cause}" if cause else "") + f" ({ordn} offense)", "!")
            # KickPlayer session-blocks rejoin until unkick-player. TK 2nd offense is a kick with
            # rank-0-on-rejoin — they must be able to rejoin. Ban keeps the session block + plugin_bans.
            if action == "kick" and rid and re.fullmatch(r"\d{6,20}", rid):
                _queue_session_unkick(rid, nm)
        return
    if t == "pos":
        # live map: fast position update for Occupied (!disabled) players.
        # DOWNED/✝ clears ONLY on a far jump from DEATH_POS (≥_DOWNED_NEAR_M) after lockout.
        # Never time-expire (15s/12s gates caused false-alive at wreck). Near-wreck POS ignored
        # forever while DOWNED (plugin _mapDead should suppress wreck; belt-and-braces here).
        # EnrichPos g=landed is NOT death. Stale POS alone is NOT death.
        # Prefer plugin emit-time unix ts (1.0.30+) over ingest wall-clock, so a bot-loop
        # hitch cannot stamp several ~0.5s POS frames with nearly the same time.
        # EnrichPos (plugin ≥1.1.3): optional y/ac/g — ac feeds airframe lookups.
        ts = time.time()
        try:
            pts = float(obj.get("ts"))
            if math.isfinite(pts) and pts > 1e9:
                ts = pts
        except (TypeError, ValueError):
            pass
        seen_pos = set()
        for pp in obj.get("p", []):
            psid = str(pp.get("id") or "")
            if not psid:
                continue
            seen_pos.add(psid)
            px, pz = pp.get("x"), pp.get("z")
            old = POS.get(psid)
            if psid in DOWNED:
                death_ts = DOWNED[psid]
                # Brief lockout so ✝ sticks on the death frame before any wreck/respawn POS.
                if ts - death_ts < _DOWNED_LOCKOUT_S:
                    continue
                # Anchor = death spot (preferred) or last POS before/at death.
                ax = az = None
                _dp = DEATH_POS.get(psid)
                if _dp:
                    ax, az = _dp[0], _dp[1]
                elif old and old[0] is not None and old[1] is not None:
                    try:
                        ax, az = float(old[0]), float(old[1])
                    except (TypeError, ValueError):
                        ax = az = None
                if ax is not None and az is not None and px is not None and pz is not None:
                    try:
                        dx = float(px) - float(ax); dz = float(pz) - float(az)
                        if (dx * dx + dz * dz) < (_DOWNED_NEAR_M * _DOWNED_NEAR_M):
                            continue  # still at/near wreck — keep ✝, freeze POS
                    except (TypeError, ValueError):
                        continue
                _clear_map_downed(psid)                                    # far jump = new sortie
                _pos_trail_clear(psid)                                     # fresh trail for new life
            _h = pp.get("h")
            try:
                _h = int(_h) if _h is not None else None
            except (TypeError, ValueError):
                _h = None
            kind = pp.get("k")
            # EnrichPos extras (additive — absent on old DLL)
            _py = pp.get("y")
            try:
                _py = float(_py) if _py is not None else None
                if _py is not None and not math.isfinite(_py):
                    _py = None
            except (TypeError, ValueError):
                _py = None
            _ac = str(pp.get("ac") or "").strip() or None
            _g = pp.get("g")
            try:
                _g = int(_g) if _g is not None else None
            except (TypeError, ValueError):
                _g = None
            # (x,z,ts,k,h,y,ac,g) — indices 0–4 unchanged for dashboard/map consumers
            POS[psid] = (px, pz, ts, kind, _h, _py, _ac, _g)
            _pos_trail_push(psid, ts, px, pz, _h)
        # Not in this Occupied tick + not DOWNED → left aircraft safely / spectating → drop
        # track so the map HIDES the blip (no lingering alive icon, no false ✝).
        for _sid in list(POS.keys()):
            if _sid not in seen_pos and _sid not in DOWNED:
                POS.pop(_sid, None)
                _pos_trail_clear(_sid)
        return
    if t == "air":
        # AI aircraft limiter telemetry: per-side AI/player aircraft counts + caps (perf panel).
        global AIR, AIR_TS
        AIR = {"s": obj.get("s", []), "ai": obj.get("ai", 0), "pl": obj.get("pl", 0),
               "teamcap": obj.get("teamcap"), "totcap": obj.get("totcap")}
        AIR_TS = time.time()
        return
    if t == "net":
        # connection-health / RTT-probe telemetry (Connection Stress panel); payload shape is plugin-defined.
        global NET, NET_TS, LAST_FRAMETIME_MS, PLAYER_RTT_MS
        NET = {k: v for k, v in obj.items() if k != "t"}
        NET_TS = time.time()
        # Per-player RTT (Steam m_nPing / Notify fallback) from net.p[].rtt_ms → dashboard players[].rtt_ms
        try:
            _plist = obj.get("p") or []
            _seen = set()
            if isinstance(_plist, list):
                for _pe in _plist:
                    if not isinstance(_pe, dict):
                        continue
                    _pid = str(_pe.get("id") or "").strip()
                    if not _pid or _pid == "0":
                        continue
                    _seen.add(_pid)
                    _rv = _pe.get("rtt_ms")
                    if _rv is None:
                        continue
                    try:
                        PLAYER_RTT_MS[_pid] = int(round(float(_rv)))
                    except (TypeError, ValueError):
                        pass
            # Drop RTT for sids no longer in this net frame (left / not probed)
            if _seen:
                for _old in list(PLAYER_RTT_MS.keys()):
                    if _old not in _seen:
                        PLAYER_RTT_MS.pop(_old, None)
        except Exception:
            pass
        _ft = obj.get("frametime_ms")   # smoothed server frametime (ms) from plugin 0.9.47; keep last known if absent
        if _ft is not None:
            LAST_FRAMETIME_MS = _ft
        return
    if t == "ent":
        # live map: per-AI-aircraft + per-ship world positions (each carries a per-unit instance id "i"
        # for client-side interpolation; no SteamID -> rendered without a name label).
        global ENT, ENT_TS
        ENT = {"a": obj.get("a", []), "s": obj.get("s", [])}
        ENT_TS = time.time()
        return
    if t == "life":
        # Life-END event from the plugin: emitted ONLY when the pilot DIES ("death") or EJECTS
        # mid-air ("eject"); ground dismounts and balance/admin moves do NOT end a life. Consumed
        # here purely for the live map's death cross (the eject case has no "down" frame).
        sid = str(obj.get("id") or "")
        if not sid or sid == "0":
            return
        reason = str(obj.get("r") or "death")
        counted = reason in ("death", "eject")    # the ONLY life-ending reasons (legacy match/exit/dc ignored)
        # live map: mark them DOWN now (so the map shows dead instantly).
        if counted:
            _now = time.time()
            # REPLAY GUARD (mirrors the kill branch's (killer,victim) dedupe): a real pilot cannot
            # die twice within 5s (respawn screen + re-entry), so a counted life this fresh is the
            # same death seen again.
            if _now - float(_life_dedup.get(sid) or 0) < 5.0:
                return
            _life_dedup[sid] = _now
            if len(_life_dedup) > 128:
                for _ds, _dt in list(_life_dedup.items()):
                    if _now - float(_dt) > 60:
                        _life_dedup.pop(_ds, None)
            _mark_map_downed(sid, _now)       # sticky ✝ until far-jump respawn
        return
    if t == "rankfunds":
        # RANK CATCH-UP + ACCUMULATIVE FUNDS (plugin-granted): the PLUGIN raises the in-game rank floor and
        # grants in-game money, emitting this event per grant. The bot ONLY surfaces the announce (it never
        # grants funds here). Customizable/suppressible via the "rankfunds" message (webcc Messages tab).
        sid = str(obj.get("id") or "")
        nm = display_name(sid, obj.get("n"))
        try:
            # plugin emits "amt"; accept "funds" too (either key is Allocation millions)
            _raw = obj.get("funds", obj.get("amt", 0))
            funds = int(round(float(_raw)))
        except (TypeError, ValueError):
            funds = 0
        rank_n = _sanei(obj.get("rank"))
        # Hold it: the name usually lands within a second (the plugin's own placeholder is
        # what reaches us first). pump_funds_announces re-resolves and emits.
        if _storable_name(sid, obj.get("n")) or _storable_name(sid, PLAYER_NAMES.get(sid)):
            funds_str = f"{funds:,}"
            activity(f"{nm}: +{funds_str} funds for reaching rank {rank_n}", "RANK")
            if sysmsg_on("rankfunds"):
                tmpl = sysmsg_text("rankfunds", _SYSMSG_RANKFUNDS_DEFAULT)
                rc.say(_render_template(tmpl, funds=funds_str, rank=rank_n, name=nm))   # reuse the {ph} filler + sanitiser
        else:
            maybe_fetch_persona(sid)
            queue_funds_announce(sid, funds, rank_n)
        return
    if t == "kill":
        # PvP kill: activity log. Does NOT touch in-game PlayerScore — that is vanilla
        # FactionHQ.RewardPlayer only (plugin read-only). In-game killfeed = the game's own.
        # Plugin 1.0.14+ emits only the top-damager kill (assists filtered). Bot dedupes by
        # (killer,victim) ~10s so console re-read cannot double-log the same death.
        kid = str(obj.get("kid") or "")
        vid = str(obj.get("vid") or "")
        kn = display_name(kid, obj.get("kn"))
        vn = display_name(vid, obj.get("vn"))
        if obj.get("kn") and kid:
            _g = _storable_name(kid, obj["kn"])
            if _g:
                PLAYER_NAMES[kid] = _g
        if obj.get("vn") and vid:
            _g = _storable_name(vid, obj["vn"])
            if _g:
                PLAYER_NAMES[vid] = _g
        # KILL DATA (shared contract): the plugin supplies human aircraft/weapon names on the kill event.
        kplane = str(obj.get("killer_plane") or obj.get("ka")
                     or STATS_META.get(kid, {}).get("aircraft") or "")
        vplane = str(obj.get("victim_plane") or obj.get("va")
                     or STATS_META.get(vid, {}).get("aircraft") or "")
        # The KILL frame carries NEITHER weapon field - the plugin sends only the two aircraft on it
        # (kid/kn/kc/vid/vn/vc/killer_plane/victim_plane/ka/va). Reading obj["w"]/obj["weapon"] here
        # yielded "" every time, so the weapon bracket never printed. The DOWN frame does carry them,
        # and it arrives first (Unit.ReportKilled runs the plugin's prefix before HQ.ReportKillAction),
        # so _recent_kill already holds the pair by the time we get here.
        _now = time.time()
        # FRESHNESS GATE, same 8s window the life handler uses on this map. _recent_kill is only
        # pruned lazily (>60s entries), so an ungated read would hand back the weapon from this victim's PREVIOUS death
        # whenever no down frame accompanied this one - a confidently wrong ordnance name on the very
        # line this change exists to make honest. Stale or absent -> print no weapon at all.
        _rkw = _recent_kill.get(vid) or {}
        if not _rkw or _now - float(_rkw.get("ts") or 0) >= 8:
            _rkw = {}
        weapon = str(_rkw.get("weapon") or "")      # the aeroplane / SAM / ship
        munition = str(_rkw.get("munition") or "")  # the ordnance, when the launch matched
        if vid:
            # Dedupe by (killer,victim): assists + console replay can re-emit the same death.
            _dk = (kid or "", vid)
            prev_ts = float(_splash_dedup.get(_dk) or _splash_dedup.get(vid) or 0)
            if prev_ts and (_now - prev_ts) < 10.0:
                return                               # same death already handled this window
            _splash_dedup[_dk] = _now
            _splash_dedup[vid] = _now                # keep vid key so older assist path still collapses
            if len(_splash_dedup) > 128:
                for sid, ts in list(_splash_dedup.items()):
                    if _now - float(ts) > 60:
                        _splash_dedup.pop(sid, None)
            _record_killer(vid, weapon=weapon, munition=munition)   # re-asserts what `down` set
        # In-game killfeed is NOT bot-owned (native always). Log only.
        # The airframe is already on the line as "(FS-20 vs A-19)". When no munition resolved,
        # describe_kill_weapon can only repeat it in longhand and then deny knowing the weapon, so
        # the bracket is pure noise - drop it and keep the line readable. (audit 10)
        _kw = describe_kill_weapon(munition, weapon) if (weapon or munition) else ""
        if kplane and _kw.endswith("weapon not identified"):
            _kw = ""
        activity(f"{kn} splashed {vn}"
                 + (f"  ({kplane} vs {vplane})" if (kplane or vplane) else "")
                 + (f"  [{_kw}]" if _kw else ""), "KILL")
        return
    if t == "down":
        # kill-data enrichment (plugin v0.9.0+): who/what shot a player down, incl AI/SAM unit names.
        # Sticky map ✝ immediately (life may arrive slightly later) — not a safe dismount.
        vid = str(obj.get("v") or "")
        if not vid:
            return
        _mark_map_downed(vid)
        # TWO fields, both already on the wire:
        #   "w"      -> the damaging UNIT: the aeroplane. Always populated.
        #   "weapon" -> killWeapon: the MUNITION when the plugin matched a recent launch by this
        #               killer, otherwise a copy of the aeroplane name.
        plane = str(obj.get("w") or "")
        munition = str(obj.get("weapon") or "")
        _record_killer(vid, plane, munition)
        return
    if t == "win":
        handle_plugin_win(rc, obj.get("f") or "")
        return
    if t == "draw":
        # Plugin ForceDraw fallback (no Defeat crown): announce-only / rotation handles end.
        activity("Match ended in a DRAW (plugin)", "MATCH")
        return
    if t == "award":
        if not USE_PLUGIN_SCORE:
            return
        if not award_on("win_points"):            # VANILLA toggle: win/placement points off -> don't bank/announce
            return
        sid = str(obj.get("id") or "")
        if not sid or sid == "0":
            return
        try:
            _raw = float(obj.get("pts", 0))
        except (TypeError, ValueError):
            return
        if not math.isfinite(_raw):               # 'inf'/'nan' pass float() but corrupt ranks.json (inf also -> OverflowError at round)
            return
        pts = int(round(_raw))
        if pts == 0:
            return
        name = obj.get("n") or STATS_META.get(sid, {}).get("name") or sid
        old_idx, new_idx, total = award_points(sid, name, pts)
        save_ranks()
        _ar = (obj.get("reason") or "").strip().lower()
        _cat = {"1st": "place_1st", "2nd": "place_2nd", "3rd": "place_3rd", "win": "win"}.get(_ar, "win")
        ledger_award(sid, name, pts, _cat, f"{_cat}: {obj.get('reason', '')}",
                     total, match=CUR_MATCH["match_id"] if CUR_MATCH else None)
        activity(f"{name}  +{pts}  ({obj.get('reason', '')})", "RANK")
        _buffer_eom_award(sid, name, _ar, pts)    # coalesce public chat; flushed on `end`
        crossed, old_ann, ann_idx = combined_rankup(sid, total, pts)   # #4: combined-rank crossing
        if crossed:
            _, rname, abbr, color = RANKS[ann_idx]
            announce_rankup(rc, sid, name, ann_idx, old_ann)
            activity(f"{name} promoted to {rname} ({abbr})!", "RANK")
            _RANK_PUSH_FLAG[0] = True   # coalesced push at end of loop (was inline SSH)
        return
    if t == "end":
        # Public Awards line (names-only, ladder colours) before we drop STATS_META.
        try:
            flush_eom_award_announce(rc)
        except Exception as e:  # noqa: BLE001 - never let announce break match teardown
            print(f"[awards] chat flush error: {e}")
            _clear_eom_award_buf()
        # Deliberately DO NOT reset each player's "ms" baseline here. The game keeps
        # PlayerScore non-zero through the post-mission delay, so snapshots keep arriving
        # with the final score for ~80s after "end". If we zeroed the baseline now, the
        # very next such snapshot (s == final, prev == 0) would re-credit the whole match
        # score -> every player's match earnings double-counted once. Leaving "ms" at the
        # final score makes those lingering snaps a no-op (s == prev), and the new match's
        # score reset (s < prev) trips the existing decrease-rebaseline path cleanly.
        STATS_META.clear()
        LIVE_SCORE.clear()
        save_ranks()
        return
    # snap / score: cache meta, and accumulate the player's REAL in-game score into their
    # lifetime points. "ms" is the last in-match score we credited; we add the increase
    # since then. It's stored in the record (restart-safe) and reset to 0 at match end,
    # so points == the player's total accumulated score across matches.
    sid = str(obj.get("id") or "")
    if not sid or sid == "0":
        return
    name = obj.get("n") or STATS_META.get(sid, {}).get("name") or sid
    STATS_META[sid] = {"name": _storable_name(sid, name) or PLAYER_NAMES.get(sid) or sid,
                       "faction": obj.get("f") or "",
                       "rank": obj.get("rk"), "teamkills": obj.get("tk"),
                       "aircraft": obj.get("ac") or "", "t": time.time()}
    _g = _storable_name(sid, name)
    if _g:
        PLAYER_NAMES[sid] = _g
    else:
        maybe_fetch_persona(sid)
    try:
        s = float(obj.get("s", 0))
    except (TypeError, ValueError):
        return
    if not math.isfinite(s):                       # reject 'inf'/'nan' before it poisons ms/points (ranks.json corruption)
        return
    LIVE_SCORE[sid] = s
    if not USE_PLUGIN_SCORE:
        return
    rec = RANK_DATA.get(sid)
    _obs_now = time.time()
    if rec is None or "ms" not in rec:
        # First time we've seen this player's in-match score this session: adopt it as the
        # baseline and credit NOTHING (they accrue from their NEXT increase). Without this,
        # a record made by ensure_player (which has no "ms") would give prev=0 and one-shot
        # credit the player's ENTIRE accumulated in-match score as lifetime points.
        r0 = RANK_DATA.setdefault(sid, {"name": _storable_name(sid, name) or sid, "points": 0})
        r0["ms"] = s
        r0["ms_t"] = _obs_now     # observation clock: the dt-scaled clamp measures frame spacing, so
        return                    # EVERY ms observation (adopt/gain/equal/rebaseline) must refresh it
    prev = rec["ms"]
    _prev_t = float(rec.get("ms_t") or _obs_now)
    rec["ms_t"] = _obs_now
    if s > prev:                                   # gained score -> credit the increase
        gain = s - prev
        # AiLimit cull window: adopt baseline, do NOT bank (amplifies limiter PlayerScore bugs).
        if _obs_now < float(_AILIMIT_SCORE_MUTE_UNTIL or 0):
            RANK_DATA[sid]["ms"] = s
            return
        # 0.9.43: the plugin now COALESCES score frames (leading-edge immediate + 1Hz trailing flush),
        # so one frame aggregates AT MOST ~2s of legit scoring - scale the clamp + spike tripwire by
        # time since the player's PREVIOUS frame (any observation, not just gains) CAPPED AT 2.0, so
        # aggregation never burns real points while a paced injection (audit fix: a 10x cap let +10k
        # every 10s bank alert-free) still clamps + trips at ~1-2x the classic thresholds.
        _dt = max(1.0, min(_obs_now - _prev_t, 2.0))
        award = min(gain, GAIN_CLAMP_MAX * _dt)    # clamp what we BANK; the raw gain still drives the spike alert below
        old_idx, new_idx, _new_pts = award_points(sid, name, award)
        RANK_DATA[sid]["ms"] = s
        # Audit: accumulate this match's (clamped) score for ONE ledger line at finalize (snaps are ~1/s).
        _acc = SCORE_ACCUM.setdefault(sid, [name, 0.0]); _acc[0] = name; _acc[1] = round(_acc[1] + award, 1)
        # Exploit tripwire: a single snap jump this large is abnormal (cf. 2026-06-24). Flag it
        # live + in the ledger (pts:0 -> audit-neutral; the real award is the "score" aggregate).
        if gain > SPIKE_THRESHOLD * _dt:
            activity(f"!! SCORE SPIKE: {name} +{gain:g} in one tick (check for exploit)", "!")
            ledger_award(sid, name, 0, "score-spike", f"single-tick gain +{gain:g} (>{SPIKE_THRESHOLD * _dt:g})",
                         RANK_DATA[sid].get("points", 0), match=CUR_MATCH["match_id"] if CUR_MATCH else None)
        _maybe_save_ranks()
        crossed, old_ann, ann_idx = combined_rankup(sid, _new_pts, award)   # #4: combined-rank crossing
        if crossed:
            _, rname, abbr, color = RANKS[ann_idx]
            announce_rankup(rc, sid, name, ann_idx, old_ann)
            activity(f"{name} promoted to {rname} ({abbr})!", "RANK")
            save_ranks()
            _RANK_PUSH_FLAG[0] = True   # coalesced push at end of loop (was inline SSH)
    elif rec is not None and s < prev:             # score reset/decreased -> rebaseline, no credit
        rec["ms"] = s


# End-of-match award chat buffer (plugin score path). Plugin emits one award frame per
# winner + up to 3 place frames, then `end`. We bank points per-frame (unchanged) but
# coalesce the PUBLIC chat into one names-only line in each player's ladder rank colour.
# Timeout / Annihilate plugin BroadcastAll lines are intentionally untouched.
_EOM_AWARD_BUF = {"wins": [], "places": {}, "ts": 0.0}  # wins: [(sid,name)]; places: {1st|2nd|3rd: (sid,name)}


# How long a plugin 'win' frame may still name the winning side. Awards follow their win frame within
# ~1s, so this is generous; it exists only to stop a faction OUTLIVING ITS OWN MATCH. Seen live
# 2026-07-31: a win frame arrived with no 'end' frame after it, so "Boscali" sat in the buffer until the
# NEXT match's award burst flushed - announcing "Victory: Boscali" 23s before the real "VICTORY! Primeva",
# i.e. the feed named BDF and then PALA for the same match end.
_EOM_FACTION_MAX_AGE = 120.0


# The victory line is HELD, not said on the win frame (2026-08-17: three lines at match end all
# restated the winner - "VICTORY! X wins", then "Victory: X pilots +2 each", then the result again
# inside the match-over header). It now waits for the award burst so the winner and the team bonus
# are ONE line. The awards land within ~1s of the win frame, but the comment above records a real
# 2026-07-31 case where NO 'end' frame followed a win frame at all - so the hold cannot rely on the
# flush ever coming. _eom_win_timeout() says the line bare once this expires, and the flush skips it
# if it beat the timer. Either way it is said exactly once.
_EOM_WIN_HOLD_S = 6.0
_EOM_WIN_PENDING = {"faction": "", "at": 0.0}


def _clear_eom_award_buf():
    _EOM_AWARD_BUF["wins"].clear()
    _EOM_AWARD_BUF["places"].clear()
    _EOM_AWARD_BUF["ts"] = 0.0
    _EOM_AWARD_BUF["faction"] = ""
    _EOM_AWARD_BUF["faction_ts"] = 0.0
    _EOM_AWARD_BUF["win_pts"] = 0
    _EOM_AWARD_BUF["loss_pts"] = 0


def _say_victory(rc, faction, win_pts=0):
    """The single match-end victory line. `win_pts` folds the team bonus in when it is known, so the
    award burst does not need a second line to state it. Owner can hide the whole thing (webcc
    Messages tab -> 'victory'); the POINTS are granted by the plugin either way."""
    _EOM_WIN_PENDING["faction"] = ""
    _EOM_WIN_PENDING["at"] = 0.0
    if not (USE_PLUGIN_SCORE and sysmsg_on("victory")):
        return
    tail = (f" <color=#FFFFFF>- +{_pts(win_pts)} to every {faction} pilot</color>") if win_pts else "!"
    rc.say(f"<color=#36FFD0>VICTORY!</color> {faction} wins the mission{tail}")


def _eom_win_timeout(rc, now=None):
    """Main-loop tick: a win frame whose award burst never arrived still gets its victory line,
    bare. Without this a plugin that emits 'win' but never 'end' would announce nothing at all."""
    f = _EOM_WIN_PENDING.get("faction")
    if f and (now or time.time()) - float(_EOM_WIN_PENDING.get("at") or 0) >= _EOM_WIN_HOLD_S:
        print(f"[awards] no award burst within {_EOM_WIN_HOLD_S:g}s of the win frame - "
              f"announcing {f} bare")
        _say_victory(rc, f)


def _rank_ladder_name(sid, name):
    """Display name tinted with the player's current ladder (cycle) rank colour."""
    try:
        idx = rank_index_for(cycle_points(sid))
        color = RANKS[idx][3]
    except Exception:  # noqa: BLE001 - chat must never raise
        color = "#FFFFFF"
    return f"<color={color}>{name}</color>"


def _buffer_eom_award(sid, name, reason, pts=0):
    """Accumulate what the plugin awarded (win team / 1st/2nd/3rd) for a compact chat flush.
    Points ride along so the announce always states the real configured values."""
    r = (reason or "").strip().lower()
    if r == "win":
        if not any(s == sid for s, _ in _EOM_AWARD_BUF["wins"]):
            _EOM_AWARD_BUF["wins"].append((sid, name))
        if pts:
            _EOM_AWARD_BUF["win_pts"] = pts       # same value for every winner - keep the last seen
    elif r == "loss":
        if pts:
            _EOM_AWARD_BUF["loss_pts"] = pts
    elif r in ("1st", "2nd", "3rd"):
        _EOM_AWARD_BUF["places"][r] = (sid, name, pts)
    else:
        return
    _EOM_AWARD_BUF["ts"] = time.time()


def flush_eom_award_announce(rc):
    """Tomo's match-end format (2026-07-28): name ONLY the top 3 (with their points), then a
    separate team line stating what winners/losers get - never a per-player winner list."""
    wins = list(_EOM_AWARD_BUF["wins"])
    places = dict(_EOM_AWARD_BUF["places"])
    faction = _EOM_AWARD_BUF.get("faction") or ""
    # A faction older than the window belongs to a PREVIOUS match (its win frame never got an 'end'
    # frame). Naming it here is worse than naming nobody - it announces the wrong side and contradicts
    # the real "VICTORY! <faction>" line that follows. Fall back to "the winning side".
    f_ts = _EOM_AWARD_BUF.get("faction_ts") or 0.0
    if faction and (not f_ts or time.time() - f_ts > _EOM_FACTION_MAX_AGE):
        print(f"[awards] discarding stale winning faction {faction!r} "
              f"({int(time.time() - f_ts) if f_ts else 'never'}s old) - not from this match")
        faction = ""
    win_pts = _EOM_AWARD_BUF.get("win_pts") or 0
    loss_pts = _EOM_AWARD_BUF.get("loss_pts") or 0
    _clear_eom_award_buf()
    # The held victory line goes FIRST and carries the team bonus, so the winner is named once
    # instead of three times. A stale faction leaves nothing to announce (it belongs to a match
    # that already ended); the pending slot is cleared either way so the timeout cannot re-say it.
    pending = _EOM_WIN_PENDING.get("faction") or ""
    if pending and faction:
        _say_victory(rc, faction, win_pts if wins else 0)
    else:
        _EOM_WIN_PENDING["faction"] = ""
        _EOM_WIN_PENDING["at"] = 0.0
    if not wins and not places:
        return
    parts = []
    for tag in ("1st", "2nd", "3rd"):
        if tag in places:
            entry = places[tag]
            s, n, p = entry if len(entry) == 3 else (entry[0], entry[1], 0)
            parts.append(f"{tag} {_rank_ladder_name(s, n)}"
                         + (f" <color=#FFFFFF>+{_pts(p)}</color>" if p else ""))
    if parts:
        rc.say(f"<color=#FFD200>Top pilots:</color> " + " - ".join(parts))
    if loss_pts:
        losers = "the defeated side"
        rc.say(f"<color=#FF8C69>Defeat:</color> <color=#FFFFFF>{losers} +{_pts(loss_pts)} each</color>")


def handle_plugin_win(rc, faction):
    """The plugin reported the authoritative winning faction (PvE or PvP). Announce it
    and tally each online player's win/loss from their last-known faction (STATS_META).
    This replaces the unreliable faction-0 FinishGame inference that mislabelled wins."""
    if not faction:
        return
    _clear_eom_award_buf()  # fresh match-end award burst follows this win frame
    _EOM_AWARD_BUF["faction"] = faction       # the team line names the side, never the players
    _EOM_AWARD_BUF["faction_ts"] = time.time()   # stamped so it can't be reused by a later match
    activity(f"VICTORY! {faction} wins the mission", "WIN")
    # HELD, not said (2026-08-17): the award burst is ~1s behind this frame and carries the team
    # bonus, so waiting lets one line state both instead of two lines stating the winner twice.
    # flush_eom_award_announce says it; _eom_win_timeout is the backstop if no burst ever comes.
    _EOM_WIN_PENDING["faction"] = faction
    _EOM_WIN_PENDING["at"] = time.time()
    fl = faction.lower()
    changed = False
    # W/L is PERMANENT in ranks.json, so only tally players who were actually here at the end.
    # STATS_META is cleared only by the 'end' frame, so mid-match it accumulates EVERY player seen at
    # any point in the match: someone who played 10 minutes of a 3-hour Escalation and left two hours
    # earlier was still credited a win or loss. And a spectator's faction is "", which never matches the
    # winning side, so every spectator was recorded as a LOSS. Gate on the live roster, and skip anyone
    # with no faction so spectators score neither. (audit 2026-08-01)
    # Liveness comes from the PLUGIN's own snap timestamp, not the RCMD roster. The roster is the
    # bot's least trustworthy signal - it blips to empty on a relay hiccup and the code elsewhere
    # explicitly treats it as stale after 30s - so gating a PERMANENT win on it would silently deny a
    # legitimate player their result during an outage. meta["t"] is refreshed by every snap frame and
    # keeps flowing over the console tail even when RCMD is down. A player seen within the window was
    # here at the end; one who left two hours into a three-hour match was not.
    skipped_absent = skipped_spec = 0
    _now_w = time.time()
    for sid, meta in STATS_META.items():
        seen = meta.get("t") or 0
        if seen and (_now_w - seen) > EOM_PRESENCE_WINDOW_S:
            skipped_absent += 1
            continue
        mf = (meta.get("faction") or "").strip()
        if not mf:
            skipped_spec += 1
            continue
        ensure_player(sid, meta.get("name") or sid)   # win event precedes award events
        rec = RANK_DATA.get(sid)
        if rec is None:
            continue
        if mf.lower() == fl:
            rec["wins"] = rec.get("wins", 0) + 1
        else:
            rec["losses"] = rec.get("losses", 0) + 1
        changed = True
    if skipped_absent or skipped_spec:
        print(f"[win] W/L tally skipped {skipped_absent} player(s) no longer on the roster and "
              f"{skipped_spec} with no faction (spectators)")
    if changed:
        save_ranks()


# Set by hot-path rank-ups (kill/award/snap) to request ONE coalesced plugin_ranks push
# at the end of the current main loop, instead of a blocking SSH handshake inline per
# rank-up (a kill burst could otherwise fire several ~15s-timeout connects mid-poll,
# stalling chat/vote parsing). A list so the hot paths mutate it without a global decl.
_RANK_PUSH_FLAG = [False]
# Set when in-memory RANK_DATA has changed in a way that does NOT warrant an immediate write - today
# only time-played accrual, which ticks every roster poll. ranks.json holds ~1858 records, so writing
# it per poll would be a multi-megabyte serialise every few seconds on the main loop. Flushed on a
# timer in main() and again on shutdown.
_RANKS_DIRTY = [False]
_RANKS_FLUSH_INTERVAL = 120.0
# Set by the "End match" admin action; consumed once by main(), which opens the map vote. It cannot be
# the process_admin_commands() return value, because that one means "an admin already cut the map over,
# so SUPPRESS the next vote" - the precise opposite. A list so _apply_one_admin_command mutates it
# without a global decl, matching _RANK_PUSH_FLAG above.
_ENDMATCH_REQUEST = [False]
# default-on / boot: if sharing is already enabled at startup, warm the peer cache + flag a combined rank
# push NOW (this runs AFTER _RANK_PUSH_FLAG is defined, unlike the load_shared_ranks_cfg site above), so the
# FIRST connect after boot already gets its combined name tag instead of waiting ~2s for the daemon warm.
if SHARED_RANKS_ENABLED:
    try:
        _OTHER_RANKS_CACHE = (_compute_other_ranks(), time.time())
        _RANK_PUSH_FLAG[0] = True
    except Exception:                             # noqa: BLE001 - boot must never fail on the share
        pass


def _rank_line_name(sid, rec=None):
    """1.1.29 rank-file contract field 6: the player's LAST-KNOWN display name from RANK_DATA,
    raw but pipe/newline-stripped (the pipe is the field separator). Empty string = unknown -
    also for placeholder names (the sid itself) and the plugin's own unresolved 'ID: 7656...'
    sentinel (recording that back would permanently mask real resolution plugin-side)."""
    try:
        nm = ""
        if isinstance(rec, dict):
            nm = rec.get("name") or ""
        if not nm:
            nm = (RANK_DATA.get(sid, {}) or {}).get("name") or ""
        nm = str(nm).replace("|", " ").replace("\r", " ").replace("\n", " ").strip()
        if not nm or nm == sid or nm.startswith("ID: "):
            return ""
        return nm
    except Exception:                                          # noqa: BLE001 - a display push must never raise
        return ""


def push_plugin_ranks():
    """Write sid|rank-label|#colour lines to plugin_ranks.txt on the container so the
    NukeStats plugin can render [ABBR] / [ABBR - n*] chat tags via Prefixed (custom chat).
    ABBR field MUST carry the prestige-aware label (no outer brackets — plugin wraps
    '['+label+']'). Never a *P name suffix on FullName. Best-effort.
    1.1.29: 6th field = last-known display name (plugin RawNameOf fallback for the
    server-side Steam-resolution gap; empty when unknown; old plugins ignore it).
    Atomic (.tmp + rename) so the plugin never latches a torn/empty read and blanks tags."""
    lines = []
    seen = set()
    # Empty RANKS = the rank ladder is OFF. Still push a line per player so the plugin keeps its
    # LastKnownName fallback (field 6) - but with an EMPTY label, which the plugin renders as NO tag.
    ladder_on = bool(RANKS)
    for sid, rec in list(RANK_DATA.items()):                   # snapshot: the poll loop mutates RANK_DATA on another thread
        if not ladder_on:
            lines.append(f"{sid}|||1||{_rank_line_name(sid, rec)}")
            seen.add(sid)
            continue
        idx = rank_index_for(cycle_points(sid))                # prestige-aware CYCLE rank (== combined total when never prestiged)
        _, rname, abbr, color = RANKS[idx]
        label = prestige_label(abbr, rname, prestige_count(sid))
        # sid|ABBR|#colour|rankIndex(1..11)|FullName|LastKnownName
        #   ABBR         -> custom-chat / radar tag (Prefixed wraps [label])
        #   rankIndex    -> numeric rank for PvP auto-balance. DELIBERATELY prestige-BLIND: balance
        #                   compares current ladder tiers, and a prestige is history, not present form.
        #   FullName     -> plain rank title (no prestige suffix)
        #   LastKnownName-> plugin name fallback when Steam can't resolve ("" = unknown)
        # font_safe the OPERATOR-authored rank text only (webcc can put a "->" arrow or a
        # middle dot in a rank name); the trailing display NAME is left exactly as Steam
        # gave it -- accents/Cyrillic/CJK render fine and must never be rewritten.
        lines.append(f"{sid}|{font_safe(label)}|{color}|{idx + 1}|{font_safe(rname)}|{_rank_line_name(sid, rec)}")
        seen.add(sid)
    if SHARED_RANKS_ENABLED and ladder_on:                     # cross-server: also tag players whose points live ONLY on a peer
        try:                                                   # server so their carried-over rank shows at join. The plugin bakes
            for sid in _other_ranks():                         # the name tag ONCE at connect, so the line must exist BEFORE they join.
                if sid in seen:                                # local record already emitted above (local always wins)
                    continue
                idx = rank_index_for(cycle_points(sid))        # combined cycle == the peer points minus prestige base for a peer-only sid
                pn = prestige_count(sid)
                if idx <= 0 and pn <= 0:                       # rank-0 stub with no prestige: no tag to show, skip
                    continue
                _, rname, abbr, color = RANKS[idx]
                label = prestige_label(abbr, rname, pn)
                lines.append(f"{sid}|{font_safe(label)}|{color}|{idx + 1}|{font_safe(rname)}|{_rank_line_name(sid)}")
                seen.add(sid)
        except Exception as e:                                 # noqa: BLE001 - a display push must never raise
            print(f"[plugin-ranks] peer merge skipped: {e}")
    body = ("\n".join(lines) + "\n").encode("utf-8")

    def _w(sftp):
        with sftp.open("plugin_ranks.txt.tmp", "wb") as f:
            f.write(body)
        try:
            sftp.rename("plugin_ranks.txt.tmp", "plugin_ranks.txt")
        except OSError:
            try:
                sftp.remove("plugin_ranks.txt")
            except OSError:
                pass
            sftp.rename("plugin_ranks.txt.tmp", "plugin_ranks.txt")
    try:
        _sftp_op(_w)
    except Exception as e:                        # noqa: BLE001
        print(f"[plugin-ranks] push failed: {e}")


def refresh_current_mission(rc):
    """Best-effort update of CURRENT_MISSION from the server. Called at startup AND
    periodically, so the name self-heals after a reconnect (e.g. the bot was restarted
    while the server was down -> the one-time startup read failed -> "(unknown)")."""
    global CURRENT_MISSION
    try:
        mr = rc.send("get-mission")
        if isinstance(mr, dict):
            cm = (mr.get("currentMission") or {}).get("Key", {})
            if cm.get("Name"):
                CURRENT_MISSION = friendly_label(cm["Name"])
    except Exception:        # noqa: BLE001
        pass


def _mission_status(rc):
    """Return the current/next mission keys and friendly labels from the server."""
    try:
        mr = rc.send("get-mission")
    except Exception:        # noqa: BLE001
        return None
    if not isinstance(mr, dict):
        return None

    def _key(which):
        data = (mr.get(which) or {}).get("Key", {})
        group = str(data.get("Group") or "")
        name = str(data.get("Name") or "")
        return (group, name)

    def _label(key):
        return friendly_label(key[1]) if key[1] else ""

    cur_key = _key("currentMission")
    next_key = _key("nextMission")
    return {
        "current_key": cur_key,
        "current_label": _label(cur_key),
        "next_key": next_key,
        "next_label": _label(next_key),
    }


def check_mission_time_warnings(rc, mtime, mission_name):
    """Announce remaining mission time as it crosses WARN_THRESHOLDS (once each per mission;
    the fired-set resets when the mission changes). mtime = [current, max, fetched_at]."""
    global _warnings_fired, _warn_mission
    if _warn_mission != mission_name:
        _warnings_fired = set()
        _warn_mission = mission_name
    if not mtime or mtime[1] <= 0:
        return
    remaining = mtime[1] - mtime[0]
    due = [t for t in WARN_THRESHOLDS if t not in _warnings_fired and remaining <= t]
    if not due:
        return
    _warnings_fired.update(due)          # a forced cut crosses several at once - mark them all...
    t = min(due)                         # ...but announce ONLY the smallest (no 5-line spam burst)
    mins = t // 60
    label = "1 minute" if mins == 1 else f"{mins} minutes"
    if sysmsg_on("timewarn"):                       # owner can silence the countdown lines (webcc Messages tab)
        rc.say(f"<color=#FFAA00>Mission time: {label} remaining.</color>")
    print(f"[timer] {label} remaining")


def check_match_milestones(rc, mtime):
    """'Stay for the next match' reminders, keyed to the mission's elapsed clock
    (mtime = [elapsed, max, fetched_at]). Per-mission state resets when a new mission
    begins -- detected by the elapsed clock jumping BACKWARD to ~0 (every new mission
    restarts it), which is reliable even when two missions share a display name. Call AFTER
    refresh_current_mission() so CURRENT_MISSION is fresh.

      * At each STAY_MARKS elapsed mark: a one-time reminder to stay for the next match.

    Adopting a mission already in progress (e.g. the bot reconnected mid-match) stays SILENT:
    already-passed reminders are pre-suppressed and no match_start messages fire."""
    global _ms_mission, _ms_last_elapsed, _ms_cycle_at
    global _ms_stay_fired
    if not mtime or mtime[1] <= 0:
        return
    elapsed, now = mtime[0], mtime[2]
    mission = CURRENT_MISSION
    # New mission? The elapsed clock resets to ~0 at every mission start (a backward jump well past
    # poll jitter); first boot bootstraps via _ms_mission is None; a fresh name is a backup signal.
    is_new = (_ms_mission is None or elapsed + 30 < _ms_last_elapsed
              or (mission != _ms_mission and mission and mission != "(unknown)"))
    if is_new and now - _ms_cycle_at > 90:        # 90s cooldown collapses the name-lag/elapsed-lag double edge
        _ms_cycle_at = now
        _ms_stay_fired = set()
        if elapsed > 180:                         # adopted an IN-PROGRESS mission -> don't backfire
            _ms_stay_fired = {m for m in STAY_MARKS if elapsed >= m}
        else:
            try:
                fire_event_messages(rc, "match_start")   # genuine fresh match -> owner match_start messages
            except Exception as e:                # noqa: BLE001
                print(f"[servermsg] match_start error: {e}")
    _ms_mission = mission
    _ms_last_elapsed = elapsed

    # --- 'stay for the next match' reminders at 105 / 125 / 145 min in ---
    for mark in STAY_MARKS:
        if mark in _ms_stay_fired or elapsed < mark:
            continue
        _ms_stay_fired.add(mark)
        if sysmsg_on("stay"):
            rc.say(sysmsg_text("stay", _SYSMSG_STAY_DEFAULT))
        activity(f"Posted the 'stay for the next match' reminder ({mark // 60} min in)", "INFO")
        print(f"[stay] reminder fired at {mark // 60} min elapsed")


def load_schedule():
    """Read schedule.json (the web CC writes it; this bot executes due items)."""
    try:
        with open(SCHEDULE_FILE, encoding="utf-8") as f:
            d = json.load(f)
        return d if isinstance(d, list) else []
    except (OSError, json.JSONDecodeError):
        return []


def save_schedule(items):
    try:
        tmp = SCHEDULE_FILE + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(items, f, indent=2)
        os.replace(tmp, SCHEDULE_FILE)
    except OSError as e:
        print(f"[sched] save failed: {e}")


def _sched_when_ts(when):
    """Parse a 'YYYY-MM-DD HH:MM' (local) schedule time to an epoch, or None."""
    try:
        return time.mktime(time.strptime(str(when)[:16].replace("T", " "), "%Y-%m-%d %H:%M"))
    except (ValueError, TypeError):
        return None


def check_schedule(rc):
    """Fire any due scheduled restarts/updates: warn players in-chat at SCHED_WARN thresholds,
    then at the target time run the guarded deploy (deploy.bat -> run.bat --deploy-plugin) as a
    DETACHED subprocess so this daemon keeps running (it just reconnects across the bounce, like
    the 05:00 task). An 'update' deploys whatever pending_plugin.dll is staged; a 'restart' is a
    plain bounce. Both go through the same relay-verified pipeline."""
    items = load_schedule()
    if not items:
        return
    now = time.time()
    dirty = False
    for it in items:
        if it.get("status") != "pending":
            continue
        ts = _sched_when_ts(it.get("when", ""))
        if ts is None:
            continue
        label = "update" if it.get("type") == "update" else "restart"
        note = f" - {it['desc']}" if it.get("desc") else ""
        remaining = ts - now
        if remaining > 0:                                  # not due yet: maybe warn
            warned = _sched_warned.setdefault(it["id"], set())
            for thr in SCHED_WARN:
                if remaining <= thr and thr not in warned:
                    warned.add(thr)
                    rc.say(f"<color=#FFAA00>** SCHEDULED {label.upper()} in {thr // 60} min{note} - wrap it up! **</color>")
                    activity(f"scheduled {label} in {thr // 60} min{note}", "!")
            continue
        # due -> fire
        it["fired"] = time.strftime("%Y-%m-%d %H:%M:%S")
        dirty = True
        rc.say(f"<color=#FF6A00>** SCHEDULED {label.upper()} NOW{note} - server back in ~1 min **</color>")
        activity(f"firing scheduled {label}{note}", "!")
        try:
            subprocess.Popen(["cmd", "/c", os.path.join(_BASE_DIR, "deploy.bat")], cwd=_BASE_DIR,
                             creationflags=getattr(subprocess, "CREATE_NEW_CONSOLE", 0))
            it["status"] = "done"
            print(f"[sched] fired {label}{note} -> deploy.bat launched")
        except OSError as e:
            it["status"] = "failed"
            it["result"] = str(e)
            print(f"[sched] fire failed: {e}")
    if dirty:
        save_schedule(items)


def _player_name_pool():
    """sid -> display name, merged from every source we know (ranks.json, the name cache,
    the live roster). Used by resolve_player + the grant 'did you mean' suggestions."""
    pool = {}
    for sid, rec in RANK_DATA.items():
        nm = (rec.get("name") or "").strip()
        if nm:
            pool[sid] = nm
    for sid, nm in PLAYER_NAMES.items():
        nm = (nm or "").strip()
        if nm and sid not in pool:
            pool[sid] = nm
    for sid, p in ROSTER_BY_SID.items():
        nm = (p.get("displayName") or "").strip()
        if nm and sid not in pool:
            pool[sid] = nm
    return pool


def resolve_player(query):
    """Resolve a SteamID or display name to a SteamID, else None. Used by the admin 'grant'
    command. Tries, in order: exact SteamID, raw SteamID, exact name, unique name-prefix, unique
    substring, then a unique FUZZY match -- so admins can grant by a partial or slightly-off name
    (game names are often truncated/odd, e.g. 'GoatseWithTheAwesomeSauc'). Every step requires a
    UNIQUE match, so it never silently grants the wrong player; admin_grant logs the resolved name."""
    q = str(query).strip()
    if not q:
        return None
    if q in RANK_DATA:                       # exact SteamID we already track
        return q
    if q.isdigit() and len(q) >= 15:         # looks like a raw SteamID (can grant to anyone)
        return q
    ql = q.lower()
    pool = _player_name_pool()
    exact = [sid for sid, nm in pool.items() if nm.lower() == ql]      # 1) exact (case-insensitive) name
    if exact:
        return exact[0] if len(exact) == 1 else None                  # ambiguous exact -> refuse
    pre = [sid for sid, nm in pool.items() if nm.lower().startswith(ql)]   # 2) unique prefix
    if len(pre) == 1:
        return pre[0]
    sub = [sid for sid, nm in pool.items() if ql in nm.lower()]        # 3) unique substring
    if len(sub) == 1:
        return sub[0]
    import difflib                                                     # 4) unique fuzzy (typo/truncation tolerant)
    scored = sorted(((difflib.SequenceMatcher(None, ql, nm.lower()).ratio(), sid)
                     for sid, nm in pool.items()), reverse=True)
    if scored and scored[0][0] >= 0.82 and (len(scored) == 1 or scored[0][0] - scored[1][0] >= 0.08):
        return scored[0][1]
    return None


_admin_cmd_offset = None     # byte offset into ADMIN_CMD_FILE; None until pre-existing lines are skipped
ADMIN_RESULTS_FILE = os.path.join(_BASE_DIR, "admin_results.jsonl")  # setcfg/etc ack lines for webcc poll


def _persist_admin_offset():
    try:
        with open(ADMIN_CMD_OFFSET_FILE, "w", encoding="utf-8") as _of:
            _of.write(str(_admin_cmd_offset))
    except OSError:
        pass


def _write_admin_result(action, ok, key="", value="", error="", extra=None):
    """Append one ack line for webcc to poll (pragmatic setcfg feedback)."""
    rec = {"ts": time.time(), "action": action, "ok": bool(ok), "key": key, "value": value}
    if error:
        rec["error"] = str(error)
    if isinstance(extra, dict):
        rec.update(extra)
    try:
        with open(ADMIN_RESULTS_FILE, "a", encoding="utf-8") as f:
            f.write(json.dumps(rec, ensure_ascii=False) + "\n")
        # Keep the file bounded (~200 recent lines)
        try:
            if os.path.getsize(ADMIN_RESULTS_FILE) > 200_000:
                with open(ADMIN_RESULTS_FILE, "r", encoding="utf-8", errors="replace") as rf:
                    lines = rf.readlines()[-150:]
                with open(ADMIN_RESULTS_FILE, "w", encoding="utf-8") as wf:
                    wf.writelines(lines)
        except OSError:
            pass
    except OSError:
        pass


def _apply_one_admin_command(rc, cmd, now_q):
    """Dispatch one complete admin queue object. Returns True if a successful changemap."""
    did_changemap = False
    _cts = cmd.get("ts")
    if not isinstance(_cts, (int, float)) or (now_q - _cts) > ADMIN_CMD_MAX_AGE:
        # FAIL CLOSED on a missing/invalid ts. Lesson of 2026-07-02 22:10: pre-ts-era queue
        # lines (the June-24 revert grants) replayed at startup and mass-wiped ranks because
        # the old guard only skipped lines it could DATE. Unknown age == stale, never fresh.
        print(f"[admin] skipping stale/undated queued command ({cmd.get('action')})")
        activity(f"Skipped a stale/undated queued admin command ({cmd.get('action')}) - not replaying it", "!")
        return False
    if cmd.get("action") == "grant":
        admin_grant(rc, cmd)
    elif cmd.get("action") == "team":
        admin_team(rc, cmd)
    elif cmd.get("action") == "endmatch":
        # Admin "End match": bank the match that just ended, then go straight to a map vote.
        #
        # This used to be a bare RCON set-time-remaining=5 sent from the panel, bypassing the bot
        # entirely. That dropped the clock into OUR PLUGIN's timeout window with the scores level, so
        # the plugin announced "it's a DRAW" - and because its only re-fire guard was "has the game
        # ended yet", which stays false when the game has no Draw end state to declare, it re-announced
        # on every tick. Owner report 2026-08-01: "it keeps spamming it in the chat... it's a draw over
        # and over". The plugin now latches that announcement to once per match (1.3.23); this branch
        # removes the reason it fired at all.
        #
        # main() does the work (it owns the vote state); this branch only raises the flag.
        #
        # It deliberately does NOT call match_finalize here. The end is applied as a FORCED CUT, and a
        # forced cut produces the game's own "Mission complete" - which the handler in main() banks
        # unconditionally, BEFORE its suppression gate. Finalising here as well would bank the match
        # early, on the wrong roster, and then bank it a second time when the cut landed.
        try:
            activity("Admin ended the match", "MAP")
            # NOT the did_changemap return value - that one means "suppress the auto vote", which is
            # the exact opposite of what this needs.
            _ENDMATCH_REQUEST[0] = True
        except Exception as e:                            # noqa: BLE001
            print(f"[admin] endmatch error: {e}")
    elif cmd.get("action") == "changemap":
        try:
            if force_change_map(rc, cmd.get("name", "")):
                did_changemap = True          # tell main() to suppress the auto mission-end vote — but ONLY
                                              # on success: a rejected map change must not eat the next vote
        except Exception as e:                # noqa: BLE001
            print(f"[admin] changemap error: {e}")
    elif cmd.get("action") == "setcfg":       # webcc settings menu: change a plugin/bot/game setting
        try:
            r = set_cfg_dispatch(rc, cmd.get("key", ""), cmd.get("value", ""), cmd.get("owner", "plugin"))
            ok = bool(isinstance(r, dict) and r.get("ok"))
            err = "" if ok else str((r or {}).get("error") or "setcfg failed")
            _write_admin_result("setcfg", ok, key=str(cmd.get("key", "")),
                                value=str(cmd.get("value", "")), error=err,
                                extra={"owner": str(cmd.get("owner", "plugin")),
                                       "needs_restart": bool((r or {}).get("needs_restart"))})
            if str(cmd.get("owner", "plugin")).lower() == "plugin":
                try:                          # plugin cfg only APPLIES while a player is online — say so
                    with open(DASHBOARD_STATE_FILE, encoding="utf-8") as _df:
                        if not json.load(_df).get("online_count"):
                            activity(f"Setting {cmd.get('key')} queued - the plugin applies it when "
                                     f"a player is next online (server is empty now)", "ADMIN")
                except (OSError, ValueError):
                    pass
        except Exception as e:                # noqa: BLE001
            print(f"[admin] setcfg error: {e}")
            _write_admin_result("setcfg", False, key=str(cmd.get("key", "")),
                                value=str(cmd.get("value", "")), error=str(e))
    elif cmd.get("action") == "dumpcfg":      # webcc settings menu: ask the plugin to re-emit its live config
        try:
            _drop_plugin_cmd("dumpcfg")
        except Exception as e:                # noqa: BLE001
            print(f"[admin] dumpcfg error: {e}")
    elif cmd.get("action") == "missionpool":  # webcc Mission Pool modal: toggle a mission in/out of the votemap pool
        try:
            if set_mission_enabled(cmd.get("mission", ""), bool(cmd.get("on", True))):
                activity(f"Mission pool: {friendly_label(cmd.get('mission', ''))} -> "
                         f"{'on' if cmd.get('on', True) else 'off'}", "MAP")
            else:
                activity(f"Mission pool: REJECTED toggle for '{cmd.get('mission', '')}' (unknown mission)", "MAP")
        except Exception as e:                # noqa: BLE001
            print(f"[admin] missionpool error: {e}")
    elif cmd.get("action") == "servermsg":    # webcc Messages modal: CRUD an automated server message
        try:
            ok, info = server_msg_apply(cmd.get("op", ""), cmd.get("msg", {}))
            if ok:
                activity(f"Server message {cmd.get('op', '')}: {info}", "BOT")
            else:
                activity(f"Server message {cmd.get('op', '')} REJECTED: {info}", "BOT")
        except Exception as e:                # noqa: BLE001
            print(f"[admin] servermsg error: {e}")
    elif cmd.get("action") == "admin_kick":
        # WebCC / command-centre kick: whisper reason, then RCON kick. Session list STAYS (no unkick).
        try:
            ksid = str(cmd.get("sid", "")).strip()
            if not re.fullmatch(r"\d{6,20}", ksid):
                activity("admin_kick: invalid SteamID - not applied", "!")
            else:
                reason = re.sub(r"[\x00-\x1f|]+", " ", str(cmd.get("reason") or "")).strip()[:160] or "kicked by admin"
                kname = str(cmd.get("name", "") or PLAYER_NAMES.get(ksid)
                            or RANK_DATA.get(ksid, {}).get("name") or "")
                whisper = (f"<color=#FF0000>You have been kicked by an admin.</color> "
                           f"<color=#FFD200>{reason}</color> "
                           f"<color=#AAAAAA>(cannot rejoin this session until unkick)</color>")
                try:
                    _drop_plugin_cmd("tell|" + ksid + "|" + whisper)
                except Exception as te:       # noqa: BLE001
                    print(f"[admin-kick] tell failed: {te}")
                due = time.time() + _ADMIN_KICK_DELAY
                for e in _pending_admin_kicks:
                    if e.get("sid") == ksid:
                        e["due"] = max(e.get("due", 0), due)
                        e["reason"] = reason
                        if kname:
                            e["name"] = kname
                        break
                else:
                    _pending_admin_kicks.append(
                        {"sid": ksid, "name": kname, "reason": reason, "due": due})
                # Moderation Reports: one row at queue (deduped). Plugin TK/flood already file their own.
                note_moderation_action(ksid, kname, "kick", reason, method="admin", source="admin")
                activity(f"ADMIN KICK queued {kname or ksid}: {reason} (TellPlayer → session-block)", "ADMIN")
        except Exception as e:                # noqa: BLE001
            print(f"[admin] admin_kick error: {e}")
    elif cmd.get("action") in ("ban_steamid", "unban_steamid"):   # webcc Reports tab: ban/unban a SteamID
        try:
            bsid = str(cmd.get("sid", "")).strip()
            if not re.fullmatch(r"\d{6,20}", bsid):   # mirror the cc_web guard: defend the plugin-cmd channel
                activity(f"{cmd.get('action')}: invalid SteamID - not applied", "!")  # vs newline/pipe injection
                bsid = ""
            if bsid:
                ban = cmd.get("action") == "ban_steamid"
                bname = str(cmd.get("name", "") or PLAYER_NAMES.get(bsid)
                            or RANK_DATA.get(bsid, {}).get("name") or "")
                breason = re.sub(r"[\x00-\x1f|]+", " ", str(cmd.get("reason") or "")).strip()[:160]
                _drop_plugin_cmd(("ban|" if ban else "unban|") + bsid)   # plugin list (in-memory + plugin_bans.txt)
                try:
                    rc.send("banlist-add" if ban else "banlist-remove", bsid)   # game-native list (immediate; no player needed)
                except Exception:             # noqa: BLE001
                    pass
                if not ban:
                    # Also clear KickPlayer session kick (flood kicks land here, not on ban_list.txt)
                    try:
                        rc.send("unkick-player", bsid)
                    except Exception:         # noqa: BLE001
                        pass
                if ban:
                    # Ban-from-Reports: existing flood/TK row gets banned=True — do not spam a 2nd row.
                    # Palette / standalone ban with no recent report → new Moderation entry.
                    if not _recent_report_for(bsid, within=120.0):
                        note_moderation_action(
                            bsid, bname, "ban",
                            breason or "banned by admin",
                            method="admin", source="admin", banned=True, within=120.0)
                set_report_banned(bsid, ban)
                try:
                    refresh_banned_players()
                except Exception:             # noqa: BLE001
                    pass
                activity(f"{'Banned' if ban else 'Unbanned'} {bname or bsid} (plugin + game ban list"
                         f"{'' if ban else ' + session unkick'})", "ADMIN")
        except Exception as e:                # noqa: BLE001
            print(f"[admin] {cmd.get('action')} error: {e}")
    elif cmd.get("action") == "logban":       # webcc Reports 'Log ban' button: record a ban in the persistent ban log
        try:
            n = log_ban(cmd.get("sid", ""), cmd.get("name", ""), cmd.get("reason", ""), cmd.get("detail"))
            if n:
                activity(f"Ban logged: {cmd.get('name', '?')} (now {n}x in the ban log)", "ADMIN")
        except Exception as e:                # noqa: BLE001
            print(f"[admin] logban error: {e}")
    elif cmd.get("action") == "rmbanlog":     # webcc Ban log 🗑 button: delete one player's logged-ban history
        try:
            if remove_ban_log(cmd.get("sid", "")):
                activity(f"Ban-log entry removed for {cmd.get('name', '') or cmd.get('sid', '?')}", "ADMIN")
        except Exception as e:                # noqa: BLE001
            print(f"[admin] rmbanlog error: {e}")
    elif cmd.get("action") in ("clear_report", "clear_reports"):   # webcc Reports tab: clear one / all reports
        try:
            if cmd.get("action") == "clear_reports":
                n = clear_all_reports()
                activity(f"Cleared all reports ({n})", "ADMIN")
            else:
                if clear_report(int(cmd.get("seq", 0))):
                    activity(f"Cleared report #{cmd.get('seq')}", "ADMIN")
        except Exception as e:                # noqa: BLE001
            print(f"[admin] {cmd.get('action')} error: {e}")
    elif cmd.get("action") == "dumpserverconfig":   # webcc Server Settings tab: re-read DedicatedServerConfig.json
        try:
            refresh_server_config()
        except Exception as e:                # noqa: BLE001
            print(f"[admin] dumpserverconfig error: {e}")
    elif cmd.get("action") == "missionaudit":   # webcc Mission Pool: re-scan official/custom missions
        # This fires on every Mission Pool modal open and every mission toggle, and it runs on the
        # bot's single-threaded main loop. A DEEP scan downloads every official mission (~15 MB) to
        # hash it, stalling the console tail, the vote timer and the roster poll for the whole
        # transfer - with players on. The panel only needs the mission LIST, so it gets a shallow scan;
        # the integrity check is opt-in via the explicit "deep" flag and is refused while anyone is
        # online. (round-2 audit 2026-08-01)
        try:
            want_deep = bool(cmd.get("deep"))
            if want_deep and len(ROSTER_BY_SID) > 0:
                activity(f"Mission integrity scan skipped - {len(ROSTER_BY_SID)} player(s) online "
                         f"(it downloads every mission and would stall the bot)", "!")
                want_deep = False
            refresh_mission_audit(deep=want_deep)
        except Exception as e:                # noqa: BLE001
            print(f"[admin] missionaudit error: {e}")
    elif cmd.get("action") == "missiontoggle":   # webcc Mission Pool: enable/disable a mission in the live rotation
        try:
            r = mission_set_enabled(cmd.get("group", "User"), cmd.get("name", ""), bool(cmd.get("on")))
            activity(f"Mission {'enabled' if cmd.get('on') else 'disabled'}: {cmd.get('name')}"
                     + ("" if r.get("ok") else f" (FAILED: {r.get('error')})"), "MAP")
        except Exception as e:                # noqa: BLE001
            print(f"[admin] missiontoggle error: {e}")
    elif cmd.get("action") == "missionworkshop":   # webcc Mission Pool: add a Steam Workshop mission
        try:
            r = mission_add_workshop(cmd.get("id", ""))
            activity(f"Workshop mission added: {cmd.get('id')}"
                     + ("" if r.get("ok") else f" (FAILED: {r.get('error')})"), "MAP")
        except Exception as e:                # noqa: BLE001
            print(f"[admin] missionworkshop error: {e}")
    elif cmd.get("action") == "missionupload":   # webcc Mission Pool: upload a custom mission folder (added OFF)
        try:
            sp = os.path.join(_BASE_DIR, str(cmd.get("staging", "")))
            with open(sp, encoding="utf-8") as f:
                up = json.load(f)
            r = mission_upload(up.get("name", ""), up.get("files", []))
            try:
                os.remove(sp)
            except OSError:
                pass
            activity(f"Mission uploaded: {up.get('name')}"
                     + ("" if r.get("ok") else f" (FAILED: {r.get('error')})"), "MAP")
        except Exception as e:                # noqa: BLE001
            print(f"[admin] missionupload error: {e}")
    elif cmd.get("action") == "setvotemap":   # webcc Votemap settings: ballot size / mode / includes
        try:
            if set_votemap_cfg(cmd.get("key", ""), cmd.get("value")):
                activity(f"Votemap: {cmd.get('key')} = {cmd.get('value')}", "MAP")
                if cmd.get("key") == "boot_map":      # FIX 4: pin it game-side the moment it is set
                    apply_boot_map_rotation("boot map set")
            else:
                activity(f"Votemap: REJECTED {cmd.get('key')} = {cmd.get('value')} (invalid key/value)", "MAP")
        except Exception as e:                # noqa: BLE001
            print(f"[admin] setvotemap error: {e}")
    elif cmd.get("action") == "banaudit":     # webcc Moderation 'Banned' tab: re-read plugin_bans.txt
        try:
            refresh_banned_players()
        except Exception as e:                # noqa: BLE001
            print(f"[admin] banaudit error: {e}")
    elif cmd.get("action") == "setserverconfig":    # webcc Server Settings tab: edit one config field (+ gpanel mirror)
        try:
            r = set_server_config(cmd.get("key", ""), cmd.get("value", ""))
            if not r.get("ok"):
                # LOUD failure: into the activity feed AND last_set so the webcc shows a red
                # per-field badge within ~1s. A save that failed must never look successful.
                activity(f"Server config REJECTED: {cmd.get('key')}: {r.get('error')}", "!")
                _srvcfg_cache["last_set"] = {"ok": False, "key": cmd.get("key", ""),
                                             "error": r.get("error"), "ts": time.time()}
                print(f"[admin] setserverconfig {cmd.get('key')}: {r.get('error')}")
        except Exception as e:                # noqa: BLE001
            activity(f"Server config REJECTED: {cmd.get('key')}: {e}", "!")
            _srvcfg_cache["last_set"] = {"ok": False, "key": cmd.get("key", ""),
                                         "error": str(e), "ts": time.time()}
            print(f"[admin] setserverconfig error: {e}")
    elif cmd.get("action") == "sysmsg":             # webcc Messages tab: edit a built-in automated message
        try:
            if sysmsg_set(cmd.get("key", ""), cmd.get("fields", {}) or {}):
                activity(f"System message '{cmd.get('key', '')}' updated", "BOT")
            else:
                activity(f"System message '{cmd.get('key', '')}' update REJECTED (unknown key)", "BOT")
        except Exception as e:                # noqa: BLE001
            print(f"[admin] sysmsg error: {e}")
    elif cmd.get("action") == "helpcfg":            # webcc Help editor: show/hide a command in the !help list
        try:
            if set_help_gate(cmd.get("cmd", ""), bool(cmd.get("on", True))):
                activity(f"!help: '{cmd.get('cmd', '')}' {'shown' if cmd.get('on') else 'hidden'}", "BOT")
            else:
                activity(f"!help: gate change for '{cmd.get('cmd', '')}' REJECTED (unknown/locked command)", "BOT")
        except Exception as e:                # noqa: BLE001
            print(f"[admin] helpcfg error: {e}")
    elif cmd.get("action") == "rankladder":         # webcc Ranks modal: replace the whole rank ladder + rank-up template
        try:
            res = rank_ladder_apply(cmd.get("payload", {}) or {})
            if res.get("ok"):
                push_plugin_ranks()                  # refresh the in-chat [Name - RANK] tags + colours immediately
                activity(f"Rank ladder updated ({len(RANKS)} ranks)", "BOT")
            else:
                activity(f"Rank ladder NOT updated: {res.get('error', '?')}", "!")
        except Exception as e:                # noqa: BLE001
            print(f"[admin] rankladder error: {e}")
    elif cmd.get("action") == "sharedranks":        # webcc Shared Ranks card: enable/disable + set the shared directory
        try:
            set_shared_ranks(bool(cmd.get("enabled")), str(cmd.get("dir", "") or ""))
            activity(f"Shared ranks {'ON' if SHARED_RANKS_ENABLED else 'off'}"
                     + (f" -> {SHARED_RANKS_DIR}" if SHARED_RANKS_ENABLED else ""), "BOT")
        except Exception as e:                # noqa: BLE001
            print(f"[admin] sharedranks error: {e}")
    elif cmd.get("action") == "awardtoggle":        # webcc Vanilla/Awards card: flip one bonus-point source on/off
        try:
            if set_award_toggle(cmd.get("key", ""), bool(cmd.get("on", True))):
                activity(f"Award source '{cmd.get('key', '')}' -> {'ON' if cmd.get('on', True) else 'OFF'}", "BOT")
            else:
                activity(f"Award toggle '{cmd.get('key', '')}' REJECTED (unknown source)", "BOT")
        except Exception as e:                # noqa: BLE001
            print(f"[admin] awardtoggle error: {e}")
    return did_changemap


def process_admin_commands(rc):
    """Apply admin commands queued by the command centre (admin_commands.jsonl). The bot
    owns ranks.json, so all manual point changes MUST flow through here (the command centre
    is a separate process and must never write ranks.json directly).

    Offset advances ONLY after each complete newline-terminated line is applied (or skipped
    as bad/stale). A torn partial JSONL line at EOF is left unconsumed until the next write."""
    global _admin_cmd_offset
    if _admin_cmd_offset is None:            # not initialized yet (set once at startup in main)
        return
    try:
        size = os.path.getsize(ADMIN_CMD_FILE)
    except OSError:
        return                               # no queue file yet -> nothing to do
    if size < _admin_cmd_offset:             # truncated/rotated: resume from the new END — replaying from 0
        _admin_cmd_offset = size             # would re-apply every surviving command (double grants/bans)
        _persist_admin_offset()
    if size == _admin_cmd_offset:
        return
    did_changemap = False
    _now_q = time.time()
    try:
        with open(ADMIN_CMD_FILE, "rb") as f:
            f.seek(_admin_cmd_offset)
            while True:
                pos_before = f.tell()
                raw = f.readline()
                if not raw:
                    break
                # Torn partial line (writer mid-flush): do NOT advance past it.
                if not raw.endswith(b"\n") and not raw.endswith(b"\r"):
                    break
                line = raw.decode("utf-8", errors="replace").strip()
                if not line:
                    _admin_cmd_offset = f.tell()
                    _persist_admin_offset()
                    continue
                try:
                    cmd = json.loads(line)
                except ValueError:
                    print(f"[admin] skipping malformed queue line at offset {pos_before}")
                    _admin_cmd_offset = f.tell()
                    _persist_admin_offset()
                    continue
                if not isinstance(cmd, dict):
                    _admin_cmd_offset = f.tell()
                    _persist_admin_offset()
                    continue
                if _apply_one_admin_command(rc, cmd, _now_q):
                    did_changemap = True
                # Advance only AFTER apply (or deliberate skip) of a complete line.
                _admin_cmd_offset = f.tell()
                _persist_admin_offset()
    except OSError:
        return
    return did_changemap


def admin_grant(rc, cmd):
    """Manually add (or subtract) rank points for a player and do every follow-on update:
    persist ranks.json, refresh the in-chat rank tag, append the audit ledger, and
    announce + record a promotion if one is crossed."""
    query = str(cmd.get("query", "")).strip()
    try:
        pts = round(float(cmd.get("points", 0)), 1)
    except (TypeError, ValueError):
        return
    if not query or pts == 0:
        return
    sid = resolve_player(query)
    if not sid:
        import difflib
        pool = _player_name_pool()
        near = difflib.get_close_matches(query.lower(), [nm.lower() for nm in pool.values()], n=3, cutoff=0.5)
        # map the lowered suggestions back to their real display names
        seen, names = set(), []
        for sug in near:
            for nm in pool.values():
                if nm.lower() == sug and nm not in seen:
                    seen.add(nm); names.append(nm); break
        hint = (" - did you mean: " + ", ".join(names)) if names else " - no close match (try the exact name, the SteamID, or click the player)"
        activity(f"admin grant: '{query}' didn't match a player{hint} - not applied", "!")
        return
    name = display_name(sid)
    old_idx, new_idx, total = award_points(sid, name, pts)
    ledger_award(sid, name, pts, "grant", "admin grant (command centre)", total, match=None)
    save_ranks()
    push_plugin_ranks()                      # refresh the in-chat [Name - RANK] tag immediately
    activity(f"ADMIN granted {_pts_i(pts):+d} pts to {name}  ->  now {_pts_i(total)} pts", "RANK")
    crossed, old_ann, ann_idx = combined_rankup(sid, total, pts)   # #4: combined-rank crossing
    if crossed:
        _, rname, abbr, color = RANKS[ann_idx]
        announce_rankup(rc, sid, name, ann_idx, old_ann)
        activity(f"{name} promoted to {rname} ({abbr})!", "RANK")


_plugin_cmd_id = 0


def admin_team(rc, cmd):
    """Relay a command-centre TEAM action (move / spec / join / balance) to the NukeStats
    plugin by dropping a per-command file 'plugin_cmd_<id>.txt' (content 'verb|steamId|faction')
    in the container game root. The plugin processes then DELETES each file (no dedup to get
    wrong). Takes effect once the v0.6.1 plugin is loaded."""
    global _plugin_cmd_id
    verb = str(cmd.get("verb", "")).strip().lower()
    if verb not in ("move", "team", "join", "spec", "spectate", "unteam", "balance",
                    "setrank", "setfunds", "addfunds", "swapteam",
                    "forceteamswap", "aircraftlist"):
        return
    sid = str(cmd.get("sid", "")).strip().replace("|", "").replace("\n", "").replace("\r", "")
    faction = str(cmd.get("faction", "")).strip().replace("|", "").replace("\n", "").replace("\r", "")   # for set*rank/*funds this 3rd field carries the NUMBER; strip framing chars (defense-in-depth, ADMIN-1)
    if verb not in ("balance", "aircraftlist") and not sid:
        activity(f"admin {verb}: no SteamID - not applied", "!")
        return
    try:
        _drop_plugin_cmd(f"{verb}|{sid}|{faction}")   # pooled session (atomic .tmp+rename)
    except Exception as e:                          # noqa: BLE001
        activity(f"team command relay failed: {e}", "!")
        return
    name = PLAYER_NAMES.get(sid) or sid or "(n/a)"
    if verb == "aircraftlist":
        activity("Dumping the live aircraft catalogue to the plugin log", "ADMIN")
    elif verb == "balance":
        activity("ADMIN ran a team balance pass", "TEAM")
    elif verb in ("spec", "spectate", "unteam"):
        activity(f"ADMIN moved {name} to spectate", "TEAM")
    elif verb in ("setrank", "setfunds", "addfunds"):
        activity(f"ADMIN {verb} {name} -> {faction}", "TEAM")
    else:
        activity(f"ADMIN moved {name} -> {faction}", "TEAM")


_PLUGIN_META_CACHE = [None, ""]      # (mtime_ns, size) -> version string


def _plugin_version():
    """Version recorded in deployed_plugin.json, cached on the file's (mtime, size).

    write_dashboard_state() asks for this every STATE_WRITE_INTERVAL (0.5s) and PLUGIN_VERSION_LIVE
    is usually EMPTY in steady state - the console tail seeks to EOF, so a plugin that loaded before
    the bot attached (i.e. after every deploy, which restarts the game first and the bot second) is
    never seen. Uncached that was two open+json.load calls a second, forever. Read as utf-8-sig so a
    BOM-prefixed marker still parses instead of blanking the header version."""
    path = os.path.join(_BASE_DIR, "deployed_plugin.json")
    try:
        st = os.stat(path)
        stamp = (st.st_mtime_ns, st.st_size)
    except OSError:
        _PLUGIN_META_CACHE[0] = None
        _PLUGIN_META_CACHE[1] = ""
        return ""
    if _PLUGIN_META_CACHE[0] == stamp:
        return _PLUGIN_META_CACHE[1]
    try:
        with open(path, encoding="utf-8-sig") as f:
            ver = str(json.load(f).get("version", "") or "")
    except (OSError, ValueError):
        ver = ""
    _PLUGIN_META_CACHE[0] = stamp
    _PLUGIN_META_CACHE[1] = ver
    return ver


# ── FIX 1: live plugin version for the webcc header ─────────────────────────────────────────────
# The plugin logs "NukeStats <version> loaded" (a plain BepInEx line, not a [NOSTATS] frame). Capture it
# from the console tail so the header shows the ACTUALLY-running build. Falls back to deployed_plugin.json
# (recorded at deploy) when the load line wasn't seen this run (the tail seeks to EOF, so a plugin that
# loaded before the bot attached is missed until the next restart).
PLUGIN_VERSION_LIVE = ""
_PLUGIN_LOADED_RE = re.compile(r"NukeStats\s+v?(\d+\.\d+(?:\.\d+)?)")


def note_plugin_version(line):
    """Update PLUGIN_VERSION_LIVE from a 'NukeStats <ver> loaded' console line. Cheap; the caller gates
    on the 'NukeStats' substring so this regex almost never runs on ordinary lines."""
    global PLUGIN_VERSION_LIVE
    m = _PLUGIN_LOADED_RE.search(line)
    if m and m.group(1) != PLUGIN_VERSION_LIVE:
        PLUGIN_VERSION_LIVE = m.group(1)
        print(f"[plugin] live version detected: {PLUGIN_VERSION_LIVE}")


def _live_plugin_version():
    """Best-known plugin version for the dashboard/header: the live load line if seen, else the value
    recorded in deployed_plugin.json at deploy time."""
    return PLUGIN_VERSION_LIVE or _plugin_version()


def set_cfg_dispatch(rc, key, value, owner):
    """webcc settings menu: route a setting change to the right owner.
       plugin  -> drop a setcfg plugin_cmd (applies live on the next HQ tick, persisted to the cfg);
       bot     -> persist to bot_overrides.json + apply the runtime global (full effect on bot restart);
       votemap -> votemap_config.json via set_votemap_cfg (same validator/writer as /api/votemap) —
                  lets ballot settings (force-PvP etc.) live in the Game Settings menu;
       game    -> DedicatedServerConfig via set_server_config (atomic + verify-after-write +
                  reload-config), or the legacy run.bat --set-votekick for the old VoteKick key."""
    key = str(key).strip()
    owner = str(owner).strip().lower()
    val = str(value).strip()
    try:
        if owner == "plugin":
            safek = key.replace("|", "").replace("\n", " ").replace("\r", " ")
            safev = val.replace("|", "").replace("\n", " ").replace("\r", " ")
            _drop_plugin_cmd("setcfg|" + safek + "|" + safev)   # (Global.* public-listing special-casing removed with the directory feature)
            activity(f"ADMIN set {key} = {val}", "CFG")
            # needs_restart here is ADVISORY ONLY and deliberately hardcoded: this module never reads
            # settings_catalogue.json, so it cannot know which plugin keys are restart-only (13 of them
            # are, e.g. the Mirage buffer settings). webcc.html derives the truth from the catalogue row
            # and ORs it with this flag, so the ack can only add information. Do NOT "fix" this to False-
            # means-false: before that OR existed, this literal silently overwrote the panel's correct
            # "restart to apply" with "applied" one second later.
            return {"ok": True, "needs_restart": False}
        if owner == "bot":
            short = key.split(".")[-1].split(":")[-1]
            if short in _VOTE_TIMING_KEYS:            # FIX 3: the two vote-timing knobs persist in .nost-data,
                return set_vote_timing(short, val)    # NOT bot_overrides.json — and re-derive+push PMD in one op
            if short not in _BOT_OVERRIDE_KEYS:
                activity(f"settings: unknown bot setting {key}", "!")
                return {"ok": False, "error": "unknown bot setting"}
            try:
                num = float(val)
                num = int(num) if num.is_integer() else num
            except ValueError:
                return {"ok": False, "error": "must be a number"}
            ov = {}
            try:
                with open(os.path.join(_BASE_DIR, "bot_overrides.json"), "r", encoding="utf-8") as f:
                    ov = json.load(f)
            except (OSError, ValueError):
                ov = {}
            ov[short] = num
            tmp = os.path.join(_BASE_DIR, "bot_overrides.json.tmp")
            with open(tmp, "w", encoding="utf-8") as f:
                json.dump(ov, f, indent=1)
            os.replace(tmp, os.path.join(_BASE_DIR, "bot_overrides.json"))
            globals()[short] = num                # apply now where the bot re-reads at runtime
            activity(f"ADMIN set {short} = {num} (restart bot to fully apply)", "CFG")
            return {"ok": True, "needs_restart": True}
        if owner == "votemap":
            if key not in _VOTEMAP_DEFAULTS:
                activity(f"settings: unknown votemap setting {key}", "!")
                return {"ok": False, "error": "unknown votemap setting"}
            if set_votemap_cfg(key, val):
                activity(f"Votemap: {key} = {val}", "CFG")
                return {"ok": True, "needs_restart": False}
            return {"ok": False, "error": "invalid votemap value"}
        if owner == "game":
            if key == "DedicatedServerConfig.VoteKick":   # legacy toggle: routed via run.bat --set-votekick
                on = val.lower() in ("1", "true", "on", "yes")
                try:
                    subprocess.Popen(["cmd", "/c", os.path.join(_BASE_DIR, "run.bat"),
                                      "--set-votekick", "on" if on else "off"], cwd=_BASE_DIR,
                                     creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
                except OSError as e:
                    return {"ok": False, "error": str(e)}
                activity(f"ADMIN set in-game VoteKick = {'on' if on else 'off'}", "CFG")
                return {"ok": True, "needs_restart": True}
            if key in _SRVCFG_MAP:                        # DedicatedServerConfig field (PostMissionDelay etc.):
                r = set_server_config(key, val)           # full pipeline — create-missing-key, atomic write,
                if isinstance(r, dict) and r.get("ok"):   # verify-after-write, reload-config, panel mirror
                    activity(f"ADMIN set server-config {key} = {val}", "CFG")
                else:
                    activity(f"settings: server-config {key} = {val} REJECTED ({(r or {}).get('error', '?')})", "!")
                return r if isinstance(r, dict) else {"ok": False, "error": "set_server_config failed"}
            activity(f"settings: unknown game setting {key}", "!")
            return {"ok": False, "error": "unknown game setting"}
    except Exception as e:                        # noqa: BLE001
        activity(f"settings change failed: {e}", "!")
        return {"ok": False, "error": str(e)}
    return {"ok": False, "error": "unknown owner"}


def _drop_plugin_cmd(body: str):
    """Atomically drop a plugin_cmd_<id>.txt for the NukeStats plugin to consume.
    Uses the persistent bot SFTP session (no fresh handshake per whisper/command)."""
    global _plugin_cmd_id
    cid = int(time.time() * 1000)
    if cid <= _plugin_cmd_id:
        cid = _plugin_cmd_id + 1
    _plugin_cmd_id = cid
    tmp, final = f"plugin_cmd_{cid}.tmp", f"plugin_cmd_{cid}.txt"
    # FONT GUARD, second choke-point: the plugin 'tell' verb (tell_player / !help / kick
    # notices) is the one player-visible path that does NOT go through RemoteCommand.send.
    # font_safe touches typographic punctuation only, so the \x1f and U+2028 body separators
    # and the ASCII verb/SteamID fields all pass through untouched.
    payload = (font_safe(body).rstrip("\n") + "\n").encode("utf-8")

    def _w(sftp):
        with sftp.open(tmp, "wb") as f:
            f.write(payload)
        try:
            sftp.rename(tmp, final)
        except OSError:
            try: sftp.remove(final)
            except OSError: pass
            sftp.rename(tmp, final)
    _sftp_op(_w)


# Whisper delivery. The plugin 'tell' command (private per-player message) is NOT
# delivering reliably on v0.7.4 (the bot drops it + the plugin logs "[cmd] recv: tell"
# with no error, but TellPlayer's RpcTargetServerMessage no-ops from the poll context -
# likely because the plugin enumerates players via FindObjectsOfType instead of the
# game's UnitRegistry.playerLookup, so p.Owner is null and the send is skipped). Until a
# plugin fix is built + verified (needs a server restart), replies go to ALL-CHAT, which
# is proven to work. These are on-demand command replies (only sent when a player types a
# command) so all-chat isn't spammy. Flip to True once the v0.7.5 'tell' fix is live.
# 2026-07-31: ON. The plugin 'tell' verb is the same path !spec confirmations, rank-ups and the
# plugin's own !help already use, and has rendered reliably since 1.1.30 (the old "doesn't
# render" note described the Unity fake-null Instance bug, fixed then). It has to be on now:
# since 1.3.15 the plugin HIDES the '!' line that asked, so an all-chat reply arrives with
# nobody's name against it - a worse outcome than either end state.
WHISPER_VIA_TELL = True


def _log_pm_lines(nm, parts):
    """Owner 2026-08-15: whispers appear in the activity feed UNSUMMARISED - one "PM"-tagged entry
    per whisper line (so webcc can filter on the tag), with ALL <...> markup stripped to plain text
    and each entry capped at 200 chars. Called strictly AFTER a successful delivery; activity()
    never raises, so logging can never affect delivery. Lines that are empty once the markup is
    gone (spacers/pure-markup) are skipped rather than logged as blank feed entries."""
    for l in parts:
        plain = re.sub(r"<[^>]*>", "", str(l)).strip()
        if plain:
            activity(f"-> {nm}: {plain[:200]}", "PM")


def whisper(rc, sid, *lines):
    """Reply to a player's command. Private via the plugin 'tell' command when
    WHISPER_VIA_TELL is on (and it's verified working); otherwise all-chat (reliable)."""
    sid = str(sid or "").replace("|", "")
    # tell-channel bodies bypass RCON send() -> apply the same placeholder-name guard here
    parts = [chat_name_safe(str(l).replace("|", "/")) for l in lines if l is not None]
    if not parts:
        return
    nm = PLAYER_NAMES.get(sid) or RANK_DATA.get(sid, {}).get("name") or sid or "?"
    if WHISPER_VIA_TELL and sid:
        try:
            _drop_plugin_cmd("tell|" + sid + "|" + "\u2028".join(parts))
        except Exception as e:                       # noqa: BLE001
            activity(f"whisper relay failed ({e}) - falling back to chat", "!")
        else:
            _log_pm_lines(nm, parts)                 # per-line, unsummarised (owner 2026-08-15)
            return
    # The all-chat fallback is PUBLIC, not a whisper: it keeps the single summarised line
    # (per-line feed entries are for private sends only - broadcasts stay summarised).
    for l in parts:
        rc.send("send-chat-message", l)   # send directly (not rc.say) so it doesn't also log a
    summary = _strip_color(parts[0])[:50]                    # [BOT] line per chat line -
    extra = f"  (+{len(parts) - 1} lines)" if len(parts) > 1 else ""
    activity(f"replied to {nm}: {summary}{extra}", "CHAT")   # the reply logs ONCE, here


def broadcast(rc, lines, label):
    """Post several lines to ALL-CHAT as one logical message, logging a SINGLE compact activity
    summary ('<label> - sent +N lines to server') instead of one [BOT] line per line (keeps the
    webcc activity feed readable for big posts like the leaderboard / !help)."""
    parts = [str(l).replace("|", "/") for l in lines if l is not None]
    if not parts:
        return
    for l in parts:
        rc.send("send-chat-message", l)   # send directly (not rc.say) so each line doesn't log
    activity(f"{label} - sent +{len(parts)} lines to server", "BOT")


def tell_player(sid, *lines):
    """Send a PRIVATE (client-side) reply to ONE player via the plugin's TellPlayer (the 'tell' verb) --
    the same mechanism !spec / team-moves use, so only that player sees it. NO all-chat fallback: the whole
    point is to keep long/noisy replies (e.g. !help) out of public chat. The asker must be online (plugin
    commands need a player present), which they are when they just typed the command.
    Lines are joined with U+2028 (LINE SEPARATOR) into ONE message, NOT \\x1f-split into many: the plugin
    splits the body on \\x1f and sends one RpcTargetServerMessage per piece, and a rapid 12-message burst
    didn't render -- a single message does. U+2028 survives the file command-channel (File.ReadAllLines
    only breaks on \\r/\\n) and renders as a line break client-side, so the whole reply arrives as one
    multi-line message with the colours intact."""
    sid = str(sid or "").replace("|", "")
    # tell-channel bodies bypass RCON send() -> apply the same placeholder-name guard here
    parts = [chat_name_safe(str(l).replace("|", "/")) for l in lines if l is not None]
    if not sid or not parts:
        return
    nm = PLAYER_NAMES.get(sid) or RANK_DATA.get(sid, {}).get("name") or sid
    # Guarded like whisper(): _sftp_op re-raises after one reconnect attempt, and this is reached from the
    # bare WELCOME_QUEUE drain inside main()'s loop. Unguarded, a relay outage during a join unwound all of
    # main() - losing an in-flight map vote and every other main()-local - and the self-heal restarted it,
    # looping for as long as the outage lasted. A dropped private line is the correct casualty here.
    try:
        _drop_plugin_cmd("tell|" + sid + "|" + "\u2028".join(parts))
    except Exception as e:  # noqa: BLE001 - never let a relay outage kill the bot
        activity(f"private reply to {nm} DROPPED (relay down: {e})", "BOT")
        return False
    _log_pm_lines(nm, parts)                         # per-line, unsummarised (owner 2026-08-15)
    return True


def _plugin_ver_tuple(ver: str):
    """Parse '1.1.4' / '1.1' -> (1,1,4) for comparisons. Unknown/empty -> (0,0,0)."""
    parts = []
    for p in re.findall(r"\d+", str(ver or "")):
        try:
            parts.append(int(p))
        except ValueError:
            break
        if len(parts) >= 3:
            break
    while len(parts) < 3:
        parts.append(0)
    return tuple(parts[:3])


def balance_lines():
    """TWIN of the plugin's !autobalance explainer (ExplainAutobalance). Change one, change both - they
    are the same promise to the same players, and a stale twin here reads as a lie in chat.

    Says nothing about the AIRBORNE case on purpose: that branches on Balance.MoveOnlyWhenGrounded, a
    plugin setting the bot cannot read reliably (the live cfg file is a partial container, so an absent
    key proves nothing). The plugin knows the live value; it owns that sentence. We point players there.
    """
    return [
        "<color=#FFD200>=== TEAM BALANCING (PvP) ===</color>",
        "Teams are kept even. Join the side that <color=#FFC857>already has more players</color> and you go straight to spectator - no warning - so pick the smaller team.",
        "To switch sides yourself: type <color=#36FFD0>!swapteam</color> to move to the smaller team (you keep your points). It only works if the other team has fewer players.",
        "If someone <color=#FFC857>leaving</color> unbalances the teams, everyone is warned first. If it hasn't evened out by the end of that warning, ONE player is switched to the smaller side - not to spectator - and keeps their points.",
        "Full detail - including the top-scorer rule and what happens if you're airborne: <color=#55FF55>!autobalance</color>",
    ]


def prestige_explain_lines(steamid):
    """Brief how-prestige-works whisper. Matches do_prestige: cycle pts >= prestige_top(), !yes confirm,
    displayed rank resets / star gained; lifetime points + W/L kept."""
    cyc = cycle_points(steamid)
    lines = [
        "<color=#FFD700>=== PRESTIGE ===</color>",
        f"Reach the top rank's points in your current cycle (<color=#55FF55>{_pts(prestige_top())}</color>), "
        f"then confirm with <color=#55FF55>!yes</color> within {PRESTIGE_CONFIRM_WINDOW}s.",
        "Your <color=#FFC857>displayed rank</color> resets to a fresh cycle and you gain a star on your tag. "
        "Lifetime points and wins/losses are kept.",
    ]
    if cyc < prestige_top():
        lines.append(f"<color=#FFC857>Not eligible yet</color> - {_pts(max(0.0, prestige_top() - cyc))} more needed.")
    else:
        lines.append(f"<color=#FFD700>You're eligible!</color> Type <color=#55FF55>!yes</color> "
                     f"to confirm ({PRESTIGE_CONFIRM_WINDOW}s).")
    return lines


def spectator_tip_lines(pvp=False):
    # PvE: no spectator tip at all. PvP: only the longer team-balance message.
    if not pvp:
        return []
    return ["On the bigger team? Type <color=#36FFD0>!swapteam</color> to switch to the smaller side instantly - you keep your points and progress."]


def leaderboard_lines(steamid=None):
    """Server-rank leaderboard as chat lines. With steamid (the !leaderboard asker) it LEADS with that
    player's own position + who's right above them, then the Top-5 by points.
    Without steamid (the 30-min auto-post) it's just the top list."""
    out = []
    # With cross-server sharing ON, rank the COMBINED board (this server + the host's other servers)
    # so the in-game leaderboard AGREES with !rank and the baked name tag, and peer-only players show.
    # Consistent with the webcc leaderboard (both read the same aggregate).
    src = RANK_DATA
    if SHARED_RANKS_ENABLED:
        try:
            agg = read_aggregate_ranks()               # {sid: {name, points, wins, losses}} summed across servers
            if agg:
                src = agg
        except Exception:                              # noqa: BLE001 - a leaderboard must never raise
            src = RANK_DATA
    pts_board = [(s, r) for s, r in src.items() if r.get("points", 0) > 0]
    pts_board.sort(key=lambda kv: kv[1].get("points", 0), reverse=True)

    if steamid is not None:                            # personalized header for the asker
        rec = src.get(steamid)
        mypts = rec.get("points", 0) if rec else 0
        idx = next((i for i, (s, _) in enumerate(pts_board) if s == steamid), None)
        if idx is None or mypts <= 0:
            out.append("<color=#FFD200>Server rank:</color> you're unranked - score points to get on the leaderboard!")
        else:
            def _tag_span(pts_v, sid_v):
                """'<color=..>[ABBR]</color> ' for the player's tier - '' while the ladder is off."""
                if not RANKS:
                    return ""
                _, nm_v, ab_v, co_v = RANKS[rank_index_for(max(0.0, pts_v - prestige_base(sid_v)))]
                return f"<color={co_v}>{prestige_tag_inner(ab_v, nm_v, prestige_count(sid_v))}</color> "
            line = (f"<color=#FFD200>Your server rank: #{idx + 1} of {len(pts_board)}</color> - "
                    f"{_tag_span(mypts, steamid)}{_pts(mypts)}.")
            if idx > 0:
                asid, arec = pts_board[idx - 1]
                apts = arec.get("points", 0)
                line += (f"  Above you: {_tag_span(apts, asid)}"
                         f"{arec.get('name', asid)} - {_pts(apts)} (+{_pts(apts - mypts)} to pass).")
            else:
                line += "  <color=#FFD200>You're #1 on the server!</color>"
            out.append(line)

    if not pts_board:
        return out or ["<color=#FFD200>Leaderboard:</color> no ranked pilots yet - score points "
                       "to get on the board!"]
    out.append("<color=#FFD200>=== TOP 5 BY POINTS (server rank) ===</color>")
    for i, (sid_b, rec) in enumerate(pts_board[:5], 1):
        bpts = rec.get("points", 0)
        if RANKS:
            _, bname, babbr, bcolor = RANKS[rank_index_for(max(0.0, bpts - prestige_base(sid_b)))]
            btag = f"<color={bcolor}>{prestige_tag_inner(babbr, bname, prestige_count(sid_b))}</color> "
        else:                                              # ladder off -> plain name + points
            btag = ""
        out.append(f"  {i}. {btag}{rec.get('name', sid_b)} - {_pts(bpts)}")
    return out


_BOT_MUTEX_HANDLE = None
_BOT_LOCK_FH = None


def acquire_bot_singleton():
    """Exclusive singleton for this install folder. Prevents DUP-BOT (two bots writing ranks /
    replaying grants). On Windows uses a Local\\ mutex keyed by absolute base dir + bot.lock PID
    marker; elsewhere uses an exclusive flock on bot.lock. Refuses a second start (exit 7)."""
    global _BOT_MUTEX_HANDLE, _BOT_LOCK_FH
    if _BOT_MUTEX_HANDLE is not None or _BOT_LOCK_FH is not None:
        return
    lock_path = os.path.join(_BASE_DIR, "bot.lock")
    abs_base = os.path.abspath(_BASE_DIR)
    if sys.platform == "win32":
        import ctypes
        # Named mutex keyed by folder — survives orphaned bot.lock after a crash.
        safe = re.sub(r"[^A-Za-z0-9_]", "_", abs_base)
        if len(safe) > 180:
            safe = safe[:90] + "_" + safe[-89:]
        name = "Local\\NukeOptionBot_" + safe
        kernel32 = ctypes.windll.kernel32
        handle = kernel32.CreateMutexW(None, True, name)
        ERROR_ALREADY_EXISTS = 183
        if not handle or kernel32.GetLastError() == ERROR_ALREADY_EXISTS:
            if handle:
                kernel32.CloseHandle(handle)
            print(f"[bot] REFUSING start: another bot already holds the singleton lock for:\n  {abs_base}")
            print("[bot] (DUP-BOT hazard — only one bot per server folder). Exit 7.")
            sys.exit(7)
        _BOT_MUTEX_HANDLE = handle
        try:
            with open(lock_path, "w", encoding="utf-8") as f:
                f.write(f"{os.getpid()}\n{abs_base}\n{time.strftime('%Y-%m-%d %H:%M:%S')}\n")
        except OSError:
            pass
        return
    # POSIX: exclusive non-blocking flock
    try:
        import fcntl  # type: ignore
    except ImportError:
        return
    _BOT_LOCK_FH = open(lock_path, "a+", encoding="utf-8")
    try:
        fcntl.flock(_BOT_LOCK_FH.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
    except (BlockingIOError, OSError):
        print(f"[bot] REFUSING start: another bot already holds bot.lock for:\n  {abs_base}")
        sys.exit(7)
    _BOT_LOCK_FH.seek(0)
    _BOT_LOCK_FH.truncate()
    _BOT_LOCK_FH.write(f"{os.getpid()}\n{abs_base}\n")
    _BOT_LOCK_FH.flush()


def main():
    global _LAST_CONSOLE_AT
    global CURRENT_MISSION
    acquire_bot_singleton()
    # Claim presence publishing for THIS process. cc_web imports this module, which starts the
    # shared-ranks daemon there too - so without the claim both processes would publish the same
    # presence file. The bot is the right owner: it is the one that maintains dashboard_state.json.
    _IS_BOT_PROCESS[0] = True
    rc = RemoteCommand(RCMD_HOST, RCMD_PORT)
    if LOCAL_CONSOLE_PATH:
        print("[bot] local console mode: tailing " + LOCAL_CONSOLE_PATH + " ; commands -> %s:%d" % (RCMD_HOST, RCMD_PORT))
        console = ConsoleSource(LOCAL_CONSOLE_PATH)
    else:
        console = SFTPConsoleSource(SFTP_HOST, SFTP_PORT, SFTP_USER, SFTP_PASS, SFTP_LOG_PATH)

    # Settings-menu accuracy across restarts (Tomo 2026-07-05): seed the last-known plugin cfg from
    # disk so the webcc never flashes catalogue defaults. A fresh dumpcfg is requested ~10s into the
    # main loop (NOT here): the reply frame arrives via the console tail, which only attaches seconds
    # after startup and seeks to end-of-file - a request dropped now would be answered before we're
    # listening and the reply silently missed.
    load_plugin_cfg_cache()
    startup_dumpcfg_due = time.time() + 10.0

    # Mission audit at startup (2026-07-31). _mission_audit_cache starts {"loaded": False} and was only
    # ever filled by an operator opening the Mission Pool modal / writing MissionRotation / uploading a
    # mission. _enabled_custom_names() reads that cache, so after EVERY restart the bot was blind to all
    # custom missions until a human happened to open the panel - which meant the time-of-day PvP variants
    # could not reach a ballot at all and the family draw collapsed to its 4 bare base names. Scheduled
    # like the dumpcfg above (after the console tail attaches) and RETRIED, because this one does SFTP and
    # a startup scan lands squarely in the known relay auth-fail window.
    startup_audit_due = time.time() + 12.0
    audit_retry_s = 30.0

    state = "IDLE"               # IDLE -> APPROVAL (!votemap) or VOTING (map ballot)
    votes = {}                   # steamid -> map option key
    first_vote_at = {}           # option key -> time of its first vote (tie-breaker)
    vote_ends_at = 0
    # Incremented every time the GAME SERVER is detected to have restarted (see the roster-poll
    # reconnect handler). A ballot records the generation it opened under, so a vote whose session died
    # underneath it can be recognised and abandoned instead of applied to the freshly booted match.
    server_generation = 0
    vote_generation = 0
    active_vote_duration = vote_duration()   # FIX 3: single ballot length for both vote kinds
    vote_context = "mission_end" # what triggered the current map vote
    vote_reminder_sent = False   # once-per-vote T-15s ballot rebroadcast (skip if duration ≤15s)
    approvals = {}               # steamid -> bool (yes/no) during a !votemap poll
    approval_ends_at = 0
    approval_threshold = 0
    approval_players = 0
    cooldown_until = 0              # anti-spam gate for player-initiated !votemap only
    suppress_mission_end_until = 0  # swallow the self-induced "Mission complete" after a !votemap switch
    vote_verify = None              # post-vote apply check: never changes the live mission for mission-end votes
    last_loop_at = time.time()      # watchdog: expose main-loop stalls (blocking I/O) in the log
    last_console_poll = 0
    last_mission_end_at = 0     # to ignore the duplicate "Mission complete" burst
    last_rank_shown = {}        # steamid -> time, to throttle per-chat rank lines
    last_namesync = 0           # last refresh of the player-name cache
    known_online = set()        # steamids seen online last poll (for join announces)
    seeded_online = False       # skip the first poll so we don't "welcome" everyone
    server_up = True            # game live: RCMD OK OR recent console/NOSTATS (not RCMD-alone)
    rcmd_up = True              # remote-command relay specifically reachable
    _down_since = 0.0           # when the relay went down (>=20s down + reconnect => real restart, see srvcfg check)
    last_season_at = time.time() - 1200   # season notice: first post ~5 min after boot, not instantly
    last_thanks_at = time.time()  # last "thanks for playing" message (+10min)
    last_leaderboard_at = time.time()  # last auto leaderboard post (+30min during a match)
    last_spectip_at = time.time()      # last spectator/team-switch tip (+12min)
    # Start the peer-server line HALF an interval in, not a full one: on a fresh boot the first useful
    # thing to tell a lone player is that the other server is busy - waiting 20 min misses them.
    last_otherserver_at = time.time() - OTHERSERVER_INTERVAL / 2
    last_ranks_flush = 0.0        # last coalesced ranks.json write (time-played accrual)
    last_rank_push = 0.0          # last push of plugin_ranks.txt to the container
    last_state_write = 0.0        # last dashboard_state.json write (command-centre feed)
    last_mtime_poll = 0.0         # last get-mission-time poll (for the dashboard header)
    last_mirror_trim = time.time()  # last console_mirror.log size check
    mtime = [0, 0, 0]             # cached (currentTime, maxTime, fetched_at) for the dashboard

    load_ranks()
    apply_pending_adjust()       # one-shot queued corrections (see the file's docstring for why)

    # seed the current mission name (best effort) so the first match record is labelled
    refresh_current_mission(rc)

    # FIX 4: pin the boot map game-side (rotation[0] + Sequence) as soon as the bot is up, so the very
    # next server boot loads it even if the file was re-templated while the bot was down.
    try:
        apply_boot_map_rotation("bot startup")
    except Exception as _e:                        # noqa: BLE001
        print(f"[boot-map] startup pin failed: {_e}")
    # FIX 3: read DedicatedServerConfig once at startup, then derive + push PostMissionDelay = vote + delay
    # so the server's real rotation timing always matches the two knobs (self-heals a stale/egg-default PMD
    # left by a re-templating boot). Best-effort: a failure here never blocks the bot from starting.
    try:
        refresh_server_config()
        sync_effective_pmd()
    except Exception as _e:                    # noqa: BLE001
        print(f"[srvcfg] startup PostMissionDelay sync skipped: {_e}")

    # Resume the admin queue from the PERSISTED offset so commands queued during a bot restart are
    # no longer silently discarded (the old skip-to-EOF lost every click in a restart window — the
    # bot restarts many times a day). A per-command 15-min staleness guard in process_admin_commands
    # keeps genuinely old lines from replaying if the offset file is lost/stale.
    global _admin_cmd_offset
    try:
        size_now = os.path.getsize(ADMIN_CMD_FILE)
    except OSError:
        size_now = 0
    _admin_cmd_offset = size_now
    try:
        with open(ADMIN_CMD_OFFSET_FILE, encoding="utf-8") as _of:
            saved = int(_of.read().strip() or 0)
        if 0 <= saved <= size_now:
            _admin_cmd_offset = saved
            if saved < size_now:
                print(f"[admin] resuming queue from saved offset {saved} (catching up {size_now - saved} bytes queued while the bot was down)")
    except (OSError, ValueError):
        pass

    def open_map_vote(context):
        nonlocal votes, first_vote_at, vote_ends_at, active_vote_duration, vote_context, state
        nonlocal vote_reminder_sent, vote_generation
        vote_generation = server_generation      # the session this ballot belongs to
        votes = {}
        first_vote_at = {}
        open_vote(len(known_online))   # build a fresh ballot (force_pvp uses the live player count)
        active_vote_duration = vote_duration()   # FIX 3: same length for the !votemap and mission-end ballots
        vote_reminder_sent = False               # allow one T-15s rebroadcast for this ballot
        announce_options(rc, active_vote_duration)
        vote_ends_at = time.time() + active_vote_duration
        vote_context = context
        state = "VOTING"
        activity(f"Map vote open for {active_vote_duration}s - {len(VOTE_OPTIONS)} maps on the ballot "
                 f"(players type !1-!{len(VOTE_OPTIONS)})", "VOTE")

    print("[bot] running. Ctrl-C to stop.")
    activity("====== Bot started - watching the server ======")
    while True:
        now = time.time()
        # Watchdog: the loop is single-threaded with blocking I/O (relay TCP, SFTP tail). A stall
        # here delays EVERY deadline (2026-07-05: a 31s stall applied a map vote 24s late and the
        # server rotation loaded the wrong map). Make stalls loud so the blocking call is findable.
        if now - last_loop_at > 5:
            print(f"[watchdog] main loop stalled {now - last_loop_at:.1f}s (a blocking call ran long)")
        last_loop_at = now

        # --- one-shot startup dumpcfg, AFTER the console tail is attached (see main() top): the
        #     plugin's Ticker answers even on an empty server; the cfg frame refreshes the persisted
        #     cache so the webcc settings menu always shows real values, never catalogue defaults. ---
        if startup_dumpcfg_due and now >= startup_dumpcfg_due:
            startup_dumpcfg_due = 0
            try:
                _drop_plugin_cmd("dumpcfg")
                print("[cfg-cache] startup dumpcfg requested (fresh settings snapshot)")
            except Exception as e:  # noqa: BLE001 - best-effort; the next setcfg/Awake dump also refreshes
                print(f"[cfg-cache] startup dumpcfg skipped: {e}")

        # --- mission audit: fill _mission_audit_cache so custom/variant missions are ballot-eligible
        #     WITHOUT an operator opening the Mission Pool modal first. Retries on failure (SFTP may be
        #     down at restart) and then refreshes slowly so a mission added on the container is picked up.
        if startup_audit_due and now >= startup_audit_due:
            try:
                # deep=False: list + classify only. A deep scan sha256s every official mission, which
                # downloads ~15 MB inside this single-threaded loop - it would stall the console tail,
                # the vote timer and the roster poll for the whole transfer, every 15 minutes. The
                # integrity check stays on the operator-triggered scan, where a pause is expected.
                _a = refresh_mission_audit(deep=False)
                if _a.get("error"):
                    startup_audit_due = now + audit_retry_s      # relay down - try again shortly
                    audit_retry_s = min(audit_retry_s * 2, 300.0)
                    print(f"[mission-audit] startup scan failed ({_a['error']}); retrying in "
                          f"{int(startup_audit_due - now)}s")
                else:
                    startup_audit_due = now + 900.0              # settled: slow refresh every 15 min
                    audit_retry_s = 30.0
                    print(f"[mission-audit] startup scan ok: {len(_a.get('official') or [])} official, "
                          f"{len(_a.get('unofficial') or [])} custom -> "
                          f"{len(_enabled_custom_names())} ballot-eligible")
            except Exception as e:  # noqa: BLE001 - never let a relay outage kill main()
                startup_audit_due = now + audit_retry_s
                audit_retry_s = min(audit_retry_s * 2, 300.0)
                print(f"[mission-audit] startup scan error: {e}; retrying in "
                      f"{int(startup_audit_due - now)}s")

        # --- drain delayed welcomes (deadline-based; runs every loop tick) ---
        # RATE-LIMITED (audit 2026-08-01). Each welcome is a BLOCKING relay round trip plus a full
        # ranks.json save, and this used to drain every due entry in a single pass: a match start where
        # 20 players connect together stalled the loop for 20 serial SFTP round trips - and if the relay
        # was down, each one first burned two 15s SSH connect timeouts. The console tail, the vote timer
        # and the roster poll all wait behind that. Cap the work per tick and give up on a player after
        # WELCOME_MAX_ATTEMPTS so a persistent outage cannot retry them forever.
        if WELCOME_QUEUE:
            due = sorted([s for s, v in WELCOME_QUEUE.items() if now >= v[0]],
                         key=lambda s: WELCOME_QUEUE[s][0])[:WELCOME_DRAIN_PER_TICK]
            for sid_w in due:
                entry = WELCOME_QUEUE.pop(sid_w)
                _dl, nm_w = entry[0], entry[1]
                tries = entry[2] if len(entry) > 2 else 0
                if sid_w in ROSTER_BY_SID and sid_w not in WELCOMED:
                    # re-resolve at SEND time: the Steam persona often lands during the
                    # ~5s welcome delay - never announce a sid/'ID: ...' placeholder when
                    # a real name is available now. Falls back to the queued name.
                    nm_now = (_storable_name(sid_w, PLAYER_NAMES.get(sid_w))
                              or _storable_name(sid_w, RANK_DATA.get(sid_w, {}).get("name"))
                              or nm_w)
                    if say_welcome(rc, sid_w, nm_now) is False:
                        # relay down - retry, but only a bounded number of times. Also bounded by
                        # ROSTER_BY_SID: once they leave, the entry stops being drained entirely.
                        if tries + 1 >= WELCOME_MAX_ATTEMPTS:
                            WELCOMED.add(sid_w)          # stop trying; a missed greeting is not worth a stall
                            activity(f"welcome for {nm_now} given up after "
                                     f"{WELCOME_MAX_ATTEMPTS} relay failures", "BOT")
                        else:
                            WELCOME_QUEUE[sid_w] = (now + 30.0, nm_now, tries + 1)

        if now - last_console_poll >= CONSOLE_POLL_INTERVAL:
            last_console_poll = now
            lines = console.poll()
            # mirror the whole batch in one write so the command centre can show the
            # live server/BepInEx console. Done BEFORE parsing so an unhandled parse
            # error can't drop a cycle's mirror lines (best-effort; never affects parsing).
            mirror_console_batch(lines)
            if lines:
                _LAST_CONSOLE_AT = now
            for line in lines:
                # SECURITY GATE (2026-07-31 audit). A player's chat message becomes a console line, and
                # every frame parser below uses .search() ANYWHERE in the line - so typing a
                # "[NOSTATS] {...}" frame into chat had the bot parse it as though the PLUGIN emitted
                # it: arbitrary score grants, forged match ends, forged moderation. 1.3.15 made it
                # worse by hiding '!' commands, so the forged line need not be seen by anyone at all.
                # A chat line is DATA, never telemetry: the parsers below read `scan`, which is blank
                # for chat. `line` itself is untouched, so the chat/vote handling further down is
                # unaffected.
                scan = "" if CHAT_RE.search(line) else line
                if "NukeStats" in scan:
                    note_plugin_version(scan)
                # real per-player score from the NukeStats plugin (frequent; handle
                # first and skip). Inert until the plugin is emitting these lines.
                ns = NOSTATS_RE.search(scan)
                if ns:
                    try:
                        handle_stats_line(rc, json.loads(ns.group(1)))
                    except Exception:  # noqa: BLE001 - STABLE-AUDIT fix: a malformed frame (e.g. a "pos"
                        # whose p isn't a list of dicts -> AttributeError) escaped the old narrow
                        # (ValueError, TypeError) guard and crash-restarted the whole poll loop every ~2s
                        # on a version-mismatched plugin. The loop must NEVER die on one bad line -
                        # but it must also never die SILENTLY: print the traceback (throttled).
                        global _FRAME_ERR_AT
                        if time.time() - _FRAME_ERR_AT > 30:
                            _FRAME_ERR_AT = time.time()
                            print("[frames] handler error (line skipped, loop continues):")
                            traceback.print_exc()
                    continue
                # a mission ending -> finalize the just-ended match in ANY state, so a
                # mission that ends mid-vote still gets its own record and can't bleed
                # into the next one; then, only when idle, show ranks + open the vote.
                # Deduped so the duplicate "Mission complete" burst doesn't re-fire.
                if MISSION_END_RE.search(scan):
                    EMPTY_FORCED_CUT["pending"] = False   # the rollover arrived on its own - no re-fire needed
                    if now - last_mission_end_at > 15:
                        last_mission_end_at = now
                        roster = get_players(rc)
                        match_finalize(rc, roster)   # close + persist the match that just ended
                        try:
                            fire_event_messages(rc, "match_end")   # owner match_end messages
                        except Exception as e:        # noqa: BLE001
                            print(f"[servermsg] match_end error: {e}")
                        # ANNOUNCE+FLAG leg of match-end deploys: if an update is staged, warn
                        # the players and drop matchend_deploy.flag for the watchdog leg.
                        # Fully self-guarded - any failure in there is silent and match-end
                        # processing (including the vote below) continues untouched.
                        matchend_deploy_notify(rc, roster)
                        # A mission ending is the PRIMARY trigger for the next-map vote, so it
                        # must NOT be blocked by the !votemap anti-spam cooldown (that gates only
                        # player-initiated votes). The only mission-ends we skip are the forced cut
                        # we caused ourselves right after a !votemap switch, and one that arrives
                        # while a map vote is already running.
                        if state != "VOTING" and now >= suppress_mission_end_until:
                            # 2026-07-29: the end-of-mission rank ROSTER is DELETED, not relocated.
                            # It used to be spoken in chat; moving it to the activity feed was not what
                            # was asked for either - it is gone. Match end says exactly: the top 3 with
                            # their points, and which side won. Nothing per-player beyond that.
                            if _votemap_cfg()["enabled"]:
                                activity("Mission ended - showing ranks, opening the map vote", "MAP")
                                open_map_vote("mission_end")
                                print("[bot] mission complete detected -> roster + vote opened")
                            else:
                                activity("Mission ended - map voting is OFF; the server rotation picks the next map", "MAP")
                                print("[bot] mission complete -> votemap disabled; server rotation advances")
                        else:
                            why = ("a map vote is already in progress" if state == "VOTING"
                                   else "just switched via !votemap")
                            activity(f"Mission ended ({why}) - no new vote opened", "MAP")
                            print(f"[bot] mission complete detected -> vote skipped ({why})")
                    continue

                parsed = parse_chat_line(line)
                if not parsed:
                    continue
                steamid = parsed["steamid"]
                text = parsed["message"].strip()
                low = text.lower()

                # show what each player typed (messages, commands, votes); flag the admin
                if LOG_CONVERSATION and text:
                    who = (PLAYER_NAMES.get(steamid)
                           or RANK_DATA.get(steamid, {}).get("name") or steamid)
                    if steamid in ADMIN_SIDS:
                        activity(f"[ADMIN] {who}: {text}", "!")     # stands out in the activity feed
                    else:
                        activity(f"{who}: {text}", "CHAT")

                # a player checks their rank (detailed breakdown; prestige-aware)
                if low == "!rank":
                    cyc = cycle_points(steamid)                       # rank tier from CYCLE points
                    total = player_points(steamid)
                    pcount = prestige_count(steamid)
                    nm = (PLAYER_NAMES.get(steamid)
                          or RANK_DATA.get(steamid, {}).get("name") or "Pilot")
                    if not RANKS:                                     # ladder off -> points only
                        whisper(rc, steamid,
                                f"<color=#FFD200>{nm}:</color> {_pts(total)} "
                                "<color=#9FB0C4>(this server has no rank ladder configured)</color>")
                        continue
                    label, color, tail = rank_progress(cyc, pcount)
                    extra = ""
                    if cyc >= prestige_top():
                        extra = "  <color=#FFD700>You can !prestige!</color>"
                    elif rank_index_for(cyc) == len(RANKS) - 1:       # at the top rank -> the next goal is prestige
                        extra = f"  ({_pts(max(0.0, prestige_top() - cyc))} to prestige)"
                    if pcount > 0:                                    # lifetime total carries across cycles
                        extra += f"  <color=#FFD700>[Prestige {pcount}]</color> (lifetime {_pts(total)})"
                    whisper(rc, steamid, f"<color={color}>{label}</color> - {nm}: "
                            f"{_pts(cyc)} ({tail}){extra}")
                    continue

                # !ranks - the whole ladder, privately. Built from the LIVE RANKS table so editing the
                # ladder in the Web CC changes this with no code change. The player's current tier is
                # marked, so one command answers both "what are the ranks" and "where am I".
                # !stats - everything we know about the asker, privately, in three lines. Same data as
                # the Web CC player card, one source of truth (player_stat_card).
                if low in ("!stats", "!stat"):
                    try:
                        c = player_stat_card(steamid)
                    except Exception as e:        # noqa: BLE001 - chat must never raise
                        print(f"[stats] card failed for {steamid}: {e}")
                        whisper(rc, steamid, "<color=#FF8C69>Couldn't read your stats just now.</color>")
                        continue
                    _rank_span = f"<color={c['colour']}>{c['rank_label']}</color> " if c["rank_label"] else ""
                    if c["next_label"]:
                        _rank_tail = f"  <color=#CFCFCF>({_pts(c['to_next'])} to {c['next_label']})</color>"
                    elif c["rank_label"]:
                        _rank_tail = "  <color=#36FFD0>(top rank)</color>"
                    else:                                 # rank ladder off -> no rank/tier decoration
                        _rank_tail = ""
                    whisper(
                        rc, steamid,
                        _rank_span
                        + f"<color=#FFFFFF>{c['name']}</color>  <color=#CFCFCF>-</color>  "
                        f"<color=#FFD200>{_pts(c['points'])}</color> <color=#CFCFCF>pts</color>"
                        + _rank_tail,
                        f"<color=#CFCFCF>W/L</color> <color=#5df2a0>{c['wins']}</color>"
                        f"<color=#CFCFCF>/</color><color=#ff9a9a>{c['losses']}</color>"
                        + (f" <color=#CFCFCF>({c['winrate']:.0f}%)</color>" if c["wins"] + c["losses"] else ""),
                        f"<color=#CFCFCF>Time played</color> <color=#FFFFFF>{c['time_played']}</color>"
                        f"  <color=#CFCFCF>-  leaderboard</color> <color=#FFD200>#{c['position']}</color>"
                        f"<color=#CFCFCF> of {c['total']}</color>"
                        + (f"  <color=#CFCFCF>-  prestige</color> <color=#FFD700>{c['prestige']}*</color>"
                           if c["prestige"] else ""))
                    continue

                if low in ("!ranks", "!ladder"):
                    # THREE lines, in the owner's format (2026-07-31): the ladder split across two
                    # arrow lines, then the thresholds. ASCII "->" deliberately - U+2192 renders as a
                    # white square in the game font (he saw exactly that on the rank-up arrow).
                    # Built from the LIVE RANKS table, so editing the ladder in the Web CC changes this
                    # with no code change; the player's own rank is drawn brighter so it stands out.
                    if not RANKS:                                     # ladder off (shipped default)
                        whisper(rc, steamid,
                                "<color=#FFD200>This server has no rank ladder configured.</color> "
                                "<color=#9FB0C4>Points still accrue - try !leaderboard.</color>")
                        continue
                    cyc = cycle_points(steamid)
                    here = rank_index_for(cyc)

                    def _abbr(i):
                        _thr, _nm, ab, col = RANKS[i]
                        return (f"<color=#FFFFFF>{ab}</color>" if i == here
                                else f"<color={col}>{ab}</color>")

                    half = (len(RANKS) + 1) // 2
                    arrow = " <color=#CFCFCF>-></color> "
                    line1 = arrow.join(_abbr(i) for i in range(half))
                    line2 = arrow.join(_abbr(i) for i in range(half, len(RANKS)))
                    points = " <color=#CFCFCF>/</color> ".join(
                        f"<color=#CFCFCF>{_pts(RANKS[i][0])}</color>" for i in range(len(RANKS)))
                    whisper(rc, steamid,
                            f"<color=#FFB800>Ranks</color>   {line1}",
                            f"     <color=#CFCFCF>-></color> {line2}",
                            f"<color=#FFB800>Points</color>  {points}")
                    continue

                # PRESTIGE: brief explainer always; open !yes confirm window when cycle pts >= top threshold.
                if low == "!prestige":
                    if not RANKS:                                     # no ladder -> nothing to prestige through
                        whisper(rc, steamid,
                                "<color=#FFD200>This server has no rank ladder configured, so there "
                                "is nothing to prestige through.</color>")
                        continue
                    cyc = cycle_points(steamid)
                    nm = (PLAYER_NAMES.get(steamid)
                          or RANK_DATA.get(steamid, {}).get("name") or "Pilot")
                    if cyc >= prestige_top():
                        _PRESTIGE_PENDING[steamid] = now + PRESTIGE_CONFIRM_WINDOW
                        activity(f"{nm} is eligible to prestige - asked to confirm", "RANK")
                    whisper(rc, steamid, *prestige_explain_lines(steamid))
                    continue

                # PRESTIGE confirm (within the 60s window)
                if low == "!yes":
                    dl = _PRESTIGE_PENDING.pop(steamid, None)
                    if dl is None or now > dl:
                        if dl is not None:
                            whisper(rc, steamid, "<color=#FFC857>Prestige confirmation expired - type "
                                    "!prestige again.</color>")
                        continue
                    nm = (PLAYER_NAMES.get(steamid)
                          or RANK_DATA.get(steamid, {}).get("name") or "Pilot")
                    new_count = do_prestige(steamid)
                    if new_count is None:
                        whisper(rc, steamid, "<color=#FFC857>You're no longer eligible to prestige.</color>")
                    else:
                        rc.say(f"<color=#FFD700>** PRESTIGE ** {nm} reached Prestige {new_count}!</color> "
                               f"Fresh cycle, one more star - fly on.")
                        activity(f"{nm} PRESTIGED -> Prestige {new_count}", "RANK")
                        _RANK_PUSH_FLAG[0] = True                     # re-bake name tags (reset tier + new star)
                    continue

                # team-kill (friendly fire) policy explainer (private)
                if low == "!notk":
                    whisper(rc, steamid,
                            "<color=#FF5555>=== NO TEAM KILLING ===</color>",
                            "Destroying a FRIENDLY player's aircraft, vehicle or building is friendly fire - it's detected and auto-punished.",
                            "<color=#FFD200>1st</color> time in a match: you're <color=#FF8C00>ejected</color> from your plane with a warning.",
                            "<color=#FFD200>2nd</color> time: <color=#FF8C00>kicked</color> - and if you rejoin, your in-game rank is reset to 0.",
                            "<color=#FFD200>3rd</color> time: <color=#FF0000>banned from the server</color>.",
                            "Counts reset each match. TKs are almost always avoidable - check your targets before firing.")
                    continue

                # how PvP team balancing works (private)
                if low == "!balance":
                    whisper(rc, steamid, *balance_lines())
                    continue

                # Discord invite (private; configured via discord.invite / NO_DISCORD_INVITE)
                if low == "!discord":
                    if DISCORD_INVITE:
                        whisper(rc, steamid,
                                f"<color=#55FF55>Join the Discord - {DISCORD_INVITE}</color> "
                                "<color=#9FB0C4>- community leaderboards + private stat tracking "
                                "(type !link to connect your pilot).</color>")
                    else:
                        whisper(rc, steamid,
                                "<color=#9FB0C4>This server hasn't set up a Discord invite.</color>")
                    continue

                # Discord account link: a short-lived code the Discord bot redeems from the shared dir
                if low == "!link":
                    # A-Z 2-9 minus the lookalikes (no O/0/I/1) - players retype these by hand
                    code = "".join(random.choices("ABCDEFGHJKLMNPQRSTUVWXYZ23456789", k=6))
                    if _save_discord_link_code(code, steamid):
                        _inv = f" ({DISCORD_INVITE})" if DISCORD_INVITE else ""
                        whisper(rc, steamid,
                                f"<color=#55FF55>Your link code: {code}</color>",
                                f"<color=#9FB0C4>Type <color=#FFD200>/link {code}</color> in our Discord"
                                f"{_inv} within 15 minutes.</color>")
                    else:
                        whisper(rc, steamid,
                                "<color=#FFC857>Couldn't save a link code - try again shortly.</color>")
                    continue

                # all-time leaderboard: top 5 by points (private to asker)
                if low == "!leaderboard":
                    whisper(rc, steamid, *leaderboard_lines(steamid))
                    continue

                # why do I have these points? (audit, private)
                if low == "!why":
                    rows = recent_ledger_for(steamid, 4)
                    if not rows:
                        whisper(rc, steamid, "<color=#FFD200>No points logged for you yet.</color>")
                    else:
                        whisper(rc, steamid, "<color=#FFD200>Your recent points:</color>",
                                *[(f"  +{_pts_i(e.get('pts'))}  [{e.get('category','')}] {e.get('reason','')}"
                                   if e.get('pts') else
                                   f"  - [{e.get('category','')}] {e.get('reason','')}") for e in rows])
                    continue

                # lifetime points (private to asker)
                if low == "!points":
                    whisper(rc, steamid,
                            f"<color=#36FFD0>Lifetime points: {_pts_i(player_points(steamid))}</color>")
                    continue

                # !f forfeit bridge: plugin owns HandleForfeit via chat (!forfeit/!ff/!surrender).
                # Bot does NOT relay !forfeit (Harmony chat path). Live <1.1.5 also ignores bare !f.
                # Pending 1.1.5 adds chat "f" + plugin_cmd forfeit|sid; bot maps !f -> forfeit|<sid>
                # so HandleForfeit runs once that verb is loaded (no game restart for the bot half).
                # If live DLL already swallowed !f (1.1.5+ chat alias), this line is usually never
                # reached (Prefix returns false before CmdSendChatMessage logs).
                if low == "!f":
                    nm = (PLAYER_NAMES.get(steamid)
                          or RANK_DATA.get(steamid, {}).get("name") or steamid)
                    try:
                        _drop_plugin_cmd("forfeit|" + steamid)
                        activity(f"mapped !f -> forfeit for {nm} ({steamid})", "CMD")
                    except Exception as e:  # noqa: BLE001
                        activity(f"!f forfeit relay failed ({e})", "!")
                    # Pre-1.1.5 live DLL has no forfeit|sid verb yet — drop is a no-op until morning.
                    # Point the asker at aliases that already work (!forfeit / !ff).
                    if _plugin_ver_tuple(_live_plugin_version()) < (1, 1, 5):
                        whisper(rc, steamid,
                                "<color=#FFC857>Use <color=#55FF55>!forfeit</color> or "
                                "<color=#55FF55>!ff</color> - short !f is armed for the "
                                "next plugin update.</color>")
                    continue

                # Command help. PRIVATE since 1.3.15 - the plugin owns the reply and renders it with
                # TellPlayer, exactly as !spec's confirmation does. The old TODO above ("for now it goes
                # to chat so it WORKS") predates that; leaving it broadcast meant 1.3.15 hid the question
                # a player typed while still dumping the answer to the whole server, which is worse than
                # either end state. The plugin's own SendHelp is the three-line grouped version.
                if low == "!help":
                    if sysmsg_on("helpcmd"):            # owner can disable !help entirely (webcc Messages tab)
                        # guarded: an unguarded relay drop here unwinds main() on an SFTP outage
                        try:
                            _drop_plugin_cmd("help|" + str(steamid))  # -> plugin verb "help" -> SendHelp
                            activity(f"private !help to {PLAYER_NAMES.get(steamid, steamid)}", "PM")
                        except Exception as e:  # noqa: BLE001
                            activity(f"!help dropped (relay down: {e})", "BOT")
                    continue

                # after a normal chat message, post just the player's rank tag
                if (SHOW_RANK_ON_CHAT and state == "IDLE" and not low.startswith("!")
                        and now - last_rank_shown.get(steamid, 0) >= RANK_CHAT_THROTTLE):
                    last_rank_shown[steamid] = now
                    _tag = rank_tag(cycle_points(steamid), prestige_count(steamid))
                    if _tag:                                          # empty while the ladder is off
                        rc.say(_tag)

                # a player calls a mid-mission map vote
                if state == "IDLE" and now >= cooldown_until and low == "!votemap":
                    if not _votemap_cfg()["enabled"]:
                        rc.say("<color=#FF5555>Map voting is currently disabled by the server.</color>")
                        continue
                    players = get_players(rc)
                    n = max(len(players), 1)
                    # get-player-list stopped returning displayName after the 2026-07-27 update, so this
                    # resolved to the literal "None" and announced 'None wants to change the map!'.
                    # Use the ladder the rest of the file uses - Steam persona pump + plugin telemetry.
                    caller = display_name(steamid, None) or "A player"
                    activity(f"{caller} called a map-change vote", "VOTE")
                    if n <= 1:
                        rc.say(f"<color=#55FF55>{caller} called a map vote - only player, "
                               f"so it's on!</color> Pick the next map:")
                        print(f"[votemap] {steamid} solo -> auto-pass")
                        activity(f"{caller} is the only player - map vote opens automatically", "VOTE")
                        open_map_vote("votemap")
                    else:
                        approvals = {steamid: True}      # the caller counts as a Yes
                        approval_threshold = n // 2 + 1
                        approval_players = n
                        approval_ends_at = now + APPROVAL_DURATION
                        state = "APPROVAL"
                        rc.say(f"<color=#FFFF00>{caller} wants to change the map!</color> "
                               f"Type !y or !n ({APPROVAL_DURATION}s) - "
                               f"need {approval_threshold} of {n} to agree.")
                        print(f"[votemap] {steamid} -> approval poll, need {approval_threshold}/{n}")
                    continue

                # approval poll: tally !y / !n
                if state == "APPROVAL":
                    if low == "!y":
                        approvals[steamid] = True
                        print(f"[approval] {steamid} -> yes")
                    elif low == "!n":
                        approvals[steamid] = False
                        print(f"[approval] {steamid} -> no")
                    continue

                # map vote: tally the numbers
                if state == "VOTING":
                    opt = extract_vote(parsed["message"])
                    if opt:
                        votes[steamid] = opt
                        first_vote_at.setdefault(opt, now)
                        print(f"[vote] {steamid} -> {opt}")

        # refresh the player-name cache and welcome anyone who just joined.
        # Only act on a confident reading (a dict reply); a None means the command
        # errored -- skip it so a transient blip doesn't re-"welcome" everyone.
        if now - last_namesync >= JOIN_POLL_INTERVAL:
            last_namesync = now
            resp = rc.get_player_list()
            console_live_now = (
                (now - float(_LAST_NOSTATS_AT or 0)) < _CONSOLE_LIVE_S
                or (now - float(_LAST_CONSOLE_AT or 0)) < _CONSOLE_LIVE_S
            )
            if isinstance(resp, dict):
                _RELAY_LAST_OK[0] = now       # STALE-DATA HONESTY: roster freshness clock (dashboard is_stale)
                if not rcmd_up:
                    rcmd_up = True
                    activity("RCMD relay reconnected", "OK")
                    # Relay-only outage while console kept server_up: do NOT treat as game restart.
                    if server_up:
                        _down_since = 0.0
                if not server_up:
                    server_up = True
                    activity("Reconnected to the server", "OK")
                    # back after a REAL stop (>=20s down) => the game (re)loaded its config: verify the
                    # pending "restart to apply" config values survived + clear their badges (re-apply
                    # once if a panel re-templating boot reverted one). Short relay blips don't count.
                    if _down_since and (now - _down_since) >= 20:
                        try:
                            srvcfg_after_restart_check()   # also re-derives + pushes PostMissionDelay (FIX 3 self-heal)
                        except Exception as _e:    # noqa: BLE001
                            print(f"[srvcfg] post-restart check failed: {_e}")
                        try:
                            boot_map_safety_net(rc)        # FIX 4: booted off the boot map + empty -> LOAD it now
                        except Exception as _e:    # noqa: BLE001
                            print(f"[boot-map] restart apply failed: {_e}")
                        EMPTY_FORCED_CUT["pending"] = False   # a real restart wiped the queued in-memory override
                        server_generation += 1   # any ballot opened before this belongs to a dead session
                    _down_since = 0.0
                players = [p for p in (resp.get("Players") or resp.get("players") or [])
                           if isinstance(p, dict)]
                # keep the per-sid roster (faction + name) fresh for the dashboard table
                ROSTER_BY_SID.clear()
                ROSTER_BY_SID.update({str(p.get("steamId")): p for p in players if p.get("steamId")})
                accrue_time_played([str(p.get("steamId")) for p in players if p.get("steamId")], now)
                roster_changed = False
                for p in players:
                    sid_p = str(p.get("steamId") or "")
                    if not sid_p:
                        continue
                    nm_p = _strip_rank_tag(p.get("displayName"))   # drop any [ABBR] rank tag
                    if nm_p is not None:
                        p["displayName"] = nm_p          # clean ROSTER_BY_SID's dict (same ref)
                    good_p = _storable_name(sid_p, nm_p)
                    if good_p:
                        PLAYER_NAMES[sid_p] = good_p
                    else:
                        # POST-2026-07-27: get-player-list returns ONLY {steamId, faction} - the
                        # game stopped sending displayName with the name-system rewrite. This whole
                        # block used to hang off `if sid_p and nm_p`, so with no displayName NOTHING
                        # ran: no Steam lookup, no record, no welcome. Returning players still looked
                        # fine (their name was already in ranks.json), which is why it read as
                        # "brand new players are invisible". The SteamID alone is enough to name
                        # them, so ask regardless.
                        if not _storable_name(sid_p, PLAYER_NAMES.get(sid_p)) and                            not _storable_name(sid_p, (RANK_DATA.get(sid_p) or {}).get("name")):
                            maybe_fetch_persona(sid_p)
                    # record everyone seen online at rank 0, even if they never score and even
                    # when we cannot name them yet (the persona pump fills the name in later)
                    if ensure_player(sid_p, good_p):
                        roster_changed = True
                if roster_changed:
                    save_ranks()
                current = {str(p.get("steamId")) for p in players if p.get("steamId")}
                # Empty-server forced-cut fallback: a cut fired while nobody was on was
                # swallowed by the paused mission clock; the first join resumes the clock,
                # so re-issue the cut and the queued map still loads (~ROLLOVER_SECONDS s).
                if EMPTY_FORCED_CUT["pending"] and current:
                    try:
                        _el_ec = find_number(rc.get_mission_time(), "current")
                    except Exception as e:    # noqa: BLE001 - fallback must never break the roster poll
                        _el_ec = None
                        print(f"[map] empty-cut clock read failed: {e}")
                    if _el_ec is None:
                        # UNREADABLE clock: we cannot tell the still-paused OLD mission from a fresh
                        # post-rollover one, and firing blind cuts the map the joiner just arrived
                        # for. Never re-fire on an unreadable read - stay armed and retry on a later
                        # poll, then give up once the queued cut is too old to still be the truth.
                        if now - float(EMPTY_FORCED_CUT.get("at") or 0) > EMPTY_CUT_MAX_AGE:
                            EMPTY_FORCED_CUT["pending"] = False
                            print("[map] empty-cut dropped - mission clock unreadable and the queued cut is stale")
                    else:
                        EMPTY_FORCED_CUT["pending"] = False
                        try:
                            if _el_ec >= 30:
                                # the paused OLD mission is still up (a fresh post-rollover
                                # mission would read ~0s elapsed) -> safe to re-fire the cut
                                rc.set_time_remaining(ROLLOVER_SECONDS)
                                activity(f"Player joined - completing the queued map change to "
                                         f"{EMPTY_FORCED_CUT.get('label') or 'the queued map'}", "MAP")
                                print("[map] re-fired the empty-server forced cut")
                            # either way the cut we caused lands about now - swallow its own
                            # "Mission complete" so it can't open a second (mission-end) vote
                            suppress_mission_end_until = now + ROLLOVER_SECONDS + 25
                        except Exception as e:    # noqa: BLE001
                            print(f"[map] empty-cut re-fire failed: {e}")
                if seeded_online:
                    # Welcome any not-yet-welcomed player whose NAME we actually know now.
                    # Iterating `current` (not just brand-new sids) means a player first seen
                    # before their name synced still gets welcomed on a later 5s poll once it
                    # does -> no more "A pilot". WELCOMED dedups; it's cleared when they leave.
                    for sid_n in current - known_online:
                        log_join_line(sid_n)          # feed records EVERY join, independent of the welcome
                    for sid_j in current:
                        if sid_j in WELCOMED or sid_j in WELCOME_QUEUE:
                            continue
                        # placeholder names (bare sid / 'ID: ...') do NOT count as known:
                        # leave the sid unqueued so a later 5s poll welcomes them once the
                        # real name syncs (persona fetch is already in flight for them).
                        nm_j = (_storable_name(sid_j, PLAYER_NAMES.get(sid_j))
                                or _storable_name(sid_j, RANK_DATA.get(sid_j, {}).get("name")))
                        # ...but a name that will NEVER resolve (deleted / limited Steam account:
                        # Steam's profile XML 404s for them) must not mean NEVER welcomed. Once
                        # the bounded Steam retry has given up, welcome them under a neutral
                        # label; the drain still re-resolves at send time, so a late real name
                        # wins. WELCOME_FALLBACK_NAME is display-only - never stored as a name.
                        if not nm_j and _PERSONA_FAILS.get(sid_j, 0) >= _PERSONA_MAX_TRIES:
                            nm_j = WELCOME_FALLBACK_NAME
                        if nm_j:
                            queue_welcome(sid_j, nm_j)   # delayed ~5s; sent from the loop drain
                    for sid_l in known_online - current:
                        JOIN_LOGGED.discard(sid_l)    # so a rejoin is logged as a fresh join
                        nm_l = (PLAYER_NAMES.get(sid_l)
                                or RANK_DATA.get(sid_l, {}).get("name") or "A pilot")
                        WELCOMED.discard(sid_l)         # so a rejoin is welcomed again
                        WELCOME_QUEUE.pop(sid_l, None)  # left within the delay -> no welcome
                        _clear_map_downed(sid_l)        # drop ✝ + death anchor
                        POS.pop(sid_l, None)            # hide map blip once they leave
                        _pos_trail_clear(sid_l)
                        activity(f"{nm_l} left   -  {len(current)} online", "LEFT")
                else:
                    seeded_online = True
                    seed_welcomed_on_restart(current)   # a restart must not re-welcome everyone
                    activity(f"{len(current)} player(s) currently online", "INFO")
                known_online = current
            else:
                # RCMD failed — do NOT clear known_online (EmptyAutoDeploy would false-empty).
                # Keep server_up True while console/NOSTATS still heartbeat.
                # Do NOT stamp _down_since on relay-only failure (that falsely triggers
                # post-restart cfg checks after >=20s when RCMD later returns).
                if rcmd_up:
                    rcmd_up = False
                    activity("RCMD relay down — using console heartbeat for server_up", "!")
                if server_up and not console_live_now:
                    server_up = False
                    _down_since = now
                    activity("Lost connection to the server - retrying every few seconds...", "!")
                elif (not server_up) and console_live_now:
                    server_up = True
                    activity("Server live via console (RCMD still down)", "OK")

        # every 10 min while players are on + idle: friendly reminder of the commands.
        # Only advance the timer when it actually sends, so it isn't "used up" during
        # a vote or an empty server.
        # SEASON 1 notice: first-week broadcast, panel-toggleable, self-expiring on the date gate.
        if (season_notice_live() and sysmsg_on("season")
                and now - last_season_at >= sysmsg_interval("season", 1500)
                and known_online):
            last_season_at = now
            rc.say(sysmsg_text("season", _SYSMSG_SEASON_DEFAULT))

        if (sysmsg_on("thanks") and now - last_thanks_at >= sysmsg_interval("thanks", THANKS_INTERVAL)
                and known_online and state == "IDLE"):
            last_thanks_at = now
            rc.say(sysmsg_text("thanks", "<color=#FFD200>Thanks for playing!</color> For a list of "
                                         "commands type <color=#55FF55>!help</color>"))

        # every 30 min during an active match: auto-post the leaderboard to chat
        if (sysmsg_on("leaderboard") and now - last_leaderboard_at >= sysmsg_interval("leaderboard", LEADERBOARD_INTERVAL)
                and known_online and state == "IDLE"):
            last_leaderboard_at = now
            broadcast(rc, leaderboard_lines(), "Leaderboard")

        # every 12 min while players are on: how to spectate / switch to the smaller team.
        # The team-switch line only shows in a PvP match (both factions have players).
        if (sysmsg_on("spectip") and now - last_spectip_at >= sysmsg_interval("spectip", SPECTIP_INTERVAL)
                and known_online and state == "IDLE"):
            last_spectip_at = now
            facs = {(p.get("faction") or "").lower() for p in ROSTER_BY_SID.values()}
            facs.discard(""); facs.discard("none"); facs.discard("null")
            for ln in spectator_tip_lines(pvp=len(facs) >= 2):
                rc.say(ln)

        # every ~20 min while players are on: how busy the OTHER server is and what it's running, so a
        # quiet server can point people at the busy one. peer_presence_line() returns "" when no peer is
        # publishing, is stale, or is empty - so this stays silent rather than advertising a dead server.
        if (sysmsg_on("otherserver")
                and now - last_otherserver_at >= sysmsg_interval("otherserver", OTHERSERVER_INTERVAL)
                and known_online and state == "IDLE"):
            try:
                _peer_ln = peer_presence_line()
                if _peer_ln:
                    last_otherserver_at = now     # only SPEAKING starts the next interval. Burning the
                    rc.say(_peer_ln)              # slot on an empty peer would silence us for 20 more
                else:                             # minutes right as that server starts filling up.
                    last_otherserver_at = now - sysmsg_interval("otherserver", OTHERSERVER_INTERVAL) + 60
            except Exception as e:                # noqa: BLE001
                last_otherserver_at = now         # a THROW does back off fully - never retry-spam a fault
                print(f"[presence] announce error: {e}")

        # owner-defined automated messages (interval + daily clock triggers; event triggers fire
        # from the match start/end hooks). Never let a bad message break the main loop.
        try:
            check_server_messages(rc, now, known_online, state)
        except Exception as e:                # noqa: BLE001
            print(f"[servermsg] tick error: {e}")

        # keep the plugin's chat-rank lookup fresh (only needed when the plugin runs)
        pump_funds_announces(rc)                  # rank-funds lines held until the name resolves
        chat_tail_tick()                          # ordinary chat -> activity feed (plugin log tail)
        pump_persona_results()                    # apply background Steam name lookups (may set the push flag)
        _eom_win_timeout(rc, now)                 # a win frame whose award burst never arrived

        # coalesced ranks.json flush: time-played ticks every roster poll, and ranks.json is a
        # multi-megabyte serialise, so it is written on a timer instead of per change.
        if _RANKS_DIRTY[0] and now - last_ranks_flush >= _RANKS_FLUSH_INTERVAL:
            last_ranks_flush = now
            _RANKS_DIRTY[0] = False
            try:
                save_ranks()
            except Exception as e:                # noqa: BLE001 - never let a disk hiccup kill the loop
                print(f"[ranks] periodic flush failed: {e}")
                _RANKS_DIRTY[0] = True            # keep it dirty so the next tick retries
        if USE_PLUGIN_SCORE and (_RANK_PUSH_FLAG[0] or now - last_rank_push >= PLUGIN_RANK_PUSH_INTERVAL):
            last_rank_push = now
            _RANK_PUSH_FLAG[0] = False
            push_plugin_ranks()

        # --- command-centre feed: apply queued admin actions, refresh clock + state ---
        if process_admin_commands(rc):        # e.g. grant points; True => an admin 'Change map' just cut the match over
            suppress_mission_end_until = now + ROLLOVER_SECONDS + 25   # swallow the self-induced "Mission complete"
            cooldown_until = now + POST_VOTE_COOLDOWN                  # block a player !votemap right after
            state = "IDLE"                                            # cancel any vote in progress so the choice sticks
            vote_verify = None                                         # and never "correct" an admin's map choice
        # --- admin "End match" (see the endmatch branch in _apply_one_admin_command) ---
        # The match is ended by a FORCED CUT, exactly like !votemap: the ballot's winner is applied with
        # force_switch=True, which is what sets the mission clock to ROLLOVER_SECONDS. Queuing a
        # next-mission alone does NOT end anything - it just decides what loads whenever the current
        # mission finally expires on its own, which is the whole match later.
        if _ENDMATCH_REQUEST[0]:
            _ENDMATCH_REQUEST[0] = False      # consume FIRST: a throw below must not re-open a vote every loop
            try:
                if state == "APPROVAL":
                    # A player-initiated !votemap approval poll is running. The admin has overruled it -
                    # say so rather than letting it vanish, then fall through to the admin's own ballot.
                    rc.say("<color=#FFC857>An admin ended the match - the map-change vote is superseded.</color>")
                    activity("Admin ended the match - cancelled the running map-change approval poll", "VOTE")
                    approvals.clear()
                    approval_ends_at = 0
                    state = "IDLE"
                if state == "VOTING":
                    activity("Admin ended the match - a map vote is already open, so its winner will "
                             "cut the match over", "MAP")
                elif _votemap_cfg()["enabled"]:
                    cooldown_until = now + POST_VOTE_COOLDOWN
                    open_map_vote("endmatch")     # closes as a FORCED switch - see `force` at the vote-close
                    activity("Admin ended the match - opening the map vote; the winner loads straight "
                             "after", "MAP")
                    print("[bot] admin end match -> vote opened (winner cuts the mission over)")
                else:
                    # Voting is OFF, so there is no ballot to end the match for us - cut it here and let
                    # the server rotation pick. Without this the button would be a no-op whenever the
                    # owner has voting disabled.
                    activity("Admin ended the match - map voting is OFF, cutting to the server "
                             "rotation", "MAP")
                    rc.set_time_remaining(ROLLOVER_SECONDS)
                    # NOT CURRENT_MISSION. Every other note_forced_cut caller passes the map being cut
                    # TO (apply_winner passes it only after reassigning; force_change_map passes the new
                    # name). Here nothing is queued - the server rotation decides - and CURRENT_MISSION
                    # is still the mission being ENDED, so passing it would make the empty-server on-join
                    # message announce "completing the queued map change to <the map we just ended>".
                    note_forced_cut(rc, "the next map in the server rotation")
                    cooldown_until = now + POST_VOTE_COOLDOWN
                    print("[bot] admin end match -> forced cut (votemap off)")
            except Exception as e:                # noqa: BLE001
                print(f"[endmatch] apply error: {e}")
                activity(f"Admin end match failed: {e}", "!")
        try:
            drain_admin_kicks(rc)             # admin/webcc kicks (TellPlayer already sent; no unkick)
        except Exception as e:                # noqa: BLE001
            print(f"[admin-kick] drain error: {e}")
        try:
            drain_session_unkicks(rc)         # automated kick-only (flood/TK) — lift session block
        except Exception as e:                # noqa: BLE001
            print(f"[unkick] drain error: {e}")
        if server_up and now - last_mtime_poll >= 15:   # skip 2 blocking rcmds during an outage
            last_mtime_poll = now
            mt = rc.get_mission_time()
            cur = find_number(mt, "current")
            mx = find_number(mt, "max")
            refresh_current_mission(rc)       # settle CURRENT_MISSION FIRST (also self-heals "(unknown)") so the
                                              # mission-time-warning dedupe key is the final value, never the
                                              # transient post-vote name -> no double "Mission time: X remaining"
            if cur is not None and mx is not None:
                mtime = [cur, mx, now]
                check_mission_time_warnings(rc, mtime, CURRENT_MISSION)
            if cur is not None and mx is not None:
                try:
                    check_match_milestones(rc, mtime)   # 'stay for next match' reminders
                except Exception as e:        # never let a milestone hiccup break the main loop
                    print(f"[milestone] check error: {e}")
            try:
                check_schedule(rc)            # fire any due scheduled restarts/updates (warns players first)
            except Exception as e:            # never let a schedule hiccup break the main loop
                print(f"[sched] check error: {e}")
        if now - last_state_write >= STATE_WRITE_INTERVAL:
            last_state_write = now
            approval_info = None
            if state == "APPROVAL":
                approval_info = {
                    "yes":     sum(1 for v in approvals.values() if v),
                    "need":    approval_threshold,
                    "players": approval_players,
                    "ends_in": max(0, int(approval_ends_at - now)),
                }
            _console_live = (
                (now - float(_LAST_NOSTATS_AT or 0)) < _CONSOLE_LIVE_S
                or (now - float(_LAST_CONSOLE_AT or 0)) < _CONSOLE_LIVE_S
            )
            write_dashboard_state(state=state, server_up=server_up, online=known_online,
                                  votes=votes, vote_ends_at=vote_ends_at,
                                  vote_context=vote_context, approval=approval_info, mtime=mtime,
                                  rcmd_up=rcmd_up, console_live=_console_live)
        if now - last_mirror_trim >= 60:
            last_mirror_trim = now
            trim_console_mirror()
            trim_activity_log()

        # approval poll closes: pass -> open a map vote; fail -> nothing happens
        if state == "APPROVAL" and now >= approval_ends_at:
            yes = sum(1 for v in approvals.values() if v)
            # re-base the bar on who is online NOW (get_players can blip to [] -> known_online backstop)
            approval_threshold, approval_players = recompute_approval(
                len(get_players(rc)) or len(known_online), approval_threshold, approval_players)
            if yes >= approval_threshold:
                rc.say(f"<color=#55FF55>Map change approved</color> ({yes}/{approval_players}) - "
                       f"vote for the next map:")
                print(f"[votemap] approved {yes}/{approval_players} -> map vote")
                activity(f"Map-change vote passed ({yes}/{approval_players} yes) - opening map vote", "VOTE")
                open_map_vote("votemap")
            else:
                rc.say(f"<color=#FF5555>Map change rejected</color> ({yes}/{approval_players} yes).")
                print(f"[votemap] rejected {yes}/{approval_players}")
                activity(f"Map-change vote rejected ({yes}/{approval_players} yes)", "VOTE")
                cooldown_until = now + POST_VOTE_COOLDOWN
                state = "IDLE"

        # Once-per-vote rebroadcast when ≤15s remain (skip if ballot itself is ≤15s).
        if (state == "VOTING"
                and not vote_reminder_sent
                and active_vote_duration > 15
                and 0 < (vote_ends_at - now) <= 15):
            announce_options(rc, duration=max(1, int(math.ceil(vote_ends_at - now))),
                             left_note="15s left")
            vote_reminder_sent = True
            activity("Map vote rebroadcast (15s left)", "VOTE")

        # map vote closes -> apply winner (force the cut-over only for a !votemap vote)
        if state == "VOTING" and now >= vote_ends_at:
            # The game server restarted while this ballot was open, so it belongs to a session that no
            # longer exists: everyone who could vote was disconnected, and a !votemap winner would
            # force-cut the match that has just booted. Abandon it. (audit 2026-08-01)
            if vote_generation != server_generation:
                activity("Map vote abandoned - the game server restarted while it was open "
                         "(the ballot belonged to the previous session)", "!")
                print(f"[votemap] abandoning ballot from generation {vote_generation} "
                      f"(server is now on {server_generation})")
                rc.say("<color=#FFC857>Map vote cancelled - the server restarted mid-vote.</color>")
                state = "IDLE"
                votes = {}
                first_vote_at = {}
                vote_ends_at = 0
                vote_context = ""
                VOTE_OPTIONS.clear()
                cooldown_until = now + POST_VOTE_COOLDOWN
                continue
            # "endmatch" is a forced context for the same reason "votemap" is: the winner must CUT the
            # live mission over, not merely be queued behind it. Without this the admin's End match
            # button banks the match, runs a ballot, announces a winner - and the match plays on.
            force = vote_context in ("votemap", "endmatch")
            prior_mission = CURRENT_MISSION            # the mission that just ended (pre-apply value)
            applied = apply_winner(rc, votes, first_vote_at, force_switch=force)
            if applied and not force:
                # Mission-end votes must not change a live match. Verify shortly after the dust settles;
                # if the game already moved on, only re-queue the winner for next time and log it.
                vote_verify = {"expected": applied["expected"], "label": applied["label"],
                               "group": applied["group"], "name": applied["name"],
                               "max_time": applied["max_time"], "prior": prior_mission,
                               "due": now + 20, "tries": 0}
            # mission-end votes: the game loads the winner when ITS post-mission countdown runs out. With
            # the FIX 3 derivation that gap IS exactly the post-vote delay, so announce it DIRECTLY instead
            # of reading a possibly-stale live PostMissionDelay.
            if not force and VOTE_OPTIONS:
                rc.say(f"<color=#9AD1FF>Loading the next map in ~{max(int(post_vote_delay()), 5)}s...</color>")
            cooldown_until = now + POST_VOTE_COOLDOWN
            if force:
                # the mid-mission cut logs its own "Mission complete" ~ROLLOVER_SECONDS later;
                # swallow that one so it doesn't immediately open a second (mission-end) vote.
                suppress_mission_end_until = now + ROLLOVER_SECONDS + 25
            state = "IDLE"

        # post-vote verification: did OUR winner actually load? If not, leave the live mission alone.
        if vote_verify and now >= vote_verify["due"]:
            try:
                status = _mission_status(rc)
                if status:
                    cur = status["current_label"] or ""
                    next_label = status["next_label"] or ""
                    CURRENT_MISSION = cur or CURRENT_MISSION
                else:
                    refresh_current_mission(rc)
                    cur = CURRENT_MISSION or ""
                    next_label = ""
                expected_key = (vote_verify["group"], vote_verify["name"])
                current_matches = bool(status and status["current_key"] == expected_key)
                next_matches = bool(status and status["next_key"] == expected_key)
                if cur and (cur == vote_verify["expected"] or current_matches):
                    print(f"[vote] verified: '{cur}' loaded as voted")
                    vote_verify = None
                elif (cur and cur != "(unknown)" and cur != (vote_verify.get("prior") or "")) or next_matches:
                    # A different new mission is running. Do not change it midway; keep the winner queued.
                    if next_matches:
                        activity(f"Vote-apply check: '{vote_verify['expected']}' missed the post-mission "
                                 f"window and is queued next; leaving current mission "
                                 f"'{cur or 'unknown'}' alone", "!")
                    else:
                        rc.set_next_mission(vote_verify["group"], vote_verify["name"],
                                            vote_verify["max_time"])
                        activity(f"Vote-apply check: '{vote_verify['expected']}' missed the post-mission "
                                 f"window; re-queued for the next mission and left current mission "
                                 f"'{cur or 'unknown'}' alone", "!")
                    vote_verify = None
                else:
                    # old mission still showing / server mid-scene-load -> look again shortly
                    vote_verify["tries"] += 1
                    if vote_verify["tries"] >= 6:          # ~90s of patience, then give up quietly
                        print(f"[vote] verify gave up: current='{cur}' expected='{vote_verify['expected']}'")
                        vote_verify = None
                    else:
                        vote_verify["due"] = now + 12
            except Exception as e:                          # never let the check break the loop
                print(f"[vote] verify error: {e}")
                vote_verify = None

        time.sleep(0.3)


def test_conn():
    """Verify the remote-command channel and show the raw get-mission-time reply."""
    rc = RemoteCommand(RCMD_HOST, RCMD_PORT)
    print(f"[test] connecting to remote commands at {RCMD_HOST}:{RCMD_PORT} ...")
    resp = rc.get_mission_time()
    if resp is None:
        print("[test] FAILED - no response. Check RCMD_HOST/RCMD_PORT and that the")
        print("       TCP port is reachable from this machine (firewall / ask Legion).")
    else:
        print(f"[test] OK - got a reply: {resp}")
        cur = find_number(resp, "current")
        mx = find_number(resp, "max")
        if cur is not None and mx is not None:
            rem = mx - cur
            print(f"[test] Mission time: {int(cur)}s elapsed of {int(mx)}s -> "
                  f"~{int(rem)}s ({int(rem)//60}m{int(rem)%60:02d}s) remaining. Channel works!")
        else:
            print("[test] Channel works, but couldn't parse current/max from the reply above.")


def test_chat(seconds=20):
    """Verify the vote-reading channel: watch the log and print any chat it sees."""
    console = SFTPConsoleSource(SFTP_HOST, SFTP_PORT, SFTP_USER, SFTP_PASS, SFTP_LOG_PATH)
    print(f"[test] watching the console log for {seconds}s - go type in game chat now...")
    end = time.time() + seconds
    seen = 0
    while time.time() < end:
        for line in console.poll():
            parsed = parse_chat_line(line)
            if parsed:
                seen += 1
                print(f"[test] chat from {parsed['steamid']}: {parsed['message']!r}")
        time.sleep(1.5)
    if seen:
        print(f"[test] OK - read {seen} chat line(s). Vote-reading works.")
    else:
        print("[test] No chat parsed. Check NO_SFTP_LOGPATH points at the right file")
        print("       and that someone actually chatted during the window.")


def _open_sftp():
    """Open an SFTP session from the NO_SFTP_* env creds. Caller closes the ssh."""
    import paramiko
    ssh = paramiko.SSHClient()
    ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    ssh.connect(SFTP_HOST, port=SFTP_PORT, username=SFTP_USER, password=SFTP_PASS,
                timeout=15, look_for_keys=False, allow_agent=False)
    return ssh, ssh.open_sftp()


# Persistent SFTP session for the RUNNING bot's hot paths (rank pushes, whispers,
# team commands). Reuses one SSH connection instead of a ~100-300ms handshake per op.
# CLI one-shots (--get/--put/--ls) keep using _open_sftp (their process exits anyway).
_BOT_SFTP = {"ssh": None, "sftp": None}


def _bot_sftp():
    s = _BOT_SFTP
    tr = s["ssh"].get_transport() if s["ssh"] else None
    if s["sftp"] is None or tr is None or not tr.is_active():
        try:
            if s["ssh"]:
                s["ssh"].close()
        except Exception:                            # noqa: BLE001
            pass
        s["ssh"], s["sftp"] = _open_sftp()
    return s["sftp"]


def _sftp_op(fn):
    """Run fn(sftp) on the persistent session; reconnect + retry once if it dropped."""
    try:
        return fn(_bot_sftp())
    except Exception:                                # noqa: BLE001 - stale/dropped conn
        try:
            if _BOT_SFTP["ssh"]:
                _BOT_SFTP["ssh"].close()
        except Exception:                            # noqa: BLE001
            pass
        _BOT_SFTP["ssh"] = _BOT_SFTP["sftp"] = None
        return fn(_bot_sftp())


def remote_ls():
    """run.bat --ls [path]: list a remote directory (default = SFTP root)."""
    import stat as statmod
    path = "."
    rest = sys.argv[sys.argv.index("--ls") + 1:]
    if rest and not rest[0].startswith("--"):
        path = rest[0]
    ssh, sftp = _open_sftp()
    try:
        print(f"[ls] {path}")
        for e in sorted(sftp.listdir_attr(path),
                        key=lambda a: (not statmod.S_ISDIR(a.st_mode), a.filename.lower())):
            kind = "d" if statmod.S_ISDIR(e.st_mode) else "-"
            print(f"  {kind} {e.st_size:>12,}  {e.filename}")
    finally:
        ssh.close()


def remote_cat():
    """run.bat --cat <path> [maxbytes]: print a remote text file (default 200 KB)."""
    rest = sys.argv[sys.argv.index("--cat") + 1:]
    if not rest:
        print("usage: run.bat --cat <remote_path> [maxbytes]")
        return
    path = rest[0]
    maxb = int(rest[1]) if len(rest) > 1 and rest[1].isdigit() else 200_000
    ssh, sftp = _open_sftp()
    try:
        with sftp.open(path, "rb") as f:
            data = f.read(maxb).decode("utf-8", "replace")
        print(f"[cat] {path} ({len(data)} chars shown)\n" + "-" * 60)
        print(data)
    finally:
        ssh.close()


def probe_missions():
    """run.bat --probe-missions: discover the Group/Name of the built-in (stock) missions.
    set-next-mission always replies 2000 but only actually changes the override for a VALID
    mission, so we set a known baseline, try a candidate, and read back the override to see if
    it 'took'. Accepted keys for MISSION_KEY_CANDIDATES missions are CACHED into
    mission_keys.json (arming them for ballots — same effect as a first successful Change map).
    The pre-existing next-mission override, if any, is restored at the end."""
    rc = RemoteCommand(RCMD_HOST, RCMD_PORT)
    # Known-valid reset key between candidates. The stock PvP "Escalation" is live-verified as
    # (BuiltIn, Escalation); a candidate that happens to EQUAL the baseline is still judged
    # correctly (the baseline itself proves the key valid).
    baseline = ("BuiltIn", "Escalation")

    def current_override():
        r = rc.send("get-mission-rotation")
        if isinstance(r, dict) and r.get("hasNextOverride"):
            k = r.get("nextOverride", {}).get("Key", {})
            return (k.get("Group"), k.get("Name"))
        return None

    orig = current_override()
    print(f"[probe] pre-existing next-mission override: {orig or 'none'}")
    groups = ["Built-in", "Built-In", "BuiltIn", "Builtin", "",
              "Official", "Base", "Stock", "Default", "Campaign"]
    names = ["Escalation", "Terminal Control", "Carrier Duel", "13. Reprisal", "Reprisal",
             "Escalation Co-op as BDF", "Escalation Co-op as PALA",
             "Terminal Control Co-op as BDF", "Terminal Control Co-op as PALA", "Breakout"]
    candidates = [(g, n) for n in names for g in groups]
    accepted = []
    print(f"[probe] testing {len(candidates)} candidate(s) ...")
    for g, n in candidates:
        rc.send("set-next-mission", baseline[0], baseline[1], 10800)  # reset baseline
        rc.send("set-next-mission", g, n, 10800)                     # try candidate
        ov = current_override()
        ok = ov == (g, n)
        if ok:
            accepted.append((g, n))
        print(f"  {'ACCEPTED' if ok else 'rejected':>8}  Group={g!r:14} Name={n!r}")
    # cache accepted keys for the candidate-resolved missions (first accepted candidate wins)
    for pool_name, cand in MISSION_KEY_CANDIDATES.items():
        hit = next(((g, n) for g, n in cand if (g, n) in accepted), None)
        if hit and not mission_key_verified(pool_name):
            d = _load_mission_keys()
            d[pool_name] = [hit[0], hit[1]]
            try:
                tmp = MISSION_KEYS_FILE + ".tmp"
                with open(tmp, "w", encoding="utf-8") as f:
                    json.dump(d, f, indent=1)
                os.replace(tmp, MISSION_KEYS_FILE)
                print(f"[probe] cached mission key: {pool_name} -> {hit[0]}/{hit[1]} (now ballot-eligible)")
            except OSError:
                pass
    restore = orig or baseline
    rc.send("set-next-mission", restore[0], restore[1], 10800)
    print(f"\n[probe] accepted: {accepted or 'NONE'}")
    print(f"[probe] override restored to {restore}; a server restart clears it entirely.")


def remote_get():
    """run.bat --get <remote> <local>: download a remote file to inspect locally."""
    rest = sys.argv[sys.argv.index("--get") + 1:]
    if len(rest) < 2:
        print("usage: run.bat --get <remote_path> <local_path>")
        return
    remote, local = rest[0], rest[1]
    ssh, sftp = _open_sftp()
    try:
        sftp.get(remote, local)
        print(f"[get] {remote} -> {local} ({os.path.getsize(local):,} bytes)")
    finally:
        ssh.close()


def remote_put():
    """run.bat --put <local> <remote>: upload a local file to a remote path."""
    rest = sys.argv[sys.argv.index("--put") + 1:]
    if len(rest) < 2:
        print("usage: run.bat --put <local_path> <remote_path>")
        return
    local, remote = rest[0], rest[1]
    if not os.path.exists(local):
        print(f"[put] local file not found: {local}")
        return
    ssh, sftp = _open_sftp()
    try:
        sftp.put(local, remote)
        print(f"[put] {local} -> {remote} ({os.path.getsize(local):,} bytes)")
    finally:
        ssh.close()


def remote_put_atomic():
    """run.bat --put-atomic <local> <remote>: upload to <remote>.deploytmp then
    atomically rename over <remote>. SAFE to replace a DLL the RUNNING server has
    mmap'd (e.g. BepInEx/plugins/NukeStats.dll): the live process keeps its old
    inode, so it does NOT corrupt (no BadImageFormatException); the new file loads
    on the next server restart. Use this instead of --put for live plugin deploys."""
    rest = sys.argv[sys.argv.index("--put-atomic") + 1:]
    if len(rest) < 2:
        print("usage: run.bat --put-atomic <local_path> <remote_path>")
        return
    local, remote = rest[0], rest[1]
    if not os.path.exists(local):
        print(f"[put-atomic] local file not found: {local}")
        return
    tmp = remote + ".deploytmp"
    ssh, sftp = _open_sftp()
    try:
        sftp.put(local, tmp)
        try:
            sftp.posix_rename(tmp, remote)           # openssh ext: atomic overwrite
        except Exception:                            # noqa: BLE001 - no posix-rename
            # Linux fallback: unlinking the dir entry is safe while the process holds
            # the inode via its mapping; the new file then takes the path.
            try:
                sftp.remove(remote)
            except Exception:                        # noqa: BLE001
                pass
            sftp.rename(tmp, remote)
        print(f"[put-atomic] {local} -> {remote} ({os.path.getsize(local):,} bytes, atomic)")
    finally:
        ssh.close()


def remote_chmod_exec():
    """run.bat --chmod-exec <remote>: chmod 0755 a remote file. Use after a --put round-trip
    on an EXECUTABLE launch wrapper/script (a plain SFTP create can land 0644 -> the server
    won't start). NOTE: for launch SCRIPTS use plain --put (truncates in place, preserves the
    inode+mode) then this; never --put-atomic (its temp file lands non-executable)."""
    rest = sys.argv[sys.argv.index("--chmod-exec") + 1:]
    if not rest:
        print("usage: run.bat --chmod-exec <remote_path>")
        return
    remote = rest[0]
    ssh, sftp = _open_sftp()
    try:
        sftp.chmod(remote, 0o755)
        print(f"[chmod-exec] {remote} -> 0755")
    finally:
        ssh.close()


# ── Automated plugin deploy (scheduled ~05:00 via deploy.bat -> run.bat --deploy-plugin) ──────
# Owns the daily restart: atomically stages a new plugin DLL (if one is pending), then stops &
# starts the game server via the Pterodactyl client API, verifying the server is actually serving
# through the RELAY (the panel's "running" state is unreliable for this egg - it flaps to "starting"
# on mission reloads). GUARDRAIL: from the stop onward, any failure forces a START so the server is
# never knowingly left offline. Run via run.bat so the SFTP env (NO_SFTP_*) is set for the upload.
_PT_UA = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
          "(KHTML, like Gecko) Chrome/124.0 Safari/537.36")
PENDING_DLL   = os.path.join(_BASE_DIR, "pending_plugin.dll")
DEPLOY_HASH   = os.path.join(_BASE_DIR, "deployed_plugin.sha256")
DEPLOY_LOG    = os.path.join(_BASE_DIR, "deploy_plugin.log")
DEPLOY_LOCK   = os.path.join(_BASE_DIR, "pending_plugin.dll.lock")
MATCHEND_FLAG = os.path.join(_BASE_DIR, "matchend_deploy.flag")
REMOTE_PLUGIN = "BepInEx/plugins/NukeStats.dll"


def _deploy_log(msg):
    line = f"{time.strftime('%Y-%m-%d %H:%M:%S')}  {msg}"
    print(line)
    try:
        with open(DEPLOY_LOG, "a", encoding="utf-8") as f:
            f.write(line + "\n")
    except OSError:
        pass


_PANEL_SCHEME_RE = re.compile(r'^[a-z][a-z0-9+.-]*://', re.I)


def normalize_panel_url(url):
    """Forgiving Pterodactyl panel BASE. Adds https:// when there's no scheme, replaces a wrong
    scheme (sftp://, ws://, ...), drops a pasted /server/... path and a trailing /api/client.
    A CORRECT base is returned byte-identical (strict superset) so existing setups are unchanged."""
    u = (url or "").strip()
    if not u:
        return ""
    m = _PANEL_SCHEME_RE.match(u)
    if m:
        if m.group(0).lower() not in ("http://", "https://"):
            u = "https://" + u[m.end():]          # someone pasted sftp://… etc.
    else:
        u = "https://" + u
    i = u.lower().find("/server/")                 # full server URL pasted -> keep the base
    if i != -1:
        u = u[:i]
    u = u.rstrip("/")
    if u.lower().endswith("/api/client"):          # only the well-known client-API path (NOT a bare /api)
        u = u[:-len("/api/client")].rstrip("/")
    return u


def _pt_friendly_json(raw, ctype):
    """json.loads with a clear error when the panel returns an HTML page (wrong URL) not JSON,
    so the cryptic 'Expecting value: line 1 column 1' never surfaces on the power button."""
    body = raw.decode("utf-8", "replace") if isinstance(raw, (bytes, bytearray)) else (raw or "")
    if not body:
        return {}
    if "json" not in (ctype or "").lower() and body.lstrip()[:1] not in ("{", "["):
        raise ValueError("the panel URL returned a web page, not the API — check panel.txt is your "
                         "panel's base address (e.g. https://panel.host.net), with no /server/... path")
    return json.loads(body)


def _pt_cfg():
    """Load Pterodactyl client-API config from apiKey.txt + panel.txt (mirrors cc_web._pt_load)."""
    cfg = {"key": None, "base": None, "server": None, "err": None}
    try:
        cfg["key"] = open(os.path.join(_BASE_DIR, "apiKey.txt")).read().strip() or None
    except OSError:
        cfg["key"] = None
    try:
        rows = [l.strip() for l in open(os.path.join(_BASE_DIR, "panel.txt")) if l.strip()]
    except OSError:
        rows = []
    raw = (rows[0] if rows else "")
    want = rows[1] if len(rows) > 1 else None
    if "/server/" in raw and not want:
        want = raw.partition("/server/")[2].split("/")[0] or None
    cfg["base"], cfg["server"] = (normalize_panel_url(raw) or None), want
    if not cfg["key"]:
        cfg["err"] = "no apiKey.txt"
    elif not cfg["base"]:
        cfg["err"] = "no panel.txt"
    elif not cfg["server"]:
        try:
            s = _pt_api(cfg, "GET", "/api/client", None).get("data", [])
            cfg["server"] = s[0]["attributes"]["identifier"] if s else None
            if not cfg["server"]:
                cfg["err"] = "API key sees no servers"
        except Exception as e:                       # noqa: BLE001
            cfg["err"] = f"discover failed: {e}"
    return cfg


def _pt_api(cfg, method, path, body):
    import ssl
    import urllib.request
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(cfg["base"] + path, data=data, method=method, headers={
        "Authorization": "Bearer " + cfg["key"], "Accept": "application/json",
        "Content-Type": "application/json", "User-Agent": _PT_UA})
    with urllib.request.urlopen(req, context=ssl.create_default_context(), timeout=15) as r:
        ctype = r.headers.get("Content-Type", "")
        raw = r.read()
    return _pt_friendly_json(raw, ctype)


def _pt_power_signal(cfg, signal):
    _pt_api(cfg, "POST", f"/api/client/servers/{cfg['server']}/power", {"signal": signal})


def _pt_state(cfg):
    a = _pt_api(cfg, "GET", f"/api/client/servers/{cfg['server']}/resources", None).get("attributes", {})
    return a.get("current_state")


def _pt_upload_plugin_bytes(cfg, local_path, remote_path=REMOTE_PLUGIN):
    """Upload a plugin DLL via the Pterodactyl files/write API (SFTP fallback).
    Used when paramiko auth flaps or SFTP is temporarily unavailable. Never logs secrets."""
    import ssl
    import urllib.parse
    import urllib.request
    data = open(local_path, "rb").read()
    q = urllib.parse.quote(remote_path)
    req = urllib.request.Request(
        cfg["base"] + f"/api/client/servers/{cfg['server']}/files/write?file=" + q,
        data=data, method="POST",
        headers={
            "Authorization": "Bearer " + cfg["key"], "Accept": "application/json",
            "Content-Type": "application/octet-stream", "User-Agent": _PT_UA,
        })
    with urllib.request.urlopen(req, context=ssl.create_default_context(), timeout=120) as r:
        r.read()
        return r.status


def _relay_alive():
    """Authoritative 'the game is actually serving' check via the relay (panel state is unreliable)."""
    try:
        get_players(RemoteCommand(RCMD_HOST, RCMD_PORT))   # raises on a dead relay; a list (even []) = up
        return True
    except Exception:                                # noqa: BLE001
        return False


def _deploy_online_players():
    """Return the live player list for deploy safety; raise if emptiness cannot be confirmed."""
    rc = RemoteCommand(RCMD_HOST, RCMD_PORT)
    code, resp = rc.send("get-player-list", return_code=True)
    if code != 2000 or not isinstance(resp, dict):
        raise RuntimeError(f"get-player-list failed (code={code})")
    return _extract_players(resp)


def _sha256(path):
    import hashlib
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(65536), b""):
            h.update(chunk)
    return h.hexdigest()


# ANNOUNCE+FLAG leg of match-end deploys (2026-08-15): last staged sha this process flagged,
# so one staged build can never be announced/flagged twice from the same bot run.
_matchend_flagged_sha = None


def matchend_deploy_notify(rc, roster):
    """At the match-end moment: if a plugin update is staged (pending_plugin.dll + its
    pending_plugin.json sidecar present and the sha differs from the deployed one — the same
    have_update check deploy_plugin_job uses), warn everyone in-chat that a restart is coming
    and atomically drop matchend_deploy.flag for the watchdog leg to consume.
    RAILS: never twice for the same staged sha (module global AND the flag file itself),
    never inside 04:40-05:20 local (the morning machinery owns that band), never when the
    server is EMPTY (EmptyAutoDeploy owns that case), and EVERY failure path is silent —
    an announce/flag hiccup must never disturb match-end processing."""
    global _matchend_flagged_sha
    try:
        # EMPTY -> EmptyAutoDeploy's case, not ours. roster is the match-end get_players()
        # result, i.e. the same relay get-player-list that _deploy_online_players() trusts
        # for deploy safety (a failed read also lands here as [] -> safe skip).
        if not roster:
            return
        # 04:40-05:20 local belongs to the 05:00 machinery - stay out of its band.
        lt = time.localtime()
        if 440 <= lt.tm_hour * 100 + lt.tm_min <= 520:
            return
        # Staged-update check, mirroring deploy_plugin_job's have_update (sidecar required).
        if not os.path.exists(PENDING_DLL) or not os.path.exists(os.path.join(_BASE_DIR, "pending_plugin.json")):
            return
        new_sha = _sha256(PENDING_DLL)
        try:
            old_sha = open(DEPLOY_HASH).read().strip()
        except OSError:
            old_sha = ""
        if new_sha == old_sha:
            return                                   # nothing genuinely new staged
        if new_sha == _matchend_flagged_sha:
            return                                   # already announced+flagged this build
        try:
            if os.path.exists(MATCHEND_FLAG):
                with open(MATCHEND_FLAG, encoding="utf-8") as ff:
                    if (json.loads(ff.readline() or "{}") or {}).get("sha") == new_sha:
                        _matchend_flagged_sha = new_sha   # flagged by a previous bot run
                        return
        except (OSError, ValueError):
            pass                                     # unreadable/stale flag -> rewrite below
        rc.say("<color=#FFC857>Server update ready - restarting at the end of this match screen. "
               "Back in ~2 minutes, please rejoin!</color>")
        tmpf = MATCHEND_FLAG + ".tmp"
        with open(tmpf, "w", encoding="utf-8") as ff:
            ff.write(json.dumps({"sha": new_sha, "ts": time.time(), "announced": True}) + "\n")
        os.replace(tmpf, MATCHEND_FLAG)
        _matchend_flagged_sha = new_sha
        activity("match-end deploy armed - players warned, matchend_deploy.flag dropped", "!")
    except Exception as e:                           # noqa: BLE001  (rail: silent, never break match end)
        print(f"[matchend-deploy] announce/flag skipped: {e}")


def deploy_plugin_job(dry=False, force=False):
    """Daily ~05:00 job (run via run.bat --deploy-plugin so the SFTP env is set). Stages a pending
    plugin DLL (atomic, mmap-safe) if it differs from the last deployed one, then RESTARTS the game
    server (stop -> offline -> start -> relay-verified) so the new DLL loads. GUARDRAIL: from the
    stop onward, any failure forces a START. --deploy-plugin-dry does pre-flight + reports state and
    what WOULD happen, with NO power/upload actions (safe to run against the live server).
    --deploy-plugin-force skips the empty-server guard (kicks everyone — ops-only, explicit).

    EXIT-CODE CONTRACT (2026-07-28) — callers (morning_0500.ps1, _empty_autodeploy_*.ps1) must be
    able to tell "the game was restarted" from "nothing happened", so they can fall back to their
    own hardened restart instead of silently losing the day's restart:
        0 = restart cycle ran and the server answered the relay (or --deploy-plugin-dry).
        2 = ABORTED before ANY power action (lock held / not configured / could not confirm empty /
            upload failed). The game was NOT restarted.
        3 = deliberately deferred: players online and not --force. The game was NOT restarted.
        4 = power actions were taken but the server did not come back verified (CRIT) — unhealthy.
    Any non-zero means "do not assume the game restarted"."""
    tag = "DRY-RUN" if dry else ("DEPLOY-FORCE" if force else "DEPLOY")
    _deploy_log(f"=== {tag} start ===")

    if not dry:
        try:
            if os.path.exists(DEPLOY_LOCK) and (time.time() - os.path.getmtime(DEPLOY_LOCK)) < 900:
                _deploy_log("ABORT: another deploy appears to be running (fresh lock)."); return 2
            fd = os.open(DEPLOY_LOCK, os.O_CREAT | os.O_WRONLY | os.O_TRUNC)
            os.write(fd, str(time.time()).encode()); os.close(fd)
        except OSError as e:
            _deploy_log(f"ABORT: cannot take lock: {e}"); return 2

    try:
        cfg = _pt_cfg()
        if cfg.get("err") or not cfg.get("server"):
            _deploy_log(f"ABORT: Pterodactyl not configured ({cfg.get('err')}). No power action taken."); return 2
        try:
            _deploy_log(f"server reachable; current panel state={_pt_state(cfg)}")
        except Exception as e:                       # noqa: BLE001
            _deploy_log(f"WARN: could not read power state: {e}")

        try:
            online_count = len(_deploy_online_players())
            _deploy_log(f"server player check: {online_count} online")
        except Exception as e:                       # noqa: BLE001
            # STALE-DATA HONESTY (2026-07-27): a dead relay usually means the SERVER is down,
            # and a panel-confirmed OFFLINE server is empty by definition - the old flow
            # ABORTED all night on an offline server. Trust the panel's power state for
            # exactly that one case; anything else still aborts (never assume empty).
            st_off = None
            try:
                st_off = _pt_state(cfg)
            except Exception as e2:                  # noqa: BLE001
                _deploy_log(f"WARN: panel state probe also failed: {e2}")
            if st_off == "offline":
                online_count = 0
                _deploy_log(f"player check failed ({e}) but panel state=offline -> treating as EMPTY (deployable)")
            else:
                _deploy_log(f"ABORT: could not confirm the server is empty ({e}; panel state={st_off or '?'}). "
                            f"No upload or power action taken.")
                return 2

        have_update = False
        if os.path.exists(PENDING_DLL):
            new_hash = _sha256(PENDING_DLL)
            try:
                old_hash = open(DEPLOY_HASH).read().strip()
            except OSError:
                old_hash = ""
            have_update = (new_hash != old_hash)
            _deploy_log(f"pending_plugin.dll present ({os.path.getsize(PENDING_DLL):,} B); "
                        f"{'NEW -> will upload' if have_update else 'unchanged -> skip upload'}")
        else:
            _deploy_log("no pending_plugin.dll -> restart only (no plugin change)")

        if dry:
            _deploy_log(f"DRY-RUN: would {'UPLOAD then ' if have_update else ''}restart (stop->start) only if empty. "
                        f"Relay alive now: {_relay_alive()}. No action taken."); return 0
        if online_count != 0 and not force:
            _deploy_log(f"ABORT: server has {online_count} player(s) online. Plugin deploy/restart deferred until empty.")
            return 3
        if online_count != 0 and force:
            _deploy_log(f"FORCE: proceeding with {online_count} player(s) online (match will restart / players kicked).")

        # upload the new DLL FIRST, while the server is still up (atomic rename is mmap-safe).
        if have_update:
            uploaded = False
            try:
                ssh, sftp = _open_sftp()
                try:
                    tmp = REMOTE_PLUGIN + ".deploytmp"
                    sftp.put(PENDING_DLL, tmp)
                    try:
                        sftp.posix_rename(tmp, REMOTE_PLUGIN)
                    except Exception:                # noqa: BLE001
                        try: sftp.remove(REMOTE_PLUGIN)
                        except Exception: pass        # noqa: BLE001
                        sftp.rename(tmp, REMOTE_PLUGIN)
                finally:
                    ssh.close()
                _deploy_log(f"uploaded plugin atomically -> {REMOTE_PLUGIN}")
                uploaded = True
            except Exception as e:                   # noqa: BLE001
                _deploy_log(f"WARN: SFTP upload failed ({e}); trying panel files/write fallback")
                try:
                    code = _pt_upload_plugin_bytes(cfg, PENDING_DLL, REMOTE_PLUGIN)
                    _deploy_log(f"uploaded plugin via panel files/write -> {REMOTE_PLUGIN} (HTTP {code})")
                    uploaded = True
                except Exception as e2:              # noqa: BLE001
                    _deploy_log(f"ABORT: upload FAILED SFTP+panel ({e2}). Server untouched (still up). Retry next run.")
                    return 2
            if not uploaded:
                _deploy_log("ABORT: upload did not complete. Server untouched."); return 2

        # restart: STOP -> KILL-if-stuck/hung -> wait offline -> START -> verify via relay.
        # xgamingserver hazard: already-stopping/starting rejects START until KILL clears it.
        rc_restart = 0
        try:
            try:
                st0 = _pt_state(cfg)
            except Exception:                        # noqa: BLE001
                st0 = "?"
            _deploy_log(f"pre-restart panel state={st0}")
            if st0 in ("stopping", "starting"):
                _deploy_log(f"stuck state={st0} -> KILL immediately (no graceful STOP wait)")
                _pt_power_signal(cfg, "kill")
                stopped = False
                for _ in range(20):                  # up to ~60s
                    time.sleep(3)
                    try:
                        if _pt_state(cfg) == "offline":
                            stopped = True; break
                    except Exception:                # noqa: BLE001
                        pass
                _deploy_log("reached offline after stuck-KILL" if stopped
                            else "still not offline after stuck-KILL -> START anyway")
            else:
                _deploy_log("sending STOP ...")
                _pt_power_signal(cfg, "stop")
                stopped = False
                for _ in range(30):                  # up to ~90s
                    time.sleep(3)
                    try:
                        if _pt_state(cfg) == "offline":
                            stopped = True; break
                    except Exception:                # noqa: BLE001
                        pass
                _deploy_log(f"server {'reached offline' if stopped else 'did NOT reach offline in 90s'}")
                if not stopped:
                    # Graceful stop hung -> hard KILL, wait again, then START.
                    _deploy_log("stop timed out -> sending KILL (force-stop)")
                    _pt_power_signal(cfg, "kill")
                    killed = False
                    for _ in range(20):              # up to ~60s
                        time.sleep(3)
                        try:
                            if _pt_state(cfg) == "offline":
                                killed = True; break
                        except Exception:            # noqa: BLE001
                            pass
                    _deploy_log("reached offline after kill" if killed
                                else "still not offline after kill -> starting anyway, CHECK MANUALLY")
            _deploy_log("sending START ...")
            _pt_power_signal(cfg, "start")
            for _ in range(20):                      # up to ~60s for the container to leave offline
                time.sleep(3)
                try:
                    if _pt_state(cfg) != "offline":
                        break
                except Exception:                    # noqa: BLE001
                    pass
            alive = False
            for _ in range(24):                      # up to ~120s for the relay to answer
                time.sleep(5)
                if _relay_alive():
                    alive = True; break
            if alive:
                _deploy_log("OK: server is back and serving (relay verified)")
                if have_update:
                    try:
                        new_sha = _sha256(PENDING_DLL)
                        with open(DEPLOY_HASH, "w") as f:
                            f.write(new_sha)
                        # record the DEPLOYED version (from the staged sidecar) so the web CC can show which
                        # plugin build is actually LIVE, not just what's staged. Atomic (tmp + os.replace).
                        ver = ""
                        pj = os.path.join(_BASE_DIR, "pending_plugin.json")
                        try:
                            if os.path.exists(pj):
                                # utf-8-sig: PowerShell 5.1 stage scripts write the sidecar with a BOM,
                                # which plain utf-8 json.load rejects - that blanked the 1.4.0 header.
                                with open(pj, encoding="utf-8-sig") as pf:
                                    ver = (json.load(pf) or {}).get("version", "")
                        except (OSError, ValueError):
                            ver = ""
                        dj = os.path.join(_BASE_DIR, "deployed_plugin.json")
                        tmpj = dj + ".tmp"
                        with open(tmpj, "w", encoding="utf-8") as df:
                            json.dump({"version": ver, "sha": new_sha[:12],
                                       "deployed_at": time.strftime("%Y-%m-%d %H:%M")}, df)
                        os.replace(tmpj, dj)
                        stamp = time.strftime("%Y%m%d-%H%M")
                        os.replace(PENDING_DLL, PENDING_DLL + ".deployed-" + stamp)
                        # Consume sidecar too — leaving pending_plugin.json after a successful
                        # deploy confuses ops ("update staged") even though _deploy_status needs the DLL.
                        if os.path.exists(pj):
                            try:
                                os.replace(pj, pj + ".deployed-" + stamp)
                            except OSError as e_pj:
                                _deploy_log(f"WARN: could not archive pending_plugin.json: {e_pj}")
                    except OSError as e:
                        _deploy_log(f"WARN: post-deploy bookkeeping failed: {e}")
            else:
                _deploy_log("CRIT: server did not answer the relay within ~3min after start - "
                            "re-sending START and leaving it; CHECK MANUALLY.")
                rc_restart = 4
                try: _pt_power_signal(cfg, "start")
                except Exception: pass                # noqa: BLE001
        except Exception as e:                       # noqa: BLE001
            _deploy_log(f"CRIT: exception during restart ({e}) -> forcing START")
            rc_restart = 4
            try: _pt_power_signal(cfg, "start")
            except Exception: pass                    # noqa: BLE001
        return rc_restart
    finally:
        if not dry:
            try: os.remove(DEPLOY_LOCK)
            except OSError: pass                      # noqa: BLE001
        _deploy_log(f"=== {tag} end ===")
        try:
            lines = open(DEPLOY_LOG, encoding="utf-8").read().splitlines()
            if len(lines) > 400:
                with open(DEPLOY_LOG, "w", encoding="utf-8") as f:
                    f.write("\n".join(lines[-400:]) + "\n")
        except OSError:
            pass


def disable_panel_restart():
    """One-shot: disable the Pterodactyl panel 'Restart' schedule so --deploy-plugin owns the daily
    05:00 restart (avoids a double restart). Reversible: re-enable it in the panel UI any time."""
    cfg = _pt_cfg()
    if cfg.get("err") or not cfg.get("server"):
        print(f"[sched] pterodactyl not configured: {cfg.get('err')}"); return
    d = _pt_api(cfg, "GET", f"/api/client/servers/{cfg['server']}/schedules", None)
    for s in d.get("data", []):
        a = s.get("attributes", {})
        if str(a.get("name", "")).strip().lower() == "restart" and a.get("is_active"):
            c = a.get("cron", {})
            _pt_api(cfg, "POST", f"/api/client/servers/{cfg['server']}/schedules/{a.get('id')}",
                    {"name": a.get("name"), "minute": c.get("minute"), "hour": c.get("hour"),
                     "day_of_month": c.get("day_of_month"), "month": c.get("month"),
                     "day_of_week": c.get("day_of_week"), "is_active": False})
            print(f"[sched] disabled panel schedule '{a.get('name')}' (id {a.get('id')}); "
                  f"the --deploy-plugin job now owns the 05:00 restart")
            return
    print("[sched] no active 'Restart' schedule found (already disabled?)")


BACKUP_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "_server_backup")
NEW_SERVER_NAME = "[ANZ | PvE & PvP | Persistent !rank | !votemap | !help]"
# AI_OPP_LIMIT / AI_OPP_ADDAI / AI_PLR_LIMIT moved UP to the bot-overrides block (see
# _BOT_OVERRIDE_KEYS): they are settings-menu editable now, and a definition down here would be
# executed AFTER the bot_overrides.json loader and silently overwrite the operator's value.


def set_server_name():
    """run.bat --set-server-name: change ServerName in DedicatedServerConfig.json.
    Surgical value replace (rest of the file untouched); local backup first."""
    path = "DedicatedServerConfig.json"
    ssh, sftp = _open_sftp()
    try:
        try:
            with sftp.open(path, "rb") as f:
                text = f.read().decode("utf-8")
        except UnicodeDecodeError:
            print("[name] ABORT: DedicatedServerConfig.json is not valid UTF-8 "
                  "(refusing to round-trip it and risk corruption)")
            return
        cfg = json.loads(text)
        old = cfg.get("ServerName")
        os.makedirs(BACKUP_DIR, exist_ok=True)
        with open(os.path.join(BACKUP_DIR, "DedicatedServerConfig.json.bak"),
                  "w", encoding="utf-8") as bf:
            bf.write(text)
        marker = f'"ServerName": {json.dumps(old)}'
        if text.count(marker) != 1:
            print(f"[name] ABORT: found {text.count(marker)} matches for {marker!r}")
            return
        new_text = text.replace(marker, f'"ServerName": {json.dumps(NEW_SERVER_NAME)}')
        json.loads(new_text)        # verify still valid JSON
        with sftp.open(path, "wb") as f:
            f.write(new_text.encode("utf-8"))
        print(f"[name] ServerName {old!r}\n           ->  {NEW_SERVER_NAME!r}")
        print("[name] takes effect on the next FULL server restart.")
    finally:
        ssh.close()


def _edit_faction_values(text, faction_name, next_faction_name, repls):
    """Surgically replace numeric values for given keys INSIDE one faction object's
    text span (from its "factionName" anchor up to the next faction's anchor, or
    EOF), so an edit can't bleed into another team. repls = [(key, value_regex,
    new_value), ...]. Returns (new_text, error_or_None)."""
    anchor = f'"factionName": "{faction_name}"'
    if text.count(anchor) != 1:
        return text, f"factionName {faction_name!r} x{text.count(anchor)}"
    start = text.index(anchor)
    end = len(text)
    if next_faction_name:
        nanchor = f'"factionName": "{next_faction_name}"'
        if nanchor in text:
            end = text.index(nanchor)
    if end <= start:
        return text, "faction span ordering"
    region = text[start:end]
    for key, valpat, newval in repls:
        region, n = re.subn(rf'("{key}":\s*){valpat}', rf'\g<1>{newval}', region, count=1)
        if n != 1:
            return text, f"{key} replaced x{n} in {faction_name}"
    return text[:start] + region + text[end:], None


def set_ai_limits():
    """run.bat --set-ai-limits [--dry-run]: in every PvE CO-OP mission, set the
    OPPOSING (AI, preventJoin==true) team's AIAircraftLimit -> AI_OPP_LIMIT (8) and
    addAIPerEnemyPlayer -> AI_OPP_ADDAI (0.75), AND the PLAYER (preventJoin==false)
    team's AIAircraftLimit -> AI_PLR_LIMIT (6). PvP missions (no preventJoin==true
    team, e.g. 'Escalation') are skipped automatically. Surgical: only those three
    numbers change, verified by a full deep-diff of the re-parsed JSON. Local backup
    of each original; --dry-run previews without uploading."""
    dry = "--dry-run" in sys.argv
    MISSIONS_DIR = "NuclearOption-Missions"
    ssh, sftp = _open_sftp()
    try:
        os.makedirs(BACKUP_DIR, exist_ok=True)
        folders = sorted(f for f in sftp.listdir(MISSIONS_DIR) if not f.startswith("."))
        changed = skipped = 0
        print(f"[ai] {'DRY-RUN: ' if dry else ''}{len(folders)} mission folder(s)\n")
        for folder in folders:
            remote_json = f"{MISSIONS_DIR}/{folder}/{folder}.json"
            try:
                with sftp.open(remote_json, "rb") as f:
                    text = f.read().decode("utf-8")
                d = json.loads(text)
            except Exception as e:                       # noqa: BLE001
                print(f"  SKIP  {folder:42} (read/parse: {e})"); skipped += 1; continue
            factions = d.get("factions")
            if not isinstance(factions, list):
                print(f"  SKIP  {folder:42} (no factions[])"); skipped += 1; continue
            opp = [fa for fa in factions
                   if fa.get("preventJoin") is True and "AIAircraftLimit" in fa]
            plr = [fa for fa in factions
                   if fa.get("preventJoin") is False and "AIAircraftLimit" in fa]
            if len(opp) != 1 or len(plr) != 1:
                print(f"  SKIP  {folder:42} (opp={len(opp)} player={len(plr)} - not a co-op layout)")
                skipped += 1; continue
            # order the named factions by their position in the text so each edit is
            # bounded to a single faction object (anchor .. next factionName)
            named = [fa for fa in factions if fa.get("factionName")]
            order = sorted(named, key=lambda fa: text.find(f'"factionName": "{fa["factionName"]}"'))
            def _next_name(fa):
                i = order.index(fa)
                return order[i + 1]["factionName"] if i + 1 < len(order) else None

            new_text, err = text, None
            for fa, repls in ((opp[0], [("AIAircraftLimit", r"-?\d+", AI_OPP_LIMIT),
                                        ("addAIPerEnemyPlayer", r"-?[\d.eE+]+", AI_OPP_ADDAI)]),
                              (plr[0], [("AIAircraftLimit", r"-?\d+", AI_PLR_LIMIT)])):
                new_text, err = _edit_faction_values(new_text, fa["factionName"], _next_name(fa), repls)
                if err:
                    break
            if err:
                print(f"  SKIP  {folder:42} ({err})"); skipped += 1; continue

            # verify ONLY the three intended numbers changed (full deep-diff)
            expected = json.loads(text)
            for fa in expected["factions"]:
                if fa.get("preventJoin") is True and "AIAircraftLimit" in fa:
                    fa["AIAircraftLimit"] = AI_OPP_LIMIT
                    fa["addAIPerEnemyPlayer"] = AI_OPP_ADDAI
                elif fa.get("preventJoin") is False and "AIAircraftLimit" in fa:
                    fa["AIAircraftLimit"] = AI_PLR_LIMIT
            try:
                got = json.loads(new_text)
            except json.JSONDecodeError as e:
                print(f"  FAIL  {folder:42} (result not valid JSON: {e})"); skipped += 1; continue
            if got != expected:
                print(f"  FAIL  {folder:42} (deep-diff: unintended change - NOT uploaded)")
                skipped += 1; continue

            print(f"  OK    {folder:42} "
                  f"{opp[0].get('factionName'):8} AI {opp[0]['AIAircraftLimit']}->{AI_OPP_LIMIT} "
                  f"addAI {opp[0].get('addAIPerEnemyPlayer'):.3g}->{AI_OPP_ADDAI} | "
                  f"{plr[0].get('factionName'):8} AI {plr[0]['AIAircraftLimit']}->{AI_PLR_LIMIT}")
            if not dry:
                with open(os.path.join(BACKUP_DIR, f"{folder}.json"), "w",
                          encoding="utf-8") as bf:
                    bf.write(text)
                with sftp.open(remote_json, "wb") as f:
                    f.write(new_text.encode("utf-8"))
            changed += 1
        print(f"\n[ai] {'would change' if dry else 'changed'} {changed}, skipped {skipped}.")
        if not dry and changed:
            print("[ai] backups in _server_backup/. Takes effect as each mission loads "
                  "(or on restart).")
    finally:
        ssh.close()


def apply_map_changes():
    """run.bat --apply-map-changes [--dry-run]: on every PvE CO-OP mission (one with a
    preventJoin==true AI team; the PvP 'Escalation' has none, so it's skipped) set the
    EW1 + FastBomber1 factories' productionTime -> 600 (Medusa/Alkyon EW planes + the fast
    bomber) and wrecksMaxNumber -> 1000, wrecksDecayTime -> 5.0. ALSO set DedicatedServerConfig
    PostMissionDelay -> the DERIVED vote+delay value (_effective_pmd, default 45) so the end-of-match map
    vote has time to apply before the rotation, matching the bot/webcc sync. ONLY those values change:
    targeted text edits, then a re-parse + full deep-diff guard (won't upload anything else). Idempotent;
    local backups in _server_backup/."""
    import re as _re
    dry = "--dry-run" in sys.argv
    MISSIONS_DIR = "NuclearOption-Missions"
    THROTTLE_CODES = ("EW1", "FastBomber1")          # Medusa/Alkyon (EW) + the fast bomber -> 600s
    _codes_re = "|".join(_re.escape(c) for c in THROTTLE_CODES)

    def _expected(obj):                              # logical version of the edits, for the diff guard
        if isinstance(obj, dict):
            if "wrecksMaxNumber" in obj:
                obj["wrecksMaxNumber"] = 1000
            if "wrecksDecayTime" in obj:
                obj["wrecksDecayTime"] = 5.0
            fo = obj.get("factoryOptions")
            if isinstance(fo, dict) and fo.get("productionType") in THROTTLE_CODES:
                fo["productionTime"] = 600.0
            for v in obj.values():
                _expected(v)
        elif isinstance(obj, list):
            for v in obj:
                _expected(v)

    def _edit(text):
        new = _re.sub(r'"wrecksMaxNumber": \d+', '"wrecksMaxNumber": 1000', text)
        new = _re.sub(r'"wrecksDecayTime": [\d.]+', '"wrecksDecayTime": 5.0', new)
        new, n_fac = _re.subn(
            r'("productionType": "(?:' + _codes_re + r')",\s+"productionTime": )[\d.]+',
            r'\g<1>600.0', new)
        return new, n_fac

    ssh, sftp = _open_sftp()
    try:
        os.makedirs(BACKUP_DIR, exist_ok=True)
        folders = sorted(f for f in sftp.listdir(MISSIONS_DIR) if not f.startswith("."))
        changed = skipped = 0
        print(f"[map] {'DRY-RUN: ' if dry else ''}{len(folders)} mission folder(s)\n")
        for folder in folders:
            remote_json = f"{MISSIONS_DIR}/{folder}/{folder}.json"
            try:
                with sftp.open(remote_json, "rb") as f:
                    text = f.read().decode("utf-8")
                d = json.loads(text)
            except Exception as e:                   # noqa: BLE001
                print(f"  SKIP  {folder:42} (read/parse: {e})"); skipped += 1; continue
            factions = d.get("factions")
            if not (isinstance(factions, list) and any(fa.get("preventJoin") is True for fa in factions)):
                print(f"  SKIP  {folder:42} (PvP / no AI team)"); skipped += 1; continue
            new_text, n_fac = _edit(text)
            # GUARD: re-parse + deep-diff that ONLY the intended values changed
            try:
                got = json.loads(new_text)
            except json.JSONDecodeError as e:
                print(f"  FAIL  {folder:42} (result not valid JSON: {e})"); skipped += 1; continue
            expected = json.loads(text)
            _expected(expected)
            if got != expected:
                print(f"  FAIL  {folder:42} (deep-diff: unintended change - NOT uploaded)")
                skipped += 1; continue
            if new_text == text:
                print(f"  ok    {folder:42} (already set; throttled factories={n_fac})"); continue
            if not dry:
                bpath = os.path.join(BACKUP_DIR, f"{folder}.json")
                if not os.path.exists(bpath):        # keep the earliest (pre-throttle) backup
                    with open(bpath, "w", encoding="utf-8") as bf:
                        bf.write(text)
                with sftp.open(remote_json, "wb") as f:
                    f.write(new_text.encode("utf-8"))
            print(f"  OK    {folder:42} throttled factories->600: {n_fac}; wrecks 1000/5")
            changed += 1

        # --- DedicatedServerConfig: PostMissionDelay -> DERIVED (vote + delay), not a hardcoded 80 ---
        # FIX 3: write the SAME derived value the bot syncs (vote_duration + post_vote_delay) so this CLI
        # path and the webcc/settings path never fight over PostMissionDelay.
        derived_pmd = float(_effective_pmd())
        cfg = "DedicatedServerConfig.json"
        with sftp.open(cfg, "rb") as f:
            ctext = f.read().decode("utf-8")
        cnew, ncfg = _re.subn(r'"PostMissionDelay": [\d.]+', f'"PostMissionDelay": {derived_pmd}', ctext)
        exp_cfg = json.loads(ctext); exp_cfg["PostMissionDelay"] = derived_pmd
        if ncfg and cnew != ctext and json.loads(cnew) == exp_cfg:
            if not dry:
                with open(os.path.join(BACKUP_DIR, "DedicatedServerConfig.json.bak"), "w", encoding="utf-8") as bf:
                    bf.write(ctext)
                with sftp.open(cfg, "wb") as f:
                    f.write(cnew.encode("utf-8"))
            print(f"  OK    DedicatedServerConfig PostMissionDelay -> {derived_pmd} (vote {vote_duration()}s + delay {post_vote_delay()}s)")
        else:
            print(f"  ok    DedicatedServerConfig PostMissionDelay unchanged (matches={ncfg})")

        print(f"\n[map] {'would change' if dry else 'changed'} {changed} mission(s), skipped {skipped}.")
        if not dry:
            print("[map] missions apply as each next loads; PostMissionDelay needs reload-config or a restart.")
    finally:
        ssh.close()


def fix_starting_ranks():
    """run.bat --check-ranks | --fix-ranks: ensure each PvE CO-OP mission's
    playerStartingRank is correct -- ALL co-ops (Escalation + Terminal Control) -> 2
    (lowered 2026-07-03 from 3/4 at the user's request so the RANK CATCH-UP feature has
    room to climb: floor starts at 2 and rises +1 per 20min to 5; the old baked-in 3/4
    overrode the catch-up base. Only this rank field changes, money/everything else
    untouched). PvP missions (no preventJoin AI team) are left untouched. Surgical regex
    on that ONE field, then a re-parse + full deep-diff guard (won't upload if anything
    else moved). A separate '.rankbak.json' backup is kept so the pre-throttle backup
    isn't clobbered. --check-ranks is read-only; --fix-ranks uploads. Applies as each
    mission NEXT loads."""
    import re as _re
    fix = "--fix-ranks" in sys.argv
    MISSIONS_DIR = "NuclearOption-Missions"
    ssh, sftp = _open_sftp()
    try:
        os.makedirs(BACKUP_DIR, exist_ok=True)
        folders = sorted(f for f in sftp.listdir(MISSIONS_DIR) if not f.startswith("."))
        wrong = fixed = skipped = 0
        print(f"[rank] {'FIX' if fix else 'CHECK (read-only)'}: {len(folders)} mission folder(s)\n")
        for folder in folders:
            remote_json = f"{MISSIONS_DIR}/{folder}/{folder}.json"
            try:
                with sftp.open(remote_json, "rb") as f:
                    text = f.read().decode("utf-8")
                d = json.loads(text)
            except Exception as e:                       # noqa: BLE001
                print(f"  SKIP  {folder:44} (read/parse: {e})"); skipped += 1; continue
            factions = d.get("factions")
            if not (isinstance(factions, list) and any(fa.get("preventJoin") is True for fa in factions)):
                print(f"  skip  {folder:44} (PvP / no AI team)"); skipped += 1; continue
            low = folder.lower()
            if "terminal" in low:
                want = 2      # was 4 (2026-06-26); lowered 2026-07-03 so rank catch-up (base 2) has room to climb
            elif "escalation" in low:
                want = 2      # was 3 (2026-06-26); same reason
            else:
                print(f"  SKIP  {folder:44} (unknown type - not touching)"); skipped += 1; continue
            ms = d.get("missionSettings")
            if not isinstance(ms, dict):
                print(f"  SKIP  {folder:44} (no missionSettings block)"); skipped += 1; continue
            cur = ms.get("playerStartingRank")        # the field lives in missionSettings
            if cur == want:
                print(f"  ok    {folder:44} rank {cur}"); continue
            wrong += 1
            print(f"  WRONG {folder:44} rank {cur} -> {want}")
            if not fix:
                continue
            if "playerStartingRank" in ms:            # present but wrong value -> replace
                new_text, n = _re.subn(r'"playerStartingRank": \d+',
                                       f'"playerStartingRank": {want}', text, count=1)
            else:                                     # missing -> insert after its sibling allowRespawn
                new_text, n = _re.subn(
                    r'(\n(\s*)"allowRespawn":\s*(?:true|false),)',
                    r'\1\n\g<2>"playerStartingRank": ' + str(want) + ',', text, count=1)
            if n != 1:
                print(f"        FAIL  anchor matched {n}x (expected 1) - skipped"); skipped += 1; continue
            try:
                got = json.loads(new_text)
            except json.JSONDecodeError as e:
                print(f"        FAIL  result not valid JSON: {e} - skipped"); skipped += 1; continue
            expected = json.loads(text); expected["missionSettings"]["playerStartingRank"] = want
            if got != expected:
                print(f"        FAIL  deep-diff: unintended change - NOT uploaded"); skipped += 1; continue
            bpath = os.path.join(BACKUP_DIR, f"{folder}.rankbak.json")
            if not os.path.exists(bpath):
                with open(bpath, "w", encoding="utf-8") as bf:
                    bf.write(text)
            with sftp.open(remote_json, "wb") as f:
                f.write(new_text.encode("utf-8"))
            fixed += 1
            print(f"        OK    uploaded ({cur} -> {want})")
        verb = "fixed" if fix else "would fix"
        print(f"\n[rank] {verb} {fixed if fix else wrong} mission(s); skipped {skipped}.")
        if fix and fixed:
            print("[rank] takes effect as each corrected mission NEXT loads "
                  "(restart or wait for rotation).")
    finally:
        ssh.close()


def set_balance_diff():
    """run.bat --set-balance-diff <n>: set the NukeStats plugin's [Balance] MaxDifference in the
    LIVE server config (BepInEx/config/anz.nukestats.cfg). Team balancing only triggers when a side
    is MORE than n ahead (n=2 => a 2-player gap is tolerated, only a 3+ gap acts; higher = fewer,
    less-twitchy moves). Surgical line-anchored single-line edit + re-read verify. BepInEx watches
    the config file so a running plugin can pick this up live; it's also what the plugin reads on its
    next load/deploy (so the staged v0.9.0 inherits it)."""
    import re as _re
    rest = [a for a in sys.argv[sys.argv.index("--set-balance-diff") + 1:] if not a.startswith("--")]
    if not rest or not rest[0].isdigit():
        print("usage: run.bat --set-balance-diff <n>   (whole number 0..10)"); return
    n = int(rest[0])
    if n > 10:
        print("[balance] refusing a MaxDifference > 10 (sanity guard)"); return
    CFG = "BepInEx/config/anz.nukestats.cfg"
    ssh, sftp = _open_sftp()
    try:
        with sftp.open(CFG, "rb") as f:
            text = f.read().decode("utf-8")
        cur = _re.search(r'(?m)^MaxDifference\s*=\s*(\d+)\s*$', text)
        if not cur:
            print("[balance] ABORT: no '^MaxDifference = <n>' line in the config"); return
        new, c = _re.subn(r'(?m)^(MaxDifference\s*=\s*)\d+\s*$', r'\g<1>' + str(n), text, count=1)
        if c != 1:
            print(f"[balance] ABORT: expected exactly 1 MaxDifference line, found {c}"); return
        if new == text:
            print(f"[balance] MaxDifference already {n} - nothing to do"); return
        tmp = CFG + ".tmp"
        with sftp.open(tmp, "wb") as f:
            f.write(new.encode("utf-8"))
        try:
            sftp.remove(CFG)
        except Exception:        # noqa: BLE001
            pass
        sftp.posix_rename(tmp, CFG)
        with sftp.open(CFG, "rb") as f:
            back = f.read().decode("utf-8")
        ok = _re.search(r'(?m)^MaxDifference\s*=\s*' + str(n) + r'\s*$', back) is not None
        print(f"[balance] MaxDifference {cur.group(1)} -> {n}: {'OK' if ok else 'VERIFY FAILED'}")
        print("[balance] BepInEx watches the cfg (can take effect live); fully applies with the v0.9.0 leave-only autobalance.")
    finally:
        ssh.close()


def set_votekick():
    """run.bat --set-votekick <on|off>: enable/disable the game's built-in VoteKick (player vote-to-kick)
    in DedicatedServerConfig.json -- the only player-facing kick feature. Surgical single-token edit on
    VoteKick.Enabled + a JSON round-trip + full deep-diff guard (won't upload if anything else moved),
    a local backup, then a reload-config so it applies without a full restart (also applies on the next
    mission load / restart). NOTE: this is SEPARATE from the send-buffer-overflow mass-disconnect."""
    import re as _re
    rest = [a for a in sys.argv[sys.argv.index("--set-votekick") + 1:] if not a.startswith("--")]
    val = rest[0].lower() if rest else ""
    if val not in ("on", "off", "true", "false", "enable", "disable"):
        print("usage: run.bat --set-votekick <on|off>"); return
    want = val in ("on", "true", "enable")
    path = "DedicatedServerConfig.json"
    ssh, sftp = _open_sftp()
    try:
        with sftp.open(path, "rb") as f:
            text = f.read().decode("utf-8")
        cfg = json.loads(text)
        vk = cfg.get("VoteKick")
        if not isinstance(vk, dict) or "Enabled" not in vk:
            print("[votekick] ABORT: no VoteKick.Enabled block in config"); return
        if bool(vk.get("Enabled")) == want:
            print(f"[votekick] already {'ENABLED' if want else 'DISABLED'} - nothing to do"); return
        new_text, n = _re.subn(r'("Enabled"\s*:\s*)(?:true|false)',
                               r'\g<1>' + ("true" if want else "false"), text, count=1)
        if n != 1:
            print(f"[votekick] ABORT: expected exactly 1 'Enabled' key, found {n} - not touching"); return
        try:
            got = json.loads(new_text)
        except json.JSONDecodeError as e:
            print(f"[votekick] ABORT: result not valid JSON: {e}"); return
        expected = json.loads(text); expected["VoteKick"]["Enabled"] = want
        if got != expected:
            print("[votekick] ABORT: deep-diff shows an unintended change - NOT uploaded"); return
        os.makedirs(BACKUP_DIR, exist_ok=True)
        bpath = os.path.join(BACKUP_DIR, "DedicatedServerConfig.votekickbak.json")
        if not os.path.exists(bpath):
            with open(bpath, "w", encoding="utf-8") as bf:
                bf.write(text)
        with sftp.open(path, "wb") as f:
            f.write(new_text.encode("utf-8"))
        print(f"[votekick] VoteKick.Enabled {vk.get('Enabled')} -> {want}: uploaded (backup in _server_backup/)")
    finally:
        ssh.close()
    try:
        rc = RemoteCommand(RCMD_HOST, RCMD_PORT)
        resp = rc.send("reload-config")
        print(f"[votekick] reload-config -> {resp!r}")
    except Exception as e:        # noqa: BLE001
        print(f"[votekick] reload-config failed ({e}); applies on the next mission load / restart anyway")
    print(f"[votekick] VoteKick is now {'ON' if want else 'OFF'} (full effect on reload-config / next mission / restart).")


# ============ Server Settings tab: edit DedicatedServerConfig.json (remote/SFTP) + mirror to gpanel ============
# cc_web has no SFTP, so the webcc routes BOTH the read (dumpserverconfig) and write (setserverconfig) through
# the bot. We read the config, set ONE dotted-path field on the parsed object, re-serialize (a json round-trip
# is game-safe), back up the original, write it back, reload-config (best-effort), and mirror the change to the
# Pterodactyl panel startup variables so a re-templating boot doesn't revert it.
SRVCFG_PATH = "DedicatedServerConfig.json"
_SRVCFG_UNSET = object()
# (dotted-key, label, type, mask, needs_restart, note)
_SRVCFG_SCHEMA = [
    ("ServerName",            "Server name",            "str",     False, True,  "Shown in the public server browser."),
    ("Password",              "Join password",          "str",     True,  True,  "Blank = open server. Masked here."),
    ("MaxPlayers",            "Max players",            "int",     False, True,  "Player cap."),
    ("Port.Value",            "Game port",              "int",     False, True,  "UDP game port. On a panel, the port must also be allocated in gpanel."),
    ("QueryPort.Value",       "Query port",             "int",     False, True,  "Steam query port. Panel-allocated."),
    ("Hidden",                "Hidden from browser",    "bool",    False, True,  "Hide from the public server list."),
    ("ModdedServer",          "Modded server",          "strbool", False, True,  "Whether mods are enabled."),
    ("DisableErrorKick",      "Stop error-kicks (leave ON)", "bool",  False, False,
     "ON = the game NEVER kicks a client for desync errors. Leave it ON. When the game does "
     "error-kick someone it also starts a 300-SECOND rejoin lockout, and every rejoin attempt "
     "during it adds another 10s - the player only sees 'Local client stopped' and thinks their "
     "game broke. Simply DYING can trigger it (the client sends updates for the aircraft it just "
     "lost). Applies on the next server restart."),
    ("PostMissionDelay",      "Post-mission delay (s)", "float",   False, False, "Seconds between mission end and the next load (the bot tunes this so the end-of-match map vote can run)."),
    ("NoPlayerStopTime",      "Empty-stop time (s)",    "float",   False, False, "Seconds with no players before the match stops."),
    ("VoteKick.Enabled",      "Vote-kick enabled",      "bool",    False, False, "Players can vote to kick (the game's built-in feature)."),
    ("VoteKick.PassRatio",    "Vote-kick pass ratio",   "float",   False, False, "Fraction of yes-votes needed (0-1)."),
    ("VoteKick.MinVotes",     "Vote-kick min votes",    "int",     False, False, "Minimum votes to start one."),
    ("VoteKick.VoteDuration", "Vote-kick duration (s)", "float",   False, False, "How long a vote runs."),
    # --- added by the 2026-07-27 game update. A config written before it has none of these keys;
    #     _srvcfg_walk(create=True) adds them on first save, so they are editable either way.
    ("VoteKick.AutoBanThreshold", "Vote-kick auto-ban after N", "int", False, False, "Successful vote-kicks after which the game auto-BANS the player. 0 = never auto-ban."),
    ("VoteKick.NewVoteLockout", "Lockout before next vote (s)", "float", False, False, "Seconds after a vote resolves before another may open."),
    ("VoteKick.RequesterCooldown", "Per-requester cooldown (s)", "float", False, False, "Seconds before the same player may start another vote-kick."),
    ("VoteKick.ResolutionDisplayTime", "Result banner time (s)", "float", False, False, "How long the vote result banner stays on screen."),
    ("ErrorKickImmuneListPaths", "Error-kick immune lists", "list", False, False, "Comma-separated paths to files listing players exempt from the desync error-kick. Finer-grained than Disable error-kick."),
]
_SRVCFG_MAP = {k: (lbl, typ, mask, nr, note) for (k, lbl, typ, mask, nr, note) in _SRVCFG_SCHEMA}
_srvcfg_cache = {"ok": False, "err": "not loaded yet", "ts": 0, "values": {}, "last_set": None}


def _srvcfg_walk(d, dotted, set_to=_SRVCFG_UNSET, create=False):
    """Get/set a dotted-path leaf. With create=True (writes only), missing intermediate dicts and the
    leaf are CREATED — a config born from a slim installer template lacks optional keys (VoteKick.*,
    PostMissionDelay, ...) and refusing to add them made every save of those fields silently fail."""
    parts = dotted.split(".")
    cur = d
    for p in parts[:-1]:
        if not isinstance(cur, dict):
            return None
        if p not in cur:
            if not (create and set_to is not _SRVCFG_UNSET):
                return None
            cur[p] = {}
        cur = cur[p]
    last = parts[-1]
    if not isinstance(cur, dict):
        return None
    if last not in cur and not (create and set_to is not _SRVCFG_UNSET):
        return None
    if set_to is not _SRVCFG_UNSET:
        cur[last] = set_to
    return cur[last]


def _srvcfg_coerce(typ, value):
    if typ == "bool":
        return value if isinstance(value, bool) else str(value).strip().lower() in ("1", "true", "on", "yes")
    if typ == "strbool":
        b = value if isinstance(value, bool) else str(value).strip().lower() in ("1", "true", "on", "yes")
        return "true" if b else "false"
    if typ == "int":
        return int(float(value))
    if typ == "float":
        return float(value)
    if typ == "list":
        # JSON array of strings. The panel edits it as a plain comma-separated text field, so a
        # write arrives as "a.txt, b.txt" — but a programmatic caller may already pass a list.
        if isinstance(value, (list, tuple)):
            return [str(s).strip() for s in value if str(s).strip()]
        return [s.strip() for s in str(value).split(",") if s.strip()]
    return str(value)


def _srvcfg_read():
    try:
        ssh, sftp = _open_sftp()
    except Exception as e:                       # noqa: BLE001
        return None, f"sftp connect: {e}"
    try:
        with sftp.open(SRVCFG_PATH, "rb") as f:
            return json.loads(f.read().decode("utf-8")), None
    except Exception as e:                        # noqa: BLE001
        return None, str(e)
    finally:
        try:
            ssh.close()
        except Exception:                         # noqa: BLE001
            pass


def refresh_server_config():
    cfg, err = _srvcfg_read()
    if err:
        _srvcfg_cache.update({"ok": False, "err": err, "ts": time.time()})
        return
    values = {}
    for (k, lbl, typ, mask, nr, note) in _SRVCFG_SCHEMA:
        v = _srvcfg_walk(cfg, k)
        if typ == "strbool":
            v = str(v).strip().lower() == "true"
        values[k] = ("********" if (mask and v) else v)
    _srvcfg_cache.update({"ok": True, "err": None, "ts": time.time(), "values": values})


# FIX 3: DedicatedServerConfig fields the operator must NOT set by hand — the bot DERIVES + syncs them.
# PostMissionDelay is now = vote_duration + post_vote_delay (sync_effective_pmd), so it's hidden from the
# Server Config tab too (not just Game Settings) to remove the last raw-mission-delay knob. It stays in
# _SRVCFG_SCHEMA so refresh_server_config still mirrors it and set_server_config can still write it.
_SRVCFG_HIDDEN_FIELDS = {"PostMissionDelay"}


def server_config_state():
    vals = _srvcfg_cache.get("values", {})
    fields = [{"key": k, "label": lbl, "type": typ, "mask": mask, "needs_restart": nr,
               "note": note, "value": vals.get(k)}
              for (k, lbl, typ, mask, nr, note) in _SRVCFG_SCHEMA if k not in _SRVCFG_HIDDEN_FIELDS]
    return {"ok": _srvcfg_cache.get("ok"), "err": _srvcfg_cache.get("err"),
            "ts": _srvcfg_cache.get("ts"), "fields": fields, "last_set": _srvcfg_cache.get("last_set"),
            "pending_restart": {k: {"ts": v.get("ts")} for k, v in _srvcfg_pending_load().items()}}


# ── per-field "saved — pending restart" state (PERSISTED, survives bot restarts) ────────────────
# Written on every successful set of a needs_restart field; verified + cleared when the game server
# comes back after a real stop (>=20s down). If a re-templating boot REVERTED the file value, we say
# so loudly and re-apply ONCE. Passwords are recorded with value=None (never persisted to disk).
SRVCFG_PENDING_FILE = os.path.join(_BASE_DIR, "srvcfg_pending.json")


def _srvcfg_pending_load():
    try:
        with open(SRVCFG_PENDING_FILE, encoding="utf-8") as f:
            d = json.load(f)
        return d if isinstance(d, dict) else {}
    except (OSError, ValueError):
        return {}


def _srvcfg_pending_save(d):
    try:
        tmp = SRVCFG_PENDING_FILE + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(d, f, indent=1)
        os.replace(tmp, SRVCFG_PENDING_FILE)
    except OSError:
        pass


def srvcfg_after_restart_check():
    """Called when the game server comes back after a real stop: verify every pending needs_restart
    value is still in the config file (a panel re-templating boot can revert it), clear the pending
    flags, and re-apply ONCE if a value was reverted. Loud either way."""
    # FIX 3 self-heal: PostMissionDelay is DERIVED (vote + delay) and hidden, so it is NOT a pending
    # needs_restart field — re-derive + re-push it after every real restart, since a panel/egg re-template
    # can silently reset it to the egg default and break the vote timing without any knob being touched.
    try:
        refresh_server_config()
        sync_effective_pmd()
    except Exception as _e:                    # noqa: BLE001
        print(f"[srvcfg] PostMissionDelay self-heal failed: {_e}")
    # FIX 4 self-heal: keep the boot map pinned at rotation[0] + Sequence (a re-templating boot can
    # rewrite MissionRotation/RotationType the same way it rewrites ServerName).
    try:
        apply_boot_map_rotation("post-restart self-heal")
    except Exception as _e:                    # noqa: BLE001
        print(f"[boot-map] rotation self-heal failed: {_e}")
    pending = _srvcfg_pending_load()
    if not pending:
        return
    cfg, err = _srvcfg_read()
    if err:
        print(f"[srvcfg] post-restart pending check skipped (read failed: {err})")
        return
    remaining = {}
    for key, rec in pending.items():
        want = rec.get("value")
        have = _srvcfg_walk(cfg, key)
        lbl = (_SRVCFG_MAP.get(key) or (key,))[0]
        if want is None:                       # masked (password): can't verify content — just clear
            activity(f"Server config: {lbl} applied by the restart", "ADMIN")
            continue
        if have == want:
            activity(f"Server config: {lbl} verified in config after the restart - now active", "ADMIN")
            continue
        if rec.get("reapplied"):
            activity(f"Server config: {lbl} REVERTED AGAIN after re-apply (panel keeps overwriting it) - "
                     f"set it in the gpanel startup variables instead", "!")
            continue
        activity(f"Server config: {lbl} was REVERTED by the restart (panel re-templating) - re-applying", "!")
        res = set_server_config(key, want)
        if res.get("ok"):
            rec["reapplied"] = True
            rec["ts"] = time.time()
            remaining[key] = rec
        else:
            activity(f"Server config: re-apply of {lbl} FAILED: {res.get('error')}", "!")
    _srvcfg_pending_save(remaining)
    refresh_server_config()


def _srvcfg_panel_mirror(key, old, new):
    """Best-effort: push the change to the matching Pterodactyl startup VARIABLE so gpanel matches and a
    re-templating boot won't revert it. Matched by the var's current server_value == the OLD config value
    (env-var names are egg-specific). Never fails the config write."""
    cfg = _pt_cfg()
    if cfg.get("err"):
        return {"mirrored": False, "reason": cfg["err"]}
    try:
        d = _pt_api(cfg, "GET", f"/api/client/servers/{cfg['server']}/startup", None)
        attrs = [v.get("attributes", {}) for v in d.get("data", [])]
    except Exception as e:                         # noqa: BLE001
        return {"mirrored": False, "reason": f"list: {e}"}
    # 1st: explicit env-var names for the common egg fields (value-matching can hit the wrong var
    # on coincidental values, and can NEVER match bools: Python str(False) != panel "false").
    known = {"ServerName": "SERVER_NAME", "MaxPlayers": "MAX_PLAYERS", "Password": "SERVER_PASSWORD",
             "Port.Value": "SERVER_PORT", "QueryPort.Value": "QUERY_PORT"}
    target = None
    if key in known:
        target = next((a for a in attrs if a.get("is_editable")
                       and str(a.get("env_variable", "")).upper() == known[key]), None)
    if target is None:                             # fallback: match by current value, bool-normalized
        olds = str(old)
        target = next((a for a in attrs if a.get("is_editable")
                       and str(a.get("server_value")).lower() == olds.lower()), None)
    if target is None:
        return {"mirrored": False, "reason": "no editable panel variable matched (config-file only)"}
    try:
        _pt_api(cfg, "PUT", f"/api/client/servers/{cfg['server']}/startup/variable",
                {"key": target.get("env_variable"), "value": str(new)})
        return {"mirrored": True, "var": target.get("env_variable")}
    except Exception as e:                         # noqa: BLE001
        return {"mirrored": False, "reason": f"put failed (key may be read-only): {e}"}


# ── Mission audit: official vs custom/workshop missions + integrity (pool-divergence status) ──
# Missions live in DedicatedServerConfig.MissionDirectory as <name>/<name>.json (Group "User"), plus any
# {Group:"Workshop",Name:<id>} rotation entries. OFFICIAL_MISSIONS = the curated pool this server ships;
# anything else present/enabled = unofficial. Official mission JSONs are hashed vs a trust-on-first-use
# baseline (mission_baseline.json) to detect edits. ALL READ-ONLY over SFTP (never writes mission files).
MISSION_BASELINE_FILE = os.path.join(_BASE_DIR, "mission_baseline.json")
_mission_audit_cache = {"ts": 0.0, "data": {"loaded": False}}


def refresh_mission_audit(deep=True):
    """SFTP-read the mission layout, classify official vs unofficial, and compute pool status
    (`eligible` = all-official & unedited). Cached. Read-only.

    deep=True  also sha256s every official mission file against the trust-on-first-use baseline to
               detect edits. That downloads every official mission (~15 MB here), so it is for
               OPERATOR-REQUESTED scans only.
    deep=False lists and classifies without reading mission bodies - what the ballot actually needs.
               The unattended periodic refresh uses this.

    A FAILED scan never replaces good cached data (audit 2026-08-01): it used to publish its empty
    result dict, so one transient SFTP blip blanked every custom mission until the next success. On a
    server whose built-in PvP modes are all off, that produced a ballot with ZERO PvP options - exactly
    the blindness the startup scan was added to prevent."""
    import hashlib
    d = {"loaded": True, "official": [], "unofficial": [], "edited": [], "missing": [],
         "mission_dir": "", "eligible": True, "reasons": [], "error": None, "scanned": False,
         "deep": bool(deep), "integrity_verified": bool(deep)}
    cfg, err = _srvcfg_read()
    if err or not isinstance(cfg, dict):
        d["error"] = err or "could not read DedicatedServerConfig.json"
        return _publish_mission_audit(d)
    mdir = str(cfg.get("MissionDirectory", "") or "").rstrip("/")
    d["mission_dir"] = mdir
    rot = []
    for e in (cfg.get("MissionRotation", []) or []):
        k = e.get("Key", {}) if isinstance(e, dict) else {}
        rot.append((str(k.get("Group", "")), str(k.get("Name", ""))))
    rot_names = {n for _, n in rot}
    base = {}
    try:
        with open(MISSION_BASELINE_FILE, encoding="utf-8") as f:
            base = json.load(f)
    except (OSError, ValueError):
        base = {}
    newbase = dict(base)

    # The SFTP session is rooted at the container home (the bot reads DedicatedServerConfig.json by a
    # RELATIVE path), but MissionDirectory is an absolute /home/<user>/... path -> resolve to candidates.
    cands = [mdir, mdir.lstrip("/")]
    _mp = mdir.lstrip("/").split("/")
    if len(_mp) >= 2 and _mp[0] == "home":
        cands.append("/".join(_mp[2:]))                         # drop /home/<user>/ -> relative to the SFTP root
    cands = [c for i, c in enumerate(cands) if c and c not in cands[:i]]

    def _op(sftp):
        # IDEMPOTENT (audit 2026-08-01): _sftp_op reconnects and re-runs this callback after an
        # exception, so appending straight into `d` duplicated every mission row on any retry - the
        # panel then showed each mission twice and the ballot pool counted it twice. Everything below
        # accumulates into LOCALS and is assigned onto `d` only once the scan has fully succeeded, so a
        # retry always starts from a clean slate.
        mb = None
        on_disk = set()
        for c in cands:
            try:
                on_disk = set(sftp.listdir(c)); mb = c; break
            except Exception:                                   # noqa: BLE001
                continue
        if mb is None:
            raise IOError("mission dir not accessible via SFTP (tried: " + ", ".join(cands) + ")")
        l_official, l_unofficial, l_edited, l_missing = [], [], [], []
        for grp, name in rot:
            official = (grp != "Workshop") and (name in OFFICIAL_MISSIONS)
            # A rotation entry whose folder is NOT on disk cannot be loaded by the game. Marking it
            # enabled put a dead mission on the ballot; winning it left the rotation stuck.
            present = name in on_disk
            row = {"name": name, "group": grp, "enabled": present, "official": official}
            if not present:
                row["missing"] = True
                l_missing.append(name)
            if official:
                # Hashing every official mission means downloading ~15 MB (some are >1 MB each). That
                # is only needed for the integrity check, so it runs on an operator-requested DEEP scan
                # only - never on the unattended periodic refresh, which just needs the mission LIST.
                if deep and present:
                    try:
                        with sftp.open(mb + "/" + name + "/" + name + ".json", "rb") as f:
                            h = hashlib.sha256(f.read()).hexdigest()
                        newbase.setdefault(name, h)             # trust-on-first-use baseline
                        if newbase[name] != h:
                            l_edited.append(name); row["edited"] = True
                    except IOError:
                        if name not in l_missing:
                            l_missing.append(name)
                        row["missing"] = True
                l_official.append(row)
            else:
                l_unofficial.append(row)
        for fold in sorted(on_disk):                            # uploaded-but-not-rotated folders
            if fold in OFFICIAL_MISSIONS or any(u["name"] == fold for u in l_unofficial):
                continue
            l_unofficial.append({"name": fold, "group": "User", "enabled": fold in rot_names, "official": False})
        # commit only on full success
        d["mission_dir"] = mb
        d["dirlist"] = sorted(on_disk)[:50]
        d["official"], d["unofficial"] = l_official, l_unofficial
        d["edited"], d["missing"] = l_edited, l_missing
        d["scanned"] = True

    try:
        _sftp_op(_op)
    except Exception as e:                                      # noqa: BLE001
        # str(e) is EMPTY for several paramiko/socket exceptions. An empty string is falsy, so the
        # caller read "no error", reset its backoff and logged success while holding an empty scan.
        d["error"] = str(e) or f"{type(e).__name__} (no message)"
    if not d.get("scanned") and not d.get("error"):
        d["error"] = "mission scan did not complete"
    if newbase != base:
        try:
            tmp = MISSION_BASELINE_FILE + ".tmp"
            with open(tmp, "w", encoding="utf-8") as f:
                json.dump(newbase, f, indent=1)
            os.replace(tmp, MISSION_BASELINE_FILE)
        except OSError:
            pass
    if any(u.get("enabled") for u in d["unofficial"]):
        d["eligible"] = False
        d["reasons"].append("an unofficial / workshop mission is enabled")
    if d["edited"]:
        d["eligible"] = False
        d["reasons"].append("official mission edited: " + ", ".join(d["edited"][:6]))
    return _publish_mission_audit(d)


def _publish_mission_audit(d):
    """Publish a scan result, but NEVER let a failed scan destroy good cached data.

    _enabled_custom_names() reads this cache and every path that can put a custom mission on a ballot
    goes through it, so replacing it with an empty result on a transient SFTP error takes the entire
    custom/PvP-variant pool off the ballot until the next success. Stale data is always better than no
    data here: the previous scan still describes the missions on the container.

    The error and its timestamp are recorded on the retained data so the panel can show that the last
    refresh failed without losing what it is showing."""
    # "loaded" means THIS RESULT IS AN AUTHORITY, not merely "the function ran". A scan that never
    # listed the mission directory knows about no missions, and callers that gate on loaded (the
    # set_votemap_cfg whitelist filters) would then strip every custom mission out of the operator's
    # weights and guaranteed pins and SAVE that to disk - deleting them for good. On the first scan
    # after a restart there is no previous cache to fall back on, so this is the only guard.
    d["loaded"] = bool(d.get("scanned"))
    # A shallow scan performs no hashing, so it must never ASSERT an integrity verdict: publishing
    # edited=[] / eligible=True would clear a real tampering finding within 15 minutes of the deep scan
    # that found it. Carry the previous verdict forward and mark this result as unverified.
    if not d.get("deep"):
        prev_any = _mission_audit_cache.get("data") or {}
        d["edited"] = list(prev_any.get("edited") or [])
        d["integrity_verified"] = False
        d["integrity_from_ts"] = prev_any.get("integrity_from_ts") or prev_any.get("ts") or 0
        if d["edited"]:
            d["eligible"] = False
            d["reasons"] = list(d.get("reasons") or []) + [
                "official mission edited: " + ", ".join(d["edited"][:6]) + " (from the last deep scan)"]
    prev = _mission_audit_cache.get("data") or {}
    if d.get("error") and prev.get("loaded") and (prev.get("official") or prev.get("unofficial")):
        keep = dict(prev)
        keep["error"] = d["error"]
        keep["stale"] = True
        keep["last_error_ts"] = time.time()
        _mission_audit_cache["data"] = keep          # keep ts: the DATA is as old as its real scan
        print(f"[mission-audit] refresh failed ({d['error']}) - keeping the previous good scan "
              f"({len(keep.get('unofficial') or [])} custom missions still ballot-eligible)")
        return keep
    d.pop("stale", None)
    _mission_audit_cache.update({"ts": time.time(), "data": d})
    return d


def mission_audit_state():
    return _mission_audit_cache["data"]


def _mission_dir_candidates(mdir):
    """SFTP-relative candidates for an absolute MissionDirectory (session is rooted at the container home)."""
    c = [mdir, mdir.lstrip("/")]
    mp = mdir.lstrip("/").split("/")
    if len(mp) >= 2 and mp[0] == "home":
        c.append("/".join(mp[2:]))
    return [x for i, x in enumerate(c) if x and x not in c[:i]]


def _read_server_config():
    """READ-ONLY DedicatedServerConfig fetch. Separate from _mission_rotation_mutate so a caller that
    only wants to LOOK at a value never opens the backup/atomic-write path. Returns {} on any failure -
    callers must treat an empty dict as "unknown", never as "the setting is absent"."""
    ssh = sftp = None
    try:
        ssh, sftp = _open_sftp()
        with sftp.open(SRVCFG_PATH, "rb") as f:
            return json.loads(f.read().decode("utf-8", "replace"))
    except Exception as e:                                  # noqa: BLE001
        print(f"[srvcfg] read failed: {e}")
        return {}
    finally:
        for h in (sftp, ssh):
            try:
                if h is not None:
                    h.close()
            except Exception:                               # noqa: BLE001
                pass


def _mission_rotation_mutate(mutate):
    """Open SFTP, read DedicatedServerConfig, run mutate(cfg) (True if it changed), back up + write via
    tmp + ATOMIC RENAME + verify-after-write, then reload-config + re-audit. NEVER an in-place rewrite:
    an in-place open(wb) is not a reliable truncate on every SFTP host - a shorter re-serialization once
    left the old file's tail behind (= corrupt JSON with Extra data). Returns {ok, error?}."""
    try:
        ssh, sftp = _open_sftp()
    except Exception as e:                              # noqa: BLE001
        return {"ok": False, "error": f"sftp: {e}"}
    try:
        with sftp.open(SRVCFG_PATH, "rb") as f:
            orig = f.read().decode("utf-8")
        cfg = json.loads(orig)
        if not mutate(cfg):
            return {"ok": True, "nochange": True}
        os.makedirs(BACKUP_DIR, exist_ok=True)
        with open(os.path.join(BACKUP_DIR, "DedicatedServerConfig.beforeedit.json"), "w", encoding="utf-8") as bf:
            bf.write(orig)
        new_text = json.dumps(cfg, indent=2)
        tmp_path = SRVCFG_PATH + ".tmp"
        with sftp.open(tmp_path, "wb") as f:            # full write to a fresh temp, then rename over
            f.write(new_text.encode("utf-8"))
        try:
            sftp.posix_rename(tmp_path, SRVCFG_PATH)
        except (AttributeError, IOError):
            try:
                sftp.remove(SRVCFG_PATH)
            except IOError:
                pass
            sftp.rename(tmp_path, SRVCFG_PATH)
        # VERIFY-AFTER-WRITE: re-read + parse; a write that did not land cleanly must FAIL LOUDLY.
        with sftp.open(SRVCFG_PATH, "rb") as f:
            reread = json.loads(f.read().decode("utf-8"))
        if reread != cfg:
            return {"ok": False, "error": "verify failed: re-read config does not match what was written"}
    except Exception as e:                             # noqa: BLE001
        return {"ok": False, "error": f"write: {e}"}
    finally:
        try:
            ssh.close()
        except Exception:                              # noqa: BLE001
            pass
    try:
        RemoteCommand(RCMD_HOST, RCMD_PORT).send("reload-config")
    except Exception:                                  # noqa: BLE001
        pass
    try:
        # deep=False: this runs on the bot's main loop after a rotation write / mission upload. A deep
        # scan downloads every official mission (~15 MB) to hash it, stalling the console tail, vote
        # timer and roster poll for the whole transfer - with players on. Only the mission LIST needs
        # refreshing here. (round-3 audit 2026-08-01)
        refresh_mission_audit(deep=False)
    except Exception:                                  # noqa: BLE001
        pass
    return {"ok": True}


def mission_set_enabled(group, name, on, max_time=10800.0):
    """Add (on) or remove (off) a mission from the live MissionRotation. Enabling an unofficial mission
    makes the pool diverge from stock (surfaced by the next mission audit)."""
    group = str(group or "User"); name = str(name or "")
    if not name:
        return {"ok": False, "error": "no mission name"}

    def _match(e):
        k = e.get("Key", {}) if isinstance(e, dict) else {}
        return k.get("Name") == name and k.get("Group") == group

    def _m(cfg):
        rot = cfg.setdefault("MissionRotation", [])
        if on:
            if any(_match(e) for e in rot):
                return False
            rot.append({"Key": {"Group": group, "Name": name}, "MaxTime": float(max_time)})
            return True
        before = len(rot)
        cfg["MissionRotation"] = [e for e in rot if not _match(e)]
        return len(cfg["MissionRotation"]) != before
    return _mission_rotation_mutate(_m)


def mission_add_workshop(workshop_id, max_time=10800.0):
    """Add a Steam Workshop mission ({Group:Workshop,Name:<id>}) to the rotation -- the server
    auto-downloads it on the next start. This enables it, so the pool diverges from stock."""
    wid = str(workshop_id or "").strip()
    if not re.fullmatch(r"\d{5,20}", wid):
        return {"ok": False, "error": "workshop id must be numeric"}
    return mission_set_enabled("Workshop", wid, True, max_time)


def mission_upload(name, files):
    """SFTP-write an uploaded mission folder into MissionDirectory/<name>/. Adds it OFF (not in the
    rotation) until the owner enables it. files=[{path, b64}]. Read of the
    config is SFTP; writes are confined to MissionDirectory/<sanitized name>/."""
    import base64
    name = re.sub(r"[^A-Za-z0-9 ._-]", "", (name or "").strip())
    if not name:
        return {"ok": False, "error": "bad mission name"}
    cfg, err = _srvcfg_read()
    if err or not isinstance(cfg, dict):
        return {"ok": False, "error": err or "config read failed"}
    mdir = str(cfg.get("MissionDirectory", "") or "").rstrip("/")
    cands = _mission_dir_candidates(mdir)
    res = {"ok": False, "error": "upload failed"}

    def _op(sftp):
        base_dir = None
        for c in cands:
            try:
                sftp.listdir(c); base_dir = c; break
            except Exception:                          # noqa: BLE001
                continue
        if base_dir is None:
            res.update({"ok": False, "error": "mission dir not accessible via SFTP"}); return
        dest = base_dir + "/" + name
        try:
            sftp.mkdir(dest)
        except Exception:                              # noqa: BLE001
            pass                                       # already exists
        n = 0
        for fobj in (files or []):
            rel = re.sub(r"[^A-Za-z0-9 ._-]", "", str(fobj.get("path", "")).split("/")[-1])  # flat: filename only
            if not rel:
                continue
            try:
                data = base64.b64decode(fobj.get("b64", "") or "")
            except Exception:                          # noqa: BLE001
                continue
            with sftp.open(dest + "/" + rel, "wb") as f:
                f.write(data)
            n += 1
        res.update({"ok": n > 0, "files": n, "name": name, "error": None if n else "no valid files"})
    try:
        _sftp_op(_op)
    except Exception as e:                             # noqa: BLE001
        return {"ok": False, "error": str(e)}
    try:
        # deep=False: this runs on the bot's main loop after a rotation write / mission upload. A deep
        # scan downloads every official mission (~15 MB) to hash it, stalling the console tail, vote
        # timer and roster poll for the whole transfer - with players on. Only the mission LIST needs
        # refreshing here. (round-3 audit 2026-08-01)
        refresh_mission_audit(deep=False)
    except Exception:                                  # noqa: BLE001
        pass
    return res


def set_server_config(key, value, _internal=False):
    meta = _SRVCFG_MAP.get(key)
    if not meta:
        return {"ok": False, "error": f"unknown field {key}"}
    # AUTHORITATIVE GUARD: hidden fields are bot-managed (derived) and MUST NOT be settable by an operator.
    # PostMissionDelay is derived = vote + delay (sync_effective_pmd); letting a raw value through here would
    # re-introduce the exact broken combination (delay shorter than the vote) the vote-timing rework removed.
    # Only the bot's own derive-and-push calls with _internal=True may write these; every operator route
    # (the setserverconfig admin handler and the settings dispatch) calls without it and is rejected.
    if key in _SRVCFG_HIDDEN_FIELDS and not _internal:
        return {"ok": False, "error": f"{meta[0]} is derived from the vote timing "
                                      f"(Map vote length + Delay after vote) and cannot be set directly"}
    lbl, typ, mask, nr, note = meta
    # "********" is the placeholder the UI echoes back for a field the operator never touched, so it
    # genuinely means "no change". An EMPTY string is an explicit CLEAR - which this schema has always
    # advertised ("Blank = open server") but this guard made unreachable, so there was no way to remove
    # a join password through the tooling at all. Only the placeholder is a no-op now.
    if mask and str(value) == "********":
        return {"ok": False, "error": "no change (password left masked)"}
    try:
        coerced = _srvcfg_coerce(typ, value)
    except (ValueError, TypeError) as e:
        return {"ok": False, "error": f"bad value: {e}"}
    try:
        ssh, sftp = _open_sftp()
    except Exception as e:                         # noqa: BLE001
        return {"ok": False, "error": f"sftp: {e}"}
    old = None
    created = False
    try:
        with sftp.open(SRVCFG_PATH, "rb") as f:
            orig_text = f.read().decode("utf-8")
        cfg = json.loads(orig_text)
        old = _srvcfg_walk(cfg, key)
        created = old is None
        if _srvcfg_walk(cfg, key, set_to=coerced, create=True) is None:
            return {"ok": False, "error": f"could not set {key} (config shape unexpected)"}
        os.makedirs(BACKUP_DIR, exist_ok=True)
        bname = "DedicatedServerConfig.beforeedit." + time.strftime("%Y%m%d-%H%M%S") + ".json"
        with open(os.path.join(BACKUP_DIR, bname), "w", encoding="utf-8") as bf:
            bf.write(orig_text)
        try:                                          # prune: keep the newest 5 timestamped backups
            baks = sorted(fn for fn in os.listdir(BACKUP_DIR)
                          if fn.startswith("DedicatedServerConfig.beforeedit.") and fn.endswith(".json"))
            for fn in baks[:-5]:
                os.remove(os.path.join(BACKUP_DIR, fn))
        except OSError:
            pass
        new_text = json.dumps(cfg, indent=2)
        tmp_path = SRVCFG_PATH + ".tmp"
        with sftp.open(tmp_path, "wb") as f:          # atomic-ish: full write to a temp, then rename over
            f.write(new_text.encode("utf-8"))
        try:
            sftp.posix_rename(tmp_path, SRVCFG_PATH)
        except (AttributeError, IOError):
            try:
                sftp.remove(SRVCFG_PATH)
            except IOError:
                pass
            sftp.rename(tmp_path, SRVCFG_PATH)
        # VERIFY-AFTER-WRITE: re-read the file and confirm the value actually landed. A save that
        # didn't land must FAIL LOUDLY, never look successful.
        with sftp.open(SRVCFG_PATH, "rb") as f:
            reread = json.loads(f.read().decode("utf-8"))
        if _srvcfg_walk(reread, key) != coerced:
            return {"ok": False, "error": f"verify failed: file does not show the new value after write"}
    except Exception as e:                         # noqa: BLE001
        return {"ok": False, "error": f"write: {e}"}
    finally:
        try:
            ssh.close()
        except Exception:                          # noqa: BLE001
            pass
    try:
        RemoteCommand(RCMD_HOST, RCMD_PORT).send("reload-config")
    except Exception:                              # noqa: BLE001
        pass
    panel = _srvcfg_panel_mirror(key, old, coerced)
    refresh_server_config()
    res = {"ok": True, "key": key, "needs_restart": nr, "panel": panel, "ts": time.time(), "created": created}
    _srvcfg_cache["last_set"] = res
    if nr:                                          # persist the per-field "pending restart" flag
        pend = _srvcfg_pending_load()
        pend[key] = {"value": (None if mask else coerced), "ts": time.time()}
        _srvcfg_pending_save(pend)
    activity(f"Server config: {lbl} -> {'********' if mask else coerced}"
             + (" (field added to config)" if created else "")
             + (f" (synced to gpanel: {panel.get('var')})" if panel.get("mirrored") else "")
             + (" - applies after a server restart" if nr else ""), "ADMIN")
    return res


def sync_effective_pmd():
    """FIX 3: push the DERIVED post-mission delay (vote_duration + post_vote_delay) to the server's real
    DedicatedServerConfig.PostMissionDelay so the game rotates the map exactly post_vote_delay seconds
    AFTER the ballot closes — never before. The operator never sets a raw delay, so it can't drift into a
    combination that rotates the map before the vote+change finish. No-op (no SFTP write) when the server
    already holds the derived value. Returns the set_server_config result dict (or a {'nochange': True})."""
    want = float(_effective_pmd())
    try:
        cur = (_srvcfg_cache.get("values") or {}).get("PostMissionDelay")
        if cur is not None and abs(float(cur) - want) < 0.5:
            return {"ok": True, "nochange": True}
    except (TypeError, ValueError):
        pass
    r = set_server_config("PostMissionDelay", want, _internal=True)
    if isinstance(r, dict) and r.get("ok"):
        activity(f"Post-mission delay synced to {int(want)}s (vote {vote_duration()}s + post-vote {post_vote_delay()}s)", "CFG")
    else:
        activity(f"Post-mission delay sync FAILED ({(r or {}).get('error', '?')}) - vote timing set, server delay unchanged", "!")
    return r


_VOTE_TIMING_KEYS = ("MAP_VOTE_DURATION", "POST_VOTE_MAP_CHANGE_DELAY")


def set_vote_timing(short, value):
    """FIX 3: apply a change to ONE of the two vote-timing knobs (from the Game Settings menu). Clamps to the
    invariant, updates the live globals + the derived VOTE_DURATION/APPROVAL_DURATION aliases, persists to the
    deploy-protected .nost-data file, and re-derives + PUSHES the server PostMissionDelay in the SAME op so the
    raw delay can never drift out of sync (or below the vote). needs_restart is False — it applies live."""
    global MAP_VOTE_DURATION, POST_VOTE_MAP_CHANGE_DELAY, VOTE_DURATION, APPROVAL_DURATION
    try:
        num = float(value)
    except (TypeError, ValueError):
        return {"ok": False, "error": "must be a number"}
    mv, pv = MAP_VOTE_DURATION, POST_VOTE_MAP_CHANGE_DELAY
    if short == "MAP_VOTE_DURATION":
        mv = num
    elif short == "POST_VOTE_MAP_CHANGE_DELAY":
        pv = num
    else:
        return {"ok": False, "error": f"unknown timing key {short}"}
    # REJECT (do not silently clamp) a value that violates the invariant, so the webcc shows WHY and the
    # setting visibly refuses to apply. The delay is the GAP after the vote closes, so a delay < 5 would let
    # the map change land less than 5s after the ballot - the exact too-tight case to block. Only the changed
    # knob can be out of range here (the other is the already-valid live value).
    if pv < 5:
        return {"ok": False, "error": f"Map-change delay must be at least 5s (you set {int(pv)}) so the map "
                                      f"change always stays at least 5s after the vote closes - not applied."}
    if mv < 10:
        return {"ok": False, "error": f"Vote length must be at least 10s (you set {int(mv)}) - not applied."}
    MAP_VOTE_DURATION, POST_VOTE_MAP_CHANGE_DELAY = _clamp_vote_timing(mv, pv)
    VOTE_DURATION = APPROVAL_DURATION = MAP_VOTE_DURATION
    _save_vote_timing()
    activity(f"Vote timing: {short} = {int(num)}  (vote {MAP_VOTE_DURATION}s + delay "
             f"{POST_VOTE_MAP_CHANGE_DELAY}s -> post-mission delay {_effective_pmd()}s)", "CFG")
    sync_effective_pmd()                       # re-derive + push PostMissionDelay = vote + delay
    return {"ok": True, "needs_restart": False}


def add_rotation_mission():
    """run.bat --add-rotation <Name> [Group] [MaxTime]: append a mission to
    MissionRotation in DedicatedServerConfig.json (Group defaults to 'User',
    MaxTime to 10800.0). Idempotent; surgical insert before the array's closing
    bracket; local backup first; verified by a JSON round-trip."""
    rest = [a for a in sys.argv[sys.argv.index("--add-rotation") + 1:] if not a.startswith("--")]
    if not rest:
        print("usage: run.bat --add-rotation <Name> [Group] [MaxTime]")
        return
    name = rest[0]
    group = rest[1] if len(rest) > 1 else "User"
    try:
        max_time = float(rest[2]) if len(rest) > 2 else 10800.0
    except ValueError:
        print("[rot] MaxTime must be a number"); return
    path = "DedicatedServerConfig.json"
    ssh, sftp = _open_sftp()
    try:
        try:
            with sftp.open(path, "rb") as f:
                text = f.read().decode("utf-8")
        except UnicodeDecodeError:
            print("[rot] ABORT: config is not valid UTF-8"); return
        cfg = json.loads(text)
        rot = cfg.get("MissionRotation")
        if not isinstance(rot, list):
            print("[rot] ABORT: no MissionRotation array"); return
        if any(isinstance(e, dict) and e.get("Key", {}).get("Group") == group
               and e.get("Key", {}).get("Name") == name for e in rot):
            print(f"[rot] '{group}/{name}' already in the rotation ({len(rot)} entries) - nothing to do.")
            return
        # locate the MissionRotation array's closing ']' (entries contain no '[')
        mr = text.index('"MissionRotation"')
        bopen = text.index("[", mr)
        bclose = text.index("]", bopen)
        insert_at = text.rindex("}", bopen, bclose) + 1
        entry = ("    {\n"
                 '      "Key": {\n'
                 f'        "Group": {json.dumps(group)},\n'
                 f'        "Name": {json.dumps(name)}\n'
                 "      },\n"
                 f'      "MaxTime": {max_time}\n'
                 "    }")
        new_text = text[:insert_at] + ",\n" + entry + text[insert_at:]
        new_cfg = json.loads(new_text)               # verify still valid JSON
        want = rot + [{"Key": {"Group": group, "Name": name}, "MaxTime": max_time}]
        if new_cfg.get("MissionRotation") != want:
            print("[rot] ABORT: post-insert rotation didn't match expected - not uploaded")
            return
        os.makedirs(BACKUP_DIR, exist_ok=True)
        with open(os.path.join(BACKUP_DIR, "DedicatedServerConfig.json.bak"),
                  "w", encoding="utf-8") as bf:
            bf.write(text)
        with sftp.open(path, "wb") as f:
            f.write(new_text.encode("utf-8"))
        print(f"[rot] added {group}/{name} (MaxTime {max_time}); rotation now "
              f"{len(new_cfg['MissionRotation'])} entries.")
        print("[rot] takes effect on the next FULL server restart.")
    finally:
        ssh.close()


def upload_bepinex():
    """run.bat --upload-bepinex: push the local NukeStats/bepinex_pack tree to the
    container root and the built NukeStats.dll to BepInEx/plugins/. RUN ONLY WITH THE
    SERVER STOPPED (it writes into the live game install). Reuses the SFTP creds."""
    base = os.path.join(os.path.dirname(os.path.abspath(__file__)), "NukeStats")
    pack = os.path.join(base, "bepinex_pack")
    if not os.path.isdir(pack):
        print(f"[up] no BepInEx pack at {pack}")
        return
    ssh, sftp = _open_sftp()
    try:
        def mkremote(rpath):
            cur = ""
            for part in rpath.strip("/").split("/"):
                cur = f"{cur}/{part}" if cur else part
                try:
                    sftp.stat(cur)
                except IOError:
                    try:
                        sftp.mkdir(cur)
                    except IOError:
                        pass
        count = 0
        for root, _dirs, files in os.walk(pack):
            rel = os.path.relpath(root, pack).replace("\\", "/")
            rdir = "" if rel == "." else rel
            if rdir:
                mkremote(rdir)
            for fn in files:
                rp = f"{rdir}/{fn}" if rdir else fn
                sftp.put(os.path.join(root, fn), rp)
                count += 1
                print(f"  put {rp}")
        mkremote("BepInEx/plugins")
        dll = next((c for c in (os.path.join(base, "bin", "Release", "NukeStats.dll"),
                                os.path.join(base, "bin", "Debug", "NukeStats.dll"))
                    if os.path.exists(c)), None)
        if dll:
            sftp.put(dll, "BepInEx/plugins/NukeStats.dll")
            print(f"  put BepInEx/plugins/NukeStats.dll  (from {dll})")
            count += 1
        else:
            print("  [warn] NukeStats.dll not built yet - build it, then re-run, or upload it later.")
        print(f"[up] uploaded {count} file(s). Now set the GPanel Doorstop startup command "
              "and start the server; check console.log for 'NukeStats loaded'.")
    finally:
        ssh.close()


def _read_tick_rate():
    """Clamp the configured engine tick rate to a safe 30-120 Hz (default 60); never raises.
    Read at WRAPPER-BUILD time, i.e. only by setup_server() (run.bat --setup-server) — a bot restart
    or a panel restart re-runs the EXISTING wrapper at its baked-in rate. There is no
    --rewrite-wrapper flag; --setup-server is idempotent and is the way to re-emit the wrapper."""
    try:
        v = int(TICK_RATE)
    except (TypeError, ValueError):
        v = 60
    return max(30, min(120, v))


def setup_server():
    """One-off admin helper (run via:  run.bat --setup-server).

    The panel's startup command launches ./NuclearOptionServer.x86_64 with no flags
    and can't be edited, so we install a wrapper at that name. Unity derives its
    data folder from the executable name minus extension, so we rename the real
    launcher by just DROPPING the .x86_64 extension (NuclearOptionServer.x86_64 ->
    NuclearOptionServer) -- that still maps to NuclearOptionServer_Data, no symlink
    needed. The wrapper then execs ./NuclearOptionServer WITH the flags the bot
    needs. Idempotent and reversible (delete the wrapper, rename NuclearOptionServer
    back to *.x86_64). Reuses the NO_SFTP_* credentials.
    """
    import paramiko
    LAUNCH = "NuclearOptionServer.x86_64"   # what the panel runs; becomes the wrapper
    REAL   = "NuclearOptionServer"          # real ELF, ext dropped -> same _Data folder
    DATA   = "NuclearOptionServer_Data"
    tick   = _read_tick_rate()              # engine frame/tick rate (Hz), 30-120, default 60 (was hardcoded 30 -> live regression)
    relay_port = int(RCMD_PORT)
    wrapper = (
        "#!/bin/sh\n"
        "# Launch wrapper (map-vote bot). Exposes the localhost-only remote-command\n"
        f"# port (127.0.0.1:5504) on 0.0.0.0:{relay_port} via whatever relay tool the container\n"
        "# has, adds the remote-command flag + a stable console log the bot tails,\n"
        "# self-injects BepInEx, mirrors that log to the panel, and execs the game so it stays PID 1.\n"
        "# Undo: run.bat --revert-server\n"
        "# Resolve the wrapper folder; some panels launch scripts with / as the working directory.\n"
        'case "$0" in */*) _wrap_dir=${0%/*} ;; *) _wrap_dir=. ;; esac\n'
        'HERE=$(cd "$_wrap_dir" 2>/dev/null && pwd -P)\n'
        'if [ ! -f "$HERE/BepInEx/core/BepInEx.Preloader.dll" ] && [ -f "/home/container/BepInEx/core/BepInEx.Preloader.dll" ]; then HERE="/home/container"; fi\n'
        'cd "$HERE" || exit 1\n'
        'export LD_LIBRARY_PATH="$HERE:$HERE/linux64:$LD_LIBRARY_PATH"\n'
        "# --- BepInEx / Doorstop injection (idempotent; forced on for panel restarts) ---\n"
        "export DOORSTOP_ENABLED=1\n"
        "export DOORSTOP_ENABLE=TRUE\n"
        "unset DOORSTOP_DISABLE\n"
        "export DOORSTOP_IGNORE_DISABLED_ENV=1\n"
        'export DOORSTOP_TARGET_ASSEMBLY="$HERE/BepInEx/core/BepInEx.Preloader.dll"\n'
        'if [ -z "$LD_PRELOAD" ]; then export LD_PRELOAD="$HERE/libdoorstop.so"; else export LD_PRELOAD="$HERE/libdoorstop.so:$LD_PRELOAD"; fi\n'
        "mkdir -p ./logs ./BepInEx\n"
        ": > ./logs/console.log\n"
        ": > ./logs/relay.log\n"
        'echo "[wrapper] Doorstop target: $DOORSTOP_TARGET_ASSEMBLY" >> ./logs/relay.log\n'
        'if [ ! -f "$DOORSTOP_TARGET_ASSEMBLY" ]; then echo "[wrapper] missing BepInEx preloader" >> ./logs/relay.log; fi\n'
        'if [ ! -f "$HERE/libdoorstop.so" ]; then echo "[wrapper] missing libdoorstop.so" >> ./logs/relay.log; fi\n'
        '{ for t in python3 python perl ncat socat nc busybox bash awk node php; do '
        'p=$(command -v "$t" 2>/dev/null) && echo "[probe] FOUND $t -> $p" '
        '|| echo "[probe] no $t"; done; } >> ./logs/relay.log 2>&1\n'
        "if command -v python3 >/dev/null 2>&1; then\n"
        "  echo '[relay] using python3' >> ./logs/relay.log\n"
        f"  python3 ./no_relay.py 0.0.0.0:{relay_port} 127.0.0.1:5504 >> ./logs/relay.log 2>&1 &\n"
        "elif command -v perl >/dev/null 2>&1; then\n"
        "  echo '[relay] using perl' >> ./logs/relay.log\n"
        f"  perl ./no_relay.pl 0.0.0.0:{relay_port} 127.0.0.1:5504 >> ./logs/relay.log 2>&1 &\n"
        "elif command -v ncat >/dev/null 2>&1; then\n"
        "  echo '[relay] using ncat' >> ./logs/relay.log\n"
        f"  ncat -l 0.0.0.0 {relay_port} -k -c 'ncat 127.0.0.1 5504' >> ./logs/relay.log 2>&1 &\n"
        "elif command -v socat >/dev/null 2>&1; then\n"
        "  echo '[relay] using socat' >> ./logs/relay.log\n"
        f"  socat TCP-LISTEN:{relay_port},fork,reuseaddr TCP:127.0.0.1:5504 >> ./logs/relay.log 2>&1 &\n"
        "else\n"
        "  echo '[relay] NO RELAY TOOL found in container' >> ./logs/relay.log\n"
        "fi\n"
        "tail -n +1 -F ./logs/console.log 2>/dev/null &\n"
        "exec ./NuclearOptionServer"
        f' -logFile ./logs/console.log -limitframerate {tick} -ServerRemoteCommands 5504 "$@"\n'
    )

    if not (SFTP_HOST and SFTP_USER and SFTP_PASS):
        print("[setup] Missing SFTP creds. Run this through run.bat:  run.bat --setup-server")
        return

    print(f"[setup] connecting to {SFTP_HOST}:{SFTP_PORT} as {SFTP_USER} ...")
    ssh = paramiko.SSHClient()
    ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    ssh.connect(SFTP_HOST, port=SFTP_PORT, username=SFTP_USER, password=SFTP_PASS,
                timeout=15, look_for_keys=False, allow_agent=False)
    sftp = ssh.open_sftp()
    try:
        names = set(sftp.listdir("."))
        if LAUNCH not in names:
            print(f"[setup] ERROR: {LAUNCH} not found in SFTP root. Entries: {sorted(names)[:10]}")
            return
        if DATA not in names:
            print(f"[setup] ERROR: {DATA} not found beside the binary; aborting to be safe.")
            return

        with sftp.open(LAUNCH, "rb") as f:
            magic = f.read(4)
        is_elf = magic == b"\x7fELF"
        kind = "ELF" if is_elf else ("script" if magic[:2] == b"#!" else "unknown")
        print(f"[setup] {LAUNCH} magic={magic!r} ({kind})")

        if REAL in names:
            if is_elf:
                print(f"[setup] ABORT: {REAL} exists but {LAUNCH} is still an ELF -- "
                      f"unclear state. Inspect manually, not touching anything.")
                return
            print(f"[setup] {REAL} already present; rewriting the wrapper only.")
        else:
            if not is_elf:
                print(f"[setup] ABORT: {LAUNCH} is not an ELF and {REAL} missing -- "
                      f"unexpected, not touching anything.")
                return
            print(f"[setup] renaming real launcher {LAUNCH} -> {REAL} (keeps {DATA} valid)")
            try:
                sftp.posix_rename(LAUNCH, REAL)
            except (IOError, OSError):
                sftp.rename(LAUNCH, REAL)

        with sftp.open(LAUNCH, "wb") as f:
            f.write(wrapper.encode("utf-8"))
        sftp.chmod(LAUNCH, 0o755)
        sftp.chmod(REAL, 0o755)

        # upload the relay helpers next to the binary so the wrapper can launch one
        here = os.path.dirname(os.path.abspath(__file__))
        helper_dirs = [
            here,
            os.path.join(here, "relay"),
            os.path.join(os.path.dirname(here), "nuclear-option-toolkit", "src", "relay"),
        ]
        for helper in ("no_relay.py", "no_relay.pl"):
            local = next((os.path.join(d, helper) for d in helper_dirs
                          if os.path.exists(os.path.join(d, helper))), os.path.join(here, helper))
            try:
                with open(local, "r", encoding="utf-8") as rf:
                    src = rf.read()
                with sftp.open(helper, "wb") as f:
                    f.write(src.encode("utf-8"))
                sftp.chmod(helper, 0o755)
                print(f"[setup] uploaded {helper} ({len(src)} bytes)")
            except FileNotFoundError:
                print(f"[setup] WARNING: local {helper} not found next to the bot; skipping.")

        with sftp.open(LAUNCH, "rb") as f:
            head = f.read(32)
        with sftp.open(REAL, "rb") as f:
            rmagic = f.read(4)
        wmode = oct(sftp.stat(LAUNCH).st_mode & 0o777)
        rmode = oct(sftp.stat(REAL).st_mode & 0o777)
        print(f"[setup] wrapper({LAUNCH}) mode={wmode} head={head[:18]!r}")
        print(f"[setup] real({REAL}) mode={rmode} magic={rmagic!r}")
        if head.startswith(b"#!/bin/sh") and rmagic == b"\x7fELF":
            print("[setup] DONE. Now fully RESTART the server in the panel, then tell me.")
        else:
            print("[setup] WARNING: verification looks off -- do NOT restart; ping me.")
    finally:
        sftp.close()
        ssh.close()


def revert_server():
    """Undo setup_server(): remove the wrapper and restore NuclearOptionServer.x86_64.
    Run via:  run.bat --revert-server
    """
    import paramiko
    LAUNCH = "NuclearOptionServer.x86_64"
    REAL   = "NuclearOptionServer"
    if not (SFTP_HOST and SFTP_USER and SFTP_PASS):
        print("[revert] Missing SFTP creds. Run through run.bat:  run.bat --revert-server")
        return
    print(f"[revert] connecting to {SFTP_HOST}:{SFTP_PORT} as {SFTP_USER} ...")
    ssh = paramiko.SSHClient()
    ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    ssh.connect(SFTP_HOST, port=SFTP_PORT, username=SFTP_USER, password=SFTP_PASS,
                timeout=15, look_for_keys=False, allow_agent=False)
    sftp = ssh.open_sftp()
    try:
        names = set(sftp.listdir("."))
        if REAL not in names:
            print(f"[revert] Nothing to do: {REAL} not present (already reverted?).")
            return
        if LAUNCH in names:
            with sftp.open(LAUNCH, "rb") as f:
                magic = f.read(4)
            if magic == b"\x7fELF":
                print(f"[revert] ABORT: {LAUNCH} is already the real ELF; not touching.")
                return
            sftp.remove(LAUNCH)
            print(f"[revert] removed wrapper {LAUNCH}")
        try:
            sftp.posix_rename(REAL, LAUNCH)
        except (IOError, OSError):
            sftp.rename(REAL, LAUNCH)
        sftp.chmod(LAUNCH, 0o755)
        with sftp.open(LAUNCH, "rb") as f:
            magic = f.read(4)
        ok = magic == b"\x7fELF"
        print(f"[revert] restored {LAUNCH} magic={magic!r} ({'OK' if ok else 'WARNING: not ELF'})")
        print("[revert] DONE. Restart the server to return to the original (flag-less) launch.")
    finally:
        sftp.close()
        ssh.close()


def check_server():
    """Diagnostic (run via: run.bat --check-server): is the wrapper running and is
    the console log being written? Prints file state and the tail of the log."""
    import paramiko
    if not (SFTP_HOST and SFTP_USER and SFTP_PASS):
        print("[check] Missing SFTP creds. Run through run.bat:  run.bat --check-server")
        return
    print(f"[check] connecting to {SFTP_HOST}:{SFTP_PORT} as {SFTP_USER} ...")
    ssh = paramiko.SSHClient()
    ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    ssh.connect(SFTP_HOST, port=SFTP_PORT, username=SFTP_USER, password=SFTP_PASS,
                timeout=15, look_for_keys=False, allow_agent=False)
    sftp = ssh.open_sftp()
    try:
        now = time.time()
        names = set(sftp.listdir("."))
        for n in ("NuclearOptionServer", "NuclearOptionServer.x86_64",
                  "NuclearOptionServer_Data", "logs"):
            if n in names:
                st = sftp.stat(n)
                print(f"[check] {n}: size={st.st_size:,} age={int(now - st.st_mtime)}s")
            else:
                print(f"[check] {n}: MISSING")
        logpath = SFTP_LOG_PATH or "/logs/console.log"
        try:
            st = sftp.stat(logpath)
        except FileNotFoundError:
            print(f"[check] {logpath}: NOT FOUND -> the wrapper hasn't run. Do a FULL "
                  f"stop+start (not reconnect) so the new launch command executes.")
            return
        age = int(now - st.st_mtime)
        print(f"[check] {logpath}: size={st.st_size:,} age={age}s "
              f"({'fresh' if age < 180 else 'STALE - not being written'})")
        with sftp.open(logpath, "rb") as f:
            data = f.read(2_000_000).decode("utf-8", "replace")
        lines = data.splitlines()
        print(f"[check] read {len(lines)} lines from console.log")
        print("[check] ---- first 25 lines (startup + args echo) ----")
        for line in lines[:25]:
            print("   " + line)
        KEYS = ("5504", "remotecommand", "remote command", "command line", "commandline",
                "argument", "listen", "bind", "unknown option", "unrecognized",
                "exception", "invalid", "socket")
        NOISE = ("transport", "allocating", "[aihelo]", "warhead", "airbase")
        hits = [ln for ln in lines
                if any(k in ln.lower() for k in KEYS)
                and not any(n in ln.lower() for n in NOISE)]
        print("[check] ---- lines mentioning 5504 / remotecommand / args / errors ----")
        for ln in hits[:40]:
            print("   >> " + ln)
        if not hits:
            print("   (no relevant lines found anywhere in the log)")
        try:
            rst = sftp.stat("/logs/relay.log")
            with sftp.open("/logs/relay.log", "rb") as f:
                rlog = f.read(8000).decode("utf-8", "replace")
            print(f"[check] ---- /logs/relay.log (size={rst.st_size}) ----")
            for ln in rlog.splitlines()[-20:]:
                print("   " + ln)
        except FileNotFoundError:
            print("[check] /logs/relay.log: not present (no relay configured/started yet)")
    finally:
        sftp.close()
        ssh.close()


def test_tunnel():
    """Probe: can we reach the localhost-bound remote-command port by tunnelling
    through the SFTP host's SSH (paramiko direct-tcpip)? If yes, the bot can stay
    on this PC and drive the server over that tunnel."""
    import paramiko
    print(f"[tunnel] SSH to {SFTP_HOST}:{SFTP_PORT} as {SFTP_USER} ...")
    ssh = paramiko.SSHClient()
    ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    ssh.connect(SFTP_HOST, port=SFTP_PORT, username=SFTP_USER, password=SFTP_PASS,
                timeout=15, look_for_keys=False, allow_agent=False)
    try:
        transport = ssh.get_transport()
        try:
            chan = transport.open_channel("direct-tcpip",
                                          ("127.0.0.1", RCMD_PORT), ("127.0.0.1", 0))
        except Exception as e:  # noqa: BLE001
            print(f"[tunnel] FAILED to open a forward channel: {e!r}")
            print("[tunnel] -> this host's SFTP/SSH does not allow port forwarding.")
            return
        print(f"[tunnel] channel open to 127.0.0.1:{RCMD_PORT}; sending get-mission-time ...")
        payload = json.dumps({"name": "get-mission-time", "arguments": []}).encode("utf-8")
        chan.sendall(len(payload).to_bytes(4, "little") + payload)
        chan.settimeout(8)
        try:
            hdr = b""
            while len(hdr) < 4:
                b = chan.recv(4 - len(hdr))
                if not b:
                    print("[tunnel] channel closed before any reply.")
                    return
                hdr += b
            length = int.from_bytes(hdr, "little")
            body = b""
            while len(body) < length:
                b = chan.recv(length - len(body))
                if not b:
                    break
                body += b
            print(f"[tunnel] OK! reply: {body.decode('utf-8', 'replace')}")
            print("[tunnel] SUCCESS -- the bot can drive the server through an SSH tunnel.")
        except Exception as e:  # noqa: BLE001
            print(f"[tunnel] channel opened but no usable reply: {e!r}")
    finally:
        ssh.close()


def find_chat():
    """Diagnostic (run.bat --findchat): pull the console log and show chat-ish lines
    plus whether the parser matches them, so we can confirm/fix the chat regex."""
    import paramiko
    if not (SFTP_HOST and SFTP_USER and SFTP_PASS):
        print("[findchat] Missing SFTP creds. Run via run.bat --findchat")
        return
    print(f"[findchat] connecting to {SFTP_HOST}:{SFTP_PORT} ...")
    ssh = paramiko.SSHClient()
    ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    ssh.connect(SFTP_HOST, port=SFTP_PORT, username=SFTP_USER, password=SFTP_PASS,
                timeout=15, look_for_keys=False, allow_agent=False)
    sftp = ssh.open_sftp()
    try:
        logpath = SFTP_LOG_PATH or "/logs/console.log"
        with sftp.open(logpath, "rb") as f:
            data = f.read(4_000_000).decode("utf-8", "replace")
        lines = data.splitlines()
        hits = [ln for ln in lines if ("chat" in ln.lower()) or ("CmdSendChatMessage" in ln)]
        print(f"[findchat] {len(hits)} chat-ish line(s) in {logpath} (showing last 30):")
        for ln in hits[-30:]:
            parsed = parse_chat_line(ln)
            print(f"  [{'PARSED  ' if parsed else 'NO-MATCH'}] {ln}")
            if parsed:
                print(f"             -> {parsed}")
        if not hits:
            print("[findchat] No chat-ish lines found. Type in game chat, then re-run.")
    finally:
        sftp.close()
        ssh.close()


def show_ranks():
    """run.bat --ranks: print the full saved standings from ranks.json -- ALL
    players incl. rank-0 Officer Cadets, sorted by points (then name)."""
    load_ranks()
    if not RANK_DATA:
        print(f"[ranks] no records yet in {RANK_FILE}")
        return
    board = sorted(RANK_DATA.items(),
                   key=lambda kv: (-kv[1].get("points", 0), kv[1].get("name", "").lower()))
    print(f"[ranks] {len(board)} player(s) in {RANK_FILE}:")
    for i, (sid, rec) in enumerate(board, 1):
        pts = rec.get("points", 0)
        nm = rec.get("name", sid)
        if not RANKS:                                    # ladder off -> points only
            print(f"  {i:>3}. {nm:<28.28} {pts:>9.1f} pts")
            continue
        cyc = cycle_points(sid)
        _, rname, abbr, _ = RANKS[rank_index_for(cyc)]
        nxt = points_to_next(cyc)
        tail = f"{nxt:.1f} to next" if nxt is not None else "max rank"
        print(f"  {i:>3}. {nm:<28.28} {pts:>9.1f} pts  [{abbr:<7}] {rname:<18} ({tail})")


# ----------------------------------------------------------------------------
# Bot command centre  (run.bat --centre / centre.bat): one coloured, interactive
# console to send server commands + bot helpers. Stays open between commands.
# ----------------------------------------------------------------------------
STATUS_CODES = {
    2000: "Success", 4000: "BadRequest", 4001: "BadHeader", 4002: "BadLength",
    4003: "JsonError", 4004: "UnknownCommand", 4005: "BadArguments",
    5000: "InternalServerError", 5001: "CommandError", 5002: "ConfigError",
}

# (alias, wire-name, args-hint, description, destructive?) -- the 19 Shockfront
# ServerCommands, exposed through friendly aliases.
CENTRE_SERVER_CMDS = [
    ("players",     "get-player-list",      "",                         "list connected players + their ranks", False),
    ("time",        "get-mission-time",     "",                         "current / max mission time", False),
    ("mission",     "get-mission",          "",                         "current + next mission", False),
    ("rotation",    "get-mission-rotation", "",                         "mission rotation + next override", False),
    ("serverid",    "get-server-id",        "",                         "the server's Steam ID", False),
    ("say",         "send-chat-message",    "<message>",                "send a message into in-game chat", False),
    ("settime",     "set-time-remaining",   "<seconds>",                "set the remaining mission time", False),
    ("nextmap",     "set-next-mission",     "<group> <name> <maxTime>", "queue the next mission (quote the name)", False),
    ("clearnext",   "clear-next-mission",   "",                         "cancel a queued next mission", False),
    ("reloadcfg",   "reload-config",        "[filepath]",               "reload the server config", True),
    ("setrotation", "set-mission-rotation", "<json>",                   "replace the mission rotation (JSON)", True),
    ("kick",        "kick-player",          "<steamId> [reason]",       "kick a player (session-block until unkick)", True),
    ("unkick",      "unkick-player",        "<steamId>",                "un-kick a player", False),
    ("clearkicks",  "clear-kicked-players", "",                         "clear the whole kick list", True),
    ("ban",         "banlist-add",          "<steamId> [reason]",       "ban a SteamID (writes to file)", True),
    ("unban",       "banlist-remove",       "<steamId>",                "remove a ban", True),
    ("banreload",   "banlist-reload",       "",                         "reload the ban list from file", False),
    ("banclear",    "banlist-clear",        "",                         "clear the in-memory ban list", True),
    ("updateready", "update-ready",         "",                         "signal a component ready", False),
]
CENTRE_BOT_CMDS = [
    ("ranks",       "show ALL saved player ranks, best first (nice table)"),
    ("rankpreview", "post the rank ladder into in-game chat"),
    ("endmission",  "force the current mission to end now"),
    ("help",        "show this command list again"),
    ("cls",         "clear the screen"),
    ("quit",        "close the command centre (the bot keeps running)"),
]


def command_centre():
    """run.bat --centre : interactive coloured console for driving the server."""
    global DEBUG
    DEBUG = False                       # we print our own tidy output instead
    try:                                # enable ANSI colours on Windows 10+
        import ctypes
        k = ctypes.windll.kernel32
        k.SetConsoleMode(k.GetStdHandle(-11), 7)
    except Exception:                   # noqa: BLE001
        pass

    R, B, DIM = "\033[0m", "\033[1m", "\033[90m"
    RED, GRN, YEL = "\033[91m", "\033[92m", "\033[93m"
    CYN, MAG, WHT = "\033[96m", "\033[95m", "\033[97m"

    def hexc(hx):
        hx = hx.lstrip("#")
        return f"\033[38;2;{int(hx[0:2],16)};{int(hx[2:4],16)};{int(hx[4:6],16)}m"

    rc = RemoteCommand(RCMD_HOST, RCMD_PORT)
    load_ranks()

    def banner():
        print(f"{CYN}{B}")
        print("  ================================================================")
        print("         NUCLEAR OPTION  -  BOT COMMAND CENTRE")
        print("  ================================================================" + R)
        print(f"{DIM}  server {RCMD_HOST}:{RCMD_PORT}   |   type a command + Enter   |"
              f"   'help' lists everything, 'quit' exits{R}\n")

    def show_help():
        print(f"\n{B}{WHT}  SERVER COMMANDS{R} {DIM}(sent live to the game server){R}")
        for alias, wire, ahint, desc, danger in CENTRE_SERVER_CMDS:
            mark = f"{RED}!{R}" if danger else " "
            print(f"   {mark} {GRN}{alias:<11}{R}{DIM}{ahint:<27}{R}{desc}")
        print(f"\n{B}{WHT}  BOT COMMANDS{R} {DIM}(local helpers){R}")
        for alias, desc in CENTRE_BOT_CMDS:
            print(f"     {CYN}{alias:<11}{R}{'':<27}{desc}")
        print(f"\n   {DIM}{RED}!{DIM} = changes the server/players -> you'll be asked to confirm."
              f"   raw <name> <args...> sends any command directly.{R}\n")

    def confirm(what):
        try:
            return input(f"{YEL}   really do '{what}'? type yes: {R}").strip().lower() == "yes"
        except (EOFError, KeyboardInterrupt):
            return False

    def show_response(code, resp):
        if code is None:
            print(f"   {RED}no response - server/relay unreachable{R}")
            return
        name = STATUS_CODES.get(code, "?")
        col = GRN if code == 2000 else RED
        print(f"   {col}{'OK' if code == 2000 else 'ERROR'} ({code} {name}){R}")
        if isinstance(resp, dict):
            print(DIM + json.dumps(resp, indent=2)[:4000] + R)
        elif isinstance(resp, str) and resp.strip():
            print(DIM + resp[:2000] + R)

    def show_players():
        code, resp = rc.send("get-player-list", return_code=True)
        if code is None:
            print(f"   {RED}no response - server/relay unreachable{R}")
            return
        if code != 2000:
            print(f"   {RED}error {code} {STATUS_CODES.get(code,'?')}{R}")
            return
        players = (resp.get("Players") or resp.get("players")) if isinstance(resp, dict) else None
        if not players:
            print(f"   {DIM}(no players online){R}")
            return
        print(f"   {B}{len(players)} player(s) online:{R}")
        for i, p in enumerate(players, 1):
            sid = str(p.get("steamId")); nm = p.get("displayName") or sid
            fac = p.get("faction") or "-"
            pts = player_points(sid)                 # lifetime score
            if RANKS:
                _, _, abbr, color = RANKS[rank_index_for(cycle_points(sid))]
                tag = f"{hexc(color)}[{abbr}]{R} "
            else:                                    # ladder off -> no tier tag
                tag = ""
            print(f"     {i:>2}. {tag}{nm:<22.22} {DIM}{fac:<8} {pts:.1f} pts   {sid}{R}")

    def show_ranks_table():
        load_ranks()
        if not RANK_DATA:
            print(f"   {DIM}no ranks saved yet{R}")
            return
        board = sorted(RANK_DATA.items(),
                       key=lambda kv: (-kv[1].get("points", 0), kv[1].get("name", "").lower()))
        print(f"\n   {B}{WHT}SERVER RANKS - {len(board)} pilots (best first){R}")
        print(f"   {DIM}{'#':>3}  {'pilot':<24}{'pts':>5}   rank{R}")
        for i, (sid, rec) in enumerate(board, 1):
            pts = rec.get("points", 0); nm = rec.get("name", sid)
            if RANKS:
                _, rname, abbr, color = RANKS[rank_index_for(cycle_points(sid))]
                print(f"   {i:>3}. {nm:<24.24}{pts:>9.1f}   {hexc(color)}[{abbr}] {rname}{R}")
            else:                                    # ladder off -> points only
                print(f"   {i:>3}. {nm:<24.24}{pts:>9.1f}")
        print()

    def post_rank_ladder():
        if not RANKS:
            print(f"   {DIM}no rank ladder configured - nothing to post{R}")
            return
        rc.say("<color=#FFFF00>=== SERVER RANKS (points needed) ===</color>")
        row = []
        for i, (thr, name, abbr, color) in enumerate(RANKS, 1):
            row.append(f"<color={color}>{i}. {name} [{abbr}] {thr}</color>")
            if len(row) == 4:
                rc.say("   ".join(row)); row = []
        if row:
            rc.say("   ".join(row))
        print(f"   {GRN}posted the rank ladder to in-game chat{R}")

    banner()
    show_help()
    while True:
        try:
            raw = input(f"{B}{CYN}command>{R} ").lstrip("﻿").strip()
        except (EOFError, KeyboardInterrupt):
            print(f"\n   {DIM}closing the command centre (the bot keeps running){R}")
            return
        if not raw:
            continue
        head, _, rest = raw.partition(" ")
        cmd, rest = head.lower(), rest.strip()

        if cmd in ("quit", "exit", "q"):
            print(f"   {DIM}closing the command centre (the bot keeps running){R}")
            return
        if cmd in ("help", "?", "commands"):
            show_help(); continue
        if cmd in ("cls", "clear"):
            os.system("cls"); banner(); continue
        if cmd == "ranks":
            show_ranks_table(); continue
        if cmd == "players":
            show_players(); continue
        if cmd == "rankpreview":
            post_rank_ladder(); continue
        if cmd == "endmission":
            if confirm("force-end the current mission"):
                show_response(*rc.send("set-time-remaining", "5", return_code=True))
            continue
        if cmd == "say":
            if not rest:
                print(f"   {DIM}usage: say <message>{R}"); continue
            show_response(*rc.send("send-chat-message", rest, return_code=True)); continue
        if cmd == "raw":
            try:
                toks = shlex.split(rest)
            except ValueError as e:
                print(f"   {RED}{e}{R}"); continue
            if not toks:
                print(f"   {DIM}usage: raw <command-name> <arg> <arg> ...{R}"); continue
            # honour the same confirmation gate the aliases use for known destructive commands
            if any(e[1] == toks[0] and e[4] for e in CENTRE_SERVER_CMDS) and not confirm(rest):
                print(f"   {DIM}cancelled{R}"); continue
            if toks[0] == "kick-player" and len(toks) >= 2 and re.fullmatch(r"\d{6,20}", str(toks[1])):
                # Route through live bot: TellPlayer + session-block (no auto-unkick).
                try:
                    reason = " ".join(toks[2:]).strip() if len(toks) > 2 else "kicked by admin"
                    with open(ADMIN_CMD_FILE, "a", encoding="utf-8") as f:
                        f.write(json.dumps({"action": "admin_kick", "sid": str(toks[1]),
                                            "reason": reason, "ts": time.time()}) + "\n")
                    print(f"   {DIM}admin kick queued (whisper + session-block; no auto-unkick){R}")
                except OSError as e:
                    print(f"   {RED}could not queue admin kick: {e}{R}")
            else:
                show_response(*rc.send(toks[0], *toks[1:], return_code=True))
            continue

        entry = (next((e for e in CENTRE_SERVER_CMDS if e[0] == cmd), None)
                 or next((e for e in CENTRE_SERVER_CMDS if e[1] == cmd), None))
        if not entry:
            print(f"   {RED}unknown command '{cmd}'{R} {DIM}- type 'help'{R}"); continue
        alias, wire, ahint, desc, danger = entry
        try:
            toks = shlex.split(rest)
        except ValueError as e:
            print(f"   {RED}{e}{R}"); continue
        if danger and not confirm(f"{alias} {rest}".strip()):
            print(f"   {DIM}cancelled{R}"); continue
        # set-time-remaining with a small value cuts the round short for everyone
        if wire == "set-time-remaining" and toks:
            try:
                if float(toks[0]) < 60 and not confirm(f"set remaining time to {toks[0]}s (ends the round soon)"):
                    print(f"   {DIM}cancelled{R}"); continue
            except ValueError:
                pass
        # Admin kick → live bot (TellPlayer + RCON); session-block; no auto-unkick.
        if wire == "kick-player" and toks:
            ksid = str(toks[0]).strip()
            if re.fullmatch(r"\d{6,20}", ksid):
                reason = " ".join(toks[1:]).strip() if len(toks) > 1 else "kicked by admin"
                try:
                    with open(ADMIN_CMD_FILE, "a", encoding="utf-8") as f:
                        f.write(json.dumps({"action": "admin_kick", "sid": ksid,
                                            "reason": reason, "ts": time.time()}) + "\n")
                    print(f"   {DIM}admin kick queued (whisper + session-block; no auto-unkick){R}")
                except OSError as e:
                    print(f"   {RED}could not queue admin kick: {e}{R}")
                continue
        # Admin ban → live bot (plugin + game list + Moderation Reports row).
        if wire == "banlist-add" and toks:
            bsid = str(toks[0]).strip()
            if re.fullmatch(r"\d{6,20}", bsid):
                reason = " ".join(toks[1:]).strip() if len(toks) > 1 else "banned by admin"
                try:
                    with open(ADMIN_CMD_FILE, "a", encoding="utf-8") as f:
                        f.write(json.dumps({"action": "ban_steamid", "sid": bsid,
                                            "reason": reason, "ts": time.time()}) + "\n")
                    print(f"   {DIM}admin ban queued (Moderation log + plugin/game lists){R}")
                except OSError as e:
                    print(f"   {RED}could not queue admin ban: {e}{R}")
                continue
        show_response(*rc.send(wire, *toks, return_code=True))


def match_selftest():
    """run.bat --matchtest: exercise the per-match lifecycle OFFLINE (temp files,
    no server) and print the resulting history/ledger/derived stats."""
    global MATCH_HISTORY_FILE, LEDGER_FILE, RANK_DATA, CURRENT_MISSION, CUR_MATCH
    import tempfile
    d = tempfile.mkdtemp()
    MATCH_HISTORY_FILE = os.path.join(d, "match_history.json")
    LEDGER_FILE = os.path.join(d, "points_ledger.jsonl")
    RANK_DATA, CUR_MATCH = {}, None

    class _Stub:
        def say(self, m):
            print("   say>", _plain(m))

    rc = _Stub()

    def _award(sid, nm, fac, pts, reason, kind):
        award_points(sid, nm, pts)
        match_award(sid, nm, fac, pts, reason, kind, local_points(sid))   # ledger balance snapshot = LOCAL (per-server audit)

    print("[matchtest] MATCH 1: Tomo + Shirley capture & win, Jerms only present")
    CURRENT_MISSION = "Escalation BDF - Dawn"
    for sid, nm in (("1", "Tomo"), ("2", "Shirley")):
        _award(sid, nm, "Boscali", 1, "capture: Riven Beach (Boscali)", "capture")
    match_set_result("Victory (Boscali)")
    for sid, nm in (("1", "Tomo"), ("2", "Shirley")):
        _award(sid, nm, "Boscali", 2, "win (Boscali)", "win")
    match_finalize(rc, [{"steamId": "1", "displayName": "Tomo", "faction": "Boscali"},
                        {"steamId": "2", "displayName": "Shirley", "faction": "Boscali"},
                        {"steamId": "3", "displayName": "Jerms", "faction": "Boscali"}])
    print("[matchtest] finalize again -> must be a no-op:")
    match_finalize(rc, [])

    print("\n[matchtest] MATCH 2: Tomo plays a loss")
    CURRENT_MISSION = "Terminal Control PALA - Day"
    _award("1", "Tomo", "Primeva", 1, "capture: Feldspar (Primeva)", "capture")
    match_set_result("Defeat (Boscali won)")
    match_finalize(rc, [{"steamId": "1", "displayName": "Tomo", "faction": "Primeva"}])

    print("\n[matchtest] MATCH 3: Mission complete, players present but NOTHING scored")
    print("            -> must NOT create a phantom record / count a match")
    CUR_MATCH = None
    with open(MATCH_HISTORY_FILE, encoding="utf-8") as f:
        before = len(json.load(f))
    match_finalize(rc, [{"steamId": "9", "displayName": "Lurker", "faction": "Boscali"}])
    with open(MATCH_HISTORY_FILE, encoding="utf-8") as f:
        after = len(json.load(f))
    print(f"[matchtest] history records before={before} after={after} "
          f"-> phantom guard: {'PASS' if after == before else 'FAIL'}")
    print("[matchtest] Lurker detail (must be 0 matches):", player_match_detail("9"))

    print("\n[matchtest] ranks.json totals:", RANK_DATA)
    print("[matchtest] fold_match_stats:", fold_match_stats())
    print("[matchtest] Tomo detail:", player_match_detail("1"))
    print("[matchtest] Jerms detail (present, never scored):", player_match_detail("3"))
    print("[matchtest] Tomo ledger:", recent_ledger_for("1", 9))
    # invariant: ledger sum == ranks for a fresh run
    led = {}
    with open(LEDGER_FILE, encoding="utf-8") as f:
        for line in f:
            e = json.loads(line)
            led[e["steamid"]] = led.get(e["steamid"], 0) + e["pts"]
    ok = all(led.get(sid, 0) == rec["points"] for sid, rec in RANK_DATA.items())
    print(f"\n[matchtest] ledger-sum == ranks invariant: {'PASS' if ok else 'FAIL'}")


def audit_ledger():
    """run.bat --audit [name]: sum points_ledger.jsonl per SteamID vs ranks.json, and break
    the awards down by category (score / win / place_* / grant / score-spike).
    Ledger may be LESS than ranks for players with pre-ledger points (normal); ledger GREATER
    than ranks would indicate a double-award bug. Informational lines (score-spike)
    carry pts:0 so they never inflate the per-player total. Pass a name to drill into one player."""
    load_ranks()
    totals = {}                      # sid -> summed pts (real awards only; info lines are 0)
    bycat = {}                       # category -> summed pts (server-wide)
    by_sid_cat = {}                  # sid -> {category -> [count, pts]}
    spikes = []                      # (ts, name, reason) for live exploit review
    try:
        with open(LEDGER_FILE, encoding="utf-8") as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    e = json.loads(line)
                except json.JSONDecodeError:
                    continue
                sid = str(e.get("steamid"))
                pts = e.get("pts", 0) or 0
                cat = e.get("category", "?")
                totals[sid] = totals.get(sid, 0) + pts
                bycat[cat] = bycat.get(cat, 0) + pts
                cc = by_sid_cat.setdefault(sid, {}).setdefault(cat, [0, 0.0])
                cc[0] += 1; cc[1] = round(cc[1] + pts, 1)
                if cat == "score-spike":
                    spikes.append((e.get("ts", ""), e.get("name", sid), e.get("reason", "")))
    except FileNotFoundError:
        print(f"[audit] no ledger yet at {LEDGER_FILE}")
    # Optional drill-down: run.bat --audit <name|sid>
    rest = sys.argv[sys.argv.index("--audit") + 1:] if "--audit" in sys.argv else []
    query = " ".join(a for a in rest if not a.startswith("--")).strip()
    if query:
        ql = query.lower()
        hits = [(sid, rec) for sid, rec in RANK_DATA.items()
                if sid == query or ql in str(rec.get("name", "")).lower()]
        if not hits:
            print(f"[audit] no player matching '{query}'")
        for sid, rec in hits:
            print(f"\n[audit] {rec.get('name', sid)} ({sid}) - {rec.get('points', 0)} pts, "
                  f"ledger {round(totals.get(sid, 0), 1)}")
            for cat, (cnt, pv) in sorted(by_sid_cat.get(sid, {}).items(), key=lambda kv: -kv[1][1]):
                print(f"    {cat:12} x{cnt:<4} {pv:+.1f}")
        return
    print(f"[audit] {len(RANK_DATA)} ranked players; ledger covers {len(totals)} of them")
    print("[audit] points by category (server-wide):")
    for cat, pv in sorted(bycat.items(), key=lambda kv: -kv[1]):
        print(f"    {cat:12} {pv:+.1f}")
    if spikes:
        print(f"[audit] {len(spikes)} score-spike flag(s) logged (review for exploits):")
        for ts, nm, why in spikes[-10:]:
            print(f"    {ts}  {nm}  {why}")
    overs = 0
    for sid, rec in sorted(RANK_DATA.items(), key=lambda kv: -kv[1].get("points", 0)):
        rp, lp = rec.get("points", 0), totals.get(sid, 0)
        if lp > rp:
            overs += 1
            print(f"  !! {rec.get('name', sid):24} ledger {lp} > ranks {rp}  (possible double-award)")
    print(f"[audit] {'OK - no over-credits' if overs == 0 else f'{overs} over-credit(s)!'}; "
          f"(ledger < ranks is expected for points earned before the ledger existed)")


def ctx_log():
    """run.bat --ctxlog <term> [lines]: show each match of <term> with N context
    lines above/below (default 3), so we can see what IDs sit next to an event."""
    import paramiko
    args = sys.argv[sys.argv.index("--ctxlog") + 1:]
    if not args:
        print("usage: run.bat --ctxlog <term> [context_lines]")
        return
    term = args[0].lower()
    ctx = int(args[1]) if len(args) > 1 and args[1].isdigit() else 3
    ssh = paramiko.SSHClient()
    ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    ssh.connect(SFTP_HOST, port=SFTP_PORT, username=SFTP_USER, password=SFTP_PASS,
                timeout=15, look_for_keys=False, allow_agent=False)
    sftp = ssh.open_sftp()
    try:
        logpath = SFTP_LOG_PATH or "/logs/console.log"
        with sftp.open(logpath, "rb") as f:
            data = f.read(16_000_000).decode("utf-8", "replace")
        lines = data.splitlines()
        shown = 0
        for i, ln in enumerate(lines):
            if term in ln.lower():
                print(f"  --- match @ line {i} ---")
                for j in range(max(0, i - ctx), min(len(lines), i + ctx + 1)):
                    print(f"  {'>>' if j == i else '  '} {lines[j].strip()}")
                shown += 1
                if shown >= 12:
                    print("  ... (stopped at 12 matches)")
                    break
        if not shown:
            print(f"[ctxlog] no matches for {term!r}")
    finally:
        sftp.close()
        ssh.close()


def scan_log():
    """Diagnostic (run.bat --scanlog [terms...]): pull the console log and surface
    lines that look like player actions (rank, score, kills, captures, ...), so we
    can see what data exists and whether it ties to a SteamID."""
    import paramiko
    if not (SFTP_HOST and SFTP_USER and SFTP_PASS):
        print("[scanlog] Missing SFTP creds. Run via run.bat --scanlog")
        return
    extra = [a.lower() for a in sys.argv[sys.argv.index("--scanlog") + 1:]]
    terms = extra or [
        "rank", "promot", "score", "kill", "destroy", "shot down", "captur",
        "objective", "credit", "reward", "experience", "eliminat", "[player]",
        "steamconnection", "death", "respawn", "landed", "takeoff",
    ]
    print(f"[scanlog] connecting to {SFTP_HOST}:{SFTP_PORT} ...")
    ssh = paramiko.SSHClient()
    ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    ssh.connect(SFTP_HOST, port=SFTP_PORT, username=SFTP_USER, password=SFTP_PASS,
                timeout=15, look_for_keys=False, allow_agent=False)
    sftp = ssh.open_sftp()
    try:
        logpath = SFTP_LOG_PATH or "/logs/console.log"
        with sftp.open(logpath, "rb") as f:
            data = f.read(16_000_000).decode("utf-8", "replace")
        lines = data.splitlines()
        print(f"[scanlog] {len(lines)} lines in {logpath}; searching: {terms}")
        counts, samples = {}, {}
        for ln in lines:
            low = ln.lower()
            for t in terms:
                if t in low:
                    counts[t] = counts.get(t, 0) + 1
                    samples.setdefault(t, [])
                    if len(samples[t]) < 5:
                        samples[t].append(ln.strip())
        if not counts:
            print("[scanlog] no matches. Try custom terms, e.g.: run.bat --scanlog elo wins")
            return
        print("[scanlog] hit counts:")
        for t in sorted(counts, key=lambda k: -counts[k]):
            print(f"  {t!r}: {counts[t]}")
        print("[scanlog] samples:")
        for t in sorted(counts, key=lambda k: -counts[k]):
            print(f"  --- {t!r} ---")
            for ln in samples[t]:
                print(f"    {ln}")
    finally:
        sftp.close()
        ssh.close()


if __name__ == "__main__":
    if "--selftest" in sys.argv:
        sample = ("81587.130: [ChatManager] CmdSendChatMessage allChat:True "
                  "connection(SteamConnection(7656119xxxxxxxxxx)) Player(Clone) 2")
        parsed = parse_chat_line(sample)
        print("parsed:", parsed)
        sample_end = ("100.0: [DedicatedServerManager] Mission complete. "
                      "Waiting 60 seconds before closing...")
        print("mission-end match:", bool(MISSION_END_RE.search(sample_end)))
        print("!votemap thresholds (players: yes-needed):",
              {n: n // 2 + 1 for n in (1, 2, 3, 4, 5, 6)})

        # ballot generation + constraints, six votes in a row ('*' = dark map)
        #
        # The ballot is CONFIG-DRIVEN (coop_count / pvp_count / pinned `guaranteed`), so the old
        # hard-coded "exactly 2 Escalation + 2 Terminal Control" invariant no longer describes a
        # correct ballot - it FAILed on every real install (S1 runs 5 pinned PvP => esc=0/tc=0;
        # S2 runs 2 pinned co-op => esc=1/tc=1) and even on a stock checkout (Breakout takes a
        # co-op slot => tc=1). Assert only what must hold for EVERY configuration and PRINT the
        # family split as information. Likewise the repeat checks: a pinned or fixed-order ballot
        # is meant to repeat, so only the RANDOM co-op fill is held to "must not repeat".
        print("\n[selftest] six ballots in a row ('*' = dark map):")
        ok = True
        _cfg_st  = _votemap_cfg()
        _coop_n  = _cfg_st["coop_count"]
        _pvp_n   = _cfg_st["pvp_count"] if _cfg_st["include_pvp"] else 0
        _vot_st  = _votable_names()
        _pins_st = [n for n in _cfg_st["guaranteed"]
                    if mission_enabled(n) and n in _vot_st and mission_key_verified(n)]
        _pin_coop  = len([n for n in _pins_st if n not in PVP_MISSIONS][:_coop_n])
        _coop_fill = max(0, _coop_n - _pin_coop)          # slots build_coop actually randomises
        _pool_sz   = sum(len(v) for v in _votemap_pool().values())
        _rand_coop = _coop_fill >= 1 and _pool_sz > _coop_fill
        print(f"  config: coop_count={_coop_n} pvp_count={_pvp_n} coop_mode={_cfg_st['coop_mode']} "
              f"pvp_mode={_cfg_st['pvp_mode']} pins={len(_pins_st)} random_coop_slots={_coop_fill}")
        prev_coop = None
        for r in range(6):
            ballot = open_vote()
            keys  = sorted(ballot, key=int)
            names = [ballot[k][1] for k in keys]
            esc  = [n for n in names if n in ESCALATION_MISSIONS]
            tc   = [n for n in names if n in TERMINAL_CONTROL_MISSIONS]
            pvp  = [n for n in names if n in PVP_MISSIONS]
            coop = frozenset(n for n in names if n not in PVP_MISSIONS)
            dark = sum(is_dark(n) for n in names)
            shown = "   ".join(
                f"{k}={ballot[k][3]}{'*' if is_dark(ballot[k][1]) else ''}" for k in keys
            )
            print(f"  vote {r + 1}: {shown}")
            print(f"          (coop={len(coop)}/{_coop_n} pvp={len(pvp)}/{_pvp_n} "
                  f"esc={len(esc)} tc={len(tc)} dark={dark})")
            if not names:
                print("          (empty ballot - every mission is disabled in the pool)")
            if len(set(names)) != len(names):
                ok = False; print("          !! the same map appears twice on one ballot")
            if keys != [str(i) for i in range(1, len(names) + 1)]:
                ok = False; print("          !! ballot keys are not a contiguous 1..N")
            _off = [n for n in names if not mission_enabled(n)]
            if _off:
                ok = False; print(f"          !! disabled map on the ballot: {_off}")
            if dark > MAX_DARK_PER_VOTE:
                ok = False; print("          !! too many dark maps")
            if _rand_coop and prev_coop is not None and coop and coop == prev_coop:
                ok = False; print("          !! identical random co-op set two votes running")
            prev_coop = coop

        # vote extraction against the most recent ballot
        print("\n[selftest] extract_vote (against the last ballot above):")
        for msg in ["!1", "!vote 2", "!3 go", "1", "4", "!9", "hi"]:
            print(f"  {msg!r:>9} -> {extract_vote(msg)}")

        if RANKS:
            print("\n[selftest] rank thresholds (points -> rank, next):")
            for pts in (0, 1, 2, 6, 24, 25, 150, 999):
                i = rank_index_for(pts)
                print(f"  {pts:>4} -> {RANKS[i][1]} ({RANKS[i][2]}); to next: {points_to_next(pts)}")
        else:
            print("\n[selftest] rank thresholds: skipped (no rank ladder configured)")
        print("\n[selftest] PASS" if ok else "\n[selftest] FAIL -- see !! lines above")
    elif "--testconn" in sys.argv:
        test_conn()
    elif "--testchat" in sys.argv:
        test_chat()
    elif "--setup-server" in sys.argv:
        setup_server()
    elif "--revert-server" in sys.argv:
        revert_server()
    elif "--check-server" in sys.argv:
        check_server()
    elif "--testtunnel" in sys.argv:
        test_tunnel()
    elif "--findchat" in sys.argv:
        find_chat()
    elif "--say" in sys.argv:
        i = sys.argv.index("--say")
        msg = " ".join(sys.argv[i + 1:]).strip() or "hello"
        rc = RemoteCommand(RCMD_HOST, RCMD_PORT)
        print(f"[say] sending to game chat: {msg!r}")
        print(f"[say] response: {rc.say(msg)}")
    elif "--endmission" in sys.argv:
        rc = RemoteCommand(RCMD_HOST, RCMD_PORT)
        secs = 5
        print(f"[endmission] forcing the current mission to end in ~{secs}s ...")
        print(f"[endmission] set-time-remaining -> {rc.set_time_remaining(secs)}")
        print("[endmission] If the running bot is watching, it should soon log:")
        print('             "[bot] mission complete detected -> vote opened".')
    elif "--cmd" in sys.argv:
        i = sys.argv.index("--cmd")
        rest = sys.argv[i + 1:]
        name = rest[0] if rest else ""
        cmdargs = rest[1:]
        rc = RemoteCommand(RCMD_HOST, RCMD_PORT)
        print(f"[cmd] sending {name!r} args={cmdargs} ...")
        print(f"[cmd] response: {rc.send(name, *cmdargs)!r}")
    elif "--players" in sys.argv:
        rc = RemoteCommand(RCMD_HOST, RCMD_PORT)
        print("[players] calling get-player-list ...")
        resp = rc.send("get-player-list")
        print(f"[players] raw response -> {resp!r}")
    elif "--colortest" in sys.argv:
        rc = RemoteCommand(RCMD_HOST, RCMD_PORT)
        msg = ("<color=#55FF55>GREEN ok</color>  "
               "<color=#FFFF00>YELLOW ok</color>  "
               "<color=#FF5555>RED ok</color>")
        print(f"[colortest] sending: {msg}")
        print(f"[colortest] response: {rc.say(msg)}")
    elif "--ls" in sys.argv:
        remote_ls()
    elif "--cat" in sys.argv:
        remote_cat()
    elif "--get" in sys.argv:
        remote_get()
    elif "--put-atomic" in sys.argv:
        remote_put_atomic()
    elif "--chmod-exec" in sys.argv:
        remote_chmod_exec()
    elif "--deploy-plugin-dry" in sys.argv:
        sys.exit(deploy_plugin_job(dry=True) or 0)
    elif "--deploy-plugin-force" in sys.argv:
        # Exit code is the ops contract (see deploy_plugin_job): 0 = the game really was
        # restarted; non-zero = it was NOT, so the caller must run its own restart.
        sys.exit(deploy_plugin_job(dry=False, force=True) or 0)
    elif "--deploy-plugin" in sys.argv:
        sys.exit(deploy_plugin_job(dry=False) or 0)
    elif "--disable-panel-restart" in sys.argv:
        disable_panel_restart()
    elif "--put" in sys.argv:
        remote_put()
    elif "--probe-missions" in sys.argv:
        probe_missions()
    elif "--set-server-name" in sys.argv:
        set_server_name()
    elif "--set-ai-limits" in sys.argv:
        set_ai_limits()
    elif "--set-balance-diff" in sys.argv:
        set_balance_diff()
    elif "--set-votekick" in sys.argv:
        set_votekick()
    elif "--apply-map-changes" in sys.argv:
        apply_map_changes()
    elif "--check-ranks" in sys.argv or "--fix-ranks" in sys.argv:
        fix_starting_ranks()
    elif "--add-rotation" in sys.argv:
        add_rotation_mission()
    elif "--upload-bepinex" in sys.argv:
        upload_bepinex()
    elif "--centre" in sys.argv or "--center" in sys.argv:
        command_centre()
    elif "--scanlog" in sys.argv:
        scan_log()
    elif "--ranks" in sys.argv:
        show_ranks()
    elif "--matchtest" in sys.argv:
        match_selftest()
    elif "--audit" in sys.argv:
        audit_ledger()
    elif "--ctxlog" in sys.argv:
        ctx_log()
    elif "--rankpreview" in sys.argv:
        if not RANKS:
            print("[rankpreview] no rank ladder configured - nothing to post")
            sys.exit(0)
        rc = RemoteCommand(RCMD_HOST, RCMD_PORT)
        online = len(get_players(rc))
        print(f"[rankpreview] {online} player(s) online; sending {len(RANKS)}-rank preview...")
        rc.say("<color=#FFFF00>=== SERVER RANKS (points needed) ===</color>")
        row = []
        for i, (thr, name, abbr, color) in enumerate(RANKS, 1):
            row.append(f"<color={color}>{i}. {name} [{abbr}] {thr}</color>")
            if len(row) == 4:
                rc.say("   ".join(row))
                row = []
        if row:
            rc.say("   ".join(row))
        print("[rankpreview] done")
    else:
        # SINGLE-INSTANCE GUARD (2026-07-27): two daemons on one install double-award
        # points and fight over ranks.json (seen when a scheduled task relaunched the
        # stack on top of a live one). The OS releases the byte-lock on ANY process
        # death, so a stale lock can never wedge a restart. CLI modes (--deploy-plugin,
        # --ls, ...) are dispatched above and never reach this daemon path.
        try:
            import msvcrt as _msvcrt
        except ImportError:
            _msvcrt = None
        if _msvcrt is not None:
            # The lock belongs to THIS INSTALL. Never fall back to the shared
            # ~/.nuke-option-toolkit: launched without NOST_DATA_DIR (a bare
            # `python no_mapvote_bot.py`, an ops script, a fresh install), two
            # DIFFERENT servers would contend for one bot.lock and the second
            # would refuse to start. _BASE_DIR is this bot's own folder.
            _lock_dir = os.environ.get("NOST_DATA_DIR") or os.path.join(_BASE_DIR, ".nost-data")
            _lock_fh = None
            try:
                os.makedirs(_lock_dir, exist_ok=True)
                _lock_fh = open(os.path.join(_lock_dir, "bot.lock"), "a")
                _lock_fh.seek(0)
            except OSError as _lock_err:
                # FAIL OPEN: not being able to OPEN the lock file (permissions, full
                # disk, AV/OneDrive holding it, unreachable data dir) is not evidence
                # of a second bot. Refusing here is a silent non-start. Warn and run;
                # a real duplicate is still caught by acquire_bot_singleton()'s named
                # mutex inside main().
                print(f"[bot] singleton lock file unavailable ({_lock_err}) - starting anyway "
                      f"(guard degraded to the named mutex).")
                _lock_fh = None
            if _lock_fh is not None:
                try:
                    _msvcrt.locking(_lock_fh.fileno(), _msvcrt.LK_NBLCK, 1)
                except OSError:
                    # Only a failed LOCK means someone else holds it.
                    print("[bot] another bot is already running for this install - exiting.")
                    sys.exit(0)
        # Self-healing: if main() ever throws an unexpected error, log it and
        # restart the loop rather than dying. Ctrl-C still stops cleanly. (An
        # external keep-alive wrapper, run_keepalive.bat, covers hard process
        # death -- killed / OOM / reboot -- that Python can't catch.)
        while True:
            try:
                main()
            except KeyboardInterrupt:
                print("\n[bot] stopped.")
                break
            except Exception:                       # noqa: BLE001 - never die on a bug
                print("[bot] main() crashed; restarting in 5s:")
                traceback.print_exc()
                sys.stdout.flush()
                activity("Bot hit an error and is auto-restarting in 5s "
                         "(details in bot_output.log)", "!")
                time.sleep(5)
