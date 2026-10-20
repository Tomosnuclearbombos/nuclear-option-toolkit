using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ServerTester.Engine
{
    // Samples the machine once a second while a test runs: the server's CPU use, the clock and temperature of the CPU its
    // main thread is on, and whether the CPU throttled. Heat moved results more than any setting did in testing, so a
    // step that throttled is flagged in the results.
    public sealed class HostMonitor : IDisposable
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        struct S { public DateTime T; public double CpuSec, Mhz, Temp; public int Cpu; public long Throttles; public double PerfLimit; }
        readonly List<S> _s = new List<S>();
        readonly Func<int> _pid;
        string _tempFile;
        object _perfLimit, _perfPct;   // Windows performance counters (created only on Windows)
        double _baseMhz;

        public HostMonitor(Func<int> pid)
        {
            _pid = pid;
            if (Native.IsLinux) _tempFile = FindTemp();
            if (Native.IsWindows) OpenWindowsCounters();
        }

        public void Sample()
        {
            int pid = _pid();
            if (pid <= 0) return;
            var s = new S { T = DateTime.UtcNow, CpuSec = double.NaN, Mhz = double.NaN, Temp = double.NaN, Cpu = -1, Throttles = -1, PerfLimit = double.NaN };
            try { using (var p = Process.GetProcessById(pid)) s.CpuSec = p.TotalProcessorTime.TotalSeconds; } catch { }
            if (Native.IsLinux)
            {
                string stat = Native.Read("/proc/" + pid + "/task/" + pid + "/stat");   // the main thread has the process's own id
                if (stat != null)
                {
                    var f = stat.Substring(stat.LastIndexOf(')') + 2).Split(' ');
                    if (f.Length > 36 && int.TryParse(f[36], out int cpu)) s.Cpu = cpu;
                }
                if (s.Cpu >= 0)
                {
                    long k = Native.ReadLong("/sys/devices/system/cpu/cpu" + s.Cpu + "/cpufreq/scaling_cur_freq");
                    if (k > 0) s.Mhz = k / 1000.0;
                }
                // throttle counts over every CPU: each CPU counts its own since boot, so comparing the CPU the main thread
                // was on at the start of a measurement with the one at its end would read a move as throttling
                long sum = -1;
                for (int c = 0; c < CpuTopology.Threads; c++)
                {
                    long t1 = Native.ReadLong("/sys/devices/system/cpu/cpu" + c + "/thermal_throttle/core_throttle_count");
                    long t2 = Native.ReadLong("/sys/devices/system/cpu/cpu" + c + "/thermal_throttle/package_throttle_count");
                    if (t1 >= 0 || t2 >= 0) sum = Math.Max(0, sum) + Math.Max(0, t1) + Math.Max(0, t2);
                }
                s.Throttles = sum;
                if (_tempFile != null) { long mc = Native.ReadLong(_tempFile); if (mc > 0) s.Temp = mc / 1000.0; }
            }
            else if (Native.IsWindows) ReadWindowsCounters(ref s);
            lock (_s) { _s.Add(s); if (_s.Count > 7200) _s.RemoveRange(0, 3600); }
        }

        // fill in the tester's own numbers for a NukeStats measurement window
        public void Attach(Measurement w)
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
            if (r.Any(x => !double.IsNaN(x.PerfLimit) && x.PerfLimit < 95)) w.Throttled = true;   // Windows: performance limited
            w.MainCpus = Native.ListText(r.Where(x => x.Cpu >= 0).Select(x => x.Cpu));
        }

        // Linux CPU temperature: Intel coretemp or AMD k10temp, the package sensor
        static string FindTemp()
        {
            try
            {
                foreach (var h in Directory.GetDirectories("/sys/class/hwmon"))
                {
                    string name = Native.Read(Path.Combine(h, "name"));
                    if (name == "coretemp" || name == "k10temp" || name == "zenpower")
                    {
                        string f = Path.Combine(h, "temp1_input");
                        if (File.Exists(f)) return f;
                    }
                }
            }
            catch { }
            return null;
        }

        // Windows: "% Performance Limit" drops below 100 when the CPU is held back (heat or power); "% Processor
        // Performance" times the base clock is the real clock.
        void OpenWindowsCounters()
        {
            try
            {
                _perfLimit = new PerformanceCounter("Processor Information", "% Performance Limit", "_Total", true);
                _perfPct = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total", true);
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                    _baseMhz = Convert.ToDouble(k?.GetValue("~MHz") ?? 0, Inv);
                ((PerformanceCounter)_perfLimit).NextValue();
                ((PerformanceCounter)_perfPct).NextValue();
            }
            catch { _perfLimit = null; _perfPct = null; }
        }

        void ReadWindowsCounters(ref S s)
        {
            try
            {
                if (_perfLimit is PerformanceCounter pl) s.PerfLimit = pl.NextValue();
                if (_perfPct is PerformanceCounter pp && _baseMhz > 0) s.Mhz = pp.NextValue() / 100.0 * _baseMhz;
            }
            catch { }
        }

        public void Dispose()
        {
            try { (_perfLimit as IDisposable)?.Dispose(); (_perfPct as IDisposable)?.Dispose(); } catch { }
        }
    }
}
