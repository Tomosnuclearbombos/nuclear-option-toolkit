using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using ServerTester.Engine;

namespace ServerTester
{
    // What the graph shows along the bottom. One measure at a time.
    public sealed class Metric
    {
        public string Name, Unit;
        public Func<Step, Measurement, double> OfWindow;
        public Func<Step, double> OfStep;
        public bool LowerIsBetter;
        public bool AgainstBaseline;   // only the core and worker tests have a baseline to set a step against
        public bool FrameRate;         // a frame rate, to set against each frame target
        public override string ToString() => Name;

        public static readonly Metric[] All =
        {
            new Metric { Name = "Busy time per frame", Unit = "ms", OfWindow = (s, w) => w.MsPerFrame, OfStep = s => s.MsPerFrame, LowerIsBetter = true },
            new Metric { Name = "Busy time against baseline", Unit = "%", OfWindow = (s, w) => s.BaseRef > 0 ? (w.MsPerFrame / s.BaseRef - 1) * 100 : double.NaN,
                         OfStep = s => s.VsBaseline, LowerIsBetter = true, AgainstBaseline = true },
            new Metric { Name = "Frames per second", Unit = "fps", OfWindow = (s, w) => w.Fps, OfStep = s => s.Fps, FrameRate = true },
            new Metric { Name = "5% low frame rate", Unit = "fps", OfWindow = (s, w) => w.Low5Fps, OfStep = s => s.Low5, FrameRate = true },
            new Metric { Name = "1% low frame rate", Unit = "fps", OfWindow = (s, w) => w.Low1Fps, OfStep = s => s.Low1, FrameRate = true },
            new Metric { Name = "Longest frame", Unit = "ms", OfWindow = (s, w) => w.FtMax, OfStep = s => s.TypicalLongest, LowerIsBetter = true },
            new Metric { Name = "Physics time", Unit = "ms/s", OfWindow = (s, w) => w.Physics, OfStep = s => s.Physics, LowerIsBetter = true },
            new Metric { Name = "Spare time", Unit = "ms/s", OfWindow = (s, w) => w.Spare, OfStep = s => s.Spare },
            new Metric { Name = "Server CPU use", Unit = "%", OfWindow = (s, w) => w.CpuPct, OfStep = s => s.CpuPct, LowerIsBetter = true },
        };
        public static Metric Busy => All[0];
        public static Metric VsBaseline => All[1];
        public static Metric Fps => All[2];
        public static Metric Low5 => All[3];
        public static Metric Low1 => All[4];
    }

    // One test on the graph: the one running now, or one opened from its results folder.
    public sealed class RunData
    {
        public string Folder;
        public List<Step> Steps = new List<Step>();
        public Results Results;
        public TestOptions Options;   // what it ran with
        public string Cpu = "";       // the PC it ran on
        public DateTime Date;
        public bool Live, Shown = true;
    }

    // The graph colours: eight colours that stay apart for colour-blind eyes too (a lighter and a darker set for the
    // light and dark themes), more after them, a grey of their own for the baseline, quiet greys for everything else.
    // Text never takes a mark's colour.
    public static class Palette
    {
        public const int MaxRuns = 8;
        static readonly string[] Light = { "#2a78d6", "#eb6834", "#1baf7a", "#eda100", "#e87ba4", "#008300", "#6250d6", "#e34948" };
        static readonly string[] Dark = { "#3987e5", "#d95926", "#199e70", "#c98500", "#d55181", "#008300", "#9085e9", "#e66767" };
        public static bool IsDark => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
        static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));

        // a setting's colour: the eight first, then more (a test can have up to 20 settings), each its own hue
        public static IBrush Setting(int index)
        {
            if (index < MaxRuns) return B((IsDark ? Dark : Light)[Math.Max(0, index)]);
            double hue = (index - MaxRuns) * 137.508 % 360;   // the golden angle keeps each new hue away from the others
            return new SolidColorBrush(new HslColor(1, hue, 0.55, IsDark ? 0.62 : 0.42).ToRgb());
        }
        public static IBrush Baseline => B(IsDark ? "#8f8d86" : "#85837d");
        public static IBrush Surface => B(IsDark ? "#1a1a19" : "#fcfcfb");
        public static IBrush Text => B(IsDark ? "#f0efec" : "#0b0b0b");
        public static IBrush Text2 => B(IsDark ? "#c3c2b7" : "#52514e");
        public static IBrush Grid => B(IsDark ? "#2e2e2b" : "#e4e3df");
        public static IBrush Band => B(IsDark ? "#222220" : "#f2f2ef");
        public static IBrush TipBack => B(IsDark ? "#2b2b29" : "#ffffff");

        // the program's own font (Inter, built in), so the graph looks the same on every system
        public static FormattedText Fmt(string s, double size, IBrush brush, FontWeight weight = FontWeight.Normal) =>
            new FormattedText(s ?? "", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(FontManager.Current.DefaultFontFamily, FontStyle.Normal, weight), size, brush);
    }

    // The names of the tests, their settings, and what tells one test apart from another.
    public static class Settings
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public static readonly string[] Kinds = { "baseline", "pair", "retest", "cores", "workers", "target", "ai" };
        public static readonly string[] KindNames = { "Baseline", "Cores and workers", "Best 3 again (2 min)", "Core count", "Physics workers", "Frame target", "AI load" };
        // the graphs whose settings are pairs of a core count and a worker count ("4x3")
        public static bool PairLike(string kind) => kind == "pair" || kind == "retest";

        public static string What(string kind) => kind == "baseline" ? "baseline" : kind == "pair" ? "cores and workers" : kind == "retest" ? "best 3 again" : kind == "cores" ? "core count"
            : kind == "workers" ? "physics workers" : kind == "target" ? "frame target" : "AI load";

        public static string Long(string kind, string v) => kind == "baseline" ? "baseline"
            : PairLike(kind) ? Long("cores", TestPlan.PairCores(v)) + ", " + Long("workers", TestPlan.PairWorkers(v).ToString(Inv))
            : kind == "cores" ? (v == "all" ? "every core" : v == "every" ? "every core (not pinned)" : v + (v == "1" ? " core" : " cores"))
            : kind == "workers" ? (v == "0" ? "Server Default" : v + (v == "1" ? " worker" : " workers")) : kind == "target" ? v + " fps" : v + " AI aircraft";

        // a test's baseline cores and workers as it saved them: 0 = every core and the game's own workers, as out of the box
        public static int BaseCoresOf(RunData r) => r.Options == null ? 0 : r.Live ? TestPlan.BaseCores(r.Options) : Math.Max(0, r.Options.BaselineCores);
        public static int BaseWorkersOf(RunData r) => r.Options == null ? 0 : r.Live ? TestPlan.BaseWorkers(r.Options) : Math.Max(0, r.Options.BaselineWorkers);
        public static string BaseText(RunData r) => BaseCoresOf(r) <= 0 && BaseWorkersOf(r) <= 0 ? "Server Default cores and workers"
            : TestPlan.CoresText(BaseCoresOf(r)) + ", " + TestPlan.WorkersText(BaseWorkersOf(r));

        // an automated test's frame target and AI load rounds ran on its pick (", on 3 cores, 3 workers"); empty otherwise
        public static string RanOn(RunData r, string kind) =>
            (kind == "target" || kind == "ai") && r.Options?.Automated == true && r.Results?.PairPick != null ? ", on " + TestPlan.PairText(r.Options, r.Results.PairPick) : "";

        // a core count's name for this test: "all" is every core only when every core was ticked
        public static string CoreName(TestOptions o, string v) =>
            v == "all" && o != null && !TestPlan.UsesEveryCore(o) ? "all " + TestPlan.Usable(o).Count + " cores ticked" : Long("cores", v);
        public static string Name(TestOptions o, string kind, string v) =>
            kind == "cores" ? CoreName(o, v) : PairLike(kind) ? CoreName(o, TestPlan.PairCores(v)) + ", " + Long("workers", TestPlan.PairWorkers(v).ToString(Inv)) : Long(kind, v);

        // the settings of one test that any of these tests measured, in order: 1, 2, 3 ... then "all"
        // (a pair: by its core count, then its workers; the re-test's own baseline isn't one of its settings)
        public static List<string> Keys(IEnumerable<RunData> runs, string kind) =>
            runs.SelectMany(r => Results.Groups(r.Steps.Where(s => kind != "retest" || !s.Baseline), kind).Select(x => x.Key)).Distinct()
                .OrderBy(k => Order(PairLike(kind) ? TestPlan.PairCores(k) : k)).ThenBy(k => PairLike(kind) ? TestPlan.PairWorkers(k) : 0).ToList();
        static int Order(string k) => int.TryParse(k, NumberStyles.Integer, Inv, out int n) ? n : k == "all" ? int.MaxValue - 1 : int.MaxValue;

        // "AMD Ryzen 7 9850X3D 8-Core Processor" -> "Ryzen 7 9850X3D"
        public static string ShortCpu(string model)
        {
            if (string.IsNullOrWhiteSpace(model)) return "";
            string s = Regex.Replace(model, @"\((R|TM)\)|\b(AMD|Intel|Processor|CPU)\b|\d+-Core|\d+(st|nd|rd|th) Gen|with Radeon.*$|@.*$", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s.Length > 0 ? s : model.Trim();
        }

        public static string When(RunData r) => r.Live ? "running now" : r.Date != default ? r.Date.ToLocalTime().ToString("d MMM HH:mm", Inv)
                                                : string.IsNullOrEmpty(r.Folder) ? "" : System.IO.Path.GetFileName(r.Folder);

        static string MissionName(TestOptions o) => o == null ? "" : o.Mission == TestPlan.RealisticBenchmark ? "Realistic Benchmark"
                                                    : o.Mission == TestPlan.StressTest ? "Stress Test" : "mission not saved";

        // the settings a test ran with: the mission and the AI load first, then the runs
        public static string SettingsOf(TestOptions o) => o == null ? "" : MissionName(o) + ", " + (o.OnlyAiLoad ? AiRange(o) : o.Ai + " AI")
                                                             + (o.Repeats > 1 ? ", " + o.Repeats + " runs" : ", 1 run");

        // the AI loads an AI load test asked for ("80 to 120 AI")
        public static string AiRange(TestOptions o)
        {
            var loads = o.AiLoads();
            return loads.Length > 1 ? loads[0] + " to " + loads[loads.Length - 1] + " AI" : o.AiFrom + " AI";
        }

        // what a test was, for its chip: what it tested, then its settings ("Physics workers test: Stress Test, 120 AI, 3 runs");
        // a test the NukeStats plugin ran on a server itself says so ("Automated test on the server: ...")
        public static string Title(RunData r)
        {
            var o = r.Options;
            if (o == null) return When(r);
            string where = r.Results?.Runner == Results.PluginRunner ? " on the server" : "";
            if (o.Automated) return "Automated test" + where + ": " + SettingsOf(o);
            var kinds = new List<string>();
            if (o.TestBaseline && !o.TestCores && !o.TestWorkers) kinds.Add("baseline");   // the core and worker tests measure one too
            if (o.TestCores) kinds.Add("core count");
            if (o.TestWorkers) kinds.Add("physics workers");
            if (o.TestTarget) kinds.Add("frame target");
            if (o.TestAiLoad) kinds.Add("AI load");
            if (kinds.Count == 0) return "Nothing measured: " + SettingsOf(o);
            string k = kinds.Count == 1 ? kinds[0] : string.Join(", ", kinds.Take(kinds.Count - 1)) + " and " + kinds[kinds.Count - 1];
            return char.ToUpper(k[0]) + k.Substring(1) + " test" + where + ": " + SettingsOf(o);
        }

        public static string Detail(RunData r) => (r.Cpu.Length > 0 ? ShortCpu(r.Cpu) + ", " : "") + When(r);

        // What each compared test was: its rows say only what differs between the tests (so nothing gets cut off), and
        // "shared" what is the same for all of them, for the line under the title. A test told apart by nothing else
        // gets its date and time.
        public static Dictionary<RunData, string> Tags(List<RunData> tests, string kind, out string shared)
        {
            var tags = tests.ToDictionary(t => t, t => "");
            shared = "";
            if (tests.Count < 2) return tags;
            string Order(TestOptions o) => (kind == "target" ? o.TargetAlternate : o.Alternate) ? "alternating" : "one after another";
            bool main = kind == "baseline" || kind == "cores" || kind == "workers" || PairLike(kind);   // one baseline: its cores and workers both
            // (each part, and whether it is worth saying when every test has it)
            var parts = new List<(Func<RunData, string> of, bool sayShared)>
            {
                (t => MissionName(t.Options), true),
                (t => t.Options == null ? "" : kind == "ai" ? AiRange(t.Options) : t.Options.Ai + " AI", true),
                (t => t.Options == null ? "" : t.Options.Repeats + (t.Options.Repeats == 1 ? " run" : " runs"), true),
                (t => t.Options == null || kind == "ai" || kind == "baseline" ? "" : Order(t.Options), true),
                (t => t.Options == null || !main ? "" : "baseline " + BaseText(t), true),
                (t => t.Options == null || kind != "target" || !t.Options.TargetAlternate ? "" : "baseline " + t.Options.BaselineTarget + " fps", true),
                (t => t.Options == null || kind != "target" ? "" : "held on " + TestPlan.HeldName(t.Options.HeldBy), true),
                (t => RanOn(t, kind).TrimStart(',', ' '), true),
                (t => t.Options == null || kind == "target" ? "" : "at " + (kind == "ai" ? t.Steps.Where(s => s.Kind == "ai").Select(s => s.Target).FirstOrDefault() is int at && at > 0 ? at : TestPlan.Compare(t.Options)
                                                                          : TestPlan.Compare(t.Options)) + " fps", false),
                (t => (t.Results?.RanOnWindows ?? Native.IsWindows) ? "Windows" : "Linux", false),
                (t => ShortCpu(t.Cpu), true),
            };
            var same = new List<string>();
            foreach (var (of, sayShared) in parts)
            {
                var values = tests.Select(of).ToList();
                if (values.Distinct().Count() < 2) { if (sayShared && values[0].Length > 0) same.Add(values[0]); continue; }
                for (int i = 0; i < tests.Count; i++)
                    if (values[i].Length > 0) tags[tests[i]] += (tags[tests[i]].Length > 0 ? ", " : "") + values[i];
            }
            foreach (var g in tests.GroupBy(t => tags[t]).Where(g => g.Count() > 1 || g.Key.Length == 0).ToList())
                foreach (var t in g) tags[t] += (tags[t].Length > 0 ? ", " : "") + When(t);
            shared = string.Join(", ", same);
            return tags;
        }
    }

    // ---------------------------------------------------------------- the comparison: a whisker chart laid out like a table
    // One test, one measure. The baseline comes first, then its settings run down the side, a row each (and a row for each
    // test being compared, named by what is different about it). The measure runs along the bottom: the dot is the
    // average of the setting's steps (what the tester judges by), the line runs from its lowest 15 second measurement to
    // its highest, and the average is written at the end of the row like a table cell. Each setting has its own colour
    // and the baseline its grey. Tests whose baselines ran the same share one baseline row, the average of them all;
    // when they differ, BaselineKey says which one is the baseline.
    public sealed class CompareChart : Control
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public Metric Metric = Metric.VsBaseline;
        public List<RunData> Runs = new List<RunData>();
        public string Kind = "cores";
        public HashSet<string> Hidden = new HashSet<string>();    // the settings left unticked
        public string BaselineKey;   // the baseline picked when the tests' baselines differ (none picked: the first)
        Point? _hover;
        const double RowH = 20, GroupGap = 6, Bottom = 54;
        string _subtitle = "";   // under the title: one test's settings, or what the compared tests share
        double Top => _subtitle.Length > 0 ? 46 : 30;

        public CompareChart()
        {
            ClipToBounds = true;
            if (Application.Current != null) Application.Current.ActualThemeVariantChanged += (_, __) => InvalidateVisual();
        }

        protected override void OnPointerMoved(PointerEventArgs e) { _hover = e.GetPosition(this); InvalidateVisual(); base.OnPointerMoved(e); }
        protected override void OnPointerExited(PointerEventArgs e) { _hover = null; InvalidateVisual(); base.OnPointerExited(e); }
        internal void SetHover(Point? p) { _hover = p; InvalidateVisual(); }   // for the screenshot check

        sealed class Row
        {
            public string Key, Label, Sub = ""; public RunData Run; public BaseGroup Base; public Stat St; public bool Baseline, Pick, Hand, FirstOfGroup;
            public int Group, Steps; public string Tag = "";
            public double Y;
        }

        // A baseline the tests on the graph ran: the tests that ran it the same way (same PC, system, mission, AI and
        // baseline setting) and all their baseline steps.
        public sealed class BaseGroup { public string Key, What = "", Label = ""; public List<RunData> Runs = new List<RunData>(); public List<Step> Steps = new List<Step>(); }

        // a test's baseline steps for this graph: the core count, physics workers and baseline graphs share the one
        // baseline (the baseline test and the core and worker tests' baseline steps); a frame target test has its own
        // when it alternated; the AI load has none
        public static List<Step> BaselineSteps(RunData r, string kind)
        {
            if (kind == "target") return r.Steps.Where(s => s.Kind == "target" && s.Ok && IsBaseline(s, r.Steps)).ToList();
            if (kind == "retest") return r.Steps.Where(s => s.Kind == "retest" && s.Baseline && s.Ok).ToList();   // its own 2 minute baselines
            if (kind == "ai" || kind == "pair" && !r.Steps.Any(s => s.Kind == "pair")
                || kind != "baseline" && !r.Steps.Any(s => s.Kind == kind || s.Kind == "baseline")) return new List<Step>();
            return r.Steps.Where(s => s.Ok && Results.IsMainBaseline(s)).ToList();
        }

        public static List<BaseGroup> BaselineGroups(IEnumerable<RunData> shown, string kind)
        {
            var groups = new List<BaseGroup>();
            foreach (var r in shown)
            {
                var steps = BaselineSteps(r, kind);
                if (steps.Count == 0) continue;
                var o = r.Options;
                string key = string.Join("|", r.Cpu ?? "", (r.Results?.RanOnWindows ?? Native.IsWindows) ? "Windows" : "Linux", o?.Mission ?? "", o?.Ai ?? 0,
                                         // (an automated test's frame target round ran on its pick, not on the baseline's settings)
                                         kind == "target" && o?.Automated == true ? "pick " + (r.Results?.PairPick ?? "running") : Settings.BaseCoresOf(r) + "|" + Settings.BaseWorkersOf(r),
                                         steps[0].Target);
                var g = groups.FirstOrDefault(x => x.Key == key);
                if (g == null) groups.Add(g = new BaseGroup { Key = key });
                g.Runs.Add(r);
                g.Steps.AddRange(steps);
            }
            // each one named by what is different about it
            if (groups.Count > 1)
            {
                var tags = Settings.Tags(groups.Select(g => g.Runs[0]).ToList(), kind, out _);
                foreach (var g in groups)
                {
                    g.What = tags[g.Runs[0]];
                    g.Label = g.What + (g.Runs.Count > 1 ? " (" + g.Runs.Count + " tests)" : "");
                }
            }
            return groups;
        }

        // busy time against a baseline its steps can't be paired with: against that baseline's average
        static Metric Against(Metric m, double refMs) => new Metric
        {
            Name = m.Name, Unit = m.Unit, LowerIsBetter = m.LowerIsBetter, AgainstBaseline = true,
            OfWindow = (s, w) => refMs > 0 ? (w.MsPerFrame / refMs - 1) * 100 : double.NaN,
            OfStep = s => refMs > 0 && !double.IsNaN(s.MsPerFrame) ? (s.MsPerFrame / refMs - 1) * 100 : double.NaN,
        };

        // the rows: the baseline first, then a setting's tests one under another, the settings in order
        List<Row> Rows(out List<string> all)
        {
            var shown = Runs.Where(r => r.Shown).ToList();
            all = Kind == "baseline" ? new List<string>() : Settings.Keys(shown, Kind);
            // (a pair is hidden by its core count or its workers)
            var keys = all.Where(k => Settings.PairLike(Kind) ? !Hidden.Contains("c:" + TestPlan.PairCores(k)) && !Hidden.Contains("w:" + TestPlan.PairWorkers(k))
                                                     : !Hidden.Contains(k)).ToList();
            var groups = BaselineGroups(shown, Kind);
            var chosen = groups.FirstOrDefault(g => g.Key == BaselineKey) ?? groups.FirstOrDefault();
            var sharing = chosen?.Runs ?? new List<RunData>();
            var inBase = new HashSet<Step>(chosen?.Steps ?? new List<Step>());
            // a test that didn't run the same baseline is set against that baseline's average
            double refMs = chosen != null ? Results.Mean(chosen.Steps, s => s.MsPerFrame) : double.NaN;
            bool against = Metric.AgainstBaseline && Kind != "target" && Kind != "ai";
            Metric For(RunData r) => against && !sharing.Contains(r) ? Against(Metric, refMs) : Metric;
            int Measured(IEnumerable<Step> g, Metric m) => g.Count(s => s.Windows.Any(w => !double.IsNaN(m.OfWindow(s, w))));
            var rows = new List<Row>();
            int group = 0;

            if (chosen != null)
            {
                int bc = Settings.BaseCoresOf(chosen.Runs[0]);
                string key = Kind == "baseline" ? "base" : Kind == "cores" ? chosen.Steps.FirstOrDefault(s => s.Kind == "cores")?.Value ?? (bc > 0 ? bc.ToString(Inv) : "all")
                           : Kind == "workers" ? Settings.BaseWorkersOf(chosen.Runs[0]).ToString(Inv) : chosen.Steps[0].Value;
                var st = Stat.Of(chosen.Steps, Metric);
                if (!Hidden.Contains(key) && st.N > 0)
                    rows.Add(new Row { Key = key, Label = "baseline", Run = chosen.Runs[0], Base = chosen,
                                       St = st, Baseline = true, Pick = chosen.Runs.Any(r => IsPick(r, key)), FirstOfGroup = true, Group = group++, Steps = Measured(chosen.Steps, Metric) });
            }
            if (Kind == "baseline")
            {
                group = Math.Max(group, 1);   // their colours count from the first, whether or not the baseline picked had this measure
                // the other baselines, each the average of the tests that ran it the same
                foreach (var g in groups.Where(g => g != chosen))
                {
                    var m = For(g.Runs[0]);
                    var st = Stat.Of(g.Steps, m);
                    if (st.N > 0)
                        rows.Add(new Row { Key = "base", Label = "baseline", Run = g.Runs[0], Base = g, St = st, FirstOfGroup = true, Group = group++, Steps = Measured(g.Steps, m) });
                }
            }
            else if (Settings.PairLike(Kind))
                // a group for each core count, a row for each of its worker counts (and each test)
                foreach (var cores in keys.GroupBy(TestPlan.PairCores))
                {
                    bool first = true;
                    foreach (var k in cores)
                        foreach (var r in shown)
                        {
                            var g = Results.Groups(r.Steps, Kind).FirstOrDefault(x => x.Key == k).Value;
                            if (g == null) continue;
                            var m = For(r);
                            var st = Stat.Of(g, m);
                            if (st.N == 0) continue;
                            rows.Add(new Row { Key = k, Label = Settings.CoreName(r.Options, cores.Key), Sub = Settings.Long("workers", TestPlan.PairWorkers(k).ToString(Inv)), Run = r,
                                               St = st, Pick = IsPick(r, k), Hand = IsHand(r, k), FirstOfGroup = first, Group = group, Steps = Measured(g, m) });
                            first = false;
                        }
                    if (!first) group++;
                }
            else
                foreach (var k in keys)
                {
                    bool first = true;
                    foreach (var r in shown)
                    {
                        var g = Results.Groups(r.Steps, Kind).FirstOrDefault(x => x.Key == k).Value;
                        if (g == null) continue;
                        g = g.Where(s => !inBase.Contains(s)).ToList();   // the baseline's steps are in its row at the top
                        if (g.Count == 0) continue;
                        var m = For(r);
                        var st = Stat.Of(g, m);
                        if (st.N == 0) continue;
                        rows.Add(new Row { Key = k, Label = Settings.Name(r.Options, Kind, k), Run = r, St = st, Pick = IsPick(r, k), Hand = IsHand(r, k), FirstOfGroup = first, Group = group, Steps = Measured(g, m) });
                        first = false;
                    }
                    if (!first) group++;
                }

            // the tests that have rows: one says its settings under the title; several share a line there and each row
            // says what is different about its own test (a baseline several tests share says so)
            var tests = rows.SelectMany(r => r.Base?.Runs ?? new List<RunData> { r.Run }).Distinct().ToList();
            var tags = Settings.Tags(tests, Kind, out string shared);
            foreach (var r in rows)
                r.Tag = r.Base != null && r.Base.Runs.Count > 1 ? "average of " + r.Base.Runs.Count + " tests" + (groups.Count > 1 ? ", " + r.Base.What : "")
                      : tags.TryGetValue(r.Run, out var t) ? t : "";
            _subtitle = tests.Count == 1 ? Settings.SettingsOf(tests[0].Options) + Settings.RanOn(tests[0], Kind) + ", " + Settings.Detail(tests[0])
                      : tests.Count > 1 && shared.Length > 0 ? "Same in all: " + shared : "";
            return rows;
        }

        // the graph is as tall as its rows
        protected override Size MeasureOverride(Size available)
        {
            var rows = Rows(out _);
            int groups = rows.Select(r => r.Group).Distinct().Count();
            double h = rows.Count == 0 ? 220 : Top + rows.Count * RowH + Math.Max(0, groups - 1) * GroupGap + Bottom;
            return new Size(double.IsInfinity(available.Width) ? 500 : available.Width, h);
        }

        public void Refresh() { InvalidateMeasure(); InvalidateVisual(); }

        public override void Render(DrawingContext g)
        {
            var b = Bounds;
            g.FillRectangle(Palette.Surface, new Rect(0, 0, b.Width, b.Height));
            string what = Settings.What(Kind);
            var rows = Rows(out var all);
            g.DrawText(Palette.Fmt(char.ToUpper(what[0]) + what.Substring(1) + " test: " + Metric.Name.ToLowerInvariant() + " (" + Metric.Unit + "), "
                                   + (Metric.LowerIsBetter ? "lower is better" : "higher is better"), 12, Palette.Text, FontWeight.SemiBold), new Point(10, 6));
            if (_subtitle.Length > 0)
            {
                var sub = Palette.Fmt(_subtitle, 10, Palette.Text2);
                sub.MaxTextWidth = Math.Max(50, b.Width - 20);
                sub.MaxLineCount = 1;
                sub.Trimming = TextTrimming.CharacterEllipsis;
                g.DrawText(sub, new Point(10, 24));
            }
            if (rows.Count == 0)
            {
                bool anyKind = Runs.Any(r => r.Shown && (Kind == "baseline" ? BaselineSteps(r, Kind).Count > 0 : r.Steps.Any(s => s.Kind == Kind && s.Ok)));
                Empty(g, Runs.Count == 0 ? "The graph fills in as each step is measured."
                      : !Runs.Any(r => r.Shown) ? "Tick a test above to see it."
                      : !anyKind && Runs.Any(r => r.Shown && r.Live && r.Options != null && Measures(r.Options)) ? "The graph fills in as each step is measured."
                      : !anyKind ? (Kind == "baseline" ? "No baseline in the tests ticked: the baseline, core count and physics worker tests measure one." : "No " + what + " results in the tests ticked.")
                      : all.Count > 0 && all.All(k => Hidden.Contains(k)) ? "Tick a setting above to see it."
                      : Metric.AgainstBaseline && (Kind == "target" || Kind == "ai") ? "Busy time against baseline is only for the baseline, core count and physics worker tests. Pick another measure."
                      : Metric.AgainstBaseline ? "No baseline measured yet. Pick busy time per frame to see the steps so far."
                      : "No " + Metric.Name.ToLowerInvariant() + " in these results: older tests didn't measure it.");
                return;
            }

            // the columns: the settings' names, the graph, the averages (and what each compared test was)
            string Value(double v) => Metric.AgainstBaseline ? v.ToString("+0.0;-0.0;0.0", Inv) + "%" : Num(v) + " " + Metric.Unit;
            double subW = rows.Select(r => r.Sub.Length > 0 ? Palette.Fmt(r.Sub, 10, Palette.Text2).Width : 0).DefaultIfEmpty(0).Max();
            double left = 12 + rows.Where(r => r.FirstOfGroup).Max(r => Palette.Fmt(r.Label, 11, Palette.Text).Width) + (subW > 0 ? 10 + subW : 0) + 12;
            // measured the way it is drawn (bold average and "suggested", the tag in small type)
            double right = rows.Max(r => 14 + Palette.Fmt(Value(r.St.Mean), 11, Palette.Text, FontWeight.SemiBold).Width
                                         + (r.Pick ? 8 + Palette.Fmt("suggested", 11, Palette.Text, FontWeight.SemiBold).Width : 0)
                                         + (r.Hand ? 8 + Palette.Fmt("picked by you", 11, Palette.Text, FontWeight.SemiBold).Width : 0)
                                         + (r.Tag.Length > 0 ? 8 + Palette.Fmt(r.Tag, 10, Palette.Text2).Width : 0)) + 6;
            right = Math.Min(right, b.Width * 0.5);
            var plot = new Rect(left, Top, Math.Max(80, b.Width - left - right), rows.Count * RowH + Math.Max(0, rows.Count(r => r.FirstOfGroup) - 1) * GroupGap);

            // the range along the bottom: every measurement, zero for the baseline measure, the targets for a frame rate
            double min = rows.Min(r => r.St.Lo), max = rows.Max(r => r.St.Hi);
            bool targets = Kind == "target" && Metric.FrameRate;
            if (Metric.AgainstBaseline) { min = Math.Min(min, 0); max = Math.Max(max, 0); }
            if (targets)
                foreach (var r in rows) if (double.TryParse(r.Key, NumberStyles.Float, Inv, out double t)) { min = Math.Min(min, t); max = Math.Max(max, t); }
            double pad = (max - min) * 0.06;
            min -= pad; max += pad;
            double step = Nice(ref min, ref max);
            double X(double v) => plot.Left + (v - min) / (max - min) * plot.Width;

            // the rows' places, a light band behind every other setting
            double y = plot.Top;
            int group = -1;
            foreach (var r in rows)
            {
                if (r.FirstOfGroup) { group++; if (group > 0) y += GroupGap; }
                r.Y = y + RowH / 2;
                if (group % 2 == 1) g.FillRectangle(Palette.Band, new Rect(0, y - (r.FirstOfGroup ? GroupGap / 2 : 0), b.Width, RowH + (r.FirstOfGroup ? GroupGap / 2 : 0)));
                y += RowH;
            }

            // the grid and the numbers along the bottom
            var grid = new Pen(Palette.Grid, 1);
            int ticks = (int)Math.Round((max - min) / step);
            for (int k = 0; k <= ticks; k++)
            {
                double v = min + step * k, x = X(v);
                g.DrawLine(grid, new Point(x, plot.Top), new Point(x, plot.Bottom));
                var t = Palette.Fmt(Tick(v, step), 10, Palette.Text2);
                g.DrawText(t, new Point(x - t.Width / 2, plot.Bottom + 4));
            }
            if (min < 0 && max > 0) g.DrawLine(new Pen(Palette.Text2, 1), new Point(X(0), plot.Top), new Point(X(0), plot.Bottom));
            g.DrawLine(new Pen(Palette.Text2, 1), new Point(plot.Left, plot.Bottom), new Point(plot.Right, plot.Bottom));
            var axis = Palette.Fmt(Metric.Name.ToLowerInvariant() + " (" + Metric.Unit + ")", 10, Palette.Text2);
            g.DrawText(axis, new Point(plot.Left + (plot.Width - axis.Width) / 2, plot.Bottom + 19));
            g.DrawText(Palette.Fmt("dot = average of its steps, line = lowest to highest 15 second measurement" + (targets ? ", dashes = target" : ""), 10, Palette.Text2),
                       new Point(10, plot.Bottom + 35));

            // the rows
            Row near = null;
            double best = double.MaxValue;
            foreach (var r in rows)
            {
                if (r.FirstOfGroup)
                {
                    var name = Palette.Fmt(r.Label, 11, Palette.Text);
                    int n = rows.Count(x => x.Group == r.Group);
                    double gy = r.Y + (n - 1) * RowH / 2;
                    bool isBase = r.Baseline && r.Label != "baseline";
                    double nameRight = left - 12 - (subW > 0 ? subW + 10 : 0);
                    g.DrawText(name, new Point(nameRight - name.Width, gy - name.Height / 2 - (isBase ? 4 : 0)));
                    if (isBase)
                    {
                        var bl = Palette.Fmt("baseline", 9, Palette.Text2);
                        g.DrawText(bl, new Point(nameRight - bl.Width, gy + name.Height / 2 - 5));
                    }
                }
                if (r.Sub.Length > 0)
                {
                    var sub = Palette.Fmt(r.Sub, 10, Palette.Text2);
                    g.DrawText(sub, new Point(left - 12 - sub.Width, r.Y - sub.Height / 2));
                }
                var brush = r.Baseline ? Palette.Baseline : Palette.Setting(Math.Max(0, Kind == "baseline" ? r.Group - 1 : Settings.PairLike(Kind) ? TestPlan.PairWorkers(r.Key) - 1 : all.IndexOf(r.Key)));
                var pen = new Pen(brush, 2);
                double x0 = X(r.St.Lo), x1 = X(r.St.Hi);
                if (x1 - x0 > 0.5)
                {
                    g.DrawLine(pen, new Point(x0, r.Y), new Point(x1, r.Y));
                    g.DrawLine(pen, new Point(x0, r.Y - 4), new Point(x0, r.Y + 4));
                    g.DrawLine(pen, new Point(x1, r.Y - 4), new Point(x1, r.Y + 4));
                }
                var c = new Point(X(r.St.Mean), r.Y);
                g.DrawEllipse(Palette.Surface, null, c, 6, 6);
                g.DrawEllipse(brush, null, c, 4.5, 4.5);
                // the target, over the mark so it shows when the mark sits on it
                if (targets && double.TryParse(r.Key, NumberStyles.Float, Inv, out double target))
                    g.DrawLine(new Pen(Palette.Text, 1.5, new DashStyle(new double[] { 2, 2 }, 0)), new Point(X(target), r.Y - RowH / 2 + 1), new Point(X(target), r.Y + RowH / 2 - 1));
                // the average at the end of the row, then "suggested", then what this test was
                var val = Palette.Fmt(Value(r.St.Mean), 11, Palette.Text, FontWeight.SemiBold);
                double tx = plot.Right + 14;
                g.DrawText(val, new Point(tx, r.Y - val.Height / 2));
                tx += val.Width + 8;
                if (r.Pick)
                {
                    var sg = Palette.Fmt("suggested", 11, Palette.Text, FontWeight.SemiBold);
                    g.DrawText(sg, new Point(tx, r.Y - sg.Height / 2));
                    tx += sg.Width + 8;
                }
                if (r.Hand)
                {
                    var hp = Palette.Fmt("picked by you", 11, Palette.Text, FontWeight.SemiBold);
                    g.DrawText(hp, new Point(tx, r.Y - hp.Height / 2));
                    tx += hp.Width + 8;
                }
                if (r.Tag.Length > 0)
                {
                    var tg = Palette.Fmt(r.Tag, 10, Palette.Text2);
                    tg.MaxTextWidth = Math.Max(20, b.Width - tx - 4);
                    tg.MaxLineCount = 1;
                    tg.Trimming = TextTrimming.CharacterEllipsis;
                    g.DrawText(tg, new Point(tx, r.Y - tg.Height / 2));
                }
                if (_hover.HasValue && Math.Abs(_hover.Value.Y - r.Y) < RowH / 2 && Math.Abs(_hover.Value.Y - r.Y) < best) { best = Math.Abs(_hover.Value.Y - r.Y); near = r; }
            }
            if (near != null)
            {
                var lines = new List<string> { (near.Label == "baseline" ? "Baseline" : near.Label) + (near.Sub.Length > 0 ? ", " + near.Sub : "") + (near.Pick ? ", suggested" : "") + (near.Hand ? ", picked by you" : "") };
                // a baseline's cores and workers (the server's defaults with the counts it ran with)
                bool ownWorkers = near.Base != null ? Settings.BaseWorkersOf(near.Run) == 0 && !(Kind == "target" && near.Run.Options?.Automated == true)
                                                    : Kind == "workers" && near.Key == "0";
                int seen = ownWorkers ? Results.WorkersSeen(near.Base != null ? near.Base.Steps : Results.Groups(near.Run.Steps, Kind).FirstOrDefault(x => x.Key == near.Key).Value ?? new List<Step>()) : 0;
                if (near.Base != null && !(Kind == "target" && near.Run.Options?.Automated == true))
                {
                    int bc = Settings.BaseCoresOf(near.Run), cores = near.Run.Results?.CpuCores > 0 ? near.Run.Results.CpuCores : near.Run.Live ? CpuTopology.Cores.Count : -1;
                    lines.Add("Cores: " + (bc > 0 ? bc.ToString(Inv) : "Server Default" + (cores > 0 ? " (" + cores + ")" : "")));
                    int bw = Settings.BaseWorkersOf(near.Run);
                    lines.Add("Workers: " + (bw > 0 ? bw.ToString(Inv) : "Server Default" + (seen > 0 ? " (" + seen + ")" : "")));
                }
                else if (ownWorkers && seen > 0) lines.Add("Workers: Server Default (" + seen + ")");
                if (near.Base != null && near.Base.Runs.Count > 1) lines.Add("Average of " + near.Base.Runs.Count + " tests");
                else
                {
                    lines.Add(Settings.Title(near.Run));
                    lines.Add(Settings.Detail(near.Run) + (near.Tag.Length > 0 ? " (" + near.Tag + ")" : ""));
                }
                lines.Add(Metric.Name + ": " + Value(near.St.Mean) + " average" + (near.Steps > 1 ? " of " + near.Steps + " steps" : ""));
                lines.Add("lowest " + Value(near.St.Lo) + ", highest " + Value(near.St.Hi) + " (" + near.St.N + " measurements)");
                DrawTip(g, new Point(Math.Min(X(near.St.Mean), b.Width - 10), near.Y), lines);
            }
        }

        // A step that is its test's baseline: the core and worker tests always have one; a frame target test only when
        // it alternated with a baseline target (then that target ran more often than once a run).
        static bool IsBaseline(Step s, IEnumerable<Step> all)
        {
            if (!s.Baseline) return false;
            if (s.Kind != "target") return s.Kind == "cores" || s.Kind == "workers";
            var same = all.Where(x => x.Kind == "target" && x.Value == s.Value).ToList();
            return same.Count > same.Select(x => x.Pass).Distinct().Count();
        }

        // a running test that will measure this graph's test
        bool Measures(TestOptions o) => o.Automated ? Kind == "baseline" || Settings.PairLike(Kind) || Kind == "target" || Kind == "ai"
                                      : Settings.PairLike(Kind) ? false : Kind == "baseline" ? o.TestBaseline || o.TestCores || o.TestWorkers : Kind == "cores" ? o.TestCores
                                      : Kind == "workers" ? o.TestWorkers : Kind == "target" ? o.TestTarget : o.TestAiLoad;

        // each test's own suggestion
        bool IsPick(RunData r, string key)
        {
            var res = r.Results;
            if (res == null) return false;
            if (Kind == "cores") return FastOf(r) > 0 && (key == "all" ? res.RecCores > 0 && !res.FastOnly && res.RecCores > FastOf(r) : res.RecCores > 0 && key == res.RecCores.ToString(Inv));
            if (Kind == "workers") return res.RecWorkers > 0 && key == res.RecWorkers.ToString(Inv);
            // the automated test: the tester's own suggestion, whichever you picked
            if (Kind == "target") return res.TargetAuto > 0 ? key == res.TargetAuto.ToString(Inv) : res.RecTarget > 0 && key == res.RecTarget.ToString(Inv);
            if (Settings.PairLike(Kind)) return (res.PairAuto ?? res.PairPick) == key;
            return false;
        }

        // a pick you made by hand, where it differs from the suggestion
        bool IsHand(RunData r, string key)
        {
            var res = r.Results;
            if (res == null) return false;
            if (Settings.PairLike(Kind)) return res.PairPick != null && res.PairPick != res.PairAuto && key == res.PairPick;
            if (Kind == "target") return res.TargetAuto > 0 && res.RecTarget > 0 && res.RecTarget != res.TargetAuto && key == res.RecTarget.ToString(Inv);
            return false;
        }

        // the most fast cores that test measured on its own ("all" is the pick when the suggestion is more than that)
        static int FastOf(RunData r) =>
            r.Steps.Where(s => s.Kind == "cores" && s.Value != "all").Select(s => int.TryParse(s.Value, NumberStyles.Integer, Inv, out int v) ? v : 0).DefaultIfEmpty(0).Max();

        // rounds the range out to a tidy step (1, 2, 2.5 or 5 times a power of ten) and returns the step
        static double Nice(ref double min, ref double max)
        {
            if (double.IsNaN(min) || double.IsNaN(max)) { min = 0; max = 1; }
            if (max - min < 1e-9) max = min + Math.Max(1, Math.Abs(min) * 0.1);
            double span = max - min, step = Math.Pow(10, Math.Floor(Math.Log10(span / 4)));
            foreach (double m in new[] { 1, 2, 2.5, 5, 10 }) if (span / (step * m) <= 5) { step *= m; break; }
            min = Math.Floor(min / step) * step;
            max = Math.Ceiling(max / step) * step;
            if (max <= min) max = min + step;
            return step;
        }

        static string Num(double v) => Math.Abs(v) >= 100 ? v.ToString("0", Inv) : Math.Abs(v) >= 10 ? v.ToString("0.#", Inv) : v.ToString("0.##", Inv);

        // the axis numbers get the decimals their step needs (2.5 one, 0.25 two), so no two of them read the same
        static string Tick(double v, double step)
        {
            int dec = 0;
            while (dec < 6 && Math.Abs(step * Math.Pow(10, dec) - Math.Round(step * Math.Pow(10, dec))) > 1e-6 * Math.Pow(10, dec)) dec++;
            if (Math.Abs(v) < step * 1e-6) v = 0;
            return v.ToString(dec == 0 ? "0" : "0." + new string('0', dec), Inv);
        }

        void DrawTip(DrawingContext g, Point at, IList<string> lines)
        {
            var texts = lines.Select((l, i) => Palette.Fmt(l, 11, i == 0 ? Palette.Text : Palette.Text2, i == 0 ? FontWeight.SemiBold : FontWeight.Normal)).ToList();
            foreach (var t in texts) t.MaxTextWidth = Math.Max(100, Bounds.Width - 24);   // a long line wraps instead of running off
            double w = texts.Max(t => t.Width) + 16, h = texts.Sum(t => t.Height) + 10;
            double x = at.X + 14, y = at.Y + 12;
            if (x + w > Bounds.Width - 4) x = Math.Max(4, at.X - w - 14);
            if (y + h > Bounds.Height - 2) y = Math.Max(2, at.Y - h - 12);
            g.DrawRectangle(Palette.TipBack, new Pen(Palette.Grid, 1), new Rect(x, y, w, h), 4, 4);
            double ty = y + 5;
            foreach (var t in texts) { g.DrawText(t, new Point(x + 8, ty)); ty += t.Height; }
        }

        void Empty(DrawingContext g, string text)
        {
            var t = Palette.Fmt(text, 12, Palette.Text2);
            t.MaxTextWidth = Math.Max(50, Bounds.Width - 40);
            t.TextAlignment = TextAlignment.Center;
            g.DrawText(t, new Point(20, (Bounds.Height - t.Height) / 2));
        }
    }

    // A row's numbers: the average of a setting's steps, and the lowest and highest 15 second measurement of them.
    public readonly struct Stat
    {
        public readonly double Mean, Lo, Hi;
        public readonly int N;   // measurements
        Stat(double mean, double lo, double hi, int n) { Mean = mean; Lo = lo; Hi = hi; N = n; }

        // the average of the steps' own values (each a step's typical measurement, what the tester judges by), the line
        // over every measurement of them
        public static Stat Of(IEnumerable<Step> steps, Metric m)
        {
            bool Ok(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
            var list = steps.Where(s => s.Windows.Any(w => Ok(m.OfWindow(s, w)))).ToList();
            var means = list.Select(m.OfStep).Where(Ok).ToList();
            var v = list.SelectMany(s => s.Windows.Select(w => m.OfWindow(s, w))).Where(Ok).ToList();
            if (means.Count == 0 || v.Count == 0) return default;
            double mean = means.Average();
            return new Stat(mean, Math.Min(mean, v.Min()), Math.Max(mean, v.Max()), v.Count);
        }
    }
}
