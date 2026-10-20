using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Simple;
using ServerTester.Engine;

namespace ServerTester
{
    public sealed class App : Application
    {
        public const string Version = "1.0.0";
        // the first of these the system has: lines of numbers and the log stay in columns
        public static readonly FontFamily Mono = new FontFamily("Cascadia Mono, Consolas, DejaVu Sans Mono, Noto Sans Mono, Liberation Mono, monospace");

        public override void Initialize()
        {
            Styles.Add(new SimpleTheme());
            RequestedThemeVariant = ThemeVariant.Default;   // follows the system's light or dark setting
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d)
            {
                var w = new MainWindow();
                w.Opened += (_, __) => w.WelcomeIfFirstTime();   // the first time: how it works, and a baseline to start with
                d.MainWindow = w;
            }
            base.OnFrameworkInitializationCompleted();
        }
    }

    internal static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            CpuTopology.Detect();
            CpuSnapshot.Local = CpuTopology.Snapshot;   // the test plan's cores, for a test that runs on this PC
            Results.ToolVersion = App.Version;
            if (args.Contains("--cli"))
            {
                Cli.AttachConsoleOnWindows();
                return Cli.Run(args).GetAwaiter().GetResult();
            }
            int shot = Array.IndexOf(args, "--screenshot");
            if (shot >= 0)
            {
                if (shot + 1 >= args.Length) { Console.Error.WriteLine("--screenshot needs a file name"); return 2; }
                return Screenshot(args[shot + 1], args);
            }
            RunGuard.CleanUpLeftovers(null);
            BuildApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }

        static AppBuilder BuildApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();

        // Renders the window off-screen to a PNG, for checking the layout without opening anything on a desktop:
        //   --screenshot out.png [--dark] [--advanced] [--demo] [--load <results folder>]... [--tab N] [--height H] [--hover x,y]
        //                        [--cmp N] [--only 3,4]   (the test above the graph, and the settings ticked)
        //                        [--apply]   (the Apply window for the newest results loaded)
        //                        [--tests cores,workers,target,ai]   (the tests ticked)
        //                        [--welcome]   (the first-time window)
        //                        [--confirm]   (START's check of the settings)
        //                        [--open [folder]]   (the Open results list, of data/results or that folder)
        static int Screenshot(string file, string[] a)
        {
            AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .WithInterFont().SetupWithoutStarting();
            if (a.Contains("--dark")) Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
            var w = new MainWindow();
            string Arg(string name) { int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
            if (Arg("--height") is string h) w.Height = int.Parse(h, CultureInfo.InvariantCulture);
            if (a.Contains("--advanced")) w.OpenFoldOuts();
            if (Arg("--tests") is string ticks) w.TickTests(ticks.Split(','));
            var loads = new List<string>();
            for (int i = 0; i + 1 < a.Length; i++) if (a[i] == "--load") loads.Add(a[i + 1]);
            if (loads.Count > 0) Console.WriteLine(w.AddFolders(loads));
            if (a.Contains("--demo")) w.LoadDemo(a.Contains("--demo-auto"));
            if (Arg("--tab") is string t) w.ShowTab(int.Parse(t, CultureInfo.InvariantCulture));
            if (Arg("--cmp") != null || Arg("--only") != null)
                w.PickView(Arg("--cmp") is string cm ? int.Parse(cm, CultureInfo.InvariantCulture) : -1, Arg("--only")?.Split(','));
            Avalonia.Controls.Window shown = w;
            if (a.Contains("--apply") && w.ApplyResults != null) shown = new ApplyWindow(w.ApplyResults);
            if (a.Contains("--welcome")) shown = w.Welcome();
            if (a.Contains("--confirm")) shown = w.Confirm();
            if (a.Contains("--open")) shown = w.ResultsChooser(Arg("--open") is string od && !od.StartsWith("--", StringComparison.Ordinal) ? od : null);
            if (shown != w && Arg("--height") is string ah) shown.Height = int.Parse(ah, CultureInfo.InvariantCulture);
            shown.Show();
            for (int i = 0; i < 5; i++) Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            if (Arg("--hover") is string hv)
            {
                var xy = hv.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                w.HoverChart(new Point(xy[0], xy[1]));
                for (int i = 0; i < 3; i++) Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            }
            var frame = shown.CaptureRenderedFrame();
            frame?.Save(file);
            Console.WriteLine(frame != null ? "saved " + file : "no frame");
            return frame != null ? 0 : 1;
        }
    }
}
