# Nuke Option Server Toolkit — Installer (MVP)

A guided, **offline** setup for the Nuclear Option community-server toolkit, plus an
**opt-in GitHub updater** for pulling plugin fixes by choice.

## Try it

```bash
python installer/setup.py
```

A browser tab opens with a 5-step wizard:

1. **Welcome** — prerequisite checks (Python, paramiko, the settings catalogue).
2. **Hosting** — where your game server runs: external Linux (Pterodactyl), your own PC, or external Windows.
3. **Connection** — SFTP + optional Pterodactyl power, with **Test** buttons. (Own-PC asks for the local game folder instead.)
4. **Features** — pick which plugin features are on (17 toggles, 4 presets: Full / PvP / PvE / Minimal). Fine-tune the *numbers* later in the web command centre's ⚙ Settings menu.
5. **Review** — optionally connect a GitHub repo for updates, then **Save**.

It runs fully locally (localhost only, guarded by a per-run token). The only network
calls are the "Test connection" buttons you click.

## What it writes (to `~/.nuke-option-toolkit/`, **outside the repo**)

| File | Contents | Shareable? |
|---|---|---|
| `config.json` | hosting scenario, host/port/user, log path, web port, selected features, GitHub repo | ✅ no secrets |
| `secrets.json` | SFTP password + Pterodactyl API key (written `0600`) | ❌ never commit |
| `anz.nukestats.cfg` | a ready-to-upload BepInEx config reflecting your feature choices | ✅ |

Set `NOST_DATA_DIR` to override the location.

## Updates (opt-in, by choice)

The install is offline; updates are a separate, deliberate step:

```bash
python installer/updater.py check     # is there a newer plugin? show release notes
python installer/updater.py update    # download + VERIFY + stage it for the next deploy
python installer/updater.py update --deploy   # ...and deploy right away
```

**Verify-before-apply:** the downloaded DLL's SHA-256 is checked against the release's
published hash, and its minisign (`.minisig`) signature is verified against the bundled
public key (`trusted.pub`) via the `minisign` CLI or pynacl/cryptography. With no
verifier available it refuses to stage unless you pass `--i-understand-unsigned`.
A staged update drops `pending_plugin.dll` (+ sidecar) into the toolkit root for the
existing guarded deploy pipeline (`run.bat --deploy-plugin`).

## Source auto-fetch (online or offline, per option)

The installer fetches the **right, latest** files for whichever hosting option you pick — you never hunt for files or check versions. Driven by a verified manifest (`sources.json`):

| Piece | Source (verified) |
|---|---|
| Server binaries | **SteamCMD, Steam app `3930080`** (always-latest; the Pterodactyl egg installs them host-side) |
| Pterodactyl egg | `pterodactyl/game-eggs/nuclear_option` (community egg; we add BepInEx on top) |
| BepInEx | `BepInEx/BepInEx` releases, **filtered to 5.4.x Unity-Mono** (never 6.x/IL2CPP); Thunderstore fallback |
| NukeStats plugin | your toolkit GitHub release (SHA-256 + minisign verified) — set `update.github_repo` to enable |

- **Autodetect** (`detect.py`): scans for a local Steam/game install → suggests own-PC; checks connectivity → defaults online/offline.
- **Online** (`fetcher.py`): resolves latest, downloads, verifies (SHA-256 + TOFU lockfile), safe-extracts. CLI: `python fetcher.py plan <option>` / `fetch <option> --dest <dir>`.
- **Offline** (`offline.py`): for less-trusting users — pre-download each file from its official GitHub, then `python offline.py urls <option>` shows the list and `validate <option> --dir <folder>` checks them. Same place/verify path runs from local bytes.
- Options: `own_pc_windows · own_pc_linux · external_linux_ptero · external_windows`.

## Status / not-yet-done (MVP)

- ✅ Source auto-fetch (manifest + fetcher + detect + offline), tested against the real egg + BepInEx; wired into the wizard (autodetect, online/offline, a Files step).

- ✅ Runnable wizard, config/secrets isolation, feature selection from the live catalogue, connection tests, opt-in updater with SHA + signature verification.
- ⏳ **Wiring the bot/web CC to read `config.json`/`secrets.json`** (today they read `run.bat` env) — until then the wizard produces the config but launching still uses the existing `run.bat`/`webcc.bat`.
- ⏳ Frozen single-file launcher (PyInstaller, per-OS incl. macOS), vendored BepInEx pack, the own-PC local transport, and a "Launch" button.
- ⏳ Ship `trusted.pub` (minisign public key) with real releases; wire signing into CI.
