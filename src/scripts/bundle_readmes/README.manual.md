# Nuclear Option Community Server — Manual bundle (v{{VERSION}})

For people who host the dedicated server **by hand** (their own box, or a setup the automated
bundles don't cover) and want to add the toolkit themselves. This folder ships **everything** —
both BepInEx packs (Windows + Linux), the NukeStats plugin, the bot, and the web
command centre — plus an optional helper that just writes your config.

You still get the **full** community server (server + plugin + bot + web console); the
only thing "manual" is that you place the files and set the startup line yourself.

---

## 1. Put the game-side files on your server

`<GAME>` = your server's game folder (the one with `NuclearOptionServer.exe` on Windows or
`NuclearOptionServer.x86_64` on Linux; on Pterodactyl that's `/home/container`).

**Shared (both OSes)** — copy from `game-side/common/`:

| From | To |
|---|---|
| `game-side/common/BepInEx/` | `<GAME>/BepInEx/` (merge) |
| `game-side/common/BepInEx/plugins/NukeStats.dll` | `<GAME>/BepInEx/plugins/NukeStats.dll` |

**Windows server** — also copy from `game-side/windows/`:

| From | To |
|---|---|
| `game-side/windows/winhttp.dll` | `<GAME>/winhttp.dll` (next to the .exe) |
| `game-side/windows/doorstop_config.ini` | `<GAME>/doorstop_config.ini` |

**Linux / Pterodactyl server** — also copy from `game-side/linux/`:

| From | To |
|---|---|
| `game-side/linux/libdoorstop.so` | `<GAME>/libdoorstop.so` |
| `game-side/linux/run_bepinex.sh` | `<GAME>/run_bepinex.sh` |
| `game-side/linux/no_relay.py` | `<GAME>/no_relay.py` (only if the bot runs on another machine) |

> BepInEx must be the **5.4.x Mono** line (shipped here). Never mix in 6.x / IL2CPP.

## 2. Edit `DedicatedServerConfig.json`

Merge the fields from `game-side/DedicatedServerConfig.snippet.json` into your
`<GAME>/DedicatedServerConfig.json` (back it up first). Key gotchas:
`ModdedServer` is the **string** `"true"`, `Port`/`QueryPort` are `{"IsOverride":true,"Value":N}`,
and `MissionDirectory` is `"NuclearOption-Missions"`. The snippet includes the stock mission rotation.

## 3. Set the startup line

- **Windows:** launch the server as you do now, adding
  `-logFile logs\console.log -ServerRemoteCommands 5504`. `winhttp.dll` injects BepInEx
  automatically — no env vars needed.
- **Linux / Pterodactyl:** set the Startup to (substitute your ports):
  ```
  mkdir -p ./logs; python3 no_relay.py 0.0.0.0:5550 127.0.0.1:5504 & export LD_LIBRARY_PATH="$(pwd):$(pwd)/linux64:$LD_LIBRARY_PATH"; export DOORSTOP_ENABLED=1; export DOORSTOP_TARGET_ASSEMBLY="$(pwd)/BepInEx/core/BepInEx.Preloader.dll"; export LD_PRELOAD="$(pwd)/libdoorstop.so:$LD_PRELOAD"; ./NuclearOptionServer.x86_64 -batchmode -nographics -logFile ./logs/console.log -limitframerate 60 -ServerRemoteCommands 5504
  ```
  (Ignore `run_bepinex.sh`; the env vars in the line above do the same job.)

Start the server and check the console for `NukeStats loaded`.

## 4. Set up the admin tools (this PC)

Run **`install.bat`** / **`./install.sh`** (or `python installer/setup.py`) and answer the
questions — it writes `~/.nuke-option-toolkit/config.json` + `secrets.json` and the plugin cfg.
Each field explains what it is and where to find it (log path, the relay host/port, and, for a
remote server, your SFTP + Pterodactyl API details).

Then start the bot and the web command centre:
```
python no_mapvote_bot.py
python cc_web.py
```
and open `http://localhost:8770`.

## Troubleshooting

- Live plugin settings only apply while at least one player is online (game limitation).
- `ModdedServer` must be the string `"true"`; query port must differ from the game port.
- Secrets live only in `secrets.json` / `apiKey.txt` on this PC.

— Full feature/command docs are in `docs/`.
