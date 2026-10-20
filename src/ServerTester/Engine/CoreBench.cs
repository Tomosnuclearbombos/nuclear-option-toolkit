using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace ServerTester.Engine
{
    // The built-in core speed test. One thread is pinned to each core in turn and runs the same mixed integer and
    // floating point loop for a fixed time; the work it gets through is that core's score. On a hybrid chip the fast
    // and efficient cores come out as two clear groups, and the fastest few of the fast cores are the ones the server's
    // main thread should get. It runs one core at a time, so the machine should be otherwise idle while it runs.
    public static class CoreBench
    {
        public const double SecondsPerCore = CoreSpeed.SecondsPerCore;

        // progress(done, total, text); returns false if cancelled, and then leaves the earlier scores as they were
        public static bool Run(Action<int, int, string> progress, CancellationToken cancel)
        {
            var cores = CpuTopology.Cores;
            int total = cores.Count;
            var scores = new double[total];
            for (int i = 0; i < cores.Count; i++)
            {
                if (cancel.IsCancellationRequested) return false;
                var core = cores[i];
                progress?.Invoke(i, total, "timing core " + (i + 1) + " of " + total + " (CPU " + core.Cpus[0] + ")");
                double best = 0;
                // twice, keep the better: the first pass can land while the clock is still ramping up
                for (int pass = 0; pass < 2; pass++)
                {
                    double s = TimeOn(core.Cpus[0], SecondsPerCore / 2);
                    if (s > best) best = s;
                }
                scores[i] = best;
            }
            if (cancel.IsCancellationRequested) return false;
            for (int i = 0; i < total; i++) cores[i].Score = scores[i];
            ClassifyBySpeed();
            progress?.Invoke(total, total, "done");
            return true;
        }

        // When the operating system does not say which cores are fast (a virtual machine, an older kernel), two groups of
        // scores far apart are taken as fast and efficient cores.
        static void ClassifyBySpeed()
        {
            if (CoreSpeed.ClassifyBySpeed(CpuTopology.Cores)) CpuTopology.TypesFromSpeedTest = true;
        }

        static double TimeOn(int cpu, double seconds)
        {
            double result = 0;
            var t = new Thread(() =>
            {
                if (Native.IsLinux) Native.LinuxSetThreadAffinity(0, new[] { cpu });
                else if (Native.IsWindows) Native.WindowsPinThisThread(cpu);
                Thread.Sleep(20);   // let the scheduler move the thread
                result = Work(seconds);
            }) { IsBackground = true, Priority = ThreadPriority.Highest };
            t.Start();
            t.Join();
            return result;
        }

        static double Work(double seconds) => CoreSpeed.Work(seconds);
    }
}
