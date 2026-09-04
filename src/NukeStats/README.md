# NukeStats — server-side stats sensor for Nuclear Option

A tiny BepInEx plugin that runs **inside the dedicated-server process** and emits the
real per-player score (which the server computes but never exposes over its
remote-command/log interface) as `[NOSTATS] {json}` lines on stdout. The existing
Python bot (`no_mapvote_bot.py`) already tails `/logs/console.log` over SFTP, so it
picks these up with no new network channel. Plugin = sensor; Python bot = brain.

## What it does
1. **Stats sensor** — emits `[NOSTATS] {json}` per player (10s snapshot + on every score change):
   ```
   [NOSTATS] {"t":"snap","id":"7656…","n":"SomePilot","f":"Boscali","s":1340,"rk":2,"tk":0}
   ```
   `s`=PlayerScore (this match), `rk`=PlayerRank, `tk`=Teamkills.
2. **End-of-game awards** — on `FactionHQ.DeclareEndGame("Victory")` it reads the winning
   faction authoritatively (the winner's own HQ declares it — no faction-0 guessing, which
   is what fixes the "won but showed defeat" bug), then emits:
   - `{"t":"win","f":"Boscali"}` — the bot announces the win + tallies W/L.
   - `{"t":"award","id":…,"pts":200,"reason":"win"}` for each player on the winning side, and
     placement bonuses `+FirstPlace/+SecondPlace/+ThirdPlace` (default 500/250/100) to the
     top-3 by PlayerScore. The bot adds these to `ranks.json`.
   - `{"t":"end"}` — match boundary.
3. **Chat reformat (Option A)** — rewrites player chat as `[Name - Rank] message` in the
   player's rank colour, by rerouting it through a server message (the normal path renders
   `Name:`/faction-colour client-side and strips rich text). Rank label+colour come from
   `plugin_ranks.txt`, which the bot writes to the container automatically (every 120s when
   `USE_PLUGIN_SCORE=True`).

## Tunables (BepInEx/config/anz.nukestats.cfg, no rebuild needed)
`Chat.Reformat` (true), `Scoring.WinPoints` (200), `FirstPlace`/`SecondPlace`/`ThirdPlace`
(500/250/100), `Stats.SnapshotSeconds` (10).

## Known tradeoffs of the chat reroute (Option A)
Because chat is rerouted as a *server message* to control format+colour:
- **Per-client mute/block is bypassed** (those are client-side on the normal path). On a
  small community server this is usually fine; use the bot's kick/ban if needed. Set
  `Chat.Reformat=false` to revert to native chat.
- **Server rate-limit is bypassed**; the plugin applies a light 0.75s/player anti-spam drop.
- Exact on-screen look (server-message styling) is confirmed at deploy.

---

## Build (needs the .NET SDK — not installed on the bot PC yet)

1. Install the **.NET SDK** (8.0+) from https://dotnet.microsoft.com/download (any machine).
2. Create a `libs/` folder next to `NukeStats.csproj` and put these DLLs in it:
   - From the server (pull with the bot, e.g.
     `run.bat --get "NuclearOptionServer_Data/Managed/Assembly-CSharp.dll" "NukeStats/libs/Assembly-CSharp.dll"`):
     `Assembly-CSharp.dll`, `Assembly-CSharp-firstpass.dll`, `UnityEngine.dll`,
     `UnityEngine.CoreModule.dll`, `Mirage.dll`
   - From the BepInEx pack's `BepInEx/core/`: `BepInEx.dll`, `0Harmony.dll`
3. `dotnet build -c Release` → produces `bin/Release/NukeStats.dll`.

### Build status (verified 2026-06-21)
Builds clean (`0 Error(s)`) against the DLLs in `libs/` with .NET 8 SDK → `bin/Release/NukeStats.dll`.
Compiling against the real assemblies confirmed all the member names: `Player.SteamID`,
`PlayerName`, `HQ.faction.factionName`, `PlayerScore`, `PlayerRank`, `Teamkills`, and the
`AddScore` patch. `EndGameOutcome` is `internal`, so it's targeted by name via
`AccessTools.Method("…EndGameOutcome:Complete")` (resolved at runtime; `HarmonyWrapSafe`
keeps the plugin alive if it ever moves, and the bot still commits on its own mission-end).

Remaining checks are **runtime-only** (confirm during the deploy test, below):
- that `p.SteamID` prints the 7656… id the bot uses (it compiled as a numeric type),
- that `p.HQ.faction.factionName` reads `Boscali`/`Primeva`,
- that `Console.WriteLine` lines actually land in `console.log`.

---

## Deploy on the GPanel/Pterodactyl server

1. **Install BepInEx (Unity Mono, x64, Linux)** into the container root (next to
   `NuclearOptionServer.x86_64`): the `BepInEx/` folder, `doorstop_config.ini`,
   `libdoorstop.so`, `run_bepinex.sh`. Use the BepInExPack from Thunderstore
   (`thunderstore.io/c/nuclear-option/p/BepInEx/BepInExPack/`) — Linux build.
   You can SFTP these in (I can help script the upload).
2. **Set the GPanel startup command** to the Doorstop launcher you already have:
   ```
   export DOORSTOP_ENABLED="1"; export DOORSTOP_TARGET_ASSEMBLY="$(pwd)/BepInEx/core/BepInEx.Preloader.dll"; export LD_LIBRARY_PATH=".:$(pwd)/linux64:$LD_LIBRARY_PATH"; export LD_PRELOAD="libdoorstop.so:$LD_PRELOAD"; ./NuclearOptionServer.x86_64
   ```
3. Copy `NukeStats.dll` to `BepInEx/plugins/NukeStats.dll`.
4. Restart the server.

## Verify (the load-bearing test)
- After restart, check `console.log` for `NukeStats loaded`. If BepInEx didn't load,
  `BepInEx/LogOutput.log` will say why.
- Join, score a point, and confirm `[NOSTATS] …` lines appear in **`/logs/console.log`**
  (`run.bat --scanlog NOSTATS`).
  - **If `[NOSTATS]` doesn't show up in console.log** (BepInEx may route stdout elsewhere):
    in `Out()` swap `Console.WriteLine` for `UnityEngine.Debug.Log` (Unity player log) or
    append to a file the bot can tail (e.g. `/home/container/no_stats.jsonl`) — tell me and
    I'll point the bot's tail at it.
- Confirm **vanilla players can still join** (a read-only server plugin shouldn't change
  client compatibility, but verify — Nuclei-style server mods are joinable).

## Turn it on in the bot
Once `[NOSTATS]` lines are confirmed flowing, set `USE_PLUGIN_SCORE = True` at the top of
`no_mapvote_bot.py` and restart the bot. That makes ranks run on the plugin's award model
(+200 win / +500/+250/+100 placement), stops the old derived +win awards and the
unreliable faction-0 victory call (the plugin's `win` event drives the announcement + W/L),
and starts pushing `plugin_ranks.txt` so chat shows `[Name - Rank]`. Then tune the `RANKS`
thresholds in the bot for the new (much larger) score scale.

## Maintenance
Nuclear Option is active Early Access — a patch can rename `PlayerScore`/`AddScore`/etc.
If stats go quiet after an update, re-run `_scan.py` against the new `Assembly-CSharp.dll`
to find renamed members, fix the names, rebuild.
