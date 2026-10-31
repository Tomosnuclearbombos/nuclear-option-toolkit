using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Mirage;
using NuclearOption.DedicatedServer;
using NuclearOption.DedicatedServer.Commands;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using ServerTester.Engine;
using Unity.Jobs.LowLevel.Unsafe;
using UnityEngine;

namespace NukeStats
{
    // The self-test (2026-10-18): the plugin runs the Server Tester's automated test on the server itself, with nobody on,
    // and leaves the results in <game folder>/NukeStats-ServerTests/<date>/ in the tester's own format. Started with
    // "setcfg|SelfTest.Start|true" or "selftest|start[|Ai=60;StepSeconds=15;...]", stopped with "selftest|stop", asked
    // with "selftest|status"; each change prints {"t":"selftest",...} and writes progress.json.
    //
    // How it runs (design: the self-test research of 2026-10-18):
    //  - The test mission is the tester's Stress Test, carried inside this DLL and served as the User mission "NukeStats
    //    Stress Test" only while a test runs (a prefix on MissionGroup.UserGroup.TryGetJson). It goes in with the server's
    //    own next-mission override (MissionRotation.OverrideNext), so the operator's rotation and its place are never
    //    touched; a next mission someone had queued before the test is put back after it.
    //  - HasPlayers answers yes while the test runs (a postfix), so the server loads and runs the mission with nobody on;
    //    only while the test mission is the next one or the one on, so no other mission ever loads with nobody on. The
    //    empty-server stop is pushed out once a second (noPlayerStopTime) and set to now at the end, which stops the
    //    mission; the server then goes back to waiting with the same next mission as before. A postfix on GameShouldStop
    //    lets go of the pretend players the moment the server stops the test mission itself (an end match from outside, a
    //    side winning), before its run loop could load the next mission with nobody on. A prefix and postfix on
    //    MissionRotation.RemoveBrokenMap do the same when the test mission fails to load, and keep the rotation's place
    //    (the game steps it back one, which would play the next mission twice).
    //  - Every setting the test changes is in memory only and never saved: the physics workers, the frame target, the
    //    visual skips, the frame pacer and hitch lines off, the timing hooks on, a measurement every 15 s, and the CPUs
    //    (every thread pinned for a step, then each put back as it was: following its cgroup's CPUs, or its own narrower
    //    ones). A crash or restart mid-test leaves the server exactly as it was configured.
    //  - The steps, picks, analysis and files are the tester's own code (SelfTestResults.cs, src/ServerTester/Engine).
    //  - Safety: it only starts with nobody on and the server empty for MinEmptySeconds; anyone connecting stops it at
    //    once (Mirage's Connected event, and a check every frame); the gameplay ticks that could end a match (Annihilate,
    //    the PvE timeout, team balance) sit still while it runs; a deadman on the config poller's thread lets go of the
    //    pretend players and the CPUs if the main thread stops ticking the test for 30 s; Application.quitting puts the
    //    CPUs back and writes what it has.
    // The hooks are plain methods of plain classes (never shared generic code; PatchSafety checks), installed at start so
    // no caller has an inlined copy of HasPlayers from before, and do nothing unless a test runs.
    internal static class SelfTest
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        internal const string MissionKeyName = "NukeStats Stress Test";

        internal static ConfigEntry<bool> Allow, Start, Alternate, VisualSkips, PinUnderQuota, CoreBench;
        internal static ConfigEntry<int> StepSeconds, Ai, WarmupSeconds, MaxCores, MaxWorkers, TargetStart, TargetStep, TargetSteps, SparePct, AiStep, AiMax, MinEmptySeconds;
        internal static ConfigEntry<string> UseCpus, ResultsDir;
#if TESTER_BUILD
        // The Server Tester's own build (it only runs on the private test server the tester sets up):
        //  - PretendPlayers: HasPlayers answers yes for the whole run, so the server runs its mission with nobody on (the
        //    tester's server config keeps an empty server from stopping it).
        //  - TestAiAircraft: every side's AI aircraft limit set so the total is this many (AiTick1s).
        internal static ConfigEntry<bool> PretendPlayers;
        internal static ConfigEntry<int> TestAiAircraft;
        static bool _testerPretend;
#endif
        static ConfigFile _config;

        internal static void Initialize(ConfigFile config)
        {
            _config = config;
            Allow = config.Bind("SelfTest", "Allow", true,
                "Allow the server test: the Server Tester's automated test, run by the plugin on this server with nobody on. "
                + "Results go in NukeStats-ServerTests in the game folder. It only starts when asked, stops the moment anyone "
                + "connects and saves nothing it changes. Off = a start is refused.");
            Start = config.Bind("SelfTest", "Start", false,
                "True starts the server test (setcfg|SelfTest.Start|true). It goes back to false at once, so a restart never starts one.");
            StepSeconds = config.Bind("SelfTest", "StepSeconds", 30, "Seconds each step measures, in whole 15 s measurements (15 to 120).");
            Ai = config.Bind("SelfTest", "Ai", 60, "AI aircraft the test runs with (half BDF, half PALA, as the Server Tester).");
            WarmupSeconds = config.Bind("SelfTest", "WarmupSeconds", 120, "Seconds the mission runs with the AI up before the first step.");
            Alternate = config.Bind("SelfTest", "Alternate", true,
                "Measure the baseline again after each core and worker pair, which takes heat and clock drift out. "
                + "The server test is the automated test, which always alternates, so off changes nothing.");
            MaxCores = config.Bind("SelfTest", "MaxCores", 0, "Most cores the pairs go up to. 0 = every fast core the server may use.");
            MaxWorkers = config.Bind("SelfTest", "MaxWorkers", 8, "Most physics workers the pairs go up to (no more than the game allows here).");
            UseCpus = config.Bind("SelfTest", "UseCpus", "", "CPUs the test may pin the server to, like 0-7,16-23. Empty = every CPU the server may use.");
            TargetStart = config.Bind("SelfTest", "TargetStart", 60, "First frame target tried (60 to 240).");
            TargetStep = config.Bind("SelfTest", "TargetStep", 10, "How much each frame target step adds.");
            TargetSteps = config.Bind("SelfTest", "TargetSteps", 7, "Frame targets tried (2 to 20). Every one is measured.");
            SparePct = config.Bind("SelfTest", "SparePct", 25, "The frame target picked leaves at least this % of each second spare (a full match costs more than the test).");
            AiStep = config.Bind("SelfTest", "AiStep", 10, "AI aircraft added each AI load step.");
            AiMax = config.Bind("SelfTest", "AiMax", 120, "Most AI aircraft the AI load round asks for.");
            VisualSkips = config.Bind("SelfTest", "VisualSkips", true,
                "Perf visual skips on for the test (pilot animation, visual constraints, visual effects, server particles), "
                + "as the Server Tester. Your own Perf settings come back after.");
            PinUnderQuota = config.Bind("SelfTest", "PinUnderQuota", false,
                "Under a host CPU limit (a container quota) the test measures only the physics workers, not pinned. "
                + "On = it also tries core counts up to one more than the limit.");
            CoreBench = config.Bind("SelfTest", "CoreBench", true, "Time each core first (about 1.5 s a core, as the Server Tester's speed test) so the fastest go first.");
            MinEmptySeconds = config.Bind("SelfTest", "MinEmptySeconds", 120, "Seconds the server has to be empty before the test starts.");
            ResultsDir = config.Bind("SelfTest", "ResultsDir", "NukeStats-ServerTests", "Results folder in the game folder, one per test.");
#if TESTER_BUILD
            PretendPlayers = config.Bind("Perf", "PretendPlayers", false, "SERVER TESTER BUILD ONLY: run the mission with nobody connected.");
            TestAiAircraft = config.Bind("Perf", "TestAiAircraft", 0, "SERVER TESTER BUILD ONLY: total AI aircraft for the test. 0 = the mission's own.");
            _testerPretend = PretendPlayers.Value;
            if (_testerPretend) NukeStatsPlugin.Log?.LogWarning("[tester] Server Tester build: the mission runs with nobody connected");
#endif
            // a start asked for before a restart never starts on its own
            if (Start.Value) { Start.Value = false; Save(); NukeStatsPlugin.Log?.LogInfo("[selftest] SelfTest.Start was left on: set to false, no test started"); }
            try
            {
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("NukeStats.StressTest.json"))
                    if (s != null) using (var r = new StreamReader(s)) _missionJson = r.ReadToEnd();
            }
            catch (Exception e) { NukeStatsPlugin.Log?.LogError("[selftest] the test mission: " + e.Message); }
            InstallHooks();
            try
            {
                _rotationRef = AccessTools.FieldRefAccess<DedicatedServerManager, MissionRotation>("missionRotation");
                _npstRef = AccessTools.FieldRefAccess<DedicatedServerManager, double>("noPlayerStopTime");
            }
            catch (Exception e) { NukeStatsPlugin.Log?.LogError("[selftest] the server's mission fields: " + e.Message); }
            try { _nextIndexRef = AccessTools.FieldRefAccess<MissionRotation, int>("_nextIndex"); }
            catch (Exception e) { NukeStatsPlugin.Log?.LogError("[selftest] the rotation's place: " + e.Message); }
            SelfTestHost.Initialize();
            _ctx = SynchronizationContext.Current;
            try { Application.quitting += OnQuitting; } catch { }
            MarkLeftovers();
            NukeStatsPlugin.Log?.LogInfo("[selftest] ready (" + (Ready(out string why) ? "can run" : "can't run here: " + why) + "; " + (Allow.Value ? "allowed" : "not allowed") + ")");
        }

        static void Save() { try { _config?.Save(); } catch (Exception e) { NukeStatsPlugin.Log?.LogError("[selftest] config save: " + e.Message); } }

        // ---------------------------------------------------------------- the three hooks (inert unless a test runs)
        internal static volatile bool _pretend;      // HasPlayers answers yes (for the test mission only)
        static volatile bool _serveMission;           // the test mission can be loaded
        static volatile string _serverEnded;          // the server stopped the test mission itself, and why
        static string _missionJson;
        static bool _hookPlayers, _hookMission, _hookStop, _hookBroken;
        static AccessTools.FieldRef<DedicatedServerManager, MissionRotation> _rotationRef;
        static AccessTools.FieldRef<DedicatedServerManager, double> _npstRef;
        static AccessTools.FieldRef<MissionRotation, int> _nextIndexRef;

        static void InstallHooks()
        {
            var h = new Harmony(NukeStatsPlugin.Guid + ".selftest");
            try
            {
                var m = AccessTools.DeclaredMethod(typeof(DedicatedServerManager), "HasPlayers", Type.EmptyTypes);
                if (m == null || m.IsGenericMethod || m.DeclaringType.IsGenericType) NukeStatsPlugin.Log?.LogError("[selftest] DedicatedServerManager.HasPlayers not found");
                else _hookPlayers = h.Patch(m, postfix: new HarmonyMethod(typeof(SelfTest).GetMethod(nameof(HasPlayersPost), BindingFlags.Static | BindingFlags.NonPublic))) != null;
            }
            catch (Exception e) { NukeStatsPlugin.Log?.LogError("[selftest] HasPlayers hook: " + e.Message); }
            try
            {
                var m = AccessTools.DeclaredMethod(typeof(MissionGroup.UserGroup), "TryGetJson", new[] { typeof(MissionKey), typeof(string).MakeByRefType() });
                if (m == null || m.IsGenericMethod || m.DeclaringType.IsGenericType) NukeStatsPlugin.Log?.LogError("[selftest] MissionGroup.UserGroup.TryGetJson not found");
                else _hookMission = h.Patch(m, prefix: new HarmonyMethod(typeof(SelfTest).GetMethod(nameof(TryGetJsonPre), BindingFlags.Static | BindingFlags.NonPublic))) != null;
            }
            catch (Exception e) { NukeStatsPlugin.Log?.LogError("[selftest] TryGetJson hook: " + e.Message); }
            try
            {
                var m = AccessTools.DeclaredMethod(typeof(DedicatedServerManager), "GameShouldStop", Type.EmptyTypes);
                if (m == null || m.IsGenericMethod || m.DeclaringType.IsGenericType || m.ReturnType != typeof(bool)) NukeStatsPlugin.Log?.LogError("[selftest] DedicatedServerManager.GameShouldStop not found");
                else _hookStop = h.Patch(m, postfix: new HarmonyMethod(typeof(SelfTest).GetMethod(nameof(GameShouldStopPost), BindingFlags.Static | BindingFlags.NonPublic))) != null;
            }
            catch (Exception e) { NukeStatsPlugin.Log?.LogError("[selftest] GameShouldStop hook: " + e.Message); }
            try
            {
                var m = AccessTools.DeclaredMethod(typeof(MissionRotation), "RemoveBrokenMap", new[] { typeof(MissionKeySaveable) });
                if (m == null || m.IsGenericMethod || m.DeclaringType.IsGenericType) NukeStatsPlugin.Log?.LogError("[selftest] MissionRotation.RemoveBrokenMap not found");
                else _hookBroken = h.Patch(m, prefix: new HarmonyMethod(typeof(SelfTest).GetMethod(nameof(RemoveBrokenMapPre), BindingFlags.Static | BindingFlags.NonPublic)),
                                           postfix: new HarmonyMethod(typeof(SelfTest).GetMethod(nameof(RemoveBrokenMapPost), BindingFlags.Static | BindingFlags.NonPublic))) != null;
            }
            catch (Exception e) { NukeStatsPlugin.Log?.LogError("[selftest] RemoveBrokenMap hook: " + e.Message); }
        }

        // runs inside the game's own checks: it must never throw. The pretend players only stand in for the test mission:
        // while it is the next mission (our override) or the one on, so a next mission someone else queued never loads with
        // nobody on.
        static void HasPlayersPost(DedicatedServerManager __instance, ref bool __result)
        {
#if TESTER_BUILD
            if (!__result && _testerPretend) __result = true;
#endif
            if (!__result && _pretend && ForTheTest(__instance)) __result = true;
        }

        static bool ForTheTest(DedicatedServerManager dsm)
        {
            try
            {
                if (dsm == null) return false;
                if (OursOn(dsm)) return true;
                var rot = _rotationRef != null ? _rotationRef(dsm) : null;
                var next = rot != null ? rot.NextOverrideOption : null;
                return next.HasValue && IsOurs(next.Value);
            }
            catch { return false; }
        }

        // The server stopping the test mission itself (an admin's end match or a lowered time limit, a side winning): the
        // pretend players go at once, in the same step, so the server goes back to waiting instead of loading the next
        // mission with nobody on (its run loop asks HasPlayers a frame later, before the test's once-a-second check could
        // see it). The test then stops and puts everything back. A stop of the operator's own paused mission at the start
        // of a test keeps them: the test mission comes next. Runs inside the game's own loop: it must never throw.
        static void GameShouldStopPost(DedicatedServerManager __instance, bool __result)
        {
            try
            {
                if (!__result || !_pretend || __instance == null || !IsOurs(__instance.CurrentMissionOption)) return;
                _pretend = false;
                _serverEnded = GameManager.gameResolution != GameResolution.Ongoing ? "the test mission ended on its own"
                    : "the server ended the test mission (someone ended the match or cut its time)";
            }
            catch { }
        }

        // The test mission failing to load: the server drops it with MissionRotation.RemoveBrokenMap and goes straight on
        // to its next mission. The pretend players go at once, in the same step, so that mission never loads with nobody on,
        // and the test then stops and puts everything back. RemoveBrokenMap also steps the rotation's place back one, which
        // would play the operator's next mission twice: the place is saved here and put back after it. The game's own method
        // still runs (it clears our next-mission override). Runs inside the game's own loop: it must never throw.
        static void RemoveBrokenMapPre(MissionRotation __instance, MissionKeySaveable key, out int __state)
        {
            __state = -1;
            try
            {
                if (!Active || !IsOurs(key)) return;
                _pretend = false;
                _serverEnded = "the test mission failed to load";
                if (__instance != null && _nextIndexRef != null) __state = _nextIndexRef(__instance);
            }
            catch { }
        }

        static void RemoveBrokenMapPost(MissionRotation __instance, int __state)
        {
            try { if (__state >= 0 && __instance != null && _nextIndexRef != null) _nextIndexRef(__instance) = __state; }
            catch { }
        }

        // the test mission, from inside this DLL, only while a test runs; any other mission reads its own file as before
        static bool TryGetJsonPre(MissionKey key, ref string json, ref bool __result)
        {
            try
            {
                if (!_serveMission || _missionJson == null || key.Key != MissionKeyName) return true;
                json = _missionJson;
                __result = true;
                return false;
            }
            catch { return true; }
        }

        // what has to work for a test to run at all here
        static bool Ready(out string why)
        {
            why = !_hookPlayers ? "the HasPlayers hook didn't go in"
                : !_hookMission ? "the test mission hook didn't go in"
                : !_hookStop ? "the GameShouldStop hook didn't go in"
                : !_hookBroken || _nextIndexRef == null ? "the RemoveBrokenMap hook didn't go in"
                : _missionJson == null ? "the test mission isn't inside this build"
                : _rotationRef == null || _npstRef == null ? "the game's missionRotation or noPlayerStopTime field has changed"
                : null;
            return why == null;
        }

        // ---------------------------------------------------------------- state
        enum St { Idle, Pending, Preparing, Loading, Instrument, Filling, Warmup, Running, Restoring }
        enum Phase { None, FillAi, Settle, Collect }
        static volatile St _state = St.Idle;
        static readonly object _lock = new object();       // the poller and the main thread both end a request that hasn't begun
        static SelfTestSettings _set;
        static SelfTestRun _run;
        static SelfTestHost.HostInfo _host;
        static string _folder;
        static string _final, _why;                        // how the last test ended: done, aborted or stopped, and why
        static float _stateAt, _pendingAt, _emptySince = -1f, _next1s;
        static string _waitWhy = "";
        static MissionOptions? _savedOverride;              // the next mission someone had queued before the test
        static MissionOptions? _startMission;               // the mission on (paused, nobody on) when the test began; null = waiting
        static int _testAi;                                 // the AI the test mission runs with now
        static bool _overrides;                             // the test's in-memory settings are in force
        static int _workersOverride, _targetOverride;
        static volatile bool _sampling;
        static Step _step;
        static Phase _phase;
        static float _phaseAt;
        static int _fillBest;
        static int _levelReached;                           // the AI that came up at the AI asked for now (the grid's other steps at it)
        static bool _matched;                               // a measurement of this step had its settings (the AI was short)
        static int _warmGot, _warmMaxAi;
        static Measurement _last;
        static volatile bool _benchDone, _benchCancel;
        static bool _quota;

        // the test is running (its settings in force, the test mission loading or on)
        static bool Testing => _state >= St.Loading && _state <= St.Running;
        // the test has the server: from the mission switch until it is back as it was
        internal static bool Active => _state >= St.Loading;

        // for Perf and PerfExtra: the test's own settings, in memory only (null = the operator's own)
        internal static int? WorkersOverride => _overrides ? _workersOverride : (int?)null;   // 0 = the game's own count, set explicitly
        internal static int? TargetOverride => _overrides ? _targetOverride : (int?)null;
        internal static bool? SkipsOverride => _overrides && _set != null ? _skipsStep ?? _set.VisualSkips : (bool?)null;
        static bool? _skipsStep;   // a step's own visual skips (false: the server as it comes); null = the test's
        // a self-test's "server as it comes" step: no visual skips and none of NukeStats's own gameplay fixes
        internal static bool StockStep => _overrides && _skipsStep == false;
        static bool _lastStock, _waitParticles;   // the step before was the stock one; this one waits for the particles
        internal static bool Overriding => _overrides;   // the frame pacer and the hitch lines are off, the timing hooks on

        // ---------------------------------------------------------------- requests (any thread)
        static SelfTestSettings _startRequest;
        static long _requestedAt;
        static volatile bool _stopRequest;

        // selftest|start[|args], selftest|stop, selftest|status (the config poller's thread or the main thread's command poll)
        internal static void Command(string body)
        {
            var parts = (body ?? "").Split(new[] { '|' }, 3);
            string sub = parts.Length > 1 ? parts[1].Trim().ToLowerInvariant() : "status";
            NukeStatsPlugin.Log?.LogInfo("[selftest] command: " + sub + (parts.Length > 2 && parts[2].Trim().Length > 0 ? " " + parts[2].Trim() : ""));
            if (sub == "start") Request(parts.Length > 2 ? parts[2] : "");
            else if (sub == "stop") Stop();
            else Emit();
        }

        static void Request(string args)
        {
            if (Allow == null || !Allow.Value) { EmitEnd("aborted", "not allowed here (SelfTest.Allow is off)"); return; }
            if (!Ready(out string why)) { EmitEnd("aborted", "can't run here: " + why); return; }
            var s = FromConfig();
            s.Apply(args);
            s.Clamp();
            lock (_lock)
            {
                if (_state != St.Idle || _startRequest != null) { NukeStatsPlugin.Log?.LogInfo("[selftest] a test is already on (" + StateName() + "): start ignored"); Emit(); return; }
                _startRequest = s;
                _requestedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                _final = null; _why = null;
            }
            if (s.Ignored.Count > 0) NukeStatsPlugin.Log?.LogWarning("[selftest] unknown settings, left out: " + string.Join(", ", s.Ignored));
            Emit();
        }

        static SelfTestSettings FromConfig() => new SelfTestSettings
        {
            StepSeconds = StepSeconds.Value, Ai = Ai.Value, WarmupSeconds = WarmupSeconds.Value, MaxCores = MaxCores.Value, MaxWorkers = MaxWorkers.Value,
            TargetStart = TargetStart.Value, TargetStep = TargetStep.Value, TargetSteps = TargetSteps.Value, SparePct = SparePct.Value, AiStep = AiStep.Value,
            AiMax = AiMax.Value, MinEmptySeconds = MinEmptySeconds.Value, Alternate = Alternate.Value, VisualSkips = VisualSkips.Value,
            PinUnderQuota = PinUnderQuota.Value, CoreBench = CoreBench.Value, UseCpus = UseCpus.Value ?? "",
        };

        static void Stop()
        {
            lock (_lock)
            {
                // a request that hasn't begun, or is still waiting for an empty server: nothing has changed yet
                if (_startRequest != null || _state == St.Pending)
                {
                    _startRequest = null;
                    if (_state == St.Pending) _state = St.Idle;
                    _final = "stopped"; _why = "stopped before it began";
                    Emit();
                    return;
                }
            }
            if (_state == St.Idle) { Emit(); return; }
            _stopRequest = true;   // the main thread stops it and puts everything back
        }

        // ---------------------------------------------------------------- the config poller's thread, about once a second
        static SynchronizationContext _ctx;
        static int _posted;
        static long _postedAt, _lastMainTick;
        static volatile bool _deadman;
        static int _pollerCount;
        static readonly SendOrPostCallback _postedTick = _ => { Interlocked.Exchange(ref _posted, 0); Tick(); };

        internal static void PollerTick()
        {
            try
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                long freq = System.Diagnostics.Stopwatch.Frequency;
                // setcfg|SelfTest.Start|true: back to false at once (saved), then the same start as selftest|start
                if (Start != null && Start.Value) { Start.Value = false; Save(); Request(""); }
                // the main thread's test work, while the server waits (nothing else ticks it then): posted to the game's own
                // main-thread queue; the game's remote command queue when that one doesn't run
                if (_state != St.Idle || _startRequest != null)
                {
                    bool stuck = Interlocked.CompareExchange(ref _posted, 0, 0) == 1 && now - Interlocked.Read(ref _postedAt) > 5 * freq;
                    if (Interlocked.CompareExchange(ref _posted, 1, 0) == 0 || stuck)
                    {
                        Interlocked.Exchange(ref _postedAt, now);
                        var rc = stuck ? ServerRemoteCommands.Instance : null;
                        if (rc != null) rc.RunOnMainThread(() => { Interlocked.Exchange(ref _posted, 0); Tick(); });
                        else if (_ctx != null) _ctx.Post(_postedTick, null);
                    }
                }
                if (_sampling)
                {
                    SelfTestHost.Sample();
                    if (++_pollerCount % 5 == 0) SelfTestHost.Repin();   // threads made since the last pass
                }
                // the deadman: the main thread hasn't ticked the test for 30 s, so let go of the pretend players and the CPUs
                long last = Interlocked.Read(ref _lastMainTick);
                if ((_pretend || SelfTestHost.Pinned) && last != 0 && now - last > 30 * freq)
                {
                    _pretend = false;
                    SelfTestHost.Restore();
                    if (!_deadman) { _deadman = true; NukeStatsPlugin.Log?.LogWarning("[selftest] main thread stalled for 30 s: pretend players off, CPUs put back"); }
                }
                // a start the main thread never picked up (nothing ran the plugin's main-thread work while the server waited)
                lock (_lock)
                {
                    if (_startRequest != null && _state == St.Idle && now - Interlocked.Read(ref _requestedAt) > 120 * freq)
                    {
                        _startRequest = null;
                        _final = "aborted"; _why = "couldn't start: the plugin's main-thread work didn't run while the server waited (start the server with -ServerRemoteCommands, or the test while a mission runs)";
                        NukeStatsPlugin.Log?.LogWarning("[selftest] " + _why);
                        Emit();
                    }
                }
            }
            catch (Exception e) { NukeStatsPlugin.Log?.LogError("[selftest] poller: " + e.Message); }
        }

        // ---------------------------------------------------------------- the main thread
        static int _lastFrame = -1;

        // every frame from PeriodicTick, and once a second from the poller's posted call while the server waits
        internal static void Tick()
        {
            try
            {
                int f = Time.frameCount;
                if (f == _lastFrame) return;
                _lastFrame = f;
                Interlocked.Exchange(ref _lastMainTick, System.Diagnostics.Stopwatch.GetTimestamp());
                if (_ctx == null) _ctx = SynchronizationContext.Current;
                if (_state == St.Idle && _startRequest == null) return;
                if (Testing)
                {
                    if (SomeoneOn()) { Abort("a player joined", false); return; }
                    if (SelfTestHost.IsWindows) SelfTestHost.MainCpuWindows = SelfTestHost.CurrentProcessorWindows();
                }
                float now = Time.realtimeSinceStartup;
                if (now < _next1s) return;
                _next1s = now + 1f;
                Step1s(now);
            }
            catch (Exception e)
            {
                NukeStatsPlugin.Log?.LogError("[selftest] " + e);
                try { if (Testing || _state == St.Preparing) Abort("an error: " + e.Message, false); } catch { }
            }
        }

        static DedicatedServerManager Dsm()
        {
            try { return NetworkManagerNuclearOption.i != null ? NetworkManagerNuclearOption.i.DedicatedServerManager : null; } catch { return null; }
        }

        // anyone on, or connecting (AllPlayers counts the server's own host player and players still logging in)
        static bool SomeoneOn()
        {
            try
            {
                var nm = NetworkManagerNuclearOption.i;
                if (nm == null || nm.Server == null) return false;
                if (nm.Server.AllPlayers.Count > 1) return true;
                var dsm = nm.DedicatedServerManager;
                return dsm != null && dsm.RealPlayerCount() > 0;
            }
            catch { return false; }
        }

        static bool IsOurs(MissionOptions o) => IsOurs(o.Key);
        static bool IsOurs(MissionKeySaveable k) => k.Name == MissionKeyName && string.Equals(k.Group, "User", StringComparison.OrdinalIgnoreCase);

        // the test mission is the one running
        static bool OursRunning(DedicatedServerManager dsm)
        {
            try { return dsm != null && IsOurs(dsm.CurrentMissionOption) && GameManager.gameState != GameState.ServerWaiting && MissionManager.IsRunning; } catch { return false; }
        }

        // the test mission is on, loading or not yet started: from the server taking it as the next mission (the empty scene
        // it loads first sets waiting, while a scene loads) until the server is back to waiting or on another mission
        static bool OursOn(DedicatedServerManager dsm)
        {
            try
            {
                return dsm != null && IsOurs(dsm.CurrentMissionOption)
                       && (GameManager.gameState != GameState.ServerWaiting || NetworkManagerNuclearOption.IsLoadingScene);
            }
            catch { return false; }
        }

        static bool SameMission(MissionOptions a, MissionOptions b) =>
            a.Key.Name == b.Key.Name && string.Equals(a.Key.Group ?? "", b.Key.Group ?? "", StringComparison.OrdinalIgnoreCase);

        static void Step1s(float now)
        {
            if (_stopRequest)
            {
                _stopRequest = false;
                if (Testing || _state == St.Preparing) { Abort("stopped by you", true); return; }
            }
            if (_deadman && Testing) { Abort("the server's main thread stalled for over 30 s", false); return; }
            SelfTestSettings req = null;
            lock (_lock)
            {
                if (_startRequest != null && _state == St.Idle)
                {
                    req = _startRequest;
                    _startRequest = null;
                    _set = req;
                    _run = null; _folder = null; _host = null; _last = null; _step = null; _phase = Phase.None; _savedOverride = null; _startMission = null;
                    _state = St.Pending;
                    _pendingAt = now;
                    _emptySince = -1f;
                    _deadman = false;
                    _serverEnded = null;
                }
            }
            if (req != null) { NukeStatsPlugin.Log?.LogInfo("[selftest] start: " + req.Text()); Emit(); }
            var dsm = Dsm();
            // the test mission ending on its own (a side won): the players are let go at once, so the server goes back to
            // waiting after its post-mission delay instead of loading the next mission with nobody on
            if (Testing && _state != St.Loading && OursOn(dsm) && GameManager.gameResolution != GameResolution.Ongoing) { Abort("the test mission ended on its own", false); return; }
            // the server stopped the test mission itself (GameShouldStopPost has let go of the pretend players already)
            string ended = _serverEnded;
            if (Testing && ended != null) { Abort(ended, false); return; }
            // the test mission's empty-server stop pushed out while it runs (the game reads it every 5 s)
            if (Testing && _state != St.Loading && OursRunning(dsm)) _npstRef(dsm) = Time.unscaledTimeAsDouble + 3600.0;
            switch (_state)
            {
                case St.Pending: PendingTick(now, dsm); break;
                case St.Preparing: if (_benchDone) Begin(now, dsm); else if (SomeoneOn()) Abort("a player joined", false); break;
                case St.Loading: LoadingTick(now, dsm); break;
                case St.Instrument: InstrumentTick(now, dsm); break;
                case St.Filling: FillingTick(now, dsm); break;
                case St.Warmup: WarmupTick(now, dsm); break;
                case St.Running: RunningTick(now, dsm); break;
                case St.Restoring: RestoringTick(now, dsm); break;
            }
        }

        // ---------------------------------------------------------------- waiting for an empty server
        static void PendingTick(float now, DedicatedServerManager dsm)
        {
            string wait = null;
            if (!Allow.Value) { EndBeforeBegin("aborted", "not allowed here (SelfTest.Allow is off)"); return; }
            if (!DedicatedServerManager.IsRunning || dsm == null) wait = "the server isn't running missions yet";
            else if (SomeoneOn()) wait = "players are on";
            else if (NetworkManagerNuclearOption.IsLoadingScene) wait = "a mission is loading";
            else if (GameManager.gameState != GameState.ServerWaiting && !MissionManager.IsRunning) wait = "the server is between missions";
            if (wait != null)
            {
                _emptySince = -1f;
                if (wait != _waitWhy) { _waitWhy = wait; NukeStatsPlugin.Log?.LogInfo("[selftest] waiting: " + wait); Emit(); }
            }
            else
            {
                if (_emptySince < 0f) _emptySince = now;
                if (now - _emptySince >= _set.MinEmptySeconds) { Prepare(now, dsm); return; }
                string w = "the server must be empty for " + _set.MinEmptySeconds + " s";
                if (w != _waitWhy) { _waitWhy = w; Emit(); }
            }
            if (now - _pendingAt > 600f) EndBeforeBegin("aborted", "the server wasn't empty and ready in 10 minutes (" + _waitWhy + ")");
        }

        static void EndBeforeBegin(string final, string why)
        {
            lock (_lock) { _state = St.Idle; _final = final; _why = why; }
            NukeStatsPlugin.Log?.LogInfo("[selftest] " + final + ": " + why);
            Emit();
        }

        // ---------------------------------------------------------------- preparing: the server as it is, the CPUs, the plan
        static void Prepare(float now, DedicatedServerManager dsm)
        {
            lock (_lock) { if (_state != St.Pending) return; _state = St.Preparing; }
            _stateAt = now;
            SelfTestHost.Snapshot();
            _host = SelfTestHost.Detect(SystemInfo.processorType, JobsUtility.JobWorkerMaximumCount, SystemInfo.processorCount);
            _quota = _host.QuotaCpus > 0 && _host.QuotaCpus < _host.Cpu.Cores.Count;
            _benchCancel = false;
            _benchDone = !(_set.CoreBench && (!_quota || _set.PinUnderQuota) && _host.Cpu.Cores.Count > 1);
            Emit();
            if (_benchDone) return;
            NukeStatsPlugin.Log?.LogInfo("[selftest] timing each core (" + _host.Cpu.Cores.Count + " cores, about " + (_host.Cpu.Cores.Count * CoreSpeed.SecondsPerCore).ToString("0", Inv) + " s)");
            var cpu = _host.Cpu;
            var t = new Thread(() =>
            {
                try { SelfTestHost.Bench(cpu, () => _benchCancel); }
                catch (Exception e) { NukeStatsPlugin.Log?.LogWarning("[selftest] core timing: " + e.Message); }
                finally { _benchDone = true; }
            }) { IsBackground = true, Name = "NukeStatsCoreTiming" };
            t.Start();
        }

        // the plan, the folder, the settings in, and the switch to the test mission
        static void Begin(float now, DedicatedServerManager dsm)
        {
            if (SomeoneOn()) { Abort("a player joined", false); return; }
            var cpu = new CpuSnapshot(_host.Cpu.Model, _host.Cpu.Cores);   // ranked again with the core timings
            _host.Cpu = cpu;
            var o = _set.Options(cpu, _host.JobWorkerMax, _quota ? _host.QuotaCpus : 0);
            _run = new SelfTestRun(o, DateTime.UtcNow) { LastRound = _set.LastRound };
            string onlyAi = _set.AiOnly ? _run.AiOnly(_set.Pair, _set.Target > 0 ? _set.Target : TestPlan.Compare(o)) : null;
            if (onlyAi != null) { EndBeforeBegin("aborted", onlyAi); SelfTestHost.Restore(); return; }
            _folder = null;
            try
            {
                string dir = Path.Combine(Paths.GameRootPath, string.IsNullOrWhiteSpace(ResultsDir.Value) ? "NukeStats-ServerTests" : ResultsDir.Value.Trim());
                _folder = Path.Combine(dir, _run.Started.ToString("yyyy-MM-dd_HHmm", Inv));
                Directory.CreateDirectory(_folder);
                foreach (var old in new[] { "selftest.log", "results.json", "measurements.csv", "report.txt" })
                    try { File.Delete(Path.Combine(_folder, old)); } catch { }
            }
            catch (Exception e) { _folder = null; NukeStatsPlugin.Log?.LogError("[selftest] results folder: " + e.Message); }
            if (_folder == null) { EndBeforeBegin("aborted", "couldn't make the results folder"); SelfTestHost.Restore(); return; }
            Say("NukeStats " + NukeStatsPlugin.Version + " server test: the Server Tester's automated test, on this server");
            Say("CPU: " + cpu.Model + ", " + cpu.Cores.Count + " cores / " + cpu.Threads + " threads the server may use (CPUs " + CpuList.Text(_host.Allowed) + ")"
                + (cpu.Hybrid ? ", " + TestPlan.FastCountOf(o) + " fast" : "") + (cpu.Cores.Any(c => !double.IsNaN(c.Score)) ? ", ranked by core timing" : ""));
            if (_host.QuotaCpus > 0 || _host.Container)
                Say("Host: " + (_host.Container ? "a container" + (_host.Pterodactyl ? " (Pterodactyl)" : "") : "not a container")
                    + (_host.QuotaCpus > 0 ? ", a CPU limit of " + _host.QuotaCpus.ToString("0.##", Inv) + " CPUs" : "") + (_host.Cpuset.Length > 0 ? ", CPU set " + _host.Cpuset : ""));
            if (_quota) Say("Host CPU limit below the core count: round 1 tests the physics workers on every core, not pinned"
                            + (_set.PinUnderQuota ? ", and core counts up to " + ((int)Math.Ceiling(_host.QuotaCpus) + 1) : ""));
            Say("Settings: " + _set.Text());
            if (o.Grid) Say("Grid test: AI " + string.Join(", ", o.GridAi) + ", frame targets " + string.Join(", ", o.GridTargets) + ", workers "
                            + string.Join(", ", o.GridWorkers.Select(w => w == 0 ? "Server Default" : w.ToString(Inv))) + ", cores " + string.Join(", ", o.GridCores)
                            + (o.GridDefault ? ", the game's own 60 fps and workers at each AI level" : "")
                            + ", the baseline (" + TestPlan.WorkersText(TestPlan.BaseWorkers(o)) + ", " + TestPlan.Compare(o) + " fps) between every " + o.RefEvery + " steps");
            else if (_run.Round == 3) Say("The AI load round only, on " + TestPlan.PairText(o, _run.PairPick) + " at " + _run.TargetPick + " fps");
            _levelReached = 0;
            Say("Plan: up to " + _run.StepsPlanned() + " steps, about " + (Eta() / 60).ToString("0", Inv) + " minutes");
            // the test's settings, in memory only
            _workersOverride = 0;                     // the game's own count, as out of the box
            _skipsStep = null; _lastStock = _waitParticles = false;
            // the AI load round only: its own target from the start; else (the grid too) the compare target
            _targetOverride = !o.Grid && _run.Round == 3 ? _run.TargetPick : TestPlan.Compare(o);
            _overrides = true;
            Perf.ApplyOverridesNow();
            _testAi = o.FirstAi;
            _serveMission = true;
            // the test mission loads before it is queued: a key that won't load makes the server drop it from the rotation,
            // which moves the operator's place in it
            if (!MissionSaveLoad.TryLoad(new MissionKey(MissionKeyName, MissionGroup.User), out _, out string err))
            {
                Abort("the test mission didn't load (" + (err ?? "no reason given") + ")", false);
                return;
            }
            Listen(true);
            SelfTestHost.ClearSamples();
            _sampling = true;
            _savedOverride = dsm.NextOverrideOption;   // put back after the test
            _startMission = GameManager.gameState != GameState.ServerWaiting ? dsm.CurrentMissionOption : (MissionOptions?)null;
            _serverEnded = null;
            _rotationRef(dsm).OverrideNext(new MissionOptions { Key = new MissionKeySaveable { Group = "User", Name = MissionKeyName }, MaxTime = 86400f });
            _pretend = true;
            // a mission paused with nobody on: end it, and the test mission comes next
            if (_startMission.HasValue) _npstRef(dsm) = 0.0;
            _state = St.Loading;
            _stateAt = now;
            Say("Switching to the test mission (" + MissionKeyName + ")" + (_savedOverride.HasValue ? "; the queued next mission, " + _savedOverride.Value.Key.Name + ", comes back after" : ""));
            Emit();
        }

        static void LoadingTick(float now, DedicatedServerManager dsm)
        {
            if (dsm == null) { Abort("the server stopped running missions", false); return; }
            if (OursRunning(dsm))
            {
                Say("Test mission running; starting the timing");
                _state = St.Instrument;
                _stateAt = now;
                Emit();
                return;
            }
            // the test mission is the server's next one (our override) until the server takes it, then the one it loads.
            // Neither: someone queued or cleared the next mission, or reloaded the rotation, before the server took ours (the
            // pretend players don't stand in for theirs, so the server keeps waiting), or ours didn't load and the server
            // dropped it: stop now.
            var rot = _rotationRef(dsm);
            var next = rot != null ? rot.NextOverrideOption : null;
            bool queued = next.HasValue && IsOurs(next.Value);
            if (!queued && !OursOn(dsm)) { Abort("the next mission was changed before the test mission loaded", false); return; }
            // the mission that was on when the test began (paused, nobody on) still stopping: its empty-server stop kept at
            // now, only for that mission and only while ours is still the next one
            if (queued && _startMission.HasValue && SameMission(dsm.CurrentMissionOption, _startMission.Value)
                && GameManager.gameState != GameState.ServerWaiting && MissionManager.IsRunning) _npstRef(dsm) = 0.0;
            if (now - _stateAt > 600f) Abort("timed out waiting for the test mission to load", false);
        }

        static void InstrumentTick(float now, DedicatedServerManager dsm)
        {
            if (!OursRunning(dsm)) { Abort("the test mission stopped", false); return; }
            if (Perf.On && Perf.PhasesOn)
            {
                Perf.RestartWindow();
                Say("Waiting for " + _run.Options.FirstAi + " AI aircraft");
                _run.AiReached = 0;
                _state = St.Filling;
                _stateAt = now;
                Emit();
                return;
            }
            if (now - _stateAt > 60f) Abort("the timing hooks didn't go in", false);
        }

        // 1. the AI fills up (to 90%, as the tester; 15 minutes at most)
        static void FillingTick(float now, DedicatedServerManager dsm)
        {
            if (!OursRunning(dsm)) { Abort("the test mission stopped", false); return; }
            if (_run.AiReached >= _run.Options.FirstAi * 0.9) { BeginWarmup(now); return; }
            if (now - _stateAt > 900f)
            {
                _run.Notes.Add("Only " + _run.AiReached + " of " + _run.Options.FirstAi + " AI aircraft came up, so the test ran with " + _run.AiReached + ".");
                BeginWarmup(now);
            }
        }

        static void BeginWarmup(float now)
        {
            Say("AI up: " + _run.AiReached + ". Warming up for " + (_run.Options.WarmupWindows * 15 / 60.0).ToString("0.#", Inv) + " minutes");
            _warmGot = 0;
            _warmMaxAi = 0;
            _state = St.Warmup;
            _stateAt = now;
            Emit();
        }

        // 2. the warm-up: the game's own physics workers, measured
        static void WarmupTick(float now, DedicatedServerManager dsm)
        {
            if (!OursRunning(dsm)) { Abort("the test mission stopped", false); return; }
            int n = _run.Options.WarmupWindows;
            if (_warmGot < n && now - _stateAt <= n * 15 * 3 + 120) return;
            if (_run.GameWorkers > 0) Say("Workers: Server Default (" + _run.GameWorkers + ")");
            // the load the steps really run with: the fill stops at 90%, and the AI keeps coming up after it
            if (_warmMaxAi > _run.AiReached) _run.AiReached = _warmMaxAi;
            _state = St.Running;
            _stateAt = now;
            NextStep(now);
        }

        // ---------------------------------------------------------------- 3. the steps
        static void NextStep(float now)
        {
            int round = _run.Round;
            var s = _run.Next(Say);
            if (_run.Round != round) Say(_run.Round == 2 ? "Round 2: frame target on the pick" : "Round 3: AI load on both picks");
            if (s == null) { Finish(now); return; }
            _step = s;
            _matched = false;
            if (s.Kind == "ai" || s.Ai != _testAi)
            {
                // more AI (an AI load step, or the grid's next level): wait for it to fill before measuring
                _testAi = s.Ai;
                _fillBest = 0;
                _phase = Phase.FillAi;
                _phaseAt = now;
                Say("Step " + s.Number + ": waiting for " + s.Ai + " AI aircraft");
                Emit();
                return;
            }
            if (_levelReached > 0) s.AiReachedForStep = _levelReached;   // the rest of a grid level
            Apply(s, now);
        }

        // the step's CPUs, workers and frame target, then 3 s to settle before its measurements start
        static void Apply(Step s, float now)
        {
            int[] all = TestPlan.AllCpus(_run.Options);
            bool unpinned = s.Cpus == null || s.Cpus.Length == 0 || s.Cpus.OrderBy(x => x).SequenceEqual(all);
            s.CpusGot = unpinned ? SelfTestHost.Restore() : SelfTestHost.Pin(s.Cpus);
            _workersOverride = s.Workers;
            _targetOverride = s.Target;
            _skipsStep = s.Skips;
            // after the stock step the particles go back off a few at a time: this step measures once they have
            _waitParticles = _lastStock && (s.Skips ?? _set.VisualSkips);
            _lastStock = s.Skips == false;
            Perf.ApplyOverridesNow();
            if (s.CpusGot.Length > 0 && s.Cpus != null && !s.CpusGot.SequenceEqual(s.Cpus.OrderBy(x => x)))
                s.Note = "the system gave the server CPUs " + CpuList.Text(s.CpusGot) + " instead of " + CpuList.Text(s.Cpus);
            _phase = Phase.Settle;
            _phaseAt = now;
            Emit();
        }

        static void RunningTick(float now, DedicatedServerManager dsm)
        {
            if (!OursRunning(dsm)) { Abort("the test mission stopped", false); return; }
            var s = _step;
            if (s == null) { NextStep(now); return; }
            if (_phase == Phase.FillAi)
            {
                if (_fillBest >= s.Ai * 0.9 || now - _phaseAt >= 600f)
                {
                    s.AiReachedForStep = _levelReached = _fillBest;
                    if (_fillBest < s.Ai * 0.9) s.Note = "only " + _fillBest + " of " + s.Ai + " AI aircraft came up";
                    Apply(s, now);
                }
                return;
            }
            if (_phase == Phase.Settle)
            {
                // the stock step waits 2 minutes: the effects the skips emptied (smoke, small fires) build up again first
                if (now - _phaseAt < (StockStep ? 120f : 3f)) return;
                if (_waitParticles && PerfExtra.ParticlesSettling && now - _phaseAt < 120f) return;
                Perf.RestartWindow();   // the step's first measurement starts now, all on its own settings
                _phase = Phase.Collect;
                _phaseAt = now;
                return;
            }
            if (_phase == Phase.Collect && now - _phaseAt > (TestPlan.KeepOf(_run.Options, s) + 2) * 15 * 3 + 120)
            {
                if (s.Note.Length == 0) s.Note = "timed out: the measurements never settled (" + (_matched ? "AI load too low" : "the setting didn't take") + ")";
                StepDone(s, now);
            }
        }

        static void StepDone(Step s, float now)
        {
            _phase = Phase.None;
            _step = null;
            Say(_run.StepLine(s));
            NextStep(now);
        }

        // ---------------------------------------------------------------- one 15 s measurement (Perf.Report, the main thread)
        internal static void OnWindow(double sec, int frames, double p95, double p99, double ftMax, int humans, int ai, int units, int workers, int target,
                                      double physics, double spare, double fixedPhase, double jobsWait)
        {
            try
            {
                if (!Testing || _run == null || sec <= 0) return;
                var now = DateTime.UtcNow;
                var w = new Measurement
                {
                    At = now, AtUtc = now, Sec = sec, Fps = frames / sec, P95 = p95, P99 = p99, FtMax = ftMax, Humans = humans, Ai = ai, Units = units,
                    Workers = workers, Target = target, Physics = physics, Spare = spare, FixedPhase = fixedPhase, JobsWait = jobsWait,
                };
                if (double.IsNaN(w.Spare)) return;   // the frame stages aren't timed yet
                SelfTestHost.Attach(w);
                _last = w;
                if (humans > 0) { Abort("a player joined", false); return; }
                float rt = Time.realtimeSinceStartup;
                switch (_state)
                {
                    case St.Filling:
                        _run.AiReached = Math.Max(_run.AiReached, w.Ai);
                        break;
                    case St.Warmup:
                        _warmGot++;
                        _warmMaxAi = Math.Max(_warmMaxAi, w.Ai);
                        if (w.Workers > 0) _run.GameWorkers = w.Workers;   // nothing has set them yet: the game's own
                        break;
                    case St.Running:
                        var s = _step;
                        if (s == null) break;
                        if (_phase == Phase.FillAi) { _fillBest = Math.Max(_fillBest, w.Ai); break; }
                        if (_phase != Phase.Collect) break;
                        bool workersOk = s.Workers > 0 ? w.Workers == s.Workers : _run.GameWorkers <= 0 || w.Workers == _run.GameWorkers;
                        if (workersOk && w.Target == s.Target && w.Humans == 0) _matched = true;
                        if (!_run.Counts(s, w)) break;
                        s.Windows.Add(w);
                        if (s.Windows.Count >= TestPlan.KeepOf(_run.Options, s)) StepDone(s, rt);
                        break;
                }
                Emit();
            }
            catch (Exception e)
            {
                NukeStatsPlugin.Log?.LogError("[selftest] measurement: " + e);
                try { Abort("an error: " + e.Message, false); } catch { }
            }
        }

        // ---------------------------------------------------------------- the end: results, then everything put back
        static void Finish(float now)
        {
            _final = "done";
            _why = "";
            BeginRestore(now);
            WriteResults();
        }

        // stops the test where it is: everything put back at once, then what it measured written
        static void Abort(string why, bool byYou)
        {
            if (_state == St.Restoring || _state == St.Idle) return;
            float now = Time.realtimeSinceStartup;
            if (_state == St.Pending) { EndBeforeBegin(byYou ? "stopped" : "aborted", why); return; }
            if (_state == St.Preparing && _run == null)
            {
                _benchCancel = true;
                SelfTestHost.Restore();
                EndBeforeBegin(byYou ? "stopped" : "aborted", why);
                return;
            }
            _final = byYou ? "stopped" : "aborted";
            _why = why;
            if (_run != null) _run.StoppedBecause = why;
            Say("The test stopped early: " + why);
            BeginRestore(now);
            WriteResults();
        }

        static void BeginRestore(float now)
        {
            _pretend = false;
            _overrides = false;
            _skipsStep = null;
            _sampling = false;
            _step = null;
            _phase = Phase.None;
            _testAi = 0;
            try { Perf.ApplyOverridesNow(); } catch (Exception e) { NukeStatsPlugin.Log?.LogError("[selftest] settings back: " + e.Message); }
            PerfExtra.SelfTestEnded();   // the visual skips back to your own setting now, not in the next mission's first second
            SelfTestHost.Restore();
            Say("The server's settings and CPUs are back");
            Listen(false);
            var dsm = Dsm();
            try
            {
                // the next mission: ours (or ours already taken): the one queued before the test, or none; someone else's: left
                var rot = dsm != null ? _rotationRef(dsm) : null;
                if (rot != null)
                {
                    var next = rot.NextOverrideOption;
                    if (!next.HasValue || IsOurs(next.Value))
                    {
                        if (_savedOverride.HasValue) rot.OverrideNext(_savedOverride.Value);
                        else rot.ClearOverride();
                    }
                }
            }
            catch (Exception e) { NukeStatsPlugin.Log?.LogError("[selftest] next mission back: " + e.Message); }
            _state = St.Restoring;
            _stateAt = now;
            EndOurs(dsm);
            Emit();
        }

        // the seconds the server waits after a mission ends with a result, from the config it loaded (0 if it can't be read)
        static float PostMissionDelay(DedicatedServerManager dsm)
        {
            try { var c = dsm != null ? dsm.Config : null; return c != null && c.PostMissionDelay > 0f ? c.PostMissionDelay : 0f; }
            catch { return 0f; }
        }

        // the test mission ends: at once with players on (they go on to the next mission), else the empty-server stop
        static void EndOurs(DedicatedServerManager dsm)
        {
            try
            {
                if (!OursOn(dsm)) return;
                if (dsm.RealPlayerCount() > 0) dsm.SetTimeRemaining(0f);
                else _npstRef(dsm) = 0.0;
            }
            catch (Exception e) { NukeStatsPlugin.Log?.LogError("[selftest] ending the test mission: " + e.Message); }
        }

        static void RestoringTick(float now, DedicatedServerManager dsm)
        {
            bool on = OursOn(dsm);
            if (on)
            {
                EndOurs(dsm);   // again each second: a mission that was still loading resets the stop when it starts
                // a side won: the server waits its own post-mission delay (PostMissionDelay in its config) before it moves on
                bool won = GameManager.gameResolution != GameResolution.Ongoing;
                float delay = won ? PostMissionDelay(dsm) : 0f;
                if (now - _stateAt < (NetworkManagerNuclearOption.IsLoadingScene ? 600f : 60f + delay)) return;
                NukeStatsPlugin.Log?.LogWarning(won
                    ? "[selftest] the test mission ended but is still on " + (now - _stateAt).ToString("0", Inv) + " s after the test stopped (post-mission delay "
                      + delay.ToString("0", Inv) + " s): letting go, the server moves on by itself"
                    : "[selftest] the test mission is still on after a minute: it stops when its empty-server time runs out");
            }
            _serveMission = false;   // a mission already loaded doesn't read its file again
            lock (_missionAi) _missionAi.Clear();
            Perf.SelfTestEnded();
            lock (_lock) _state = St.Idle;
            Say(_final == "done" ? "Finished; the server is back as it was." : "Stopped (" + _why + "); the server is back as it was.");
            Emit();
        }

        static void WriteResults()
        {
            if (_run == null || _folder == null) return;
            try
            {
                Results.ToolVersion = "(NukeStats plugin " + NukeStatsPlugin.Version + ", on the server)";
                var r = Results.Analyse(_run);
                SelfTestFiles.Write(_folder, _run, r, NukeStatsPlugin.Version, ServerBuild(), OsText(), HostRecord());
                foreach (var line in r.Report(_run).Split('\n')) if (line.Trim().Length > 0) NukeStatsPlugin.Log?.LogInfo("[selftest] " + line.TrimEnd('\r'));
                Say("Results in " + _folder);
            }
            catch (Exception e) { NukeStatsPlugin.Log?.LogError("[selftest] results: " + e); }
        }

        static Dictionary<string, object> HostRecord()
        {
            if (_host == null) return null;
            SelfTestHost.Throttled(_host.CpuStatPath, out long periods, out long usec);
            return new Dictionary<string, object>
            {
                ["container"] = _host.Container, ["pterodactyl"] = _host.Pterodactyl, ["quotaCpus"] = Math.Round(_host.QuotaCpus, 2),
                ["allowedCpus"] = CpuList.Text(_host.Allowed), ["cpuset"] = _host.Cpuset, ["cgroup"] = _host.Cgroup,
                ["jobWorkerMax"] = _host.JobWorkerMax, ["processorCount"] = _host.ProcessorCount,
                // how often the host's CPU limit held the server back during the test (-1 = not known here)
                ["limitThrottledPeriods"] = periods >= 0 && _host.ThrottledStart >= 0 ? periods - _host.ThrottledStart : -1,
                ["limitThrottledSeconds"] = usec >= 0 && _host.ThrottledUsecStart >= 0 ? Math.Round((usec - _host.ThrottledUsecStart) / 1e6, 1) : -1,
            };
        }

        // the dedicated server's Steam build, as the tester records it
        static string ServerBuild()
        {
            try
            {
                foreach (var dir in new[] { Paths.GameRootPath, Path.GetDirectoryName(Paths.GameRootPath) ?? "" })
                {
                    string acf = Path.Combine(dir, "steamapps", "appmanifest_3930080.acf");
                    if (!File.Exists(acf)) continue;
                    var m = Regex.Match(File.ReadAllText(acf), "\"buildid\"\\s+\"(\\d+)\"");
                    if (m.Success) return m.Groups[1].Value;
                }
            }
            catch { }
            return "";
        }

        static string OsText()
        {
            try
            {
                if (SelfTestHost.IsWindows) return "Microsoft Windows " + Environment.OSVersion.Version;
                if (SelfTestHost.IsLinux) return "Linux " + (File.Exists("/proc/sys/kernel/osrelease") ? File.ReadAllText("/proc/sys/kernel/osrelease").Trim() : "");
            }
            catch { }
            return Environment.OSVersion.ToString();
        }

        // ---------------------------------------------------------------- AI load (Perf.Tick, once a second in a mission)
        // Each side's AI aircraft limit set so the total is the test's load, each side keeping its share of the mission's own
        // limits (the Stress Test: half BDF, half PALA), with the per-player parts off, so every machine is
        // tested with the same load. Only on the test mission while a test runs (or the Server Tester's own build).
        static readonly Dictionary<FactionHQ, int> _missionAi = new Dictionary<FactionHQ, int>();

        internal static void AiTick1s()
        {
            try
            {
                int total = 0;
#if TESTER_BUILD
                if (TestAiAircraft != null && TestAiAircraft.Value > 0) total = TestAiAircraft.Value;
#endif
                if (total <= 0 && Testing && _testAi > 0 && OursRunning(Dsm())) total = _testAi;
                if (total <= 0) return;
                var hqs = new List<FactionHQ>();
                foreach (var hq in FactionRegistry.GetAllHQs()) if (hq != null) hqs.Add(hq);
                if (hqs.Count == 0) return;
                lock (_missionAi)
                {
                    if (_missionAi.Count > 32)   // sides of missions that have ended
                    {
                        var gone = new List<FactionHQ>();
                        foreach (var k in _missionAi.Keys) if (k == null) gone.Add(k);
                        foreach (var k in gone) _missionAi.Remove(k);
                    }
                    int sum = 0;
                    foreach (var hq in hqs)
                    {
                        if (!_missionAi.ContainsKey(hq)) _missionAi[hq] = Math.Max(0, hq.AIAircraftLimit);
                        sum += _missionAi[hq];
                    }
                    // the total split in the mission's own proportions (evenly if it has none), the remainder to the sides with
                    // the largest fractions
                    var share = new int[hqs.Count];
                    var frac = new double[hqs.Count];
                    int given = 0;
                    for (int i = 0; i < hqs.Count; i++)
                    {
                        double exact = sum > 0 ? (double)total * _missionAi[hqs[i]] / sum : (double)total / hqs.Count;
                        share[i] = (int)Math.Floor(exact);
                        frac[i] = exact - share[i];
                        given += share[i];
                    }
                    for (; given < total; given++)
                    {
                        int best = 0;
                        for (int i = 1; i < hqs.Count; i++) if (frac[i] > frac[best]) best = i;
                        share[best]++;
                        frac[best] = -1;
                    }
                    for (int i = 0; i < hqs.Count; i++)
                    {
                        hqs[i].AIAircraftLimit = share[i];
                        hqs[i].reduceAIPerFriendlyPlayer = 0f;
                        hqs[i].addAIPerEnemyPlayer = 0f;
                    }
                }
            }
            catch (Exception e) { NukeStatsPlugin.Log?.LogError("[selftest] AI count: " + e.Message); }
        }

        // ---------------------------------------------------------------- players
        static UnityEngine.Events.UnityAction<INetworkPlayer> _onConnected;

        static void Listen(bool on)
        {
            try
            {
                var server = NetworkManagerNuclearOption.i?.Server;
                if (server == null) return;
                if (_onConnected == null) _onConnected = OnConnected;
                if (on) server.Connected.AddListener(_onConnected);
                else server.Connected.RemoveListener(_onConnected);
            }
            catch (Exception e) { NukeStatsPlugin.Log?.LogWarning("[selftest] join watch: " + e.Message); }
        }

        // anyone connecting stops the test at once, before they have even logged in
        static void OnConnected(INetworkPlayer p)
        {
            try { if (p != null && !p.IsHost && Testing) Abort("a player joined", false); } catch { }
        }

        // the server shutting down mid-test: the CPUs back, and what it measured written
        static void OnQuitting()
        {
            try
            {
                _pretend = false;
                SelfTestHost.Restore();
                if (!Testing) return;
                _final = "aborted";
                _why = "the server shut down";
                if (_run != null) _run.StoppedBecause = _why;
                Say("The test stopped early: the server shut down");
                WriteResults();
                _state = St.Idle;
                Emit();
            }
            catch { }
        }

        // a test the server stopped in the middle of (a crash, a restart) is marked as ended
        static void MarkLeftovers()
        {
            try
            {
                string dir = Path.Combine(Paths.GameRootPath, string.IsNullOrWhiteSpace(ResultsDir.Value) ? "NukeStats-ServerTests" : ResultsDir.Value.Trim());
                if (!Directory.Exists(dir)) return;
                foreach (var d in Directory.GetDirectories(dir))
                {
                    string p = Path.Combine(d, "progress.json");
                    if (!File.Exists(p)) continue;
                    var m = Regex.Match(File.ReadAllText(p), "\"state\":\\s*\"([a-z]+)\"");
                    if (!m.Success || m.Groups[1].Value == "done" || m.Groups[1].Value == "aborted" || m.Groups[1].Value == "stopped") continue;
                    string why = "the server stopped during the test (a restart, crash or shutdown)";
                    var sb = new StringBuilder("{\"t\":\"selftest\",\"state\":\"aborted\",\"folder\":");
                    SelfTestFiles.Str(sb, Path.GetFileName(d));
                    sb.Append(",\"why\":");
                    SelfTestFiles.Str(sb, why);
                    sb.Append(",\"updated\":\"").Append(DateTime.UtcNow.ToString("o", Inv)).Append("\"}");
                    SelfTestFiles.WriteAtomic(p, sb.ToString() + "\n");
                    SelfTestFiles.Log(d, DateTime.UtcNow.ToString("HH:mm:ss", Inv) + "  " + why);
                    NukeStatsPlugin.Log?.LogWarning("[selftest] the test in " + d + " never finished: " + why);
                }
            }
            catch (Exception e) { NukeStatsPlugin.Log?.LogWarning("[selftest] earlier tests: " + e.Message); }
        }

        // ---------------------------------------------------------------- what it says
        static void Say(string s)
        {
            NukeStatsPlugin.Log?.LogInfo("[selftest] " + s);
            SelfTestFiles.Log(_folder, DateTime.UtcNow.ToString("HH:mm:ss", Inv) + "  " + s);
        }

        static string StateName()
        {
            switch (_state)
            {
                case St.Idle: return _final ?? "idle";
                case St.Pending: return "pending";
                case St.Preparing: return "preparing";
                case St.Loading: case St.Instrument: return "loading";
                case St.Filling: return "filling";
                case St.Warmup: return "warmup";
                case St.Running: return "running";
                default: return "restoring";
            }
        }

        // seconds still to go, roughly: each step its measurements and settling, the rounds to come as if every target held
        static double Eta()
        {
            if (_run == null) return 0;
            var o = _run.Options;
            double perStep = o.KeepWindows * 15 + 6;
            // the steps to go, each its own length (the re-test's 2 minutes), then the rounds not added yet
            double left = _run.Steps.Skip(_run.Done).Sum(x => TestPlan.KeepOf(o, x) * 15 + 6) + Math.Max(0, _run.StepsPlanned() - _run.Steps.Count) * perStep;
            if (_run.Round < 2 && !_run.RetestAdded) left += TestPlan.RetestCount(o) * (TestPlan.RetestWindows - o.KeepWindows) * 15;
            if (_state <= St.Filling) left += 120 + o.WarmupWindows * 15;
            else if (_state == St.Warmup) left += Math.Max(0, o.WarmupWindows - _warmGot) * 15;
            return left;
        }

        static readonly object _emitLock = new object();

        // {"t":"selftest",...} on the plugin's line, and progress.json in the results folder
        static void Emit()
        {
            try
            {
                lock (_emitLock)
                {
                    var sb = new StringBuilder(512);
                    bool asked = _state == St.Idle && _startRequest != null;   // a new start, not begun: nothing of the last test's
                    string state = asked ? "pending" : StateName();
                    sb.Append("{\"t\":\"selftest\",\"state\":\"").Append(state).Append('"');
                    var run = _run;
                    // a test that has begun (or the last one, once it ended); a new start waiting never writes over its file
                    bool current = run != null && _folder != null && !asked;
                    if (current)
                    {
                        var s = _step ?? run.Current;
                        sb.Append(",\"round\":").Append(run.Round)
                          .Append(",\"step\":").Append(s != null ? s.Number : run.Done)
                          .Append(",\"steps\":").Append(run.StepsPlanned())
                          .Append(",\"label\":");
                        SelfTestFiles.Str(sb, s != null ? s.Label : "");
                        sb.Append(",\"eta_s\":").Append(((int)Eta()).ToString(Inv));
                        sb.Append(",\"folder\":");
                        SelfTestFiles.Str(sb, _folder != null ? Path.GetFileName(_folder) : "");
                    }
                    string why = _state == St.Pending ? _waitWhy : _state == St.Idle || _state == St.Restoring ? (_why ?? "") : "";
                    sb.Append(",\"why\":");
                    SelfTestFiles.Str(sb, why);
                    var w = _last;
                    if (w != null && current)
                        sb.Append(",\"last\":{\"fps\":").Append(Num(w.Fps, 1)).Append(",\"busy\":").Append(Num(w.MsPerFrame, 2)).Append(",\"ai\":").Append(w.Ai).Append('}');
                    if (current && (run.PairPick != null || run.TargetAuto != 0))
                    {
                        sb.Append(",\"pick\":{\"pair\":");
                        SelfTestFiles.Str(sb, run.PairPick ?? "");
                        sb.Append(",\"target\":").Append(run.TargetPick).Append('}');
                    }
                    sb.Append('}');
                    string line = sb.ToString();
                    NukeStatsPlugin.Out(line);
                    if (_folder != null && current)
                    {
                        string p = line.Substring(0, line.Length - 1) + ",\"updated\":\"" + DateTime.UtcNow.ToString("o", Inv) + "\"}\n";
                        try { SelfTestFiles.WriteAtomic(Path.Combine(_folder, "progress.json"), p); } catch { }
                    }
                }
            }
            catch (Exception e) { NukeStatsPlugin.Log?.LogError("[selftest] status: " + e.Message); }
        }

        static void EmitEnd(string final, string why)
        {
            _final = final; _why = why;
            NukeStatsPlugin.Log?.LogInfo("[selftest] " + final + ": " + why);
            var sb = new StringBuilder("{\"t\":\"selftest\",\"state\":\"").Append(final).Append("\",\"why\":");
            SelfTestFiles.Str(sb, why);
            sb.Append('}');
            NukeStatsPlugin.Out(sb.ToString());
        }

        static string Num(double v, int digits) => double.IsNaN(v) || double.IsInfinity(v) ? "null" : Math.Round(v, digits).ToString(Inv);
    }
}
