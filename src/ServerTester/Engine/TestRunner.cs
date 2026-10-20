using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ServerTester.Engine
{
    // Runs a whole test: the test server up with nobody on and a fixed AI load, a warm-up, then each step in turn.
    // Settings change live (CPUs through the process's affinity, workers and the frame target through the plugin's
    // command files), so the mission loads once. A step only keeps measurements taken after its change has landed,
    // with the AI load close to the target and nobody connected.
    public sealed class TestRunner : ITestRun
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public event Action<string> Log;
        public event Action<string, double> Status;      // text, progress 0..1
        public event Action<Step, Measurement> WindowAdded;   // every measurement, for the live graph
        public event Action<Step> StepDone;

        public TestOptions Options { get; private set; }
        public List<Step> Steps { get; private set; } = new List<Step>();
        public int AiReached { get; private set; }
        public int GameWorkers { get; private set; }   // the physics workers the game picked on its own, as measured in the warm-up
        // the automated test's picks: the pair of core count and workers for the next rounds, then the frame target
        public string PairPick { get; internal set; }
        public string PairAuto { get; internal set; }   // what the tester itself suggested (PairPick differs when you picked)
        public int TargetAuto { get; internal set; }
        public string PairWhy { get; internal set; } = "";
        public bool PairByHand { get; internal set; }
        public int TargetPick { get; internal set; }
        public bool TargetByHand { get; internal set; }
        // asks you to pick when a round is done (the question, the choices best first with the suggestion as the first;
        // returns the key picked). Without it the suggestion is taken.
        public Func<string, List<KeyValuePair<string, string>>, CancellationToken, Task<string>> AskPick;
        public event Action<int> RoundStarted;   // 1 cores and workers, 2 frame target, 3 AI load
        public event Action RetestStarted;       // round 1's best pairs measured again, 2 minutes each
        public List<string> Notes { get; } = new List<string>();
        public string StoppedBecause { get; private set; }
        public DateTime Started { get; private set; }

        void Say(string s) => Log?.Invoke(DateTime.Now.ToString("HH:mm:ss", Inv) + "  " + s);

        // a finished test read back from its results folder, to analyse it again (ServerTester --cli --analyse <folder>)
        internal static TestRunner Saved(List<Step> steps, TestOptions o, int aiReached, DateTime started, string stoppedBecause) =>
            new TestRunner { Steps = steps, Options = o, AiReached = aiReached, Started = started, StoppedBecause = stoppedBecause };

        public async Task Run(TestOptions o, CancellationToken cancel)
        {
            Options = o;
            Started = DateTime.Now;
            if (o.Cpu == null) o.Cpu = CpuTopology.Snapshot();   // this PC's cores, as they are when the test starts
            Steps = TestPlan.Build(o);
            string powerBefore = null;
            using (var server = new ServerProcess())
            using (var host = new HostMonitor(() => server.Pid))
            {
                server.ServerLine += l => { if (l.Contains("Exception") || l.Contains("Loading next Mission")) Say("server: " + l); };
                try
                {
                    Status?.Invoke("Getting the test server ready", 0);
                    if (RunGuard.OtherTesterRunning()) { StoppedBecause = "another copy of the tester is running a test from this folder"; return; }
                    RunGuard.CleanUpLeftovers(Say);   // before the power setting changes: a leftover run puts its own back
                    ServerSetup.WriteConfigs(o);
                    foreach (var busy in OtherBusyProcesses()) Notes.Add(busy);
                    foreach (var n in Notes) Say(n);
                    if (o.HighPower) { powerBefore = PowerPlan.SetHigh(Say); }
                    server.Start(Say);
                    RunGuard.Begin(server.Pid, powerBefore);
                    string pb = powerBefore;
                    RunGuard.OnEmergency(() => { try { server.Dispose(); } catch { } if (pb != null) PowerPlan.Restore(pb, _ => { }); RunGuard.End(); });
                    using (var tick = new Timer(_ => host.Sample(), null, 0, 1000))
                    {
                        // 1. the mission loads and the AI fills up
                        Status?.Invoke("Loading the test mission", 0);
                        if (!await WaitFor(server, host, cancel, 600, w => true, "the test mission to load")) return;
                        Say("Test mission running; waiting for " + o.FirstAi + " AI aircraft");
                        AiReached = 0;
                        var fillStart = DateTime.UtcNow;
                        while (!cancel.IsCancellationRequested)
                        {
                            foreach (var w in Read(server, host)) { AiReached = Math.Max(AiReached, w.Ai); Status?.Invoke("AI aircraft up: " + w.Ai + " of " + o.FirstAi, 0); }
                            if (AiReached >= o.FirstAi * 0.9) break;
                            if ((DateTime.UtcNow - fillStart).TotalMinutes > 15)
                            {
                                Notes.Add("Only " + AiReached + " of " + o.FirstAi + " AI aircraft came up, so the test ran with " + AiReached + ".");
                                break;
                            }
                            if (!server.Running) { StoppedBecause = "the test server stopped on its own " + ServerSetup.LogNote() + ""; return; }
                            await Task.Delay(1000, cancel);
                        }
                        if (cancel.IsCancellationRequested) return;
                        Say("AI up: " + AiReached + ". Warming up for " + (o.WarmupWindows * 15 / 60.0).ToString("0.#", Inv) + " minutes");
                        _warming = true;
                        if (!await Collect(server, host, cancel, o.WarmupWindows, null)) return;
                        _warming = false;
                        if (GameWorkers > 0) Say("Workers: Server Default (" + GameWorkers + ")");
                        // the load the steps really run with: the fill stops at 90%, and the AI keeps coming up after it
                        if (_warmMaxAi > AiReached) AiReached = _warmMaxAi;

                        // 2. the steps
                        int done = 0, total = Steps.Count, passes = Math.Max(1, o.Repeats);
                        int round = 1;   // the automated test: 1 baseline + cores and workers, 2 frame target, 3 AI load
                        int curAi = o.FirstAi, levelReached = 0;   // the AI asked for now, and how many came up at it
                        if (o.Automated) RoundStarted?.Invoke(1);
                        for (int at = 0; at < Steps.Count; at++)
                        {
                            var s = Steps[at];
                            if (cancel.IsCancellationRequested) break;
                            // every frame target is measured: a higher target can still lift the frame rate when a lower one wasn't held
                            {
                            if (!server.Running) { StoppedBecause = "the test server stopped on its own " + ServerSetup.LogNote() + ""; break; }
                            if (s.Kind == "ai" || s.Ai != curAi)
                            {
                                // more AI (an AI load step, or the grid's next level): wait for it to fill before measuring
                                server.SetConfig("Perf.TestAiAircraft", s.Ai.ToString(Inv));
                                s.AiReachedForStep = levelReached = await FillTo(server, host, s.Ai, 600, cancel);
                                curAi = s.Ai;
                                if (s.AiReachedForStep < s.Ai * 0.9) s.Note = "only " + s.AiReachedForStep + " of " + s.Ai + " AI aircraft came up";
                            }
                            else if (levelReached > 0) s.AiReachedForStep = levelReached;   // the rest of a grid level
                            string run = passes > 1 ? " (run " + s.Pass + " of " + passes + ")" : "";
                            Status?.Invoke("Step " + s.Number + " of " + total + run + ": " + s.Label, (double)done / total);
                            await RunStep(server, host, s, cancel);
                            done++;
                            StepDone?.Invoke(s);
                            if (s.Ok)
                                Say(string.Format(Inv, "Step {0}/{1}{10} {2}: {3:0.0} fps, busy {4:0.00} ms a frame, physics {5:0} ms/s, spare {6:0} ms/s, server CPU {7:0}%{8}{9}",
                                    s.Number, total, s.Label, s.Fps, s.MsPerFrame, s.Physics, s.Spare, s.CpuPct,
                                    double.IsNaN(s.Mhz) ? "" : string.Format(Inv, ", {0:0} MHz", s.Mhz), s.Throttled ? ", CPU HELD BACK" : "", run));
                            else Say("Step " + s.Number + " " + s.Label + ": no result (" + s.Note + ")");
                            // the AI load measures every load up to the most set; the analysis says how far its frame rate held
                            }
                            // the automated test: a round done, its pick made, the next round's steps added (round 1's best
                            // pairs are measured again, 2 minutes each, before its pick)
                            while (o.Automated && at == Steps.Count - 1 && round < 3 && !cancel.IsCancellationRequested)
                            {
                                if (round == 1 && !_retestAdded)
                                {
                                    _retestAdded = true;
                                    var again = Retest(o);
                                    if (again.Count > 0)
                                    {
                                        lock (Steps)
                                            foreach (var n in again) { n.Number = Steps.Count + 1; Steps.Add(n); }
                                        total = Steps.Count;
                                        RetestStarted?.Invoke();
                                        continue;
                                    }
                                }
                                var next = round == 1 ? await AfterPairs(o, cancel) : await AfterTargets(o, cancel);
                                if (next == null) break;   // nothing to go on with (the round didn't measure)
                                round++;
                                lock (Steps)
                                    foreach (var n in next) { n.Number = Steps.Count + 1; Steps.Add(n); }
                                total = Steps.Count;
                                if (next.Count > 0) RoundStarted?.Invoke(round);
                            }
                        }
                        if (cancel.IsCancellationRequested) StoppedBecause = "stopped by you";
                    }
                }
                catch (OperationCanceledException) { StoppedBecause = "stopped by you"; }
                catch (Exception e) { StoppedBecause = "an error: " + e.Message; Say("Error: " + e); }
                finally
                {
                    if (StoppedBecause == null && cancel.IsCancellationRequested) StoppedBecause = "stopped by you";
                    Status?.Invoke("Stopping the test server", 1);
                    await server.Stop(Say);
                    if (powerBefore != null) PowerPlan.Restore(powerBefore, Say);
                    RunGuard.End();
                    Status?.Invoke(StoppedBecause == null ? "Finished" : "Stopped: " + StoppedBecause, 1);
                }
            }
        }

        List<Measurement> Read(ServerProcess server, HostMonitor host)
        {
            var got = server.ReadNew(host);
            foreach (var w in got) WindowAdded?.Invoke(null, w);
            return got;
        }

        async Task<bool> WaitFor(ServerProcess server, HostMonitor host, CancellationToken cancel, int seconds, Func<Measurement, bool> ok, string what)
        {
            var t0 = DateTime.UtcNow;
            while (!cancel.IsCancellationRequested)
            {
                if (Read(server, host).Any(ok)) return true;
                if (!server.Running) { StoppedBecause = "the test server stopped on its own while waiting for " + what + " " + ServerSetup.LogNote() + ""; return false; }
                if ((DateTime.UtcNow - t0).TotalSeconds > seconds) { StoppedBecause = "timed out waiting for " + what; return false; }
                await Task.Delay(1000, cancel);
            }
            return false;
        }

        // n measurements, whatever they are (the warm-up)
        async Task<bool> Collect(ServerProcess server, HostMonitor host, CancellationToken cancel, int n, Step into)
        {
            int got = 0;
            var t0 = DateTime.UtcNow;
            while (got < n && !cancel.IsCancellationRequested)
            {
                var ws = Read(server, host);
                got += ws.Count;
                foreach (var w in ws)
                {
                    _warmMaxAi = Math.Max(_warmMaxAi, w.Ai);
                    if (_warming && w.Workers > 0) GameWorkers = w.Workers;   // nothing has set them yet
                }
                if (!server.Running) { StoppedBecause = "the test server stopped on its own"; return false; }
                if ((DateTime.UtcNow - t0).TotalSeconds > n * 15 * 3 + 120) break;
                await Task.Delay(1000, cancel);
            }
            return !cancel.IsCancellationRequested;
        }

        bool _retestAdded;

        // round 1's pairs done: its best ones measured again, 2 minutes each (none with fewer than 2 pairs to choose from)
        List<Step> Retest(TestOptions o)
        {
            List<Step> copy;
            lock (Steps) copy = Steps.ToList();
            var top = Results.TopPairs(copy, o);
            if (top.Count < 2) return new List<Step>();
            Say("The best " + top.Count + " measured again, 2 minutes each: " + string.Join("; ", top.Select(p => TestPlan.PairText(o, p))));
            return TestPlan.AutoRetest(o, top);
        }

        // round 1 done: the pair to go on with, then the frame target steps on it
        async Task<List<Step>> AfterPairs(TestOptions o, CancellationToken cancel)
        {
            List<Step> copy;
            lock (Steps) copy = Steps.ToList();
            string auto = Results.PickPair(copy, o, out string why);
            if (auto == null) { Say("No core and worker pair measured, so the automated test stops here"); return null; }
            PairPick = PairAuto = auto; PairWhy = why;
            if (o.PickPairByHand && AskPick != null)
            {
                // the re-test's pairs first (their 2 minute result), then the rest of round 1
                string Pct(double cost) => ((cost - 1) * 100).ToString("+0.0;-0.0;0.0", Inv) + "%";
                var again = Results.RetestCosts(copy, o);
                var choices = again.Select(x => new KeyValuePair<string, string>(x.pair,
                        TestPlan.PairText(o, x.pair) + ": " + Pct(x.cost) + " over 2 minutes" + (x.pair == auto ? " (suggested)" : "")))
                    .Concat(Results.PairCosts(copy, o).Where(x => again.All(a => a.pair != x.pair)).Select(x => new KeyValuePair<string, string>(x.pair,
                        TestPlan.PairText(o, x.pair) + ": " + Pct(x.cost) + (x.pair == auto ? " (suggested)" : "")))).ToList();
                choices = choices.OrderBy(c => c.Key == auto ? 0 : 1).ToList();
                string picked = await AskPick("Cores and workers round done. Pick the cores and workers for the frame target and AI load rounds:", choices, cancel);
                if (picked != null) { PairByHand = picked != auto; PairPick = picked; }
            }
            Say("Cores and workers: " + TestPlan.PairText(o, PairPick) + (PairByHand ? ", picked by you" : ", picked automatically (" + why + ")"));
            var next = TestPlan.AutoTargets(o, PairPick);
            return next.Count > 0 ? next : await AfterTargets(o, cancel);
        }

        // round 2 done: the frame target to go on with, then the AI load steps on both picks
        async Task<List<Step>> AfterTargets(TestOptions o, CancellationToken cancel)
        {
            List<Step> copy;
            lock (Steps) copy = Steps.ToList();
            int auto = Results.PickTarget(copy, o, out _, out _, out bool noneHeld);
            TargetAuto = auto;
            TargetPick = auto > 0 ? auto : TestPlan.Compare(o);
            var results = Results.TargetResults(copy, o);
            if (o.PickTargetByHand && AskPick != null && results.Count > 0)
            {
                var choices = results.OrderByDescending(x => x.target).Select(x => new KeyValuePair<string, string>(x.target.ToString(Inv),
                    x.target + " fps: " + (x.held ? "held, " + (x.spare / 10).ToString("0", Inv) + "% of each second spare" : "not held, ran at " + x.fps.ToString("0.#", Inv) + " fps")
                    + (x.target == auto ? " (suggested)" : ""))).ToList();
                choices = choices.OrderBy(c => c.Key == auto.ToString(Inv) ? 0 : 1).ToList();
                string picked = await AskPick("Frame target round done. Pick the frame target for the AI load round:", choices, cancel);
                if (picked != null && int.TryParse(picked, NumberStyles.Integer, Inv, out int t)) { TargetByHand = t != auto; TargetPick = t; }
            }
            Say("Frame target for the AI load round: " + TargetPick + " fps" + (TargetByHand ? ", picked by you" : noneHeld ? ", none held, so the one with the highest frame rate"
                : auto > 0 ? ", picked automatically" : " (none measured, so the compare rate)"));
            return TestPlan.AutoAi(o, PairPick, TargetPick);
        }

        int _warmMaxAi;   // the most AI seen during the warm-up
        bool _warming;

        int AiFloor(Step s)
        {
            int reached = s.AiReachedForStep > 0 ? s.AiReachedForStep : AiReached;
            return Math.Max(1, (int)Math.Floor(Math.Min(s.Ai, reached > 0 ? reached : s.Ai) * 0.85));
        }

        // waits until about `want` AI aircraft are up (or the time runs out); returns how many got up
        async Task<int> FillTo(ServerProcess server, HostMonitor host, int want, int seconds, CancellationToken cancel)
        {
            int best = 0;
            var t0 = DateTime.UtcNow;
            while (!cancel.IsCancellationRequested && server.Running && (DateTime.UtcNow - t0).TotalSeconds < seconds)
            {
                foreach (var w in Read(server, host)) best = Math.Max(best, w.Ai);
                if (best >= want * 0.9) break;
                await Task.Delay(1000, cancel);
            }
            return best;
        }

        bool? _skipsNow;   // the visual skips the server has now (null = as the test's config file set them)
        bool _lastStock;   // the last step was the stock one (NukeStats's gameplay fixes off)

        async Task RunStep(ServerProcess server, HostMonitor host, Step s, CancellationToken cancel)
        {
            if (_skipsNow == null) _skipsNow = Options.VisualSkips;
            Read(server, host);   // what was measured before this step's settings change (a wait for your pick) isn't this step's
            int loadsBefore = server.MissionLoads;
            if (!server.Running) { s.Note = "the test server had stopped"; return; }
            s.CpusGot = server.SetCpus(s.Cpus);
            server.SetConfig("Perf.JobWorkers", s.Workers.ToString(Inv));
            server.SetConfig("Perf.TargetFps", s.Target.ToString(Inv));
            bool skips = s.Skips ?? Options.VisualSkips;   // a step's own visual skips (the server as it comes), else the test's
            bool stock = s.Skips == false;                  // the server as it comes: NukeStats's gameplay fixes off too
            // the stock step drops 2 minutes (the effects the skips emptied build up again); the step after it 45 s (the
            // particles go back off a few at a time)
            int dropFirst = stock ? 8 : _lastStock && skips ? 3 : 1;
            if (_skipsNow != skips)
            {
                foreach (var k in new[] { "SkipPilotAnimation", "SkipVisualConstraints", "SkipVisualEffects", "SkipServerParticles" })
                    server.SetConfig("Perf." + k, skips ? "true" : "false");
                _skipsNow = skips;
            }
            if (_lastStock != stock)
                foreach (var k in new[] { "AirbaseTaxi.Enabled", "AiAirportRotation.Enabled", "MissileBurnoutFix.Enabled" })
                    server.SetConfig(k, stock ? "false" : "true");
            _lastStock = stock;
            if (s.CpusGot.Length > 0 && !s.CpusGot.SequenceEqual(s.Cpus.OrderBy(x => x)))
                s.Note = "the system gave the server CPUs " + Native.ListText(s.CpusGot) + " instead of " + Native.ListText(s.Cpus);
            bool settled = false;
            int dropped = 0;
            var t0 = DateTime.UtcNow;
            var lastPin = DateTime.UtcNow;
            int keep = TestPlan.KeepOf(Options, s);
            while (!cancel.IsCancellationRequested && s.Windows.Count < keep)
            {
                if (!server.Running) { s.Note = "the test server stopped"; StoppedBecause = "the test server stopped on its own"; return; }
                if ((DateTime.UtcNow - lastPin).TotalSeconds > 10) { server.SetCpus(s.Cpus); lastPin = DateTime.UtcNow; }   // threads made since
                if (server.MissionLoads != loadsBefore)
                {
                    // the mission ended (a side won) and loaded again: wait for the AI and a short warm-up, then start the step over
                    Say("The test mission ended and reloaded; repeating this step once it settles");
                    lock (s.Windows) s.Windows.Clear();
                    loadsBefore = server.MissionLoads;
                    await Collect(server, host, cancel, 8, null);
                    settled = false; dropped = 0; t0 = DateTime.UtcNow;
                    continue;
                }
                foreach (var w in Read(server, host))
                {
                    // the game's own workers (0): the count it picked on its own
                    bool workersOk = s.Workers > 0 ? w.Workers == s.Workers : GameWorkers <= 0 || w.Workers == GameWorkers;
                    bool matches = workersOk && w.Target == s.Target && w.Humans == 0;
                    if (!matches) { settled = false; continue; }
                    if (!settled) { settled = true; dropped = 0; }
                    if (dropped < dropFirst) { dropped++; continue; }   // the window the change landed in (more after the stock step)
                    if (w.Ai < AiFloor(s)) continue;             // a few AI short (shot down, respawning)
                    lock (s.Windows) s.Windows.Add(w);
                    WindowAdded?.Invoke(s, w);
                    if (s.Windows.Count >= keep) break;
                }
                if ((DateTime.UtcNow - t0).TotalSeconds > (keep + 2) * 15 * 3 + 120)
                {
                    if (s.Note.Length == 0) s.Note = "timed out: the measurements never settled (" + (settled ? "AI load too low" : "the setting didn't take") + ")";
                    return;
                }
                await Task.Delay(1000, cancel);
            }
        }

        // anything else using the CPU hard while the test runs takes time from the server and skews the results
        static IEnumerable<string> OtherBusyProcesses()
        {
            var first = new Dictionary<int, (string, double)>();
            try
            {
                foreach (var p in Process.GetProcesses())
                    try { first[p.Id] = (p.ProcessName, p.TotalProcessorTime.TotalSeconds); } catch { }
                Thread.Sleep(3000);
                var busy = new List<(string, double)>();
                foreach (var p in Process.GetProcesses())
                    try
                    {
                        if (!first.TryGetValue(p.Id, out var f) || p.Id == Environment.ProcessId) continue;
                        double pct = (p.TotalProcessorTime.TotalSeconds - f.Item2) / 3.0 * 100;
                        if (pct >= 15) busy.Add((p.ProcessName, pct));
                    }
                    catch { }
                return busy.OrderByDescending(b => b.Item2).Take(5)
                           .Select(b => string.Format(Inv, "{0} was using {1:0}% of a CPU at the start: close it if you can, it takes time from the server", b.Item1, b.Item2)).ToList();
            }
            catch { return new string[0]; }
        }
    }

    // A high-performance power setting for the test, put back after.
    public static class PowerPlan
    {
        const string WinHigh = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

        static string Run(string exe, params string[] args)
        {
            try
            {
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                foreach (var a in args) psi.ArgumentList.Add(a);
                using (var p = Process.Start(psi)) { string o = p.StandardOutput.ReadToEnd(); p.WaitForExit(5000); return p.ExitCode == 0 ? o.Trim() : null; }
            }
            catch { return null; }
        }

        // returns what to put back, or null when nothing was changed
        public static string SetHigh(Action<string> log)
        {
            if (Native.IsWindows)
            {
                string cur = Run("powercfg", "/getactivescheme");
                var m = cur != null ? System.Text.RegularExpressions.Regex.Match(cur, "[0-9a-fA-F-]{36}") : null;
                if (m == null || !m.Success || m.Value.Equals(WinHigh, StringComparison.OrdinalIgnoreCase)) return null;
                if (Run("powercfg", "/setactive", WinHigh) == null) { log("Couldn't switch to the High performance power plan (it may be hidden on this PC); testing on the current one"); return null; }
                log("Power plan set to High performance for the test");
                return m.Value;
            }
            string prof = Run("powerprofilesctl", "get");
            if (prof == null || prof == "performance") return null;
            if (Run("powerprofilesctl", "set", "performance") == null) { log("Couldn't set the performance power profile; testing on " + prof); return null; }
            log("Power profile set to performance for the test (was " + prof + ")");
            return prof;
        }

        public static void Restore(string before, Action<string> log)
        {
            string r = Native.IsWindows ? Run("powercfg", "/setactive", before) : Run("powerprofilesctl", "set", before);
            log(r != null ? "Power setting put back to " + before : "Couldn't put the power setting back to " + before + "; set it back by hand");
        }
    }
}
