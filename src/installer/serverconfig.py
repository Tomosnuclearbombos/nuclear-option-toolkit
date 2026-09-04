#!/usr/bin/env python3
"""Read/write Nuclear Option's DedicatedServerConfig.json — ports, name, players, password.

Schema (the subset the installer owns): Port/QueryPort are {IsOverride, Value}; ServerName,
MaxPlayers, Password are scalars; ModdedServer is the STRING "true"/"false". We MERGE into
any existing file (preserving unknown keys) and back it up first, so re-running is idempotent
and never clobbers a hand-tuned config. DEFAULTS below only ever apply to a FRESH config.
"""
import copy
import json
import os
import time

CONFIG_NAME = "DedicatedServerConfig.json"

# The stock (vanilla, Group "BuiltIn") rotation the toolkit ships. No custom mission files are
# installed; owners can upload their own via the Web CC Missions modal and extend this rotation.
_MISSION_NAMES = [
    "Escalation Co-op as BDF", "Escalation Co-op as PALA",
    "Terminal Control Co-op as BDF", "Terminal Control Co-op as PALA",
    "Escalation",
]
_ROTATION = [{"Key": {"Group": "BuiltIn", "Name": n}, "MaxTime": 7200.0} for n in _MISSION_NAMES]

# Defaults used only when creating a fresh config (no existing file to merge into).
# The VoteKick block, the two error-kick keys and the two timing keys are written here so a fresh
# install lands the POST-2026-07-27 game shape: the Web CC Server Config tab can only edit keys the
# game understands, and a config born without them made every save of those fields a create-on-write.
# build_config() does cfg.update(existing), so an existing config still wins key-for-key.
DEFAULTS = {
    "MissionDirectory": "NuclearOption-Missions",
    "ModdedServer": "true",
    "Hidden": False,
    "ServerName": "My Nuclear Option Server",
    "Password": "",
    "MaxPlayers": 16,
    "RotationType": 2,
    "MissionRotation": _ROTATION,
    # True = the game never error-kicks. Shipped ON deliberately: an error-kick also starts a
    # 300s rejoin lockout that each retry extends, and the player only sees "Local client
    # stopped" - it reads as a broken game, not a kick. Cost us a live incident 2026-07-28.
    "DisableErrorKick": True,
    "PostMissionDelay": 45.0,          # the bot re-derives this (vote length + post-vote delay)
    "NoPlayerStopTime": 30.0,
    "VoteKick": {
        "Enabled": True,
        "PassRatio": 0.6,
        "MinVotes": 3,
        "AutoBanThreshold": 3,         # 2026-07-27 game update: auto-BAN after N successful vote-kicks
        "VoteDuration": 45.0,
        "ResolutionDisplayTime": 20.0,  # 2026-07-27
        "NewVoteLockout": 10.0,         # 2026-07-27
        "RequesterCooldown": 300.0,     # 2026-07-27
    },
    "BanListPaths": ["ban_list.txt"],
    "ErrorKickImmuneListPaths": [],     # 2026-07-27: per-player exemption from the desync error-kick
}


def validate_ports(game_port, query_port):
    """Return '' if ok, else a human error string."""
    try:
        g, q = int(game_port), int(query_port)
    except (TypeError, ValueError):
        return "ports must be numbers"
    if not (1024 <= g <= 65535 and 1024 <= q <= 65535):
        return "ports must be between 1024 and 65535"
    if g == q:
        return "game port and query port must differ"
    return ""


def build_config(existing, game_port, query_port, server_name="", max_players=0,
                 password="", modded=True):
    cfg = copy.deepcopy(DEFAULTS)                  # deep: DEFAULTS now holds nested dict/list values
    if isinstance(existing, dict):
        cfg.update(existing)                       # preserve everything they already had
    cfg["Port"] = {"IsOverride": True, "Value": int(game_port)}
    cfg["QueryPort"] = {"IsOverride": True, "Value": int(query_port)}
    if server_name:
        cfg["ServerName"] = server_name
    if max_players:
        cfg["MaxPlayers"] = int(max_players)
    if password:
        cfg["Password"] = password
    cfg["ModdedServer"] = "true" if modded else "false"
    return cfg


def write_config(dest_dir, game_port, query_port, server_name="", max_players=0,
                 password="", modded=True, ts=None):
    """Write DedicatedServerConfig.json into dest_dir, merging + backing-up any existing one.
    Returns (path, backup_or_None). Raises ValueError on invalid ports."""
    err = validate_ports(game_port, query_port)
    if err:
        raise ValueError(err)
    os.makedirs(dest_dir, exist_ok=True)
    path = os.path.join(dest_dir, CONFIG_NAME)
    existing, backup = None, None
    if os.path.exists(path):
        try:
            with open(path, encoding="utf-8") as f:
                existing = json.load(f)
        except (OSError, ValueError):
            existing = None
        backup = path + ".bak-" + (ts or time.strftime("%Y%m%d-%H%M%S"))
        try:
            with open(path, "rb") as a, open(backup, "wb") as b:
                b.write(a.read())
        except OSError:
            backup = None
    cfg = build_config(existing, game_port, query_port, server_name, max_players, password, modded)
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(cfg, f, indent=2)
    os.replace(tmp, path)
    return path, backup
