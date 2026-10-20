using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;

namespace ServerTester.Engine
{
    // Shared with the NukeStats plugin (linked into src/NukeStats/NukeStats.csproj), which runs the same automated test on
    // a server itself: nothing here may use the tester's own classes, the operating system or anything newer than
    // .NET Standard 2.1.

    // One physical core: its logical CPUs (two with hyper-threading), its type on a hybrid chip and how fast it is.
    public sealed class PhysCore
    {
        public int Index;            // the operating system's own order
        public int[] Cpus;
        public string Type = "";     // "fast", "efficient" or "" when the chip has one kind
        public long FirmwareRank;    // higher = faster (the firmware's favoured-core ranking, or the top clock)
        public long MaxMhz;
        public double Score = double.NaN;   // the built-in core speed test, work per second on this core (higher = faster)
        public string CpuText => CpuList.Text(Cpus);
    }

    // The CPU a test ran on, as the test sees it: this PC's (CpuTopology) for a test that runs here, the CPUs a server may
    // use for a test the plugin runs there, or the one a results.json describes for results from another machine. The
    // test plan and the analysis read the cores from here, never from the machine they happen to run on.
    public sealed class CpuSnapshot
    {
        public string Model = "unknown CPU";
        public int Threads;
        public bool Windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);   // Windows numbers a core's CPUs differently to Linux
        public List<PhysCore> Cores = new List<PhysCore>();   // the operating system's order
        List<PhysCore> _ranked;                                // fastest first
        public bool Hybrid => Cores.Any(c => c.Type == "efficient");

        // the CPU of the machine the program runs on (the tester sets this to its CpuTopology; null = one core)
        public static Func<CpuSnapshot> Local;
        public static CpuSnapshot Here() => Local?.Invoke() ?? new CpuSnapshot { Threads = 1, Cores = new List<PhysCore> { new PhysCore { Index = 0, Cpus = new[] { 0 } } } };

        public CpuSnapshot() { }

        // ranked: the cores fastest first (as Rank sorts them, or as a results.json lists them); null = rank them now
        public CpuSnapshot(string model, IEnumerable<PhysCore> cores, IEnumerable<PhysCore> ranked = null)
        {
            Model = string.IsNullOrWhiteSpace(model) ? "unknown CPU" : model.Trim();
            Cores = cores.OrderBy(c => c.Index).ToList();
            Threads = Cores.Sum(c => c.Cpus.Length);
            _ranked = ranked?.ToList();
        }

        public List<PhysCore> Ranked() => (_ranked ?? (_ranked = Rank(Cores))).ToList();

        public List<PhysCore> FastCores()
        {
            var r = Ranked();
            return Hybrid ? r.Where(c => c.Type != "efficient").ToList() : r;
        }

        // fast cores first, then the speed test (if it ran), then the firmware's rank, then the operating system's order
        // Speed test scores within 3% of each other count as the same speed (that much is run-to-run noise), so the
        // firmware's own favoured-core ranking decides between them.
        public static List<PhysCore> Rank(IEnumerable<PhysCore> cores)
        {
            var all = cores.ToList();
            double top = all.Select(c => c.Score).Where(x => !double.IsNaN(x) && x > 0).DefaultIfEmpty(0).Max();
            int Tier(PhysCore c) => top > 0 && !double.IsNaN(c.Score) ? (int)Math.Floor((top - c.Score) / top / 0.03) : 0;
            return all.OrderBy(c => c.Type == "efficient" ? 1 : 0)
                      .ThenBy(Tier)
                      .ThenByDescending(c => c.FirmwareRank)
                      .ThenBy(c => c.Index).ToList();
        }
    }

    // CPU lists as the operating system writes them: "0-3,12-19".
    public static class CpuList
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // "0-3,12-19" -> {0,1,2,3,12,...}
        public static int[] Parse(string s)
        {
            var r = new List<int>();
            if (string.IsNullOrWhiteSpace(s)) return r.ToArray();
            foreach (var part in s.Trim().Split(','))
            {
                var p = part.Trim();
                if (p.Length == 0) continue;
                int dash = p.IndexOf('-');
                if (dash > 0 && int.TryParse(p.Substring(0, dash), NumberStyles.Integer, Inv, out int a) && int.TryParse(p.Substring(dash + 1), NumberStyles.Integer, Inv, out int b))
                    for (int i = a; i <= b; i++) r.Add(i);
                else if (int.TryParse(p, NumberStyles.Integer, Inv, out int x)) r.Add(x);
            }
            return r.Distinct().OrderBy(x => x).ToArray();
        }

        // {0,1,2,3,12,13} -> "0-3,12-13"
        public static string Text(IEnumerable<int> cpus)
        {
            var a = cpus.Distinct().OrderBy(x => x).ToList();
            if (a.Count == 0) return "none";
            var parts = new List<string>();
            int start = a[0], prev = a[0];
            for (int i = 1; i <= a.Count; i++)
            {
                if (i < a.Count && a[i] == prev + 1) { prev = a[i]; continue; }
                parts.Add(prev != start ? start.ToString(Inv) + "-" + prev.ToString(Inv) : start.ToString(Inv));
                if (i < a.Count) { start = prev = a[i]; }
            }
            return string.Join(",", parts);
        }
    }

    // The built-in core speed test's work, and what its scores say about the cores. The caller pins the thread that runs
    // Work to one core first (each program does that its own way).
    public static class CoreSpeed
    {
        public const double SecondsPerCore = 1.5;

        // a fixed mix of integer and floating point work over a small table (stays in the core's own cache); returns the
        // rounds it got through per second
        public static double Work(double seconds)
        {
            var table = new double[2048];
            for (int i = 0; i < table.Length; i++) table[i] = i * 0.5 + 1;
            ulong x = 88172645463325252UL;
            double acc = 0;
            long rounds = 0;
            var sw = Stopwatch.StartNew();
            long limit = (long)(seconds * Stopwatch.Frequency);
            while (sw.ElapsedTicks < limit)
            {
                for (int k = 0; k < 4096; k++)
                {
                    x ^= x << 13; x ^= x >> 7; x ^= x << 17;
                    int j = (int)(x & 2047);
                    acc = acc * 0.999999 + table[j] * 1.000001;
                    table[j] = acc * 0.5 + (x & 7);
                }
                rounds++;
            }
            if (acc == 42.4242) Console.Write("");   // keeps the loop from being optimised away
            return rounds / sw.Elapsed.TotalSeconds;
        }

        // When the operating system does not say which cores are fast (a virtual machine, an older kernel), two groups of
        // scores far apart are taken as fast and efficient cores. Returns true when it set the types.
        public static bool ClassifyBySpeed(IList<PhysCore> cores)
        {
            if (cores.Any(c => c.Type.Length > 0) || cores.Count < 2) return false;
            var s = cores.Select(c => c.Score).Where(x => !double.IsNaN(x) && x > 0).OrderByDescending(x => x).ToList();
            if (s.Count < 2) return false;
            // the biggest drop between neighbouring scores
            int cut = -1; double drop = 0;
            for (int i = 1; i < s.Count; i++)
            {
                double d = (s[i - 1] - s[i]) / s[i - 1];
                if (d > drop) { drop = d; cut = i; }
            }
            if (drop < 0.20) return false;   // one kind of core
            double threshold = (s[cut - 1] + s[cut]) / 2;
            foreach (var c in cores) c.Type = c.Score >= threshold ? "fast" : "efficient";
            return true;
        }
    }
}
