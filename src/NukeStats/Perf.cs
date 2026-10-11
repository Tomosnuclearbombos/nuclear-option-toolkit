using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using Unity.Jobs.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.LowLevel;

namespace NukeStats
{
    // 1.9.5 (owner 2026-10-06): server performance.
    //  - Perf.Timing measures where the frame goes and prints one {"t":"perf"} line a minute: the game's own work
    //    (AI pilots, the physics jobs and the wait for them, aircraft, missiles, ships, ground vehicles, turrets, guns,
    //    gun rounds, radars, faction HQs, airbases, the network), the Tacview recorder, and each plugin tick. Values are
    //    milliseconds of main thread time per second. Switching it off removes the timing hooks again.
    //  - Perf.JobWorkers sets how many job worker threads the game uses for its physics jobs. 0 = the game's own choice
    //    (half the cores the host reports, 2 to 8). Takes effect within a second, no restart.
    // Nothing here changes how the game plays.
    internal static class Perf
    {
        internal static ConfigEntry<bool> Timing;
        internal static ConfigEntry<int> JobWorkers;
        internal static ConfigEntry<bool> Phases;
        internal static ConfigEntry<int> TargetFps;
        internal static ConfigEntry<bool> SkipPilotAnimation;
        internal static ConfigEntry<bool> SkipVisualConstraints;
        internal static ConfigEntry<int> HitchMs;

        const float ReportSec = 60f;

        internal static void Initialize(ConfigFile config)
        {
            Timing = config.Bind("Perf", "Timing", false,
                "Measure where the server's frame time goes and log one summary line a minute (game units, physics jobs, "
                + "network, the Tacview recorder and every plugin tick, in milliseconds per second). Measuring costs a "
                + "little itself, so leave it off when not testing.");
            Phases = config.Bind("Perf", "Phases", false,
                "With Timing on, also time every stage of Unity's frame (the physics step, Update, LateUpdate, network "
                + "send and the rest) by adding markers to the engine's frame loop. Off by default: switch it on for a test.");
            TargetFps = config.Bind("Perf", "TargetFps", 0,
                "Frame-rate target while a mission runs. 0 = the game's own (60). Above 60 lets a frame that finished early "
                + "start sooner instead of waiting out its slot; the physics step stays at 60 a second either way. Never "
                + "goes below 60, and the 5 fps target of an empty waiting server is left alone. Takes effect within a second.");
            JobWorkers = config.Bind("Perf", "JobWorkers", 0,
                "How many worker threads the game uses for its physics jobs. 0 = the game's own choice (half the host's "
                + "cores, 2 to 8). Takes effect within a second.");
            SkipPilotAnimation = config.Bind("Perf", "SkipPilotAnimation", false,
                "Stop the pilot body animation on the server (the pilot model in each cockpit and pilots on the ground after "
                + "ejecting). Only the look of the model; the landing gear animation is left alone because the gear has "
                + "colliders. Takes effect within a second.");
            SkipVisualConstraints = config.Bind("Perf", "SkipVisualConstraints", false,
                "Stop the purely visual moving parts on the server: the wake foam on ships and the hydraulic rams on the "
                + "missile trucks and the howitzer. Nothing on aircraft is touched (one jet steers its thrust with one). "
                + "Takes effect within a second.");
            HitchMs = config.Bind("Perf", "HitchMs", 80,
                "With Timing and Phases on, log one hitch line for any frame that takes longer than this many milliseconds: "
                + "which frame stages and timed parts took the time, and whether a garbage collection ran in that frame. "
                + "At most one line every 2 seconds. 0 = off.");
            // Owner 2026-10-07: which CPU each host runs on (the provider may have moved to Ryzen). The engine reads it
            // from the system; logged once at start.
            try
            {
                NukeStatsPlugin.Log?.LogInfo($"[perf] cpu: {SystemInfo.processorType} | {SystemInfo.processorCount} threads | "
                    + $"{SystemInfo.processorFrequency} MHz | job workers max {JobsUtility.JobWorkerMaximumCount} | "
                    + $"memory {SystemInfo.systemMemorySize} MB");
            }
            catch (Exception e) { NukeStatsPlugin.Log?.LogError("[perf] cpu info: " + e.Message); }
        }

        // ---------------------------------------------------------------- what is timed
        // (label, type, method). Several methods can share a label; they are added together.
        static readonly string[][] Targets =
        {
            new[] { "jobs_schedule", "NuclearOption.Jobs.JobManager", "ScheduleJobs" },
            new[] { "jobs_wait",     "NuclearOption.Jobs.JobManager", "FinishJobs" },
            new[] { "aircraft",      "Aircraft",       "FixedUpdate" },
            new[] { "missiles",      "Missile",        "FixedUpdate" },
            new[] { "ships",         "Ship",           "Update" },
            new[] { "ship_ai",       "ShipAI",         "Update" },
            new[] { "ground",        "GroundVehicle",  "Update" },
            new[] { "turrets",       "Turret",         "FixedUpdate" },
            new[] { "guns",          "Gun",            "Update" },
            new[] { "guns",          "Gun",            "FixedUpdate" },
            new[] { "rounds",        "BulletSim",      "FixedUpdate" },
            new[] { "rounds",        "BulletSim",      "Update" },
            new[] { "radars",        "Radar",          "FixedUpdate" },
            new[] { "radars",        "Radar",          "Update" },
            new[] { "hq",            "FactionHQ",      "Update" },
            new[] { "airbases",      "Airbase",        "Update" },
            new[] { "net_recv",      "Mirage.SocketLayer.Peer", "UpdateReceive" },
            new[] { "net_send",      "Mirage.SocketLayer.Peer", "UpdateSent" },
            new[] { "recorder",      "NOBlackBox.Recorder_mono", "Update" },
            new[] { "recorder",      "NOBlackBox.Recorder_mono", "LateUpdate" },
            // 1.9.6 test 3 (owner 2026-10-09 "measure those game hitches"): most hitches were inside the game's own Update
            // scripts, in none of the parts above. These are the game's server-side scripts with few copies that could do
            // a burst of work in one frame, so a hitch line can name the one that did.
            new[] { "slow_updates",  "SlowUpdateExtensions+Runner", "Update" },   // every unit's periodic jobs, in one runner
            // the HQ spawner (a slow-update job every 5 s per HQ): the 2026-10-09 test hitch was one FactionHQ.DeployUnits
            new[] { "spawn_aircraft","FactionHQ",       "DeployAIAircraft" },
            new[] { "spawn_vehicles","FactionHQ",       "DeployVehicles" },
            new[] { "spawn_sorting", "FactionHQ",       "SortAirbases" },
            new[] { "spawn_sorting", "FactionHQ",       "SortDepots" },
            new[] { "mission_mgr",   "MissionManager",  "Update" },
            new[] { "mission_runner","MissionRunner",   "Update" },               // objectives (runs inside mission_mgr)
            new[] { "capture",       "Capture",         "Update" },
            new[] { "fragments",     "FragmentManager", "Update" },               // building collapse pieces and dust
            new[] { "map_buildings", "MapBuildingSet",  "Update" },
            new[] { "level",         "LevelInfo",       "Update" },
            new[] { "weather",       "Lightning",       "Update" },
            new[] { "weather",       "CloudLayer",      "Update" },
            new[] { "reserve_report","ReserveReport",   "Update" },
            new[] { "explosions",    "Shockwave",       "Update" },
            new[] { "explosions",    "MushroomCloud",   "Update" },
            new[] { "explosions",    "ExplosionAudioManager", "Update" },
            new[] { "explosions",    "DamageParticles", "Update" },
            new[] { "pilots_down",   "PilotDismounted", "Update" },
            new[] { "pilots_down",   "Parachute",       "Update" },
            new[] { "pilots_down",   "EjectionSeat",    "LateUpdate" },
            new[] { "net_transforms","NuclearOption.NetworkTransforms.SendTransformBatcher", "LateUpdate" },
            new[] { "votekick",      "NuclearOption.Networking.VoteKickManager", "Update" },
            new[] { "steam",         "SteamManager",    "Update" },
            new[] { "observers",     "Observer",        "Update" },
        };

        // The plugin's own per-frame work, in PeriodicTick order (Lap(n) after each call there).
        internal static readonly string[] PlugNames =
        {
            "PerfTick", "RttProbe", "FactionColours", "PvETimeout", "", "Annihilate", "", "PingKick",
            "TkWatch", "Names", "WebNames", "JoinAnnounces", "Snapshot", "CleanupPilots", "Balance", "PendingBalance",
            "Bounces", "Life", "Pos", "MapEnt", "Tk", "", "Grief", "AirPanel", "Catchup", "RankFunds",
            "LimboWatch", "Commands", "", "Perf",   // a blank name = a slot this build does not use (not printed)
            "Humans"    // 1.9.6 test 3: the frame's player list (built by the first caller, which used to be RttProbe)
        };

        static readonly List<string> _labels = new List<string>();
        static readonly Dictionary<MethodBase, int> _slotOf = new Dictionary<MethodBase, int>();
        static readonly List<MethodBase> _patched = new List<MethodBase>();
        static long[] _ticks = new long[0];
        static int[] _calls = new int[0];
        static int _aiSlot = -1, _playerSlot = -1;
        static MethodBase _pilotMethod;
        static Harmony _harmony;
        static bool _on;                          // hooks installed and counting

        static readonly long[] _plugTicks = new long[PlugNames.Length];
        static long _lapT;

        // frame time, from the per-frame PeriodicTick
        static int _frames;
        static double _ftSum;
        static float _ftMax;
        static readonly int[] _ftHist = new int[251];   // 1 ms bins, the last one = 250 ms or more
        static float _windowStart = -1f;
        static long _rankLoadTicks; static int _rankLoads;
        static long _logChars; static int _logLines;

        internal static bool On => _on;

        // ---------------------------------------------------------------- hooks
        static int SlotFor(string label)
        {
            int i = _labels.IndexOf(label);
            if (i >= 0) return i;
            _labels.Add(label);
            return _labels.Count - 1;
        }

        static void Install()
        {
            if (_on) return;
            try
            {
                _harmony = _harmony ?? new Harmony(NukeStatsPlugin.Guid + ".perf");
                var pre = new HarmonyMethod(typeof(Perf).GetMethod(nameof(Pre), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First };
                var post = new HarmonyMethod(typeof(Perf).GetMethod(nameof(Post), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First };
                var missing = new List<string>();
                foreach (var t in Targets)
                {
                    var type = AccessTools.TypeByName(t[1]);
                    var m = type != null ? AccessTools.DeclaredMethod(type, t[2], Type.EmptyTypes) : null;
                    if (m == null) { missing.Add(t[1] + "." + t[2]); continue; }
                    if (_slotOf.ContainsKey(m)) continue;
                    int slot = SlotFor(t[0]);
                    _slotOf[m] = slot;
                    try { _harmony.Patch(m, prefix: pre, postfix: post); _patched.Add(m); }
                    catch (Exception e) { _slotOf.Remove(m); missing.Add(t[1] + "." + t[2] + " (" + e.Message + ")"); }
                }
                // AI pilots: every pilot runs here each physics step; player pilots return straight away on the server
                _pilotMethod = AccessTools.DeclaredMethod(typeof(Pilot), "Pilot_OnAeroInputsApplied", Type.EmptyTypes);
                if (_pilotMethod != null)
                {
                    _aiSlot = SlotFor("ai_pilots");
                    _playerSlot = SlotFor("player_pilots");
                    try
                    {
                        _harmony.Patch(_pilotMethod,
                            prefix: new HarmonyMethod(typeof(Perf).GetMethod(nameof(PilotPre), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First },
                            postfix: new HarmonyMethod(typeof(Perf).GetMethod(nameof(PilotPost), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First });
                        _patched.Add(_pilotMethod);
                    }
                    catch (Exception e) { missing.Add("Pilot.Pilot_OnAeroInputsApplied (" + e.Message + ")"); }
                }
                else missing.Add("Pilot.Pilot_OnAeroInputsApplied");
                SlowRunnerInstall(missing);
                SpawnProbeInstall(missing);
                if (_ticks.Length < _labels.Count) { _ticks = new long[_labels.Count]; _calls = new int[_labels.Count]; }
                if (_frameTicks.Length < _labels.Count) _frameTicks = new long[_labels.Count];
                _on = true;
                ResetWindow();
                NukeStatsPlugin.Log?.LogInfo($"[perf] timing on: {_patched.Count} method(s) hooked"
                    + (missing.Count > 0 ? ", not found: " + string.Join(", ", missing) : ""));
            }
            catch (Exception e)
            {
                NukeStatsPlugin.Log?.LogError("[perf] timing could not start: " + e.Message);
                Uninstall();
            }
        }

        static void Uninstall()
        {
            _on = false;
            try
            {
                foreach (var m in _patched) _harmony?.Unpatch(m, HarmonyPatchType.All, _harmony.Id);
            }
            catch (Exception e) { NukeStatsPlugin.Log?.LogError("[perf] unhook: " + e.Message); }
            _patched.Clear();
            _slotOf.Clear();
            _spIndex.Clear();
            _spOuter = false;
            NukeStatsPlugin.Log?.LogInfo("[perf] timing off");
        }

        static void Pre(out long __state) { __state = Stopwatch.GetTimestamp(); }

        // runs inside the game's own methods: it must never throw
        static void Post(long __state, MethodBase __originalMethod)
        {
            try
            {
                if (!_on || !_slotOf.TryGetValue(__originalMethod, out int slot) || slot >= _ticks.Length) return;
                long dt = Stopwatch.GetTimestamp() - __state;
                _ticks[slot] += dt;
                _calls[slot]++;
                if (slot < _frameTicks.Length) _frameTicks[slot] += dt;
            }
            catch { }
        }

        // ---------------------------------------------------------------- the game's slow-update runner (1.9.6 test 3)
        // SlowUpdateExtensions.Runner fires every unit's periodic jobs (radar-altitude checks, AI decisions and the rest)
        // from one Update. The 2026-10-09 test server caught a 110 ms burst there. For a frame that spends over 20 ms in
        // it, note how many jobs fired, how many are registered, and which methods fired most, for that frame's hitch line.
        static FieldInfo _fSlowToCall, _fSlowCalls, _fSlowCount, _fSlowAction;
        static string _slowFrameInfo;              // this frame's burst, or null
        const double SlowBurstMs = 20.0;

        static void SlowRunnerInstall(List<string> missing)
        {
            try
            {
                var runnerT = AccessTools.TypeByName("SlowUpdateExtensions+Runner");
                var callT = AccessTools.TypeByName("SlowUpdateExtensions+Call");
                var m = runnerT != null ? AccessTools.DeclaredMethod(runnerT, "Update", Type.EmptyTypes) : null;
                if (m == null || callT == null) { missing.Add("SlowUpdateExtensions+Runner.Update (burst detail)"); return; }
                _fSlowToCall = AccessTools.Field(runnerT, "toCall");
                _fSlowCalls = AccessTools.Field(runnerT, "calls");
                _fSlowCount = AccessTools.Field(runnerT, "count");
                _fSlowAction = AccessTools.Field(callT, "Action");
                if (_fSlowToCall == null || _fSlowCalls == null || _fSlowAction == null) { missing.Add("SlowUpdateExtensions fields"); return; }
                _harmony.Patch(m,
                    prefix: new HarmonyMethod(typeof(Perf).GetMethod(nameof(SlowPre), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(Perf).GetMethod(nameof(SlowPost), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First });
                _patched.Add(m);
            }
            catch (Exception e) { missing.Add("SlowUpdateExtensions+Runner.Update (" + e.Message + ")"); }
        }

        static void SlowPre(out long __state) { __state = Stopwatch.GetTimestamp(); }

        // runs inside the game's own Update: it must never throw; the reflection below only happens on a burst frame
        static void SlowPost(long __state, object __instance)
        {
            try
            {
                if (!_on || __instance == null) return;
                double ms = (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
                if (ms < SlowBurstMs) return;
                var toCall = _fSlowToCall.GetValue(__instance) as List<int>;
                var calls = _fSlowCalls.GetValue(__instance) as Array;
                int registered = _fSlowCount != null ? (int)_fSlowCount.GetValue(__instance) : -1;
                var byName = new Dictionary<string, int>();
                if (toCall != null && calls != null)
                {
                    foreach (int i in toCall)
                    {
                        if (i < 0 || i >= calls.Length) continue;
                        var act = _fSlowAction.GetValue(calls.GetValue(i)) as Delegate;
                        string n = CallName(act);
                        byName[n] = byName.TryGetValue(n, out int c) ? c + 1 : 1;
                    }
                }
                var top = new List<KeyValuePair<string, int>>(byName);
                top.Sort((a, b) => b.Value.CompareTo(a.Value));
                var sb = new StringBuilder(256);
                sb.Append("{\"ms\":").Append(F(ms)).Append(",\"fired\":").Append(toCall != null ? toCall.Count : -1)
                  .Append(",\"registered\":").Append(registered).Append(",\"top\":{");
                for (int k = 0; k < top.Count && k < 5; k++)
                {
                    if (k > 0) sb.Append(',');
                    sb.Append('"').Append(top[k].Key).Append("\":").Append(top[k].Value);
                }
                sb.Append("}}");
                _slowFrameInfo = sb.ToString();
            }
            catch { }
        }

        // "Aircraft.CheckRadarAlt" for a method, the class that owns a lambda instead of its compiler-made holder
        static string CallName(Delegate d)
        {
            try
            {
                if (d == null) return "?";
                var mi = d.Method;
                var t = mi.DeclaringType;
                while (t != null && t.Name.StartsWith("<") && t.DeclaringType != null) t = t.DeclaringType;
                string tn = t != null ? t.Name : "?";
                string mn = mi.Name;
                int lt = mn.IndexOf('<'), gt = mn.IndexOf('>');
                if (lt == 0 && gt > 1) mn = mn.Substring(1, gt - 1) + "(lambda)";
                return (tn + "." + mn).Replace("\"", "'");
            }
            catch { return "?"; }
        }

        // ---------------------------------------------------------------- one line per aircraft spawn (1.9.6 test 3d)
        // Owner 2026-10-09 "add timers on those steps": an AI aircraft spawn held a test frame for ~110 ms. Hangar.
        // SpawnAircraft is the whole spawn; inside it are the copy of the aircraft (Spawner.SpawnAircraft minus the network
        // spawn), the network spawn (Mirage ServerObjectManager.Spawn, which on this host-mode server also runs the
        // aircraft's start-up: Aircraft.OnStartClient -> its flight physics (SetComplexPhysics) and its weapons
        // (WeaponManager.SpawnWeapons)), and the AI weapon choice (SelectAIAircraftWeapons). Each spawn logs
        // {"t":"spawn"} with the type, whether it is the first of that type this mission (one-off loading would show up
        // there), and the milliseconds of each step. Steps can sit inside each other, so they do not add up.
        static readonly string[] SpawnStepNames = { "copy_and_network", "network", "start_up", "physics", "weapons", "weapon_choice" };
        static readonly Dictionary<MethodBase, int> _spIndex = new Dictionary<MethodBase, int>();
        static readonly long[] _spAcc = new long[SpawnStepNames.Length];
        static readonly int[] _spDepth = new int[SpawnStepNames.Length];
        static bool _spOuter;
        static float _spOuterAt;
        static readonly HashSet<string> _spSeen = new HashSet<string>();   // aircraft types spawned this mission

        static void SpawnProbeInstall(List<string> missing)
        {
            try
            {
                var outer = AccessTools.DeclaredMethod(typeof(Hangar), "SpawnAircraft");
                if (outer == null) { missing.Add("Hangar.SpawnAircraft (spawn lines)"); return; }
                void step(int idx, Type t, string name, bool allOverloads)
                {
                    if (t == null) { missing.Add("spawn step " + SpawnStepNames[idx]); return; }
                    var ms = new List<MethodInfo>();
                    foreach (var m in AccessTools.GetDeclaredMethods(t))
                        if (m.Name == name && !m.IsAbstract && !m.IsGenericMethodDefinition) { ms.Add(m); if (!allOverloads) break; }
                    if (ms.Count == 0) { missing.Add(t.Name + "." + name); return; }
                    foreach (var m in ms)
                    {
                        if (_spIndex.ContainsKey(m)) continue;
                        _spIndex[m] = idx;
                        _harmony.Patch(m,
                            prefix: new HarmonyMethod(typeof(Perf).GetMethod(nameof(SpPre), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First },
                            postfix: new HarmonyMethod(typeof(Perf).GetMethod(nameof(SpPost), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First });
                        _patched.Add(m);
                    }
                }
                step(0, typeof(Spawner), "SpawnAircraft", false);
                step(1, AccessTools.TypeByName("Mirage.ServerObjectManager"), "Spawn", true);
                step(2, typeof(Aircraft), "OnStartClient", false);
                step(3, typeof(Aircraft), "SetComplexPhysics", false);
                step(4, typeof(WeaponManager), "SpawnWeapons", false);
                step(5, typeof(WeaponManager), "SelectAIAircraftWeapons", false);
                _harmony.Patch(outer,
                    prefix: new HarmonyMethod(typeof(Perf).GetMethod(nameof(SpOuterPre), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(Perf).GetMethod(nameof(SpOuterPost), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First });
                _patched.Add(outer);
                AwakeProbeInstall();
            }
            catch (Exception e) { missing.Add("spawn lines (" + e.Message + ")"); }
        }

        // Test 3f (2026-10-10): the first A-19 Brawler's 110 ms is all in the copy, and neither collision shapes nor code
        // preparation changed it. The copy runs every part's Awake and OnEnable, so time those (only the game's component
        // types found in the aircraft prefabs, only while a spawn is in progress) and name the biggest in a slow spawn's line.
        static readonly Dictionary<MethodBase, string> _awakeName = new Dictionary<MethodBase, string>();
        static readonly Dictionary<string, long> _awakeAcc = new Dictionary<string, long>();
        static int _awakeHooks;

        static void AwakeProbeInstall()
        {
            try
            {
                var types = new HashSet<Type>();
                foreach (var d in Encyclopedia.i.aircraft)
                {
                    if (d == null || d.unitPrefab == null) continue;
                    foreach (var c in d.unitPrefab.GetComponentsInChildren<Component>(true))
                        for (var t = c != null ? c.GetType() : null; t != null && t.Assembly == typeof(Unit).Assembly; t = t.BaseType)
                            types.Add(t);
                }
                var pre = new HarmonyMethod(typeof(Perf).GetMethod(nameof(AwPre), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First };
                var post = new HarmonyMethod(typeof(Perf).GetMethod(nameof(AwPost), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First };
                foreach (var t in types)
                    foreach (var name in new[] { "Awake", "OnEnable" })
                    {
                        var m = AccessTools.DeclaredMethod(t, name, Type.EmptyTypes);
                        if (m == null || m.IsAbstract || _awakeName.ContainsKey(m) || _slotOf.ContainsKey(m)) continue;
                        try { _harmony.Patch(m, prefix: pre, postfix: post); _patched.Add(m); _awakeName[m] = t.Name + "." + name; _awakeHooks++; }
                        catch { }
                    }
                NukeStatsPlugin.Log?.LogInfo($"[perf] spawn detail: {_awakeHooks} part start-ups timed ({types.Count} part types)");
            }
            catch (Exception e) { NukeStatsPlugin.Log?.LogError("[perf] spawn detail: " + e.Message); }
        }

        static void AwPre(out long __state) { __state = _spOuter ? Stopwatch.GetTimestamp() : 0; }

        static void AwPost(long __state, MethodBase __originalMethod)
        {
            try
            {
                if (__state == 0 || !_spOuter || !_awakeName.TryGetValue(__originalMethod, out string n)) return;
                long dt = Stopwatch.GetTimestamp() - __state;
                _awakeAcc[n] = _awakeAcc.TryGetValue(n, out long v) ? v + dt : dt;   // includes any start-up it calls
            }
            catch { }
        }

        static void SpOuterPre(out long __state)
        {
            __state = 0;
            try
            {
                float now = Time.realtimeSinceStartup;
                if (_spOuter && now - _spOuterAt < 1f) return;     // already inside a spawn (a stale one is restarted)
                _spOuter = true;
                _spOuterAt = now;
                Array.Clear(_spAcc, 0, _spAcc.Length);
                Array.Clear(_spDepth, 0, _spDepth.Length);
                _awakeAcc.Clear();
                __state = Stopwatch.GetTimestamp();
            }
            catch { }
        }

        // runs inside the game's spawn: it must never throw
        static void SpOuterPost(long __state, NuclearOption.Networking.Player player, AircraftDefinition definition)
        {
            try
            {
                if (__state == 0) return;
                _spOuter = false;
                if (!_on) return;
                double f = 1000.0 / Stopwatch.Frequency;
                string unit = definition != null ? (definition.unitName ?? definition.name) : "?";
                bool first = _spSeen.Add(unit);
                var sb = new StringBuilder(320);
                sb.Append("{\"t\":\"spawn\",\"unit\":\"").Append(unit.Replace("\"", "'").Trim())
                  .Append("\",\"player\":").Append(player != null ? "true" : "false")
                  .Append(",\"first\":").Append(first ? "true" : "false")
                  .Append(",\"ms\":").Append(F((Stopwatch.GetTimestamp() - __state) * f));
                for (int i = 0; i < SpawnStepNames.Length; i++)
                    sb.Append(",\"").Append(SpawnStepNames[i]).Append("\":").Append(F(_spAcc[i] * f));
                double totalMs = (Stopwatch.GetTimestamp() - __state) * f;
                if (totalMs >= 20.0 && _awakeAcc.Count > 0)
                {
                    // a slow spawn: the part start-ups that took the most (each includes what it calls)
                    var top = new List<KeyValuePair<string, long>>(_awakeAcc);
                    top.Sort((a, b) => b.Value.CompareTo(a.Value));
                    sb.Append(",\"parts\":{");
                    for (int k = 0; k < top.Count && k < 6; k++)
                    {
                        if (k > 0) sb.Append(',');
                        sb.Append('"').Append(top[k].Key).Append("\":").Append(F(top[k].Value * f));
                    }
                    sb.Append('}');
                }
                sb.Append('}');
                NukeStatsPlugin.Out(sb.ToString());
            }
            catch { }
        }

        static void SpPre(out long __state, MethodBase __originalMethod)
        {
            __state = 0;
            try
            {
                if (!_spOuter || !_spIndex.TryGetValue(__originalMethod, out int i)) return;
                __state = _spDepth[i]++ == 0 ? Stopwatch.GetTimestamp() : -1;   // -1: nested, counted by the outer call
            }
            catch { }
        }

        static void SpPost(long __state, MethodBase __originalMethod)
        {
            try
            {
                if (__state == 0 || !_spIndex.TryGetValue(__originalMethod, out int i)) return;
                if (_spDepth[i] > 0) _spDepth[i]--;
                if (__state > 0 && _spOuter) _spAcc[i] += Stopwatch.GetTimestamp() - __state;
            }
            catch { }
        }

        static void PilotPre(out long __state) { __state = Stopwatch.GetTimestamp(); }

        static void PilotPost(long __state, Pilot __instance)
        {
            try
            {
                if (!_on) return;
                long dt = Stopwatch.GetTimestamp() - __state;
                int slot = (__instance != null && __instance.aircraft != null && __instance.aircraft.Player != null) ? _playerSlot : _aiSlot;
                if (slot < 0 || slot >= _ticks.Length) return;
                _ticks[slot] += dt;
                _calls[slot]++;
                if (slot < _frameTicks.Length) _frameTicks[slot] += dt;
            }
            catch { }
        }

        // ---------------------------------------------------------------- Unity's frame stages
        // Perf.Phases: a marker before every stage of Unity's player loop (and one at the end of each phase). Each marker
        // closes the stage that was running and opens the next, so every stage's main thread time is the gap between two
        // markers. TimeUpdate holds the frame-rate wait, so it is the idle time.
        sealed class PerfMarker { }
        static PlayerLoopSystem _origLoop;
        static bool _phasesOn;
        static readonly List<string> _stageNames = new List<string>();
        static long[] _stageTicks = new long[0];
        static int _curStage = -1;
        static long _stageStamp;

        static PlayerLoopSystem Marker(int idx) =>
            new PlayerLoopSystem { type = typeof(PerfMarker), updateDelegate = () => Mark(idx) };

        static void Mark(int idx)
        {
            long now = Stopwatch.GetTimestamp();
            int cur = _curStage;
            if (cur >= 0 && cur < _stageTicks.Length)
            {
                long d = now - _stageStamp;
                _stageTicks[cur] += d;
                if (cur < _frameStage.Length) _frameStage[cur] += d;
            }
            _curStage = idx;
            _stageStamp = now;
            if (idx == 0) FrameBoundary(now);       // the first stage of a new frame: the last frame is complete
        }

        // ---------------------------------------------------------------- hitches (1.9.6 diagnostics, owner 2026-10-08)
        // One frame over Perf.HitchMs: log where that frame went (its biggest stages, timed parts and plugin ticks) and
        // whether a garbage collection finished inside it, to tell GC pauses from game work.
        static long[] _frameStage = new long[0];
        static long[] _frameTicks = new long[0];
        static readonly long[] _framePlug = new long[PlugNames.Length];
        static long _frameStart;
        static int _frameGc;
        static float _lastHitchLog = -999f;
        static int _hitches, _hitchesGc, _hitchesUnlogged;
        static int _waitStage = -1;            // the frame-rate wait (idle): not work, so not part of a hitch

        static void FrameBoundary(long now)
        {
            int gc = 0;
            try { gc = GC.CollectionCount(0); } catch { }
            try
            {
                int limit = HitchMs != null ? HitchMs.Value : 0;
                if (_frameStart != 0 && limit > 0)
                {
                    double ms = (now - _frameStart) * 1000.0 / Stopwatch.Frequency;
                    double wait = (_waitStage >= 0 && _waitStage < _frameStage.Length)
                        ? _frameStage[_waitStage] * 1000.0 / Stopwatch.Frequency : 0.0;
                    double work = ms - wait;
                    bool running = false;
                    try { running = MissionManager.IsRunning; } catch { }
                    if (running && work >= limit) Hitch(work, ms, gc - _frameGc);
                }
            }
            catch { }
            _frameStart = now;
            _frameGc = gc;
            _slowFrameInfo = null;
            Array.Clear(_frameStage, 0, _frameStage.Length);
            Array.Clear(_frameTicks, 0, _frameTicks.Length);
            Array.Clear(_framePlug, 0, _framePlug.Length);
        }

        static void Top(StringBuilder sb, string key, IList<string> names, long[] ticks, int n)
        {
            var order = new List<int>();
            for (int i = 0; i < ticks.Length && i < names.Count; i++) if (ticks[i] * 1000.0 / Stopwatch.Frequency >= 1.0) order.Add(i);
            order.Sort((x, y) => ticks[y].CompareTo(ticks[x]));
            sb.Append(",\"").Append(key).Append("\":{");
            for (int k = 0; k < order.Count && k < n; k++)
            {
                if (k > 0) sb.Append(',');
                sb.Append('"').Append(names[order[k]]).Append("\":").Append(F(ticks[order[k]] * 1000.0 / Stopwatch.Frequency));
            }
            sb.Append('}');
        }

        static void Hitch(double ms, double wallMs, int gcInFrame)
        {
            _hitches++;
            if (gcInFrame > 0) _hitchesGc++;
            float t = Time.realtimeSinceStartup;
            if (t - _lastHitchLog < 2f) { _hitchesUnlogged++; return; }
            _lastHitchLog = t;
            var sb = new StringBuilder(512);
            sb.Append("{\"t\":\"hitch\",\"ms\":").Append(F(ms))
              .Append(",\"wall_ms\":").Append(F(wallMs))
              .Append(",\"gc\":").Append(gcInFrame)
              .Append(",\"used_mb\":").Append(F(PerfHeld.UsedMb()))
              .Append(",\"heap_mb\":").Append(F(PerfHeld.HeapMb()));
            try { sb.Append(",\"units\":").Append(UnitRegistry.allUnits.Count); } catch { }
            Top(sb, "stages", _stageNames, _frameStage, 5);
            Top(sb, "timed", _labels, _frameTicks, 5);
            Top(sb, "plugin", PlugNames, _framePlug, 3);
            if (_slowFrameInfo != null) sb.Append(",\"slow_burst\":").Append(_slowFrameInfo);
            sb.Append('}');
            NukeStatsPlugin.Out(sb.ToString());
        }

        static void PhasesInstall()
        {
            try
            {
                var root = PlayerLoop.GetCurrentPlayerLoop();
                var phases = root.subSystemList;
                if (phases == null || phases.Length == 0) { NukeStatsPlugin.Log?.LogWarning("[perf] frame loop has no phases"); return; }
                _origLoop = root;
                _stageNames.Clear();
                var newPhases = new PlayerLoopSystem[phases.Length];
                for (int p = 0; p < phases.Length; p++)
                {
                    var ph = phases[p];
                    string pn = ph.type != null ? ph.type.Name : "Phase" + p;
                    var subs = ph.subSystemList ?? new PlayerLoopSystem[0];
                    var list = new List<PlayerLoopSystem>(subs.Length * 2 + 1);
                    foreach (var sub in subs)
                    {
                        _stageNames.Add(pn + "." + (sub.type != null ? sub.type.Name : "?"));
                        list.Add(Marker(_stageNames.Count - 1));
                        list.Add(sub);
                    }
                    list.Add(Marker(-1));                 // end of this phase
                    ph.subSystemList = list.ToArray();
                    newPhases[p] = ph;
                }
                root.subSystemList = newPhases;
                _stageTicks = new long[_stageNames.Count];
                _frameStage = new long[_stageNames.Count];
                _waitStage = _stageNames.IndexOf("TimeUpdate.WaitForLastPresentationAndUpdateTime");
                _frameStart = 0;                     // no hitch measured across the install
                _curStage = -1;
                PlayerLoop.SetPlayerLoop(root);
                _phasesOn = true;
                NukeStatsPlugin.Log?.LogInfo("[perf] frame stages timed: " + _stageNames.Count + " stages in " + phases.Length + " phases");
            }
            catch (Exception e) { NukeStatsPlugin.Log?.LogError("[perf] frame stages could not start: " + e.Message); }
        }

        static void PhasesUninstall()
        {
            try { PlayerLoop.SetPlayerLoop(_origLoop); }
            catch (Exception e) { NukeStatsPlugin.Log?.LogError("[perf] frame stages: restore failed: " + e.Message); }
            _phasesOn = false;
            _curStage = -1;
            _frameStart = 0;
            NukeStatsPlugin.Log?.LogInfo("[perf] frame stages off");
        }

        // ---------------------------------------------------------------- the plugin's own ticks
        internal static void LapStart() { _lapT = Stopwatch.GetTimestamp(); }

        internal static void Lap(int i)
        {
            long now = Stopwatch.GetTimestamp();
            _plugTicks[i] += now - _lapT;
            _framePlug[i] += now - _lapT;
            _lapT = now;
        }

        internal static void RankMapLoaded(long ticks) { _rankLoadTicks += ticks; _rankLoads++; }

        internal static void Logged(int chars) { _logChars += chars; _logLines++; }

        // ---------------------------------------------------------------- per frame (from PeriodicTick)
        static float _nextCheck;
        static float _runningSince = -1f;      // when the current mission started running (realtime), -1 = not running
        const float InstallAfterSec = 1f;      // owner 2026-10-06: 1 s after the mission starts (players are still loading)
        static int _appliedWorkers;               // what we set; 0 = we have not changed it

        internal static void Tick()
        {
            float now = Time.realtimeSinceStartup;
            if (_on)
            {
                float ms = Time.unscaledDeltaTime * 1000f;
                _frames++;
                _ftSum += ms;
                if (ms > _ftMax) _ftMax = ms;
                _ftHist[Mathf.Clamp((int)ms, 0, 250)]++;
                if (now - _windowStart >= ReportSec) Report(now);
            }
            if (now < _nextCheck) return;
            _nextCheck = now + 1f;
            bool running = false;
            try { running = MissionManager.IsRunning; } catch { }
            if (!running) { _runningSince = -1f; if (_spSeen.Count > 0) _spSeen.Clear(); }   // a new mission: "first" again
            else if (_runningSince < 0f) _runningSince = now;
            bool want = Timing != null && Timing.Value;
            // The hooks only go in once a mission has been running for 1 s. Installed in the first frames after
            // a start (Timing saved on in the config) they aborted the game (2026-10-06, S1: signal 6 inside
            // Harmony.Patch); switched on mid-mission they always worked.
            if (want && !_on && running && now - _runningSince >= InstallAfterSec) Install();
            else if (!want && _on) Uninstall();
            bool wantPhases = _on && Phases != null && Phases.Value;
            if (wantPhases && !_phasesOn) PhasesInstall();
            else if (!wantPhases && _phasesOn) PhasesUninstall();
            ApplyWorkers();
            ApplyFps();
            ApplySkips(running);
        }

        // ---------------------------------------------------------------- visual-only work the server can skip
        // Owner 2026-10-07: "dig into the audio and those other settings". Each switch is off by default and is undone
        // when switched off again. Checked against the live game code and its unit files (2026-10-07):
        //  - sound: NO switch. Pausing it (AudioListener.pause, test 8) made the sound stages grow the longer it was on
        //    (38 -> 143 ms/s on S1): the game keeps restarting sounds it sees stopped.
        //  - animators: all but five use the PilotController (the pilot model). The five others move landing gear on two
        //    aircraft and carry the gear colliders, so they are never touched.
        //  - constraints: ships' wake foam and the rams on four trucks and the howitzer have nothing under them that
        //    the game reads. Aircraft are never touched: one fighter's constraint steers its thrust direction.
        //  - particles: NO switch. Stopping them is out (flares and chaff are only removed, and stop decoying missiles,
        //    once their particles stop; pooled effects return on OnParticleSystemStopped). Stripping the screen-only
        //    modules (test 9 Perf.SkipParticleExtras) showed no clear gain on S1; owner 2026-10-07: "don't use the
        //    particle switch, remove it".
        static bool _scanAnim, _scanCon;
        static readonly HashSet<int> _seenUnits = new HashSet<int>();
        static readonly List<KeyValuePair<Animator, AnimatorCullingMode>> _animsOff = new List<KeyValuePair<Animator, AnimatorCullingMode>>();
        static readonly List<Behaviour> _consOff = new List<Behaviour>();
        static float _nextPrune;

        static void ApplySkips(bool running)
        {
            try
            {
                bool wantAnim = SkipPilotAnimation != null && SkipPilotAnimation.Value;
                bool wantCon = SkipVisualConstraints != null && SkipVisualConstraints.Value;
                if (!wantAnim && _animsOff.Count > 0) RestoreAnims();
                if (!wantCon && _consOff.Count > 0) RestoreConstraints();
                if (wantAnim != _scanAnim || wantCon != _scanCon || !running) _seenUnits.Clear();   // look at every unit again
                _scanAnim = wantAnim; _scanCon = wantCon;
                if (!running || (!wantAnim && !wantCon)) return;

                int anims = 0, cons = 0;
                foreach (Unit u in UnitRegistry.allUnits)
                {
                    if (u == null || !_seenUnits.Add(u.GetInstanceID())) continue;
                    if (wantAnim && (u is Aircraft || u is PilotDismounted))
                    {
                        foreach (Animator an in u.GetComponentsInChildren<Animator>(true))
                        {
                            RuntimeAnimatorController ctl = an.runtimeAnimatorController;
                            if (ctl == null || ctl.name != "PilotController" || an.cullingMode == AnimatorCullingMode.CullCompletely) continue;
                            _animsOff.Add(new KeyValuePair<Animator, AnimatorCullingMode>(an, an.cullingMode));
                            an.cullingMode = AnimatorCullingMode.CullCompletely;   // nothing is ever on screen on the server
                            anims++;
                        }
                    }
                    if (wantCon && u is Ship)
                    {
                        foreach (PositionConstraint c in u.GetComponentsInChildren<PositionConstraint>(true)) cons += Off(c, "wake");
                        foreach (RotationConstraint c in u.GetComponentsInChildren<RotationConstraint>(true)) cons += Off(c, "wake");
                    }
                    else if (wantCon && u is GroundVehicle)
                    {
                        foreach (AimConstraint c in u.GetComponentsInChildren<AimConstraint>(true))
                            cons += Off(c, "cylinder") + Off(c, "piston");
                    }
                }
                if (anims > 0 || cons > 0)
                    NukeStatsPlugin.Log?.LogDebug($"[perf] stopped {anims} pilot animators, {cons} visual constraints (now {_animsOff.Count} / {_consOff.Count})");
                float now = Time.realtimeSinceStartup;
                if (now >= _nextPrune)
                {
                    _nextPrune = now + 30f;
                    _animsOff.RemoveAll(p => p.Key == null);
                    _consOff.RemoveAll(c => c == null);
                }
            }
            catch (Exception e) { NukeStatsPlugin.Log?.LogError("[perf] skips: " + e.Message); }
        }

        // One constraint off if its object is named <prefix>... (wakefoam, wakeFoam, cylinder, piston, Piston).
        static int Off(Behaviour c, string prefix)
        {
            if (c == null || !c.enabled || !c.gameObject.name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return 0;
            c.enabled = false;
            _consOff.Add(c);
            return 1;
        }

        static void RestoreAnims()
        {
            int n = 0;
            foreach (var p in _animsOff) if (p.Key != null) { p.Key.cullingMode = p.Value; n++; }
            _animsOff.Clear();
            NukeStatsPlugin.Log?.LogInfo($"[perf] pilot animation back on ({n})");
        }

        static void RestoreConstraints()
        {
            int n = 0;
            foreach (var c in _consOff) if (c != null) { c.enabled = true; n++; }
            _consOff.Clear();
            NukeStatsPlugin.Log?.LogInfo($"[perf] visual constraints back on ({n})");
        }

        // Perf.TargetFps (owner 2026-10-07, his theory: a server that waits should not be held to exactly 60). The game
        // sets the target only when its state changes (GameManager.LimitFrameRate: 5 while waiting for players, else the
        // -limitframerate value); this re-applies ours once a second during a mission and hands it back at 0.
        static int _fpsApplied;                   // what we set; 0 = we have not changed it
        static void ApplyFps()
        {
            try
            {
                int want = TargetFps != null ? TargetFps.Value : 0;
                if (want != 0 && want < 60) want = 0;                    // never a cap below the game's 60
                if (want > 240) want = 240;
                bool waiting = GameManager.gameState == GameState.ServerWaiting;
                if (waiting) { _fpsApplied = 0; return; }                // the game's own 5 fps; it resets the rate itself
                if (want > 0)
                {
                    if (Application.targetFrameRate != want)
                    {
                        int was = Application.targetFrameRate;
                        Application.targetFrameRate = want;
                        _fpsApplied = want;
                        NukeStatsPlugin.Log?.LogInfo($"[perf] frame-rate target {was} -> {want}");
                    }
                }
                else if (_fpsApplied > 0)
                {
                    int game = GameManager.OverrideTargetFrameRate ?? 60;
                    int was = Application.targetFrameRate;
                    Application.targetFrameRate = game;
                    _fpsApplied = 0;
                    NukeStatsPlugin.Log?.LogInfo($"[perf] frame-rate target {was} -> {game} (the game's own)");
                }
            }
            catch (Exception e) { NukeStatsPlugin.Log?.LogError("[perf] frame-rate target: " + e.Message); }
        }

        static void ApplyWorkers()
        {
            try
            {
                int want = JobWorkers != null ? JobWorkers.Value : 0;
                int max = JobsUtility.JobWorkerMaximumCount;
                if (want > 0)
                {
                    int n = Mathf.Clamp(want, 1, Mathf.Max(1, max));
                    if (JobsUtility.JobWorkerCount != n)
                    {
                        int was = JobsUtility.JobWorkerCount;
                        JobsUtility.JobWorkerCount = n;
                        _appliedWorkers = n;
                        NukeStatsPlugin.Log?.LogInfo($"[perf] job workers {was} -> {n} (most the host allows: {max})");
                    }
                }
                else if (_appliedWorkers > 0)
                {
                    // back to the game's own choice (GameManager.SetJobCount)
                    int n = max > 2 ? Mathf.Clamp((max + 1) / 2, 2, 8) : JobsUtility.JobWorkerCount;
                    int was = JobsUtility.JobWorkerCount;
                    JobsUtility.JobWorkerCount = n;
                    _appliedWorkers = 0;
                    NukeStatsPlugin.Log?.LogInfo($"[perf] job workers {was} -> {n} (the game's own choice)");
                }
            }
            catch (Exception e) { NukeStatsPlugin.Log?.LogError("[perf] job workers: " + e.Message); }
        }

        static void ResetWindow()
        {
            _windowStart = Time.realtimeSinceStartup;
            _frames = 0; _ftSum = 0; _ftMax = 0;
            Array.Clear(_ftHist, 0, _ftHist.Length);
            Array.Clear(_ticks, 0, _ticks.Length);
            Array.Clear(_calls, 0, _calls.Length);
            Array.Clear(_plugTicks, 0, _plugTicks.Length);
            Array.Clear(_stageTicks, 0, _stageTicks.Length);
            _rankLoadTicks = 0; _rankLoads = 0; _logChars = 0; _logLines = 0;
            _hitches = 0; _hitchesGc = 0; _hitchesUnlogged = 0;
        }

        static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        static void Report(float now)
        {
            try
            {
                double sec = Math.Max(0.001, now - _windowStart);
                double toMsPerSec = 1000.0 / Stopwatch.Frequency / sec;
                int p95 = 0, seen = 0, need = (int)Math.Ceiling(_frames * 0.95);
                for (int i = 0; i < _ftHist.Length; i++) { seen += _ftHist[i]; if (seen >= need) { p95 = i; break; } }
                int ai = 0, players = 0, units = 0, aiTaxi = 0, aiAir = 0;
                try
                {
                    units = UnitRegistry.allUnits.Count;
                    foreach (var a in UnitRegistry.allAircraft)
                    {
                        if (a == null) continue;
                        if (a.Player != null) { players++; continue; }
                        ai++;
                        // 1.9.6 test 4 (AirbaseTaxi): AI still taxiing at an airbase vs AI up in the air
                        try
                        {
                            if (a.pilots != null && a.pilots.Length > 0 && a.pilots[0] != null && a.pilots[0].currentState is AIPilotTaxiState) aiTaxi++;
                            else if (a.radarAlt > 20f) aiAir++;
                        }
                        catch { }
                    }
                }
                catch { }
                var sb = new StringBuilder(1024);
                sb.Append("{\"t\":\"perf\",\"sec\":").Append(F(sec))
                  .Append(",\"frames\":").Append(_frames)
                  .Append(",\"ft_ms\":").Append(F(_frames > 0 ? _ftSum / _frames : 0))
                  .Append(",\"ft_p95\":").Append(p95)
                  .Append(",\"ft_max\":").Append(F(_ftMax))
                  .Append(",\"humans\":").Append(NukeStatsPlugin.Humans().Count)
                  .Append(",\"player_aircraft\":").Append(players)
                  .Append(",\"ai_aircraft\":").Append(ai)
                  .Append(",\"ai_taxiing\":").Append(aiTaxi)
                  .Append(",\"ai_airborne\":").Append(aiAir)
                  .Append(",\"units\":").Append(units)
                  .Append(",\"workers\":").Append(JobsUtility.JobWorkerCount)
                  .Append(",\"workers_max\":").Append(JobsUtility.JobWorkerMaximumCount)
                  .Append(",\"target_fps\":").Append(Application.targetFrameRate)
                  .Append(",\"skips\":{\"pilot_anims\":").Append(_animsOff.Count)
                  .Append(",\"constraints\":").Append(_consOff.Count).Append('}')
                  .Append(",\"ms\":{");
                for (int i = 0; i < _labels.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(_labels[i]).Append("\":").Append(F(_ticks[i] * toMsPerSec));
                }
                sb.Append("},\"calls\":{");
                for (int i = 0; i < _labels.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(_labels[i]).Append("\":").Append((int)Math.Round(_calls[i] / sec));
                }
                sb.Append("},\"plugin_ms\":{");
                double plugTotal = 0;
                bool firstPlug = true;
                for (int i = 0; i < PlugNames.Length; i++)
                {
                    if (PlugNames[i].Length == 0) continue;
                    double v = _plugTicks[i] * toMsPerSec;
                    plugTotal += v;
                    if (!firstPlug) sb.Append(',');
                    firstPlug = false;
                    sb.Append('"').Append(PlugNames[i]).Append("\":").Append(F(v));
                }
                sb.Append("},\"plugin_total_ms\":").Append(F(plugTotal))
                  .Append(",\"rankmap_loads\":").Append(_rankLoads)
                  .Append(",\"rankmap_ms\":").Append(F(_rankLoadTicks * 1000.0 / Stopwatch.Frequency))
                  .Append(",\"plugin_lines\":").Append(_logLines)
                  .Append(",\"plugin_kb\":").Append(F(_logChars / 1024.0));
                if (_phasesOn && _stageTicks.Length == _stageNames.Count)
                {
                    // every stage over 0.5 ms/s, biggest first, plus each phase's total
                    var order = new List<int>();
                    for (int i = 0; i < _stageTicks.Length; i++) if (_stageTicks[i] * toMsPerSec >= 0.5) order.Add(i);
                    order.Sort((x, y) => _stageTicks[y].CompareTo(_stageTicks[x]));
                    sb.Append(",\"stages\":{");
                    for (int k = 0; k < order.Count; k++)
                    {
                        if (k > 0) sb.Append(',');
                        sb.Append('"').Append(_stageNames[order[k]]).Append("\":").Append(F(_stageTicks[order[k]] * toMsPerSec));
                    }
                    var phaseTot = new Dictionary<string, double>();
                    var phaseOrder = new List<string>();
                    for (int i = 0; i < _stageTicks.Length; i++)
                    {
                        string ph = _stageNames[i].Substring(0, Math.Max(0, _stageNames[i].IndexOf('.')));
                        if (!phaseTot.ContainsKey(ph)) { phaseTot[ph] = 0; phaseOrder.Add(ph); }
                        phaseTot[ph] += _stageTicks[i] * toMsPerSec;
                    }
                    sb.Append("},\"phases\":{");
                    for (int k = 0; k < phaseOrder.Count; k++)
                    {
                        if (k > 0) sb.Append(',');
                        sb.Append('"').Append(phaseOrder[k]).Append("\":").Append(F(phaseTot[phaseOrder[k]]));
                    }
                    sb.Append('}');
                }
                PerfHeld.Append(sb, _hitches, _hitchesGc, _hitchesUnlogged);
                sb.Append('}');
                ResetWindow();
                NukeStatsPlugin.Out(sb.ToString());
            }
            catch (Exception e) { NukeStatsPlugin.Log?.LogError("[perf] report: " + e.Message); ResetWindow(); }
        }
    }
}
