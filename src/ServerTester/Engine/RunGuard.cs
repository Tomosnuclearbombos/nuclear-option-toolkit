using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ServerTester.Engine
{
    // Makes sure a test never leaves the machine changed, even when the tester itself is killed, crashes or loses its
    // SSH session:
    //  - while a test runs, data/run-state.json holds the test server's process id and the power setting to put back;
    //    the next start of the tester (and every test) stops a leftover test server and puts that power setting back;
    //  - on Windows the test server is in a job object that ends it when the tester's process ends, however it ends;
    //  - on Linux SIGTERM and SIGHUP stop the test server and put the power setting back before the tester exits.
    public static class RunGuard
    {
        static string StatePath => Path.Combine(ServerSetup.DataDir, "run-state.json");
        static Action _emergency;
        static bool _hooked;
        static PosixSignalRegistration _term, _hup;

        public static void Begin(int pid, string powerBefore)
        {
            try
            {
                Directory.CreateDirectory(ServerSetup.DataDir);
                File.WriteAllText(StatePath, JsonSerializer.Serialize(new { pid, tester = Environment.ProcessId, power = powerBefore ?? "" }));
            }
            catch { }
        }

        public static void End()
        {
            try { if (File.Exists(StatePath)) File.Delete(StatePath); } catch { }
            _emergency = null;
        }

        // what to do if the tester is told to quit mid-test
        public static void OnEmergency(Action a)
        {
            _emergency = a;
            if (_hooked) return;
            _hooked = true;
            AppDomain.CurrentDomain.ProcessExit += (_, __) => Fire();
            if (!Native.IsWindows)
            {
                try
                {
                    _term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => Fire());
                    _hup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, ctx => Fire());
                }
                catch { }
            }
        }

        static void Fire()
        {
            var a = _emergency;
            _emergency = null;
            try { a?.Invoke(); } catch { }
        }

        // another copy of this tester (same program file) running a test from this folder right now
        public static bool OtherTesterRunning()
        {
            try
            {
                if (!File.Exists(StatePath)) return false;
                int tester;
                using (var doc = JsonDocument.Parse(File.ReadAllText(StatePath)))
                    if (!doc.RootElement.TryGetProperty("tester", out var t) || !t.TryGetInt32(out tester)) return false;
                if (tester == Environment.ProcessId) return false;
                using (var p = Process.GetProcessById(tester))
                {
                    if (p.HasExited) return false;
                    string exe = Native.IsLinux ? new FileInfo("/proc/" + tester + "/exe").LinkTarget : p.MainModule?.FileName;
                    return exe != null && Environment.ProcessPath != null
                           && string.Equals(Path.GetFullPath(exe), Path.GetFullPath(Environment.ProcessPath), Native.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                }
            }
            catch { return false; }   // gone (or not ours to look at): nothing running
        }

        // a test server left over from a tester that didn't get to clean up (never one another copy is still running)
        public static void CleanUpLeftovers(Action<string> log)
        {
            if (OtherTesterRunning()) return;
            string power = null;
            try
            {
                if (File.Exists(StatePath))
                {
                    using (var doc = JsonDocument.Parse(File.ReadAllText(StatePath)))
                        if (doc.RootElement.TryGetProperty("power", out var p) && p.ValueKind == JsonValueKind.String && p.GetString().Length > 0) power = p.GetString();
                }
            }
            catch { }
            int killed = 0;
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    if (!proc.ProcessName.StartsWith("NuclearOption", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!IsOurServer(proc)) continue;
                    proc.Kill(true);
                    killed++;
                }
                catch { }
            }
            if (killed > 0) log?.Invoke("Stopped a test server left running by an earlier run");
            if (power != null) PowerPlan.Restore(power, log ?? (_ => { }));
            End();
            // command files the old server never read would be applied by the next one at start-up
            try
            {
                if (Directory.Exists(ServerSetup.ServerDir))
                    foreach (var f in Directory.GetFiles(ServerSetup.ServerDir, "plugin_cmd_tester*.txt").Concat(Directory.GetFiles(ServerSetup.ServerDir, "tester_tmp_*.txt")))
                        File.Delete(f);
            }
            catch { }
        }

        // is this process the tester's own test server (its program file is in data/server)?
        public static bool IsOurServer(Process p)
        {
            string exe = "";
            try
            {
                if (Native.IsLinux)
                {
                    var fi = new FileInfo("/proc/" + p.Id + "/exe");
                    exe = fi.LinkTarget ?? "";
                }
                else exe = p.MainModule?.FileName ?? "";
            }
            catch { }
            return exe.Length > 0 && exe.StartsWith(ServerSetup.ServerDir, Native.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

        // ---------------------------------------------------------------- Windows: end the server with the tester
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimits info, int length);
        [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [StructLayout(LayoutKind.Sequential)]
        struct BasicLimits
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct IoCounters { public ulong Read, Write, Other, ReadBytes, WriteBytes, OtherBytes; }

        [StructLayout(LayoutKind.Sequential)]
        struct ExtendedLimits
        {
            public BasicLimits Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        static IntPtr _job;

        public static void TieToTester(Process server)
        {
            if (!Native.IsWindows) return;
            try
            {
                if (_job == IntPtr.Zero)
                {
                    _job = CreateJobObject(IntPtr.Zero, null);
                    var info = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x2000 } };   // KILL_ON_JOB_CLOSE
                    SetInformationJobObject(_job, 9, ref info, Marshal.SizeOf<ExtendedLimits>());
                }
                AssignProcessToJobObject(_job, server.Handle);
            }
            catch { }
        }
    }
}
