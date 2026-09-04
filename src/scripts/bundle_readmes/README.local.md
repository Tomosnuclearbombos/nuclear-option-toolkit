# Nuclear Option Community Server — Local bundle (v{{VERSION}})

Run a full community server on **your own PC**: the dedicated server, the NukeStats plugin,
the bot, and the web command centre — launched together with one click.

**Everything except the game binaries is in this folder** (BepInEx, the plugin, the bot, the
web command centre). The installer fetches the game binaries from Steam via SteamCMD.

---

## Before you start

- Windows 10/11 (or Linux). **Python 3.8+** (from <https://python.org> — tick *Add python.exe to
  PATH*).
- ~5 GB free disk for the dedicated server.
- For internet play: the ability to forward two **UDP** ports on your router.

---

## Install it

1. Unzip this folder somewhere short (avoid OneDrive / Program Files).
2. **Windows:** double-click **`install.bat`**.  **macOS/Linux:** run **`./install.sh`**.
   The first screen has an **Install Python packages** button if anything is missing.
3. In the wizard:
   - **Server step** — choose *Install it for me* (SteamCMD) or point to an existing server
     folder. SteamCMD is a two-step download (it self-updates first, then downloads the ~several-GB
     server) — the buttons walk you through it. Then set ports, server name, your admin SteamID64,
     max players, password.
   - **Connection step** — the local log path and remote-command port are pre-filled; you don't
     need to forward the command port (the bot reaches the server on `127.0.0.1`).
   - **Features step** — turn plugin features on/off.
   - **Install step** — click **Install & set up**. It copies the bundled BepInEx + plugin
     into your server folder, writes the config, and makes the launchers.
4. Click **🚀 Launch Everything** — it boots the server (BepInEx loads the plugin), starts the bot
   and the web command centre, and opens your dashboard at `http://localhost:8770`.

After the first time, just double-click **`START EVERYTHING.bat`** in your server folder.

---

## Port forwarding (internet play)

Open and forward **both** ports as **UDP** to this PC: game `7777/UDP` and query `7778/UDP`
(or whatever you chose). LAN-only? You can skip forwarding.

## Troubleshooting

- **Plugin not loading.** Confirm `winhttp.dll` and `doorstop_config.ini` sit next to
  `NuclearOptionServer.exe`, and `BepInEx\plugins\NukeStats.dll` exists. (The installer places
  these; re-run *Install & set up* if needed.)
- **`ModuleNotFoundError`.** Use the **Install Python packages** button on the Welcome step.
- **Settings don't apply on an empty server.** Live plugin settings only take effect while at
  least one player is online — that's a game limitation.

— Full feature/command docs are in `docs/`.
