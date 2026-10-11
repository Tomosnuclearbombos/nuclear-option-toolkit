# Changelog

What changed in each public release. Every GitHub release shows its own section from this file.

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
