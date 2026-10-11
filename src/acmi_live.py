#!/usr/bin/env python3
"""Live NOBlackBox feed for the web command centre's map (1.9.4, owner 2026-10-04: "the noblackbox live map blend").

NOBlackBox writes the running mission's Tacview recording on the game host (tacview/<UTC start>_<mission>_<map>.acmi)
while the mission plays; the text reaches the file in small pieces, so it is only a second or two behind. This follows
that file READ-ONLY over SFTP (its own persistent session, separate from the bot's), parses each new line and keeps the
current state of every recorded object. The panel draws what the plugin feed does not show: AI aircraft and
helicopters with their types, ground units, ships, missiles and bombs in flight, gun rounds, flares, explosions and
nuke shockwaves, ejected pilots and destructions. Players stay on the plugin feed (names, ranks, click actions); their
recorded aircraft carry a Pilot property and the panel skips those.

Never writes to the host. Never prints hosts, credentials or Steam IDs (NOBlackBox records none: RecordSteamID=false).

  python acmi_live.py <recording.acmi | recording.zip.acmi>   offline self-test: replay a local file, print a summary
"""
import collections
import io
import os
import re
import stat
import sys
import threading
import time
import zipfile

REMOTE_DIR = "tacview"
POLL_S = 1.0                 # read new bytes this often once caught up
LIST_S = 5.0                 # look for a newer recording this often
ACTIVE_S = 180.0             # a raw .acmi untouched this long is not the running mission (a killed server leaves one)
CHUNK = 2 * 1024 * 1024      # bytes per read while catching up
SAMPLES = 12                 # positions kept per object (enough for the panel's delayed playback)
RM_KEEP_S = 120.0            # removals remembered this long (recording time) for clients catching up
GONE_KEEP_S = 15.0           # a removed explosion stays deliverable this long (it often lives under one read)
LIVE_S = 20.0                # no new data for this long = not live (mission ended, recorder stopped)
OFFSET_WINDOW_S = 60.0       # recording-time -> wall-clock offset = the smallest lag seen over this window

_SPLIT = re.compile(r"(?<!\\),")
_DESTROYED = "Has been destroyed."


def kind_of(type_str):
    """ACMI Type tags -> one-letter kind the panel draws ('' = not drawn)."""
    tags = set(t.strip().lower() for t in str(type_str or "").split("+"))
    if not tags or tags == {""}:
        return ""
    if "parachutist" in tags or "human" in tags:
        return "e"                       # ejected pilot
    if "explosion" in tags:
        return "x"                       # explosion / nuke shockwave (Radius)
    if "flare" in tags or "decoy" in tags or "chaff" in tags:
        return "f"
    if "shell" in tags or "projectile" in tags:
        return "r" if "misc" in tags else "m"   # Misc+Projectile+Shell = gun round; Projectile+Shell = guided shell
    if "missile" in tags or "rocket" in tags or "torpedo" in tags:
        return "m"
    if "bomb" in tags:
        return "b"
    if "rotorcraft" in tags:
        return "h"
    if "air" in tags:
        return "a"
    if "sea" in tags or "watercraft" in tags:
        return "s"
    if "static" in tags or "building" in tags:
        return "B"                       # buildings: only a destruction is drawn
    if "antiaircraft" in tags:
        return "d"                       # air defence vehicle
    if "ground" in tags:
        return "g"
    return ""


class AcmiState:
    """Objects of one recording. Fed raw text lines; answers delta queries. Thread-safe."""

    def __init__(self):
        self.lock = threading.Lock()
        self.reset("")

    def reset(self, name):
        self.file = name
        self.objs = {}                   # id -> dict
        self.gone = {}                   # id -> dict: explosions removed in the last GONE_KEEP_S (see _remove)
        self.rm = collections.deque()    # (seq, id, t_rec)
        self.seq = 0
        self.min_seq = 1                 # a client older than this needs a full snapshot
        self.t = 0.0                     # current recording time (seconds from the recording start)
        self.header = {}
        self.pending = ""                # an escaped end-of-line continues on the next line

    # ---------------------------------------------------------------------------------------- parsing
    def feed(self, lines):
        with self.lock:
            for line in lines:
                self._line(line)

    def _line(self, line):
        if self.pending:
            line = self.pending + line
            self.pending = ""
        if line.endswith("\\") and not line.endswith("\\\\"):
            self.pending = line[:-1] + " "
            return
        if not line:
            return
        c = line[0]
        if c == "#":
            try:
                self.t = float(line[1:])
            except ValueError:
                pass
            return
        if c == "-":
            self._remove(line[1:].strip())
            return
        i = line.find(",")
        if i <= 0:
            return                       # FileType=/FileVersion= header lines, junk
        oid, rest = line[:i], line[i + 1:]
        if oid == "0":
            self._global(rest)
            return
        o = self.objs.get(oid)
        if o is None:
            o = self.gone.get(oid)       # a removed explosion still takes its late Radius lines (NOBlackBox writes them after)
        if o is not None and rest.startswith("T=") and "," not in rest:
            self._pos(o, rest[2:])       # fast path: a plain position update
            return
        props = {}
        for part in _SPLIT.split(rest):
            k, eq, v = part.partition("=")
            if eq:
                props[k] = v.replace("\\,", ",")
        if o is None:
            if not props.get("Type"):
                return                   # only a declaration creates an object (NOBlackBox sends a late Radius after
                                         # an explosion's removal; that must not resurrect it as an empty object)
            o = self.objs[oid] = {"i": oid, "k": "", "n": "", "c": "", "p": "", "pl": 0, "v": 1, "d": None, "r": None,
                                  "rt": None, "g": 0,
                                  "x": None, "z": None, "h": None, "s": collections.deque(maxlen=SAMPLES),
                                  "q": 0, "mq": 0}
        meta = False
        ty = props.get("Type")
        if ty:                           # "Type=" (empty) is sent with Visible=0 when a unit is disabled: keep the kind
            k = kind_of(ty)
            if k != o["k"]:
                o["k"] = k
                meta = True
        for key, field in (("Name", "n"), ("Coalition", "c"), ("Pilot", "p")):
            if key in props and props[key] != o[field]:
                o[field] = props[key]
                meta = True
        # a PLAYER's aircraft: our NOBlackBox build writes its callsign as "<code> (<name>) <id>" ("(Pilot)" until the
        # name resolves) and a Pilot property; AI callsigns have no brackets. The panel draws players from its own feed.
        if not o["pl"] and (o["p"] or (o["k"] in ("a", "h") and "(" in props.get("CallSign", ""))):
            o["pl"] = 1
            meta = True
        if "Visible" in props:
            vis = 0 if props["Visible"].strip() in ("0", "0.0") else 1
            if vis != o["v"]:
                o["v"] = vis
                meta = True
        if "Radius" in props:
            try:
                r = float(props["Radius"])
                if r != o["r"]:
                    o["r"] = r
                    o["rt"] = self.t             # when it reached this radius: the panel grows the ring on that clock
                    meta = True
            except ValueError:
                pass
        if "AGL" in props and o["k"] in ("a", "h"):
            try:
                g = 1 if float(props["AGL"]) < 2.0 else 0   # the video's AIRBORNE_AGL: below 2 m = on the ground
            except ValueError:
                g = o["g"]
            if g != o["g"]:
                o["g"] = g
                meta = True
        if meta:
            self.seq += 1
            o["mq"] = o["q"] = self.seq
        if "T" in props:
            self._pos(o, props["T"])

    def _pos(self, o, tv):
        f = tv.split("|")
        n = len(f)
        x = z = h = None
        if n >= 9:
            x, z, h = f[6], f[7], f[8]
        elif n == 5:
            x, z = f[3], f[4]
        try:
            nx = float(x) if x else o["x"]
            nz = float(z) if z else o["z"]
            nh = float(h) if h else o["h"]
        except ValueError:
            return
        if nx is None or nz is None:
            return
        if nx == o["x"] and nz == o["z"] and nh == o["h"]:
            return
        o["x"], o["z"], o["h"] = nx, nz, nh
        self.seq += 1
        o["q"] = self.seq
        o["s"].append((self.seq, self.t, nx, nz, nh))

    def _remove(self, oid):
        o = self.objs.pop(oid, None)
        if o is None:
            return
        # An explosion usually lives under a second, so it is declared AND removed inside one read of the file and
        # was never delivered (owner 2026-10-08: "only seeing the explosions sometimes"). Keep it a little while so
        # the next delta still carries it; its removal goes out in the same delta and the page fades it.
        if o["k"] == "x" and o["x"] is not None:
            o["gone_t"] = self.t
            self.gone[oid] = o
        for g in [g for g, go in self.gone.items() if self.t - go["gone_t"] > GONE_KEEP_S]:
            del self.gone[g]
        self.seq += 1
        self.rm.append((self.seq, oid, self.t))
        while self.rm and self.t - self.rm[0][2] > RM_KEEP_S:
            self.min_seq = max(self.min_seq, self.rm.popleft()[0] + 1)

    def _global(self, rest):
        if rest.startswith("Event="):
            ev = rest[6:].split("|")
            if len(ev) >= 3 and ev[0] == "Message" and ev[2].strip() == _DESTROYED:
                o = self.objs.get(ev[1].strip())
                if o is not None and o["d"] is None:
                    o["d"] = self.t
                    self.seq += 1
                    o["mq"] = o["q"] = self.seq
            return
        for part in _SPLIT.split(rest):
            k, eq, v = part.partition("=")
            if eq and k in ("ReferenceTime", "RecordingTime", "Title", "MapId"):
                self.header[k] = v

    # ---------------------------------------------------------------------------------------- queries
    def delta(self, since, offset):
        """Objects changed after `since` (sequence number) with their new positions, wall-clock stamped with
        `offset` (wall = recording time + offset). since <= 0 or too old -> a full snapshot."""
        with self.lock:
            full = since <= 0 or since < self.min_seq - 1 or since > self.seq
            out = []
            for o in list(self.objs.values()) + list(self.gone.values()):
                k = o["k"]
                if not k or o["x"] is None:
                    continue
                if k == "B" and o["d"] is None:
                    continue             # standing buildings are not drawn
                if full and o["d"] is not None and self.t - o["d"] > 300.0:
                    continue             # destroyed longer ago than the panel's X fade (the video's 300 s)
                if not full and o["q"] <= since:
                    continue
                if full:
                    samples = list(o["s"])[-3:] if o["s"] else []
                else:
                    samples = [s for s in o["s"] if s[0] > since]
                    if not samples and o["mq"] <= since:
                        continue
                if not samples:
                    samples = [(0, o["s"][-1][1] if o["s"] else self.t, o["x"], o["z"], o["h"])]
                row = {"i": o["i"], "k": k, "c": o["c"], "v": o["v"],
                       "s": [[round(t + offset, 2), round(x), round(z), (None if h is None else round(h))]
                             for _, t, x, z, h in samples]}
                if o["n"]:
                    row["n"] = o["n"]
                if o["p"]:
                    row["p"] = o["p"]
                if o["pl"]:
                    row["pl"] = 1
                if o["d"] is not None:
                    row["d"] = round(o["d"] + offset, 2)
                if o["r"] is not None:
                    row["r"] = round(o["r"])
                    row["rt"] = round((o["rt"] if o["rt"] is not None else self.t) + offset, 2)
                if o["g"]:
                    row["g"] = 1
                out.append(row)
            rm = [] if full else [[oid, round(t + offset, 2)] for q, oid, t in self.rm if q > since]
            return {"full": full, "seq": self.seq, "objs": out, "rm": rm}

    def counts(self):
        with self.lock:
            return collections.Counter(o["k"] or "?" for o in self.objs.values())


class AcmiFollower:
    """Follows the newest recording on the host. connect() -> (ssh, sftp) opens a fresh session."""

    def __init__(self, connect, label=""):
        self.connect = connect
        self.label = label
        self.state = AcmiState()
        self._ssh = self._sftp = None
        self._off = 0
        self._rest = b""
        self._next_list = 0.0
        self.caught_up = False
        self.last_data = 0.0             # wall time new data last arrived
        self.error = ""
        self._lags = collections.deque()  # (wall, wall - recording time) observed while caught up
        self.offset = None
        self._thread = None

    def start(self):
        if self._thread is None:
            self._thread = threading.Thread(target=self._run, name="acmi-live", daemon=True)
            self._thread.start()

    # ---------------------------------------------------------------------------------------- loop
    def _run(self):
        backoff = 5.0
        while True:
            try:
                if self._sftp is None:
                    self._ssh, self._sftp = self.connect()
                    try:
                        self._sftp.get_channel().settimeout(25)
                    except Exception:                    # noqa: BLE001
                        pass
                self._step()
                self.error = ""
                backoff = 5.0
                time.sleep(POLL_S if self.caught_up else 0.05)
            except Exception as e:                       # noqa: BLE001 - never die; reconnect with backoff
                self.error = type(e).__name__
                self._close()
                time.sleep(backoff)
                backoff = min(60.0, backoff * 2)

    def _close(self):
        try:
            if self._ssh is not None:
                self._ssh.close()
        except Exception:                                # noqa: BLE001
            pass
        self._ssh = self._sftp = None

    def _step(self):
        now = time.time()
        if now >= self._next_list or not self.state.file:
            self._next_list = now + LIST_S
            newest = None
            for a in self._sftp.listdir_attr(REMOTE_DIR):
                nm = a.filename
                if not nm.endswith(".acmi") or nm.endswith(".zip.acmi") or not stat.S_ISREG(a.st_mode or 0):
                    continue
                if newest is None or (a.st_mtime or 0) > (newest.st_mtime or 0):
                    newest = a
            active = newest is not None and now - (newest.st_mtime or 0) < ACTIVE_S
            name = newest.filename if active else ""
            if name != self.state.file:
                with self.state.lock:
                    self.state.reset(name)
                self._off, self._rest = 0, b""
                self.caught_up = False
                self._lags.clear()
                self.offset = None
            if not name:
                self.caught_up = True
                return
        path = REMOTE_DIR + "/" + self.state.file
        size = self._sftp.stat(path).st_size
        if size < self._off:                             # replaced/truncated: start this file again
            with self.state.lock:
                self.state.reset(self.state.file)
            self._off, self._rest = 0, b""
            self.caught_up = False
        if size == self._off:
            self.caught_up = True
            return
        want = min(CHUNK, size - self._off)
        with self._sftp.open(path, "rb") as f:
            f.seek(self._off)
            if want > 65536:
                f.prefetch(self._off + want)             # pipelined reads: catching up on a long recording is quick
            data = f.read(want)
        if not data:
            return
        self._off += len(data)
        buf = self._rest + data
        cut = buf.rfind(b"\n")
        if cut < 0:
            self._rest = buf
            return
        self._rest = buf[cut + 1:]
        lines = buf[:cut].decode("utf-8", "replace").replace("\r", "").split("\n")
        self.state.feed(lines)
        self.last_data = time.time()
        self.caught_up = self._off >= size
        if self.caught_up:
            self._note_lag(self.last_data)

    def _note_lag(self, wall):
        lag = wall - self.state.t
        self._lags.append((wall, lag))
        while self._lags and wall - self._lags[0][0] > OFFSET_WINDOW_S:
            self._lags.popleft()
        self.offset = min(v for _, v in self._lags)

    # ---------------------------------------------------------------------------------------- panel
    def snapshot(self, since):
        live = bool(self.state.file) and self.caught_up and self.offset is not None and \
            time.time() - self.last_data < LIVE_S
        out = {"live": live, "file": self.state.file, "err": self.error, "read": self._off}
        if live:
            out.update(self.state.delta(int(since or 0), self.offset))
            out["lag"] = round(time.time() - (self.state.t + self.offset), 2)
        return out


# -------------------------------------------------------------------------------------------- self-test
def _selftest(path):
    st = AcmiState()
    st.reset(os.path.basename(path))
    t0 = time.time()
    if path.endswith(".zip.acmi"):
        z = zipfile.ZipFile(path)
        fh = io.TextIOWrapper(z.open(z.namelist()[0]), encoding="utf-8-sig", errors="replace")
    else:
        fh = open(path, encoding="utf-8-sig", errors="replace")
    n = 0
    batch = []
    for line in fh:
        batch.append(line.rstrip("\r\n"))
        if len(batch) >= 5000:
            st.feed(batch)
            n += len(batch)
            batch = []
    st.feed(batch)
    n += len(batch)
    dt = time.time() - t0
    full = st.delta(0, 0.0)
    print("lines %d in %.1f s (%.0f lines/s); recording time %.0f s; seq %d" % (n, dt, n / max(dt, 1e-6), st.t, st.seq))
    print("objects alive at the end by kind:", dict(st.counts()))
    kinds = collections.Counter(o["k"] for o in full["objs"])
    print("full snapshot: %d objects %s, %d bytes as JSON" % (len(full["objs"]), dict(kinds),
                                                               len(__import__("json").dumps(full))))
    mid = st.seq - 2000
    d = st.delta(mid, 0.0)
    print("delta over the last 2000 changes: %d objects, %d removals, full=%s" % (len(d["objs"]), len(d["rm"]), d["full"]))
    print("header:", {k: v for k, v in st.header.items() if k != "Title"})


if __name__ == "__main__":
    if len(sys.argv) > 1:
        _selftest(sys.argv[1])
    else:
        print(__doc__)
