using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using NuclearOption.Networking;
using UnityEngine;

namespace NukeStats
{
    // 1.9.4 (owner 2026-10-04): teamkill watch list.
    //  - Teamkill.WatchList holds the SteamIDs of players with 3 or more teamkill entries in the Moderation ban log,
    //    counted across both servers. The bot keeps it up to date with setcfg; taking entries out of the ban log takes
    //    a player off the list again.
    //  - A listed player's first teamkill in a match skips the eject warning: they are kicked straight away with no
    //    message (they could not read it) and their in-game rank goes to 0, the same as a normal second offence. Their
    //    next teamkill that match is the normal third offence, a ban. That part lives in TkTick (NukeStatsPlugin.cs).
    //  - A listed player gets one private red warning a few seconds after joining the server.
    internal static class TkWatch
    {
        internal static ConfigEntry<string> WatchList;

        const string Warning = "<color=#FF5555>!WARNING!  You have teamkilled too many players. Another team kill may result in a ban !WARNING!</color>";
        const float WarnDelaySec = 15f;           // after the player is first seen, so it lands after the bot's welcome whisper
        const float GoneSec = 60f;                // not seen for this long = left the server; a mission reload is shorter

        internal static void Initialize(ConfigFile config)
        {
            WatchList = config.Bind("Teamkill", "WatchList", "",
                "SteamIDs on the teamkill watch list, comma separated. Kept up to date by the bot: players with 3 or more "
                + "teamkill entries in the Moderation ban log across both servers. A listed player's first teamkill in a match "
                + "is a kick with the in-game rank reset to 0 instead of the eject warning, the next one that match is a ban, "
                + "and they get a private warning when they join.");
        }

        // ---------------------------------------------------------------- the list
        static string _parsedFrom;
        static readonly HashSet<string> _sids = new HashSet<string>(StringComparer.Ordinal);

        static void Refresh()
        {
            string v = WatchList != null ? (WatchList.Value ?? "") : "";
            if (v == _parsedFrom) return;
            _parsedFrom = v;
            _sids.Clear();
            foreach (var part in v.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string s = part.Trim();
                if (s.Length >= 6 && s.Length <= 20 && IsDigits(s)) _sids.Add(s);
            }
            NukeStatsPlugin.Log?.LogInfo("[tkwatch] watch list: " + _sids.Count + " player(s)");
        }

        static bool IsDigits(string s)
        {
            foreach (char ch in s) if (ch < '0' || ch > '9') return false;
            return true;
        }

        internal static bool IsListed(string sid)
        {
            if (string.IsNullOrEmpty(sid)) return false;
            Refresh();
            return _sids.Contains(sid);
        }

        // ---------------------------------------------------------------- join warning
        static readonly Dictionary<string, float> _seen = new Dictionary<string, float>(StringComparer.Ordinal);      // sid -> last seen
        static readonly Dictionary<string, float> _pending = new Dictionary<string, float>(StringComparer.Ordinal);   // sid -> warn at
        static readonly HashSet<string> _warned = new HashSet<string>(StringComparer.Ordinal);                          // warned this stay
        static float _next;

        // Called every frame from PeriodicTick (main thread); does its work once a second.
        internal static void Tick()
        {
            try
            {
                float now = Time.realtimeSinceStartup;
                if (now < _next) return;
                _next = now + 1f;
                Refresh();

                var bySid = new Dictionary<string, Player>(StringComparer.Ordinal);
                foreach (var p in NukeStatsPlugin.Humans())
                {
                    string sid = NukeStatsPlugin.Sid(p);
                    if (string.IsNullOrEmpty(sid) || sid == "0") continue;
                    bySid[sid] = p;
                    _seen[sid] = now;
                    if (_sids.Contains(sid) && !_warned.Contains(sid) && !_pending.ContainsKey(sid))
                        _pending[sid] = now + WarnDelaySec;
                }

                List<string> gone = null;
                foreach (var kv in _seen) if (now - kv.Value > GoneSec) (gone ??= new List<string>()).Add(kv.Key);
                if (gone != null)
                    foreach (var sid in gone) { _seen.Remove(sid); _warned.Remove(sid); _pending.Remove(sid); }

                List<string> due = null;
                foreach (var kv in _pending) if (now >= kv.Value) (due ??= new List<string>()).Add(kv.Key);
                if (due == null) return;
                foreach (var sid in due)
                {
                    _pending.Remove(sid);
                    if (!_sids.Contains(sid)) continue;                     // taken off the list meanwhile
                    if (!bySid.TryGetValue(sid, out var p)) continue;       // not on right now: re-queued when next seen
                    _warned.Add(sid);
                    NukeStatsPlugin.Instance?.TellPlayer(p, Warning);
                    NukeStatsPlugin.Log?.LogInfo("[tkwatch] warned " + NukeStatsPlugin.RawNameOf(p) + " on joining");
                }
            }
            catch (Exception e) { NukeStatsPlugin.Log?.LogError("[tkwatch] tick: " + e); }
        }
    }
}
