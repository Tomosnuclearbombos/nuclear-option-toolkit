using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ServerTester.Engine;

namespace NukeStats
{
    // The self-test's steps, picks and files, apart from the game (no Unity here, so a harness can drive it with made-up
    // measurements and check it against the Server Tester). The steps come from the tester's own TestPlan, the picks from
    // its Results.PickPair and PickTarget, the findings and files from its Results (src/ServerTester/Engine, linked into
    // this build), so a test the plugin runs on a server reads exactly like one the tester ran.
    internal sealed class SelfTestRun : ITestRun
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public TestOptions Options { get; private set; }
        public List<Step> Steps { get; } = new List<Step>();
        public int AiReached { get; set; }
        public int GameWorkers { get; set; }   // the physics workers the game picked on its own, as measured in the warm-up
        public string PairPick { get; private set; }
        public string PairAuto { get; private set; }
        public string PairWhy { get; private set; } = "";
        public bool PairByHand => false;       // nobody picks by hand on a server: each round's suggestion is taken
        public int TargetPick { get; private set; }
        public int TargetAuto { get; private set; }
        public bool TargetByHand => false;
        public List<string> Notes { get; } = new List<string>();
        public string StoppedBecause { get; set; }
        public DateTime Started { get; private set; }   // UTC
        public int Round { get; private set; } = 1;     // 1 baseline + cores and workers, 2 frame target, 3 AI load
        public int LastRound = 3;                       // a test cut short on purpose (selftest|start Parts=...) stops after this round

        int _at = -1;                                   // the step running now (Steps[_at])

        public SelfTestRun(TestOptions o, DateTime startedUtc)
        {
            Options = o;
            Started = DateTime.SpecifyKind(startedUtc, DateTimeKind.Utc);
            Steps.AddRange(TestPlan.Build(o));
            if (o.Grid) Round = 3;   // the grid test is all its steps, no rounds after
        }

        public Step Current => _at >= 0 && _at < Steps.Count ? Steps[_at] : null;
        public int Done => Math.Max(0, _at);

        // The next step to run, or null when the test is done. Mirrors TestRunner.Run: every frame target is measured (a
        // higher target can still lift the frame rate when a lower one wasn't held), every AI load up to the most set is
        // measured (the analysis says how far its frame rate held), and when a round's last step is done its pick is made and
        // the next round's steps are added.
        public Step Next(Action<string> say)
        {
            if (_at >= 0) NextRound(say);
            _at++;
            if (_at >= Steps.Count) return null;
            return Steps[_at];
        }

        // Parts=ai with a Pair: the AI load round only, on the pair and frame target given; why it can't, or null
        public string AiOnly(string pair, int target)
        {
            string c; int w;
            try { c = TestPlan.PairCores(pair); w = TestPlan.PairWorkers(pair); }
            catch { return "Pair=" + pair + " isn't a pair of cores and workers, like everyx6"; }
            if (!TestPlan.CoreValues(Options).Contains(c) || w < 1 || w > TestPlan.MaxWorkers(Options)) return "Pair=" + pair + " isn't one this server can run";
            Steps.Clear();
            Steps.AddRange(TestPlan.AutoAi(Options, pair, target));
            for (int i = 0; i < Steps.Count; i++) Steps[i].Number = i + 1;
            Round = 3;
            PairPick = PairAuto = pair; PairWhy = "given at the start (the AI load round only)";
            TargetPick = TargetAuto = target;
            return null;
        }

        // the AI load a step needs to reach before its measurements count (a few short: shot down, respawning)
        public int AiFloor(Step s)
        {
            int reached = s.AiReachedForStep > 0 ? s.AiReachedForStep : AiReached;
            return Math.Max(1, (int)Math.Floor(Math.Min(s.Ai, reached > 0 ? reached : s.Ai) * 0.85));
        }

        // a measurement that ran with the step's own settings, nobody connected and the AI up
        public bool Counts(Step s, Measurement w)
        {
            bool workersOk = s.Workers > 0 ? w.Workers == s.Workers : GameWorkers <= 0 || w.Workers == GameWorkers;   // 0: the game's own count
            return workersOk && w.Target == s.Target && w.Humans == 0 && w.Ai >= AiFloor(s);
        }

        bool _retestAdded;
        public bool RetestAdded => _retestAdded;   // round 1's best pairs have their 2 minute steps

        void NextRound(Action<string> say)
        {
            while (Options.Automated && _at == Steps.Count - 1 && Round < 3)
            {
                // round 1's best pairs measured again, 2 minutes each, before its pick (none with fewer than 2 to choose from)
                if (Round == 1 && !_retestAdded)
                {
                    _retestAdded = true;
                    var top = Results.TopPairs(Steps.ToList(), Options);
                    if (top.Count >= 2)
                    {
                        say("The best " + top.Count + " measured again, 2 minutes each: " + string.Join("; ", top.Select(p => TestPlan.PairText(Options, p))));
                        foreach (var n in TestPlan.AutoRetest(Options, top)) { n.Number = Steps.Count + 1; Steps.Add(n); }
                        continue;
                    }
                }
                var next = Round == 1 ? AfterPairs(say) : AfterTargets(say);
                if (next == null) break;   // nothing to go on with (the round didn't measure, or the test stops after it)
                Round++;
                foreach (var n in next) { n.Number = Steps.Count + 1; Steps.Add(n); }
            }
        }

        // round 1 done: the pair to go on with, then the frame target steps on it
        List<Step> AfterPairs(Action<string> say)
        {
            string auto = Results.PickPair(Steps.ToList(), Options, out string why);
            if (auto == null) { say("No core and worker pair measured, so the automated test stops here"); return null; }
            PairPick = PairAuto = auto; PairWhy = why;
            say("Cores and workers: " + TestPlan.PairText(Options, PairPick) + ", picked automatically (" + why + ")");
            if (LastRound < 2) return null;
            var next = TestPlan.AutoTargets(Options, PairPick);
            return next.Count > 0 ? next : AfterTargets(say);
        }

        // round 2 done: the frame target to go on with, then the AI load steps on both picks
        List<Step> AfterTargets(Action<string> say)
        {
            int auto = Results.PickTarget(Steps.ToList(), Options, out _, out _, out bool noneHeld);
            TargetAuto = auto;
            TargetPick = auto > 0 ? auto : TestPlan.Compare(Options);
            say("Frame target for the AI load round: " + TargetPick + " fps" + (noneHeld ? ", none held, so the one with the highest frame rate"
                : auto > 0 ? ", picked automatically" : " (none measured, so the compare rate)"));
            if (LastRound < 3) return null;
            return TestPlan.AutoAi(Options, PairPick, TargetPick);
        }

        // every step the whole test will have, for the estimate (the rounds still to come as if every target is reached)
        public int StepsPlanned()
        {
            int n = Steps.Count;
            if (Round < 2 && !_retestAdded) n += TestPlan.RetestCount(Options);
            if (Round < 2 && LastRound >= 2) n += Options.Targets().Length * (Options.TargetAlternate ? 2 : 1) * Math.Max(1, Options.Repeats);
            if (Round < 3 && LastRound >= 3) n += Options.AiLoads().Length;
            return n;
        }

        // the step's line for the log, as the tester's
        public string StepLine(Step s)
        {
            int total = StepsPlanned();
            if (!s.Ok) return "Step " + s.Number + " " + s.Label + ": no result (" + s.Note + ")";
            return string.Format(Inv, "Step {0}/{1} {2}: {3:0.0} fps, busy {4:0.00} ms a frame, physics {5:0} ms/s, spare {6:0} ms/s, server CPU {7:0}%{8}{9}",
                s.Number, total, s.Label, s.Fps, s.MsPerFrame, s.Physics, s.Spare, s.CpuPct,
                double.IsNaN(s.Mhz) ? "" : string.Format(Inv, ", {0:0} MHz", s.Mhz), s.Throttled ? ", CPU HELD BACK" : "");
        }
    }

    // What one self-test runs with: the [SelfTest] settings, with any the start command names (selftest|start|Ai=60;...)
    // in their place for that test only. Never saved.
    internal sealed class SelfTestSettings
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public int StepSeconds = 30, Ai = 60, WarmupSeconds = 120, MaxCores, MaxWorkers = 8, TargetStart = 60, TargetStep = 10, TargetSteps = 7,
                   SparePct = 25, AiStep = 10, AiMax = 120, MinEmptySeconds = 120;
        public bool Alternate = true, VisualSkips = true, PinUnderQuota, CoreBench = true;
        public string UseCpus = "", Parts = "baseline,pairs,target,ai";
        // Parts=ai with a Pair: the AI load round only, on that pair of cores and workers ("everyx6") at Target fps
        // (rounds 1 and 2 left out, to check other AI levels on what a test already picked)
        public string Pair = "";
        public int Target;
        // Parts=grid: the grid test (TestOptions.Grid), with these lists ("20,30,40"; workers 0 = Server Default; cores
        // "every" = not pinned, or counts of the fastest cores)
        public string GridAi = "20,30,40,50,60", GridTargets = "90,120", GridWorkers = "0,1,2,3,4,6,8", GridCores = "every";
        public int RefEvery = 6;
        public bool GridDefault;   // GridDefault=1: each AI level also measures the game's own 60 fps target and workers
        public int BaselineWorkers = -1;   // -1 = one fewer than the cores (TestPlan.DefaultBaseWorkers), 0 = the game's own
        public bool IsGrid => Parts.ToLowerInvariant().Split(',').Select(x => x.Trim()).Contains("grid");
        public List<string> Ignored = new List<string>();   // names in the command it didn't know

        // "StepSeconds=15;Ai=60;Alternate=0;UseCpus=0-3,8-11" over these settings (a value may hold commas, so only ; splits)
        public void Apply(string args)
        {
            foreach (var part in (args ?? "").Split(';'))
            {
                if (part.Trim().Length == 0) continue;
                int eq = part.IndexOf('=');
                if (eq <= 0) { Ignored.Add(part.Trim()); continue; }
                string k = part.Substring(0, eq).Trim(), v = part.Substring(eq + 1).Trim();
                if (!Set(k, v)) Ignored.Add(k);
            }
        }

        // Parts names the rounds that run: the baseline and the pairs always do, "target" and "ai" the two after them
        public void ApplyParts(string v) { Parts = v.Replace('+', ',').Replace(' ', ','); }

        bool Set(string k, string v)
        {
            bool B() => v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("on", StringComparison.OrdinalIgnoreCase) || v.Equals("yes", StringComparison.OrdinalIgnoreCase);
            int I(int def) => int.TryParse(v, NumberStyles.Integer, Inv, out int x) ? x : def;
            switch (k.ToLowerInvariant())
            {
                case "stepseconds": StepSeconds = I(StepSeconds); return true;
                case "ai": Ai = I(Ai); return true;
                case "warmupseconds": WarmupSeconds = I(WarmupSeconds); return true;
                case "maxcores": MaxCores = I(MaxCores); return true;
                case "maxworkers": MaxWorkers = I(MaxWorkers); return true;
                case "targetstart": TargetStart = I(TargetStart); return true;
                case "targetstep": TargetStep = I(TargetStep); return true;
                case "targetsteps": TargetSteps = I(TargetSteps); return true;
                case "sparepct": SparePct = I(SparePct); return true;
                case "aistep": AiStep = I(AiStep); return true;
                case "aimax": AiMax = I(AiMax); return true;
                case "minemptyseconds": MinEmptySeconds = I(MinEmptySeconds); return true;
                case "alternate": Alternate = B(); return true;
                case "visualskips": VisualSkips = B(); return true;
                case "pinunderquota": PinUnderQuota = B(); return true;
                case "corebench": CoreBench = B(); return true;
                case "usecpus": UseCpus = v; return true;
                case "parts": ApplyParts(v); return true;
                case "pair": Pair = v; return true;
                case "gridai": GridAi = v; return true;
                case "gridtargets": GridTargets = v; return true;
                case "gridworkers": GridWorkers = v; return true;
                case "gridcores": GridCores = v; return true;
                case "refevery": RefEvery = I(RefEvery); return true;
                case "griddefault": GridDefault = B(); return true;
                case "baselineworkers": BaselineWorkers = I(BaselineWorkers); return true;
                case "target": Target = I(Target); return true;
            }
            return false;
        }

        // every value inside what the test can run with
        public void Clamp()
        {
            StepSeconds = Math.Max(15, Math.Min(120, (int)Math.Round(StepSeconds / 15.0) * 15));   // whole 15 s measurements
            Ai = Math.Max(1, Math.Min(400, Ai));
            WarmupSeconds = Math.Max(0, Math.Min(900, WarmupSeconds));
            MaxCores = Math.Max(0, MaxCores);
            MaxWorkers = Math.Max(1, Math.Min(16, MaxWorkers));
            TargetStart = Math.Max(60, Math.Min(240, TargetStart));                                // NukeStats' TargetFps works from 60 to 240
            TargetStep = Math.Max(1, Math.Min(100, TargetStep));
            TargetSteps = Math.Max(2, Math.Min(20, TargetSteps));
            SparePct = Math.Max(0, Math.Min(90, SparePct));
            AiStep = Math.Max(1, AiStep);
            AiMax = Math.Max(1, Math.Min(400, AiMax));
            MinEmptySeconds = Math.Max(0, Math.Min(3600, MinEmptySeconds));
            Target = Target <= 0 ? 0 : Math.Max(60, Math.Min(240, Target));
            RefEvery = Math.Max(1, Math.Min(50, RefEvery));
        }

        // only the AI load round (Parts=ai and a Pair)
        public bool AiOnly
        {
            get
            {
                var p = Parts.ToLowerInvariant().Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                return p.Count == 1 && p[0] == "ai" && Pair.Length > 0;
            }
        }

        // the last round the test runs: 3 with the AI load, 2 with the frame target but not the AI load, else 1
        public int LastRound
        {
            get
            {
                var p = Parts.ToLowerInvariant().Split(',').Select(x => x.Trim()).ToList();
                return p.Contains("ai") ? 3 : p.Contains("target") ? 2 : 1;
            }
        }

        // the Server Tester's automated test with these settings on this CPU (TestOptions Automated, as its window and
        // --cli --automated set it up): the baseline out of the box, always alternating (the Alternate setting doesn't turn
        // that off), one run, the Stress Test
        public TestOptions Options(CpuSnapshot cpu, int jobWorkerMax, double quotaCpus)
        {
            var o = new TestOptions
            {
                Automated = true, Cpu = cpu, Mission = TestPlan.StressTest, Alternate = true, Repeats = 1,   // the automated test always alternates
                BaselineCores = 0, BaselineWorkers = 0, MaxCores = MaxCores,
                // the game can't run more physics workers than JobsUtility.JobWorkerMaximumCount
                MaxWorkers = jobWorkerMax > 0 ? Math.Max(1, Math.Min(MaxWorkers, jobWorkerMax)) : MaxWorkers,
                Ai = Ai, AiStart = Ai, AiStep = AiStep, AiMax = AiMax,
                KeepWindows = StepSeconds / 15, WarmupWindows = WarmupSeconds / 15, VisualSkips = VisualSkips, HighPower = false,
                TargetStart = TargetStart, TargetStep = TargetStep, TargetSteps = TargetSteps, TargetAlternate = false, TargetBaseline = TestPlan.CompareTarget,
                CompareFps = TestPlan.CompareTarget, SparePct = SparePct, HeldBy = TestPlan.HeldOnAverage,
                QuotaCpus = quotaCpus, PinUnderQuota = quotaCpus > 0 && PinUnderQuota,
            };
            o.BaselineWorkers = BaselineWorkers < 0 ? TestPlan.DefaultBaseWorkers(o) : BaselineWorkers;
            if (IsGrid)
            {
                int[] Ints(string v) => v.Split(',').Select(x => int.TryParse(x.Trim(), NumberStyles.Integer, Inv, out int n) ? n : -1).Where(n => n >= 0).ToArray();
                o.Grid = true; o.Automated = false;
                o.GridAi = Ints(GridAi).Where(n => n > 0 && n <= 400).ToArray(); o.GridTargets = Ints(GridTargets); o.GridWorkers = Ints(GridWorkers);
                o.GridCores = GridCores.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
                o.RefEvery = RefEvery; o.GridDefault = GridDefault;
                if (o.GridAi.Length > 0) o.Ai = o.AiStart = o.GridAi.Min();
            }
            // the cores whose CPUs are all in UseCpus (none of them: every core)
            int[] use = CpuList.Parse(UseCpus);
            if (use.Length > 0)
            {
                var cores = cpu.Cores.Where(c => c.Cpus.All(use.Contains)).Select(c => c.Index).ToArray();
                o.UseCores = cores.Length > 0 ? cores : null;
            }
            return o;
        }

        // the settings in short, for the log
        public string Text() => string.Format(Inv, "StepSeconds={0};Ai={1};WarmupSeconds={2};Alternate={3};MaxCores={4};MaxWorkers={5};UseCpus={6};Targets={7}+{8}x{9};SparePct={10};AiStep={11};AiMax={12};"
                                              + "VisualSkips={13};PinUnderQuota={14};CoreBench={15};MinEmptySeconds={16};Parts={17}",
            StepSeconds, IsGrid ? "grid" : Ai.ToString(Inv), WarmupSeconds, Alternate ? 1 : 0, MaxCores, MaxWorkers, UseCpus, TargetStart, TargetStep, TargetSteps, SparePct, AiStep, AiMax,
            VisualSkips ? 1 : 0, PinUnderQuota ? 1 : 0, CoreBench ? 1 : 0, MinEmptySeconds, Parts) + (AiOnly ? ";Pair=" + Pair + ";Target=" + Target : "")
            + (IsGrid ? ";GridAi=" + GridAi + ";GridTargets=" + GridTargets + ";GridWorkers=" + GridWorkers + ";GridCores=" + GridCores + ";RefEvery=" + RefEvery
                        + ";GridDefault=" + (GridDefault ? 1 : 0) : "");
    }

    // The results folder, in the Server Tester's own format (src/ServerTester/Engine/Analysis.cs builds its contents):
    // results.json, measurements.csv, report.txt, plus progress.json while it runs and selftest.log. The tester's "Open
    // results" reads it like one of its own, given the CPU it ran on is in results.json.
    internal static class SelfTestFiles
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        internal const string Runner = Results.PluginRunner;

        // results.json, measurements.csv and report.txt; extras go into results.json after the tester's own parts
        // (test.clock, runner, host)
        internal static void Write(string folder, SelfTestRun run, Results r, string version, string server, string os, IDictionary<string, object> host)
        {
            Directory.CreateDirectory(folder);
            r.Folder = folder;
            r.Runner = Runner;
            var tree = r.JsonTree(run, version, server, os);
            if (tree["test"] is Dictionary<string, object> test) test["clock"] = "utc";   // window_time and the date are UTC
            tree["runner"] = Runner;
            if (host != null) tree["host"] = host;
            WriteAtomic(Path.Combine(folder, "results.json"), Json(tree));
            WriteAtomic(Path.Combine(folder, "measurements.csv"), Results.CsvText(run));
            WriteAtomic(Path.Combine(folder, "report.txt"), r.Report(run));
        }

        // written under another name and renamed, so a reader never sees half a file
        internal static void WriteAtomic(string path, string text)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        internal static void Log(string folder, string line)
        {
            try { if (folder != null) File.AppendAllText(Path.Combine(folder, "selftest.log"), line + "\n", new UTF8Encoding(false)); } catch { }
        }

        // ---------------------------------------------------------------- JSON, written by hand (no JSON library in the game)
        // Dictionaries keep their order; NaN and infinity are null, as the tester writes them.
        internal static string Json(object v)
        {
            var sb = new StringBuilder(64 * 1024);
            Write(sb, v, 0);
            sb.Append('\n');
            return sb.ToString();
        }

        static void Indent(StringBuilder sb, int n) { sb.Append('\n'); sb.Append(' ', n * 2); }

        static void Write(StringBuilder sb, object v, int depth)
        {
            switch (v)
            {
                case null: sb.Append("null"); return;
                case bool b: sb.Append(b ? "true" : "false"); return;
                case string s: Str(sb, s); return;
                case int i: sb.Append(i.ToString(Inv)); return;
                case long l: sb.Append(l.ToString(Inv)); return;
                case double d: sb.Append(double.IsNaN(d) || double.IsInfinity(d) ? "null" : d.ToString("R", Inv)); return;
                case float f: sb.Append(float.IsNaN(f) || float.IsInfinity(f) ? "null" : ((double)f).ToString("R", Inv)); return;
                case IDictionary dict:
                {
                    if (dict.Count == 0) { sb.Append("{}"); return; }
                    sb.Append('{');
                    bool first = true;
                    foreach (DictionaryEntry e in dict)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        Indent(sb, depth + 1);
                        Str(sb, Convert.ToString(e.Key, Inv));
                        sb.Append(": ");
                        Write(sb, e.Value, depth + 1);
                    }
                    Indent(sb, depth);
                    sb.Append('}');
                    return;
                }
                case IEnumerable list:
                {
                    var items = list.Cast<object>().ToList();
                    if (items.Count == 0) { sb.Append("[]"); return; }
                    sb.Append('[');
                    for (int k = 0; k < items.Count; k++)
                    {
                        if (k > 0) sb.Append(',');
                        Indent(sb, depth + 1);
                        Write(sb, items[k], depth + 1);
                    }
                    Indent(sb, depth);
                    sb.Append(']');
                    return;
                }
                default:
                    if (v is IFormattable fm) { sb.Append(fm.ToString(null, Inv)); return; }
                    Str(sb, v.ToString());
                    return;
            }
        }

        internal static void Str(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", Inv));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
