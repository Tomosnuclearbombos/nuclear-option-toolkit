using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using Mirage;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;
using UnityEngine.Events;

namespace NukeStats
{
    // 2026-10-15 (owner: "try that approach on the test server and be thorough"; no extra daily restarts). Both servers'
    // managed heap grows all day (S2 146 MB after the 05:00 restart, about 960 MB by the next morning) and a bigger heap
    // means longer garbage-collection pauses. The game does a full clean-up at every mission change (empty scene, its
    // registries cleared, a forced full collection, then the log line "MonoHeap: used / heap MB (After CLEANUP)"), but a few
    // long-lived references still point into each ended mission. One kept reference to one dead unit keeps its old FactionHQ,
    // and through it most of that mission, so every mission change leaves a few MB to tens of MB behind for good.
    //
    // Measured on the test server (10 missions in one process): our own PerfExtra._vaporOff and
    // ObjectiveThrottle._next/_before kept 72 MB after 10 missions (about 8 MB more per change); the game's Carrier Fleet
    // Breakthrough objectives stay hooked on the server's spawn event for the life of the process (8.9 MB per CFB played).
    // Releasing them after each clean-up kept the floor at 154 MB instead of 183 after four missions.
    //
    // WHEN: once per mission change, right after the game's own GameManager.ResetGame. On the dedicated server that runs
    // after the empty scene has loaded (every object of the ended mission is destroyed by then) and right before the
    // game's forced full collection, so what is let go here is freed by the game's own collection and its "After CLEANUP"
    // line shows the result. No mission runs at that moment (MissionManager.Runner is null; checked, never done otherwise),
    // so nothing can ask for what is released. The hook is a postfix on a plain static method of a plain class (never a
    // generic type's method, see PatchSafety).
    // Stand-in: only if that hook has never run in this process (it runs at start-up already), the once-a-second tick runs
    // the pass at the first tick of each new mission (a new runner seen; the last one is only held weakly). The runner is
    // gone for only 2-6 s between missions on the test server and the plugin's tick hardly runs while no mission scene is
    // loaded, so "no runner for N seconds" was not a reliable trigger. Every part only lets go of what is destroyed, what
    // belongs to no current mission or the plugin's own, and the parts that would touch shared game lists are skipped while
    // a mission runs, so the stand-in pass is safe inside the new mission too.
    //
    // WHAT (each fix its own switch in [MemHygiene], all on, read at every pass so a switch takes effect at the next
    // mission change; a count per fix on the perf line, "mem_hygiene", and one log line the first time each fix acts):
    //  ClearObjectiveThrottle  our destroy-check schedule (ObjectiveThrottle) forgets the ended mission's objectives.
    //  DropDeadVapour          our wing-vapour list (PerfExtra) forgets aircraft that no longer exist.
    //  UnhookEndedObjectives   objectives of ended missions are taken off the server's spawn event (the game never does
    //                          it for an objective a mission stopped, nor for any objective still running at the end).
    //  DropOldMissionListeners destroyed MissionManagers are taken off the host client's disconnect event (the game adds
    //                          one every mission and removes it only when the host itself disconnects).
    //  ClearPluginMissionRefs  the plugin's own "last mission seen" references and small per-mission lists.
    //  ClearGameScratch        the game's static scratch lists, which it empties at the start of their next use anyway.
    //  ClearNullMissionHQs     the game's empty placeholder mission (Mission.NullMission) lets go of the ended mission's
    //                          FactionHQs, which the game writes into it at every mission end and kept for a whole mission.
    // Every one of these is either our own bookkeeping or something the game rebuilds before it reads it again, so no
    // gameplay changes. Nothing that belongs to a running mission is touched.
    internal static class MemHygiene
    {
        internal static ConfigEntry<bool> ClearObjectiveThrottle;
        internal static ConfigEntry<bool> DropDeadVapour;
        internal static ConfigEntry<bool> UnhookEndedObjectives;
        internal static ConfigEntry<bool> DropOldMissionListeners;
        internal static ConfigEntry<bool> ClearPluginMissionRefs;
        internal static ConfigEntry<bool> ClearGameScratch;
        internal static ConfigEntry<bool> ClearNullMissionHQs;

        // Stands in for a released "last mission seen" reference. It is never the same object as any mission or runner, so
        // every "is this a new mission?" test in the plugin gives exactly the answer the old (ended) mission object gave,
        // without keeping that object alive.
        internal static readonly object Released = new object();

        const int KObj = 0, KVap = 1, KHook = 2, KListen = 3, KRefs = 4, KScratch = 5, KNull = 6, KCount = 7;
        static readonly string[] Keys = { "objthrottle", "vapour", "objective_hooks", "dead_listeners", "plugin_refs", "game_scratch", "null_mission_hqs" };
        static readonly string[] Names = { "ClearObjectiveThrottle", "DropDeadVapour", "UnhookEndedObjectives", "DropOldMissionListeners", "ClearPluginMissionRefs", "ClearGameScratch", "ClearNullMissionHQs" };
        static ConfigEntry<bool>[] Switches() => new[] { ClearObjectiveThrottle, DropDeadVapour, UnhookEndedObjectives, DropOldMissionListeners, ClearPluginMissionRefs, ClearGameScratch, ClearNullMissionHQs };
        static readonly int[] _win = new int[KCount], _tot = new int[KCount];
        static readonly bool[] _said = new bool[KCount], _errSaid = new bool[KCount];
        static int _passesWin, _passesTot;
        static double _passMsMax;
        static bool _inPass, _hookSeen;
        static bool _anyMissionSeen;
        static WeakReference _lastRunner;   // the runner seen last, held weakly (it never keeps an ended mission alive)

        // the game's own floor: "MonoHeap: <used> / <heap> MB (After CLEANUP)", logged right after its forced collection
        static int _floorMb = -1, _floorHeapMb = -1, _prevFloorMb = -1, _floors, _beforeMb = -1;

        internal static void Initialize(ConfigFile config)
        {
            ClearObjectiveThrottle = config.Bind("MemHygiene", "ClearObjectiveThrottle", true,
                "At each mission change, the destroy-units check schedule (Perf.DestroyCheckSeconds) forgets the objectives of the "
                + "mission that ended. They stay \"running\" for ever in the game, so before this they were never forgotten and "
                + "each kept most of its old mission in memory. Takes effect at the next mission change.");
            DropDeadVapour = config.Bind("MemHygiene", "DropDeadVapour", true,
                "At each mission change, the list of wing-vapour effects switched off by Perf.SkipVisualEffects forgets aircraft "
                + "that no longer exist (before this it only did so above 400 entries, and each dead entry kept its old mission "
                + "in memory). Takes effect at the next mission change.");
            UnhookEndedObjectives = config.Bind("MemHygiene", "UnhookEndedObjectives", true,
                "At each mission change, objectives of missions that have ended are taken off the server's spawn event. The game "
                + "never does this for an objective that a mission stopped or that was still running at the end (Carrier Fleet "
                + "Breakthrough's airframe objectives), so they stayed for the life of the server, kept their old mission in "
                + "memory and logged a '[CrashAircraftObjective]' line for player aircraft in every later mission.");
            DropOldMissionListeners = config.Bind("MemHygiene", "DropOldMissionListeners", true,
                "At each mission change, the destroyed mission managers of earlier missions are taken off the host's disconnect "
                + "event. The game adds one every mission and only removes it when the server shuts down; each one kept its "
                + "chat, vote-kick and spawn objects (and the players they list) in memory.");
            ClearPluginMissionRefs = config.Bind("MemHygiene", "ClearPluginMissionRefs", true,
                "At each mission change, the plugin lets go of its own references to the mission that ended (its last-mission "
                + "markers, the airport rotation's runner, the minute's objective timings, dead pilots, "
                + "animators, constraints and particle systems). Every check still sees the next mission as new, exactly as before.");
            ClearGameScratch = config.Bind("MemHygiene", "ClearGameScratch", true,
                "At each mission change, empty the game's own static scratch lists (AI target candidates, the faction player "
                + "list, rearm planning, the vote-kick player history, the sonic-boom list, the server camera, Mirage's sorted "
                + "identity list). The game empties or rebuilds each of them before it reads it again; until then they kept the "
                + "ended mission in memory.");
            ClearNullMissionHQs = config.Bind("MemHygiene", "ClearNullMissionHQs", true,
                "At each mission change, the game's empty placeholder mission lets go of the ended mission's FactionHQs. The game "
                + "writes them into it when it switches to 'no mission', and they stayed there through the whole next mission, "
                + "with most of the ended mission behind them. Only the mission editor reads that field.");
            ResolveGame();
            try { Application.logMessageReceived += OnLog; }
            catch (Exception e) { NukeStatsPlugin.Log?.LogWarning("[mem] game log listener not added (floor_mb stays -1): " + e.Message); }
            ConfigEntry<bool>[] sw = Switches();
            var parts = new List<string>();
            for (int k = 0; k < KCount; k++) parts.Add(Names[k] + (On(sw[k]) ? " on" : " OFF"));
            NukeStatsPlugin.Log?.LogInfo("[mem] mission-change memory clean-up (after the game's ResetGame, before its own collection): "
                + string.Join(", ", parts) + "; counts on the perf line as mem_hygiene");
        }

        static bool On(ConfigEntry<bool> e) => e != null && e.Value;

        // An ended mission's object, never the current one, the placeholder or the game's empty NullMission
        internal static bool OldMission(object m)
        {
            if (m == null || ReferenceEquals(m, Released) || ReferenceEquals(m, Mission.NullMission)) return false;
            object cur = null;
            try { cur = MissionManager.CurrentMission; } catch { }
            return !ReferenceEquals(m, cur);
        }

        internal static bool OldRunner(object r)
        {
            if (r == null || ReferenceEquals(r, Released)) return false;
            object cur = null;
            try { cur = MissionManager.Runner; } catch { }
            return !ReferenceEquals(r, cur);
        }

        // ------------------------------------------------------------------ the trigger
        // GameManager.ResetGame: a plain public static method of a plain class (live Assembly-CSharp df5bed594dd8; proof in
        // plugin_mem\reflect_mem). Called by NetworkManagerNuclearOption.LoadSystemScene after the empty scene has loaded and
        // right before MemoryCleanup (the forced full collection), and by GameManager.SetupGame before a mission map loads.
        [HarmonyPatch]
        internal static class ResetGamePatch
        {
            static MethodBase TargetMethod()
            {
                MethodInfo m = AccessTools.DeclaredMethod(typeof(GameManager), nameof(GameManager.ResetGame), Type.EmptyTypes);
                if (m != null && PatchSafety.IsShared(m))
                {
                    NukeStatsPlugin.Log?.LogError("[mem] GameManager.ResetGame is a generic type's method in this game build: not hooked (the 1 s tick does the clean-up)");
                    return null;   // Harmony skips the class ("[patch] ResetGamePatch SKIPPED")
                }
                return m;
            }

            // after the game's own ResetGame; never throws into the game
            static void Postfix()
            {
                try
                {
                    if (!_hookSeen) { _hookSeen = true; NukeStatsPlugin.Log?.LogInfo("[diag] GameManager.ResetGame hooked (memory clean-up at each mission change)"); }
                    Pass("resetgame");
                }
                catch { }
            }
        }

        // once a second from Perf.Tick: notes running missions, and stands in for the hook above only if it has never run in
        // this process (it runs at start-up already, so normally this does nothing else): at the first tick of each new
        // mission (a runner other than the last one seen), the pass, with that new mission running
        internal static void Tick1s(float now)
        {
            try
            {
                var r = MissionManager.Runner;
                if (r == null) return;
                _anyMissionSeen = true;
                object last = _lastRunner != null ? _lastRunner.Target : null;
                if (ReferenceEquals(last, r)) return;
                bool newMission = _lastRunner != null;          // a runner was seen before (alive or collected by now)
                _lastRunner = new WeakReference(r);
                if (newMission && !_hookSeen) Pass("tick", missionRunningOk: true);
            }
            catch { }
        }

        // ------------------------------------------------------------------ the pass
        // missionRunningOk: only the stand-in tick, at the first tick of a new mission. Every part lets go only of what is
        // destroyed, what belongs to no current mission (CurrentMissionObjects) or the plugin's own; the game's shared scratch
        // lists are left alone while a mission runs.
        static void Pass(string why, bool missionRunningOk = false)
        {
            if (_inPass) return;
            bool running;
            try { running = MissionManager.Runner != null; }
            catch { return; }
            if (running && !missionRunningOk) return;   // the hook's pass never runs inside a mission (cannot happen at its call sites)
            _inPass = true;
            long t0 = Stopwatch.GetTimestamp();
            var got = new int[KCount];
            try
            {
                if (On(ClearObjectiveThrottle)) got[KObj] = Step(KObj, ObjectiveThrottle.ReleaseOldMission);
                if (On(DropDeadVapour)) got[KVap] = Step(KVap, PerfExtra.DropDeadVapour);
                if (On(UnhookEndedObjectives)) got[KHook] = Step(KHook, UnhookObjectives);
                if (On(DropOldMissionListeners)) got[KListen] = Step(KListen, DropDeadMissionListeners);
                if (On(ClearPluginMissionRefs)) got[KRefs] = Step(KRefs, ReleasePluginRefs);
                // the game's statics are only read once a mission has run in this process (the game has used those types by
                // then, so reading them never runs a static constructor early)
                if (On(ClearGameScratch) && _anyMissionSeen) got[KScratch] = Step(KScratch, () => ClearScratch(running));
                if (On(ClearNullMissionHQs)) got[KNull] = Step(KNull, ReleaseNullMissionHQs);
            }
            finally
            {
                double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                _inPass = false;
                _passesWin++; _passesTot++;
                if (ms > _passMsMax) _passMsMax = ms;
                for (int k = 0; k < KCount; k++) { _win[k] += got[k]; _tot[k] += got[k]; FirstTime(k, got[k]); }
            }
        }

        static int Step(int k, Func<int> f)
        {
            try { return f(); }
            catch (Exception e)
            {
                if (!_errSaid[k]) { _errSaid[k] = true; NukeStatsPlugin.Log?.LogError($"[mem] {Names[k]} failed (logged once, the others go on): {e}"); }
                return 0;
            }
        }

        static void FirstTime(int k, int n)
        {
            if (n <= 0 || _said[k]) return;
            _said[k] = true;
            string what;
            switch (k)
            {
                case KObj: what = $"{n} objective(s) of the ended mission dropped from the destroy-check schedule"; break;
                case KVap: what = $"{n} wing-vapour entry(ies) of aircraft that no longer exist dropped"; break;
                case KHook: what = $"{n} objective(s) of ended missions taken off the server's spawn event"; break;
                case KListen: what = $"{n} destroyed mission manager(s) taken off the host's disconnect event"; break;
                case KRefs: what = $"{n} plugin reference(s) to the ended mission let go"; break;
                case KNull: what = $"{n} FactionHQ(s) of the ended mission let go by the game's empty placeholder mission"; break;
                default: what = $"{n} entry(ies) in the game's scratch lists emptied"; break;
            }
            NukeStatsPlugin.Log?.LogInfo($"[mem] {Names[k]}: {what} at a mission change (logged once; the perf line's mem_hygiene counts the rest)");
        }

        // ------------------------------------------------------------------ the game's objects, read by reflection
        // Field names checked against the live DLLs (Assembly-CSharp df5bed594dd8, Mirage d1e0dba87d81, UnityEngine.CoreModule
        // of the live host): plugin_mem\reflect_mem. Each one is looked up once; a missing one only switches its own part off.
        static FieldInfo _fOnSpawn, _fSorted, _fNeedsSorting, _fDisconnected, _fBooms, _fBoomUnit, _fBoomSource, _fVoteHistory;
        static readonly List<KeyValuePair<string, FieldInfo>> _scratch = new List<KeyValuePair<string, FieldInfo>>();
        static readonly Dictionary<string, FieldInfo> _upCache = new Dictionary<string, FieldInfo>();

        static void ResolveGame()
        {
            var missing = new List<string>();
            const BindingFlags I = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            const BindingFlags S = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            FieldInfo F(Type t, string name, BindingFlags fl)
            {
                FieldInfo f = null;
                try { f = t != null ? t.GetField(name, fl | BindingFlags.DeclaredOnly) : null; } catch { }
                if (f == null) missing.Add((t != null ? t.Name : "?") + "." + name);
                return f;
            }
            _fOnSpawn = F(typeof(NetworkWorld), "onSpawn", I);
            _fSorted = F(typeof(NetworkWorld), "_sortedIdentities", I);
            _fNeedsSorting = F(typeof(NetworkWorld), "_needsSorting", I);
            _fDisconnected = F(typeof(NetworkClient), "_disconnected", I);
            void Scratch(Type t, string name) { var f = F(t, name, S); if (f != null) _scratch.Add(new KeyValuePair<string, FieldInfo>(t.Name + "." + name, f)); }
            Scratch(typeof(CombatAI), "targetCandidates");
            Scratch(typeof(CombatAI), "targetsAttacked");
            Scratch(typeof(FactionHQ), "playersCache");
            Scratch(typeof(RearmMissionController), "rearmerMissionMatchups");
            Scratch(typeof(RearmMissionController), "filteredMissions");
            Scratch(typeof(RearmMissionController), "availableRearmersCache");
            _fVoteHistory = F(typeof(VoteKickManager), "_historyConnectedPlayers", S);
            _fBooms = F(typeof(SonicBoomManager), "managedBooms", S);
            Type boom = null;
            try { boom = typeof(SonicBoomManager).GetNestedType("ManagedSonicBoom", BindingFlags.NonPublic | BindingFlags.Public); } catch { }
            if (boom == null) missing.Add("SonicBoomManager.ManagedSonicBoom");
            _fBoomUnit = boom != null ? F(boom, "supersonicUnit", I) : null;
            _fBoomSource = boom != null ? F(boom, "sourceObject", I) : null;
            if (missing.Count > 0)
                NukeStatsPlugin.Log?.LogWarning("[mem] not found in this game build, left alone: " + string.Join(", ", missing));
        }

        // an instance field declared on the type or one of its base types (the event wrappers keep their fields on generic
        // base classes); looked up once per type
        static object Up(object o, string name)
        {
            if (o == null) return null;
            Type t0 = o.GetType();
            string key = t0.AssemblyQualifiedName + "|" + name;
            if (!_upCache.TryGetValue(key, out FieldInfo f))
            {
                for (Type t = t0; t != null && f == null; t = t.BaseType)
                    f = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
                _upCache[key] = f;
            }
            return f != null ? f.GetValue(o) : null;
        }

        static NetworkManagerNuclearOption Nm()
        {
            try { return NetworkManagerNuclearOption.i; } catch { return null; }
        }

        // UnhookEndedObjectives. CrashAircraftObjective / SuccessfulSortieObjective subscribe to the server's NetworkWorld.onSpawn
        // (it lives as long as the process) in OnStart and unsubscribe only in Cleanup(). MissionRunner.StopObjective (an
        // outcome's "Stop") never calls Cleanup(), and MissionRunner.Cleanup() at the end is never reached on a dedicated server
        // (UnloadMission sets the runner to null before MissionManager.OnDestroy calls Runner?.Cleanup()). Here no mission runs,
        // so every objective on that event belongs to an ended mission; one of the current mission (if the game ever had one
        // loaded at this point) is left alone. Removed with the event's own public remove, exactly as Cleanup() would.
        static int UnhookObjectives()
        {
            var nm = Nm();
            NetworkWorld world = nm != null && nm.Server != null ? nm.Server.World : null;
            if (world == null || _fOnSpawn == null) return 0;
            if (!(_fOnSpawn.GetValue(world) is Delegate d)) return 0;
            HashSet<object> current = null;
            int n = 0;
            foreach (Delegate x in d.GetInvocationList())
            {
                if (!(x.Target is Objective o)) continue;
                if (current == null) current = CurrentMissionObjects();
                if (current.Contains(o)) continue;
                if (!(x is Action<NetworkIdentity> h)) continue;
                world.onSpawn -= h;
                n++;
            }
            return n;
        }

        // The objectives and outcomes of the current mission (the running one's active objectives, and every objective and
        // outcome of the current Mission unless it is the empty placeholder), compared by reference. Empty when no mission
        // is set, as at the hook's moment.
        internal static HashSet<object> CurrentMissionObjects()
        {
            var set = new HashSet<object>(SameObject.I);
            try
            {
                var r = MissionManager.Runner;
                if (r != null) foreach (var o in r.ActiveObjectives) if (o != null) set.Add(o);
            }
            catch { }
            try
            {
                Mission m = MissionManager.CurrentMission;
                var ro = m != null && !ReferenceEquals(m, Mission.NullMission) ? m.RuntimeObjectives : null;
                if (ro != null)
                {
                    if (ro.AllObjectives != null) foreach (var o in ro.AllObjectives) if (o != null) set.Add(o);
                    if (ro.AllOutcomes != null) foreach (var o in ro.AllOutcomes) if (o != null) set.Add(o);
                }
            }
            catch { }
            return set;
        }

        // DropOldMissionListeners. MissionManager.StartMission adds its private ClientDisconnected to the host client's
        // Disconnected event on every mission; only StopMission removes it, and that runs only when the host's own client
        // disconnects (server shutdown). Each destroyed MissionManager keeps its NetworkIdentity and every behaviour on the
        // same object (Spawner, MessageManager, ChatManager with every player who chatted, VoteKickManager ...). A destroyed
        // object's ClientDisconnected would only set the static MissionManager.IsRunning to false at shutdown, which the live
        // one does too. DynamicMap adds DynamicMap_OnStopClient the same way (not seen on a headless server); a destroyed one
        // is dropped too. The listeners are found in the event's runtime call list (read only) and removed with the event's
        // own public RemoveListener (Unity matches target + method and rebuilds its invoke list at once).
        static int DropDeadMissionListeners()
        {
            var nm = Nm();
            NetworkClient client = nm != null ? nm.Client : null;
            if (client == null || _fDisconnected == null) return 0;
            object late = _fDisconnected.GetValue(client);                // Mirage DisconnectAddLateEvent
            object calls = Up(Up(late, "_event"), "m_Calls");             // UnityEvent<ClientStoppedReason> -> InvokableCallList
            if (!(Up(calls, "m_RuntimeCalls") is IList list) || list.Count == 0) return 0;
            List<UnityAction<ClientStoppedReason>> dead = null;
            foreach (object call in list)
            {
                if (!(Up(call, "Delegate") is UnityAction<ClientStoppedReason> del)) continue;
                var t = del.Target as UnityEngine.Object;
                if (ReferenceEquals(t, null) || t != null) continue;      // a C# object that Unity has destroyed only
                if (!(t is MissionManager) && !(t is DynamicMap)) continue;
                (dead ?? (dead = new List<UnityAction<ClientStoppedReason>>())).Add(del);
            }
            if (dead == null) return 0;
            int before = list.Count;
            foreach (var del in dead) client.Disconnected.RemoveListener(del);
            object after = Up(calls, "m_RuntimeCalls");
            return after is IList l2 ? Math.Max(0, before - l2.Count) : dead.Count;
        }

        // ClearPluginMissionRefs: each class lets go of what it holds of the ended mission (each part on its own, so one
        // that fails never stops the others)
        static readonly HashSet<string> _subErrSaid = new HashSet<string>();

        static int Sub(string name, Func<int> f, StringBuilder detail)
        {
            int n = 0;
            try { n = f(); }
            catch (Exception e)
            {
                if (_subErrSaid.Add(name)) NukeStatsPlugin.Log?.LogError($"[mem] {name} failed (logged once, the other parts go on): {e}");
            }
            if (detail != null) { if (detail.Length > 0) detail.Append(", "); detail.Append(name).Append(' ').Append(n); }
            return n;
        }

        static int ReleasePluginRefs()
        {
            StringBuilder detail = null;
            int n = 0;
            n += Sub("rankfloor", StartingRankFloorPatch.ReleaseOldMission, detail);
            n += Sub("airport", AiAirportRotation.ReleaseOldMission, detail);
            n += Sub("pingkick", PingKick.ReleaseOldMission, detail);
            n += Sub("timings", PerfMission.ReleaseOldMission, detail);
            n += Sub("pilots+caches", NukeStatsPlugin.ReleaseOldMissionRefs, detail);
            n += Sub("anims+constraints", Perf.ReleaseOldMission, detail);
            n += Sub("particles", PerfExtra.PruneParticlesNow, detail);
            return n;
        }

        // ClearGameScratch. Each list is emptied by the game at the start of its next use (CombatAI target searches, FactionHQ
        // GetPlayers, RearmMissionController planning; all filled and read by plain synchronous code, never across frames);
        // the vote-kick history at the next mission's VoteKickManager.OnStartServer (only emptied here while no VoteKickManager
        // is alive); the server camera reference is only read by the (destroyed) CameraControlUI and set again by the next
        // one; Mirage rebuilds its sorted identity list on the next call whenever it is due for a re-sort, so it is only
        // emptied then; sonic-boom entries only when their unit and sound object are both destroyed.
        // missionRunning (only the stand-in tick): the game's shared scratch lists are left alone, they may be the running
        // mission's own; only the parts that let go of destroyed objects run.
        static int ClearScratch(bool missionRunning)
        {
            StringBuilder detail = null;
            int n = 0;
            if (!missionRunning) foreach (var kv in _scratch)
            {
                var f = kv.Value;
                n += Sub(kv.Key, () =>
                {
                    object v = f.GetValue(null);
                    if (v is IList list) { int c = list.Count; if (c > 0) list.Clear(); return c; }
                    if (v is IDictionary dict) { int c = dict.Count; if (c > 0) dict.Clear(); return c; }
                    return 0;
                }, detail);
            }
            // VoteKickManager._historyConnectedPlayers (static): the last mission's players. The next VoteKickManager empties it
            // in its OnStartServer; it is only emptied here while no VoteKickManager is alive (the game's ResetGame has already
            // cleared the destroyed one's singleton field at the hook's moment), so a new mission's history is never touched.
            n += Sub("VoteKickManager._historyConnectedPlayers", () =>
            {
                if (_fVoteHistory == null) return 0;
                VoteKickManager live = NetworkSceneSingleton<VoteKickManager>.i;
                if (live != null) return 0;                                   // a live vote-kick manager owns it
                if (!(_fVoteHistory.GetValue(null) is IDictionary d) || d.Count == 0) return 0;
                int c = d.Count;
                d.Clear();
                return c;
            }, detail);
            // SonicBoomManager.managedBooms: the camera's FixedUpdate drops an entry whose unit is destroyed (Manage: Destroy its
            // sound object, return false) at the next mission. An entry is only taken out here when its unit AND its sound object
            // (a child of the scene's Datum) are both destroyed, so there is nothing left for the game to do with it.
            n += Sub("SonicBoomManager.managedBooms", () =>
            {
                if (_fBooms == null || _fBoomUnit == null || _fBoomSource == null) return 0;
                if (!(_fBooms.GetValue(null) is IList list) || list.Count == 0) return 0;
                int c = 0;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    object b = list[i];
                    if (b == null) continue;
                    var unit = _fBoomUnit.GetValue(b) as UnityEngine.Object;
                    var src = _fBoomSource.GetValue(b) as UnityEngine.Object;
                    if (ReferenceEquals(unit, null) || unit != null) continue;   // the unit must be destroyed
                    if (!ReferenceEquals(src, null) && src != null) continue;     // and so must its sound object
                    list.RemoveAt(i);
                    c++;
                }
                return c;
            }, detail);
            n += Sub("CameraControlUI.cam", () =>
            {
                CameraStateManager cam = CameraControlUI.cam;
                if (ReferenceEquals(cam, null) || cam != null) return 0;   // only one destroyed with its scene
                CameraControlUI.cam = null;
                return 1;
            }, detail);
            n += Sub("World._sortedIdentities", () =>
            {
                if (missionRunning) return 0;                                    // a running mission's list: left alone
                var nm = Nm();
                NetworkWorld world = nm != null && nm.Server != null ? nm.Server.World : null;
                if (world == null || _fSorted == null || _fNeedsSorting == null) return 0;
                if (!(_fNeedsSorting.GetValue(world) is bool due) || !due) return 0;   // only while it is due for a rebuild
                if (!(_fSorted.GetValue(world) is IList sorted) || sorted.Count == 0) return 0;
                int c = sorted.Count;
                sorted.Clear();
                return c;
            }, detail);
            return n;
        }

        // ClearNullMissionHQs. MissionManager.SetNullMission raises onMissionLoad(Mission.NullMission) while the ended mission's
        // scene is still loaded, so every one of its FactionHQs runs OnMissionLoad -> Mission.GetFactionFromHq, which writes the
        // HQ into the static placeholder's factions (MissionFaction.FactionHQ). They stayed there until the next mission ended,
        // with most of the ended mission behind them (test server: 8-9.5 MB, through the whole next mission). The only reader
        // of that field is the mission editor's objective panel. Only an HQ that Unity has destroyed is let go.
        static int ReleaseNullMissionHQs()
        {
            var fs = Mission.NullMission != null ? Mission.NullMission.factions : null;
            if (fs == null) return 0;
            int n = 0;
            foreach (MissionFaction f in fs)
            {
                if (f == null) continue;
                FactionHQ hq = f.FactionHQ;
                if (ReferenceEquals(hq, null) || hq != null) continue;      // only one that Unity has destroyed
                f.FactionHQ = null;
                n++;
            }
            return n;
        }

        sealed class SameObject : IEqualityComparer<object>
        {
            internal static readonly SameObject I = new SameObject();
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }

        // ------------------------------------------------------------------ the game's floor line (read only)
        static void OnLog(string msg, string stack, LogType type)
        {
            try
            {
                if (msg == null || msg.Length < 24 || msg[0] != 'M' || !msg.StartsWith("MonoHeap: ", StringComparison.Ordinal)) return;
                bool after = msg.IndexOf("(After CLEANUP)", StringComparison.Ordinal) > 0;
                bool before = !after && msg.IndexOf("(BEFORE CLEANUP)", StringComparison.Ordinal) > 0;
                if (!after && !before) return;
                int i = 10, used = ReadInt(msg, ref i);
                int slash = msg.IndexOf('/', i);
                int heap = -1;
                if (slash > 0) { int j = slash + 1; heap = ReadInt(msg, ref j); }
                if (used < 0) return;
                if (before) { _beforeMb = used; return; }
                _prevFloorMb = _floorMb;
                _floorMb = used; _floorHeapMb = heap; _floors++;
            }
            catch { }
        }

        static int ReadInt(string s, ref int i)
        {
            while (i < s.Length && s[i] == ' ') i++;
            int v = 0, start = i;
            while (i < s.Length && s[i] >= '0' && s[i] <= '9') { v = v * 10 + (s[i] - '0'); i++; }
            return i > start ? v : -1;
        }

        // ------------------------------------------------------------------ the perf line (Perf.Report), then reset
        internal static void Append(StringBuilder sb)
        {
            try
            {
                sb.Append(",\"mem_hygiene\":{\"passes\":").Append(_passesWin)
                  .Append(",\"passes_total\":").Append(_passesTot)
                  .Append(",\"pass_ms_max\":").Append(_passMsMax.ToString("0.###", CultureInfo.InvariantCulture));
                for (int k = 0; k < KCount; k++) sb.Append(",\"").Append(Keys[k]).Append("\":").Append(_win[k]);
                sb.Append(",\"total\":{");
                for (int k = 0; k < KCount; k++) { if (k > 0) sb.Append(','); sb.Append('"').Append(Keys[k]).Append("\":").Append(_tot[k]); }
                sb.Append("},\"off\":\"");
                bool first = true;
                ConfigEntry<bool>[] sw = Switches();
                for (int k = 0; k < KCount; k++) if (!On(sw[k])) { if (!first) sb.Append(','); sb.Append(Keys[k]); first = false; }
                sb.Append("\",\"floor_mb\":").Append(_floorMb)
                  .Append(",\"floor_heap_mb\":").Append(_floorHeapMb)
                  .Append(",\"floor_prev_mb\":").Append(_prevFloorMb)
                  .Append(",\"floors\":").Append(_floors)
                  .Append('}');
            }
            catch { }
            for (int k = 0; k < KCount; k++) _win[k] = 0;
            _passesWin = 0; _passMsMax = 0;
        }
    }
}
