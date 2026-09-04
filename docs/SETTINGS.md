# Settings guide

All 92 settings, in plain English. They're in the dashboard under **Settings → Game Settings**, and
this page is the same list written out.

Each one gives the name, what it does, and the value it ships with. You can change them while the
server is running, and most take effect on the next tick.

## Contents

- [Messages & Feed](#messages--feed), 2 settings
- [Scoring & Ranks](#scoring--ranks), 6 settings
- [Rank + Fund catch-up](#rank--fund-catch-up), 3 settings
- [Match](#match), 13 settings
- [End of Match & Votes](#end-of-match--votes), 7 settings
- [Team Balance](#team-balance), 17 settings
- [Moderation](#moderation), 12 settings
- [Anti-Grief](#anti-grief), 24 settings
- [PvE](#pve), 1 setting
- [AI & Performance](#ai--performance), 7 settings

## Messages & Feed

### Custom chat
The plugin writes its own chat and join lines (faction colours; rank tags when the rank ladder is on). OFF = chat stays completely native - no rewriting at all.

*Default: `on`*

### Slur Filter
Replaces a whole message with a canned line if it contains a racist slur; ordinary swearing is left alone.

*Default: `on`*

## Scoring & Ranks

### 1st Place Bonus
Bonus rank points for the match's top scorer, whichever side. Needs Award: Win / Placement Points ON.

*Default: `500`, range 0–5000*

### 2nd Place Bonus
Bonus rank points for the match's second-highest scorer, whichever side. Needs Award: Win / Placement Points ON.

*Default: `250`, range 0–5000*

### 3rd Place Bonus
Bonus rank points for the match's third-highest scorer, whichever side. Needs Award: Win / Placement Points ON.

*Default: `100`, range 0–5000*

### Score Gain Clamp (per second)
Hard cap on score one player can bank per second of match time; anything above is logged and flagged but never credited. Lower it during a live exploit incident. Needs a bot restart to apply.

*Default: `1000`, range 10–100000*

### Score Spike Alert (per second)
A single score update above this many points per second is flagged in the activity feed as a possible exploit. Informational only - the clamp above does the blocking. Needs a bot restart to apply.

*Default: `1000`, range 10–100000*

### Win Points
Rank points to every player on the winning side at match end (server rank, not in-game score). Needs Award: Win / Placement Points ON or they are discarded.

*Default: `200`, range 0–2000*

## Rank + Fund catch-up

### PvP Starting Rank Floor
PvP only: minimum starting in-game rank for every player (sets opening funds and what they may fly). A floor on top of the mission's own start rank; never lowers anyone. 0 = off; PvE unaffected. Max 5 (game clamp).

*Default: `3`, range 0–5*

### Rank funds - who gets paid
When rank funds pay (needs Funds per Rank > 0); the start/join floor never pays. catchup_raised = catch-up ranks only; catchup_all = everyone each catch-up step (both need catch-up on). any_rankup = any rank earned in play, the only mode that works with catch-up off.

*Default: `catchup_raised`*

### Rank-up Funds per Rank (Allocation millions)
Funds granted per rank, same units as !addfunds (millions of display funds: enter 3, not 3,000,000). 0 = off. Pays mid-match rank-ups/catch-up only, never the start/join floor. Every mode except any_rankup needs 'Rank Catch-up: Minutes per +1' above 0.

*Default: `30`*

## Match

### Annihilate Auto-Win
End the match when one side has no planes AND no hangars to spawn from, once that holds for the grace time. Applies to PvE co-op too unless the PvP-only row below is on.

*Default: `on`*

### Annihilate Both Sides Dead
If every side is wiped at once: noop = do nothing; draw = force a draw.

*Default: `noop`*

### Annihilate Count AI Aircraft
Count AI aircraft as 'planes left'. ON = a side still flying AI is not wiped.

*Default: `on`*

### Annihilate Grace Seconds
Seconds the wiped condition must hold continuously before victory; stops a momentary gap between spawns from ending the match.

*Default: `20`, range 5–120*

### Annihilate Min Match Seconds
Do not annihilate until this many seconds after mission load (avoids false wins while bases/AI spin up).

*Default: `120`, range 30–600*

### Annihilate Min Players
Minimum connected humans required before annihilate can fire (empty-lobby guard).

*Default: `1`, range 0–32*

### Annihilate PvP Only
ON = annihilate ends PvP matches only. OFF = PvE co-op can also end when a side has no planes and no hangars.

*Default: `off`*

### Annihilate Require No Spawn
ON = a side is wiped only with no planes AND no hangars to spawn from. OFF = no planes alone ends it, even if the side could still have rebuilt from hangars.

*Default: `on`*

### Forfeit Vote Cooldown
Seconds before a team can start another forfeit vote (anti-spam); the vote window is min(60, this).

*Default: `90`, range 30–600*

### Forfeit Voting Enabled
PvP only: lets a team vote to surrender the match via !forfeit / !f (majority needed).

*Default: `on`*

### Forfeit: minimum team size
Players needed on your side before a forfeit vote can start or pass. At 1, a lone player is their own majority and can instantly end the match for everyone; 3 keeps it a team decision.

*Default: `3`, range 1–16*

### PvP Timeout Decides Winner
PvP only: when the timer runs out with no winner, the team with the higher total in-game score wins (exact tie = draw). OFF = just rotate with no result. No effect in PvE.

*Default: `on`*

### Timeout Lead (before mission end)
Fires the timeout result (PvE defeat / PvP score result) this many seconds BEFORE the mission's MaxTime, leaving room for the map vote before the game auto-rotates. 0 = exactly at MaxTime.

*Default: `120`, range 0–600*

## End of Match & Votes

### Force PvP at high population
When enough players are online the end-of-match ballot goes PvP-heavy, using the three settings below. Applies from the next ballot.

*Default: `on`*

### Force PvP: PvE maps on ballot
How many PvE/co-op maps stay on the ballot while PvP is being forced (0 = PvP-only ballot).

*Default: `0`, range 0–12*

### Force PvP: PvP modes on ballot
How many PvP modes go on the ballot while PvP is forced, capped by the built-in PvP modes enabled in the Mission Pool.

*Default: `6`, range 0–6*

### Force PvP: player count
Force the PvP-heavy ballot once at least this many players are online.

*Default: `24`, range 1–200*

### Lockout Between Map Votes (s)
Seconds after a vote is applied before another map vote can open - this is what blocks an immediate player !votemap. Needs a bot restart.

*Default: `90`, range 0–1800*

### Max Dark Maps per Ballot
Caps Night/Thunderstorm/Overcast/Dusk variants in the random co-op fill only (Dawn isn't dark). Pinned maps and the PvP half aren't counted, so a force-PvP ballot can be all dark. Needs a bot restart.

*Default: `3`, range 0–6*

### Mission Cut-Down After a Vote (s)
When a map vote lands mid-mission the running mission's clock is cut to this many seconds so the winner loads right after. Too low races the mission-end handling. Needs a bot restart.

*Default: `10`, range 5–120*

## Team Balance

### Auto-Move Players
PvP only: when a side is too far ahead, actually move the best-fit player to the smaller side (off = block-join only).

*Default: `on`*

### Balance Check Interval
Seconds between auto-balance checks.

*Default: `6`, range 2–60*

### Balance Warning Hold
After teams go unbalanced, broadcast a warning and wait this many seconds before moving anyone.

*Default: `300`, range 0–900*

### Deferred Move Timeout (s)
How long a deferred balance move (see Only Move Grounded Players) waits for the pilot to land or die before applying anyway. 0 = wait indefinitely.

*Default: `900`, range 0–3600*

### Drop Point: Heartland BDF
Landing spot for players put on BDF (Boscali) on Heartland, as 'x,z' in metres. Used by every plugin teleport (auto-balance, !swapteam/!forceteamswap) - re-check it whenever the map changes. Unparseable values fall back to an empty ocean corner.

*Default: `-5000,60000`*

### Drop Point: Heartland PALA
Landing spot for players put on PALA (Primeva) on Heartland, as 'x,z' in metres. Used by every plugin teleport (auto-balance, !swapteam/!forceteamswap) - re-check it whenever the map changes. Unparseable values fall back to an empty ocean corner.

*Default: `-5000,-60000`*

### Drop Point: Ignus BDF
BDF (Boscali) landing spot on Ignus, as 'x,z' in metres. Used by every plugin teleport (auto-balance, !swapteam). Re-check after map changes; a value that won't parse falls back to an empty ocean corner.

*Default: `75000,0`*

### Drop Point: Ignus PALA
PALA (Primeva) landing spot on Ignus, as 'x,z' in metres. Used by every plugin teleport (auto-balance, !swapteam). Re-check after map changes; a value that won't parse falls back to an empty ocean corner.

*Default: `-75000,0`*

### Max Team Size Gap
How many players one side may be ahead before balancing kicks in (2 = a 2-player gap is fine, only a 3+ gap acts).

*Default: `2`, range 1–6*

### Min Players for Balancing
Auto-balance never MOVES anyone with fewer humans than this online. It does NOT gate the join blocker: joining the fuller side past the gap limit still bounces to spectate at any headcount - only Enforce Balance OFF spares small lobbies entirely.

*Default: `6`, range 2–32*

### Min Seconds Between Moves
Minimum seconds between two auto-balance moves, to stop churn.

*Default: `20`, range 5–120*

### Move Cooldown (Games)
Once moved, a player won't be moved again for this many games, spreading the burden around.

*Default: `2`, range 0–5*

### Never Move The Top Scorer
ON = each team's current top scorer (live match score) is never auto-moved; the next-best candidate goes instead, or no move happens if they were the only option. Nobody is protected before the first point is scored.

*Default: `on`*

### New-Joiner Protection
Never move a player who joined less than this many seconds ago; the strongest balance immunity. 0 = off.

*Default: `900`, range 0–1800*

### Only Move Grounded Players
ON = an airborne player picked for balancing is warned and moved once they land or die, so nobody loses a sortie; grounded players move at once. OFF = move mid-flight. Deferred Move Timeout caps the wait.

*Default: `on`*

### Team Balance Enabled
PvP only: keeps team sizes close. A side ahead by more than Max Team Size Gap blocks new joins, and with Auto-Move on the best-fit player is moved to the smaller side.

*Default: `on`*

### Team-Swap Spawn Altitude
Spawn altitude in metres for the brief Cricket used by !swapteam/!forceteamswap before ejecting; raise to 3000 if crashes appear.

*Default: `2500`, range 1500–5000*

## Moderation

### Admin SteamIDs
Comma-separated SteamIDs allowed to use the admin chat commands; also the list Exempt Admins from Auto-Kick reads. Panel moves don't need it, and a bare !swapteam (no player named) stays public for everyone.

*Default: ``*

### Big Unit Collateral Exempt
If the same blast also killed a big enemy objective (carrier/destroyer/ship), the friendly kill is treated as collateral: flagged, never punished.

*Default: `on`*

### Collateral Max Per Match
Cap on exonerating collateral verdicts one player can get per match; beyond it, kills use the normal punishment ladder. 0 = uncapped.

*Default: `3`, range 0–50*

### Collateral Window (s)
Seconds each way around a friendly kill in which the same shooter's other kills count toward the collateral verdict. Ignored on nuke-scale blasts, which use the Nuclear window instead.

*Default: `2.5`, range 0.5–10*

### Damage Calibration Log
Log-only: writes a [dmgcal] line per player-caused unit death (credited damage, top attacker's share) to help tune Teamkill Min Damage. Changes nothing in game; players never see it.

*Default: `on`*

### Enrich Position Feed
Adds altitude, airframe and a landed flag to the position feed; the panel's aircraft lookups read the airframe code. OFF = smaller legacy payload only.

*Default: `on`*

### Nuclear Collateral Window (s)
Collateral window for nuke-scale blasts (yield over 200); the nuke verdict/warning is also delayed this long. Backward reach is effectively capped near 20s whatever you set, because kill evidence is only kept about 60s.

*Default: `20`, range 5–40*

### Silent Collateral Min Enemies
Blasts with at least this many enemy kills (and meeting the Silent Ratio) get a silent, log-only verdict. 0 = tier off.

*Default: `10`, range 0–100*

### Silent Collateral Ratio
Companion to Silent Min Enemies: enemy kills must also be at least this many times the friendly count for a silent verdict.

*Default: `5`, range 1–50*

### Teamkill Collateral Check
Judge blast collateral before punishing: a blast that hit only friendlies goes up the punishment ladder; one that also hit enemies is reported, not punished. OFF = classic ladder, verdicts only logged.

*Default: `on`*

### Teamkill Min Damage
Minimum credited damage for a friendly kill to count as a punishable teamkill; filters out grazes. 0 = off.

*Default: `100`, range 0–10000*

### Teamkill Punishment
Auto-punishes friendly fire: 1st = eject+warning, 2nd = kick+rank reset, 3rd = persistent ban.

*Default: `on`*

## Anti-Grief

### Absorb Send-Buffer Overflow
The mass-disconnect fix. The game kicks any player whose reliable send buffer overflows, and a flood overflows everyone at once - kicking the lobby. This absorbs the overflow instead; dead clients still drop via the normal timeout. Cost: a sustained flood shows as desync for those affected until it stops. Leave ON.

*Default: `on`*

### Auto-Release Wedged Sessions
Frees clients wedged mid-load: connected, faction back, but the map never finishes loading. ON = after the release time below they get a clean disconnect and can rejoin at once - never the kick list, so no lockouts. Detection logs wedges even while OFF; enable only once those logs show real wedges, not slow loaders. Spawn-menu idlers are never touched.

*Default: `off`*

### Command Allowlist (jsonKeys)
Only used when Policy = AllowlistTypes: comma-separated unit jsonKeys to allow (case-insensitive). Empty = all ground vehicles. Turn on Command Policy Diagnostics to discover jsonKeys.

*Default: ``*

### Command Policy Diagnostics
Logs each command order's unit type, deployed state, and ALLOW/DROP decision to BepInEx/LogOutput.log. Verbose - turn ON briefly to find jsonKeys or confirm what's blocked, then back OFF.

*Default: `off`*

### Dead-Unit Command Kick Strikes
Kicks a player after this many dead-unit commands within 10 seconds; orders already in flight when the unit died never count. 0 = off (drop only). Needs Drop Dead-NetId RPCs ON; honours Report Only and admin exemption.

*Default: `3`, range 0–50*

### Drop Dead-NetId RPCs
Silently drops any command aimed at a unit that no longer exists (not just move orders), removing the log-and-error storm the game would otherwise answer with - an amplifier under a flood. Also the gate for the dead-unit kick below; leave ON.

*Default: `on`*

### Exempt Admins from Auto-Kick
ON = never auto-kick anyone listed in Admin.SteamIds (honoured by the dead-unit, inbound-flood and overflow-source kicks). Turn OFF only to self-test a detector on your own admin account.

*Default: `on`*

### Inbound Flood Kick After (s)
Auto-kick a sender that stays over the inbound cap for this many seconds straight. 0 = drop only, never kick; values above 0 are floored to 0.5. Enable (1.5 is typical) only after [flood-measure] shows legit clients never sustain the cap, or busy players get false-kicked. Admins exempt.

*Default: `0`, range 0–60*

### Inbound Message Burst
Burst allowance: messages that may arrive at once before drops start. The bucket starts full, so salvos, spawn-ins and mission loads never drop. Keep near 2x the per-second cap.

*Default: `800`, range 50–20000*

### Inbound Message Rate Guard
Per-connection cap on ALL inbound network messages - the guard against one flooder mass-disconnecting the whole lobby (the server re-broadcasts to everyone). Excess is dropped; real peaks are logged as [flood-measure] for tuning the cap below. Fails open on any error.

*Default: `on`*

### Inbound Messages Per Second
Sustained inbound messages per second, per connection. Deliberately high: clients legitimately send a lot. Keep it 3-5x the [flood-measure] peak in the console - a dropped message can cost a shot or spawn, so never tighten on a guess.

*Default: `400`, range 50–5000*

### Lift Error-Kick Rejoin Lockout
The game's error-kick fires on harmless snapshot noise, yet adds a hidden ~300s rejoin lockout and walks players toward a permanent Error Auto Ban. ON = clear that lockout and roll the ban ladder back: the disconnect still happens, but rejoin is immediate and error noise can never auto-ban. Cheat-grade instant bans are left alone. Works whatever DisableErrorKick is set to.

*Default: `on`*

### Log Rate-Dropped Messages
Logs name/SteamID of senders being rate-dropped by the Inbound Message Rate Guard, at most once per 5s per connection. Only that guard drops anything now - move orders are no longer rate-limited.

*Default: `on`*

### Overflow: Kick the Source After N Victims
Kick the source once one sender overflows this many DISTINCT victims within ~3s (a legit sender overflows almost nobody). 0 = absorb only, no kick. Set ~6 only after the [flood] blame lines have only ever named real flooders. Suppressed when 3+ sources flood at once; admins exempt.

*Default: `0`, range 0–64*

### Raise Reliable Send Buffer
Anti mass-disconnect: raises the per-connection send-buffer cap so a message burst is absorbed instead of overflowing and disconnecting everyone. Applies at the next match host. Leave ON.

*Default: `on`*

### Reliable Send Buffer Limit
Per-connection reliable send-buffer cap (game default 3000). 8000-24000 suits normal play; go higher (48000+) only as a stopgap for a very high-unit-count server; 999999 removes the ceiling at a memory cost. Never lowers an already-higher value; applies at the next match host.

*Default: `12000`, range 3000–999999*

### Report Only (no kick)
ON = incidents are detected and filed in Reports but nobody is kicked - use it to trial the dead-unit strike threshold on real play. OFF = kick as configured.

*Default: `off`*

### Spotting Points Safety Breaker
Vanilla spotting pay plus a per-player meter: abnormal earnings stop only that player earning from spotting. No kick, nothing in chat; the block clears at match end or mission change. OFF = vanilla pay, no metering.

*Default: `on`*

### Spotting Points: Emergency Off
Emergency switch: ON stops radar spotting and jamming paying any points or funds to anyone, immediately, no restart. Use only if spotting misbehaves - normal running is OFF with the breaker above.

*Default: `off`*

### Spotting Points: Match Backstop
Per-match spotting-score cap per player; it catches a slow drip that stays under the rate limit. Deliberately high - a cap an honest 3-hour match could reach would punish your best radar pilot. Check the logged spotting totals before changing.

*Default: `25000`*

### Spotting Points: Rate Limit
Spotting score one player may earn per rate window before the breaker trips; 300 is far above any real radar pilot. Raise it if a genuine player is ever caught; never lower it on a guess.

*Default: `300`*

### Spotting Points: Rate Window
Window (seconds) the spotting rate limit is measured over. Leave at 60 unless deliberately re-tuning the breaker.

*Default: `60`*

### Unit Command Policy
Which units players may move-command. HeliDroppedOnly (recommended) = only player-deployed ground vehicles. All / RateLimitOnly = any commandable unit (can allow command-flooding). AllowlistTypes = the list below. Disabled = none. This filters targets, not speed - the game itself caps ~5 move orders/sec per player. Applies live.

*Default: `HeliDroppedOnly`*

### Wedge Release After (Seconds)
Seconds a player must stay wedged (map load never completed) before Auto-Release disconnects them; floored to 60. Raise for very slow loaders; a grace window after map changes protects mid-rotation reloads.

*Default: `180`, range 60–600*

## PvE

### PvE Timeout = Defeat
PvE co-op: when the mission timer runs out and humans haven't won, declare them defeated instead of silently rotating.

*Default: `on`*

## AI & Performance

### AI Housekeeping (runs the stuck-AI clear)
Master switch for AI housekeeping; the Stuck-AI Clear Time below runs on it, so OFF = stuck AI is never cleared. Also holds a loose background AI ceiling that mission caps normally keep the server under. Only ever removes AI aircraft, never players.

*Default: `on`*

### Clean Up Ejected Pilots
Periodically despawn lingering ejected pilots on the map to cut clutter and load.

*Default: `on`*

### Ejected Pilot Lifetime
Seconds a dismounted pilot may linger before being cleaned up.

*Default: `300`, range 30–1800*

### Server Tick Rate (Hz)
Engine updates per second: higher = snappier AI and missile reactions (not physics) at more CPU cost. TO APPLY: save, run run.bat --setup-server, then restart the SERVER (drops players) - a plain panel restart keeps the old rate.

*Default: `60`, range 30–120*

### Stats Snapshot Interval
Seconds between full per-player stats snapshots sent to the bot.

*Default: `10`, range 2–60*

### Stuck-AI Clear Time
A grounded AI that hasn't moved for this many seconds is cleared to free a clogged runway. 0 = off. Needs AI Housekeeping ON or this timer does nothing.

*Default: `45`, range 0–300*

### Stuck-AI Move Radius
Metres a grounded AI must move within the clear time to count as not stuck. Needs AI Housekeeping ON.

*Default: `25`, range 5–200*
