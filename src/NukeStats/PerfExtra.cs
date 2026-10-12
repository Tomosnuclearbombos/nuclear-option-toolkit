using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.LowLevel;

namespace NukeStats
{
    // 1.9.2 (owner 2026-10-12: "that way forward is perfect"): three switches from the 10-12 test-server work, all off by
    // default so nothing changes until one is switched on (live, no restart; the hooks go in once a mission has run 1 s).
    //  - Perf.SkipVisualEffects: work only a screen would show. Wing vapour (VaporEffect, off), the PropFan propeller spin
    //    (its Update only animates; its FixedUpdate keeps the physics) and the smoke trail emitter (TrailEmitter). Checked
    //    in the game code: nothing else reads them. ConstantSpeedProp's Update is NOT skipped: it sets the rpm ratio the
    //    pitch governor and prop strikes use. Test server, 40 AI: about 10 ms/s.
    //  - Perf.SkipServerParticles: every player's game draws its own effects, nobody sees the server's. Each particle
    //    system keeps playing (the game's timers, pools and callbacks run as before) but may hold no particles
    //    (maxParticles 0). Never Clear(): emptying a system set to remove itself when empty made the missile motor's
    //    Play() throw (test server 10-12, twice). Flares and chaff are never touched: their particles are part of the
    //    decoy. Test server, 40 AI: particle work 87 -> 73 ms/s, about 3% more frames, 0 errors in 16 minutes.
    //  - Perf.FramePacer: N = the plugin paces frames to N a second itself (Unity's cap off): sleep to just before the
    //    frame is due, then wait out the last part. Unity's cap comes out ~7% short on the PvE host (60 -> 53, 100 -> 93);
    //    on Windows both hit the target, so this is for a live test on Linux.
    internal static class PerfExtra
    {
        internal static ConfigEntry<bool> SkipVisualEffects;
        internal static ConfigEntry<bool> SkipServerParticles;
        internal static ConfigEntry<int> FramePacer;

        internal static void Initialize(ConfigFile config)
        {
            SkipVisualEffects = config.Bind("Perf", "SkipVisualEffects", false,
                "Skip work only a screen would show: wing vapour, the PropFan propeller spin and smoke trail emitters. "
                + "Nothing the game reads. Switched back on when this is turned off.");
            SkipServerParticles = config.Bind("Perf", "SkipServerParticles", false,
                "The server's particle effects keep running but hold no particles (nobody sees them; each player's game draws "
                + "its own). Flares and chaff are left alone. Restored when this is turned off.");
            FramePacer = config.Bind("Perf", "FramePacer", 0,
                "0 = Unity's frame-rate cap (Perf.TargetFps). 60 to 240 = the plugin paces frames to exactly that many a "
                + "second instead (Unity's cap is off while it does). Only while a mission runs.");
        }

        static bool _running;
        static float _runningSince = -1f;

        // once a second from Perf.Tick
        internal static void Tick1s(bool running, float now)
        {
            _running = running;
            if (!running) { _runningSince = -1f; _vaporSeen.Clear(); }
            else if (_runningSince < 0f) _runningSince = now;
            bool settled = running && now - _runningSince >= 1f;
            try { VisualTick(settled); } catch (Exception e) { NukeStatsPlugin.Log?.LogError("[perf] visual effects: " + e.Message); }
            try { ParticlesTick(settled, now); } catch (Exception e) { NukeStatsPlugin.Log?.LogError("[perf] server particles: " + e.Message); }
            if (PacerFps() > 0 && settled) EnsurePacer();
        }

        // ---------------------------------------------------------------- visual effects
        static bool _visualHooks;
        static volatile bool _skipVisual;
        static readonly HashSet<int> _vaporSeen = new HashSet<int>();
        static readonly List<Behaviour> _vaporOff = new List<Behaviour>();

        static void VisualTick(bool settled)
        {
            bool want = SkipVisualEffects != null && SkipVisualEffects.Value;
            if (want && settled && !_visualHooks) InstallVisualHooks();
            _skipVisual = want && _visualHooks;
            if (!want)
            {
                if (_vaporOff.Count > 0)
                {
                    int n = 0;
                    foreach (Behaviour b in _vaporOff) if (b != null) { b.enabled = true; n++; }
                    _vaporOff.Clear();
                    _vaporSeen.Clear();
                    NukeStatsPlugin.Log?.LogInfo($"[perf] wing vapour back on ({n})");
                }
                return;
            }
            if (!settled) return;
            foreach (Aircraft a in UnitRegistry.allAircraft)
            {
                if (a == null || !_vaporSeen.Add(a.GetInstanceID())) continue;
                foreach (VaporEffect v in a.GetComponentsInChildren<VaporEffect>(true))
                {
                    if (v == null || !v.enabled) continue;
                    v.enabled = false;
                    _vaporOff.Add(v);
                }
            }
            if (_vaporOff.Count > 400) _vaporOff.RemoveAll(b => b == null);
        }

        static void InstallVisualHooks()
        {
            _visualHooks = true;   // once, even if a patch fails
            var h = new Harmony(NukeStatsPlugin.Guid + ".visual");
            var skip = new HarmonyMethod(typeof(PerfExtra).GetMethod(nameof(SkipPrefix), BindingFlags.Static | BindingFlags.NonPublic));
            int ok = 0;
            foreach (var t in new[] { new[] { "PropFan", "Update" }, new[] { "TrailEmitter", "FixedUpdate" } })
            {
                try
                {
                    MethodInfo m = AccessTools.DeclaredMethod(AccessTools.TypeByName(t[0]), t[1]);
                    if (m == null) { NukeStatsPlugin.Log?.LogWarning($"[perf] visual effects: {t[0]}.{t[1]} not found"); continue; }
                    h.Patch(m, prefix: skip);
                    ok++;
                }
                catch (Exception e) { NukeStatsPlugin.Log?.LogError($"[perf] visual effects: {t[0]}.{t[1]}: {e.Message}"); }
            }
            NukeStatsPlugin.Log?.LogInfo($"[perf] visual effect skips in ({ok} of 2 hooks, plus wing vapour off per aircraft)");
        }

        // runs inside the game's own methods: false = skip the original while the switch is on
        static bool SkipPrefix() => !_skipVisual;

        // ---------------------------------------------------------------- server particles
        static readonly Dictionary<ParticleSystem, int> _origMax = new Dictionary<ParticleSystem, int>();
        static readonly HashSet<int> _decoys = new HashSet<int>();
        static float _nextSweep;
        static long _sweepTicks;

        static void ParticlesTick(bool settled, float now)
        {
            bool want = SkipServerParticles != null && SkipServerParticles.Value && settled;
            if (want)
            {
                if (now < _nextSweep) return;
                _nextSweep = now + 2f;
                long t0 = Stopwatch.GetTimestamp();
                int newly = 0;
                foreach (ParticleSystem ps in UnityEngine.Object.FindObjectsByType<ParticleSystem>(FindObjectsSortMode.None))
                {
                    if (ps == null || _origMax.ContainsKey(ps)) continue;
                    int id = ps.GetInstanceID();
                    if (_decoys.Contains(id)) continue;
                    if (ps.GetComponentInParent<IRFlare>(true) != null || ps.GetComponentInParent<RadarChaff>(true) != null)
                    {
                        _decoys.Add(id);
                        continue;
                    }
                    ParticleSystem.MainModule main = ps.main;
                    _origMax[ps] = main.maxParticles;
                    main.maxParticles = 0;   // never Clear() (see the note at the top)
                    newly++;
                }
                _sweepTicks += Stopwatch.GetTimestamp() - t0;
                if (_origMax.Count > 20000)
                {
                    var dead = new List<ParticleSystem>();
                    foreach (var kv in _origMax) if (kv.Key == null) dead.Add(kv.Key);
                    foreach (var d in dead) _origMax.Remove(d);
                }
            }
            else if (_origMax.Count > 0)
            {
                int n = 0;
                foreach (KeyValuePair<ParticleSystem, int> kv in _origMax)
                {
                    if (kv.Key == null) continue;
                    ParticleSystem.MainModule main = kv.Key.main;
                    main.maxParticles = kv.Value;
                    n++;
                }
                _origMax.Clear();
                _decoys.Clear();
                NukeStatsPlugin.Log?.LogInfo($"[perf] server particles back on ({n} systems restored)");
            }
        }

        // ---------------------------------------------------------------- frame pacer (first step of every frame)
        struct NukeStatsPace { }
        static bool _pacerIn;
        static long _next, _period, _waits, _lateTicks, _lateMax;
        static double _sleepCostMs = 2.0;
        static bool _calibrated;

        internal static int PacerFps()
        {
            int fps = FramePacer != null ? FramePacer.Value : 0;
            if (fps <= 0) return 0;
            return Mathf.Clamp(fps, 60, 240);
        }

        // Perf.ApplyFps: while the plugin paces, Unity's cap is off
        internal static bool PacingActive(out int fps)
        {
            fps = PacerFps();
            return fps > 0 && _running && _pacerIn;
        }

        static void EnsurePacer()
        {
            try
            {
                PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();
                PlayerLoopSystem[] phases = root.subSystemList;
                if (phases == null) return;
                for (int i = 0; i < phases.Length; i++)
                {
                    if (phases[i].type != typeof(UnityEngine.PlayerLoop.TimeUpdate)) continue;
                    PlayerLoopSystem[] subs = phases[i].subSystemList ?? new PlayerLoopSystem[0];
                    foreach (PlayerLoopSystem s in subs) if (s.type == typeof(NukeStatsPace)) { _pacerIn = true; return; }
                    var list = new List<PlayerLoopSystem>(subs.Length + 1);
                    list.Add(new PlayerLoopSystem { type = typeof(NukeStatsPace), updateDelegate = Pace });
                    list.AddRange(subs);
                    phases[i].subSystemList = list.ToArray();
                    root.subSystemList = phases;
                    PlayerLoop.SetPlayerLoop(root);
                    if (!_pacerIn) NukeStatsPlugin.Log?.LogInfo("[perf] frame pacer in the frame loop");
                    _pacerIn = true;
                    return;
                }
            }
            catch (Exception e) { NukeStatsPlugin.Log?.LogError("[perf] frame pacer: " + e.Message); }
        }

        static void Calibrate()
        {
            _calibrated = true;
            double freqMs = Stopwatch.Frequency / 1000.0, sum = 0;
            for (int i = 0; i < 5; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                Thread.Sleep(1);
                sum += (Stopwatch.GetTimestamp() - t0) / freqMs;
            }
            _sleepCostMs = sum / 5;
            NukeStatsPlugin.Log?.LogInfo($"[perf] frame pacer: Thread.Sleep(1) takes {_sleepCostMs:0.00} ms here");
        }

        static void Pace()
        {
            try
            {
                if (!PacingActive(out int fps) || GameManager.gameState == GameState.ServerWaiting) { _next = 0; return; }
                if (!_calibrated) Calibrate();
                _period = Stopwatch.Frequency / fps;
                long now = Stopwatch.GetTimestamp();
                if (_next != 0 && now < _next)
                {
                    long target = _next;
                    double freqMs = Stopwatch.Frequency / 1000.0;
                    while (true)
                    {
                        now = Stopwatch.GetTimestamp();
                        long left = target - now;
                        if (left <= 0) break;
                        double leftMs = left / freqMs;
                        if (leftMs > _sleepCostMs + 0.4)
                        {
                            long t0 = now;
                            Thread.Sleep(1);
                            _sleepCostMs = _sleepCostMs * 0.95 + (Stopwatch.GetTimestamp() - t0) / freqMs * 0.05;
                        }
                        else if (leftMs > 0.2) Thread.Yield();
                        else Thread.SpinWait(20);
                    }
                    long late = now - target;
                    _lateTicks += late;
                    if (late > _lateMax) _lateMax = late;
                    _waits++;
                    _next = target + _period;     // on the grid: no drift
                }
                else
                {
                    _next = now + _period;        // the frame ran over: the next slot starts now, as Unity's cap does
                }
            }
            catch (Exception e) { _next = 0; NukeStatsPlugin.Log?.LogError("[perf] frame pacer: " + e.Message); }
        }

        // ---------------------------------------------------------------- the perf line (Perf.Report), then reset
        internal static void Append(StringBuilder sb, double sec)
        {
            double toMs = 1000.0 / Stopwatch.Frequency;
            sb.Append(",\"extra\":{\"visual_skip\":").Append(_skipVisual ? "true" : "false")
              .Append(",\"vapour_off\":").Append(_vaporOff.Count)
              .Append(",\"particles_zeroed\":").Append(_origMax.Count)
              .Append(",\"particle_sweep_ms_per_s\":").Append((_sweepTicks * toMs / sec).ToString("0.00", CultureInfo.InvariantCulture))
              .Append(",\"pacer_fps\":").Append(PacingActive(out int fps) ? fps : 0)
              .Append(",\"pacer_waits\":").Append(_waits)
              .Append(",\"pacer_late_avg_us\":").Append((_waits > 0 ? _lateTicks * toMs * 1000.0 / _waits : 0).ToString("0.0", CultureInfo.InvariantCulture))
              .Append(",\"pacer_late_max_us\":").Append((_lateMax * toMs * 1000.0).ToString("0", CultureInfo.InvariantCulture))
              .Append('}');
            _sweepTicks = 0; _waits = 0; _lateTicks = 0; _lateMax = 0;
        }
    }
}
