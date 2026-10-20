using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using ServerTester.Engine;

namespace NukeStats
{
    // The machine the server runs on, for the self-test (SelfTest.cs): the CPUs it may use and how they are laid out, a CPU
    // limit from the host (a container's quota), the server's CPUs for each step and back again, and once a second what
    // the tester's HostMonitor samples (the server's CPU use, the clock and temperature of the CPU its main thread is on,
    // and whether the CPU throttled). File reads and pinning run on the config poller's thread, never the game's.
    // Nothing here uses Unity, so it is safe off the main thread.
    internal static class SelfTestHost
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        internal static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        internal static readonly bool IsLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

        static string Read(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; } catch { return null; }
        }

        static long ReadLong(string path, long def = -1)
        {
            string s = Read(path);
            return s != null && long.TryParse(s.Split(' ')[0], NumberStyles.Integer, Inv, out long v) ? v : def;
        }

        // ---------------------------------------------------------------- at start-up (Awake, the main thread)
        static int _mainTid = -1;           // Linux: the game's main thread
        static int[] _processCpus = new int[0];   // the CPUs the process may use, as it started

        internal static void Initialize()
        {
            try
            {
                if (IsLinux)
                {
                    string st = Read("/proc/thread-self/stat");   // the calling thread: Awake runs on the main thread
                    if (st != null && int.TryParse(st.Split(' ')[0], NumberStyles.Integer, Inv, out int tid)) _mainTid = tid;
                    _processCpus = StatusCpus();
                }
                else if (IsWindows) _processCpus = WinProcessCpus();
            }
            catch (Exception e) { NukeStatsPlugin.Log?.LogWarning("[selftest] host: " + e.Message); }
        }

        static int[] StatusCpus()
        {
            string status = Read("/proc/self/status") ?? "";
            foreach (var line in status.Split('\n'))
                if (line.StartsWith("Cpus_allowed_list:", StringComparison.Ordinal)) return CpuList.Parse(line.Substring(18));
            return new int[0];
        }

        // ---------------------------------------------------------------- what the server may use
        internal sealed class HostInfo
        {
            public CpuSnapshot Cpu;
            public int[] Allowed = new int[0];
            public bool Container, Pterodactyl;
            public double QuotaCpus;       // the host's CPU limit in CPUs (0 = none)
            public string Cpuset = "";     // the cgroup's CPU set, as written
            public string Cgroup = "";     // "v2", "v1" or ""
            public string CpuStatPath;     // the cgroup's cpu.stat, for throttling by the limit
            public int JobWorkerMax, ProcessorCount;
            public long ThrottledStart = -1, ThrottledUsecStart = -1;
        }

        // the CPU layout of the CPUs the server may use (model: the game's own SystemInfo.processorType when /proc has none)
        internal static HostInfo Detect(string model, int jobWorkerMax, int processorCount)
        {
            var h = new HostInfo { JobWorkerMax = jobWorkerMax, ProcessorCount = processorCount };
            List<PhysCore> cores = null;
            try
            {
                if (IsLinux) cores = LinuxCores(ref model, out h.Allowed);
                else if (IsWindows) cores = WindowsCores(out h.Allowed);
            }
            catch (Exception e) { NukeStatsPlugin.Log?.LogWarning("[selftest] CPU layout: " + e.Message); }
            if (cores == null || cores.Count == 0)
            {
                int n = Math.Max(1, processorCount);
                h.Allowed = Enumerable.Range(0, n).ToArray();
                cores = h.Allowed.Select((c, i) => new PhysCore { Index = i, Cpus = new[] { c } }).ToList();
            }
            h.Cpu = new CpuSnapshot(model, cores);
            try { if (IsLinux) Limits(h); } catch (Exception e) { NukeStatsPlugin.Log?.LogWarning("[selftest] CPU limit: " + e.Message); }
            h.Pterodactyl = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("P_SERVER_UUID"));
            h.Container = h.Pterodactyl || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SERVER_MEMORY")) || File.Exists("/.dockerenv")
                          || File.Exists("/run/.containerenv") || (Read("/proc/1/cgroup") ?? "").IndexOf("docker", StringComparison.Ordinal) >= 0
                          || (Read("/proc/1/cgroup") ?? "").IndexOf("kubepods", StringComparison.Ordinal) >= 0;
            return h;
        }

        // /sys: the online CPUs the process may use, grouped into cores (package:die:core), the hybrid types and the
        // firmware's ranking; with the files missing (some containers) each CPU counts as its own core
        static List<PhysCore> LinuxCores(ref string model, out int[] allowed)
        {
            foreach (var line in (Read("/proc/cpuinfo") ?? "").Split('\n'))
                if (line.StartsWith("model name", StringComparison.Ordinal)) { model = line.Substring(line.IndexOf(':') + 1).Trim(); break; }
            int[] online = CpuList.Parse(Read("/sys/devices/system/cpu/online"));
            int[] mine = _processCpus.Length > 0 ? _processCpus : StatusCpus();
            allowed = online.Length > 0 && mine.Length > 0 ? online.Intersect(mine).OrderBy(x => x).ToArray() : mine.Length > 0 ? mine : online;
            if (allowed.Length == 0) return null;
            int[] pc = CpuList.Parse(Read("/sys/devices/cpu_core/cpus"));    // Intel hybrid: fast cores
            int[] ec = CpuList.Parse(Read("/sys/devices/cpu_atom/cpus"));    // Intel hybrid: efficient cores
            var groups = new Dictionary<string, List<int>>();
            var order = new List<string>();
            foreach (int c in allowed)
            {
                string t = "/sys/devices/system/cpu/cpu" + c.ToString(Inv) + "/topology/";
                string key = (Read(t + "physical_package_id") ?? "0") + ":" + (Read(t + "die_id") ?? "0") + ":" + (Read(t + "core_id") ?? "cpu" + c.ToString(Inv));
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
                long rank = ReadLong(cp + "/cpufreq/amd_pstate_prefcore_ranking");
                if (rank <= 0) rank = ReadLong(cp + "/acpi_cppc/highest_perf");
                long max = ReadLong(cp + "/cpufreq/cpuinfo_max_freq");
                if (rank <= 0) rank = max > 0 ? max / 1000 : 0;
                string type = "";
                if (pc.Length > 0 && ec.Length > 0) type = pc.Contains(g[0]) ? "fast" : ec.Contains(g[0]) ? "efficient" : "";
                cores.Add(new PhysCore { Index = idx++, Cpus = g.ToArray(), Type = type, FirmwareRank = rank, MaxMhz = max > 0 ? max / 1000 : 0 });
            }
            return cores;
        }

        // the host's CPU limit: cgroup v2 cpu.max (the lowest from the server's group up to the root), or v1's CFS quota;
        // and the CPU set the group allows
        static void Limits(HostInfo h)
        {
            string v2 = null, v1cpu = null, v1set = null;
            foreach (var line in (Read("/proc/self/cgroup") ?? "").Split('\n'))
            {
                var parts = line.Split(new[] { ':' }, 3);
                if (parts.Length < 3) continue;
                if (parts[0] == "0" && parts[1].Length == 0) v2 = parts[2];
                else if (parts[1].Split(',').Contains("cpu")) v1cpu = parts[2];
                else if (parts[1].Split(',').Contains("cpuset")) v1set = parts[2];
            }
            double best = 0;
            if (v2 != null && File.Exists("/sys/fs/cgroup/cgroup.controllers"))
            {
                h.Cgroup = "v2";
                foreach (var dir in Upwards("/sys/fs/cgroup", v2))
                {
                    string max = Read(Path.Combine(dir, "cpu.max"));
                    if (max != null)
                    {
                        var f = max.Split(' ');
                        if (f.Length >= 2 && f[0] != "max" && double.TryParse(f[0], NumberStyles.Float, Inv, out double q) && double.TryParse(f[1], NumberStyles.Float, Inv, out double p) && q > 0 && p > 0)
                            best = best > 0 ? Math.Min(best, q / p) : q / p;
                    }
                    if (h.CpuStatPath == null && File.Exists(Path.Combine(dir, "cpu.stat"))) h.CpuStatPath = Path.Combine(dir, "cpu.stat");
                    if (h.Cpuset.Length == 0) h.Cpuset = Read(Path.Combine(dir, "cpuset.cpus.effective")) ?? "";
                }
            }
            else if (v1cpu != null)
            {
                h.Cgroup = "v1";
                foreach (var root in new[] { "/sys/fs/cgroup/cpu,cpuacct", "/sys/fs/cgroup/cpu" })
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (var dir in Upwards(root, v1cpu))
                    {
                        long q = ReadLong(Path.Combine(dir, "cpu.cfs_quota_us")), p = ReadLong(Path.Combine(dir, "cpu.cfs_period_us"));
                        if (q > 0 && p > 0) best = best > 0 ? Math.Min(best, (double)q / p) : (double)q / p;   // -1 = no limit
                        if (h.CpuStatPath == null && File.Exists(Path.Combine(dir, "cpu.stat"))) h.CpuStatPath = Path.Combine(dir, "cpu.stat");
                    }
                    break;
                }
                foreach (var dir in Upwards("/sys/fs/cgroup/cpuset", v1set ?? "/"))
                {
                    h.Cpuset = Read(Path.Combine(dir, "cpuset.effective_cpus")) ?? Read(Path.Combine(dir, "cpuset.cpus")) ?? "";
                    if (h.Cpuset.Length > 0) break;
                }
            }
            h.QuotaCpus = best;
            Throttled(h.CpuStatPath, out h.ThrottledStart, out h.ThrottledUsecStart);
        }

        // the group's own folder first, then each one above it, up to the root
        static IEnumerable<string> Upwards(string root, string path)
        {
            var parts = (path ?? "/").Trim('/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            for (int n = parts.Count; n >= 0; n--)
            {
                string dir = n == 0 ? root : root + "/" + string.Join("/", parts.Take(n));
                if (Directory.Exists(dir)) yield return dir;
            }
        }

        // the periods the CPU limit held the server back, and for how long (microseconds)
        internal static void Throttled(string cpuStat, out long periods, out long usec)
        {
            periods = -1; usec = -1;
            if (cpuStat == null) return;
            foreach (var line in (Read(cpuStat) ?? "").Split('\n'))
            {
                var f = line.Split(' ');
                if (f.Length < 2 || !long.TryParse(f[1], NumberStyles.Integer, Inv, out long v)) continue;
                if (f[0] == "nr_throttled") periods = v;
                else if (f[0] == "throttled_usec") usec = v;
                else if (f[0] == "throttled_time") usec = v / 1000;   // v1: nanoseconds
            }
        }

        // ---------------------------------------------------------------- Linux affinity, one thread at a time
        const int MaskBytes = 128;   // 1024 CPUs
        [DllImport("libc", EntryPoint = "sched_setaffinity", SetLastError = true)] static extern int set_libc(int tid, IntPtr size, byte[] mask);
        [DllImport("libc", EntryPoint = "sched_getaffinity", SetLastError = true)] static extern int get_libc(int tid, IntPtr size, byte[] mask);
        [DllImport("libc.so.6", EntryPoint = "sched_setaffinity", SetLastError = true)] static extern int set_libc6(int tid, IntPtr size, byte[] mask);
        [DllImport("libc.so.6", EntryPoint = "sched_getaffinity", SetLastError = true)] static extern int get_libc6(int tid, IntPtr size, byte[] mask);
        static bool _libc6;   // "libc" didn't resolve on this Mono: use the full name

        static int SetMask(int tid, byte[] mask)
        {
            if (!_libc6) { try { return set_libc(tid, (IntPtr)MaskBytes, mask); } catch (DllNotFoundException) { _libc6 = true; } catch (EntryPointNotFoundException) { _libc6 = true; } }
            return set_libc6(tid, (IntPtr)MaskBytes, mask);
        }

        static int GetMask(int tid, byte[] mask)
        {
            if (!_libc6) { try { return get_libc(tid, (IntPtr)MaskBytes, mask); } catch (DllNotFoundException) { _libc6 = true; } catch (EntryPointNotFoundException) { _libc6 = true; } }
            return get_libc6(tid, (IntPtr)MaskBytes, mask);
        }

        static byte[] Mask(IEnumerable<int> cpus)
        {
            var m = new byte[MaskBytes];
            foreach (int c in cpus) if (c >= 0 && c < MaskBytes * 8) m[c / 8] |= (byte)(1 << (c % 8));
            return m;
        }

        static int[] Tids()
        {
            var r = new List<int>();
            try
            {
                foreach (var d in Directory.GetDirectories("/proc/self/task"))
                    if (int.TryParse(Path.GetFileName(d), NumberStyles.Integer, Inv, out int tid)) r.Add(tid);
            }
            catch { }
            return r.ToArray();
        }

        // ---------------------------------------------------------------- Windows affinity, the whole process
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetProcessAffinityMask(IntPtr process, out UIntPtr processMask, out UIntPtr systemMask);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetProcessAffinityMask(IntPtr process, UIntPtr mask);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentThread();
        [DllImport("kernel32.dll", SetLastError = true)] static extern UIntPtr SetThreadAffinityMask(IntPtr thread, UIntPtr mask);
        [DllImport("kernel32.dll")] static extern uint GetCurrentProcessorNumber();
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetLogicalProcessorInformationEx(int rel, IntPtr buf, ref uint len);

        static int[] WinProcessCpus()
        {
            if (!GetProcessAffinityMask(GetCurrentProcess(), out UIntPtr pm, out _)) return new int[0];
            ulong m = (ulong)pm;
            return Enumerable.Range(0, 64).Where(i => (m & (1UL << i)) != 0).ToArray();
        }

        static ulong WinMask(IEnumerable<int> cpus)
        {
            ulong m = 0;
            foreach (int c in cpus) if (c >= 0 && c < 64) m |= 1UL << c;
            return m;
        }

        // GetLogicalProcessorInformationEx (the tester's CpuTopology.Windows): processor group 0, the cores the process may use
        static List<PhysCore> WindowsCores(out int[] allowed)
        {
            allowed = _processCpus.Length > 0 ? _processCpus : WinProcessCpus();
            uint len = 0;
            GetLogicalProcessorInformationEx(0, IntPtr.Zero, ref len);
            if (len == 0) return null;
            IntPtr buf = Marshal.AllocHGlobal((int)len);
            var raw = new List<KeyValuePair<int[], int>>();
            try
            {
                if (!GetLogicalProcessorInformationEx(0, buf, ref len)) return null;
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
                        var mine = allowed;
                        if (mine.Length > 0) cpus = cpus.Where(c => mine.Contains(c)).ToList();
                        if (cpus.Count > 0) raw.Add(new KeyValuePair<int[], int>(cpus.ToArray(), eff));
                    }
                    off += size;
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            bool hybrid = raw.Select(r => r.Value).Distinct().Count() > 1;
            int top = raw.Count > 0 ? raw.Max(r => r.Value) : 0;
            return raw.Select((r, i) => new PhysCore { Index = i, Cpus = r.Key, FirmwareRank = r.Value, Type = hybrid ? (r.Value == top ? "fast" : "efficient") : "" }).ToList();
        }

        // ---------------------------------------------------------------- pinning the server for a step, and back
        static readonly object _pinLock = new object();
        static Dictionary<int, byte[]> _snapMasks;   // Linux: each thread's own mask before the test
        static byte[] _snapProcess;                   // Linux: the process's own mask before the test (its first thread's)
        static byte[] _snapCpuset;                    // Linux: the CPUs the server's cgroup gave it before the test
        static ulong _snapWinMask;                    // Windows: the process mask before the test
        static int[] _pinned;                         // the CPUs pinned to now; null = as before the test
        static bool _pinnedSince;                     // the test pinned the server since the snapshot or the last restore
        internal static bool Pinned { get { lock (_pinLock) return _pinned != null; } }

        // each thread's CPUs as they are now, to put back after the test
        internal static void Snapshot()
        {
            lock (_pinLock)
            {
                _pinned = null;
                _pinnedSince = false;
                if (IsLinux)
                {
                    _snapMasks = new Dictionary<int, byte[]>();
                    foreach (int tid in Tids())
                    {
                        var m = new byte[MaskBytes];
                        try { if (GetMask(tid, m) == 0) _snapMasks[tid] = m; } catch { }
                    }
                    int[] proc = StatusCpus();
                    _snapProcess = proc.Length > 0 ? Mask(proc) : _processCpus.Length > 0 ? Mask(_processCpus) : null;
                    int[] set = CgroupCpus();
                    _snapCpuset = set.Length > 0 ? Mask(set) : null;
                }
                else if (IsWindows && GetProcessAffinityMask(GetCurrentProcess(), out UIntPtr pm, out _)) _snapWinMask = (ulong)pm;
            }
        }

        // the CPUs the server's cgroup lets it use now (its own cpuset's effective CPUs, or the nearest group above it with a
        // CPU set); with no CPU set to read, the online CPUs (what a thread nobody pinned may use)
        static int[] CgroupCpus()
        {
            try
            {
                string v2 = null, v1set = null;
                foreach (var line in (Read("/proc/self/cgroup") ?? "").Split('\n'))
                {
                    var parts = line.Split(new[] { ':' }, 3);
                    if (parts.Length < 3) continue;
                    if (parts[0] == "0" && parts[1].Length == 0) v2 = parts[2];
                    else if (parts[1].Split(',').Contains("cpuset")) v1set = parts[2];
                }
                if (v2 != null && File.Exists("/sys/fs/cgroup/cgroup.controllers"))
                {
                    foreach (var dir in Upwards("/sys/fs/cgroup", v2))
                    {
                        int[] c = CpuList.Parse(Read(Path.Combine(dir, "cpuset.cpus.effective")));
                        if (c.Length > 0) return c;
                    }
                }
                else if (Directory.Exists("/sys/fs/cgroup/cpuset"))
                {
                    foreach (var dir in Upwards("/sys/fs/cgroup/cpuset", v1set ?? "/"))
                    {
                        int[] c = CpuList.Parse(Read(Path.Combine(dir, "cpuset.effective_cpus")) ?? Read(Path.Combine(dir, "cpuset.cpus")));
                        if (c.Length > 0) return c;
                    }
                }
            }
            catch { }
            return CpuList.Parse(Read("/sys/devices/system/cpu/online"));
        }

        // every CPU the kernel could ever bring online: a thread with this mask uses whatever its cgroup gives it, now and
        // after any change to the cgroup's CPUs
        static byte[] WideMask()
        {
            int[] possible = CpuList.Parse(Read("/sys/devices/system/cpu/possible"));
            return Mask(possible.Length > 0 ? possible : Enumerable.Range(0, MaskBytes * 8));
        }

        // The mask a thread gets back after the test (Linux). Since kernel 6.2 a mask set by hand stays as it was asked for,
        // and a later change to the cgroup's CPUs (systemd's AllowedCPUs, nost-cores) only narrows it, never widens it. So a
        // thread that only had what its cgroup gave it (its mask was the cgroup's CPU set) gets every CPU back, which the
        // kernel cuts to the cgroup's CPUs and keeps following them; only a thread with narrower CPUs of its own (taskset,
        // the game's own pinning) gets its own mask back. A thread made during the test is put back as the process was.
        internal static byte[] RestoreMask(byte[] own, byte[] process, byte[] cpuset, byte[] wide)
        {
            byte[] m = own ?? process;
            if (m == null) return wide;
            return cpuset != null && SameMask(m, cpuset) ? wide : m;
        }

        internal static bool SameMask(byte[] a, byte[] b)
        {
            if (a == null || b == null) return false;
            int n = Math.Max(a.Length, b.Length);
            for (int i = 0; i < n; i++) if ((i < a.Length ? a[i] : 0) != (i < b.Length ? b[i] : 0)) return false;
            return true;
        }

        // the whole server on these CPUs; returns the CPUs its main thread really has afterwards
        internal static int[] Pin(int[] cpus)
        {
            lock (_pinLock)
            {
                _pinned = cpus.ToArray();
                _pinnedSince = true;
                PinNow();
                return Got();
            }
        }

        // threads made since the last pass take their creator's CPUs, so the pin is set again every few seconds
        internal static void Repin()
        {
            lock (_pinLock) { if (_pinned != null) PinNow(); }
        }

        static void PinNow()
        {
            try
            {
                if (IsLinux)
                {
                    var m = Mask(_pinned);
                    foreach (int tid in Tids()) { try { SetMask(tid, m); } catch { } }
                }
                else if (IsWindows) SetProcessAffinityMask(GetCurrentProcess(), (UIntPtr)WinMask(_pinned));
            }
            catch (Exception e) { NukeStatsPlugin.Log?.LogWarning("[selftest] pin: " + e.Message); }
        }

        // Every thread back as before the test (RestoreMask: what its cgroup gives it, or its own narrower CPUs); threads
        // made during it as the process was. Nothing is touched when the test hasn't pinned anything since the snapshot or
        // the last restore. Safe to call from any thread, and more than once.
        internal static int[] Restore()
        {
            lock (_pinLock)
            {
                _pinned = null;
                if (!_pinnedSince) return Got();
                try
                {
                    if (IsLinux && _snapMasks != null)
                    {
                        byte[] wide = WideMask();
                        foreach (int tid in Tids())
                        {
                            byte[] m = RestoreMask(_snapMasks.TryGetValue(tid, out var own) ? own : null, _snapProcess, _snapCpuset, wide);
                            try { SetMask(tid, m); } catch { }
                        }
                    }
                    else if (IsWindows && _snapWinMask != 0) SetProcessAffinityMask(GetCurrentProcess(), (UIntPtr)_snapWinMask);
                    _pinnedSince = false;
                }
                catch (Exception e) { NukeStatsPlugin.Log?.LogWarning("[selftest] CPUs back: " + e.Message); }
                return Got();
            }
        }

        // the CPUs the main thread has now
        static int[] Got()
        {
            try
            {
                if (IsLinux)
                {
                    if (_mainTid > 0)
                    {
                        var m = new byte[MaskBytes];
                        if (GetMask(_mainTid, m) == 0)
                        {
                            var r = new List<int>();
                            for (int c = 0; c < MaskBytes * 8; c++) if ((m[c / 8] & (1 << (c % 8))) != 0) r.Add(c);
                            return r.ToArray();
                        }
                    }
                    return StatusCpus();
                }
                if (IsWindows) return WinProcessCpus();
            }
            catch { }
            return new int[0];
        }

        // ---------------------------------------------------------------- the core speed test (the tester's CoreBench)
        // One background thread pinned to each core in turn runs the same work as the tester's speed test; twice per
        // core, the better kept. Run while the server waits with nobody on, so it has the machine to itself.
        internal static bool Bench(CpuSnapshot cpu, Func<bool> cancelled)
        {
            var scores = new double[cpu.Cores.Count];
            for (int i = 0; i < cpu.Cores.Count; i++)
            {
                if (cancelled()) return false;
                int target = cpu.Cores[i].Cpus[0];
                if (IsLinux) SetMask(0, Mask(new[] { target }));   // 0 = this thread
                else if (IsWindows && target < 64) SetThreadAffinityMask(GetCurrentThread(), (UIntPtr)(1UL << target));
                Thread.Sleep(20);   // let the scheduler move the thread
                double best = 0;
                for (int pass = 0; pass < 2; pass++) best = Math.Max(best, CoreSpeed.Work(CoreSpeed.SecondsPerCore / 2));
                scores[i] = best;
            }
            for (int i = 0; i < scores.Length; i++) cpu.Cores[i].Score = scores[i];
            CoreSpeed.ClassifyBySpeed(cpu.Cores);
            return true;
        }

        // ---------------------------------------------------------------- once a second while a test runs
        struct S { public DateTime T; public double CpuSec, Mhz, Temp; public int Cpu; public long Throttles; }
        static readonly List<S> _s = new List<S>();
        static string _tempFile;
        static string[] _throttleFiles;
        static bool _monitorReady;
        internal static volatile int MainCpuWindows = -1;   // Windows: the CPU the main thread was last on (the main thread asks)

        internal static int CurrentProcessorWindows() { try { return (int)GetCurrentProcessorNumber(); } catch { return -1; } }

        static void MonitorSetup()
        {
            _monitorReady = true;
            if (!IsLinux) return;
            try
            {
                foreach (var h in Directory.GetDirectories("/sys/class/hwmon"))
                {
                    string name = Read(Path.Combine(h, "name"));
                    if (name == "coretemp" || name == "k10temp" || name == "zenpower")
                    {
                        string f = Path.Combine(h, "temp1_input");
                        if (File.Exists(f)) { _tempFile = f; break; }
                    }
                }
            }
            catch { }
            var files = new List<string>();
            foreach (int c in CpuList.Parse(Read("/sys/devices/system/cpu/online")))
                foreach (var n in new[] { "core_throttle_count", "package_throttle_count" })
                {
                    string f = "/sys/devices/system/cpu/cpu" + c.ToString(Inv) + "/thermal_throttle/" + n;
                    if (File.Exists(f)) files.Add(f);
                }
            _throttleFiles = files.ToArray();
        }

        internal static void ClearSamples() { lock (_s) _s.Clear(); }

        internal static void Sample()
        {
            if (!_monitorReady) MonitorSetup();
            var s = new S { T = DateTime.UtcNow, CpuSec = double.NaN, Mhz = double.NaN, Temp = double.NaN, Cpu = -1, Throttles = -1 };
            try
            {
                if (IsLinux)
                {
                    // utime + stime, in the kernel's 100 ticks a second
                    string stat = Read("/proc/self/stat");
                    if (stat != null)
                    {
                        var f = stat.Substring(stat.LastIndexOf(')') + 2).Split(' ');
                        if (f.Length > 12 && long.TryParse(f[11], NumberStyles.Integer, Inv, out long ut) && long.TryParse(f[12], NumberStyles.Integer, Inv, out long st)) s.CpuSec = (ut + st) / 100.0;
                    }
                    if (_mainTid > 0)
                    {
                        string ts = Read("/proc/self/task/" + _mainTid.ToString(Inv) + "/stat");
                        if (ts != null)
                        {
                            var f = ts.Substring(ts.LastIndexOf(')') + 2).Split(' ');
                            if (f.Length > 36 && int.TryParse(f[36], NumberStyles.Integer, Inv, out int cpu)) s.Cpu = cpu;
                        }
                    }
                    if (s.Cpu >= 0)
                    {
                        long k = ReadLong("/sys/devices/system/cpu/cpu" + s.Cpu.ToString(Inv) + "/cpufreq/scaling_cur_freq");
                        if (k > 0) s.Mhz = k / 1000.0;
                    }
                    // throttle counts over every CPU: each CPU counts its own since boot, so comparing the CPU the main thread
                    // was on at the start of a measurement with the one at its end would read a move as throttling
                    if (_throttleFiles != null && _throttleFiles.Length > 0)
                    {
                        long sum = 0;
                        foreach (var f in _throttleFiles) sum += Math.Max(0, ReadLong(f, 0));
                        s.Throttles = sum;
                    }
                    if (_tempFile != null) { long mc = ReadLong(_tempFile); if (mc > 0) s.Temp = mc / 1000.0; }
                }
                else
                {
                    using (var p = Process.GetCurrentProcess()) s.CpuSec = p.TotalProcessorTime.TotalSeconds;
                    s.Cpu = MainCpuWindows;
                }
            }
            catch { }
            lock (_s) { _s.Add(s); if (_s.Count > 7200) _s.RemoveRange(0, 3600); }
        }

        // fill in the tester's own numbers for a measurement window (HostMonitor.Attach)
        internal static void Attach(Measurement w)
        {
            List<S> r;
            DateTime a = w.AtUtc.AddSeconds(-w.Sec), b = w.AtUtc.AddSeconds(1);
            lock (_s) r = _s.Where(x => x.T >= a && x.T <= b).ToList();
            if (r.Count < 2) return;
            var first = r[0];
            var last = r[r.Count - 1];
            double dt = (last.T - first.T).TotalSeconds;
            if (dt > 0 && !double.IsNaN(first.CpuSec) && !double.IsNaN(last.CpuSec)) w.CpuPct = (last.CpuSec - first.CpuSec) / dt * 100.0;
            var mhz = r.Where(x => !double.IsNaN(x.Mhz)).Select(x => x.Mhz).ToList();
            if (mhz.Count > 0) w.MainMhz = mhz.Average();
            var temp = r.Where(x => !double.IsNaN(x.Temp)).Select(x => x.Temp).ToList();
            if (temp.Count > 0) w.TempC = temp.Max();
            if (first.Throttles >= 0 && last.Throttles > first.Throttles) w.Throttled = true;
            w.MainCpus = CpuList.Text(r.Where(x => x.Cpu >= 0).Select(x => x.Cpu));
        }
    }
}
