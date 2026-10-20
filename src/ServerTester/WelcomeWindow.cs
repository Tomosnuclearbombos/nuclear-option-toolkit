using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ServerTester.Engine;

namespace ServerTester
{
    // The first time the tester opens (no results yet): how it works, and a baseline to start with, the server as it runs
    // out of the box. True from ShowDialog means take the baseline now, on the cores in Cores.
    public sealed class WelcomeWindow : Avalonia.Controls.Window
    {
        readonly ComboBox _cores = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        public int Cores => System.Math.Max(0, _cores.SelectedIndex);

        public WelcomeWindow(string baseline, IList<string> cores, int core, string time, bool installed)
        {
            Title = "Welcome to the Server Tester";
            Width = 540; Height = 560; CanResize = true;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var p = new StackPanel { Margin = new Thickness(12), Spacing = 8 };
            p.Children.Add(MainWindow.Header("Start with a baseline"));
            p.Children.Add(Para("This tests how your PC runs a Nuclear Option dedicated server and suggests settings for it. It runs its own "
                                + "copy of the server, hidden with a random password, so it won't touch a server you already run."));
            p.Children.Add(Para("First, take a baseline: the Stress Test on every core, with one worker fewer than the cores. "
                                + "Every core and worker setting the tests try is measured against these settings, and the graph can set later "
                                + "tests against this baseline too, so take it before you change anything."));
            p.Children.Add(new Border
            {
                BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Padding = new Thickness(8, 6), CornerRadius = new CornerRadius(3),
                Child = new TextBlock { Text = CpuTopology.Model + "\n" + baseline, TextWrapping = TextWrapping.Wrap, FontFamily = App.Mono, FontSize = 12 },
            });
            _cores.ItemsSource = cores;
            _cores.SelectedIndex = core;
            p.Children.Add(new StackPanel { Spacing = 2, Children = { new TextBlock { Text = "Cores", FontSize = 12 }, _cores } });
            p.Children.Add(new TextBlock { Text = "If your server won't have every core, pick its cores here.",
                                           Foreground = Brushes.Gray, FontSize = 12, TextWrapping = TextWrapping.Wrap });
            p.Children.Add(Para("It takes about " + time + (installed ? "" : ", plus the server download (about 1 GB)")
                                + ". Keep the PC otherwise idle."));
            p.Children.Add(Para("After that, the automated test finds the cores, physics workers and frame target your server needs, and "
                                + "Apply results puts them on your server. Hover an option's ? to see what it does."));

            var take = new Button { Content = "TAKE A BASELINE", MinWidth = 140, HorizontalContentAlignment = HorizontalAlignment.Center };
            take.Click += (_, __) => Close(true);
            var later = new Button { Content = "NOT NOW", Width = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
            later.Click += (_, __) => Close(false);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right,
                                           Margin = new Thickness(12, 6, 12, 12), Children = { take, later } };
            var dock = new DockPanel();
            DockPanel.SetDock(buttons, Dock.Bottom);
            dock.Children.Add(buttons);
            dock.Children.Add(new ScrollViewer { Content = p });
            Content = dock;
        }

        static Control Para(string t) => new TextBlock { Text = t, TextWrapping = TextWrapping.Wrap };
    }
}
