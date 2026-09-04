#!/usr/bin/env python3
"""
Nuclear Option - unified COMMAND CENTRE (single-window TUI).

One polished, full-screen window that replaces both the old watch screen
(watch.bat) and the old interactive command centre (centre.bat):

  TOP-LEFT   live SERVER CONSOLE  - every line the bot reads from the game's
             console.log as it goes through BepInEx/the plugin, colourised so
             errors/exceptions jump out (for monitoring plugin issues).
  TOP-RIGHT  PLAYERS table        - refreshed continuously: each online pilot's
             plane, server rank, in-game rank and points scored this match.
  MID-RIGHT  ACTIVITY feed        - chat, joins/leaves, votes, captures, wins,
             rank-ups (everything the old watch screen showed).
  BOTTOM     COMMAND console      - an always-visible list of commands plus an
             input line that drives the live server (say, kick, nextmap, ...).

This is a VIEWER + controller. It reads the files the bot publishes
(console_mirror.log, dashboard_state.json, activity.log) and sends commands
straight to the server relay - exactly like the old command centre. Closing
this window does NOT stop the bot. It needs no SFTP credentials of its own.

Run it via commandcentre.bat (opens Windows Terminal maximised). Requires the
'textual' package:  python -m pip install textual
"""
from __future__ import annotations

import json
import os
import re
import threading
import time

# Reuse the bot's constants + relay client so there is a single source of truth
# for ranks, the command list and the server address. Importing is side-effect
# free (the bot only runs when launched as __main__).
import no_mapvote_bot as bot

from rich.text import Text
from textual import on
from textual.app import App, ComposeResult
from textual.containers import Horizontal, Vertical, ScrollableContainer
from textual.screen import ModalScreen
from textual.suggester import Suggester
from textual.widgets import Button, DataTable, Footer, Header, Input, RichLog, Static

RANKS              = bot.RANKS
CENTRE_SERVER_CMDS = bot.CENTRE_SERVER_CMDS
# A few commands behave differently in the command centre than in the bot's own
# console (e.g. nextmap is name-only with Tab-autocomplete here, not the raw
# <group> <name> <maxTime> the bot console still takes). Override only the DISPLAYED
# hint/desc so the shared CENTRE_SERVER_CMDS the bot also uses stays untouched.
CC_HINT_OVERRIDE = {
    "nextmap": ("<mission name>", "queue the next mission (Tab to autocomplete)"),
}
STATUS_CODES       = bot.STATUS_CODES
RCMD_HOST          = bot.RCMD_HOST
RCMD_PORT          = bot.RCMD_PORT
rank_index_for     = bot.rank_index_for

STATE_FILE   = bot.DASHBOARD_STATE_FILE
CONSOLE_FILE = bot.CONSOLE_MIRROR_FILE
ACTIVITY_FILE = bot.ACTIVITY_FILE
RANK_FILE    = bot.RANK_FILE
ADMIN_CMD_FILE = bot.ADMIN_CMD_FILE   # grants etc. are queued here for the bot to apply
SETTINGS_FILE  = os.path.join(os.path.dirname(os.path.abspath(__file__)), "command_centre_settings.json")

TAIL_INTERVAL  = 0.7    # seconds between console/activity tail polls
STATE_INTERVAL = 2.0    # seconds between dashboard_state.json reads (table refresh)
FEED_STALE_S   = 60     # if dashboard_state.json is older than this, warn the bot may be down
                        # (was 25; routine SFTP/RCMD stalls of 15–30s+ false-alarmed the feed)
CONSOLE_SUMMARY_INTERVAL = 15   # seconds between "filtered N noisy lines" summary lines


# ---------------------------------------------------------------------------
# Faction display helpers
# ---------------------------------------------------------------------------
def faction_short(name: str) -> str:
    n = (name or "").lower()
    if n.startswith("bosc") or n == "bdf":
        return "BDF"
    if n.startswith("prim") or n == "pala":
        return "PALA"
    return (name or "-")[:6]


def faction_color(name: str) -> str:
    n = (name or "").lower()
    if n.startswith("bosc") or n == "bdf":
        return "#5BA3FF"
    if n.startswith("prim") or n == "pala":
        return "#FF6B5B"
    return "grey62"


def fmt_clock(seconds: float) -> str:
    seconds = max(0, int(seconds))
    h, rem = divmod(seconds, 3600)
    m, s = divmod(rem, 60)
    return f"{h}:{m:02d}:{s:02d}" if h else f"{m}:{s:02d}"


# ---------------------------------------------------------------------------
# Line colourising
# ---------------------------------------------------------------------------
def console_style(line: str) -> str:
    low = line.lower()
    if any(k in line for k in ("Exception", "NullReference", "Traceback")) or \
       any(k in low for k in ("error", "failed", "fatal")):
        return "bold #FF5555"
    if "warn" in low:
        return "#FFC857"
    if "[nostats]" in low:
        return "grey42"
    if any(k in line for k in ("NukeStats", "[diag]", "BepInEx", "Doorstop", "Harmony", "Chainloader")):
        return "#4DD0E1"
    if "[ChatManager]" in line or "CmdSendChatMessage" in line:
        return "#9DA9C9"
    return "grey70"


_ACT_RE = re.compile(r"^(\d\d:\d\d:\d\d [AP]M)\s\s(.*)$")
_ACT_TAGS = {
    "[WIN]": "bold #4CE04C", "[JOIN]": "#4CE04C", "[OK]": "#4CE04C",
    "[LOSS]": "#FF5555", "[!]": "bold #FF5555",
    "[KILL]": "#FF8C00", "[CAP]": "#FFC857", "[RANK]": "#E07CE0",
    "[VOTE]": "#4DD0E1", "[MAP]": "#4DD0E1",
    "[BOT]": "#3CA0A8", "[PM]": "#9D8CFF", "[CHAT]": "#D7DAE0",
    "[TEAM]": "#FFC857", "[LEFT]": "grey50", "[INFO]": "grey50",
}


def activity_text(line: str) -> Text:
    color = "grey70"
    for tag, c in _ACT_TAGS.items():
        if tag in line:
            color = c
            break
    if "======" in line:
        color = "bold white"
    m = _ACT_RE.match(line)
    if m:
        t = Text()
        t.append(m.group(1) + "  ", style="grey42")
        t.append(m.group(2), style=color)
        return t
    return Text(line, style=color)


def console_text(line: str) -> Text:
    return Text(line, style=console_style(line))


# ---------------------------------------------------------------------------
# Console noise filter. The server console is dominated by a few spammy sources
# (remote-command polling, weapon/AI chatter, Unity physics warnings, our own
# [NOSTATS] feed). These are suppressed and counted; a periodic one-line summary
# shows they're still happening. ERROR lines ALWAYS show (in red), even inside a
# noise category, so problems are never hidden. Ctrl+N flips to a raw view.
# ---------------------------------------------------------------------------
_ERR_TOKENS = ("Exception", "NullReference", "Traceback", "stack trace")
_ERR_LOW    = ("error", "failed", "fatal", " denied", "could not patch")

# suppressed-category key -> short label shown in the summary line
NOISE_LABELS = {
    "remote":    "remote-cmd",
    "weapon":    "weapon-mgr",
    "ai":        "AI-units",
    "nostats":   "NOSTATS",
    "blast":     "blast",
    "kinematic": "kinematic-vel",
    "engine":    "engine-warn",
    "steam":     "Steam-net",
}

_ENGINE_NOISE = (
    "linear velocity of a kinematic",
    "boxcollider does not support negative",
    "the effective box size has been forced",
    "if you absolutely need to use negative s",
    "did you use #pragma only_renderers",
    "if subshaders removal was intentional",
    "fallback handler could not load library",
    "particle system is trying to spawn",
)


def is_error_line(line: str) -> bool:
    low = line.lower()
    return any(k in line for k in _ERR_TOKENS) or any(k in low for k in _ERR_LOW)


def classify_console(line: str) -> str:
    """Return 'error' (always show, red), 'show' (normal), or a NOISE_LABELS key
    to suppress + count."""
    low = line.lower()
    err = is_error_line(line)
    if "[serverremotecommands]" in low:
        # surface only remote-command PROBLEMS (an exception, or a non-Success reply)
        if err or ("response:" in low and "success" not in low):
            return "error"
        return "remote"
    if "[weaponmanager]" in low:
        return "error" if err else "weapon"
    if ("[aihelo]" in low or "[aiplane]" in low or "[aiground]" in low
            or "aipilot" in low):                 # e.g. "AIPilotCombatModes ... UseBombs target null"
        return "error" if err else "ai"
    if "[nostats]" in low:
        return "error" if err else "nostats"
    if "[blastmanager]" in low or "blast manager" in low:
        return "error" if err else "blast"
    if "[steammanager]" in low:
        # benign Steam Datagram Relay / GameNetworkingSockets spew (stats "won't fit",
        # "Waited Nms for SteamNetworkingSockets lock", ICE/TURN, P2PBadRoute) -> summarise;
        # surface only genuine connectivity failures (e.g. can't reach ANY relay cluster).
        if err or "unable to communicate with any" in low or "no route" in low:
            return "error"
        return "steam"
    if any(p in low for p in _ENGINE_NOISE):
        return "engine"
    return "error" if err else "show"


# ---------------------------------------------------------------------------
# Incremental file tailer (byte-offset based; survives truncation/rotation)
# ---------------------------------------------------------------------------
class Tailer:
    def __init__(self, path: str):
        self.path = path
        self.offset = 0
        self.buf = b""

    def seed(self, max_lines: int) -> list[str]:
        """Return the last `max_lines` lines and park the offset at EOF."""
        try:
            size = os.path.getsize(self.path)
            self.offset = size
            with open(self.path, "rb") as f:
                read = min(size, 256_000)
                f.seek(size - read)
                data = f.read()
            return data.decode("utf-8", "replace").splitlines()[-max_lines:]
        except OSError:
            return []

    def poll(self) -> list[str]:
        try:
            size = os.path.getsize(self.path)
        except OSError:
            return []
        if size < self.offset:           # truncated/rotated -> start over
            self.offset, self.buf = 0, b""
        if size == self.offset:
            return []
        try:
            with open(self.path, "rb") as f:
                f.seek(self.offset)
                chunk = f.read()
                self.offset = f.tell()
        except OSError:
            return []
        self.buf += chunk
        parts = self.buf.split(b"\n")
        self.buf = parts.pop()           # keep trailing partial line
        return [p.decode("utf-8", "replace") for p in parts]


# ---------------------------------------------------------------------------
# Inline autocomplete for player names after the "grant " command. Completes the
# partial name with the matching full player name (online or from ranks.json);
# accept with Right-arrow / End / Tab.
# ---------------------------------------------------------------------------
class CommandSuggester(Suggester):
    """Inline ghost-text autocomplete. Completes the COMMAND on the first word, then
    context-aware ARGS: player names (grant/spec/move/join), factions (move/join),
    mission names (nextmap). Accept with Right-arrow / End / Tab."""

    def __init__(self, names_provider, commands, missions):
        super().__init__(use_cache=False, case_sensitive=False)
        self._names = names_provider                  # callable -> list[str] of player names
        self._commands = sorted(set(commands))
        self._missions = list(missions)

    @staticmethod
    def _pick(value, pool, partial, tail_len):
        if not partial:
            return None
        pl = partial.lower()
        for item in pool:
            if item and item.lower().startswith(pl) and len(item) > tail_len:
                return value + item[tail_len:]        # always starts with value -> renders as a ghost
        return None

    async def get_suggestion(self, value: str) -> str | None:
        if not value:
            return None
        if " " not in value:                          # completing the command word
            return self._pick(value, self._commands, value, len(value))
        head, _, remainder = value.partition(" ")
        cmd = head.lower()
        if cmd == "nextmap":                          # whole remainder = mission name (has spaces)
            if not remainder:                         # just typed "nextmap " -> offer the first map as an on-ramp
                return (value + self._missions[0]) if self._missions else None
            return self._pick(value, self._missions, remainder, len(remainder))
        if cmd in ("grant", "spec", "spectate"):
            return self._pick(value, self._names(), remainder, len(remainder))
        if cmd in ("move", "join", "team"):           # <player> then <faction>
            if remainder.endswith(" ") or " " in remainder.strip():
                last = value.rsplit(" ", 1)[1]
                return self._pick(value, ["boscali", "primeva"], last, len(last))
            return self._pick(value, self._names(), remainder, len(remainder))
        return None


# ---------------------------------------------------------------------------
# A RichLog whose lines can be LEFT-CLICKED to copy that line to the clipboard
# (handy for grabbing an error line out of the console). Copy goes to the
# terminal clipboard (OSC 52) via App.copy_to_clipboard.
# ---------------------------------------------------------------------------
class ClickableLog(RichLog):
    can_focus = False            # clicking to copy must not steal focus from the command box

    def on_click(self, event) -> None:
        try:
            if event.screen_offset not in self.content_region:
                return                            # click landed on the border/padding, not a line
            row = self.scroll_offset.y + (event.screen_y - self.content_region.y)
            if 0 <= row < len(self.lines):
                text = self.lines[row].text.rstrip()
                if text.strip():
                    self.app.copy_to_clipboard(text)
                    self.notify(f"copied: {text.strip()[:70]}", timeout=2)
        except Exception:        # noqa: BLE001 - copying must never crash the UI
            pass


# ---------------------------------------------------------------------------
# Per-player action panel, opened by clicking a row in the PLAYERS table.
# Returns {action, player[, points]} via dismiss(); the app applies it.
# ---------------------------------------------------------------------------
class PlayerActionScreen(ModalScreen):
    CSS = """
    PlayerActionScreen { align: center middle; }
    #box { width: 62; height: auto; padding: 1 2; border: round #5BA3FF; background: $surface; }
    #row1, #row2, #row3 { height: auto; margin-top: 1; }
    #amt { width: 24; }
    Button { margin: 0 1 0 0; }
    """

    BINDINGS = [("escape", "dismiss_none", "Close")]

    def __init__(self, player: dict):
        super().__init__()
        self.player = player

    def compose(self) -> ComposeResult:
        p = self.player
        with Vertical(id="box"):
            yield Static(Text(f"{p.get('rank_abbr', '')}  {p.get('name', '?')}",
                              style=f"bold {p.get('rank_color', 'white')}"))
            yield Static(Text(f"{p.get('faction', '')}  ·  {p.get('aircraft') or '—'}  ·  "
                              f"{p.get('points', 0):g} pts  ·  SteamID {p.get('sid', '')}", style="grey62"))
            with Horizontal(id="row1"):
                yield Input(placeholder="points  e.g. 100 / -50", id="amt")
                yield Button("Grant", id="grant", variant="success")
            with Horizontal(id="row2"):
                yield Button("Kick", id="kick", variant="warning")
                yield Button("Ban", id="ban", variant="error")
                yield Button("Copy SteamID", id="copysid")
                yield Button("Close", id="close")
            with Horizontal(id="row3"):
                yield Button("→ Boscali", id="toboscali")
                yield Button("→ Primeva", id="toprimeva")
                yield Button("Spectate", id="tospec", variant="warning")

    def action_dismiss_none(self) -> None:
        self.dismiss(None)

    def _do_grant(self) -> None:
        raw = self.query_one("#amt", Input).value.strip()
        try:
            pts = float(raw)
        except ValueError:
            self.notify("enter a number of points first", severity="error", timeout=3)
            return
        self.dismiss({"action": "grant", "player": self.player, "points": pts})

    @on(Button.Pressed, "#grant")
    def _b_grant(self) -> None:
        self._do_grant()

    @on(Input.Submitted, "#amt")
    def _i_grant(self) -> None:
        self._do_grant()

    @on(Button.Pressed, "#kick")
    def _b_kick(self) -> None:
        self.dismiss({"action": "kick", "player": self.player})

    @on(Button.Pressed, "#ban")
    def _b_ban(self) -> None:
        self.dismiss({"action": "ban", "player": self.player})

    @on(Button.Pressed, "#copysid")
    def _b_copy(self) -> None:
        self.dismiss({"action": "copysid", "player": self.player})

    @on(Button.Pressed, "#toboscali")
    def _b_toboscali(self) -> None:
        self.dismiss({"action": "move", "player": self.player, "faction": "boscali"})

    @on(Button.Pressed, "#toprimeva")
    def _b_toprimeva(self) -> None:
        self.dismiss({"action": "move", "player": self.player, "faction": "primeva"})

    @on(Button.Pressed, "#tospec")
    def _b_tospec(self) -> None:
        self.dismiss({"action": "spec", "player": self.player})

    @on(Button.Pressed, "#close")
    def _b_close(self) -> None:
        self.dismiss(None)


# ---------------------------------------------------------------------------
# Live ASCII map. Terrain is sampled from the REAL game map images and baked into
# map_atlas.py by build_map_atlas.py (re-run that if the source images change).
# We render a coloured ASCII rendition of the satellite map (true per-cell colour
# from the image) with the in-game quadrant grid, named bases, and live plane
# positions overlaid. World x = E-W, z = N-S; x0/x1/z0/z1 = the world extent each
# image covers (calibrate against live plane coords vs known base quadrants).
# ---------------------------------------------------------------------------
try:
    from map_atlas import ATLAS as _ATLAS
except Exception:                                    # noqa: BLE001 - map optional
    _ATLAS = {}

# Retro tactical style: solid-green land on black water, faint grid, small base
# icons placed by in-game grid ref. (The satellite palette in the atlas is unused
# now - we only need each cell's land/water classification.)
_WATER = frozenset("Wws")
_SEA = "#000000"                     # water -> black
_LAND_BG = "#2EA847"                 # land -> solid green
_CONCRETE = "#8b9097"                # cities / docks / airfields -> grey
_BRIDGE_FG = "#a9b0b8"               # bridge / causeway -> light-grey thin line
_BRIDGE_GLYPH = "═"
_GRID_LAND = "#1c6e35"               # faint grid line over land
_GRID_SEA = "#1f5e33"                # faint grid line over sea
_GRIDX_STYLE = "#56cf70"            # grid intersections (bright)
_LABEL_STYLE = "#5cab6e"            # row/col grid labels (dim green)
_BASE_ICON = "⌂"
_BASE_STYLE = "bold #FFD400"         # base icon (amber) - used by the legend
_GROUND_ICON = "✝"                   # shot-down / grounded player, at last known location
_GROUND_STYLE = "bold #D08A8A on #160c0c"


def _edge_glyph(terr, r, c, C, R):
    """Coastline smoothing: a land/concrete cell touching water renders as a
    triangle / half-block (land colour) over water, so edges + thin necks look
    crisp instead of blocky. Returns the glyph, or None for solid interior land."""
    def water(rr, cc):
        return not (0 <= cc < C and 0 <= rr < R) or terr[rr][cc] in _WATER \
            or terr[rr][cc] == "B"          # bridge sits over water -> coast forms around it
    wu, wd, wl, wr = water(r - 1, c), water(r + 1, c), water(r, c - 1), water(r, c + 1)
    if not (wu or wd or wl or wr):
        return None                                  # interior -> solid block
    if wu and wl and not wd and not wr: return "◢"   # land lower-right
    if wu and wr and not wd and not wl: return "◣"   # land lower-left
    if wd and wl and not wu and not wr: return "◥"   # land upper-right
    if wd and wr and not wu and not wl: return "◤"   # land upper-left
    if wu and not wd: return "▄"                     # land bottom half
    if wd and not wu: return "▀"                     # land top half
    if wl and not wr: return "▐"                     # land right half
    if wr and not wl: return "▌"                     # land left half
    return None                                      # thin two-sided strip -> solid


def world_to_cell(data, x, z):
    """world (x,z) -> (atlas_col, atlas_row) via the image-extent bounds, or None."""
    try:
        c = int((x - data["x0"]) / (data["x1"] - data["x0"]) * data["cols"])
        r = int((z - data["z0"]) / (data["z1"] - data["z0"]) * data["rows"])
        if 0 <= c < data["cols"] and 0 <= r < data["rows"]:
            return c, r
    except Exception:                                # noqa: BLE001
        pass
    return None


def grid_to_cell(data, ref):
    """in-game grid ref like 'k17' -> (atlas_col, atlas_row), or None. Goes via the
    in-game grid model (cell centre -> world) then the bounds -> on the terrain."""
    try:
        wx = data["xmin"] + (int(ref[1:]) - 0.5) * data["cell"]
        wz = data["znorth"] - (ord(ref[0].lower()) - 97 + 0.5) * data["cell"]
        return world_to_cell(data, wx, wz)
    except Exception:                                # noqa: BLE001
        return None


def grid_ref(x, z, data):
    """world (x,z) -> in-game grid reference like 'f18', via the in-game grid model
    (xmin/cell origin, z increases NORTH). Matches what players read in-game."""
    try:
        col = int((x - data["xmin"]) / data["cell"]) + 1
        row = chr(97 + int((data["znorth"] - z) / data["cell"]))
        return f"{row}{col}"
    except Exception:                                # noqa: BLE001
        return None


# The app
# ---------------------------------------------------------------------------
class CommandCentre(App):
    TITLE = "Nuclear Option - Command Centre"
    SUB_TITLE = f"{RCMD_HOST}:{RCMD_PORT}"

    CSS = """
    Screen { layout: vertical; }
    #status { height: 3; padding: 0 1; background: $panel; }
    #main { height: 1fr; }
    #leftcol { width: 1fr; }
    #rightcol { width: 1fr; }
    #console { height: 7; border: round #4DD0E1; }        /* 5 content lines + border */
    #players { height: auto; max-height: 18; border: round #5BA3FF; }  /* fit rows, no scroll */
    #maplegend { height: 2; padding: 0 1; background: $panel; }   /* fixed map key (never scrolls) */
    #mapbox { height: 2fr; border: round #6BE06B; overflow: auto auto; scrollbar-size: 1 1; background: #000000; }
    #map { width: auto; height: auto; background: #000000; }   /* retro black sea; sized to content -> pans */
    #activity { height: 1fr; border: round #E07CE0; }     /* tall - extends up next to the console */
    #cmdlist { height: auto; max-height: 6; padding: 0 1; border: round #5A5A5A; }
    #response { height: 8; border: round #FFC857; }
    #cmd { height: 3; }
    DataTable { height: 1fr; }
    """

    BINDINGS = [
        ("ctrl+q", "quit", "Quit"),
        ("ctrl+l", "clear_panes", "Clear panes"),
        ("ctrl+n", "toggle_filter", "Raw/filtered console"),
    ]

    def __init__(self):
        super().__init__()
        self._rc = bot.RemoteCommand(RCMD_HOST, RCMD_PORT)
        self._rc_lock = threading.Lock()
        self._con_tail = Tailer(CONSOLE_FILE)
        self._act_tail = Tailer(ACTIVITY_FILE)
        self._filter = True              # filter console noise (Ctrl+N -> raw view)
        self._suppressed = {}            # noise-category -> count since last summary
        self._last_summary = 0.0         # when the last "filtered N" summary was written
        self._pending = None             # (wire, args, label) awaiting 'confirm'
        self._known_names = []           # full player names for 'grant' autocomplete
        self._ranks_names = []           # cached names from ranks.json (offline players too)
        self._ranks_mtime = None
        _local_cmds = ["grant", "leaderboard", "lb", "top", "ranks", "rankpreview",
                       "endmission", "raw", "help", "cls", "quit",
                       "move", "spec", "spectate", "join", "balance", "nextmap"]
        # PvP missions FIRST so `nextmap escalation` exact-matches the PvP "Escalation"
        # (and Tab surfaces it) instead of the coop "Escalation Co-op as BDF ..." name.
        self._missions = list(getattr(bot, "PVP_MISSIONS", [])) + \
            list(getattr(bot, "ESCALATION_MISSIONS", [])) + \
            list(getattr(bot, "TERMINAL_CONTROL_MISSIONS", []))
        self._suggester = CommandSuggester(
            lambda: self._known_names,
            [e[0] for e in CENTRE_SERVER_CMDS] + _local_cmds,
            self._missions)
        self._players_by_sid = {}        # sid -> player dict, for the click-to-act menu
        self._table_sig = None           # last rendered players-table signature (skip rebuild if unchanged)
        self._state_miss = 0             # consecutive failed dashboard_state.json reads (debounce the "bot down" banner)

    # ----- layout -----
    def compose(self) -> ComposeResult:
        yield Header(show_clock=True)
        yield Static(id="status")
        with Horizontal(id="main"):
            with Vertical(id="leftcol"):
                yield ClickableLog(id="console", max_lines=2500, wrap=False, markup=False, auto_scroll=True)
                yield DataTable(id="players", zebra_stripes=True, cursor_type="row")
                yield Static(id="maplegend")                 # fixed legend (above the map)
                with ScrollableContainer(id="mapbox", can_focus=True):
                    # shrink=False so the Static keeps its full content size -> the
                    # container actually overflows and scroll/pan works
                    yield Static(id="map", expand=False, shrink=False)
            with Vertical(id="rightcol"):
                yield ClickableLog(id="activity", max_lines=1500, wrap=True, markup=False, auto_scroll=True)
                yield Static(id="cmdlist")
                yield ClickableLog(id="response", max_lines=400, wrap=True, markup=False, auto_scroll=True)
                yield Input(id="cmd", suggester=self._suggester,
                            placeholder="type a command  (e.g.  say hello)   -   'help' lists everything")
        yield Footer()

    def on_mount(self) -> None:
        con = self.query_one("#console", RichLog)
        con.border_title = "SERVER CONSOLE  (live - BepInEx / plugin / errors)"
        act = self.query_one("#activity", RichLog)
        act.border_title = "ACTIVITY  (chat / joins / votes / ranks / wins)"
        resp = self.query_one("#response", RichLog)
        resp.border_title = "COMMAND OUTPUT"
        try:
            self.query_one("#mapbox", ScrollableContainer).border_title = "LIVE MAP  (wheel / arrows to pan)"
            self.query_one("#map", Static).update(Text("  waiting for the bot's feed…", style="grey50"))
        except Exception:                        # noqa: BLE001
            pass

        table = self.query_one("#players", DataTable)
        table.border_title = "PLAYERS"
        # widths trimmed so the table fits the half-width column without scrolling
        table.add_column("#", width=2)
        table.add_column("Pilot", width=20)      # roomier so names aren't truncated
        table.add_column("Fac", width=4)
        table.add_column("Plane", width=18)
        table.add_column("Rank", width=18)
        table.add_column("IG", width=3)
        table.add_column("Match", width=8)
        table.add_column("Coords", width=14)     # in-game grid ref + km x,z (map calibration aid)

        try:
            self.query_one("#maplegend", Static).update(self._legend_text())
        except Exception:                        # noqa: BLE001
            pass
        self.query_one("#cmdlist", Static).update(self._command_list())
        resp.write(activity_text("Command centre ready. Reading the bot's live feed - type 'help' for commands."))

        # seed the panes so they aren't empty on launch (the console seed is filtered
        # too, else it opens full of remote-command spam)
        seed_suppressed = {}
        for line in self._con_tail.seed(600):
            cat = classify_console(line) if self._filter else "show"
            if cat in ("error", "show"):
                con.write(Text(line, style="bold #FF5555") if cat == "error" else console_text(line))
            else:
                seed_suppressed[cat] = seed_suppressed.get(cat, 0) + 1
        if seed_suppressed:
            parts = [f"{seed_suppressed[k]} {NOISE_LABELS[k]}" for k in NOISE_LABELS if seed_suppressed.get(k)]
            con.write(Text(f"  ⋯ filtered {' · '.join(parts)} noisy startup lines  (Ctrl+N for raw)", style="grey42"))
        self._last_summary = time.time()
        for line in self._act_tail.seed(250):
            act.write(activity_text(line))

        # restore the last-chosen colour theme (palette) and persist any future change
        try:
            with open(SETTINGS_FILE, encoding="utf-8") as f:
                saved_theme = json.load(f).get("theme")
            if saved_theme:
                self.theme = saved_theme
        except Exception:        # noqa: BLE001 - missing/invalid file or unknown theme name
            pass
        self.watch(self, "theme", self._save_theme, init=False)

        self.set_interval(TAIL_INTERVAL, self._poll_tails)
        self.set_interval(STATE_INTERVAL, self._refresh_state)
        self._refresh_state()
        self.query_one("#cmd", Input).focus()

    def _save_theme(self, theme: str) -> None:
        """Persist the chosen theme so the next launch reuses it (Ctrl+P to change it)."""
        try:
            with open(SETTINGS_FILE, "w", encoding="utf-8") as f:
                json.dump({"theme": theme}, f)
        except OSError:
            pass

    # ----- the always-visible command list -----
    def _command_list(self) -> Text:
        t = Text()
        t.append("SERVER  ", style="bold white")
        for alias, _wire, ahint, _desc, danger in CENTRE_SERVER_CMDS:
            hint = CC_HINT_OVERRIDE.get(alias, (ahint, _desc))[0]
            t.append(alias, style="bold #FF7B7B" if danger else "bold #6BE06B")
            if hint:
                t.append(f" {hint}", style="grey50")
            t.append("  ")
        t.append("\nLOCAL   ", style="bold white")
        for alias, hint in (("grant", "<player> <pts>"), ("leaderboard", "top points"),
                            ("ranks", "saved ranks"), ("rankpreview", "post ladder"),
                            ("endmission", "force end"), ("raw", "<cmd> <args>"),
                            ("help", ""), ("cls", "clear"), ("quit", "")):
            t.append(alias, style="bold #4DD0E1")
            if hint:
                t.append(f" {hint}", style="grey50")
            t.append("  ")
        t.append("\nTEAM    ", style="bold white")
        for alias, hint in (("move", "<player> <boscali|primeva>"), ("spec", "<player>"),
                            ("join", "<player> <faction>"), ("balance", "force a PvP balance pass")):
            t.append(alias, style="bold #36FFD0")
            if hint:
                t.append(f" {hint}", style="grey50")
            t.append("  ")
        t.append("\n")
        t.append("red", style="bold #FF7B7B")
        t.append(" = changes the server -> asks you to type 'confirm'.   ", style="grey58")
        t.append("Click", style="bold")
        t.append(" a player = actions · ", style="grey58")
        t.append("click", style="bold")
        t.append(" a console line = copy   ", style="grey58")
        t.append("Ctrl+L", style="bold")
        t.append(" clear panes   ", style="grey58")
        t.append("Ctrl+N", style="bold")
        t.append(" raw/filtered console   ", style="grey58")
        t.append("Ctrl+Q", style="bold")
        t.append(" quit", style="grey58")
        return t

    # ----- live tails -----
    def _writelog(self, log: RichLog, renderable) -> None:
        """Write a line but only auto-scroll if the user is already at the bottom,
        so scrolling up to read history isn't yanked back when new lines arrive."""
        log.write(renderable, scroll_end=log.is_vertical_scroll_end)

    def _poll_tails(self) -> None:
        con = self.query_one("#console", RichLog)
        for line in self._con_tail.poll():
            self._emit_console(con, line)
        self._flush_console_summary(con)
        act = self.query_one("#activity", RichLog)
        for line in self._act_tail.poll():
            self._writelog(act, activity_text(line))

    def _emit_console(self, con: RichLog, line: str) -> None:
        if not self._filter:
            self._writelog(con, console_text(line))
            return
        cat = classify_console(line)
        if cat == "error":
            self._writelog(con, Text(line, style="bold #FF5555"))   # always surface, highlighted
        elif cat == "show":
            self._writelog(con, console_text(line))
        else:
            self._suppressed[cat] = self._suppressed.get(cat, 0) + 1

    def _flush_console_summary(self, con: RichLog) -> None:
        now = time.time()
        if now - self._last_summary < CONSOLE_SUMMARY_INTERVAL:
            return
        if self._suppressed:
            secs = max(1, int(now - self._last_summary))
            parts = [f"{self._suppressed[k]} {NOISE_LABELS[k]}" for k in NOISE_LABELS if self._suppressed.get(k)]
            self._writelog(con, Text(f"  ⋯ filtered {' · '.join(parts)}  (last {secs}s · Ctrl+N for raw)", style="grey42"))
            self._suppressed = {}
        self._last_summary = now

    # ----- state / players table -----
    def _read_state(self):
        try:
            with open(STATE_FILE, encoding="utf-8") as f:
                return json.load(f)
        except (OSError, ValueError):
            return None

    def _refresh_state(self) -> None:
        st = self._read_state()
        status = self.query_one("#status", Static)
        if not st:
            # A single dropped sample is almost always the bot's atomic-replace window, not a
            # real outage - don't flash "bot down". Only show it after 2 consecutive misses
            # (~4s, well under FEED_STALE_S); keep the last good status line up until then.
            self._state_miss += 1
            if self._state_miss >= 2:
                status.update(Text("waiting for the bot's feed...  (is the bot running? launch run_keepalive.bat)",
                                   style="bold #FFC857"))
            return
        self._state_miss = 0

        now = time.time()
        age = now - st.get("ts", 0)
        line = Text()
        if age > FEED_STALE_S:
            line.append("● FEED STALE ", style="bold #FF5555")
            line.append(f"(bot silent {int(age)}s - may be down)  ", style="#FF5555")
        elif st.get("server_up"):
            line.append("● SERVER UP  ", style="bold #4CE04C")
        else:
            line.append("● SERVER DOWN  ", style="bold #FF5555")
        line.append("│ ", style="grey42")
        line.append(f"{st.get('mission', '?')}  ", style="white")
        line.append("│ ", style="grey42")
        cur, mx, at = st.get("time_current", 0), st.get("time_max", 0), st.get("time_at", 0)
        if mx:
            left = mx - (cur + (now - at)) if at else (mx - cur)
            line.append(f"⏱ {fmt_clock(left)} left  ", style="#4DD0E1")
            line.append("│ ", style="grey42")
        line.append(f"{st.get('online_count', 0)} online  ", style="bold white")
        line.append("│ ", style="grey42")
        line.append("plugin ", style="grey58")
        line.append("live  " if st.get("plugin_live") else "quiet  ",
                    style="#4CE04C" if st.get("plugin_live") else "grey50")
        line.append("│ ", style="grey42")
        # the bot's vote state machine -> a plain-English label (IDLE just means "no vote")
        _state_label = {"IDLE": "in match", "VOTING": "MAP VOTE", "APPROVAL": "vote poll"}
        _st = st.get("state", "")
        line.append(_state_label.get(_st, _st), style="#E07CE0")

        # second line: vote / approval banner (or a quiet hint)
        line.append("\n")
        vote, appr = st.get("vote"), st.get("approval")
        if vote:
            line.append(f"VOTE ({vote.get('ends_in', 0)}s)  ", style="bold #4DD0E1")
            for opt in vote.get("options", []):
                line.append(f"{opt['key']}:", style="bold #4DD0E1")
                line.append(f"{opt['label']} ", style="white")
                line.append(f"[{opt['votes']}]  ", style="#6BE06B")
        elif appr:
            line.append(f"MAP-CHANGE POLL ({appr.get('ends_in', 0)}s)  ", style="bold #FFC857")
            line.append(f"{appr.get('yes', 0)}/{appr.get('players', 0)} yes, "
                        f"need {appr.get('need', 0)}", style="white")
        else:
            line.append("no vote in progress", style="grey42")
        status.update(line)

        self._active_map = self._atlas_for(st.get("mission"))   # for table grid-refs + map
        players = st.get("players", [])
        self._fill_table(players)
        self._refresh_map(st)

    @staticmethod
    def _atlas_for(mission):
        m = (mission or "").lower()
        if "heartland" in m or "escalation" in m:
            return _ATLAS.get("heartland")
        if "ignus" in m or "terminal" in m:
            return _ATLAS.get("ignus")
        return None
        self._update_known_names(players)

    def _update_known_names(self, players: list[dict]) -> None:
        """Keep the 'grant' autocomplete list current: online players (from the feed)
        plus everyone in ranks.json (so offline players can be completed too)."""
        names = {p["name"] for p in players if p.get("name")}
        try:
            m = os.path.getmtime(RANK_FILE)
            if m != self._ranks_mtime:
                self._ranks_mtime = m
                with open(RANK_FILE, encoding="utf-8") as f:
                    self._ranks_names = [r.get("name") for r in json.load(f).values() if r.get("name")]
        except (OSError, ValueError):
            pass
        names.update(self._ranks_names)
        self._known_names = sorted(names, key=str.lower)

    def _fill_table(self, players: list[dict]) -> None:
        # Skip the full clear+rebuild when nothing visible changed (this runs every 2s).
        # Order matters because of the '#' column, so the tuple-of-tuples preserves order.
        sig = tuple((p.get("sid"), p.get("name"), p.get("aircraft"), p.get("points"),
                     p.get("faction"), p.get("ingame_rank"), p.get("match_points"),
                     p.get("fresh"), p.get("rank_abbr"), p.get("rank_color"),
                     p.get("x"), p.get("z")) for p in players)
        if sig == self._table_sig:
            return
        table = self.query_one("#players", DataTable)
        try:                                     # remember where the user was
            prev_row = table.cursor_row
            prev_scroll = table.scroll_offset.y
        except Exception:                        # noqa: BLE001
            prev_row, prev_scroll = 0, 0
        self._players_by_sid = {}
        table.clear()
        for i, p in enumerate(players, 1):
            fresh = p.get("fresh")
            name = p.get("name", "?")
            plane = p.get("aircraft") or "—"
            pts = p.get("points", 0)
            sid = str(p.get("sid", ""))
            if sid:
                self._players_by_sid[sid] = p
            try:
                num = rank_index_for(bot.cycle_points(sid) if sid else pts) + 1
            except Exception:                        # noqa: BLE001
                num = rank_index_for(pts) + 1        # OFFCDT=1, PLTOFF=2, ... ACM=11
            rank_cell = Text(f"{num} {p.get('rank_abbr', '')} {pts:.0f}",
                             style=f"bold {p.get('rank_color', 'white')}")
            ig = p.get("ingame_rank")
            ig_cell = Text("—" if ig is None else str(ig),
                           style="white" if fresh else "grey42")
            mp_cell = Text(f"{p.get('match_points', 0):.1f}",
                           style="bold #6BE06B" if fresh else "grey50")
            px, pz = p.get("x"), p.get("z")
            if px is None or pz is None:
                pos_cell = Text("—", style="grey42")
            else:
                gr = grid_ref(px, pz, getattr(self, "_active_map", None)) or "?"
                pos_cell = Text(f"{gr} {round(px/1000)},{round(pz/1000)}",
                                style="#9FD0FF" if fresh else "grey50")
            fac = p.get("faction", "")
            table.add_row(
                Text(str(i), style="grey50"),
                Text(name, style="white" if fresh else "grey54"),
                Text(faction_short(fac), style=faction_color(fac)),
                Text(plane, style="#FFD27F" if plane != "—" else "grey42"),
                rank_cell,
                ig_cell,
                mp_cell,
                pos_cell,
                key=sid or None,
            )
        # restore the cursor + scroll so the periodic refresh doesn't yank the user back
        try:
            if table.row_count and 0 <= prev_row < table.row_count:
                table.move_cursor(row=prev_row, animate=False)
            if prev_scroll:
                table.scroll_to(y=prev_scroll, animate=False)
        except Exception:                        # noqa: BLE001
            pass
        self._table_sig = sig                    # only set on the rebuild path

    # ----- live ASCII map -----
    def _refresh_map(self, st: dict) -> None:
        try:
            mp = self.query_one("#map", Static)
        except Exception:                        # noqa: BLE001
            return
        data = getattr(self, "_active_map", None) or self._atlas_for(st.get("mission"))
        if not data:
            sig = ("none", st.get("mission"))
            if sig != getattr(self, "_map_sig", None):
                self._map_sig = sig
                msg = ("  no live map for this mission:\n  "
                       f"{st.get('mission', '?')}") if _ATLAS else \
                      "  map atlas missing - run  python build_map_atlas.py"
                mp.update(Text(msg, style="grey50"))
            return
        players = st.get("players", [])
        # dirty-check: only re-render when something on the map actually moved
        sig = (data["name"], tuple(sorted(
            (p.get("sid"), p.get("x"), p.get("z"), p.get("grounded"),
             p.get("faction"), p.get("aircraft"), p.get("fresh"))
            for p in players)))
        if sig == getattr(self, "_map_sig", None):
            return
        self._map_sig = sig
        try:
            mp.update(self._build_map(data, players))
        except Exception as e:                   # noqa: BLE001 - the map must never crash the UI
            mp.update(Text(f"  map render error: {e}", style="#FF5555"))

    @staticmethod
    def _map_cell(data, x, z):
        """world (x,z) -> (atlas_col 0..C-1, atlas_row 0..R-1) or None if off-map."""
        try:
            fx = (x - data["x0"]) / (data["x1"] - data["x0"])
            fz = (z - data["z0"]) / (data["z1"] - data["z0"])
            c = int(fx * data["cols"])
            r = int(fz * data["rows"])
            if 0 <= c < data["cols"] and 0 <= r < data["rows"]:
                return c, r
        except Exception:                        # noqa: BLE001
            pass
        return None

    @staticmethod
    def _plane_glyph(p) -> str:
        plane = (p.get("aircraft") or "").lower()
        if any(k in plane for k in ("ibis", "chicane", "sah-", "helo", "heli")):
            return "h"      # helicopter
        if any(k in plane for k in ("compass", "cricket", "t/a-", "transport", "tanker")):
            return "+"      # support / recon / transport
        return "▲"          # fighter / attacker

    @staticmethod
    def _legend_text() -> Text:
        """Fixed 2-line map key shown above the map (never scrolls)."""
        t = Text(no_wrap=True)
        t.append(" ", style="")
        t.append("  ", style=f"on {_LAND_BG}"); t.append(" land  ", style="grey70")
        t.append("  ", style=f"on {_SEA}"); t.append(" water  ", style="grey70")
        t.append("  ", style=f"on {_CONCRETE}"); t.append(" city/field  ", style="grey70")
        t.append(_BRIDGE_GLYPH, style=_BRIDGE_FG); t.append(" bridge  ", style="grey70")
        t.append("⌂", style=_BASE_STYLE); t.append(" base   ", style="grey70")
        t.append("┊", style=_GRIDX_STYLE); t.append(" grid (in-game refs)", style="grey62")
        t.append("\n ", style="")
        t.append(" ▲ ", style=f"bold white on {faction_color('Boscali')}")
        t.append(" Boscali   ", style="grey70")
        t.append(" ▲ ", style=f"bold white on {faction_color('Primeva')}")
        t.append(" Primeva     ", style="grey70")
        t.append("▲", style="grey78"); t.append(" jet   ", style="grey62")
        t.append("h", style="grey78"); t.append(" heli   ", style="grey62")
        t.append("+", style="grey78"); t.append(" support   ", style="grey62")
        t.append("✝ shot down / grounded", style="#FF7B7B")
        return t

    def _map_base(self, data):
        """Build (and cache per-mission) the STATIC retro map layer: green land /
        black water / grey concrete + the in-game quadrant grid (from the world
        grid model, so labels = real refs) + small base icons. Planes stamped per
        refresh."""
        key = data["name"]
        if getattr(self, "_mapbase_key", None) == key:
            return self._mapbase
        C, R = data["cols"], data["rows"]
        terr = data["terr"]
        x0, x1, z0, z1 = data["x0"], data["x1"], data["z0"], data["z1"]
        xmin, cell, znorth = data["xmin"], data["cell"], data["znorth"]
        colat = lambda x: (x - x0) / (x1 - x0) * C       # noqa: E731
        rowat = lambda z: (z - z0) / (z1 - z0) * R       # noqa: E731
        # quadrant grid lines + labels at TRUE in-game cell boundaries within view
        clo, chi = int((x0 - xmin) / cell) + 1, int((x1 - xmin) / cell) + 1
        vlines, col_labels = set(), []
        for c in range(clo, chi + 2):
            ax = colat(xmin + (c - 1) * cell)
            if 0 <= ax < C:
                vlines.add(int(round(ax)))
        for c in range(clo, chi + 1):
            ax = colat(xmin + (c - 0.5) * cell)
            if 0 <= ax < C:
                col_labels.append((int(ax), str(c)))
        ilo, ihi = int((znorth - z0) / cell), int((znorth - z1) / cell)
        hlines, rowlab = set(), {}
        for i in range(ilo, ihi + 2):
            ay = rowat(znorth - i * cell)
            if 0 <= ay < R:
                hlines.add(int(round(ay)))
        for i in range(ilo, ihi + 1):
            ay = rowat(znorth - (i + 0.5) * cell)
            if 0 <= ay < R:
                rowlab[int(ay)] = chr(97 + i)

        def bg_of(code):
            if code in _WATER or code == "B":
                return _SEA
            return _CONCRETE if code == "c" else _LAND_BG

        cells = [[None] * C for _ in range(R)]
        for r in range(R):
            trow = terr[r]
            row = cells[r]
            on_h = r in hlines
            for c in range(C):
                if trow[c] == "B":                       # causeway: thin line, not a block
                    row[c] = (_BRIDGE_GLYPH, f"{_BRIDGE_FG} on {_SEA}")
                    continue
                bg = bg_of(trow[c])
                if c in vlines or on_h:
                    if c in vlines and on_h:
                        row[c] = ("┼", f"bold {_GRIDX_STYLE} on {bg}")
                    else:
                        gfg = _GRID_SEA if bg == _SEA else _GRID_LAND
                        row[c] = (("┊" if c in vlines else "┈"), f"{gfg} on {bg}")
                elif bg == _SEA:
                    row[c] = (" ", f"on {_SEA}")
                else:
                    eg = _edge_glyph(terr, r, c, C, R)   # smooth coastlines / thin necks
                    row[c] = ((eg, f"{bg} on {_SEA}") if eg else (" ", f"on {bg}"))
        # small base icons (no labels). 'g' = grid ref, 'i' = image fraction.
        def snap_to_land(ac, ar):
            """nudge a base onto the nearest GREEN land cell so it never floats and
            never sits on water / a bridge / a concrete strip (its bg = the green
            landmass under it)."""
            green = lambda rr, cc: terr[rr][cc] not in _WATER and terr[rr][cc] not in "cB"
            if green(ar, ac):
                return ac, ar
            for rad in range(1, 8):
                for dr in range(-rad, rad + 1):
                    for dc in range(-rad, rad + 1):
                        if max(abs(dr), abs(dc)) != rad:
                            continue
                        nc, nr = ac + dc, ar + dr
                        if 0 <= nc < C and 0 <= nr < R and green(nr, nc):
                            return nc, nr
            return ac, ar
        for entry in data.get("bases", ()):
            kind = entry[1]
            snap = True
            if kind == "g":
                cell_xy = grid_to_cell(data, entry[2])
            elif kind == "w":                            # world coord (airfields) - sits on
                cell_xy = world_to_cell(data, entry[2], entry[3])  # its forced concrete tile,
                snap = False                             # so DON'T snap it onto green land
            else:
                cell_xy = (min(C - 1, max(0, int(entry[2] * C))),
                           min(R - 1, max(0, int(entry[3] * R))))
            if cell_xy:
                ac, ar = snap_to_land(*cell_xy) if snap else cell_xy
                # base icon sits on the SAME colour as the tile under it (grey runway / land)
                cells[ar][ac] = (_BASE_ICON, f"bold #FFD400 on {bg_of(terr[ar][ac])}")
        # header (col numbers) + per-row gutters (row letters)
        head = [" "] * C
        for ax, s in col_labels:
            cx = ax - len(s) // 2
            for k, chx in enumerate(s):
                if 0 <= cx + k < C:
                    head[cx + k] = chx
        head_str = "    " + "".join(head)
        guts = [(f" {rowlab[r]}  " if r in rowlab else "    ") for r in range(R)]
        self._mapbase_key = key
        self._mapbase = (cells, head_str, guts)
        return self._mapbase

    def _build_map(self, data, players) -> Text:
        cells_b, head, guts = self._map_base(data)
        C, R = data["cols"], data["rows"]
        cells = [row[:] for row in cells_b]          # cheap copy; stamp planes on top
        dead, seen, flying = [], {}, []
        # 1) grounded / shot-down players at their LAST known location (drawn first)
        for p in players:
            x, z = p.get("x"), p.get("z")
            if x is None or z is None:
                if p.get("fresh"):
                    dead.append(p.get("name", "?"))     # no position ever recorded
                continue
            cell = self._map_cell(data, x, z)
            if not cell:
                continue
            if p.get("grounded"):
                c, r = cell
                cells[r][c] = (_GROUND_ICON, _GROUND_STYLE)
            else:
                flying.append((cell, p))
        # 2) flying planes on top (a live blip wins a shared cell over a ✝)
        for cell, p in flying:
            c, r = cell
            n = seen.get((r, c), 0) + 1
            seen[(r, c)] = n
            col = faction_color(p.get("faction", ""))
            glyph = self._plane_glyph(p) if n == 1 else str(min(9, n))
            cells[r][c] = (glyph, f"bold white on {col}")     # radar blip
        out = Text(no_wrap=True)
        out.append(head + "\n", style=_LABEL_STYLE)
        for r in range(R):
            out.append(guts[r], style=_LABEL_STYLE)
            for (g, stx) in cells[r]:
                out.append(g, style=stx)
            out.append("\n")
        if dead:
            out.append("  ✝ down: ", style="bold #FF7B7B")
            out.append(", ".join(dead[:10]) + ("  …" if len(dead) > 10 else ""), style="grey62")
        return out

    # ----- click a player row -> per-player action menu -----
    @on(DataTable.RowSelected, "#players")
    def _on_player_selected(self, event: DataTable.RowSelected) -> None:
        sid = event.row_key.value if event.row_key else None
        p = self._players_by_sid.get(str(sid))
        if p:
            self.push_screen(PlayerActionScreen(p), self._handle_player_action)

    def _handle_player_action(self, result) -> None:
        if not result:
            return
        p = result.get("player", {})
        sid = str(p.get("sid", ""))
        name = p.get("name", "?")
        action = result.get("action")
        if action == "grant":
            self._queue_grant(sid or name, result.get("points", 0))
        elif action == "kick":
            if sid:
                self._send("kick-player", [sid], f"kick {name}")   # reversible (unkick); kick now
            else:
                self._respond(Text(f"can't kick {name}: no SteamID known", style="#FF7B7B"))
        elif action == "ban":
            if sid:
                self._dispatch("banlist-add", [sid], f"ban {name}", danger=True)   # serious -> confirm
            else:
                self._respond(Text(f"can't ban {name}: no SteamID known", style="#FF7B7B"))
        elif action == "copysid":
            self.copy_to_clipboard(sid)
            self.notify(f"copied SteamID for {name}", timeout=2)
        elif action == "move":
            if sid:
                self._queue_team("move", sid, result.get("faction", ""), name)
            else:
                self._respond(Text(f"can't move {name}: no SteamID known", style="#FF7B7B"))
        elif action == "spec":
            if sid:
                self._queue_team("spec", sid, "", name)
            else:
                self._respond(Text(f"can't move {name}: no SteamID known", style="#FF7B7B"))

    # ----- key actions -----
    def action_clear_panes(self) -> None:
        for wid in ("#console", "#activity", "#response"):
            self.query_one(wid, RichLog).clear()

    def action_toggle_filter(self) -> None:
        self._filter = not self._filter
        self._respond(Text(f"[INFO] console filter {'ON (noise summarised)' if self._filter else 'OFF (raw - every line)'}",
                           style="#4DD0E1"))

    # ----- command input -----
    def _respond(self, renderable) -> None:
        self.query_one("#response", RichLog).write(renderable)

    @on(Input.Submitted, "#cmd")
    def _on_command(self, event: Input.Submitted) -> None:
        raw = event.value.strip().lstrip("﻿")
        event.input.value = ""
        if not raw:
            return
        self._respond(Text(f"› {raw}", style="bold grey70"))

        # resolve a pending destructive confirmation first
        if self._pending:
            wire, args, label = self._pending
            self._pending = None
            if raw.lower() in ("confirm", "yes", "y"):
                self._respond(Text(f"running: {label}", style="#FFC857"))
                self._send(wire, args, label)
            else:
                self._respond(Text("cancelled.", style="grey50"))
            return

        head, _, rest = raw.partition(" ")
        cmd, rest = head.lower(), rest.strip()

        # local-only commands
        if cmd in ("quit", "exit", "q"):
            self.exit()
            return
        if cmd in ("help", "?", "commands"):
            self._show_help()
            return
        if cmd in ("cls", "clear"):
            self.action_clear_panes()
            return
        if cmd == "ranks":
            self._show_ranks()
            return
        if cmd in ("leaderboard", "lb", "top"):
            self._show_leaderboard()
            return
        if cmd == "grant":
            # grant <player name or steamID> <points>  (name may contain spaces; -N removes)
            who, _, amt = rest.rpartition(" ")
            who, amt = who.strip(), amt.strip()
            if not who or not amt:
                self._respond(Text("usage: grant <player name or steamID> <points>   "
                                   "e.g.  grant Tomo 100   (use -50 to remove)", style="grey50"))
                return
            try:
                pts = float(amt)
            except ValueError:
                self._respond(Text(f"'{amt}' is not a number.  usage: grant <player> <points>", style="#FF7B7B"))
                return
            self._queue_grant(who, pts)
            return

        # local helpers that still hit the relay
        if cmd == "rankpreview":
            self._send_rankpreview()
            return
        if cmd == "endmission":
            self._pending = ("set-time-remaining", ["5"], "force the current mission to end")
            self._respond(Text("⚠ force-end the mission? type 'confirm' to run, anything else cancels.",
                               style="bold #FFC857"))
            return
        if cmd == "say":
            if not rest:
                self._respond(Text("usage: say <message>", style="grey50"))
            else:
                # Admin broadcast: auto-prefix [Admin] and render the whole line orange.
                # send-chat-message -> RpcServerMessage, which renders Unity rich text.
                wire_msg = f"<color=#FF8C00>[Admin] {rest}</color>"
                self._send("send-chat-message", [wire_msg], f"say [Admin] {rest}")
            return
        if cmd in ("move", "team", "join", "spec", "spectate", "balance"):
            # team control -> queued for the bot to relay to the NukeStats plugin (needs the
            # v0.6.0 plugin loaded; takes effect on a live PvP match).
            if cmd == "balance":
                self._queue_team("balance")
                return
            toks = rest.split()
            if cmd in ("spec", "spectate"):
                if not toks:
                    self._respond(Text("usage: spec <player>", style="grey50")); return
                sid, info = self._resolve_player(rest.strip())
                if not sid:
                    self._respond(Text(info, style="#FF7B7B")); return
                self._queue_team("spec", sid, "", info); return
            if len(toks) < 2:
                self._respond(Text(f"usage: {cmd} <player> <boscali|primeva>", style="grey50")); return
            faction = toks[-1].lower()
            if faction not in ("boscali", "primeva", "bdf", "pala", "bosc", "prim"):
                self._respond(Text(f"unknown faction '{faction}'  -  use boscali / primeva", style="#FF7B7B")); return
            sid, info = self._resolve_player(" ".join(toks[:-1]))
            if not sid:
                self._respond(Text(info, style="#FF7B7B")); return
            self._queue_team("move" if cmd in ("move", "team") else "join", sid, faction, info); return
        if cmd == "nextmap":
            # name-only: derive the "User" group + a default 2h max-time, fuzzy-match the name
            # (Tab to autocomplete the full mission). Bypasses the quote-the-name dance.
            if not rest.strip():
                self._respond(Text("usage: nextmap <mission name>   (Tab to autocomplete)", style="grey50")); return
            full = self._resolve_mission(rest.strip())
            if not full:
                self._respond(Text(f"no mission matches '{rest.strip()}'  -  Tab to autocomplete", style="#FF7B7B")); return
            self._dispatch("set-next-mission", ["User", full, "7200"], f"nextmap {full}", danger=False)
            return
        if cmd == "raw":
            toks = rest.split()
            if not toks:
                self._respond(Text("usage: raw <command-name> <arg> ...", style="grey50"))
                return
            danger = any(e[1] == toks[0] and e[4] for e in CENTRE_SERVER_CMDS)
            self._dispatch(toks[0], toks[1:], rest, danger)
            return

        # known server-command alias?
        entry = next((e for e in CENTRE_SERVER_CMDS if e[0] == cmd), None) \
            or next((e for e in CENTRE_SERVER_CMDS if e[1] == cmd), None)
        if not entry:
            self._respond(Text(f"unknown command '{cmd}'  -  type 'help'", style="#FF7B7B"))
            return
        alias, wire, ahint, desc, danger = entry
        self._dispatch(wire, rest.split() if rest else [], f"{alias} {rest}".strip(), danger)

    def _dispatch(self, wire: str, args: list[str], label: str, danger: bool) -> None:
        if danger:
            self._pending = (wire, args, label)
            self._respond(Text(f"⚠ '{label}' changes the server. type 'confirm' to run, anything else cancels.",
                               style="bold #FFC857"))
        else:
            self._send(wire, args, label)

    # ----- relay I/O (off the UI thread) -----
    def _send(self, wire: str, args: list[str], label: str) -> None:
        self.run_worker(lambda: self._send_blocking(wire, args, label), thread=True)

    def _send_blocking(self, wire: str, args: list[str], label: str) -> None:
        try:
            with self._rc_lock:
                code, resp = self._rc.send(wire, *args, return_code=True)
        except Exception as e:                       # noqa: BLE001
            self.call_from_thread(self._respond, Text(f"send failed: {e}", style="#FF5555"))
            return
        self.call_from_thread(self._post_response, code, resp)

    def _post_response(self, code, resp) -> None:
        if code is None:
            self._respond(Text("no response - server/relay unreachable", style="#FF5555"))
            return
        name = STATUS_CODES.get(code, "?")
        ok = code == 2000
        self._respond(Text(f"{'OK' if ok else 'ERROR'} ({code} {name})",
                           style="#6BE06B" if ok else "#FF5555"))
        if isinstance(resp, dict):
            self._respond(Text(json.dumps(resp, indent=2, ensure_ascii=False)[:3000], style="grey62"))
        elif isinstance(resp, str) and resp.strip():
            self._respond(Text(resp[:1500], style="grey62"))

    def _send_rankpreview(self) -> None:
        def work():
            def chat(text):                       # raise on relay failure so we don't falsely claim success
                code, _ = self._rc.send("send-chat-message", text, return_code=True)
                if code is None:
                    raise ConnectionError("relay unreachable mid-rankpreview")
            try:
                with self._rc_lock:
                    chat("<color=#FFFF00>=== SERVER RANKS (points needed) ===</color>")
                    row = []
                    for i, (thr, nm, abbr, color) in enumerate(RANKS, 1):
                        row.append(f"<color={color}>{i}. {nm} [{abbr}] {thr}</color>")
                        if len(row) == 4:
                            chat("   ".join(row)); row = []
                    if row:
                        chat("   ".join(row))
                self.call_from_thread(self._respond, Text("posted the rank ladder to in-game chat", style="#6BE06B"))
            except Exception as e:                    # noqa: BLE001
                self.call_from_thread(self._respond, Text(f"rankpreview failed: {e}", style="#FF5555"))
        self.run_worker(work, thread=True)

    def _show_help(self) -> None:
        r = self.query_one("#response", RichLog)
        r.write(Text("SERVER COMMANDS (sent live to the game):", style="bold white"))
        for alias, wire, ahint, desc, danger in CENTRE_SERVER_CMDS:
            hint, d = CC_HINT_OVERRIDE.get(alias, (ahint, desc))
            t = Text("  ")
            t.append("! " if danger else "  ", style="#FF7B7B")
            t.append(f"{alias:<12}", style="bold #6BE06B")
            t.append(f"{hint:<26}", style="grey50")
            t.append(d, style="grey70")
            r.write(t)
        r.write(Text("LOCAL: grant <player> <pts> · leaderboard · ranks · rankpreview · endmission · raw <cmd> · help · cls · quit",
                     style="#4DD0E1"))
        r.write(Text("  grant adds rank points to a player (offline or online) and re-ranks them - e.g.  grant Tomo 100  (or -50 to remove)",
                     style="grey58"))
        r.write(Text("  CLICK a player row for Grant / Kick / Ban / Copy-SteamID  ·  CLICK any console or activity line to copy it to the clipboard",
                     style="grey58"))

    def _queue_grant(self, who: str, pts: float) -> None:
        """Queue a point grant for the bot to apply. The bot owns ranks.json (in-memory +
        periodic saves), so the command centre must NOT write it directly - it appends the
        request here and the bot does every follow-on update (save, chat-tag, rank-up, ledger)."""
        rec = {"action": "grant", "query": who, "points": pts, "ts": time.time()}
        try:
            with open(ADMIN_CMD_FILE, "a", encoding="utf-8") as f:
                f.write(json.dumps(rec) + "\n")
            self._respond(Text(f"queued: {pts:+g} pts to '{who}'  -  watch the activity feed for the bot's confirmation",
                               style="#6BE06B"))
        except OSError as e:
            self._respond(Text(f"could not queue grant: {e}", style="#FF5555"))

    def _resolve_player(self, query: str):
        """(sid, name) for a player matching `query` (SteamID or name substring), or (None, msg)."""
        q = (query or "").strip().lower()
        if not q:
            return None, "name a player"
        if q.isdigit() and q in self._players_by_sid:
            return q, self._players_by_sid[q].get("name", q)
        hits = [(sid, p) for sid, p in self._players_by_sid.items()
                if q in str(p.get("name", "")).lower()]
        if not hits:
            return None, f"no player matching '{query}'"
        if len(hits) > 1:
            names = ", ".join(p.get("name", "?") for _, p in hits)
            return None, f"ambiguous '{query}': {names}  -  be more specific"
        return hits[0][0], hits[0][1].get("name", hits[0][0])

    def _resolve_mission(self, query: str):
        """Full mission name for `query` (exact, else prefix, else substring), or None."""
        q = (query or "").strip().lower()
        if not q:
            return None
        for m in self._missions:
            if m.lower() == q:
                return m
        for m in self._missions:
            if m.lower().startswith(q):
                return m
        for m in self._missions:
            if q in m.lower():
                return m
        return None

    def _queue_team(self, verb: str, sid: str = "", faction: str = "", name: str = "") -> None:
        """Queue a team action (move/spec/join/balance) for the bot to relay to the plugin.
        Like grants, the command centre never touches the server directly - the bot owns the
        SFTP channel and uploads it to plugin_commands.txt for the plugin to execute."""
        rec = {"action": "team", "verb": verb, "sid": sid, "faction": faction, "ts": time.time()}
        try:
            with open(ADMIN_CMD_FILE, "a", encoding="utf-8") as f:
                f.write(json.dumps(rec) + "\n")
        except OSError as e:
            self._respond(Text(f"could not queue team command: {e}", style="#FF5555"))
            return
        who = name or sid or "?"
        if verb == "balance":
            self._respond(Text("queued: balance pass  -  needs a live PvP match; watch the activity feed",
                               style="#6BE06B"))
        elif verb in ("spec", "spectate"):
            self._respond(Text(f"queued: move {who} to spectate (no team)", style="#6BE06B"))
        else:
            self._respond(Text(f"queued: move {who} -> {faction}", style="#6BE06B"))

    def _show_leaderboard(self) -> None:
        """Top 5 by points (server rank) - same as in-game !leaderboard."""
        r = self.query_one("#response", RichLog)
        try:
            with open(RANK_FILE, encoding="utf-8") as f:
                data = json.load(f)
        except (OSError, ValueError):
            r.write(Text("could not read ranks.json", style="#FF5555"))
            return
        # --- top 5 by points (server rank) ---
        pts_board = sorted(((s, rc) for s, rc in data.items() if rc.get("points", 0) > 0),
                           key=lambda kv: -kv[1].get("points", 0))[:5]
        r.write(Text("  TOP 5 - POINTS (server rank)", style="bold white"))
        if pts_board:
            for i, (sid, rec) in enumerate(pts_board, 1):
                pts = rec.get("points", 0)
                _, _rname, abbr, color = RANKS[rank_index_for(bot.cycle_points(sid))]
                t = Text(f"  {i}. ", style="grey50")
                t.append(f"{rec.get('name', sid):<20.20}", style="white")
                t.append(f"{pts:>9.1f} ", style="#6BE06B")
                t.append(f"[{abbr}]", style=f"bold {color}")
                r.write(t)
        else:
            r.write(Text("  no ranked pilots yet", style="grey42"))

    def _show_ranks(self) -> None:
        r = self.query_one("#response", RichLog)
        try:
            with open(RANK_FILE, encoding="utf-8") as f:
                data = json.load(f)
        except (OSError, ValueError):
            r.write(Text("could not read ranks.json", style="#FF5555"))
            return
        board = sorted(data.items(), key=lambda kv: (-kv[1].get("points", 0),
                                                      kv[1].get("name", "").lower()))
        r.write(Text(f"SERVER RANKS - {len(board)} pilots (best first):", style="bold white"))
        for i, (sid, rec) in enumerate(board[:25], 1):
            pts = rec.get("points", 0)
            _, rname, abbr, color = RANKS[rank_index_for(bot.cycle_points(sid))]
            t = Text(f"  {i:>2}. ", style="grey50")
            t.append(f"{rec.get('name', sid):<22.22}", style="white")
            t.append(f"{pts:>9.1f}  ", style="#6BE06B")
            t.append(f"[{abbr}] {rname}", style=f"bold {color}")
            r.write(t)
        if len(board) > 25:
            r.write(Text(f"  ... and {len(board) - 25} more", style="grey42"))


if __name__ == "__main__":
    CommandCentre().run()
