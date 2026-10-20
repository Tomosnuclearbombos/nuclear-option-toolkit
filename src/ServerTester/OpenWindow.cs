using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ServerTester.Engine;

namespace ServerTester
{
    // Open results: the tests in a folder (data/results to start with), each saying what it was, to tick the ones to put
    // on the graph. The system's folder picker can't pick several folders everywhere (KDE's picks one), so the ticking
    // happens here and the picker only finds another folder.
    public sealed class OpenWindow : Avalonia.Controls.Window
    {
        readonly HashSet<string> _onGraph;
        readonly int _room;
        readonly TextBlock _where = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 12 };
        readonly StackPanel _list = new StackPanel { Spacing = 8, Margin = new Thickness(12, 4, 12, 4) };
        readonly TextBlock _note = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 12, Margin = new Thickness(12, 6, 12, 0) };
        readonly Button _open = new Button { Content = "OPEN", Width = 90, HorizontalContentAlignment = HorizontalAlignment.Center, IsEnabled = false };
        readonly List<(CheckBox box, string folder, bool readable)> _rows = new List<(CheckBox, string, bool)>();

        public OpenWindow(string folder, IEnumerable<string> onGraph, int room)
        {
            Title = "Open test results";
            Width = 560; Height = 560; CanResize = true;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            _onGraph = new HashSet<string>(onGraph.Select(Path.GetFullPath));
            _room = Math.Max(0, room);

            var other = new Button { Content = "Other folder..." };
            other.Click += async (_, __) => await Browse();
            var cancel = new Button { Content = "CANCEL", Width = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
            cancel.Click += (_, __) => Close(null);
            _open.Click += (_, __) => Close(Ticked().ToList());

            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _open, cancel } };
            var buttons = new DockPanel { Margin = new Thickness(12, 8, 12, 12) };
            DockPanel.SetDock(right, Dock.Right);
            buttons.Children.Add(right);
            buttons.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { other } });

            var top = new StackPanel { Margin = new Thickness(12, 4, 12, 4), Spacing = 4, Children = { MainWindow.Header("Tick the tests to open"), _where } };
            var dock = new DockPanel();
            DockPanel.SetDock(top, Dock.Top);
            DockPanel.SetDock(buttons, Dock.Bottom);
            DockPanel.SetDock(_note, Dock.Bottom);
            dock.Children.Add(top);
            dock.Children.Add(buttons);
            dock.Children.Add(_note);
            dock.Children.Add(new ScrollViewer { Content = _list });
            Content = dock;
            List(folder);
        }

        // the tests in a folder, newest first: what each tested and its settings, then the PC and when
        void List(string folder)
        {
            _rows.Clear();
            _list.Children.Clear();
            var tests = MainWindow.TestFolders(new[] { folder });
            _where.Text = "In " + folder;
            if (tests.Count == 0)
                _list.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "No test results in this folder. Tests save them in data/results. "
                                                   + "For results from another PC, press Other folder... and pick where you copied them." });
            foreach (var t in tests)
            {
                string title, detail;
                bool readable = true;
                try
                {
                    var r = Results.Load(t, out var steps, out _);
                    var run = new RunData { Folder = t, Steps = steps, Results = r, Options = r.Options, Cpu = r.CpuModel ?? "", Date = r.Date };
                    title = Settings.Title(run);
                    detail = Settings.Detail(run);
                }
                catch (Exception e) { title = Path.GetFileName(t); detail = "Couldn't read it: " + e.Message; readable = false; }
                bool there = _onGraph.Contains(t);
                var box = new CheckBox
                {
                    IsChecked = there, IsEnabled = readable && !there, VerticalContentAlignment = VerticalAlignment.Top,
                    Content = new StackPanel { Children =
                    {
                        new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap },
                        new TextBlock { Text = detail + (there ? ", on the graph already" : ""), FontSize = 12, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap },
                    } },
                };
                box.IsCheckedChanged += (_, __) => Update();
                _rows.Add((box, t, readable));
                _list.Children.Add(box);
            }
            Update();
        }

        IEnumerable<string> Ticked() => _rows.Where(r => r.box.IsChecked == true && !_onGraph.Contains(r.folder)).Select(r => r.folder);

        // the graph holds Palette.MaxRuns tests: once the ticks fill it, the rest can't be ticked
        void Update()
        {
            int n = Ticked().Count(), left = _room - n;
            foreach (var r in _rows)
                if (!_onGraph.Contains(r.folder) && r.box.IsChecked != true) r.box.IsEnabled = r.readable && left > 0;
            _open.IsEnabled = n > 0;
            _note.Text = _room == 0 ? "The graph is full (" + Palette.MaxRuns + " tests): take one off with its × first."
                : (n == 0 ? "Nothing ticked. " : n + " ticked. ")
                  + (left == 0 ? "That fills the graph (" + Palette.MaxRuns + " tests)." : "Room for " + left + " more on the graph.");
        }

        async Task Browse()
        {
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "A test's results folder, or the folder that holds several",
            });
            string p = picked.Select(f => f.TryGetLocalPath()).FirstOrDefault(x => x != null);
            if (p != null) List(p);
        }
    }
}
