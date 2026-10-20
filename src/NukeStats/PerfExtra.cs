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
    //    2026-10-15: fires that deal damage are never touched either (see IsDamagingFire), and objects that hold only
    //    particle systems are switched off, which takes them out of Unity's particle update (see SwitchOffQueued).
    //    2026-10-15: a system that ends its own object (stop action Destroy or Disable) is never zeroed, nothing under a
    //    missile is switched off, and a smoke trail emitter whose trail ends its own object keeps running under
    //    SkipVisualEffects (see EndsItself): an empty missile smoke trail removed itself, with its emitter, the moment
    //    the motor burned out instead of 15 s later, and a missile that went off right then threw in Motor.Destruct.
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
                "Skip work only a screen would show: wing vapour, the PropFan propeller spin and smoke trail emitters "
                + "(not the ones whose trail removes itself when empty). Nothing the game reads. Switched back on when this "
                + "is turned off.");
            SkipServerParticles = config.Bind("Perf", "SkipServerParticles", false,
                "The server's particle effects hold no particles, and the parts of units and effects that are only particles "
                + "are switched off (nobody sees them; each player's game draws its own). Flares, chaff, pooled effects, "
                + "fires that deal damage, missile parts and effects that remove themselves when empty are left alone. "
                + "Restored when this is turned off.");
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
            bool want = SelfTest.SkipsOverride ?? (SkipVisualEffects != null && SkipVisualEffects.Value);   // a self-test's own, in memory
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
            if (_trailRuns.Count > 1024) _trailRuns.Clear();   // keyed by instance id; missiles come and go, a re-check is cheap
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

        // MemHygiene.DropDeadVapour (2026-10-15), at a mission change with no mission running (every aircraft of the ended
        // mission is destroyed by then). The list is only read to switch vapour back on when SkipVisualEffects is turned
        // off, which skips destroyed entries; but each destroyed entry still kept its aircraft, that aircraft's FactionHQ and
        // most of its mission in memory, and the list was only pruned above 400 entries (test server: 69 MB after 10 missions).
        internal static int DropDeadVapour() => _vaporOff.Count == 0 ? 0 : _vaporOff.RemoveAll(b => b == null);

        // MemHygiene.ClearPluginMissionRefs: the particle table's destroyed entries now instead of 15 s later (the idle prune)
        internal static int PruneParticlesNow()
        {
            if (_ps.Count == 0) return 0;
            int before = _ps.Count;
            PrunePs(-1);
            return before - _ps.Count;
        }

        static void InstallVisualHooks()
        {
            _visualHooks = true;   // once, even if a patch fails
            var h = new Harmony(NukeStatsPlugin.Guid + ".visual");
            int ok = 0;
            foreach (var t in new[] { new[] { "PropFan", "Update", nameof(SkipPrefix) }, new[] { "TrailEmitter", "FixedUpdate", nameof(TrailPrefix) } })
            {
                try
                {
                    MethodInfo m = AccessTools.DeclaredMethod(AccessTools.TypeByName(t[0]), t[1]);
                    if (m == null) { NukeStatsPlugin.Log?.LogWarning($"[perf] visual effects: {t[0]}.{t[1]} not found"); continue; }
                    h.Patch(m, prefix: new HarmonyMethod(typeof(PerfExtra).GetMethod(t[2], BindingFlags.Static | BindingFlags.NonPublic)));
                    ok++;
                }
                catch (Exception e) { NukeStatsPlugin.Log?.LogError($"[perf] visual effects: {t[0]}.{t[1]}: {e.Message}"); }
            }
            NukeStatsPlugin.Log?.LogInfo($"[perf] visual effect skips in ({ok} of 2 hooks, plus wing vapour off per aircraft)");
        }

        // A self-test's settings have come out (SelfTest.BeginRestore): the skips follow your own setting from now on. The
        // once-a-second check only runs in a mission, so without this the test's skips would carry into the first second of
        // the next one.
        internal static void SelfTestEnded()
        {
            try { _skipVisual = SkipVisualEffects != null && SkipVisualEffects.Value && _visualHooks; }
            catch (Exception e) { NukeStatsPlugin.Log?.LogError("[perf] visual effects after the self-test: " + e.Message); }
        }

        // runs inside the game's own methods: false = skip the original while the switch is on
        static bool SkipPrefix() => !_skipVisual;

        // TrailEmitter.FixedUpdate only feeds particles into its trail system (TrailEmitter.Emit), but a trail that ends
        // its own object (stop action Destroy: the sustainer smoke of AAM-29 Scythe, NL-98, ARAD-116) lives on those
        // particles: empty, it removes itself and its emitter as soon as it stops, at the motor's burnout, and a missile
        // that goes off in the next two physics steps (the ARH seeker's miss check opens right then) throws in
        // Missile.Motor.Destruct (StopTrail on the removed emitter): no explosion, no RpcDetonate to the clients, never
        // destroyed. So such an emitter keeps running; the others (booster smoke, stop action None) are skipped as before.
        static readonly Dictionary<int, bool> _trailRuns = new Dictionary<int, bool>();
        static AccessTools.FieldRef<TrailEmitter, ParticleSystem> _trailSystemRef;
        static bool _trailSystemRefFailed;
        static int _trailsKept;

        static bool TrailPrefix(TrailEmitter __instance)
        {
            if (!_skipVisual) return true;
            try
            {
                int id = __instance.GetInstanceID();
                if (!_trailRuns.TryGetValue(id, out bool run))
                {
                    run = TrailMustRun(__instance);
                    _trailRuns[id] = run;
                    if (run) _trailsKept++;
                }
                return run;
            }
            catch { return true; }   // cannot tell: the game's own code runs (the safe side)
        }

        static bool TrailMustRun(TrailEmitter te)
        {
            if (_trailSystemRef == null && !_trailSystemRefFailed)
            {
                try { _trailSystemRef = AccessTools.FieldRefAccess<TrailEmitter, ParticleSystem>("trailSystem"); }
                catch (Exception e)
                {
                    _trailSystemRefFailed = true;
                    NukeStatsPlugin.Log?.LogWarning("[perf] TrailEmitter.trailSystem not readable (" + e.Message + "): every smoke trail emitter runs");
                }
            }
            if (_trailSystemRef == null) return true;
            ParticleSystem ps = _trailSystemRef(te);
            return ps == null || EndsItself(ps);
        }

        // A system that removes (Destroy) or switches off (Disable) its own object once it has stopped and holds no
        // particles: emptied, it ends at once instead of when its last particle dies, while game code may still hold it.
        // (Callback is only set by the game's own effect pool, which hands the effect back once it is no longer alive.)
        static bool EndsItself(ParticleSystem ps)
        {
            ParticleSystemStopAction a = ps.main.stopAction;
            return a == ParticleSystemStopAction.Destroy || a == ParticleSystemStopAction.Disable;
        }

        // ---------------------------------------------------------------- server particles
        // 2026-10-15 (owner: "Particle clean up sure"): the scene search for particle systems ran every 2 s and its lists
        // were never pruned. In Rising Tsunami on 10-14 one search took ~3 ms at the start and ~14 ms by the end (it
        // allocated a fresh array of every system each time), and the lists held 12,627 flare/chaff ids and tens of
        // thousands of systems, most long destroyed (the old clean-up only ran above 20,000 and then ran on every search).
        // Now: one search every 10 s, so a new effect is handled within 10 s; a system seen before costs one lookup by id;
        // anything destroyed leaves the list after each search, and between missions once the old mission is gone.
        sealed class PsEntry
        {
            public ParticleSystem Ps; public int OrigMax; public byte Kind; public int Seen;
            public GameObject Go; public byte Elig; public bool Off; public byte Revived;   // switched off (see SwitchOffQueued)
        }
        const byte KindZeroed = 0, KindDecoy = 1, KindFire = 2, KindEnds = 3;   // KindEnds: ends its own object (EndsItself)
        const float SweepSeconds = 10f;
        static readonly Dictionary<int, PsEntry> _ps = new Dictionary<int, PsEntry>();
        static readonly List<int> _psGone = new List<int>();
        static int _sweepNo, _nZeroed, _nDecoys, _nFires, _nEnds, _sweepsInWindow;
        static float _nextSweep, _nextIdlePrune;
        static long _sweepTicks, _sweepMaxTicks;
        static bool _psSettled;

        static void ParticlesTick(bool settled, float now)
        {
            bool on = SelfTest.SkipsOverride ?? (SkipServerParticles != null && SkipServerParticles.Value);
            if (!on)
            {
                if (_ps.Count > 0) RestoreParticles();
                _psSettled = false;
                return;
            }
            if (!settled)
            {
                // between missions: drop the old mission's systems once they are destroyed
                if (_psSettled) { _psSettled = false; _nextIdlePrune = now + 15f; _offQueue.Clear(); }
                if (_ps.Count > 0 && now >= _nextIdlePrune) { _nextIdlePrune = now + 15f; PrunePs(-1); }
                return;
            }
            if (!_psSettled) { _psSettled = true; _nextSweep = now; }   // first search as soon as the mission has settled
            if (now >= _nextSweep)
            {
                _nextSweep = now + SweepSeconds;
                SweepParticles();
            }
            if (_offQueue.Count > 0) SwitchOffQueued();
        }

        static void SweepParticles()
        {
            long t0 = Stopwatch.GetTimestamp();
            int sweep = ++_sweepNo;
            ParticleSystem[] all = UnityEngine.Object.FindObjectsByType<ParticleSystem>(FindObjectsSortMode.None);
            for (int i = 0; i < all.Length; i++)
            {
                ParticleSystem ps = all[i];
                if ((object)ps == null) continue;
                int id = ps.GetInstanceID();
                if (_ps.TryGetValue(id, out PsEntry e))
                {
                    e.Seen = sweep;
                    if (e.Off)
                    {
                        // found active again: the game switched it back on. Once more is fine; after that it is the
                        // game's to show and hide, and it is left on.
                        e.Off = false;
                        _revivedInWindow++;
                        if (++e.Revived >= 2) e.Elig = EligLeave; else _offQueue.Enqueue(id);
                    }
                    continue;
                }
                e = new PsEntry { Ps = ps, Seen = sweep, Go = ps.gameObject };
                if (ps.GetComponentInParent<IRFlare>(true) != null || ps.GetComponentInParent<RadarChaff>(true) != null)
                    e.Kind = KindDecoy;
                else if (IsDamagingFire(ps))
                    e.Kind = KindFire;
                else if (EndsItself(ps))
                    e.Kind = KindEnds;     // keeps its particles, so it ends when the game's own timing says (see TrailPrefix)
                else
                {
                    ParticleSystem.MainModule main = ps.main;
                    e.OrigMax = main.maxParticles;
                    main.maxParticles = 0;   // never Clear() (see the note at the top)
                    _offQueue.Enqueue(id);
                }
                _ps[id] = e;
            }
            PrunePs(sweep);
            long dt = Stopwatch.GetTimestamp() - t0;
            _sweepTicks += dt;
            if (dt > _sweepMaxTicks) _sweepMaxTicks = dt;
            _sweepsInWindow++;
        }

        // Drops destroyed systems (not found by search 'sweep', and gone; -1 = check every entry) and recounts the kinds.
        static void PrunePs(int sweep)
        {
            _psGone.Clear();
            int zeroed = 0, decoys = 0, fires = 0, ends = 0, off = 0;
            foreach (KeyValuePair<int, PsEntry> kv in _ps)
            {
                PsEntry e = kv.Value;
                if (e.Seen != sweep && e.Ps == null) { _psGone.Add(kv.Key); continue; }   // Unity's null: destroyed
                if (e.Kind == KindDecoy) decoys++; else if (e.Kind == KindFire) fires++; else if (e.Kind == KindEnds) ends++; else zeroed++;
                if (e.Off) off++;
            }
            for (int i = 0; i < _psGone.Count; i++) _ps.Remove(_psGone[i]);
            _nZeroed = zeroed; _nDecoys = decoys; _nFires = fires; _nEnds = ends; _nOff = off;
        }

        // 2026-10-15: a fire that burns things (DamageParticles with fireDamage > 0: ship ammo cook-off 15/s in 15 m for
        // 300 s, large fires 10/s in 20-30 m for 180-300 s, storage tanks 10/s in 80 m for 150 s, ammo fires 5-10/s for
        // 60 s; 8 of the game's 31 DamageParticles prefabs) only lives while one of its particle systems holds particles:
        // its SlowUpdate (every 1 s) destroys the whole object once particleCount is 0 everywhere, and only that object
        // deals the fire damage. With maxParticles 0 every such fire was removed within a few seconds of the sweep, so on
        // the server it dealt one or two seconds of fire damage instead of minutes. Those fires are left alone now. Fires
        // that deal no damage (smoke, small fires, fireballs, fragments) are still zeroed: only their look ends early.
        static AccessTools.FieldRef<DamageParticles, float> _fireDamageRef;
        static bool _fireDamageRefFailed;

        static bool IsDamagingFire(ParticleSystem ps)
        {
            DamageParticles dp = ps.GetComponentInParent<DamageParticles>(true);
            if (dp == null) return false;
            if (_fireDamageRef == null && !_fireDamageRefFailed)
            {
                try { _fireDamageRef = AccessTools.FieldRefAccess<DamageParticles, float>("fireDamage"); }
                catch (Exception e)
                {
                    _fireDamageRefFailed = true;
                    NukeStatsPlugin.Log?.LogWarning("[perf] DamageParticles.fireDamage not readable (" + e.Message + "): every fire is left alone");
                }
            }
            if (_fireDamageRef == null) return true;   // cannot tell: leave it alone (the safe side)
            try { return _fireDamageRef(dp) > 0f; } catch { return true; }
        }

        // ---- switching off what nobody sees (2026-10-15, owner: "Yes try that again")
        // Why zeroing alone still cost ~60 ms/s on S2: Unity's particle update (PreLateUpdate.ParticleSystemBeginUpdateAll
        // and PostLateUpdate.ParticleSystemEndUpdateAll) costs for every particle system on an active object each frame,
        // playing or not, with or without particles (stopping the playing ones saved 28%, switching off the objects 61%,
        // most of them stopped muzzle flashes and launch smoke). Test server, Rising Tsunami, BeginUpdateAll in 10 s windows
        // switched round for 13 minutes (lab 2): only zeroed 38.9 ms/s; Stop() on the playing loops 27.9 (the game plays
        // many again at once: jet nozzles, ship wakes and props check isPlaying several times a second); renderer off
        // 38.7; switching off the objects that hold only particle systems 15.2 (EndUpdateAll 7.5 -> 4.4); both 13.7.
        // Culling mode Pause and Pause() showed no clear gain (lab 1, minute windows).
        // So an object is switched off (SetActive(false)) when it is plainly a piece of effect and nothing else:
        //  - it and everything under it hold only Transform, ParticleSystem and ParticleSystemRenderer (no scripts,
        //    lights, colliders, audio or meshes, so nothing the game reads goes with it);
        //  - it is not a scene root, every system in it stops with "None" (no Destroy/Disable/Callback stop action, so
        //    no pool or clean-up waits on it), and nothing above it is a pooled effect, a fire (DamageParticles), a flare,
        //    chaff or a signal flare, a missile, or a particle system with a stop action.
        // The game's own calls keep working: Play()/Stop() on a switched-off system do nothing (muzzle flashes, launch
        // smoke, jet exhaust, wakes), isPlaying reads false, and none of the game's isPlaying checks on such systems
        // does more than switch the effect. Flares, chaff, fires that deal damage and pooled effects are never switched
        // off. If the game switches one back on, it is switched off again once; a second time it is left to the game.
        // At most ~2 ms of this per second (the first search of a mission finds ~3,000 systems), so a mission start
        // never stalls on it. Turning SkipServerParticles off switches everything back on.
        const byte EligUnknown = 0, EligOff = 1, EligLeave = 2;
        const double OffBudgetMs = 2.0;
        // the server particles are being switched off (after the skips came back on): the first search hasn't run yet or
        // its queue isn't empty. A self-test's step after its stock step waits for this.
        internal static bool ParticlesSettling
        {
            get
            {
                bool on = SelfTest.SkipsOverride ?? (SkipServerParticles != null && SkipServerParticles.Value);
                return on && (!_psSettled || _offQueue.Count > 0);
            }
        }
        static readonly Queue<int> _offQueue = new Queue<int>();
        static readonly List<Component> _offComps = new List<Component>();
        static readonly List<ParticleSystem> _offSystems = new List<ParticleSystem>();
        static int _nOff, _offInWindow, _revivedInWindow;
        static long _offTicks;

        static void SwitchOffQueued()
        {
            long t0 = Stopwatch.GetTimestamp();
            long budget = (long)(OffBudgetMs * Stopwatch.Frequency / 1000.0);
            while (_offQueue.Count > 0 && Stopwatch.GetTimestamp() - t0 < budget)
            {
                int id = _offQueue.Dequeue();
                if (!_ps.TryGetValue(id, out PsEntry e) || e.Kind != KindZeroed || e.Off) continue;
                try
                {
                    if (e.Ps == null || e.Go == null || !e.Go.activeSelf) continue;
                    if (e.Elig == EligUnknown) e.Elig = OnlyAnEffect(e.Ps) ? EligOff : EligLeave;
                    if (e.Elig != EligOff) continue;
                    e.Go.SetActive(false);
                    e.Off = true;
                    _nOff++;
                    _offInWindow++;
                }
                catch (Exception ex)
                {
                    e.Elig = EligLeave;
                    NukeStatsPlugin.Log?.LogWarning("[perf] server particles: could not switch off " + SafeName(e) + ": " + ex.Message);
                }
            }
            _offTicks += Stopwatch.GetTimestamp() - t0;
        }

        static bool OnlyAnEffect(ParticleSystem ps)
        {
            Transform t = ps.transform;
            if (t.parent == null) return false;                                   // a scene root: an effect that runs its own life
            _offComps.Clear();
            ps.GetComponentsInChildren<Component>(true, _offComps);
            for (int i = 0; i < _offComps.Count; i++)
            {
                Component c = _offComps[i];
                if (c == null) return false;                                      // a missing script: leave it
                if (!(c is Transform) && !(c is ParticleSystem) && !(c is ParticleSystemRenderer)) return false;
            }
            _offSystems.Clear();
            ps.GetComponentsInChildren<ParticleSystem>(true, _offSystems);
            for (int i = 0; i < _offSystems.Count; i++)
                if (_offSystems[i].main.stopAction != ParticleSystemStopAction.None) return false;
            for (Transform a = t.parent; a != null; a = a.parent)
            {
                if (a.GetComponent<ParticleEffectManager.PooledEffectReturner>() != null || a.GetComponent<DamageParticles>() != null
                    || a.GetComponent<IRFlare>() != null || a.GetComponent<RadarChaff>() != null || a.GetComponent<SpecialFlare>() != null
                    || a.GetComponent<Missile>() != null)   // a missile's motor holds its systems and stops them at the end
                    return false;
                ParticleSystem above = a.GetComponent<ParticleSystem>();
                if (above != null && above.main.stopAction != ParticleSystemStopAction.None) return false;
            }
            return true;
        }

        static string SafeName(PsEntry e)
        {
            try { return e.Go != null ? e.Go.name : "?"; } catch { return "?"; }
        }

        static void RestoreParticles()
        {
            int n = 0, on = 0;
            foreach (PsEntry e in _ps.Values)
            {
                if (e.Kind != KindZeroed || e.Ps == null) continue;
                if (e.Off && e.Go != null && !e.Go.activeSelf) { e.Go.SetActive(true); on++; }
                ParticleSystem.MainModule main = e.Ps.main;
                main.maxParticles = e.OrigMax;
                n++;
            }
            _ps.Clear();
            _offQueue.Clear();
            _nZeroed = 0; _nDecoys = 0; _nFires = 0; _nEnds = 0; _nOff = 0;
            NukeStatsPlugin.Log?.LogInfo($"[perf] server particles back on ({n} systems restored, {on} switched back on)");
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
            if (fps <= 0 || SelfTest.Overriding) return 0;   // a self-test measures with Unity's own cap, as the tester
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
            MissileBurnoutFix.Window(out int burnoutPs, out int burnoutTrails);
            sb.Append(",\"extra\":{\"visual_skip\":").Append(_skipVisual ? "true" : "false")
              .Append(",\"vapour_off\":").Append(_vaporOff.Count)
              .Append(",\"particles_zeroed\":").Append(_nZeroed)
              .Append(",\"particle_decoys\":").Append(_nDecoys)
              .Append(",\"particle_fires\":").Append(_nFires)
              .Append(",\"particle_ends_itself\":").Append(_nEnds)
              .Append(",\"trail_emitters_kept\":").Append(_trailsKept)
              .Append(",\"burnout_removed_systems\":").Append(burnoutPs)      // MissileBurnoutFix: removed entries left out
              .Append(",\"burnout_removed_trails\":").Append(burnoutTrails)
              .Append(",\"particles_off\":").Append(_nOff)
              .Append(",\"particles_off_queue\":").Append(_offQueue.Count)
              .Append(",\"particles_switched_off\":").Append(_offInWindow)
              .Append(",\"particles_revived\":").Append(_revivedInWindow)
              .Append(",\"particle_off_ms_per_s\":").Append((_offTicks * toMs / sec).ToString("0.00", CultureInfo.InvariantCulture))
              .Append(",\"particle_sweeps\":").Append(_sweepsInWindow)
              .Append(",\"particle_sweep_ms_per_s\":").Append((_sweepTicks * toMs / sec).ToString("0.00", CultureInfo.InvariantCulture))
              .Append(",\"particle_sweep_max_ms\":").Append((_sweepMaxTicks * toMs).ToString("0.00", CultureInfo.InvariantCulture))
              .Append(",\"pacer_fps\":").Append(PacingActive(out int fps) ? fps : 0)
              .Append(",\"pacer_waits\":").Append(_waits)
              .Append(",\"pacer_late_avg_us\":").Append((_waits > 0 ? _lateTicks * toMs * 1000.0 / _waits : 0).ToString("0.0", CultureInfo.InvariantCulture))
              .Append(",\"pacer_late_max_us\":").Append((_lateMax * toMs * 1000.0).ToString("0", CultureInfo.InvariantCulture))
              .Append('}');
            _sweepTicks = 0; _sweepMaxTicks = 0; _sweepsInWindow = 0; _offTicks = 0; _offInWindow = 0; _revivedInWindow = 0; _waits = 0; _lateTicks = 0; _lateMax = 0;
            _trailsKept = 0;
        }
    }
}
