using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ServerTester.Engine;

namespace ServerTester
{
    // Putting the results on your real server: through the NukeStats dashboard (the webcc), by hand, and the CPUs.
    public sealed class ApplyWindow : Avalonia.Controls.Window
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public ApplyWindow(Results r)
        {
            Title = "Apply the results";
            Width = 560; Height = 640; CanResize = true;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var p = new StackPanel { Margin = new Thickness(12), Spacing = 8 };
            string json = r.Folder != null ? Path.Combine(r.Folder, "results.json") : "";

            p.Children.Add(Header("1. With the NukeStats dashboard (webcc)"));
            p.Children.Add(Para("In the dashboard open Settings, press \"Apply Server Tester results\" and pick this file. It shows the "
                                + "settings below and applies them to the plugin straight away, no restart."));
            p.Children.Add(Copyable(json));
            p.Children.Add(new Button { Content = "Open the results folder", Command = new Cmd(() => Open(r.Folder)) });

            p.Children.Add(Header("2. By hand"));
            var lines = new List<string> { "[Perf]" };
            if (r.RecWorkers > 0) lines.Add("JobWorkers = " + r.RecWorkers);
            if (r.RecTarget > 0) lines.Add("TargetFps = " + r.RecTarget);
            if (lines.Count > 1)
            {
                p.Children.Add(Para("In BepInEx/config/anz.nukestats.cfg on your server, under [Perf] (with the server stopped):"));
                p.Children.Add(Copyable(string.Join("\n", lines)));
                var without = new List<string>();
                if (r.RecTarget > 0) without.Add("Without NukeStats the frame target is the server's start option -limitframerate " + r.RecTarget + ".");
                if (r.RecWorkers > 0) without.Add("The game sets its worker count at start-up (half the CPU's threads, 2 to 8); only NukeStats can change it.");
                p.Children.Add(Para(string.Join(" ", without)));
            }
            else p.Children.Add(Para("This test suggests no [Perf] settings: it didn't test the physics workers or the frame target, they didn't "
                                     + "finish, or no frame target held. report.txt says which."));

            // CPU numbers belong to the PC the test ran on
            // (Windows and Linux number the threads of a core differently, so the same PC under the other system counts as another)
            bool thisPc = (string.IsNullOrEmpty(r.CpuModel) || r.CpuModel == CpuTopology.Model) && r.RanOnWindows == Native.IsWindows;
            bool onServer = r.Runner == Results.PluginRunner;   // the NukeStats plugin ran it on the server itself
            p.Children.Add(Header(thisPc ? "3. The CPUs (this PC)" : onServer ? "3. The CPUs (the server that ran the test)" : "3. The CPUs (the PC that ran the test)"));
            if (r.RecCores > 0)
            {
                string cpus = Native.ListText(r.RecCpus);
                if (!thisPc) p.Children.Add(Para("These results came from another PC or system (" + r.CpuModel + " on " + (r.RanOnWindows ? "Windows" : "Linux")
                                                 + "), so these lines are for that one."));
                p.Children.Add(Para("Give the server its " + r.RecCores + " fastest cores, CPUs " + cpus + ", and keep other programs off them."));
                if (r.RanOnWindows)
                {
                    p.Children.Add(Para("Start the server from a .bat file with:"));
                    p.Children.Add(Copyable("start \"\" /affinity " + r.AffinityHex + " NuclearOptionServer.exe <your usual options>"));
                }
                else
                {
                    p.Children.Add(Para("A systemd service (put your service's name in):"));
                    p.Children.Add(Copyable("sudo systemctl set-property <your-server>.service AllowedCPUs=" + cpus));
                    p.Children.Add(Para("Or in front of a script's start line:"));
                    p.Children.Add(Copyable("taskset -c " + cpus + " ./NuclearOptionServer.x86_64 ..."));
                }
                if (thisPc)
                {
                    var result = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray };
                    p.Children.Add(new Button { Content = "Pin a server running on this PC now", Command = new Cmd(() => result.Text = PinRunning(r.RecCpus)) });
                    p.Children.Add(result);
                    p.Children.Add(Para("Pinning a running server lasts until it restarts. On a rented server the host decides the CPUs."));
                }
            }
            else if (r.PairPick != null && TestPlan.PairCores(r.PairPick) == "every")
                // the cores and workers round ran on every core, not pinned: a server under a CPU limit
                p.Children.Add(Para(r.Container
                    ? "The host decides the CPUs. This server runs in a container" + (r.QuotaCpus > 0 ? " held to " + r.QuotaCpus.ToString("0.##", Inv) + " CPUs' worth of time" : "")
                      + ", so the test measured the physics workers on every core it can use, not pinned, and there is no core count to set."
                    : "The cores and workers picked ran on every core, not pinned, so there is no core count to set: leave the server as it is."));
            else p.Children.Add(Para("This test suggests no core count: it didn't test the core count, or didn't get as far as every fast core."));

            // CLOSE stays at the bottom; the rest scrolls
            var close = new Button { Content = "CLOSE", Width = 90, HorizontalContentAlignment = HorizontalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right,
                                     Margin = new Thickness(12, 6, 12, 12), Command = new Cmd(Close) };
            var dock = new DockPanel();
            DockPanel.SetDock(close, Dock.Bottom);
            dock.Children.Add(close);
            dock.Children.Add(new ScrollViewer { Content = p });
            Content = dock;
        }

        static Control Header(string t) => MainWindow.Header(t);
        static Control Para(string t) => new TextBlock { Text = t, TextWrapping = TextWrapping.Wrap };

        static Control Copyable(string text)
        {
            var box = new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = App.Mono, FontSize = 12 };
            var copy = new Button { Content = "Copy", VerticalAlignment = VerticalAlignment.Top };
            copy.Click += async (s, e) =>
            {
                var top = GetTopLevel(copy);
                if (top?.Clipboard != null) await top.Clipboard.SetTextAsync(text);
                copy.Content = "Copied";
            };
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,6,Auto") };
            Grid.SetColumn(box, 0); Grid.SetColumn(copy, 2);
            g.Children.Add(box); g.Children.Add(copy);
            return g;
        }

        // the one Nuclear Option server running on this PC (not the tester's own); with more than one, it says which and
        // leaves them, given each server needs its own CPUs
        static string PinRunning(int[] cpus)
        {
            List<Process> servers;
            try
            {
                servers = Process.GetProcesses().Where(x => x.ProcessName.StartsWith("NuclearOptionServer", StringComparison.OrdinalIgnoreCase) && !RunGuard.IsOurServer(x)).ToList();
            }
            catch (Exception e) { return "Couldn't look at the running programs: " + e.Message; }
            if (servers.Count == 0) return "No Nuclear Option server is running on this PC.";
            if (servers.Count > 1)
                return servers.Count + " servers are running (processes " + string.Join(", ", servers.Select(x => x.Id)) + "). Each needs its own CPUs, "
                       + "so pin each by hand with the lines above.";
            var p = servers[0];
            try
            {
                if (Native.IsWindows)
                {
                    ulong m = 0; foreach (int c in cpus) if (c < 64) m |= 1UL << c;
                    p.ProcessorAffinity = (IntPtr)(long)m;
                    return "Pinned process " + p.Id + " to CPUs " + Native.ListText(cpus) + ". This lasts until it restarts.";
                }
                int ok = 0, all = 0;
                foreach (var d in Directory.GetDirectories("/proc/" + p.Id + "/task"))
                    if (int.TryParse(Path.GetFileName(d), out int tid)) { all++; if (Native.LinuxSetThreadAffinity(tid, cpus)) ok++; }
                if (ok == 0) return "Process " + p.Id + " refused (it runs as another user?). Use the systemctl or taskset line above instead.";
                return "Pinned " + ok + " of " + all + " threads of process " + p.Id + " to CPUs " + Native.ListText(cpus) + ". This lasts until it restarts.";
            }
            catch (Exception e) { return "Process " + p.Id + " refused (" + e.Message + "). Use the lines above instead."; }
        }

        static void Open(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;
                var psi = new ProcessStartInfo(Native.IsWindows ? "explorer.exe" : "xdg-open") { UseShellExecute = false };
                psi.ArgumentList.Add(path);
                Process.Start(psi);
            }
            catch { }
        }

        sealed class Cmd : System.Windows.Input.ICommand
        {
            readonly Action _a;
            public Cmd(Action a) { _a = a; }
            public event EventHandler CanExecuteChanged { add { } remove { } }
            public bool CanExecute(object p) => true;
            public void Execute(object p) => _a();
        }
    }
}
