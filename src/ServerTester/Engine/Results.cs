using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ServerTester.Engine
{
    // Reading a finished test back, and the tester's own files (the analysis is in Analysis.cs, shared with the plugin).
    public sealed partial class Results
    {
        // ---------------------------------------------------------------- reading a finished test back
        // results.json gives the suggestions, measurements.csv every measurement of every step.
        public static Results Load(string folder, out List<Step> steps, out string summary)
        {
            steps = new List<Step>();
            var r = new Results { Folder = folder };
            summary = "";
            bool alternate = true, utc = false;
            using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "results.json"))))
            {
                var root = doc.RootElement;
                int I(JsonElement o, string k) => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : -1;
                string S(JsonElement o, string k) => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
                if (root.TryGetProperty("test", out var tst))
                {
                    if (S(tst, "pairPick").Length > 0) r.PairPick = S(tst, "pairPick");
                    r.PairAuto = S(tst, "pairAuto").Length > 0 ? S(tst, "pairAuto") : r.PairPick;
                    r.TargetAuto = I(tst, "targetAuto");
                }
                if (root.TryGetProperty("recommend", out var rec))
                {
                    r.RecCores = I(rec, "cores"); r.RecWorkers = I(rec, "workers"); r.RecTarget = I(rec, "targetFps"); r.HighestHeld = I(rec, "highestHeldFps");
                    r.RecCpus = CpuList.Parse(S(rec, "cpus"));
                    r.FastOnly = rec.TryGetProperty("fastCoresOnly", out var fo) && fo.ValueKind == JsonValueKind.True;
                }
                if (root.TryGetProperty("findings", out var f) && f.ValueKind == JsonValueKind.Array)
                    foreach (var x in f.EnumerateArray()) if (x.ValueKind == JsonValueKind.String) r.Findings.Add(x.GetString());
                if (root.TryGetProperty("test", out var t) && t.TryGetProperty("noise", out var n) && n.ValueKind == JsonValueKind.Number) r.Noise = n.GetDouble();
                if (root.TryGetProperty("test", out var t2) && t2.TryGetProperty("os", out var os) && os.ValueKind == JsonValueKind.String)
                    r.RanOnWindows = os.GetString().StartsWith("Microsoft Windows", StringComparison.OrdinalIgnoreCase);
                if (root.TryGetProperty("test", out var t3))
                {
                    r.Mission = S(t3, "mission");
                    if (t3.TryGetProperty("heldBy", out var hb) && hb.ValueKind == JsonValueKind.String) r.HeldBy = hb.GetString();
                    if (t3.TryGetProperty("alternate", out var al) && al.ValueKind == JsonValueKind.False) alternate = false;
                    utc = S(t3, "clock") == "utc";   // the plugin's own test: its measurement times are UTC
                    if (t3.TryGetProperty("quotaCpus", out var q) && q.ValueKind == JsonValueKind.Number) r.QuotaCpus = q.GetDouble();
                }
                // who ran it (the NukeStats plugin, on the server itself), and the host it ran on
                r.Runner = S(root, "runner");
                if (root.TryGetProperty("host", out var host) && host.ValueKind == JsonValueKind.Object)
                    r.Container = host.TryGetProperty("container", out var ct) && ct.ValueKind == JsonValueKind.True;
                string cpu = root.TryGetProperty("cpu", out var c) ? S(c, "model") : "";
                r.CpuModel = cpu;
                r.CpuCores = root.TryGetProperty("cpu", out var c2) ? I(c2, "cores") : -1;
                if (DateTime.TryParse(S(root, "date"), Inv, DateTimeStyles.RoundtripKind, out var when)) r.Date = when;
                // when it started, in this PC's time (the plugin's own test writes UTC; a date with no zone is local)
                summary = cpu + ", " + (r.Date != default ? LocalTime(r.Date).ToString("yyyy-MM-dd HH:mm:ss", Inv) : S(root, "date").Replace('T', ' ').Split('.')[0]);
            }
            string csv = Path.Combine(folder, "measurements.csv");
            if (!File.Exists(csv)) return r;
            var lines = File.ReadAllLines(csv);
            var byNumber = new Dictionary<int, Step>();
            var day = r.Date == default ? DateTime.Today : utc ? r.Date.ToUniversalTime().Date : r.Date.ToLocalTime().Date;
            // from the test's start, so a first measurement past midnight lands on the next day too
            var lastAt = r.Date == default ? default(DateTime) : utc ? r.Date.ToUniversalTime() : r.Date.ToLocalTime();
            for (int i = 1; i < lines.Length; i++)
            {
                var f = SplitCsv(lines[i]);
                if (f.Count < 20 || !int.TryParse(f[0], out int num)) continue;
                double D(int k) => double.TryParse(f[k], NumberStyles.Float, Inv, out double v) ? v : double.NaN;
                if (!byNumber.TryGetValue(num, out var st))
                {
                    // an AI load step's AI is the load it asked for (its Value), not what its first measurement saw
                    int ai = f[1] == "ai" && int.TryParse(f[2], NumberStyles.Integer, Inv, out int asked) ? asked : (int)D(13);
                    st = new Step { Number = num, Kind = f[1], Value = f[2], Label = f[3], Baseline = f[4] == "1", Workers = (int)D(14), Target = (int)D(15), Ai = ai,
                                    Pass = f.Count > 20 && int.TryParse(f[20], out int pass) ? pass : 1 };
                    byNumber[num] = st;
                    steps.Add(st);
                }
                // the time of day it was taken (window_time), on the test's own date; past midnight, the next day
                if (TimeSpan.TryParse(f[5], Inv, out var tod))
                {
                    var at = day + tod;
                    if (lastAt != default && at < lastAt - TimeSpan.FromHours(12)) { day = day.AddDays(1); at = day + tod; }   // past midnight (not a clock change)
                    lastAt = at;
                }
                st.Windows.Add(new Measurement
                {
                    At = utc ? DateTime.SpecifyKind(lastAt, DateTimeKind.Utc).ToLocalTime() : lastAt,
                    AtUtc = utc ? DateTime.SpecifyKind(lastAt, DateTimeKind.Utc) : lastAt.ToUniversalTime(), Sec = 15, Fps = D(6), P95 = D(7), Physics = D(9), Spare = D(10), FixedPhase = D(11), JobsWait = D(12), Ai = (int)D(13),
                    Workers = (int)D(14), Target = (int)D(15), CpuPct = D(16), MainMhz = D(17), TempC = D(18), Throttled = f[19] == "1",
                    P99 = f.Count > 21 ? D(21) : double.NaN, FtMax = f.Count > 22 ? D(22) : double.NaN,
                });
            }
            Relate(steps, alternate);
            using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "results.json"))))
                r.Options = OptionsFrom(doc.RootElement, steps, out _);
            return r;
        }

        static DateTime LocalTime(DateTime d) => d.Kind == DateTimeKind.Unspecified ? d : d.ToLocalTime();

        // when a saved test started, in UTC (its results.json's date), to put tests in order: the plugin names its folders by
        // UTC and the tester by this PC's time, so the folder names don't. MinValue when it can't be read.
        public static DateTime StartedUtc(string folder)
        {
            try
            {
                using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "results.json"))))
                    if (doc.RootElement.TryGetProperty("date", out var d) && d.ValueKind == JsonValueKind.String
                        && DateTime.TryParse(d.GetString(), Inv, DateTimeStyles.RoundtripKind, out var when))
                        return when.ToUniversalTime();
            }
            catch { }
            return DateTime.MinValue;
        }

        // the options a saved test ran with, from its results.json (a test from before the mission choice has no mission),
        // with the CPU it ran on, so it reads the same on any PC
        static TestOptions OptionsFrom(JsonElement root, List<Step> steps, out int aiReached)
        {
            var o = new TestOptions { TestBaseline = steps.Any(s => s.Kind == "baseline"), TestCores = steps.Any(s => s.Kind == "cores"), TestWorkers = steps.Any(s => s.Kind == "workers"),
                                      TestTarget = steps.Any(s => s.Kind == "target"), TestAiLoad = steps.Any(s => s.Kind == "ai"), Mission = "", Cpu = CpuFrom(root) };
            aiReached = 0;
            if (root.TryGetProperty("test", out var t))
            {
                int I(string k, int def) => t.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : def;
                o.Ai = I("ai", o.Ai); aiReached = I("aiReached", 0); o.Repeats = I("repeats", 1); o.CompareFps = I("compareFps", 60);
                o.SparePct = I("sparePct", 25); o.BaselineCores = I("baselineCores", 0); o.BaselineWorkers = I("baselineWorkers", 4); o.KeepWindows = I("keepWindows", 3);
                o.Alternate = !(t.TryGetProperty("alternate", out var alt) && alt.ValueKind == JsonValueKind.False);
                o.TargetAlternate = t.TryGetProperty("targetAlternate", out var ta) && ta.ValueKind == JsonValueKind.True;
                o.TargetBaseline = I("targetBaseline", 60);
                // the AI load test's own settings (a test from before they were options started at its AI, ten at a time, up to 120)
                var aiSteps = steps.Where(s => s.Kind == "ai").Select(s => s.Ai).ToList();
                o.AiStart = I("aiStart", aiSteps.Count > 0 ? aiSteps.Min() : o.Ai); o.AiStep = I("aiStep", 10); o.AiMax = I("aiMax", Math.Max(120, aiSteps.DefaultIfEmpty(0).Max()));
                if (t.TryGetProperty("heldBy", out var hb) && hb.ValueKind == JsonValueKind.String) o.HeldBy = hb.GetString();
                if (t.TryGetProperty("mission", out var m) && m.ValueKind == JsonValueKind.String) o.Mission = m.GetString();
                bool B(string k) => t.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
                o.Automated = B("automated") || steps.Any(s => s.Kind == "pair");
                o.Grid = B("grid") || steps.Any(s => s.Kind == "grid");
                int[] Ints(string k) => t.TryGetProperty(k, out var a) && a.ValueKind == JsonValueKind.Array
                    ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt32()).ToArray() : null;
                o.GridAi = Ints("gridAi") ?? o.GridAi; o.GridTargets = Ints("gridTargets") ?? o.GridTargets; o.GridWorkers = Ints("gridWorkers") ?? o.GridWorkers;
                o.GridDefault = B("gridDefault");
                if (t.TryGetProperty("visualSkips", out var vs) && (vs.ValueKind == JsonValueKind.True || vs.ValueKind == JsonValueKind.False)) o.VisualSkips = vs.GetBoolean();
                if (t.TryGetProperty("gridCores", out var gc) && gc.ValueKind == JsonValueKind.Array)
                    o.GridCores = gc.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()).ToArray();
                o.PickPairByHand = B("pickPairByHand"); o.PickTargetByHand = B("pickTargetByHand");
                if (t.TryGetProperty("useCores", out var uc) && uc.ValueKind == JsonValueKind.Array)
                    o.UseCores = uc.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt32()).ToArray();
                // the plugin's test on a server under a CPU limit
                if (t.TryGetProperty("quotaCpus", out var q) && q.ValueKind == JsonValueKind.Number) o.QuotaCpus = q.GetDouble();
                o.PinUnderQuota = B("pinUnderQuota");
            }
            return o;
        }

        // the CPU a saved test ran on, from its results.json's layout (fastest first); null when it has none (this PC's
        // then). Results from before the layout had each core's index get the operating system's order, the order of each
        // core's first CPU.
        static CpuSnapshot CpuFrom(JsonElement root)
        {
            if (!root.TryGetProperty("cpu", out var c) || c.ValueKind != JsonValueKind.Object || !c.TryGetProperty("layout", out var lay) || lay.ValueKind != JsonValueKind.Array) return null;
            var ranked = new List<PhysCore>();
            foreach (var e in lay.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                int[] cpus = CpuList.Parse(e.TryGetProperty("cpus", out var cp) && cp.ValueKind == JsonValueKind.String ? cp.GetString() : "");
                if (cpus.Length == 0) continue;
                ranked.Add(new PhysCore
                {
                    Cpus = cpus,
                    Type = e.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.String ? ty.GetString() : "",
                    Score = e.TryGetProperty("score", out var sc) && sc.ValueKind == JsonValueKind.Number ? sc.GetDouble() : double.NaN,
                    Index = e.TryGetProperty("index", out var ix) && ix.ValueKind == JsonValueKind.Number ? ix.GetInt32() : -1,
                });
            }
            if (ranked.Count == 0) return null;
            if (ranked.Any(k => k.Index < 0) || ranked.Select(k => k.Index).Distinct().Count() < ranked.Count)
            {
                int i = 0;
                foreach (var k in ranked.OrderBy(k => k.Cpus[0])) k.Index = i++;
            }
            string model = c.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : "";
            var snap = new CpuSnapshot(model, ranked, ranked);
            if (c.TryGetProperty("threads", out var th) && th.ValueKind == JsonValueKind.Number && th.GetInt32() > 0) snap.Threads = th.GetInt32();
            if (root.TryGetProperty("test", out var t) && t.TryGetProperty("os", out var os) && os.ValueKind == JsonValueKind.String)
                snap.Windows = os.GetString().StartsWith("Microsoft Windows", StringComparison.OrdinalIgnoreCase);
            return snap;
        }

        // a saved test back as a TestRunner, with the options it ran with and the CPU it ran on, so the analysis can run on
        // it again on any PC
        public static TestRunner Reload(string folder)
        {
            Load(folder, out var steps, out _);
            TestOptions o = null;
            int aiReached = 0;
            DateTime started = DateTime.Now;
            string stopped = null;
            using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "results.json"))))
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("stoppedBecause", out var sb) && sb.ValueKind == JsonValueKind.String) stopped = sb.GetString();
                // the frame targets a run skipped (a lower one was missed): not in measurements.csv, which only has steps
                // that measured, and the held rule needs them
                if (root.TryGetProperty("steps", out var js) && js.ValueKind == JsonValueKind.Array)
                    foreach (var e in js.EnumerateArray())
                    {
                        string note = e.TryGetProperty("note", out var nt) && nt.ValueKind == JsonValueKind.String ? nt.GetString() : "";
                        int num = e.TryGetProperty("n", out var nn) && nn.ValueKind == JsonValueKind.Number ? nn.GetInt32() : -1;
                        if (!note.StartsWith("skipped", StringComparison.Ordinal) || num < 0 || steps.Any(x => x.Number == num)) continue;
                        string Str(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
                        int Int(string k, int def) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : def;
                        steps.Add(new Step { Number = num, Kind = Str("kind"), Value = Str("value"), Label = Str("label"), Target = Int("target", 0), Workers = Int("workers", 0),
                                             Pass = Int("run", 1), Baseline = e.TryGetProperty("baseline", out var bl) && bl.ValueKind == JsonValueKind.True,
                                             Note = note, Skipped = true });
                    }
                steps.Sort((x, y) => x.Number.CompareTo(y.Number));
                if (root.TryGetProperty("date", out var d) && d.ValueKind == JsonValueKind.String && DateTime.TryParse(d.GetString(), Inv, DateTimeStyles.RoundtripKind, out var when)) started = when;
                o = OptionsFrom(root, steps, out aiReached);
                if (o.Mission == "") o.Mission = TestPlan.StressTest;
            }
            var saved = TestRunner.Saved(steps, o, aiReached, started, stopped);
            // the automated test's picks
            using (var doc2 = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "results.json"))))
                if (doc2.RootElement.TryGetProperty("test", out var t2))
                {
                    string Str(string k) => t2.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
                    if (Str("pairPick").Length > 0) { saved.PairPick = Str("pairPick"); saved.PairWhy = Str("pairWhy"); saved.PairAuto = Str("pairAuto").Length > 0 ? Str("pairAuto") : saved.PairPick; }
                    saved.TargetAuto = t2.TryGetProperty("targetAuto", out var ta2) && ta2.ValueKind == JsonValueKind.Number ? ta2.GetInt32() : 0;
                    saved.PairByHand = t2.TryGetProperty("pairByHand", out var pb) && pb.ValueKind == JsonValueKind.True;
                    saved.TargetPick = t2.TryGetProperty("targetPick", out var tp) && tp.ValueKind == JsonValueKind.Number ? tp.GetInt32() : 0;
                    saved.TargetByHand = t2.TryGetProperty("targetByHand", out var tb) && tb.ValueKind == JsonValueKind.True;
                }
            return saved;
        }

        static List<string> SplitCsv(string line)
        {
            var o = new List<string>();
            var sb = new StringBuilder();
            bool q = false;
            foreach (char ch in line)
            {
                if (ch == '"') { q = !q; continue; }
                if (ch == ',' && !q) { o.Add(sb.ToString()); sb.Clear(); continue; }
                sb.Append(ch);
            }
            o.Add(sb.ToString());
            return o;
        }

        // ---------------------------------------------------------------- files
        public void Save(TestRunner run)
        {
            Folder = Path.Combine(ServerSetup.ResultsDir, run.Started.ToString("yyyy-MM-dd_HHmm", Inv));
            Directory.CreateDirectory(Folder);
            var json = JsonTree(run, App.Version, ServerSetup.ServerBuild(), System.Runtime.InteropServices.RuntimeInformation.OSDescription);
            File.WriteAllText(Path.Combine(Folder, "results.json"), JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(Folder, "measurements.csv"), CsvText(run));
            File.WriteAllText(Path.Combine(Folder, "report.txt"), Report(run));
            // the test server's own logs, so a server that stopped on its own leaves the reason behind (the next test
            // starts a fresh log)
            foreach (var (from, to) in new[] { (ServerSetup.LogPath, "server-console.log"),
                                               (Path.Combine(ServerSetup.ServerDir, "BepInEx", "LogOutput.log"), "server-bepinex.log"),
                                               (ServerSetup.LauncherLogPath, "server-output.log") })
                try { if (File.Exists(from)) File.Copy(from, Path.Combine(Folder, to), true); } catch { }
        }

    }
}
