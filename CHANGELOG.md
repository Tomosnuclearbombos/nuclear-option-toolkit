# Changelog

What changed in each public release. Every GitHub release shows its own section from this file.

## [1.9.2] 2026-10-13

* **The recorder is a lot lighter.** Build 3 of the
  [NOBlackBox fork](https://github.com/Tomosnuclearbombos/NOBlackBox/releases) takes about 80% less of the
  game's main thread, which on my PvE server was 54 to 85 ms of every second with 16 or more players on. It
  now hears about new units, destroyed units and explosions from the game, so it no longer searches the
  whole scene for them, and it only visits an object when its next update is due. Download the new zip and
  unzip it over the old `NOBlackBox` folder.
* **Mission objectives are checked less often.** A destroy-units objective was checking every one of its
  targets every frame, and on Escalation one objective alone cost about 10 ms of every second. The plugin
  now checks each one every quarter of a second while none of its targets are dying, and every frame again
  as soon as one is, given the game only marks one target per check. This is `Perf.DestroyCheckSeconds`
  (0.25, and 0 turns it off). On the test server it took the mission code from 11.9 to 1.5 ms a second.
* **A stuck player is let go.** Flood guard E stops the game disconnecting everyone when a flood fills every
  player's send buffer at once, however, it was also holding on to a single player whose connection had
  stopped taking data. He then sat frozen for about 30 seconds until the game timed him out. If one
  player's buffer stays full on its own for 5 seconds (`Flood.SingleVictimReleaseSeconds`) he is now
  disconnected so he can rejoin straight away, and a flood that hits two or more players is still blocked
  as before.
* **Hitches are named.** With `Perf.Timing` on, each long frame in the log now says which mission objective
  or outcome caused it and how many units a spawn made, and the bot keeps every one in `hitch_log.jsonl`
  with the mission it happened on. Of note, this is how I found a 17-unit convoy that spawned in one frame
  and cost about 90 ms.
* **Three more performance switches, all off by default.** `Perf.SkipVisualEffects` turns off wing vapour,
  propeller spin and the smoke trail emitter, `Perf.SkipServerParticles` stops particle effects filling up
  with particles nobody sees on a server (flares and chaff are left alone), and `Perf.FramePacer` has the
  plugin pace the frames in place of Unity's frame cap. The first two are on on my servers. The frame pacer
  made no difference on Windows and I haven't tested it on a Linux host yet.
* **All of these are in the dashboard.** The three switches, the objective check interval and the stuck player
  release are in the Settings menu, under AI & Performance and Anti-Grief, and they change straight away with no
  restart.
* **Smaller fixes.** The console history keeps 20 copies of one line a minute and counts the rest, given one
  player's full network queue logged the same error 22,075 times in 33 seconds and the day's earlier history
  was trimmed away. The ban log ignores the same offence logged twice within 30 seconds. Mission time warnings
  and the timed chat messages now wait 90 seconds after the server has been empty, as an empty server only
  loads its mission when the first player joins. The deep mission audit's result now survives a bot restart.
  A player whose wreck ejects them after a death no longer gets a landed sortie, and the name taken from a
  player's own game when Steam has none now swaps out characters the name font can't draw, as the game does.

## [1.9.1] 2026-10-12

* **Unnamed players fixed.** A new account with no Steam profile showed as "Pilot" everywhere, given Steam
  has no name to give. The plugin now uses the name the player's own game sent when they joined, and if
  Steam finds the real name later that one takes over. Of note, the player's game picks that name, so it's
  only used when Steam has nothing.
* **Wrong rank tags in the activity feed fixed.** The feed tags lines by name, so a new player shown as
  Pilot got the rank of a different player who is actually called Pilot. A name shared by two or more
  accounts, or the stand-in for a player with no name, now gets no tag, and the line shows exactly what
  the bot wrote.
* **Explosions fixed in the NOBlackBox fork.** The same explosion was being recorded again every half
  second for about 10 seconds, which is why explosions grew slowly out to a few kilometres on the live
  map and in Tacview. Each one is now recorded once, at its real blast radius. The download on the
  [fork's releases page](https://github.com/Tomosnuclearbombos/NOBlackBox/releases) is now the whole
  `NOBlackBox` folder, given the recorder won't start without the unit files that come with it and the
  last release only had the DLL.

## [1.9.0] 2026-10-10

This is the first public release since 1.5.0. It brings the public build up to the plugin, bot and
dashboard my own servers run, minus the parts that only work with my missions or the way my two servers
are set up. The version goes to 1.9 to line up with the plugin on my servers. Of note, the AI limiter
is gone.

### Plugin

* **Lighter on the server.** A fair bit has been tidied up under the hood, so the plugin uses
  noticeably less of the server. It takes units and factions from the game's own lists, which saves a
  search of the whole scene every second, it reads the rank file at most once a second, and it no
  longer rebuilds the player list every frame when nobody is on. The Steam ping lookup has its own
  thread now, so a slow reply from Steam can't hold up a frame.
* **New performance tools.** I've added tools that show exactly where the server spends its time. The
  bot writes a line to `perf_log.csv` once a minute with the player and AI counts, frame time, CPU and
  memory, and with `Perf.Timing` on it adds the breakdown. There are also settings for the frame rate
  the server aims for (`Perf.TargetFps`), the number of worker threads the game uses for physics
  (`Perf.JobWorkers`), and two switches that stop visual work nobody sees on a server.
* **AI planes get off the ground.** Since 0.33, AI planes have been bunching up at one airport. They now
  take off from the three nearest friendly airports, and the taxi and takeoff fixes stop them sitting
  stuck at a busy airfield. A side that is short of AI can also spawn up to three every five seconds
  where the game spawns one, however, the mission's AI limit still applies. The taxi fixes are based on
  parts of Appulcake's AI Plane Unstuck (MIT).
* **High ping kicker added.** Once 13 or more players are on a co-op mission (15 or more on PvP),
  anyone whose ping averages 250ms or more over 30 seconds gets a private warning and is kicked 60
  seconds later unless it drops. This goes off the average so a spike will not get anyone kicked. It
  starts with the highest ping and only kicks as many people as it takes to get the server back down to
  12 (14 on PvP). Of note, rejoining with a high ping gets a player kicked again, and the third kick
  keeps them out until the next mission. Admins are never kicked, and the numbers are all in Game
  Settings.
* **Repeat teamkillers.** Anyone flagged for teamkilling 3 or more times now gets a warning when they
  join the server. Their first teamkill in a match skips the eject and kicks them straight away, which
  resets their rank to 0. Another teamkill that match is a ban.
* **Changing the map early no longer fakes a win.** The server was reading it as the clock running
  out, so it announced a victory and paid the placement bonuses.
* **Annihilate can be switched off per mission.** Annihilate never fires on a mission listed in
  `Annihilate.ExcludeMissions`. I added this given it was ending one of my missions about a minute in.
* **AI limiter removed.** Its caps were 32 AI a side and 64 aircraft in total, which is well above what
  missions normally set, so it wasn't doing much. The stuck aircraft clearing it also did is covered by
  the taxi fixes now.

### Bot

* **Map vote in two rounds.** When a mission ends players get 30 seconds to vote on the mission, then
  30 seconds on its time of day or weather. The winner is announced and the map loads 15 seconds later.
  This is set up in the Missions window, where you pick which missions are variants of which and Map
  voting can be set to two rounds, one round or off, however, one round is still the default. Of note,
  a mission with no variants skips the second round.
* **Safer plugin deploys.** The new plugin is uploaded and checked before the server restarts, and the
  old one is kept. The bot also checks for players again right before the restart, so if someone joins
  during the upload the update waits.
* **Draws finish properly now.** The game has no draw ending, so the bot cuts the mission short when a
  draw is called. It now checks the server accepted the cut and sends it again every 10 seconds if it
  didn't.
* **PvE wins and losses.** The end of mission message on co-op missions now says whether the human
  side (BDF or PALA) won or lost.
* **No countdown on a forced change.** Cutting a mission short used to set off the time remaining
  warnings as the clock dropped. This is fixed.
* **Join lines wait for the name.** The join line waits a few seconds for the player's name to come
  through before it posts.
* **The ballot order is random.** The options are numbered in a different order every vote, so it's not
  always Escalation as !1.
* **The rank list is only sent to the plugin when it changes.**

### Dashboard

* **New layout.** Two columns, and the console has its own window from the Console button.
* **Activity feed.** It goes back as far as the log does with a line marking each day, whispers can be
  filtered by kind, and names show their rank tags.
* **Live map icons.** Units are drawn with icons taken from the game's own models. With the optional
  NOBlackBox recorder installed the map also shows ground vehicles, missiles and bombs, explosions,
  wrecks, gun rounds and flares, and the README has the steps.
* **Teamkill watch list.** Everyone on it is listed in the Moderation window.
* **Sharper maps.** The Heartland and Ignus map images have been redone at a higher resolution.

### Installer and launchers

* **The panel shows the server as running.** When the start command preloads Doorstop the panel used
  to stay on "starting", given the game's ready line never reached its console. The launch wrapper now
  writes that line straight to the panel console.
* **START EVERYTHING.bat fixes.** It never stops a plugin deploy that is running, it repairs PATH when a
  scheduled task starts it, and it closes the old console windows it used to leave behind.
* **Settings.** The AI limiter is out of the setup wizard and the config templates, and the plugin
  config example has the new sections. Game Settings has 113 settings now, all written out in
  `docs/SETTINGS.md`.

### Updating from 1.5.0

* **Live map extras are a separate download.** The updater only brings the bot, the plugin and the
  dashboard's own files, so the unit icons, the sharper maps and the NOBlackBox live map module are in
  `live-map-extras.zip` on the
  [1.9.0 release](https://github.com/Tomosnuclearbombos/nuclear-option-toolkit/releases/tag/v1.9.0).
  Unzip it into the toolkit folder (the one with `cc_web.py` in it) and restart the dashboard. Of note,
  the NOBlackBox part needs the [NOBlackBox recorder](https://github.com/Tomosnuclearbombos/NOBlackBox)
  on the game server, and a fresh install from the Pterodactyl download already has all of it.
