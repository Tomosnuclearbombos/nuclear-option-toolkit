using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ServerTester.Engine
{
    // The private test server's process: start, stop, its log, its CPUs and the plugin's command files.
    public sealed class ServerProcess : IDisposable
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        Process _p;
        long _logPos;
        string _partial = "";
        public int Pid => _p != null && !_p.HasExited ? GamePid() : -1;
        public bool Running { get { try { return _p != null && !_p.HasExited; } catch { return false; } } }
        public event Action<string> ServerLine;     // interesting lines from the game's own log
        public readonly List<Measurement> Windows = new List<Measurement>();
        int _missionLoads;
        public int MissionLoads => _missionLoads;

        public void Start(Action<string> log)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ServerSetup.LogPath));
            if (File.Exists(ServerSetup.LogPath)) File.Delete(ServerSetup.LogPath);
            _logPos = 0;
            var args = new List<string> { "-logFile", ServerSetup.LogPath, "-limitframerate", "60", "-DedicatedServer", ServerSetup.ConfigPath };
            ProcessStartInfo psi;
            if (Native.IsWindows)
            {
                psi = new ProcessStartInfo(ServerSetup.ServerExe) { WorkingDirectory = ServerSetup.ServerDir, UseShellExecute = false, CreateNoWindow = true };
            }
            else
            {
                // BepInEx on Linux starts through its own script (it sets up the loader, then runs the server in its place)
                psi = new ProcessStartInfo("/bin/sh") { WorkingDirectory = ServerSetup.ServerDir, UseShellExecute = false, CreateNoWindow = true };
                psi.ArgumentList.Add(Path.Combine(ServerSetup.ServerDir, "run_bepinex.sh"));
                psi.ArgumentList.Add(ServerSetup.ServerExe);
                psi.Environment["LD_LIBRARY_PATH"] = Path.Combine(ServerSetup.ServerDir, "linux64") + ":" + (Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? "");
                psi.Environment["TZ"] = "UTC";
                psi.Environment["LC_ALL"] = "C.UTF-8";
            }
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            RunGuard.CleanUpLeftovers(log);
            // what the start script and the server print, kept for when the server stops before writing its own log
            try { File.Delete(ServerSetup.LauncherLogPath); } catch { }
            var outFile = new StreamWriter(ServerSetup.LauncherLogPath, false) { AutoFlush = true };
            void Keep(string line) { if (line == null) return; lock (outFile) { try { outFile.WriteLine(line); } catch { } } }
            _p = Process.Start(psi);
            RunGuard.TieToTester(_p);
            _p.OutputDataReceived += (_, e) => Keep(e.Data);
            _p.ErrorDataReceived += (_, e) => Keep(e.Data);
            _p.Exited += (_, __) => { lock (outFile) { try { outFile.Dispose(); } catch { } } };
            _p.EnableRaisingEvents = true;
            _p.BeginOutputReadLine();
            _p.BeginErrorReadLine();
            log("Test server started (process " + _p.Id + ")");
            if (Native.IsWindows)
            {
                try { _p.PriorityClass = ProcessPriorityClass.High; } catch { }
                Native.WindowsNoPowerThrottling(_p.Handle);
            }
        }

        // On Linux the start script replaces itself with the server (same process), or leaves it as its child.
        int GamePid()
        {
            if (!Native.IsLinux) return _p.Id;
            try
            {
                if (File.Exists("/proc/" + _p.Id + "/exe") && (Native.Read("/proc/" + _p.Id + "/comm") ?? "").StartsWith("NuclearOption", StringComparison.Ordinal)) return _p.Id;
                foreach (var d in Directory.GetDirectories("/proc"))
                {
                    if (!int.TryParse(Path.GetFileName(d), out int pid)) continue;
                    string stat = Native.Read(d + "/stat");
                    if (stat == null) continue;
                    var f = stat.Substring(stat.LastIndexOf(')') + 2).Split(' ');
                    if (f.Length > 1 && f[1] == _p.Id.ToString(Inv) && (Native.Read(d + "/comm") ?? "").StartsWith("NuclearOption", StringComparison.Ordinal)) return pid;
                }
            }
            catch { }
            return _p.Id;
        }

        public async Task Stop(Action<string> log)
        {
            if (!Running) return;
            int pid = Pid;
            log("Stopping the test server");
            if (Native.IsLinux && pid > 0) Native.LinuxSignal(pid, 15);
            for (int i = 0; i < 40 && Running; i++) await Task.Delay(500);
            if (Running) { try { _p.Kill(true); } catch { } }
        }

        public void Dispose() { try { if (Running) _p.Kill(true); } catch { } }

        // ---------------------------------------------------------------- the plugin's command files
        // NukeStats reads plugin_cmd_*.txt in the server folder about once a second; setcfg changes a setting live.
        static int _cmdSeq;

        public void SetConfig(string key, string value)
        {
            // written under another name and renamed, so the plugin never sees it half-written
            string stamp = DateTime.UtcNow.Ticks.ToString(Inv) + "_" + System.Threading.Interlocked.Increment(ref _cmdSeq).ToString(Inv);
            string tmp = Path.Combine(ServerSetup.ServerDir, "tester_tmp_" + stamp + ".txt");
            File.WriteAllText(tmp, "setcfg|" + key + "|" + value + "\n");
            File.Move(tmp, Path.Combine(ServerSetup.ServerDir, "plugin_cmd_tester" + stamp + ".txt"));
        }

        // ---------------------------------------------------------------- CPUs
        // The whole server on a set of CPUs. Windows: the process mask, which moves every thread at once and takes effect
        // straight away. Linux: every thread of the process, one by one (a thread keeps its own mask). New threads take
        // their creator's mask, so it is set again during each step. Returns the CPUs the server really has afterwards.
        public int[] SetCpus(int[] cpus)
        {
            try
            {
                if (!Running) return new int[0];
                if (Native.IsWindows)
                {
                    ulong m = 0;
                    foreach (int c in cpus) if (c < 64) m |= 1UL << c;
                    _p.ProcessorAffinity = (IntPtr)(long)m;
                    _p.Refresh();
                    ulong got = (ulong)(long)_p.ProcessorAffinity;
                    return Enumerable.Range(0, 64).Where(i => (got & (1UL << i)) != 0).ToArray();
                }
                int pid = Pid;
                foreach (var d in Directory.GetDirectories("/proc/" + pid + "/task"))
                    if (int.TryParse(Path.GetFileName(d), out int tid)) Native.LinuxSetThreadAffinity(tid, cpus);
                foreach (var line in File.ReadAllLines("/proc/" + pid + "/status"))
                    if (line.StartsWith("Cpus_allowed_list:", StringComparison.Ordinal)) return Native.ParseList(line.Substring(18));
            }
            catch { }
            return new int[0];
        }

        // ---------------------------------------------------------------- the log
        // Reads what the server has added since the last look; the server keeps the file open, so it is shared.
        public List<Measurement> ReadNew(HostMonitor host)
        {
            var got = new List<Measurement>();
            try
            {
                if (!File.Exists(ServerSetup.LogPath)) return got;
                using (var fs = new FileStream(ServerSetup.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (fs.Length < _logPos) _logPos = 0;
                    fs.Seek(_logPos, SeekOrigin.Begin);
                    var buf = new byte[fs.Length - _logPos];
                    int n = fs.Read(buf, 0, buf.Length);
                    _logPos += n;
                    string text = _partial + Encoding.UTF8.GetString(buf, 0, n);
                    int cut = text.LastIndexOf('\n');
                    if (cut < 0) { _partial = text; return got; }
                    _partial = text.Substring(cut + 1);
                    foreach (var raw in text.Substring(0, cut).Split('\n'))
                    {
                        string line = raw.TrimEnd('\r');
                        int i = line.IndexOf("[NOSTATS] {\"t\":\"perf\"", StringComparison.Ordinal);
                        if (i >= 0)
                        {
                            var w = Parse(line.Substring(i + 10));
                            if (w != null) { host?.Attach(w); got.Add(w); Windows.Add(w); }
                            continue;
                        }
                        if (line.Contains("[DedicatedServerManager] Loading next Mission")) _missionLoads++;
                        if (line.Contains("[DedicatedServerManager]") || line.Contains("[tester]") || line.Contains("Exception") || line.Contains("[Error"))
                            ServerLine?.Invoke(line.Trim());
                    }
                }
            }
            catch { }
            return got;
        }

        static Measurement Parse(string json)
        {
            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    var r = doc.RootElement;
                    double N(JsonElement o, string k) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : double.NaN;
                    JsonElement Obj(string k) => r.TryGetProperty(k, out var v) ? v : default;
                    var w = new Measurement { At = DateTime.Now, AtUtc = DateTime.UtcNow, Sec = N(r, "sec") };
                    if (!(w.Sec > 0)) return null;
                    w.Fps = N(r, "frames") / w.Sec;
                    w.P95 = N(r, "ft_p95");
                    w.FtMax = N(r, "ft_max");
                    w.P99 = N(r, "ft_p99");
                    w.Ai = (int)Zero(N(r, "ai_aircraft"));
                    w.Humans = (int)Zero(N(r, "humans"));
                    w.Units = (int)Zero(N(r, "units"));
                    w.Workers = (int)Zero(N(r, "workers"));
                    w.Target = (int)Zero(N(r, "target_fps"));
                    w.Physics = N(Obj("stages"), "FixedUpdate.PhysicsFixedUpdate");
                    w.Spare = N(Obj("phases"), "TimeUpdate");
                    w.FixedPhase = N(Obj("phases"), "FixedUpdate");
                    w.JobsWait = N(Obj("ms"), "jobs_wait");
                    if (double.IsNaN(w.Spare)) return null;   // timing not fully on yet
                    return w;
                }
            }
            catch { return null; }
        }

        static double Zero(double v) => double.IsNaN(v) ? 0 : v;
    }
}
