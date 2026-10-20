using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace ServerTester.Engine
{
    // The CPU as the operating system describes it.
    public static class CpuTopology
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public static string Model = "unknown CPU";
        public static int Threads;
        public static List<PhysCore> Cores = new List<PhysCore>();
        public static bool Hybrid => Cores.Any(c => c.Type == "efficient");
        public static bool TypesFromSpeedTest;   // the types came from the speed test, not from the operating system

        public static void Detect()
        {
            Threads = Environment.ProcessorCount;
            try
            {
                if (Native.IsLinux) Linux();
                else if (Native.IsWindows) Windows();
            }
            catch { }
            if (Cores.Count == 0)
                Cores = Enumerable.Range(0, Threads).Select(i => new PhysCore { Index = i, Cpus = new[] { i } }).ToList();
            Threads = Cores.Sum(c => c.Cpus.Length);
        }

        // fast cores first, then the speed test (if it ran), then the firmware's rank, then the operating system's order
        public static List<PhysCore> Ranked() => CpuSnapshot.Rank(Cores);

        public static List<PhysCore> FastCores()
        {
            var r = Ranked();
            return Hybrid ? r.Where(c => c.Type != "efficient").ToList() : r;
        }

        // this PC's CPU for a test: a copy, so the speed test can't change a test's cores while it runs
        public static CpuSnapshot Snapshot()
        {
            var copy = Cores.Select(c => new PhysCore { Index = c.Index, Cpus = c.Cpus, Type = c.Type, FirmwareRank = c.FirmwareRank, MaxMhz = c.MaxMhz, Score = c.Score }).ToList();
            return new CpuSnapshot(Model, copy, CpuSnapshot.Rank(copy)) { Threads = Threads };
        }

        // ---------------------------------------------------------------- Linux: /proc and /sys
        static void Linux()
        {
            foreach (var line in (Native.Read("/proc/cpuinfo") ?? "").Split('\n'))
                if (line.StartsWith("model name", StringComparison.Ordinal)) { Model = line.Substring(line.IndexOf(':') + 1).Trim(); break; }
            int[] online = Native.ParseList(Native.Read("/sys/devices/system/cpu/online"));
            if (online.Length == 0) online = Enumerable.Range(0, Environment.ProcessorCount).ToArray();
            int[] pc = Native.ParseList(Native.Read("/sys/devices/cpu_core/cpus"));    // Intel hybrid: fast cores
            int[] ec = Native.ParseList(Native.Read("/sys/devices/cpu_atom/cpus"));    // Intel hybrid: efficient cores
            var groups = new Dictionary<string, List<int>>();
            var order = new List<string>();
            foreach (int c in online)
            {
                string t = "/sys/devices/system/cpu/cpu" + c.ToString(Inv) + "/topology/";
                string key = (Native.Read(t + "physical_package_id") ?? "0") + ":" + (Native.Read(t + "die_id") ?? "0") + ":" + (Native.Read(t + "core_id") ?? c.ToString(Inv));
                if (!groups.TryGetValue(key, out var l)) { groups[key] = l = new List<int>(); order.Add(key); }
                l.Add(c);
            }
            var cores = new List<PhysCore>();
            int idx = 0;
            foreach (var key in order)
            {
                var g = groups[key];
                g.Sort();
                string cp = "/sys/devices/system/cpu/cpu" + g[0].ToString(Inv);
                long rank = Native.ReadLong(cp + "/cpufreq/amd_pstate_prefcore_ranking");
                if (rank <= 0) rank = Native.ReadLong(cp + "/acpi_cppc/highest_perf");
                long max = Native.ReadLong(cp + "/cpufreq/cpuinfo_max_freq");
                if (rank <= 0) rank = max > 0 ? max / 1000 : 0;
                string type = "";
                if (pc.Length > 0 && ec.Length > 0) type = pc.Contains(g[0]) ? "fast" : ec.Contains(g[0]) ? "efficient" : "";
                cores.Add(new PhysCore { Index = idx++, Cpus = g.ToArray(), Type = type, FirmwareRank = rank, MaxMhz = max > 0 ? max / 1000 : 0 });
            }
            Cores = cores;
        }

        // ---------------------------------------------------------------- Windows: GetLogicalProcessorInformationEx
        static void Windows()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                {
                    var n = k?.GetValue("ProcessorNameString") as string;
                    if (!string.IsNullOrWhiteSpace(n)) Model = n.Trim();
                }
            }
            catch { }
            uint len = 0;
            Native.GetLogicalProcessorInformationEx(0, IntPtr.Zero, ref len);
            if (len == 0) return;
            IntPtr buf = Marshal.AllocHGlobal((int)len);
            var raw = new List<KeyValuePair<int[], int>>();
            try
            {
                if (!Native.GetLogicalProcessorInformationEx(0, buf, ref len)) return;
                int off = 0;
                while (off < len)
                {
                    IntPtr e = buf + off;
                    int rel = Marshal.ReadInt32(e, 0), size = Marshal.ReadInt32(e, 4);
                    if (size <= 0) break;
                    if (rel == 0)   // RelationProcessorCore
                    {
                        int eff = Marshal.ReadByte(e, 9);
                        int groupCount = Marshal.ReadInt16(e, 30);
                        var cpus = new List<int>();
                        for (int gi = 0; gi < groupCount; gi++)
                        {
                            IntPtr ga = e + 32 + gi * 16;
                            ulong mask = (ulong)Marshal.ReadInt64(ga, 0);
                            int group = Marshal.ReadInt16(ga, 8);
                            if (group != 0) continue;   // processor group 0 only (the first 64 CPUs)
                            for (int b = 0; b < 64; b++) if ((mask & (1UL << b)) != 0) cpus.Add(b);
                        }
                        if (cpus.Count > 0) raw.Add(new KeyValuePair<int[], int>(cpus.ToArray(), eff));
                    }
                    off += size;
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            bool hybrid = raw.Select(r => r.Value).Distinct().Count() > 1;
            int top = raw.Count > 0 ? raw.Max(r => r.Value) : 0;
            Cores = raw.Select((r, i) => new PhysCore
            {
                Index = i, Cpus = r.Key, FirmwareRank = r.Value,
                Type = hybrid ? (r.Value == top ? "fast" : "efficient") : "",
            }).ToList();
        }
    }
}
