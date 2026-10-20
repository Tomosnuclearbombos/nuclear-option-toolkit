using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace ServerTester.Engine
{
    // The operating system calls the tester needs, for Linux and Windows.
    internal static class Native
    {
        internal static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        internal static readonly bool IsLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ---------------------------------------------------------------- Linux
        [DllImport("libc", EntryPoint = "sched_setaffinity", SetLastError = true)]
        static extern int sched_setaffinity(int pid, IntPtr size, byte[] mask);

        [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
        static extern int sys_kill(int pid, int sig);

        const int MaskBytes = 128;   // 1024 CPUs

        static byte[] Mask(IEnumerable<int> cpus)
        {
            var m = new byte[MaskBytes];
            foreach (int c in cpus) if (c >= 0 && c < MaskBytes * 8) m[c / 8] |= (byte)(1 << (c % 8));
            return m;
        }

        // pid 0 = the calling thread
        internal static bool LinuxSetThreadAffinity(int tid, IEnumerable<int> cpus) => sched_setaffinity(tid, (IntPtr)MaskBytes, Mask(cpus)) == 0;

        internal static void LinuxSignal(int pid, int sig) { try { sys_kill(pid, sig); } catch { } }

        internal static string Read(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; } catch { return null; }
        }

        internal static long ReadLong(string path, long def = -1)
        {
            string s = Read(path);
            return s != null && long.TryParse(s.Split(' ')[0], NumberStyles.Integer, Inv, out long v) ? v : def;
        }

        // "0-3,12-19" -> {0,1,2,3,12,...}
        internal static int[] ParseList(string s) => CpuList.Parse(s);

        // {0,1,2,3,12,13} -> "0-3,12-13"
        internal static string ListText(IEnumerable<int> cpus) => CpuList.Text(cpus);

        // ---------------------------------------------------------------- Windows
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentThread();
        [DllImport("kernel32.dll", SetLastError = true)] static extern UIntPtr SetThreadAffinityMask(IntPtr thread, UIntPtr mask);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetLogicalProcessorInformationEx(int rel, IntPtr buf, ref uint len);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState info, int size);

        [StructLayout(LayoutKind.Sequential)]
        struct PowerThrottlingState { public uint Version, ControlMask, StateMask; }

        internal static bool WindowsPinThisThread(int cpu) =>
            cpu >= 0 && cpu < 64 && SetThreadAffinityMask(GetCurrentThread(), (UIntPtr)(1UL << cpu)) != UIntPtr.Zero;

        // Windows 11 treats a process without a window as background work and may slow it (lower clocks, efficient
        // cores, coarse timers). This tells it not to, for the server process only (ProcessPowerThrottling, class 4).
        internal static bool WindowsNoPowerThrottling(IntPtr process)
        {
            var s = new PowerThrottlingState { Version = 1, ControlMask = 0x1 | 0x4, StateMask = 0 };
            try { return SetProcessInformation(process, 4, ref s, Marshal.SizeOf<PowerThrottlingState>()); } catch { return false; }
        }
    }
}
