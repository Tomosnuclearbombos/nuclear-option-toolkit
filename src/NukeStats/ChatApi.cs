// ChatApi.cs: the plugin's and the bot's commands as the game's native "/" chat commands (Nuclear Option 0.34.5+
// modding API).
//
// What players get: type "/" and the commands are in the game's own autocomplete with a short description and their
// arguments, next to the game's built-in /help, /clear, /ping, /kick. The "!" commands keep working.
// How it works:
//   - each "/" command is run through the same handler as its "!" twin (TryHandleChatCommand), so the behaviour is the
//     plugin's own and nothing is duplicated
//   - a command the plugin does not own (the bot's !rank, !points, ...) is handed to the bot as the exact console line
//     the game writes for a typed chat message ("[ChatManager] CmdSendChatMessage ... !rank"), so the bot answers it as
//     if it had been typed, with no bot change
//   - the plugin's [Admin] SteamIds get the game's Admin permission level when they join: they can use the built-in
//     /adminkick and the admin-only commands here, which nobody else sees in the autocomplete
//   - the game's remote command port takes "nukestats <verb> <args>", the same lines as the plugin_cmd files
//     (Chat.RemoteCommand)
//   - every player's server tag is their rank, in its rank colour (SyncTags, Chat.RankServerTag)
// The game shows the command result in the player's own chat. We reply through our usual private lines instead, so the
// result itself is empty.
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using NuclearOption.Chat;
using NuclearOption.ModdingApi;
using NuclearOption.Networking;
using UnityEngine;

namespace NukeStats
{
    internal static class ChatApi
    {
        internal static ConfigEntry<bool> SlashCommands, RankServerTag, RemoteCommand;

        sealed class Cmd
        {
            public string Name, Description; public string[] Aliases; public ChatCommandArgument[] Args;
            public PermissionLevel Level = PermissionLevel.Player;
            public bool BotOnly;   // straight to the bot, even where the plugin has a "!" verb of the same name
        }

        static ChatCommandArgument Text(string name, bool optional = true) => new ChatCommandArgument(name, ChatArgumentType.Text, optional);
        static ChatCommandArgument PlayerArg(string name, bool optional) => new ChatCommandArgument(name, ChatArgumentType.Player, optional);

        // Descriptions: plain words, no dashes (house style for chat text).
        static readonly Cmd[] Commands =
        {
            // the plugin's own
            new Cmd { Name = "spec", Description = "Go to spectate (admins can name a player)", Aliases = new[] { "spectate" }, Args = new[] { PlayerArg("player", true) } },
            new Cmd { Name = "forfeit", Description = "Vote to surrender the match for your team", Aliases = new[] { "f", "ff", "surrender" } },
            new Cmd { Name = "autobalance", Description = "How team autobalance works", Aliases = new[] { "ab" } },
            new Cmd { Name = "swapteam", Description = "Move to the other team if it has fewer players" },
            // the bot's (handed on as a typed chat command)
            // /yes is its own command, not an alias of /y: the game hands an alias over under its main name, so "/yes"
            // would reach the bot as "!y" and the bot's prestige confirm (which reads "!yes") would never fire
            new Cmd { Name = "y", Description = "Yes: agree to a map vote" },
            new Cmd { Name = "yes", Description = "Yes: confirm a /prestige" },
            new Cmd { Name = "n", Description = "No: say no to a vote", Aliases = new[] { "no" } },
            new Cmd { Name = "rank", Description = "Your rank and points" },   // no argument: the bot matches "!rank" exactly
            new Cmd { Name = "ranks", Description = "The rank ladder", Aliases = new[] { "ladder" } },
            new Cmd { Name = "points", Description = "Your lifetime points total" },
            new Cmd { Name = "stats", Description = "Your rank, points, wins and time played", Aliases = new[] { "stat" } },
            new Cmd { Name = "leaderboard", Description = "The top pilots" },
            new Cmd { Name = "prestige", Description = "Start a fresh rank cycle and earn a star" },
            new Cmd { Name = "discord", Description = "How to join the Discord" },
            new Cmd { Name = "notk", Description = "The team kill rules" },
            new Cmd { Name = "votemap", Description = "Start a vote to change the map" },
            // a whole number, not a fixed choice list: a ballot can hold more options than any list here, and the game
            // rejects a value that is not in a choice list
            new Cmd { Name = "vote", Description = "Vote in the map ballot (or just type the number)",
                      Args = new[] { new ChatCommandArgument("option", ChatArgumentType.Int, false) } },
            new Cmd { Name = "why", Description = "Where your recent points came from" },
            new Cmd { Name = "balance", Description = "How team balancing works", BotOnly = true },
            // admins only (the plugin checks [Admin] SteamIds again itself)
            new Cmd { Name = "move", Description = "Admin: move a player to a team", Level = PermissionLevel.Admin,
                      Args = new[] { PlayerArg("player", false), Text("team", false) } },
            new Cmd { Name = "balancenow", Description = "Admin: balance the teams now", Level = PermissionLevel.Admin },   // "/balance" is the bot's public explainer
            new Cmd { Name = "setrank", Description = "Admin: set a player's in game rank", Level = PermissionLevel.Admin,
                      Args = new[] { PlayerArg("player", false), Text("rank", false) } },
            new Cmd { Name = "setfunds", Description = "Admin: set a player's funds", Level = PermissionLevel.Admin,
                      Args = new[] { PlayerArg("player", false), Text("amount", false) } },
            new Cmd { Name = "addfunds", Description = "Admin: add to a player's funds", Level = PermissionLevel.Admin,
                      Args = new[] { PlayerArg("player", false), Text("amount", false) } },
            new Cmd { Name = "forceteamswap", Description = "Admin: move a player to the other team now", Level = PermissionLevel.Admin,
                      Args = new[] { PlayerArg("player", false) } },
        };

        // From Awake: binds the [Chat] switches, then registers what they turn on. SlashCommands and RemoteCommand are
        // read once here (a change needs a restart); RankServerTag is read on every SyncTags pass (live).
        internal static void Initialize(ConfigFile config)
        {
            SlashCommands = config.Bind("Chat", "SlashCommands", true,
                "Register the plugin's and the bot's commands as the game's own \"/\" chat commands (the game's \"/\" list "
                + "shows them with a short description; the \"!\" commands keep working). Admin commands are only listed "
                + "for [Admin] SteamIds, who also get the game's Admin permission level when they join. Needs a restart.");
            RankServerTag = config.Bind("Chat", "RankServerTag", true,
                "Set each player's server tag to their rank shorthand, in its rank colour (from the bot's rank ladder; no "
                + "ladder = no tag). Players see it when they turn server tags on in the game's chat settings. OFF = the "
                + "plugin leaves server tags alone (tags it already set stay until the player rejoins). Live.");
            RemoteCommand = config.Bind("Chat", "RemoteCommand", true,
                "Register the \"nukestats\" command on the game's remote command port: \"nukestats <verb> <args>\" runs the "
                + "same line as a plugin_cmd file (setcfg, ban, kick, ...), so the bot can send plugin commands without "
                + "a file drop. Anyone who can reach that port can run every plugin command, and in the standard setup the "
                + "relay port is reachable from the internet. Turn this off if you don't want that: the bot then uses "
                + "plugin_cmd files. Needs a restart.");
            Register();
        }

        static bool _done;
        internal static int Registered, Failed;
        // the prefix for on-screen command hints: "/" once our commands are in the game's "/" list, else "!"
        internal static string P => Registered > 0 ? "/" : "!";

        static void Register()
        {
            if (_done) return;
            _done = true;
            // [Admin] SteamIds get the game's Admin level at join whether or not the "/" commands are registered
            try { PlayerHandle.PlayerJoined += OnJoined; } catch (Exception e) { NukeStatsPlugin.Log?.LogError("[chatapi] PlayerJoined: " + e.Message); }
            if (SlashCommands == null || SlashCommands.Value)
            {
                foreach (var c in Commands)
                {
                    try
                    {
                        string verb = c.Name;
                        bool botOnly = c.BotOnly;
                        bool ok = ChatCommands.RegisterServerCommand(c.Name, c.Description, req => Run(req, verb, botOnly), c.Args, c.Level, c.Aliases);
                        if (ok) Registered++; else { Failed++; NukeStatsPlugin.Log?.LogWarning("[chatapi] /" + c.Name + " not registered (name taken)"); }
                    }
                    catch (Exception e) { Failed++; NukeStatsPlugin.Log?.LogError("[chatapi] /" + c.Name + ": " + e.Message); }
                }
                NukeStatsPlugin.Log?.LogInfo($"[chatapi] {Registered} slash command(s) registered, {Failed} failed");
            }
            else NukeStatsPlugin.Log?.LogInfo("[chatapi] slash commands off (Chat.SlashCommands)");
            // The game's remote command port now also takes "nukestats" followed by the parts of one plugin command line:
            // ["setcfg", "Points.Enabled", "true"] runs exactly what a plugin_cmd file holding "setcfg|Points.Enabled|true"
            // runs. It is queued for the game's main thread and answered at once, so a busy or loading main thread never
            // holds the port up.
            if (RemoteCommand == null || RemoteCommand.Value)
            {
                try
                {
                    if (DedicatedServerCommands.Register("nukestats", Remote)) NukeStatsPlugin.Log?.LogInfo("[chatapi] remote command 'nukestats' registered");
                    else NukeStatsPlugin.Log?.LogWarning("[chatapi] remote command 'nukestats' not registered (name taken)");
                }
                catch (Exception e) { NukeStatsPlugin.Log?.LogError("[chatapi] remote: " + e.Message); }
            }
            else NukeStatsPlugin.Log?.LogInfo("[chatapi] remote command off (Chat.RemoteCommand)");
        }

        // Rank tags. Each player's server tag is their rank shorthand in its rank colour, from the bot's plugin_ranks.txt.
        // The game draws "[tag] Name" in chat and on the leaderboard (or everywhere) for players who turn server tags on
        // in their chat settings; it is off by default. Set only when it changes, so the game sends it once per change.
        static float _nextTags;
        static bool _tagErrLogged;
        internal static void SyncTags(float now)
        {
            if (RankServerTag == null || !RankServerTag.Value) return;
            if (now < _nextTags) return;
            _nextTags = now + 5f;
            try
            {
                foreach (var p in NukeStatsPlugin.Humans())
                {
                    if (p == null) continue;
                    string want = NukeStatsPlugin.RankTagFor(p) ?? "";
                    if ((p.ServerTag ?? "") == want) continue;
                    p.SetServerTag(want);
                    NukeStatsPlugin.Log?.LogInfo($"[chatapi] tag {NukeStatsPlugin.RawNameOf(p)} ({NukeStatsPlugin.Sid(p)}): {want}");
                }
            }
            catch (Exception e)
            {
                if (_tagErrLogged) return;
                _tagErrLogged = true;
                NukeStatsPlugin.Log?.LogError("[chatapi] tags: " + e);
            }
        }

        static CommandResponse Remote(IReadOnlyList<string> args)
        {
            if (args == null || args.Count == 0 || string.IsNullOrWhiteSpace(args[0]))
                return CommandResponse.Create(StatusCode.BadArguments, "usage: nukestats <verb> [args...]");
            var parts = new string[args.Count];
            for (int i = 0; i < args.Count; i++) parts[i] = (args[i] ?? "").Replace('\r', ' ').Replace('\n', ' ');
            string line = string.Join("|", parts);
            DedicatedServerCommands.RunOnMainThread(() => NukeStatsPlugin.ExecCommand(line));
            return CommandResponse.Create(StatusCode.Success, "queued " + parts[0].Trim().ToLowerInvariant());
        }

        static ChatCommandResult Run(ChatCommandRequest req, string verb, bool botOnly)
        {
            try
            {
                Player p = req.Sender.GetInternalPlayer();
                if (p == null) return ChatCommandResult.Ok(null);
                // The game's checked args: a player picked from the list comes as "[@SteamID]", which the plugin's name
                // lookup (Resolve) takes as that exact player. Control characters go, as for typed chat, so a crafted
                // argument can never forge a second console line for the bot.
                string raw = req.Args != null && req.Args.Count > 0 ? string.Join(" ", req.Args) : (req.RawArgs ?? "");
                raw = NukeStatsPlugin.StripControlChars(raw ?? "").Trim();
                string text = "!" + verb + (raw.Length > 0 ? " " + raw : "");
                var inst = NukeStatsPlugin.Instance;
                ChatManager cm = null; try { cm = NetworkSceneSingleton<ChatManager>.i; } catch { }
                if (!botOnly && !ReferenceEquals(inst, null) && inst.TryHandleChatCommand(cm, p, text))
                {
                    NukeStatsPlugin.Log?.LogInfo($"[chatapi] /{verb} by {NukeStatsPlugin.RawNameOf(p)} -> plugin");
                    return ChatCommandResult.Ok(null);
                }
                string sid = NukeStatsPlugin.Sid(p);
                // The bot reads chat from the game's own console line; this is that line (same time prefix too), for
                // the command typed with "!".
                Debug.Log($"{Time.unscaledTime:0.000}: [ChatManager] CmdSendChatMessage allChat:True connection(SteamConnection({sid})) {sid} {text}");
                NukeStatsPlugin.Log?.LogInfo($"[chatapi] /{verb} by {NukeStatsPlugin.RawNameOf(p)} -> bot");
                return ChatCommandResult.Ok(null);
            }
            catch (Exception e)
            {
                NukeStatsPlugin.Log?.LogError("[chatapi] /" + verb + ": " + e);
                return ChatCommandResult.Ok(null);
            }
        }

        static void OnJoined(PlayerHandle h)
        {
            try
            {
                Player p = h.GetInternalPlayer();
                if (p == null || !NukeStatsPlugin.IsAdmin(p)) return;
                if (h.SetPermissionLevel(PermissionLevel.Admin))
                    NukeStatsPlugin.Log?.LogInfo($"[chatapi] {NukeStatsPlugin.RawNameOf(p)} is an admin: game permission Admin");
            }
            catch (Exception e) { NukeStatsPlugin.Log?.LogError("[chatapi] OnJoined: " + e.Message); }
        }
    }
}
