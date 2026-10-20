using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ServerTester.Engine;

namespace ServerTester
{
    // The same test without the window, for a machine you reach over SSH:
    //   ServerTester --cli [--install] [--bench] [--automated | --tests baseline,cores,workers,target,ai] [--order alternate|sequential]
    //                      [--use-cpus 0-11] [--step-seconds 30]
    //                      [--mission stress|realistic] [--ai 60] [--repeats 1] [--baseline-cores N] [--baseline-workers N] [--no-power]
    //                      [--target-start 60] [--target-step 10] [--target-steps 7] [--target-baseline 90]
    //                      [--ai-start 60] [--ai-step 10] [--ai-max 120]
    //                      [--compare-fps 90] [--spare 25] [--held-by average|low5|low1]
    //   ServerTester --cli --grid [--grid-ai 20,30,40,50,60] [--grid-targets 90,120] [--grid-cores every|1,2,4] [--grid-workers 0,2,4,8] [--ref-every 6]
    //   ServerTester --cli --analyse data/results/<date>   (a saved test analysed again, on any PC: its results.json says which CPU it ran on)
    // Exit codes: 0 finished, 1 couldn't start, 2 wrong arguments, 3 the test stopped early, 130 stopped by you.
    // On Windows the program is a window program, so a shell doesn't wait for it: run it with
    //   start "" /wait ServerTester.exe --cli ...      (cmd)     or     Start-Process -Wait -NoNewWindow ServerTester.exe '--cli ...'   (PowerShell)
    internal static class Cli
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // The Windows build is a window program, so it has no console of its own: borrow the one it was started from.
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool AllocConsole();

        public static void AttachConsoleOnWindows()
        {
            if (!Native.IsWindows) return;
            try
            {
                if (!AttachConsole(-1)) AllocConsole();
                var o = new System.IO.StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                Console.SetOut(o);
                Console.SetError(o);
            }
            catch { }
        }

        static string Arg(string[] a, string name, string def)
        {
            int i = Array.IndexOf(a, name);
            return i >= 0 && i + 1 < a.Length ? a[i + 1] : def;
        }

        public static async Task<int> Run(string[] a)
        {
            void Log(string s) => Console.WriteLine(s);
            // a saved test analysed again: ServerTester --cli --analyse data/results/<date>
            int an = Array.IndexOf(a, "--analyse");
            if (an >= 0)
            {
                if (an + 1 >= a.Length) { Console.Error.WriteLine("--analyse needs a results folder"); return 2; }
                TestRunner saved;
                try { saved = Results.Reload(a[an + 1]); }
                catch (Exception e) { Console.Error.WriteLine("Couldn't read the results in " + a[an + 1] + ": " + e.Message); return 2; }
                Log(Results.Analyse(saved).Report(saved));
                return 0;
            }
            using var cancel = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; Log("stopping..."); cancel.Cancel(); };
            Log("Nuclear Option Server Tester " + App.Version + ": " + CpuTopology.Model + ", " + CpuTopology.Cores.Count + " cores / " + CpuTopology.Threads + " threads"
                + (CpuTopology.Hybrid ? ", " + TestPlan.FastCount + " fast" : ""));
            if (RunGuard.OtherTesterRunning()) { Console.Error.WriteLine("Another copy of the tester is running a test from this folder: wait for it to finish."); return 1; }
            if (a.Contains("--install") || !ServerSetup.ServerInstalled)
            {
                if (!await ServerSetup.InstallOrUpdateServer(Log, p => Console.Write("\r  downloading " + (p * 100).ToString("0", Inv) + "%   "), cancel.Token)) return 1;
                Console.WriteLine();
            }
            if (!ServerSetup.InstallMods(Log)) return 1;
            if (a.Contains("--bench"))
            {
                if (!CoreBench.Run((d, t, s) => Log("  " + s), cancel.Token)) { Log("Stopped."); return 130; }
                foreach (var c in CpuTopology.Ranked())
                    Log(string.Format(Inv, "  core CPUs {0,-8} {1,-10} score {2:0}", c.CpuText, c.Type.Length > 0 ? c.Type : "-", c.Score));
            }
            string tests = Arg(a, "--tests", "baseline,cores,workers,target");
            var o = new TestOptions
            {
                Automated = a.Contains("--automated"),   // the automated test, each round's best picked automatically
                UseCores = Arg(a, "--use-cpus", "") is string uc && uc.Length > 0 ? CpuTopology.Cores.Where(c => c.Cpus.All(Native.ParseList(uc).Contains)).Select(c => c.Index).ToArray() : null,
                TestBaseline = tests.Contains("baseline"), TestCores = tests.Contains("cores"), TestWorkers = tests.Contains("workers"), TestTarget = tests.Contains("target"), TestAiLoad = tests.Contains("ai"),
                Alternate = a.Contains("--automated") || Arg(a, "--order", "alternate") != "sequential",   // the automated test always alternates
                VisualSkips = !a.Contains("--no-visual-skips"),   // off: the server does all its visual work, as it comes
                Ai = int.Parse(Arg(a, "--ai", "60"), Inv),
                AiStart = int.Parse(Arg(a, "--ai-start", Arg(a, "--ai", "60")), Inv),
                AiStep = int.Parse(Arg(a, "--ai-step", "10"), Inv),
                AiMax = int.Parse(Arg(a, "--ai-max", "120"), Inv),
                Mission = Arg(a, "--mission", "stress").StartsWith("real", StringComparison.OrdinalIgnoreCase) ? TestPlan.RealisticBenchmark : TestPlan.StressTest,
                Repeats = int.Parse(Arg(a, "--repeats", "1"), Inv),
                KeepWindows = Math.Max(2, int.Parse(Arg(a, "--step-seconds", "30"), Inv) / 15),
                BaselineCores = int.Parse(Arg(a, "--baseline-cores", "0"), Inv),       // 0 = every core, as out of the box
                BaselineWorkers = int.Parse(Arg(a, "--baseline-workers", "-1"), Inv),  // 0 = the game's own; unset = one fewer than the cores
                WarmupWindows = int.Parse(Arg(a, "--warmup", "8"), Inv),
                HighPower = !a.Contains("--no-power"),
                TargetStart = int.Parse(Arg(a, "--target-start", "60"), Inv),
                TargetStep = int.Parse(Arg(a, "--target-step", "10"), Inv),
                TargetSteps = int.Parse(Arg(a, "--target-steps", "7"), Inv),
                TargetAlternate = Arg(a, "--target-baseline", "") != "",
                TargetBaseline = int.Parse(Arg(a, "--target-baseline", "90") is string tb && tb.Length > 0 ? tb : "90", Inv),
                CompareFps = int.Parse(Arg(a, "--compare-fps", "90"), Inv),
                SparePct = int.Parse(Arg(a, "--spare", "25"), Inv),
                HeldBy = Arg(a, "--held-by", "average") is string hb && (hb == TestPlan.HeldOnLow5 || hb == TestPlan.HeldOnLow1) ? hb : TestPlan.HeldOnAverage,
            };
            if (o.BaselineWorkers < 0) o.BaselineWorkers = TestPlan.DefaultBaseWorkers(o);
            // the grid test: --grid [--grid-ai 20,30,40,50,60] [--grid-targets 90,120] [--grid-cores every|1,2,4]
            //                       [--grid-workers 0,1,2,3,4,6,8] [--ref-every 6] [--grid-default]   (workers 0 = Server Default;
            //                       --grid-default adds the game's own 60 fps and workers at each AI level)
            if (a.Contains("--grid"))
            {
                int[] Ints(string name, int[] def) => Arg(a, name, "") is string v && v.Length > 0 ? v.Split(',').Select(x => int.Parse(x.Trim(), Inv)).ToArray() : def;
                o.Grid = true;
                o.GridAi = Ints("--grid-ai", o.GridAi); o.GridTargets = Ints("--grid-targets", o.GridTargets); o.GridWorkers = Ints("--grid-workers", o.GridWorkers);
                if (Arg(a, "--grid-cores", "") is string gc && gc.Length > 0) o.GridCores = gc.Split(',').Select(x => x.Trim()).ToArray();
                o.RefEvery = int.Parse(Arg(a, "--ref-every", "6"), Inv);
                o.GridDefault = a.Contains("--grid-default");
                o.Ai = o.GridAi.Length > 0 ? o.GridAi.Min() : o.Ai;
            }
            var run = new TestRunner();
            run.Log += Log;
            Log("Plan: " + (o.Automated ? "up to " : "") + TestPlan.StepCount(o) + " steps");
            await run.Run(o, cancel.Token);
            var r = Results.Analyse(run);
            r.Save(run);
            Log("");
            Log(r.Report(run));
            Log("Saved in " + r.Folder);
            return run.StoppedBecause == null ? 0 : run.StoppedBecause == "stopped by you" ? 130 : 3;
        }
    }
}
