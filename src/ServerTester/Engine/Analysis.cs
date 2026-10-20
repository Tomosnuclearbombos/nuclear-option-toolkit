using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace ServerTester.Engine
{
    // Shared with the NukeStats plugin (linked into src/NukeStats/NukeStats.csproj): the plugin runs the automated test on
    // a server itself and picks, analyses and writes its results with this same code, so its picks and files are the
    // tester's. Reading results back and the tester's own files are in Results.cs. Nothing here may use the tester's own
    // classes (the CPU comes from the options' CpuSnapshot) or anything newer than .NET Standard 2.1.

    // A test that ran (or is running): what the analysis and the files need from it.
    public interface ITestRun
    {
        TestOptions Options { get; }
        List<Step> Steps { get; }
        int AiReached { get; }
        int GameWorkers { get; }
        string PairPick { get; }
        string PairAuto { get; }
        string PairWhy { get; }
        bool PairByHand { get; }
        int TargetPick { get; }
        int TargetAuto { get; }
        bool TargetByHand { get; }
        List<string> Notes { get; }
        string StoppedBecause { get; }
        DateTime Started { get; }   // local time; UTC (DateTimeKind.Utc) for a test the plugin ran on a server
    }

    // What a finished (or stopped) test found, and the files it leaves in data/results/<date>/.
    public sealed partial class Results
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public int RecCores = -1, RecWorkers = -1, RecTarget = -1, AiHeld = -1, HighestHeld = -1;
        public string PairPick;   // the automated test: the core count and workers it went on with ("4x3")
        public string PairAuto;   // and the tester's own suggestions (they differ from the picks when you picked by hand)
        public int TargetAuto = -1;
        public int[] RecCpus = new int[0];
        public bool FastOnly;
        public double Noise = 0.05;
        public List<string> Findings = new List<string>();
        public string Folder;
        public string CpuModel = "";   // the PC the test ran on
        public int CpuCores = -1;      // the cores of the PC it ran on
        public bool RanOnWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        public string Mission = "";    // the test mission it ran (empty: a test from before there was a choice)
        public string HeldBy = TestPlan.HeldOnAverage;   // what its frame targets were held on
        public string NoiseFrom = "";  // where Noise came from: "runs", "baseline", or "" (the default)
        public TestOptions Options;    // what the test ran with (a saved test: read back from its results.json)
        public DateTime Date;
        // who ran it: "" = the Server Tester; PluginRunner = the NukeStats plugin, on the server itself
        public string Runner = "";
        public const string PluginRunner = "NukeStats plugin";
        public bool Container;         // the server ran in a container (a rented server): the host decides its CPUs
        public double QuotaCpus;       // the CPU limit the host held it to, in CPUs (0 = none)
        // the program's own version, for report.txt (the tester sets it to App.Version, the plugin to its own)
        public static string ToolVersion = "";

        public string AffinityHex
        {
            get { ulong m = 0; foreach (int c in RecCpus) if (c < 64) m |= 1UL << c; return m.ToString("X", Inv); }
        }

        // the setting groups of one kind, in the order they first ran: (value, its steps)
        public static List<KeyValuePair<string, List<Step>>> Groups(IEnumerable<Step> steps, string kind)
        {
            var g = new List<KeyValuePair<string, List<Step>>>();
            foreach (var s in steps.Where(x => x.Kind == kind && x.Ok))
            {
                int i = g.FindIndex(x => x.Key == s.Value);
                if (i < 0) g.Add(new KeyValuePair<string, List<Step>>(s.Value, new List<Step> { s }));
                else g[i].Value.Add(s);
            }
            return g;
        }

        public static double Mean(List<Step> l, Func<Step, double> f)
        {
            var v = l.Select(f).Where(x => !double.IsNaN(x)).ToList();
            return v.Count > 0 ? v.Average() : double.NaN;
        }

        // Sets each core and worker step's BaseRef: in the alternate order (1, b, 2, b ...) the average of the baseline
        // steps either side of it in the same run of the test (a baseline step is its own reference); in the
        // one-after-another order, or with no baseline beside it, the average of every baseline step in the run (the baseline
        // test's and the core and worker tests', all the one setting).
        public static void Relate(IList<Step> steps, bool alternate)
        {
            // the baseline test's steps and the core and worker tests' baseline steps all run the one baseline setting
            bool Usable(Step x) => x.Ok && x.MsPerFrame > 0;
            var bases = steps.Where(x => IsMainBaseline(x) && Usable(x)).ToList();
            double all = bases.Count > 0 ? bases.Average(x => x.MsPerFrame) : double.NaN;
            var byNumber = new Dictionary<int, Step>();
            foreach (var x in bases) byNumber[x.Number] = x;
            foreach (var x in steps.Where(x => x.Kind == "baseline" || x.Kind == "cores" || x.Kind == "workers" || x.Kind == "pair"))
            {
                x.BaseRef = double.NaN;
                if (!Usable(x)) continue;
                if (!alternate) { x.BaseRef = all; continue; }
                if (IsMainBaseline(x)) { x.BaseRef = x.MsPerFrame; continue; }
                // the baseline steps just before and just after it, in the same run
                var near = new[] { x.Number - 1, x.Number + 1 }.Where(byNumber.ContainsKey).Select(n => byNumber[n]).Where(b => b.Pass == x.Pass).Select(b => b.MsPerFrame).ToList();
                x.BaseRef = near.Count > 0 ? near.Average() : all;
            }
            // the grid test: against the baseline steps at the same AI just before and just after it, on busy time a SECOND
            // (busy ms a frame x fps): the frame targets differ, and at a higher target the 60 a second physics steps
            // spread over more frames, so busy time a frame would read lower for more work
            var refs = steps.Where(x => x.Kind == "grid" && x.Baseline && Usable(x)).OrderBy(x => x.Number).ToList();
            foreach (var x in steps.Where(x => x.Kind == "grid"))
            {
                x.BaseRef = double.NaN;
                if (!Usable(x) || !(x.Fps > 0)) continue;
                if (x.Baseline) { x.BaseRef = x.MsPerFrame; continue; }
                var near = new[] { refs.LastOrDefault(b => b.Ai == x.Ai && b.Number < x.Number), refs.FirstOrDefault(b => b.Ai == x.Ai && b.Number > x.Number) }
                    .Where(b => b != null && b.Fps > 0).Select(b => b.MsPerFrame * b.Fps).ToList();
                if (near.Count > 0) x.BaseRef = near.Average() / x.Fps;   // so VsBaseline = busy a second against the baseline's
            }
            // the re-test: against its own 2 minute baselines either side (their average when one is missing)
            var reBases = steps.Where(x => x.Kind == "retest" && x.Baseline && Usable(x)).ToList();
            double reAll = reBases.Count > 0 ? reBases.Average(x => x.MsPerFrame) : double.NaN;
            var reByNumber = new Dictionary<int, Step>();
            foreach (var x in reBases) reByNumber[x.Number] = x;
            foreach (var x in steps.Where(x => x.Kind == "retest"))
            {
                x.BaseRef = double.NaN;
                if (!Usable(x)) continue;
                if (x.Baseline) { x.BaseRef = x.MsPerFrame; continue; }
                var near = new[] { x.Number - 1, x.Number + 1 }.Where(reByNumber.ContainsKey).Select(n => reByNumber[n].MsPerFrame).ToList();
                x.BaseRef = near.Count > 0 ? near.Average() : reAll;
            }
        }

        // the physics workers a step's measurements ran with, the most common (the game's own count, for its own steps)
        public static int WorkersSeen(IEnumerable<Step> steps) =>
            steps.SelectMany(s => s.Windows).Where(w => w.Workers > 0).GroupBy(w => w.Workers).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();

        // a step that ran the test's baseline setting: the baseline test's, or a core or worker test's baseline step
        public static bool IsMainBaseline(Step x) => x.Baseline && (x.Kind == "baseline" || x.Kind == "cores" || x.Kind == "workers");

        // How much the same setting's comparison with its baseline moved between its own runs: differences smaller than this
        // are noise. With one run each, how much the baseline moved from one of its steps to the next. 3% to 15%.
        public static double NoiseOf(IList<Step> steps, string[] kinds, out string from)
        {
            double Rel(Step x) => x.BaseRef > 0 && !double.IsNaN(x.MsPerFrame) ? x.MsPerFrame / x.BaseRef : double.NaN;
            var spreads = new List<double>();
            foreach (var kind in kinds)
                foreach (var g in Groups(steps, kind).Where(x => x.Value.Count >= 2))
                {
                    var v = g.Value.Select(Rel).Where(x => !double.IsNaN(x) && x > 0).ToList();
                    if (v.Count >= 2 && v.Max() - v.Min() > 1e-9) spreads.Add((v.Max() - v.Min()) / v.Average());
                }
            from = spreads.Count > 0 ? "runs" : "";
            if (spreads.Count == 0)
            {
                var b = steps.Where(x => IsMainBaseline(x) && x.Ok && x.MsPerFrame > 0).OrderBy(x => x.Number).ToList();
                for (int i = 1; i < b.Count; i++) if (b[i].Pass == b[i - 1].Pass) spreads.Add(Math.Abs(b[i].MsPerFrame / b[i - 1].MsPerFrame - 1));
                if (spreads.Count > 0) from = "baseline";
            }
            return spreads.Count > 0 ? Math.Max(0.03, Math.Min(0.15, spreads.Max())) : 0.05;
        }

        // the automated test's first round: each pair measured, best first (busy time against the baseline either side)
        public static List<(string pair, double cost)> PairCosts(IList<Step> steps, TestOptions o)
        {
            Relate(steps, o.Alternate);
            double Rel(Step x) => x.BaseRef > 0 && !double.IsNaN(x.MsPerFrame) ? x.MsPerFrame / x.BaseRef : double.NaN;
            return Groups(steps, "pair").Select(g => (pair: g.Key, cost: Mean(g.Value, Rel))).Where(x => !double.IsNaN(x.cost)).OrderBy(x => x.cost).ToList();
        }

        // the re-test's pairs, best first (busy time against its own baselines either side)
        public static List<(string pair, double cost)> RetestCosts(IList<Step> steps, TestOptions o)
        {
            Relate(steps, o.Alternate);
            double Rel(Step x) => x.BaseRef > 0 && !double.IsNaN(x.MsPerFrame) ? x.MsPerFrame / x.BaseRef : double.NaN;
            return Groups(steps.Where(x => x.Kind == "retest" && !x.Baseline), "retest").Select(g => (pair: g.Key, cost: Mean(g.Value, Rel)))
                .Where(x => !double.IsNaN(x.cost)).OrderBy(x => x.cost).ToList();
        }

        // round 1's best pairs, for the re-test
        public static List<string> TopPairs(IList<Step> steps, TestOptions o) =>
            PairCosts(steps, o).Take(TestPlan.RetestPairs).Select(x => x.pair).ToList();

        // the pair to go on with: the best of the re-test. A test without one (from before it, or with only one pair): the
        // fewest cores, then the fewest workers (never fewer than 2, the game's own least, when 2 or more tie), of the
        // pairs within the noise of the best
        public static string PickPair(IList<Step> steps, TestOptions o, out string why)
        {
            why = "";
            var again = RetestCosts(steps, o);
            if (again.Count > 0)
            {
                why = again.Count > 1 ? "the best of the top " + again.Count + " measured again for 2 minutes" : "measured again for 2 minutes";
                return again[0].pair;
            }
            var costs = PairCosts(steps, o);
            if (costs.Count == 0) return null;
            double noise = NoiseOf(steps, new[] { "pair" }, out _);
            double best = costs[0].cost;
            var near = costs.Where(x => x.cost <= best * (1 + noise)).ToList();
            int CoreRank(string pair) { string c = TestPlan.PairCores(pair); return c == "all" || c == "every" ? 1000 : int.Parse(c, Inv); }
            var two = near.Where(x => TestPlan.PairWorkers(x.pair) >= 2).ToList();
            var pick = (two.Count > 0 ? two : near).OrderBy(x => CoreRank(x.pair)).ThenBy(x => TestPlan.PairWorkers(x.pair)).First();
            why = near.Count > 1 ? near.Count + " pairs within " + (noise * 100).ToString("0", Inv) + "% of the best, so the fewest cores and workers"
                                 : "the best";
            return pick.pair;
        }

        // each frame target measured: held (in every run that measured it), its spare time and the frame rate it gave
        public static List<(int target, bool held, double spare, double fps)> TargetResults(IList<Step> steps, TestOptions o)
        {
            var targetSteps = steps.Where(s => s.Kind == "target" && s.Ok).ToList();
            return targetSteps.Select(s => s.Target).Distinct().OrderBy(t => t).Select(t =>
            {
                var ss = targetSteps.Where(s => s.Target == t).ToList();
                return (target: t, held: ss.All(s => TestPlan.Holds(o, s)), spare: ss.Average(s => s.Spare), fps: ss.Average(s => s.Fps));
            }).ToList();
        }

        // the frame target to go with: the highest held with SparePct of each second spare, else the highest held, else (none
        // held) the one that gave the highest frame rate, given a higher target lifts the frame rate even when it isn't held
        public static int PickTarget(IList<Step> steps, TestOptions o, out int highestHeld, out bool noSpare, out bool noneHeld)
        {
            var targets = TargetResults(steps, o);
            highestHeld = targets.Where(x => x.held).Select(x => x.target).DefaultIfEmpty(-1).Max();
            int rec = targets.Where(x => x.held && x.spare >= o.SparePct * 10.0).Select(x => x.target).DefaultIfEmpty(-1).Max();
            noSpare = noneHeld = false;
            if (rec < 0 && highestHeld > 0) { rec = highestHeld; noSpare = true; }
            if (rec < 0 && targets.Count > 0) { rec = targets.OrderByDescending(x => x.fps).First().target; noneHeld = true; }
            return rec;
        }

        public static Results Analyse(ITestRun run)
        {
            var r = new Results { CpuModel = TestPlan.Cpu(run.Options).Model, CpuCores = TestPlan.Cpu(run.Options).Cores.Count, RanOnWindows = TestPlan.Cpu(run.Options).Windows, Date = run.Started, Mission = run.Options.Mission,
                                  HeldBy = run.Options.HeldBy, Options = run.Options, QuotaCpus = run.Options.QuotaCpus };
            var steps = run.Steps;
            var o = run.Options;

            // each core and worker step against the baseline steps either side of it, so a slow drift over the test (the
            // CPU warming up, the mission getting busier) cancels out instead of passing for a difference
            Relate(steps, o.Alternate);
            if (o.Grid)
            {
                var g = steps.Where(x => x.Kind == "grid" && x.Ok).ToList();
                r.Findings.Add("Grid test: " + g.Count(x => !x.Baseline) + " settings over AI " + string.Join(", ", o.GridAi) + ", frame targets " + string.Join(", ", o.GridTargets)
                               + (o.GridDefault ? " and the game's own 60 fps and workers" + (o.VisualSkips ? " (with and without the visual skips)" : "") : "")
                               + ", each set against the baseline at the same AI (every core, " + TestPlan.WorkersText(TestPlan.BaseWorkers(o)) + ", " + TestPlan.Compare(o) + " fps), measured "
                               + g.Count(x => x.Baseline) + " times. results.json and measurements.csv have every step.");
            }
            // a test whose baseline never measured (it timed out): its steps' own busy times, as before there were baselines
            var noBaseline = new HashSet<string>(new[] { "cores", "workers" }.Where(k => !steps.Any(x => x.Kind == k && x.BaseRef > 0)));
            double Rel(Step x) => x.BaseRef > 0 && !double.IsNaN(x.MsPerFrame) ? x.MsPerFrame / x.BaseRef : noBaseline.Contains(x.Kind) ? x.MsPerFrame : double.NaN;

            // how much the same setting's comparison with its baseline moved between its own runs: differences smaller
            // than this are noise. With one run each, how much the baseline moved from one of its steps to the next.
            var spreads = new List<double>();
            foreach (var kind in new[] { "cores", "workers", "pair" })
                foreach (var g in Groups(steps, kind).Where(x => x.Value.Count >= 2))
                {
                    var v = g.Value.Select(Rel).Where(x => !double.IsNaN(x) && x > 0).ToList();
                    if (v.Count >= 2 && v.Max() - v.Min() > 1e-9) spreads.Add((v.Max() - v.Min()) / v.Average());
                }
            r.NoiseFrom = spreads.Count > 0 ? "runs" : "";
            if (spreads.Count == 0)
                foreach (var kind in new[] { "cores", "workers", "baseline" })
                {
                    var b = steps.Where(x => x.Kind == kind && x.Baseline && x.Ok && x.MsPerFrame > 0).OrderBy(x => x.Number).ToList();
                    for (int i = 1; i < b.Count; i++) if (b[i].Pass == b[i - 1].Pass) spreads.Add(Math.Abs(b[i].MsPerFrame / b[i - 1].MsPerFrame - 1));
                }
            if (r.NoiseFrom == "" && spreads.Count > 0) r.NoiseFrom = "baseline";
            r.Noise = spreads.Count > 0 ? Math.Max(0.03, Math.Min(0.15, spreads.Max())) : 0.05;

            // the baseline test: how the server ran out of the box (or on the cores picked)
            var baseSteps = steps.Where(x => x.Kind == "baseline" && x.Ok).ToList();
            if (baseSteps.Count > 0)
            {
                int bw = TestPlan.BaseWorkers(o), seen = WorkersSeen(baseSteps);
                int bc = TestPlan.BaseCores(o);
                r.Findings.Add("Baseline (cores: " + (bc > 0 ? bc.ToString(Inv) : "Server Default (" + TestPlan.Cpu(o).Cores.Count + ")") + ", workers: "
                               + (bw > 0 ? bw.ToString(Inv) : "Server Default" + (seen > 0 ? " (" + seen + ")" : ""))
                               + ", " + baseSteps[0].Target + " fps target, " + run.AiReached + " AI aircraft"
                               + (baseSteps.Count > 1 ? ", average of " + baseSteps.Count + " steps" + (o.Repeats > 1 ? " over " + o.Repeats + " runs" : "") : "")
                               + "): main thread busy " + Mean(baseSteps, x => x.MsPerFrame).ToString("0.0", Inv) + " ms a frame at "
                               + Mean(baseSteps, x => x.Fps).ToString("0.#", Inv) + " fps, " + Mean(baseSteps, x => x.Spare).ToString("0", Inv) + " ms of each second spare.");
            }

            // a server with no time to spare on its baseline: every step is flat out, so the numbers swing a lot
            var flat = steps.Where(x => IsMainBaseline(x) && x.Ok).ToList();
            if (flat.Count > 0 && Mean(flat, x => x.Spare) < 50)
                r.Findings.Add("The server was flat out on its baseline (" + Mean(flat, x => x.Fps).ToString("0.#", Inv) + " fps at a " + flat[0].Target + " fps target, "
                               + run.AiReached + " AI aircraft), so the steps swing a lot and the comparisons are rough. Test again with "
                               + "fewer AI aircraft, 40 for example, and other programs closed.");

            // a baseline that drifted a lot from the first step to the last: say so (it is taken out of the comparison)
            foreach (var kind in new[] { "cores", "workers" })
            {
                var b = steps.Where(x => x.Kind == kind && x.Baseline && x.Ok && x.MsPerFrame > 0).OrderBy(x => x.Number).ToList();
                if (b.Count < 3 || !o.Alternate) continue;
                double drift = b[b.Count - 1].MsPerFrame / b[0].MsPerFrame - 1;
                if (Math.Abs(drift) > Math.Max(0.05, r.Noise))
                    r.Findings.Add("In the " + (kind == "cores" ? "core count" : "physics worker") + " test the baseline got " + (Math.Abs(drift) * 100).ToString("0", Inv) + "% "
                                   + (drift > 0 ? "busier" : "less busy") + " from first step to last (the CPU warming up, or the mission's load changing). Each setting is "
                                   + "compared with the baseline steps either side of it, which takes that out.");
            }

            // cores: the fewest cores whose main thread is no busier than with the best count (every fast core, or fewer when
            // fewer measured better, as one chiplet of a two-chiplet Ryzen can)
            // (the counts go up through the cores ticked; "all" is every ticked core with the efficient ones, "every" the
            // server not pinned, the out-of-the-box baseline, so neither is a count)
            var cores = Groups(steps, "cores");
            var counts = cores.Where(x => x.Key != "all" && x.Key != "every").ToList();
            int fast = TestPlan.UsableFast(o).Count;
            var allFast = cores.FirstOrDefault(x => x.Key == fast.ToString(Inv)).Value;
            if (allFast != null)
            {
                double refCost = Mean(allFast, Rel);
                double bestCost = counts.Select(x => Mean(x.Value, Rel)).Where(v => !double.IsNaN(v)).DefaultIfEmpty(refCost).Min();
                foreach (var g in counts.OrderBy(x => int.Parse(x.Key, Inv)))
                    if (Mean(g.Value, Rel) <= bestCost * (1 + r.Noise)) { r.RecCores = int.Parse(g.Key, Inv); break; }
                var all = cores.FirstOrDefault(x => x.Key == "all").Value;
                if (all != null)
                {
                    double allCost = Mean(all, Rel);
                    r.FastOnly = !(allCost < refCost * (1 - r.Noise));
                    r.Findings.Add(r.FastOnly
                        ? "The efficient cores didn't help, so keep the server on the fast cores only."
                        : "The efficient cores made the main thread less busy, so the server can have them too.");
                }
                if (r.RecCores > 0) r.RecCpus = TestPlan.CpusOf(o, r.RecCores);
                if (all != null && !r.FastOnly) { r.RecCores = TestPlan.Usable(o).Count; r.RecCpus = TestPlan.UsableCpus(o); }   // every core ticked measured best
            }

            // workers: the fewest whose main thread is no busier than with the best count (more only use more CPU)
            // (the game's own count, "0", is a baseline to compare with, not a count to suggest)
            var allWorkers = Groups(steps, "workers");
            var workers = allWorkers.Where(g => g.Key != "0").ToList();
            int baseW = TestPlan.BaseWorkers(o);
            // the counts the plan has (TestPlan.Build: 1 up to MaxWorkers), not just the ones measured
            var planned = o.TestWorkers ? Enumerable.Range(1, TestPlan.MaxWorkers(o)).Select(v => v.ToString(Inv)).ToList()
                                        : steps.Where(s => s.Kind == "workers").Select(s => s.Value).Distinct().ToList();
            bool complete = planned.Count > 0 && planned.All(v => workers.Any(g => g.Key == v));
            if (workers.Count > 0 && !complete)
                r.Findings.Add("The physics worker test didn't finish (" + workers.Count + " of " + planned.Count + " counts measured), so it suggests no worker count.");
            if (workers.Count > 0 && complete)
            {
                double best = allWorkers.Select(g => Mean(g.Value, Rel)).Where(x => !double.IsNaN(x)).DefaultIfEmpty(double.NaN).Min();
                if (!double.IsNaN(best))
                {
                    r.RecWorkers = workers.Where(g => Mean(g.Value, Rel) <= best * (1 + r.Noise)).Select(g => int.Parse(g.Key, Inv)).DefaultIfEmpty(baseW > 0 ? baseW : -1).Min();
                    // never fewer than the game's own least (GameManager.SetJobCount: 2 to 8), when 2 was tested
                    if (r.RecWorkers < 0)
                        r.Findings.Add("Server Default workers measured best, so leave the worker count to the game (Perf.JobWorkers 0).");
                    if (r.RecWorkers == 1 && workers.Any(g => g.Key == "2"))
                    {
                        r.RecWorkers = 2;
                        r.Findings.Add("1 physics worker was no slower than more at this load, but the game never uses fewer than 2, so 2 is suggested. "
                                       + "A busier server needs more: test with more AI aircraft to see where they start to matter.");
                    }
                }
            }

            // frame target: held = 98% of the target or better (on the measure picked), in every run of the whole test. The
            // suggestion is the highest one held with at least SparePct of each second left over on average, given a busy
            // match with players costs more than this test does; with none held, the one that gave the highest frame rate.
            var targetSteps = steps.Where(s => s.Kind == "target").ToList();
            var targets = TargetResults(steps, o);
            r.RecTarget = PickTarget(steps, o, out r.HighestHeld, out bool noSpare, out bool noneHeld);
            bool everyRun = o.Repeats > 1 && targetSteps.Where(s => s.Target == r.HighestHeld && s.Ok).Select(s => s.Pass).Distinct().Count() >= o.Repeats;
            if (r.HighestHeld > 0)
                r.Findings.Add("The highest frame target held" + (o.HeldBy == TestPlan.HeldOnAverage ? "" : " on " + TestPlan.HeldName(o.HeldBy)) + " was " + r.HighestHeld + " fps at " + run.AiReached + " AI aircraft"
                               + (everyRun ? " (in every run)" : "")
                               + (noSpare ? "; none left " + o.SparePct + "% of each second spare, so the highest held, " + r.RecTarget + ", is suggested."
                                  : r.RecTarget > 0 && r.RecTarget < r.HighestHeld ? "; " + r.RecTarget + " is the highest that left " + o.SparePct + "% of each second spare." : "."));
            else if (noneHeld)
            {
                // none held: the target that gave the most frames, and what it ran at; with next to no spare time the CPU was
                // flat out at this AI load
                var best = targets.First(x => x.target == r.RecTarget);
                r.Findings.Insert(0, "No frame target held" + (o.HeldBy == TestPlan.HeldOnAverage ? "" : " on " + TestPlan.HeldName(o.HeldBy)) + " at " + run.AiReached
                               + " AI aircraft. The highest frame rate, " + best.fps.ToString("0.#", Inv) + " fps, came at a target of " + best.target
                               + ", so it is suggested" + (best.spare < 100 ? ". The CPU was flat out at this load (" + best.spare.ToString("0", Inv) + " ms of each second spare): with fewer "
                                                     + "AI aircraft, 40 for example, a target can hold." : "."));
            }

            var aiSteps = steps.Where(s => s.Kind == "ai" && s.Ok).ToList();
            if (aiSteps.Count > 0)
            {
                // the AI that was really up, not the AI asked for
                // held: the frame rate stayed at its target, or (a target the server couldn't hold) at what its first step ran at
                var first = aiSteps.OrderBy(s => s.Number).First();
                double hold = Math.Min(first.Target, first.Fps);
                bool underTarget = first.Fps < first.Target * 0.98;
                r.AiHeld = aiSteps.Where(s => s.Fps >= hold * 0.98).Select(s => (int)Math.Round(double.IsNaN(s.AiSeen) ? s.Ai : s.AiSeen)).DefaultIfEmpty(-1).Max();
                bool topHeld = aiSteps.OrderBy(s => s.Ai).Last() is Step top && top.Fps >= hold * 0.98 && top.Ai + Math.Max(1, o.AiStep) > o.AiMax;
                if (r.AiHeld > 0) r.Findings.Add((underTarget ? "At a frame target of " + first.Target + " the server ran about " + first.Fps.ToString("0", Inv) + " fps and kept that"
                                                               : first.Target + " fps held") + " up to " + r.AiHeld + " AI aircraft with nobody connected"
                                                 + (run.PairPick != null ? " on " + TestPlan.PairText(o, run.PairPick) : "")
                                                 + (topHeld ? " (every load tested held; " + o.AiMax + " was the most tested)." : "."));
            }

            // the automated test: its picks, made automatically or by you as each round finished
            r.TargetAuto = run.TargetAuto > 0 ? run.TargetAuto : -1;
            if (run.PairPick != null)
            {
                r.PairPick = run.PairPick;
                r.PairAuto = run.PairAuto ?? run.PairPick;
                string c = TestPlan.PairCores(run.PairPick);
                int w = TestPlan.PairWorkers(run.PairPick);
                if (c == "every")
                {
                    // every core, not pinned (a server under a CPU limit): no cores to set, the host or the system decides them
                    r.RecCores = -1;
                    r.RecCpus = new int[0];
                    r.FastOnly = false;
                }
                else
                {
                    r.RecCpus = TestPlan.CoreCpus(o, c);
                    r.RecCores = c == "all" ? TestPlan.Usable(o).Count : int.Parse(c, Inv);
                    r.FastOnly = TestPlan.Cpu(o).Hybrid && c != "all" && TestPlan.UsableFast(o).All(k => k.Type != "efficient");
                }
                r.RecWorkers = w;
                var costs = PairCosts(steps, o);
                // its 2 minute re-test when it had one
                var again = RetestCosts(steps, o).Where(x => x.pair == run.PairPick).ToList();
                double vs = (again.Count > 0 ? again : costs.Where(x => x.pair == run.PairPick)).Select(x => (x.cost - 1) * 100).DefaultIfEmpty(double.NaN).First();
                if (costs.Count == 0 && !string.IsNullOrEmpty(run.PairWhy))   // no round 1 (the plugin's AI load round only)
                    r.Findings.Insert(0, "Cores and physics workers: " + TestPlan.PairText(o, run.PairPick) + ", " + run.PairWhy + ".");
                else
                    r.Findings.Insert(0, "Cores and physics workers: " + TestPlan.PairText(o, run.PairPick) + (double.IsNaN(vs) ? "" : " (" + vs.ToString("+0.0;-0.0;0.0", Inv) + "% busy time against baseline)")
                                         + ", picked " + (run.PairByHand ? "by you" : "automatically") + " from " + costs.Count + " pairs measured"
                                         + (run.PairByHand || string.IsNullOrEmpty(run.PairWhy) ? "." : ": " + run.PairWhy + "."));
            }
            // a server under a CPU limit: why there is no core count
            if (o.QuotaCpus > 0)
                r.Findings.Add("The host holds this server to " + o.QuotaCpus.ToString("0.##", Inv) + " CPUs' worth of time (a CPU limit) though it can see "
                               + TestPlan.Cpu(o).Threads + ", so pinning can't help: the cores and workers round measured the physics workers on every core, not pinned"
                               + (o.PinUnderQuota ? ", and core counts up to one more than the limit." : ". The host decides the CPUs."));
            if (run.TargetPick > 0 && run.TargetByHand)
            {
                r.RecTarget = run.TargetPick;
                r.Findings.Add("You picked a frame target of " + run.TargetPick + " fps for the AI load round.");
            }

            if (!o.Alternate && (o.TestCores || o.TestWorkers || o.Automated))
                r.Findings.Add("Cores and workers ran one after another, so slow drift (the CPU warming up, its clock, the "
                               + "mission settling) is mixed into their differences. The alternate order takes it out.");
            if (steps.Any(s => s.Throttled))
                r.Findings.Add("The CPU was held back (heat or a power limit) in " + steps.Count(s => s.Throttled) + " step(s), marked in the table. That moves results more "
                               + "than any setting: check the cooling and power settings, then test again.");
            return r;
        }

        // ---------------------------------------------------------------- the files' contents
        // results.json as a tree of dictionaries, lists and values, in the order it is written: the tester writes it with
        // System.Text.Json, the plugin with its own small writer. version, server (the game's build id) and os say what ran
        // it; the plugin adds its own parts (the clock it used, who ran it, the host) after.
        public Dictionary<string, object> JsonTree(ITestRun run, string version, string server, string os)
        {
            var o = run.Options;
            var cpu = TestPlan.Cpu(o);
            var test = new Dictionary<string, object>
            {
                ["mission"] = o.Mission, ["heldBy"] = o.HeldBy, ["ai"] = o.Ai, ["aiReached"] = run.AiReached, ["alternate"] = o.Alternate, ["repeats"] = o.Repeats,
                ["compareFps"] = TestPlan.Compare(o), ["targets"] = o.Targets(), ["targetAlternate"] = o.TargetAlternate,
                ["targetBaseline"] = o.BaselineTarget, ["sparePct"] = o.SparePct,
                ["aiStart"] = o.AiFrom, ["aiStep"] = Math.Max(1, o.AiStep), ["aiMax"] = o.AiMax,
                ["automated"] = o.Automated, ["pickPairByHand"] = o.PickPairByHand, ["pickTargetByHand"] = o.PickTargetByHand,
                ["useCores"] = o.UseCores ?? new int[0], ["useCpus"] = CpuList.Text(TestPlan.UsableCpus(o)),
                ["pairPick"] = run.PairPick ?? "", ["pairAuto"] = run.PairAuto ?? "", ["pairWhy"] = run.PairWhy ?? "", ["pairByHand"] = run.PairByHand,
                ["targetPick"] = run.TargetPick, ["targetAuto"] = run.TargetAuto, ["targetByHand"] = run.TargetByHand,
                ["baselineCores"] = TestPlan.BaseCores(o), ["baselineWorkers"] = TestPlan.BaseWorkers(o), ["gameWorkers"] = run.GameWorkers, ["keepWindows"] = o.KeepWindows,
                ["noise"] = Math.Round(Noise, 3), ["noiseFrom"] = NoiseFrom, ["server"] = server ?? "", ["os"] = os ?? "", ["visualSkips"] = o.VisualSkips,
            };
            if (o.Grid)
            {
                test["grid"] = true; test["gridAi"] = o.GridAi; test["gridTargets"] = o.GridTargets; test["gridWorkers"] = o.GridWorkers;
                test["gridCores"] = o.GridCores; test["refEvery"] = o.RefEvery; test["gridDefault"] = o.GridDefault;
            }
            // a server under a CPU limit (the plugin's test): the cores and workers round tested the workers, not pinned
            if (o.QuotaCpus > 0) { test["quotaCpus"] = Math.Round(o.QuotaCpus, 2); test["pinUnderQuota"] = o.PinUnderQuota; }
            return new Dictionary<string, object>
            {
                ["tool"] = "NukeStats Server Tester",
                ["version"] = version,
                ["date"] = run.Started.ToString("o", Inv),
                ["stoppedBecause"] = run.StoppedBecause,
                ["cpu"] = new Dictionary<string, object>
                {
                    ["model"] = cpu.Model, ["cores"] = cpu.Cores.Count, ["threads"] = cpu.Threads,
                    ["hybrid"] = cpu.Hybrid, ["fastCores"] = TestPlan.FastCountOf(o),
                    ["layout"] = cpu.Ranked().Select(c => new Dictionary<string, object> { ["cpus"] = c.CpuText, ["type"] = c.Type, ["score"] = double.IsNaN(c.Score) ? null : (object)Math.Round(c.Score),
                                                                                         ["index"] = c.Index }).ToList(),
                },
                ["test"] = test,
                ["recommend"] = new Dictionary<string, object>
                {
                    ["cores"] = RecCores, ["cpus"] = RecCpus.Length > 0 ? CpuList.Text(RecCpus) : "", ["affinityHex"] = RecCpus.Length > 0 && RanOnWindows ? AffinityHex : "",   // Linux numbers the threads differently
                    ["fastCoresOnly"] = FastOnly, ["workers"] = RecWorkers, ["targetFps"] = RecTarget, ["highestHeldFps"] = HighestHeld,
                },
                // the plugin settings the webcc applies (Settings > Apply Server Tester results)
                ["settings"] = SettingsToApply(),
                ["findings"] = Findings.Concat(run.Notes).ToList(),
                ["steps"] = run.Steps.Where(s => s.Ok || s.Note.Length > 0).Select(s => new Dictionary<string, object>
                {
                    ["n"] = s.Number, ["run"] = s.Pass, ["kind"] = s.Kind, ["value"] = s.Value, ["label"] = s.Label, ["baseline"] = s.Baseline,
                    ["cpus"] = s.Cpus != null ? CpuList.Text(s.Cpus) : "", ["workers"] = s.Workers, ["target"] = s.Target, ["ai"] = R(s.AiSeen, 0),
                    ["fps"] = R(s.Fps, 1), ["p95"] = R(s.P95, 1), ["busyMsPerFrame"] = R(s.MsPerFrame, 3), ["physicsMsPerSec"] = R(s.Physics, 1),
                    ["vsBaselinePct"] = R(s.VsBaseline, 1), ["low5Fps"] = R(s.Low5, 1), ["low1Fps"] = R(s.Low1, 1), ["longestMs"] = R(s.Longest, 1),
                    ["spareMsPerSec"] = R(s.Spare, 1), ["serverCpuPct"] = R(s.CpuPct, 0), ["mainMhz"] = R(s.Mhz, 0), ["tempC"] = R(s.TempC, 0),
                    ["throttled"] = s.Throttled, ["note"] = s.Note,
                }).ToList(),
            };
        }

        public const string CsvHeader = "step,kind,value,label,baseline,window_time,fps,ft_p95,busy_ms_per_frame,physics_ms_s,spare_ms_s,fixed_ms_s,jobs_wait_ms_s,ai,workers,target,server_cpu_pct,main_mhz,temp_c,throttled,run,ft_p99,ft_max";

        // measurements.csv: every measurement every step kept, one a line (window_time is the time of day it was taken)
        public static string CsvText(ITestRun run)
        {
            var csv = new StringBuilder(CsvHeader + "\n");
            foreach (var s in run.Steps)
                foreach (var w in s.Windows)
                    csv.AppendLine(string.Join(",", s.Number.ToString(Inv), s.Kind, s.Value, "\"" + s.Label + "\"", s.Baseline ? "1" : "0", w.At.ToString("HH:mm:ss", Inv),
                        F(w.Fps), F(w.P95), F(w.MsPerFrame), F(w.Physics), F(w.Spare), F(w.FixedPhase), F(w.JobsWait), w.Ai.ToString(Inv), w.Workers.ToString(Inv), w.Target.ToString(Inv),
                        F(w.CpuPct), F(w.MainMhz), F(w.TempC), w.Throttled ? "1" : "0", s.Pass.ToString(Inv), F(w.P99), F(w.FtMax)));
            return csv.ToString();
        }

        public Dictionary<string, string> SettingsToApply()
        {
            var d = new Dictionary<string, string>();
            if (RecWorkers > 0) d["Perf.JobWorkers"] = RecWorkers.ToString(Inv);
            if (RecTarget > 0) d["Perf.TargetFps"] = RecTarget.ToString(Inv);
            return d;
        }

        static string N0(double v) => double.IsNaN(v) ? "" : v.ToString("0", Inv);

        static object R(double v, int digits) => double.IsNaN(v) ? null : (object)Math.Round(v, digits);
        static string F(double v) => double.IsNaN(v) ? "" : v.ToString("0.###", Inv);

        // ---------------------------------------------------------------- the plain-text report
        public string Report(ITestRun run)
        {
            var sb = new StringBuilder();
            var cpu = TestPlan.Cpu(run.Options);
            int fast = TestPlan.FastCountOf(run.Options);
            sb.AppendLine("Nuclear Option Server Tester " + ToolVersion + ", " + run.Started.ToString("d MMM yyyy HH:mm", Inv) + (run.Started.Kind == DateTimeKind.Utc ? " UTC" : ""));
            sb.AppendLine("CPU: " + cpu.Model + ", " + cpu.Cores.Count + " cores / " + cpu.Threads + " threads"
                          + (cpu.Hybrid ? " (" + fast + " fast, " + (cpu.Cores.Count - fast) + " efficient)" : ""));
            sb.AppendLine("Load: " + run.Options.Mission + " (" + (run.Options.Mission == TestPlan.RealisticBenchmark
                          ? "the full Escalation battle, every ground unit and ship" : "Escalation's map, aircraft only, guns only") + "), " + run.AiReached + " AI aircraft, nobody connected. Cores and workers compared at " + TestPlan.Compare(run.Options) + " fps.");
            if (run.StoppedBecause != null) sb.AppendLine("The test stopped early: " + run.StoppedBecause + ". The results are from before that.");
            sb.AppendLine();
            sb.AppendLine("WHAT TO SET");
            if (RecCores > 0) sb.AppendLine("  Cores:   " + RecCores + " (CPUs " + CpuList.Text(RecCpus) + (RanOnWindows ? ", Windows affinity mask " + AffinityHex : "") + ")");
            if (RecWorkers > 0) sb.AppendLine("  Physics workers (Perf.JobWorkers):  " + RecWorkers);
            if (RecTarget > 0) sb.AppendLine("  Frame target (Perf.TargetFps):     " + RecTarget);
            foreach (var f in Findings.Concat(run.Notes)) sb.AppendLine("  - " + f);
            sb.AppendLine("An empty test server does different work to a full match: use these to pick the cores and workers, and check the");
            sb.AppendLine("frame target on a busy night.");
            sb.AppendLine();
            sb.AppendLine("STEPS (busy = ms the main thread worked each frame, lower is better; vs base = busy time against the");
            sb.AppendLine(run.Options.Alternate ? "baseline steps either side, which cores and workers are judged by)"
                          : "average of every baseline step in the run, which cores and workers are judged by; this order doesn't take drift out)");
            bool runs = run.Options.Repeats > 1;
            sb.AppendLine(string.Format(Inv, "  {0,-4} {10}{1,-44} {2,7} {3,9} {11,8} {4,8} {5,7} {6,8} {7,6} {8,5}", "#", "setting", "fps", "busy ms", "physics", "spare", "srv CPU", "MHz", "temp", "", runs ? "run " : "", "vs base"));
            foreach (var s in run.Steps.Where(x => x.Ok))
                sb.AppendLine(string.Format(Inv, "  {0,-4} {10}{1,-44} {2,7:0.0} {3,9:0.000} {11,8} {4,8} {5,7} {6,8} {7,6} {8,5}{9}", s.Number,
                    s.Label.Length > 44 ? s.Label.Substring(0, 43) + "~" : s.Label, s.Fps, s.MsPerFrame, N0(s.Physics), N0(s.Spare),
                    double.IsNaN(s.CpuPct) ? "" : N0(s.CpuPct) + "%", N0(s.Mhz), N0(s.TempC), s.Throttled ? "  held back" : "",
                    runs ? s.Pass.ToString(Inv).PadRight(4) : "", double.IsNaN(s.VsBaseline) ? "" : s.VsBaseline.ToString("+0.0;-0.0;0.0", Inv) + "%"));
            // the frame target steps with their lows and longest frame
            var tsteps = run.Steps.Where(x => x.Ok && x.Kind == "target").ToList();
            if (tsteps.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("FRAME TARGET (held on " + TestPlan.HeldName(run.Options.HeldBy) + ": " + (TestPlan.HeldShare(run.Options.HeldBy) * 100).ToString("0", Inv) + "% of the target or better)");
                sb.AppendLine(string.Format(Inv, "  {0,-4} {7}{1,-8} {2,8} {3,8} {4,8} {5,9} {6,7}", "#", "target", "average", "5% low", "1% low", "longest", "spare", runs ? "run " : ""));
                foreach (var s in tsteps)
                    sb.AppendLine(string.Format(Inv, "  {0,-4} {7}{1,-8} {2,8:0.0} {3,8} {4,8} {5,9} {6,7:0}{8}", s.Number, s.Target, s.Fps,
                        double.IsNaN(s.Low5) ? "" : s.Low5.ToString("0.0", Inv), double.IsNaN(s.Low1) ? "" : s.Low1.ToString("0.0", Inv),
                        double.IsNaN(s.Longest) ? "" : s.Longest.ToString("0.0", Inv) + " ms", s.Spare, runs ? s.Pass.ToString(Inv).PadRight(4) : "",
                        TestPlan.Holds(run.Options, s) ? "" : "  missed"));
            }
            sb.AppendLine();
            if (run.Steps.Any(x => x.Ok && (x.Kind == "cores" || x.Kind == "workers")))
                sb.AppendLine("Differences under " + (Noise * 100).ToString("0", Inv) + "% count as none ("
                              + (NoiseFrom == "runs" ? "how much the same setting moved between its own runs, against its baselines"
                                 : NoiseFrom == "baseline" ? "how much the baseline moved from one step to the next"
                                 : "the default: nothing ran twice to measure it") + ").");
            return sb.ToString();
        }
    }
}
