# Nuclear Option Community Server — Pterodactyl bundle (v{{VERSION}})

This folder turns your **Pterodactyl-hosted** Nuclear Option dedicated server into a full
community server: persistent ranks, a real-score economy, PvP team balance,
anti-grief, a live battle map, and a browser admin console.

**Everything is already in this folder** — BepInEx, the NukeStats plugin, the bot, and the
web command centre. You don't download anything else.

---

## What the installer does

When you run it and enter your panel details, it:

1. Connects to your server over **SFTP** and uploads BepInEx + the plugin + the relay.
2. Writes your **`DedicatedServerConfig.json`** (ports, `ModdedServer`, the stock mission rotation,
   server name, max players, password) — merging into any existing config and backing it up.
3. Installs a tiny **launch wrapper** so the server boots **modded with no panel edits**
   (Pterodactyl won't let an API change the Startup command, so the wrapper does the work —
   it self-injects BepInEx, opens the command relay, and writes a stable console log).
4. If you give it your panel **API key**, it stops the server before the upload and starts it
   after. Otherwise you restart it once from the panel.
5. Sets up the **bot + web command centre** here on this PC and makes a **START EVERYTHING**
   launcher.

---

## Before you start

- A Pterodactyl server already created with the **Nuclear Option** egg.
- **Python 3.8+** on this PC (from <https://python.org> — tick *Add python.exe to PATH*).
- Your panel's **SFTP details** and a **client API key** (the installer explains where to find
  each one).
- Ideally a spare **network allocation** for the command relay (Panel → *Network*).

---

## Install it

1. Unzip this folder somewhere on your PC.
2. **Windows:** double-click **`install.bat`**.  **macOS/Linux:** run **`./install.sh`**.
   (Or run `python installer/setup.py`.) If a prerequisite is missing, the first screen has an
   **Install Python packages** button.
3. A wizard opens in your browser. Work through it — every field shows **what it is** and
   **where to find it**:
   - **Server step** — game/query ports, server name, your admin SteamID64, max players, password.
   - **Connection step** — your panel's SFTP host/port/user/password, your client API key + panel
     URL (for power), and the relay port. Use **Test SFTP** and **Test panel** to confirm.
   - **Features step** — turn plugin features on/off (you can fine-tune everything later in the
     web console).
   - **Install step** — click **Install to my server**. It uploads (~25 MB; give it a minute or
     two) and reports each step.
4. When it finishes, your server is restarting (or restart it from the panel). Watch the panel
   **Console** for `NukeStats loaded`.
5. Click **🚀 Launch Everything** to start the bot + web command centre and open your dashboard
   at `http://localhost:8770`.

After the first time, just double-click **`START EVERYTHING.bat`** in this folder.

---

## Troubleshooting

- **`NukeStats loaded` never appears.** Make sure the egg's Startup runs the game binary by name
  (the default). The wrapper handles BepInEx — you do not edit the Startup. Check the panel
  console and `logs/relay.log` in the container.
- **The bot can't send commands.** The relay port must be a real, reachable Pterodactyl
  **allocation** (Panel → Network), and the “Server address” in the Connection step must be your
  server's public host. The bot connects to `host:relay-port`.
- **Settings don't seem to apply.** Live plugin settings only take effect while **at least one
  player is online** — that's a game limitation, not a bug.
- **SFTP password.** On Pterodactyl this is your **panel account** password.
- **`ModdedServer` must be the string `"true"`** — the installer writes it correctly; don't change
  its type if you hand-edit the config.

To undo the server changes: delete the wrapper in the container and rename
`NuclearOptionServer` back to `NuclearOptionServer.x86_64`.

Your SFTP password and API key live only in `secrets.json` / `apiKey.txt` on **this** PC and are
never uploaded or committed.

— Full feature/command docs are in `docs/`.
