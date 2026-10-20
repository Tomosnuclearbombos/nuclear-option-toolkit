using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ServerTester.Engine
{
    // Shared with the NukeStats plugin (linked into src/NukeStats/NukeStats.csproj), which builds the automated test's
    // steps with this same code when it runs the test on a server itself: the plan reads the cores from the options'
    // CpuSnapshot, never from the machine it runs on, and nothing here may use the tester's own classes.
    public sealed class TestOptions
    {
        public bool TestBaseline = true, TestCores = true, TestWorkers = true, TestTarget = true, TestAiLoad = false;
        // the automated test: the baseline, every pair of core count and physics worker count, then the frame target on
        // the pair picked, then the AI load on both picks. Each round's pick is made automatically, or by you when the
        // round is done.
        public bool Automated;
        public bool PickPairByHand, PickTargetByHand;
        public int[] UseCores;             // the cores (PhysCore.Index) the tests may use, ticked in the CPU list; null = every core
        public CpuSnapshot Cpu;            // the CPU the test runs (or ran) on; null = this PC's
        // a server the host holds to fewer CPUs than it can see (a CPU limit, in CPUs; 0 = none): the cores can't be picked,
        // so the cores and workers round tests the workers on every core, not pinned ("everyx3"); PinUnderQuota tests the
        // core counts up to one more than the limit as well
        public double QuotaCpus;
        public bool PinUnderQuota;
        public bool Alternate = true;      // true: 1, base, 2, base, 3, base ...   false: 1, 2, 3, 4 ...
        // the baseline is the server as it runs out of the box: on every core, with the game's own physics workers
        public int BaselineCores = 0;      // 0 = every core (not pinned); N = the N fastest cores
        public int BaselineWorkers = 0;    // 0 = the game's own (JobWorkers not set); N = N workers
        public int MaxCores = 0;           // 0 = up to every fast core
        public int MaxWorkers = 8;
        public int Ai = 60;
        public string Mission = TestPlan.StressTest;   // the test mission: Stress Test, or Realistic Benchmark to check settings in a full battle
        public int KeepWindows = 2;        // 15 s measurements kept per step, after one dropped (2 = 30 seconds a step)
        public int Repeats = 1;            // how many times the whole test runs, start to finish (the AI load test runs once, at the end)
        public int WarmupWindows = 8;      // after the mission loads and the AI is up (2 minutes)
        public bool VisualSkips = true;
        public bool HighPower = true;
        public int GamePort = 7790, QueryPort = 27030;
        // the frame target step test: Start, Start+Step, ... (TargetSteps of them, 2 to 20), one after another or each
        // followed by a baseline target (90 unless set)
        public int TargetStart = 60, TargetStep = 10, TargetSteps = 7;
        public bool TargetAlternate = false;
        public int TargetBaseline = TestPlan.CompareTarget;
        public int CompareFps = TestPlan.CompareTarget;                    // the target cores and workers are compared at
        public int SparePct = 25;   // the suggested frame target leaves at least this much of each second spare
        public string HeldBy = TestPlan.HeldOnAverage;   // what a frame target is held on: the average, the 5% lows or the 1% lows

        // NukeStats' TargetFps works from 60 to 240; steps outside that are left out
        public int[] Targets() => Enumerable.Range(0, Math.Max(2, Math.Min(20, TargetSteps)))
            .Select(i => TargetStart + i * Math.Max(1, TargetStep)).Where(x => x >= 60 && x <= 240).Distinct().ToArray();
        public int BaselineTarget => TargetBaseline >= 60 && TargetBaseline <= 240 ? TargetBaseline : TestPlan.CompareTarget;

        // the AI load test: AiStart, AiStart+AiStep, ... up to AiMax. It runs at the end, and AI that is already up
        // can't be taken away, so after other tests it starts no lower than the AI they ran with
        public int AiStart = 60, AiStep = 10, AiMax = 120;
        public bool OnlyAiLoad => !Automated && TestAiLoad && !TestBaseline && !TestCores && !TestWorkers && !TestTarget;
        public int AiFrom => OnlyAiLoad ? Math.Max(1, AiStart) : Math.Max(AiStart, Ai);
        public int[] AiLoads()
        {
            var loads = new List<int>();
            for (int ai = AiFrom; ai <= AiMax && loads.Count < 50; ai += Math.Max(1, AiStep)) loads.Add(ai);
            return loads.ToArray();
        }
        // the grid test: every AI level crossed with frame targets, core counts and worker counts, each set against the
        // baseline (every core, the baseline workers, 90 fps) at the same AI, measured again after every few steps
        public bool Grid;
        public int[] GridAi = { 20, 30, 40, 50, 60 }, GridTargets = { 90, 120 };
        public int[] GridWorkers = { 0, 1, 2, 3, 4, 6, 8 };   // 0 = Server Default
        public string[] GridCores = { "every" };              // core counts: "every" = not pinned, "4" = the 4 fastest
        public int RefEvery = 6;
        public bool GridDefault;   // each AI level also measures the game's own 60 fps target and workers, between the same baselines

        // the AI the server fills up to before the first step
        public int FirstAi => Grid && GridAi.Length > 0 ? GridAi[0] : OnlyAiLoad && AiLoads().Length > 0 ? AiLoads()[0] : Ai;
    }

    // One test step: what it ran with and what it measured.
    public sealed class Step
    {
        public string Kind;        // baseline, cores, workers, pair, retest, target, ai
        public string Value;       // "4", "all", ...
        public string Label;
        public bool Baseline;
        public int[] Cpus;         // the CPUs it asked for
        public int[] CpusGot;      // the CPUs the server really had
        public int Workers, Target, Ai;
        public bool? Skips;        // the visual skips for this step alone (false: the server as it comes); null = the test's own
        public int AiReachedForStep;   // AI load steps: how many AI were really up
        public readonly List<Measurement> Windows = new List<Measurement>();
        public string Note = "";
        public int Number;
        public int Pass = 1;       // which run of the whole test it belongs to
        public int Keep;           // 15 s measurements this step keeps (0 = the test's KeepWindows; the re-test's steps keep 2 minutes)
        public bool Skipped;      // a frame target left out because a lower one was already missed in its run
        public double BaseRef = double.NaN;   // the baseline's busy time per frame to set this step against (Results.Relate)
        public bool Ok => Windows.Count > 0;

        static double Med(IEnumerable<double> v)
        {
            var a = v.Where(x => !double.IsNaN(x)).OrderBy(x => x).ToList();
            if (a.Count == 0) return double.NaN;
            return a.Count % 2 == 1 ? a[a.Count / 2] : (a[a.Count / 2 - 1] + a[a.Count / 2]) / 2.0;
        }
        public double Fps => Med(Windows.Select(w => w.Fps));
        public double P95 => Windows.Count > 0 ? Windows.Max(w => w.P95) : double.NaN;
        public double MsPerFrame => Med(Windows.Select(w => w.MsPerFrame));
        public double Physics => Med(Windows.Select(w => w.Physics));
        public double Spare => Med(Windows.Select(w => w.Spare));
        public double CpuPct => Med(Windows.Select(w => w.CpuPct));
        public double Mhz => Med(Windows.Select(w => w.MainMhz));
        public double Low5 => Med(Windows.Select(w => w.Low5Fps));    // the 5% low frame rate
        public double Low1 => Med(Windows.Select(w => w.Low1Fps));    // the 1% low frame rate
        public double Longest => Windows.Count > 0 ? Windows.Select(w => w.FtMax).Where(x => !double.IsNaN(x)).DefaultIfEmpty(double.NaN).Max() : double.NaN;   // ms
        public double TypicalLongest => Med(Windows.Select(w => w.FtMax));   // the longest frame of a usual 15 s measurement
        public double TempC => Windows.Count > 0 ? Windows.Select(w => w.TempC).Where(x => !double.IsNaN(x)).DefaultIfEmpty(double.NaN).Max() : double.NaN;
        public double AiSeen => Med(Windows.Select(w => (double)w.Ai));
        public bool Throttled => Windows.Any(w => w.Throttled);
        // busy time per frame against the baseline, in percent: +10 = the main thread worked 10% more for each frame
        public double VsBaseline => BaseRef > 0 ? (MsPerFrame / BaseRef - 1) * 100 : double.NaN;
    }

    // One NukeStats measurement (its {"t":"perf"} line, one every 15 s in the test build) plus what the tester sampled
    // over the same seconds.
    public sealed class Measurement
    {
        public DateTime At;      // local time, for the files and the graphs (the plugin's own test: UTC, its results say "clock":"utc")
        public DateTime AtUtc;   // for matching the host's samples (a clock change mid-test can't move it)
        public double Sec, Fps, P95, FtMax, Physics, Spare, FixedPhase, JobsWait;
        public double P99 = double.NaN;   // frame times (ms) the slowest 5% and 1% of frames were longer than, and the longest
        // the 5% low frame rate: only from the NukeStats that writes ft_p99 too (its ft_p95 is in 0.1 ms steps; the one
        // before it rounded down to whole milliseconds, which reads above the average)
        public double Low5Fps => P95 > 0 && P99 > 0 ? 1000 / P95 : double.NaN;
        public double Low1Fps => P99 > 0 ? 1000 / P99 : double.NaN;   // the 1% low frame rate
        public int Ai, Humans, Units, Workers, Target;
        public double CpuPct = double.NaN, MainMhz = double.NaN, TempC = double.NaN;
        public bool Throttled;
        public string MainCpus = "";
        public double MsPerFrame => Fps > 0 ? Math.Max(0, 1000 - Spare) / Fps : double.NaN;   // main-thread busy time for each frame
    }

    // Builds the list of steps from the options and the CPU's layout.
    public static class TestPlan
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        // the frame target the baseline and the cores and workers are measured at: 90 is safe for a lower end CPU, and the
        // worse settings score clearly worse there and the better ones better
        public const int CompareTarget = 90;
        // the automated test's re-test: round 1's best 3 pairs measured again, 2 minutes each (8 measurements), with a
        // 2 minute baseline before, between and after them
        public const int RetestPairs = 3, RetestWindows = 8;
        // the two missions the payload carries: the steady one for finding the settings, the full battle for checking them
        public const string StressTest = "Stress Test", RealisticBenchmark = "Realistic Benchmark";

        // A frame target is held when the measure picked reaches this share of it. The average has to be all but on the
        // target; the lows sit a little under it even on a server with time to spare (frame times vary by a fraction of a
        // millisecond either way), so they get the share a comfortably held target measured on the gaming PC test.
        public const string HeldOnAverage = "average", HeldOnLow5 = "low5", HeldOnLow1 = "low1";
        public static double HeldShare(string heldBy) => heldBy == HeldOnLow1 ? 0.80 : heldBy == HeldOnLow5 ? 0.90 : 0.98;
        public static double HeldValue(string heldBy, Step s)
        {
            double v = heldBy == HeldOnLow1 ? s.Low1 : heldBy == HeldOnLow5 ? s.Low5 : s.Fps;
            return double.IsNaN(v) ? s.Fps : v;   // results from before the lows were measured
        }
        public static bool Holds(TestOptions o, Step s) => s.Ok && HeldValue(o.HeldBy, s) >= s.Target * HeldShare(o.HeldBy);
        public static string HeldName(string heldBy) => heldBy == HeldOnLow1 ? "the 1% lows" : heldBy == HeldOnLow5 ? "the 5% lows" : "the average frame rate";

        // the CPU a test's options describe (this PC's when they don't say)
        public static CpuSnapshot Cpu(TestOptions o) => o?.Cpu ?? CpuSnapshot.Here();

        public static int FastCount => CpuSnapshot.Here().FastCores().Count;   // this PC's fast cores
        public static int FastCountOf(TestOptions o) => Cpu(o).FastCores().Count;

        public static int[] AllCpus(TestOptions o) => Cpu(o).Cores.SelectMany(c => c.Cpus).OrderBy(x => x).ToArray();

        // the cores the tests may use, fastest first: the ones ticked in the CPU list (every core when none are)
        public static List<PhysCore> Usable(TestOptions o)
        {
            var ranked = Cpu(o).Ranked();
            if (o.UseCores == null || o.UseCores.Length == 0) return ranked;
            var u = ranked.Where(c => o.UseCores.Contains(c.Index)).ToList();
            return u.Count > 0 ? u : ranked;
        }
        public static bool UsesEveryCore(TestOptions o) => Usable(o).Count == Cpu(o).Cores.Count;
        // the usable cores a core count counts up through: the fast ones (all of them when only efficient ones are ticked)
        public static List<PhysCore> UsableFast(TestOptions o)
        {
            var u = Usable(o);
            var f = u.Where(c => c.Type != "efficient").ToList();
            return f.Count > 0 ? f : u;
        }
        public static int[] CpusOf(TestOptions o, int cores) => UsableFast(o).Take(cores).SelectMany(c => c.Cpus).OrderBy(x => x).ToArray();
        public static int[] UsableCpus(TestOptions o) => Usable(o).SelectMany(c => c.Cpus).OrderBy(x => x).ToArray();
        // the core counts the tests cycle through: 1 up to every usable fast core, then every usable core when that adds
        // efficient ones ("all"). Under a CPU limit: every core, not pinned ("every"), after the counts up to one more than
        // the limit when those are tested too.
        public static List<string> CoreValues(TestOptions o)
        {
            if (o.QuotaCpus > 0)
            {
                var q = new List<string>();
                if (o.PinUnderQuota)
                {
                    int most = Math.Min((int)Math.Ceiling(o.QuotaCpus) + 1, UsableFast(o).Count);
                    if (o.MaxCores > 0) most = Math.Min(most, o.MaxCores);
                    q.AddRange(Enumerable.Range(1, Math.Max(1, most)).Select(v => v.ToString(Inv)));
                }
                q.Add("every");
                return q;
            }
            int top = o.MaxCores > 0 ? Math.Min(o.MaxCores, UsableFast(o).Count) : UsableFast(o).Count;
            var values = Enumerable.Range(1, Math.Max(1, top)).Select(v => v.ToString(Inv)).ToList();
            if (Usable(o).Count > UsableFast(o).Count) values.Add("all");
            return values;
        }

        // the baseline's cores: 0 = every core, as out of the box, else that many of the fastest
        public static int BaseCores(TestOptions o) => o.BaselineCores > 0 ? Math.Min(o.BaselineCores, FastCountOf(o)) : 0;
        public static int[] BaseCpus(TestOptions o) => o.BaselineCores > 0 ? CpusOf(o, BaseCores(o)) : AllCpus(o);
        // the core count test's step for the baseline: with every core ticked, every core is "all" on a hybrid CPU and every
        // fast core otherwise; with some unticked, "every" (the server not pinned, as out of the box)
        public static string BaseCoreValue(TestOptions o) => o.BaselineCores > 0 ? BaseCores(o).ToString(Inv)
            : !UsesEveryCore(o) ? "every" : Cpu(o).Hybrid ? "all" : FastCountOf(o).ToString(Inv);

        // the baseline's workers: 0 = the game's own, as out of the box
        public static int BaseWorkers(TestOptions o) => o.BaselineWorkers > 0 ? Math.Min(o.BaselineWorkers, MaxWorkers(o)) : 0;
        // the baseline's workers to start with: one fewer than its cores (a hosted server: the cores its CPU limit allows,
        // each 100% a core), whatever the game picks on its own
        public static int DefaultBaseWorkers(TestOptions o)
        {
            int cores = BaseCores(o) > 0 ? BaseCores(o) : o.QuotaCpus > 0 ? Math.Max(1, (int)Math.Floor(o.QuotaCpus)) : Cpu(o).Cores.Count;
            return Math.Max(1, Math.Min(cores - 1, MaxWorkers(o)));
        }

        public static string CoresText(int cores) => cores <= 0 ? "Server Default cores" : cores + (cores == 1 ? " core" : " cores");
        public static string WorkersText(int workers) => workers <= 0 ? "Server Default workers" : workers + (workers == 1 ? " worker" : " workers");

        // the baseline test: the server as it runs out of the box (or on the cores picked), first in every run of the test
        static Step BaselineStep(TestOptions o) => new Step
        {
            Kind = "baseline", Value = "base", Baseline = true, Cpus = BaseCpus(o), Workers = BaseWorkers(o), Target = Compare(o), Ai = o.Ai,
            Label = "baseline",
        };

        public static int MaxWorkers(TestOptions o) => Math.Max(1, Math.Min(o.MaxWorkers, Math.Max(1, Cpu(o).Threads - 1)));

        public static List<Step> Build(TestOptions o)
        {
            if (o.Grid) return GridSteps(o);
            var steps = new List<Step>();
            int[] baseCpus = BaseCpus(o);
            int baseWorkers = BaseWorkers(o);
            int passes = Math.Max(1, o.Repeats);
            for (int r = 0; r < passes; r++)
            {
                int first = steps.Count;
                if (o.TestBaseline && !o.Automated) steps.Add(BaselineStep(o));
                if (o.Automated) { AutoFirst(o, steps); for (int i = first; i < steps.Count; i++) steps[i].Pass = r + 1; continue; }
                if (o.TestCores && CoreValues(o).Count >= 2)
                    Sequence(steps, CoreValues(o), BaseCoreValue(o), o.Alternate, v => CoreStep(o, v, baseWorkers, o.Ai, Compare(o)), baselineFirst: true);
                if (o.TestWorkers)
                {
                    var values = Enumerable.Range(1, MaxWorkers(o)).Select(v => v.ToString(Inv)).ToList();
                    Sequence(steps, values, baseWorkers.ToString(Inv), o.Alternate, v => new Step
                    {
                        Kind = "workers", Value = v, Label = WorkersText(int.Parse(v, Inv)), Cpus = baseCpus,
                        Workers = int.Parse(v, Inv), Target = Compare(o), Ai = o.Ai,
                    }, baselineFirst: true);
                }
                // the frame target goes up a step at a time, every target measured (a higher target lifts the frame rate even
                // when a lower one wasn't held)
                if (o.TestTarget)
                    Sequence(steps, o.Targets().Select(t => t.ToString(Inv)).ToList(), o.BaselineTarget.ToString(Inv), o.TargetAlternate, v => new Step
                    {
                        Kind = "target", Value = v, Label = "target " + v + " fps", Cpus = baseCpus, Workers = baseWorkers,
                        Target = int.Parse(v, Inv), Ai = o.Ai,
                    });
                for (int i = first; i < steps.Count; i++) steps[i].Pass = r + 1;
            }
            // once, at the very end: AI that is already up can't be taken away, so nothing could run after it at the
            // test's own load
            if (o.TestAiLoad && !o.Automated)
                foreach (int ai in o.AiLoads())
                    steps.Add(new Step { Kind = "ai", Value = ai.ToString(Inv), Label = ai + " AI aircraft", Cpus = baseCpus, Workers = baseWorkers, Target = Compare(o), Ai = ai, Pass = passes });
            for (int i = 0; i < steps.Count; i++) steps[i].Number = i + 1;
            return steps;
        }

        public static int Compare(TestOptions o) => o.CompareFps >= 60 && o.CompareFps <= 240 ? o.CompareFps : CompareTarget;

        // a core count's CPUs: "every" = every core (not pinned), "all" = every core ticked, N = the N fastest ticked
        public static int[] CoreCpus(TestOptions o, string v) => v == "every" ? AllCpus(o) : v == "all" ? UsableCpus(o) : CpusOf(o, int.Parse(v, Inv));

        static string CoreLabel(TestOptions o, string v)
        {
            int[] cpus = CoreCpus(o, v);
            return v == "every" ? "every core (" + cpus.Length + " CPUs, not pinned)" : v == "all" ? "all " + Usable(o).Count + " cores ticked (CPUs " + CpuList.Text(cpus) + ")"
                 : v + (v == "1" ? " core" : " cores") + " (CPUs " + CpuList.Text(cpus) + ")";
        }

        static Step CoreStep(TestOptions o, string v, int workers, int ai, int target) =>
            new Step { Kind = "cores", Value = v, Label = CoreLabel(o, v), Cpus = CoreCpus(o, v), Workers = workers, Target = target, Ai = ai };

        // ---------------------------------------------------------------- the automated test
        // A pair: a core count and a worker count, written "4x3" ("allx3", "everyx3").
        public static string PairValue(string cores, int workers) => cores + "x" + workers.ToString(Inv);
        public static string PairCores(string pair) => pair.Substring(0, pair.LastIndexOf('x'));
        public static int PairWorkers(string pair) => int.Parse(pair.Substring(pair.LastIndexOf('x') + 1), Inv);
        public static string PairText(TestOptions o, string pair)
        {
            string c = PairCores(pair);
            return (c == "every" ? "every core" : c == "all" ? "all " + Usable(o).Count + " cores ticked" : CoresText(int.Parse(c, Inv))) + ", " + WorkersText(PairWorkers(pair));
        }

        // round 1, each run of the test: the baseline, then every core count with every worker count (each followed by
        // the baseline when alternating)
        static void AutoFirst(TestOptions o, List<Step> steps)
        {
            var baseline = BaselineStep(o);
            steps.Add(baseline);
            foreach (var c in CoreValues(o))
                for (int w = 1; w <= MaxWorkers(o); w++)
                {
                    string v = PairValue(c, w);
                    steps.Add(new Step { Kind = "pair", Value = v, Label = CoreLabel(o, c) + ", " + WorkersText(w), Cpus = CoreCpus(o, c), Workers = w,
                                         Target = Compare(o), Ai = o.Ai });
                    if (o.Alternate) { var b = BaselineStep(o); b.Label += " (between)"; steps.Add(b); }
                }
        }

        // round 1's re-test: the best pairs measured again for 2 minutes each, a 2 minute baseline before, between and after
        // them (its own baselines, as an alternating frame target test has its own)
        public static List<Step> AutoRetest(TestOptions o, IList<string> pairs)
        {
            Step Base() => new Step { Kind = "retest", Value = "base", Baseline = true, Label = "baseline, 2 minutes", Cpus = BaseCpus(o), Workers = BaseWorkers(o),
                                      Target = Compare(o), Ai = o.Ai, Keep = RetestWindows, Pass = Math.Max(1, o.Repeats) };
            var steps = new List<Step> { Base() };
            foreach (var p in pairs)
            {
                string c = PairCores(p);
                int w = PairWorkers(p);
                steps.Add(new Step { Kind = "retest", Value = p, Label = CoreLabel(o, c) + ", " + WorkersText(w) + ", 2 minutes", Cpus = CoreCpus(o, c), Workers = w,
                                     Target = Compare(o), Ai = o.Ai, Keep = RetestWindows, Pass = Math.Max(1, o.Repeats) });
                steps.Add(Base());
            }
            return steps;
        }

        // the grid's baseline at this AI: every core, the baseline workers, at the compare target (90), as every test's baseline
        static Step GridRef(TestOptions o, int ai) => new Step
        {
            Kind = "grid", Value = ai.ToString(Inv) + "/ref", Baseline = true, Cpus = AllCpus(o), Workers = BaseWorkers(o), Target = Compare(o), Ai = ai,
            Label = ai + " AI, baseline (every core, " + WorkersText(BaseWorkers(o)) + ", " + Compare(o) + " fps)",
        };

        // the grid test, AI level by level (AI that is up can't be taken away): the reference, then each frame target with
        // each core count and worker count, the reference again after every RefEvery steps and at the end of the level
        public static List<Step> GridSteps(TestOptions o)
        {
            var steps = new List<Step>();
            int most = MaxWorkers(o);
            foreach (int ai in o.GridAi.Where(a => a > 0).Distinct().OrderBy(a => a))
            {
                steps.Add(GridRef(o, ai));
                int since = 0;
                // the game's own 60 fps target and workers next to the settings, so both see the same host: first the server as
                // it comes (no visual skips), then the same with the test's visual skips
                if (o.GridDefault)
                {
                    if (o.VisualSkips)
                    {
                        steps.Add(new Step
                        {
                            Kind = "grid", Value = ai.ToString(Inv) + "/60/" + PairValue("every", 0) + "/stock", Cpus = CoreCpus(o, "every"), Workers = 0, Target = 60, Ai = ai,
                            Skips = false, Label = ai + " AI, 60 fps, " + CoreLabel(o, "every") + ", " + WorkersText(0) + ", no visual skips or gameplay fixes",
                        });
                        if (++since >= Math.Max(1, o.RefEvery)) { steps.Add(GridRef(o, ai)); since = 0; }
                    }
                    if (!(Compare(o) == 60 && BaseWorkers(o) == 0))   // (not when it is the baseline itself)
                    {
                        steps.Add(new Step
                        {
                            Kind = "grid", Value = ai.ToString(Inv) + "/60/" + PairValue("every", 0), Cpus = CoreCpus(o, "every"), Workers = 0, Target = 60, Ai = ai,
                            Label = ai + " AI, 60 fps, " + CoreLabel(o, "every") + ", " + WorkersText(0),
                        });
                        if (++since >= Math.Max(1, o.RefEvery)) { steps.Add(GridRef(o, ai)); since = 0; }
                    }
                }
                foreach (int t in o.GridTargets.Where(x => x >= 60 && x <= 240).Distinct())
                    foreach (var c in o.GridCores.Distinct())
                        foreach (int w in o.GridWorkers.Where(x => x >= 0 && x <= most).Distinct())
                        {
                            if (t == Compare(o) && c == "every" && w == BaseWorkers(o)) continue;   // the baseline itself
                            if (o.GridDefault && t == 60 && c == "every" && w == 0) continue;   // measured already, as the default
                            steps.Add(new Step
                            {
                                Kind = "grid", Value = ai.ToString(Inv) + "/" + t.ToString(Inv) + "/" + PairValue(c, w), Cpus = CoreCpus(o, c), Workers = w, Target = t, Ai = ai,
                                Label = ai + " AI, " + t + " fps, " + CoreLabel(o, c) + ", " + WorkersText(w),
                            });
                            if (++since >= Math.Max(1, o.RefEvery)) { steps.Add(GridRef(o, ai)); since = 0; }
                        }
                if (since > 0) steps.Add(GridRef(o, ai));
            }
            for (int i = 0; i < steps.Count; i++) steps[i].Number = i + 1;
            return steps;
        }

        // the re-test's steps, for the estimate: none with fewer than 2 pairs to choose from
        public static int RetestCount(TestOptions o)
        {
            if (!o.Automated || o.Grid) return 0;
            int pairs = CoreValues(o).Count * MaxWorkers(o);
            return pairs >= 2 ? 2 * Math.Min(RetestPairs, pairs) + 1 : 0;
        }

        // the 15 s measurements a step keeps
        public static int KeepOf(TestOptions o, Step s) => s.Keep > 0 ? s.Keep : o.KeepWindows;

        // round 2: the frame target on the pair picked, each run of the test
        public static List<Step> AutoTargets(TestOptions o, string pair)
        {
            var steps = new List<Step>();
            int[] cpus = CoreCpus(o, PairCores(pair));
            int workers = PairWorkers(pair);
            for (int r = 0; r < Math.Max(1, o.Repeats); r++)
            {
                int first = steps.Count;
                Sequence(steps, o.Targets().Select(t => t.ToString(Inv)).ToList(), o.BaselineTarget.ToString(Inv), o.TargetAlternate, v => new Step
                {
                    Kind = "target", Value = v, Label = "target " + v + " fps", Cpus = cpus, Workers = workers, Target = int.Parse(v, Inv), Ai = o.Ai,
                });
                for (int i = first; i < steps.Count; i++) steps[i].Pass = r + 1;
            }
            return steps;
        }

        // round 3: the AI load on the pair and the frame target picked, once
        public static List<Step> AutoAi(TestOptions o, string pair, int target) =>
            o.AiLoads().Select(ai => new Step { Kind = "ai", Value = ai.ToString(Inv), Label = ai + " AI aircraft", Cpus = CoreCpus(o, PairCores(pair)),
                                                Workers = PairWorkers(pair), Target = target, Ai = ai, Pass = Math.Max(1, o.Repeats) }).ToList();

        // every step the whole test will have, for the estimate (the automated rounds as if every target is reached)
        public static int StepCount(TestOptions o)
        {
            int n = Build(o).Count;
            if (!o.Automated || o.Grid) return n;
            int targets = o.Targets().Length * (o.TargetAlternate ? 2 : 1) * Math.Max(1, o.Repeats);
            return n + RetestCount(o) + targets + o.AiLoads().Length;
        }

        // seconds the steps take: each its measurements plus the one dropped after its change and a few seconds settling
        // (the re-test's steps their 2 minutes)
        public static double StepSeconds(TestOptions o) =>
            StepCount(o) * ((1 + o.KeepWindows) * 15 + 8) + RetestCount(o) * (RetestWindows - o.KeepWindows) * 15;

        // Alternate: each value followed by the baseline (1, b, 2, b, 3, b ...), so heat and clock drift show up in the
        // baseline's own numbers. One after another: 1, 2, 3 ...
        static void Sequence(List<Step> steps, List<string> values, string baseline, bool alternate, Func<string, Step> make, bool baselineFirst = false)
        {
            // a core or worker baseline that isn't one of the values (the game's own workers, every core unpinned): one
            // after another measures it first. A frame target test starts where you set it.
            if (baselineFirst && !alternate && !values.Contains(baseline)) values = new[] { baseline }.Concat(values).ToList();
            foreach (var v in values)
            {
                var s = make(v);
                s.Baseline = v == baseline;
                steps.Add(s);
                if (alternate)
                {
                    var b = make(baseline);
                    b.Baseline = true;
                    b.Label += " (baseline)";
                    steps.Add(b);
                }
            }
        }
    }
}
