using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using NuclearOption.Networking;
using UnityEngine;

namespace NukeStats
{
    // 1.9.1 (owner 2026-09-28): automatic high-ping kicker.
    //  - ON while the server has MORE than PvEPlayers (12, i.e. 13+) on a co-op mission, or more than PvPPlayers
    //    (14, i.e. 15+) on a PvP mission; OFF (and every pending warning dropped) once it falls back to that number.
    //  - Ping = the plugin's own per-player RTT (the figure the web panel shows), averaged over AverageSeconds (30):
    //    consistent high ping is kicked, a spike is not (Steam already smooths its reading, and one sample counts at
    //    most SampleCapMs toward the average).
    //  - Highest average first, and only as many as it takes to bring the server back to the threshold; admins exempt.
    //    A player who has just joined holds a place until their average is known, so a higher ping arriving is never
    //    passed over for a lower one already on.
    //  - First time: a private red warning, then a kick WarnSeconds (60) later unless the average has dropped, with a
    //    chat line naming them and their ping. A plain disconnect, so they can come straight back.
    //  - Rejoin after a kick: ONE check at WarnSeconds after joining; still high = kicked again (no new warning).
    //    After that check they are treated like everyone else (warning first). The third kick of the mission is the
    //    game's own mission kick, which keeps them out until the mission changes.
    //  - Strike counts reset when the mission changes.
    internal static class PingKick
    {
        internal static ConfigEntry<bool>  Enabled;
        internal static ConfigEntry<int>   PvEPlayers;
        internal static ConfigEntry<int>   PvPPlayers;
        internal static ConfigEntry<int>   MaxPingMs;
        internal static ConfigEntry<int>   AverageSeconds;
        internal static ConfigEntry<int>   WarnSeconds;

        const float SampleCapMs = 1000f;          // a reading above this counts as this, so one freeze cannot dominate the average
        const float MinCoverage = 0.8f;           // the window must be at least 80% covered before anyone is judged
        const float KickedGoneSec = 10f;          // a kicked player still listed this soon after is gone, not a rejoiner

        internal static void Initialize(ConfigFile config)
        {
            Enabled = config.Bind("PingKick", "Enabled", true,
                "Automatically kick players whose AVERAGE ping stays at or above MaxPingMs while the server is busy. "
                + "Private red warning first, kicked 60 s later unless it drops; a third kick in one mission keeps them out "
                + "until the mission changes. Admins are exempt.");
            PvEPlayers = config.Bind("PingKick", "PvEPlayers", 12,
                new ConfigDescription("Co-op (PvE) missions: the kicker is on while MORE than this many players are on "
                    + "(12 = on at 13) and never takes the server below it.", new AcceptableValueRange<int>(1, 64)));
            PvPPlayers = config.Bind("PingKick", "PvPPlayers", 14,
                new ConfigDescription("PvP missions: the kicker is on while MORE than this many players are on "
                    + "(14 = on at 15) and never takes the server below it.", new AcceptableValueRange<int>(1, 64)));
            MaxPingMs = config.Bind("PingKick", "MaxPingMs", 250,
                new ConfigDescription("Average ping (ms) at or above which a player counts as high ping.", new AcceptableValueRange<int>(50, 2000)));
            AverageSeconds = config.Bind("PingKick", "AverageSeconds", 30,
                new ConfigDescription("Seconds of ping readings averaged before a player is judged (spikes do not count).", new AcceptableValueRange<int>(10, 120)));
            WarnSeconds = config.Bind("PingKick", "WarnSeconds", 60,
                new ConfigDescription("Seconds between the private warning and the kick (and after a rejoin).", new AcceptableValueRange<int>(10, 300)));
        }

        // ---------------------------------------------------------------- ping history (fed by NoteRttSample)
        sealed class Samples { internal readonly Queue<KeyValuePair<float, float>> Q = new Queue<KeyValuePair<float, float>>(); }
        static readonly Dictionary<string, Samples> _samples = new Dictionary<string, Samples>();

        static float Window() => Mathf.Max(10, AverageSeconds != null ? AverageSeconds.Value : 30);

        internal static void OnSample(string sid, float ms)
        {
            try
            {
                if (string.IsNullOrEmpty(sid)) return;
                if (!_samples.TryGetValue(sid, out var s)) { s = new Samples(); _samples[sid] = s; }
                float now = Time.realtimeSinceStartup;
                s.Q.Enqueue(new KeyValuePair<float, float>(now, Mathf.Min(ms, SampleCapMs)));
                float keep = Window() + 5f;
                while (s.Q.Count > 0 && now - s.Q.Peek().Key > keep) s.Q.Dequeue();
            }
            catch { }
        }

        // average over the window, or -1 when there is not enough of the window to judge yet
        static float Average(string sid, float since)
        {
            if (!_samples.TryGetValue(sid, out var s) || s.Q.Count == 0) return -1f;
            float now = Time.realtimeSinceStartup;
            float win = Window();
            float from = Mathf.Max(now - win, since);
            if (now - from < win * MinCoverage) return -1f;          // joined too recently to have a full window
            float sum = 0f, first = -1f; int n = 0;
            foreach (var kv in s.Q)
            {
                if (kv.Key < from) continue;
                if (first < 0f) first = kv.Key;
                sum += kv.Value; n++;
            }
            if (n < 5 || first < 0f || now - first < win * MinCoverage * 0.8f) return -1f;
            return sum / n;
        }

        // whatever readings there are since joining (for holding a place while the full average builds), -1 if none
        static float EarlyAverage(string sid, float since)
        {
            if (!_samples.TryGetValue(sid, out var s)) return -1f;
            float from = Mathf.Max(Time.realtimeSinceStartup - Window(), since);
            float sum = 0f; int n = 0;
            foreach (var kv in s.Q) if (kv.Key >= from) { sum += kv.Value; n++; }
            return n > 0 ? sum / n : -1f;
        }

        // ---------------------------------------------------------------- per-mission state
        static object _mission;
        static readonly Dictionary<string, int> _strikes = new Dictionary<string, int>();        // kicks this mission
        static readonly Dictionary<string, float> _warnedAt = new Dictionary<string, float>();   // warned, kick due at +WarnSeconds
        static readonly Dictionary<string, float> _joinedAt = new Dictionary<string, float>();   // present players: first seen
        static readonly HashSet<string> _present = new HashSet<string>();
        static readonly HashSet<string> _rejoinCheck = new HashSet<string>();                   // kicked before, one check pending
        static readonly Dictionary<string, float> _kickedAt = new Dictionary<string, float>();   // last kick time
        static readonly Dictionary<string, float> _toldAt = new Dictionary<string, float>();     // last red warning sent
        static float _next;
        static bool _wasOn;

        static void Reset(string why)
        {
            if (_strikes.Count > 0 || _warnedAt.Count > 0) NukeStatsPlugin.Log?.LogInfo("[pingkick] reset (" + why + ")");
            _strikes.Clear(); _warnedAt.Clear(); _joinedAt.Clear(); _present.Clear(); _rejoinCheck.Clear(); _kickedAt.Clear(); _toldAt.Clear();
            _wasOn = false;
        }

        static int Threshold(out bool pvp)
        {
            pvp = false;
            try { pvp = NukeStatsPlugin.IsPvpMission(MissionManager.CurrentMission); } catch { }
            return pvp ? (PvPPlayers != null ? PvPPlayers.Value : 14) : (PvEPlayers != null ? PvEPlayers.Value : 12);
        }

        static int Ms(float avg) => Mathf.RoundToInt(avg);

        static float Joined(string sid) => _joinedAt.TryGetValue(sid, out var j) ? j : 0f;

        // Called every frame from PeriodicTick (main thread); does its work once a second.
        internal static void Tick()
        {
            try
            {
                float now = Time.realtimeSinceStartup;
                if (now < _next) return;
                _next = now + 1f;

                object mission = MissionManager.CurrentMission;
                if (!ReferenceEquals(mission, _mission)) { Reset("new mission"); _mission = mission; }
                if (mission == null) return;

                var humans = NukeStatsPlugin.Humans();
                var bySid = new Dictionary<string, Player>();
                foreach (var p in humans)
                {
                    string sid = NukeStatsPlugin.Sid(p);
                    if (string.IsNullOrEmpty(sid) || sid == "0") continue;
                    if (_kickedAt.TryGetValue(sid, out var kt) && now - kt < KickedGoneSec) continue;   // on the way out
                    bySid[sid] = p;
                }
                // joins / leaves: a player who leaves loses their pending warning, rejoin check and ping history
                foreach (var kv in bySid)
                    if (_present.Add(kv.Key))
                    {
                        _joinedAt[kv.Key] = now;
                        if (_strikes.TryGetValue(kv.Key, out int k) && k >= 1 && k < 3) _rejoinCheck.Add(kv.Key);
                    }
                var gone = new List<string>();
                foreach (var sid in _present) if (!bySid.ContainsKey(sid)) gone.Add(sid);
                foreach (var sid in gone)
                {
                    _present.Remove(sid); _joinedAt.Remove(sid); _warnedAt.Remove(sid); _rejoinCheck.Remove(sid); _samples.Remove(sid); _toldAt.Remove(sid);
                }

                if (Enabled == null || !Enabled.Value) { _warnedAt.Clear(); _rejoinCheck.Clear(); _wasOn = false; return; }

                // a player the game has mission-kicked for high ping must never be let back in this mission. The first
                // strike-3 kick already put them on the game's list, so this is a plain disconnect (re-listing them here
                // could land after the game cleared the list for the next mission and lock them out of that one too)
                var redrop = new List<string>();
                foreach (var kv in bySid)
                    if (_strikes.TryGetValue(kv.Key, out int k) && k >= 3 && !NukeStatsPlugin.IsAdmin(kv.Value)) redrop.Add(kv.Key);
                foreach (var sid in redrop)
                {
                    NukeStatsPlugin.Log?.LogInfo("[pingkick] " + NukeStatsPlugin.RawNameOf(bySid[sid]) + " is out for the rest of the mission - removed again");
                    Drop(bySid[sid], sid, missionKick: false);
                    _kickedAt[sid] = now;
                    bySid.Remove(sid);                           // gone: not counted toward the threshold
                }

                int count = bySid.Count;
                int limit = Threshold(out bool pvp);
                bool on = count > limit;
                if (on != _wasOn)
                {
                    NukeStatsPlugin.Log?.LogInfo("[pingkick] " + (on ? "ON" : "OFF") + ": " + count + " players on a "
                        + (pvp ? "PvP" : "co-op") + " mission (on above " + limit + ")");
                    _wasOn = on;
                }

                float max = MaxPingMs != null ? MaxPingMs.Value : 250;
                float warn = WarnSeconds != null ? WarnSeconds.Value : 60;
                float win = Window();

                if (!on)
                {
                    // the rejoin check is ONE look at WarnSeconds after joining; a rejoiner whose moment passes while the
                    // server is not over the line just goes back to the normal warning path
                    _warnedAt.Clear();
                    var passed = new List<string>();
                    foreach (var sid in _rejoinCheck) if (now - Joined(sid) >= warn) passed.Add(sid);
                    foreach (var sid in passed) _rejoinCheck.Remove(sid);
                    return;
                }
                int room = count - limit;                        // how many it may remove before the server is back at the line

                // 1) the line for a kick, highest average first: warned players and rejoiners waiting on their check.
                //    One whose time is up is kicked; one still on its clock keeps its place, so a lower ping whose time
                //    ran out first never takes the room from a higher one.
                var line = new List<KeyValuePair<string, float>>();
                var isDue = new HashSet<string>();
                var drop = new List<string>();
                foreach (var kv in _warnedAt)
                {
                    string sid = kv.Key;
                    bool due = now - kv.Value >= warn;
                    if (!bySid.TryGetValue(sid, out var p) || NukeStatsPlugin.IsAdmin(p)) { drop.Add(sid); continue; }
                    float avg = Average(sid, Joined(sid));
                    if (due)
                    {
                        if (avg < 0f) { drop.Add(sid); continue; }          // lost the readings (should not happen) - never kick blind
                        if (avg < max)
                        {
                            NukeStatsPlugin.Log?.LogInfo("[pingkick] " + NukeStatsPlugin.RawNameOf(p) + " dropped to " + Ms(avg) + "ms - not kicked");
                            drop.Add(sid); continue;
                        }
                        isDue.Add(sid);
                    }
                    else if (avg < max) continue;                // under the line right now: holds no place ahead of anyone
                    line.Add(new KeyValuePair<string, float>(sid, avg));
                }
                foreach (var sid in drop) _warnedAt.Remove(sid);
                var settled = new List<string>();
                foreach (var sid in _rejoinCheck)
                {
                    bool due = now - Joined(sid) >= warn;
                    if (!bySid.TryGetValue(sid, out var p) || NukeStatsPlugin.IsAdmin(p)) { settled.Add(sid); continue; }
                    // the no-warning kick stays close to the 60 s mark: no readings, or still queued behind a higher ping
                    // AverageSeconds later, and they go back to the normal warning path instead
                    if (now - Joined(sid) >= warn + win) { settled.Add(sid); continue; }
                    float avg = Average(sid, Joined(sid));
                    if (avg < 0f) continue;
                    if (avg < max)
                    {
                        if (due)
                        {
                            NukeStatsPlugin.Log?.LogInfo("[pingkick] " + NukeStatsPlugin.RawNameOf(p) + " rejoined at " + Ms(avg) + "ms - stays");
                            settled.Add(sid);
                        }
                        continue;
                    }
                    if (due) isDue.Add(sid);
                    line.Add(new KeyValuePair<string, float>(sid, avg));
                }
                foreach (var sid in settled) _rejoinCheck.Remove(sid);
                line.Sort((x, y) => y.Value.CompareTo(x.Value));
                int places = room;
                foreach (var e in line)
                {
                    if (places <= 0) break;                      // the rest wait; the server is back at the line either way
                    places--;
                    if (!isDue.Contains(e.Key)) continue;        // still on its clock: keeps its place
                    _warnedAt.Remove(e.Key);
                    _rejoinCheck.Remove(e.Key);
                    KickFor(bySid[e.Key], e.Key, e.Value);
                    room--;
                }

                // 2) new warnings: highest average first, only as many as could still be removed. Places are held for
                //    players already warned and for anyone whose average is not known yet (a rejoiner before their check,
                //    a new arrival whose first readings are high or not in yet), so a higher ping is never passed over. A
                //    rejoiner whose average is known competes for a place like everyone else, without a warning: their one
                //    check decides.
                int held = _warnedAt.Count;
                var cands = new List<KeyValuePair<string, float>>();
                foreach (var kv in bySid)
                {
                    string sid = kv.Key;
                    if (_warnedAt.ContainsKey(sid)) continue;
                    if (_kickedAt.TryGetValue(sid, out var kt) && now - kt < KickedGoneSec) continue;   // kicked this tick
                    if (NukeStatsPlugin.IsAdmin(kv.Value)) continue;
                    float joined = Joined(sid);
                    float avg = Average(sid, joined);
                    if (avg < 0f)
                    {
                        if (_rejoinCheck.Contains(sid)) held++;
                        else if (now - joined < win + 10f)
                        {
                            float early = EarlyAverage(sid, joined);
                            if (early < 0f || early >= max) held++;
                        }
                        continue;
                    }
                    if (avg >= max) cands.Add(new KeyValuePair<string, float>(sid, avg));
                }
                int slots = room - held;
                if (slots <= 0 || cands.Count == 0) return;
                cands.Sort((x, y) => y.Value.CompareTo(x.Value));
                for (int i = 0; i < cands.Count && slots > 0; i++)
                {
                    string sid = cands[i].Key;
                    slots--;
                    if (_rejoinCheck.Contains(sid)) continue;    // their place; the rejoin check decides, no warning
                    var p = bySid[sid];
                    if (_toldAt.TryGetValue(sid, out var told) && now - told < warn)
                    {
                        // warned moments ago and the count dipped to the line in between: same clock, no repeat message
                        _warnedAt[sid] = told;
                        continue;
                    }
                    _warnedAt[sid] = now;
                    _toldAt[sid] = now;
                    NukeStatsPlugin.Trace("PingKick.Warn");
                    NukeStatsPlugin.Instance?.TellPlayer(p, "<color=#FF5555>High Ping Detected: avg. " + Ms(cands[i].Value)
                        + "ms - You will be kicked in " + Mathf.RoundToInt(warn) + " seconds unless it drops.</color>");
                    NukeStatsPlugin.Log?.LogInfo("[pingkick] warned " + NukeStatsPlugin.RawNameOf(p) + " (avg " + Ms(cands[i].Value)
                        + "ms, " + count + " on, limit " + limit + ")");
                }
            }
            catch (Exception e) { NukeStatsPlugin.Log?.LogError("[pingkick] tick: " + e); }
        }

        static void KickFor(Player p, string sid, float avg)
        {
            _strikes.TryGetValue(sid, out int k);
            k++;
            _strikes[sid] = k;
            _kickedAt[sid] = Time.realtimeSinceStartup;          // also keeps them out of the rest of this tick
            string name = NukeStatsPlugin.SafeText(NukeStatsPlugin.RawNameOf(p));
            NukeStatsPlugin.Trace("PingKick.Kick");
            NukeStatsPlugin.Instance?.BroadcastAll(name + " was kicked for high ping (" + Ms(avg) + "ms).");
            NukeStatsPlugin.Log?.LogInfo("[pingkick] kicked " + NukeStatsPlugin.RawNameOf(p) + " (avg " + Ms(avg) + "ms, strike " + k
                + (k >= 3 ? ", out for the rest of the mission" : "") + ")");
            Drop(p, sid, missionKick: k >= 3);
        }

        // strikes 1-2: a plain disconnect (no kick list, they can rejoin at once); strike 3: the game's own mission kick
        // (the vote-kick path), which refuses them until the mission changes and is cleared by the game itself.
        static void Drop(Player p, string sid, bool missionKick)
        {
            try
            {
                var conn = p != null ? p.Owner : null;
                if (conn == null) return;
                if (missionKick)
                {
                    var nm = NetworkManagerNuclearOption.i;
                    var auth = nm != null ? nm.Authenticator : null;
                    // OnMissionKick is overloaded (INetworkPlayer / CSteamID); the CSteamID assembly is not referenced,
                    // so the connection overload is bound by reflection once.
                    if (auth != null)
                    {
                        if (_missionKick == null)
                            _missionKick = HarmonyLib.AccessTools.Method(auth.GetType(), "OnMissionKick", new[] { typeof(Mirage.INetworkPlayer) });
                        if (_missionKick != null) _missionKick.Invoke(auth, new object[] { conn });
                        else NukeStatsPlugin.Log?.LogWarning("[pingkick] OnMissionKick(INetworkPlayer) not found - disconnect only");
                    }
                }
                conn.Disconnect();
            }
            catch (Exception e) { NukeStatsPlugin.Log?.LogError("[pingkick] drop: " + e); }
        }
        static System.Reflection.MethodInfo _missionKick;
    }
}
