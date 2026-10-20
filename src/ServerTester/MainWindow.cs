using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Platform.Storage;
using ServerTester.Engine;

namespace ServerTester
{
    // One plain window, top to bottom like Rufus: the test server, the CPU, the test options, the graph, then the status
    // and the buttons.
    public sealed class MainWindow : Window
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        readonly TextBlock _serverStatus = new TextBlock { TextWrapping = TextWrapping.Wrap };
        readonly Button _install = new Button { Content = "INSTALL", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        readonly TextBlock _cpuSummary = new TextBlock { TextWrapping = TextWrapping.Wrap };
        // the CPU's cores, fastest first, each ticked when the tests may use it
        readonly StackPanel _cores = new StackPanel();
        readonly HashSet<int> _untickedCores = new HashSet<int>();
        // the automated test (the rounds, each on the best of the last) or the tests picked by hand
        readonly RadioButton _mAuto = new RadioButton { Content = "Automated test", GroupName = "testmode", IsChecked = true };
        readonly RadioButton _mCustom = new RadioButton { Content = "Pick the tests myself", GroupName = "testmode" };
        static readonly string[] PickWays = { "Pick the best automatically", "Stop and let me pick" };
        readonly ComboBox _pickPair = new ComboBox { ItemsSource = PickWays, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        readonly ComboBox _pickTarget = new ComboBox { ItemsSource = PickWays, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        readonly TextBlock _pairNote = new TextBlock { Foreground = Brushes.Gray, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        readonly NumericUpDown _stepSecs = new NumericUpDown { Minimum = 30, Maximum = 300, Value = 30, Increment = 15, FormatString = "0" };
        // a round of the automated test done and waiting for your pick
        readonly StackPanel _pickRow = new StackPanel { Spacing = 4, IsVisible = false };
        readonly TextBlock _pickText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
        readonly ComboBox _pickBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        readonly Button _pickGo = new Button { Content = "CONTINUE", Width = 110, HorizontalContentAlignment = HorizontalAlignment.Center };
        List<string> _pickKeys = new List<string>();
        TaskCompletionSource<string> _pickWait;
        readonly Button _bench = new Button { Content = "TEST CORE SPEEDS", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        readonly TextBlock _benchStatus = new TextBlock { Foreground = Brushes.Gray, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        readonly CheckBox _tBaseline = new CheckBox { Content = "Baseline", IsChecked = true };
        readonly TextBlock _baseNote = new TextBlock { Foreground = Brushes.Gray, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        readonly CheckBox _tCores = new CheckBox { Content = "Core count", IsChecked = true };
        readonly CheckBox _tWorkers = new CheckBox { Content = "Physics workers", IsChecked = true };
        readonly CheckBox _tTarget = new CheckBox { Content = "Frame target", IsChecked = true };
        readonly CheckBox _tAi = new CheckBox { Content = "AI load", IsChecked = false };
        readonly ComboBox _order = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "Alternate with a baseline (1, b, 2, b, 3, b ...)", "One after another (1, 2, 3, 4 ...)" }, SelectedIndex = 0 };
        readonly ComboBox _baseCores = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        // the baseline's workers: Server Default (index 0), or 1 to the most the CPU allows
        readonly ComboBox _baseWorkers = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        List<string> _baseCoreNames = new List<string>();
        List<int> _baseCoreValues = new List<int> { 0 };   // TestOptions.BaselineCores for each choice: 0 = every core
        readonly NumericUpDown _ai = new NumericUpDown { Minimum = 10, Maximum = 120, Value = 60, Increment = 5, FormatString = "0" };
        readonly ComboBox _mission = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, SelectedIndex = 0, ItemsSource = new[]
        {
            "Stress Test: aircraft only, steady, for finding settings",
            "Realistic Benchmark: full battle with ground units and ships, for checking settings",
        } };
        readonly NumericUpDown _tStart = new NumericUpDown { Minimum = 60, Maximum = 240, Value = 60, Increment = 5, FormatString = "0" };
        readonly NumericUpDown _tStep = new NumericUpDown { Minimum = 1, Maximum = 90, Value = 10, Increment = 5, FormatString = "0" };
        readonly NumericUpDown _tSteps = new NumericUpDown { Minimum = 2, Maximum = 20, Value = 7, Increment = 1, FormatString = "0" };
        readonly ComboBox _tOrder = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "One after another", "Alternate with 90 fps", "Alternate with my own target" }, SelectedIndex = 0 };
        readonly NumericUpDown _tBase = new NumericUpDown { Minimum = 60, Maximum = 240, Value = 100, Increment = 10, FormatString = "0", IsEnabled = false, Width = 120 };
        readonly TextBlock _tList = new TextBlock { Foreground = Brushes.Gray, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        readonly NumericUpDown _aiStart = new NumericUpDown { Minimum = 10, Maximum = 200, Value = 60, Increment = 10, FormatString = "0" };
        readonly NumericUpDown _aiStep = new NumericUpDown { Minimum = 5, Maximum = 50, Value = 10, Increment = 5, FormatString = "0" };
        readonly NumericUpDown _aiMax = new NumericUpDown { Minimum = 10, Maximum = 200, Value = 120, Increment = 10, FormatString = "0" };
        readonly TextBlock _aiList = new TextBlock { Foreground = Brushes.Gray, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        readonly NumericUpDown _repeats = new NumericUpDown { Minimum = 1, Maximum = 10, Value = 1, Increment = 1, FormatString = "0" };
        readonly NumericUpDown _compareFps = new NumericUpDown { Minimum = 60, Maximum = 240, Value = TestPlan.CompareTarget, Increment = 10, FormatString = "0" };
        readonly NumericUpDown _spare = new NumericUpDown { Minimum = 0, Maximum = 60, Value = 25, Increment = 5, FormatString = "0" };
        static readonly string[] HeldKeys = { TestPlan.HeldOnAverage, TestPlan.HeldOnLow5, TestPlan.HeldOnLow1 };
        readonly ComboBox _heldBy = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, SelectedIndex = 0,
            ItemsSource = HeldKeys.Select(k => "On " + TestPlan.HeldName(k) + " (" + (TestPlan.HeldShare(k) * 100).ToString("0", CultureInfo.InvariantCulture) + "% of the target or better)").ToArray() };
        readonly CheckBox _power = new CheckBox { Content = "High performance power setting during the test", IsChecked = true };
        readonly CheckBox _skips = new CheckBox { Content = "NukeStats visual skips on (recommended for a live server)", IsChecked = true };
        readonly StackPanel _advanced = new StackPanel { Spacing = 6, IsVisible = false };
        readonly TextBlock _estimate = new TextBlock { Foreground = Brushes.Gray, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        readonly WrapPanel _runChips = new WrapPanel();
        readonly CompareChart _compare = new CompareChart();
        readonly ComboBox _cmpMetric = new ComboBox { ItemsSource = Metric.All, SelectedIndex = 1, HorizontalAlignment = HorizontalAlignment.Stretch };
        readonly ComboBox _cmpKind = new ComboBox { ItemsSource = Settings.KindNames, SelectedIndex = 1, HorizontalAlignment = HorizontalAlignment.Stretch };
        // when the tests on the graph ran different baselines: which one the others are set against
        readonly ComboBox _cmpBase = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        readonly StackPanel _cmpBaseRow = new StackPanel { Spacing = 4, IsVisible = false };
        List<CompareChart.BaseGroup> _cmpBaseGroups = new List<CompareChart.BaseGroup>();
        readonly Dictionary<string, string> _cmpBasePick = new Dictionary<string, string>();   // the baseline picked on each graph
        string _cmpBaseSig = "";
        // the settings of the test picked above the comparison, to tick the ones to compare (kept for each test)
        readonly WrapPanel _cmpTicks = new WrapPanel();
        readonly Dictionary<string, HashSet<string>> _cmpHidden = new Dictionary<string, HashSet<string>>();
        readonly Dictionary<string, HashSet<string>> _cmpSeen = new Dictionary<string, HashSet<string>>();
        string _cmpTickKeys = "";
        readonly TextBox _log = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Height = 330, FontFamily = App.Mono, FontSize = 11 };
        readonly ProgressBar _progress = new ProgressBar { Minimum = 0, Maximum = 1, Value = 1, Height = 26, Foreground = new SolidColorBrush(Color.Parse("#37b24d")) };
        readonly TextBlock _barText = new TextBlock { Text = "READY", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                                                      FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(8, 0) };
        readonly TextBlock _status = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        readonly TextBlock _clock = new TextBlock { Text = "00:00:00", FontSize = 12, FontFamily = App.Mono, VerticalAlignment = VerticalAlignment.Center };
        readonly Button _start = new Button { Content = "START", Width = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        readonly Button _apply = new Button { Content = "Apply results...", IsEnabled = false };
        readonly Button _folder = new Button { Content = "Open results..." };
        readonly Button _close = new Button { Content = "CLOSE", Width = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        readonly DispatcherTimer _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        readonly Stopwatch _watch = new Stopwatch();
        readonly List<RunData> _runs = new List<RunData>();   // the tests on the graph, up to Palette.MaxRuns
        TabControl _tabs;
        internal void ShowTab(int i) { if (_tabs != null) _tabs.SelectedIndex = i; }
        CancellationTokenSource _cancel;
        TestRunner _run;
        Results _results;   // what Apply uses: the test that just finished, or the last one opened
        bool _busy, _testing;   // _testing: a test of ours runs (opened results then wait for it before going to Apply)

        // another copy of the tester running a test from this folder: no install, speed test or test of our own meanwhile
        bool OtherCopyBusy()
        {
            if (!RunGuard.OtherTesterRunning()) return false;
            _status.Text = "Another copy of the tester is running a test from this folder: try again when it finishes";
            return true;
        }

        public MainWindow()
        {
            Title = "Nuclear Option Server Tester " + App.Version;
            Width = 600; Height = 900; MinWidth = 560; MinHeight = 600;
            Content = Layout();
            _compare.Runs = _runs;
            _compare.Kind = Settings.Kinds[_cmpKind.SelectedIndex];
            FillCpu();
            RefreshServer();
            Idle();
            UpdateEstimate();
            foreach (var c in new ToggleButton[] { _tBaseline, _tCores, _tWorkers, _tTarget, _tAi, _mAuto, _mCustom }) c.IsCheckedChanged += (_, __) => UpdateEstimate();
            _cmpBase.SelectionChanged += (_, __) =>
            {
                int i = _cmpBase.SelectedIndex;
                if (i < 0 || i >= _cmpBaseGroups.Count) return;
                _compare.BaselineKey = _cmpBasePick[_compare.Kind] = _cmpBaseGroups[i].Key;
                _compare.Refresh();
            };
            _order.SelectionChanged += (_, __) => UpdateEstimate();
            _baseCores.SelectionChanged += (_, __) => { if (!_baseWorkersPicked) DefaultWorkers(); UpdateEstimate(); };
            _baseWorkers.SelectionChanged += (_, __) => { if (!_settingWorkers && _baseWorkers.SelectedIndex >= 0) _baseWorkersPicked = true; UpdateEstimate(); };
            foreach (var n in new[] { _ai, _stepSecs, _repeats, _tStart, _tStep, _tSteps, _tBase, _aiStart, _aiStep, _aiMax }) n.ValueChanged += (_, __) => UpdateEstimate();
            _tOrder.SelectionChanged += (_, __) => { _tBase.IsEnabled = _tOrder.SelectedIndex == 2; UpdateEstimate(); };
            _cmpMetric.SelectionChanged += (_, __) => { _compare.Metric = (Metric)_cmpMetric.SelectedItem; _compare.Refresh(); };
            _cmpKind.SelectionChanged += (_, __) =>
            {
                _compare.Kind = Settings.Kinds[Math.Max(0, _cmpKind.SelectedIndex)];
                // the measure that test is judged by: against the baseline for cores and workers, frames per second for
                // the frame target, busy time for the AI load
                // the frame target: the measure the test shown was held on (its own rule, else the option here)
                string held = _runs.Where(x => x.Shown && x.Steps.Any(st => st.Kind == "target" && st.Ok)).Select(x => x.Results?.HeldBy).FirstOrDefault(h => h != null)
                              ?? HeldKeys[Math.Max(0, _heldBy.SelectedIndex)];
                _cmpMetric.SelectedItem = _compare.Kind == "target" ? (held == TestPlan.HeldOnLow1 ? Metric.Low1 : held == TestPlan.HeldOnLow5 ? Metric.Low5 : Metric.Fps)
                                        : _compare.Kind == "ai" || _compare.Kind == "baseline" ? Metric.Busy : Metric.VsBaseline;
                _cmpTickKeys = "\0";
                RefreshTicks();
                _compare.Refresh();
            };
            _pickGo.Click += (_, __) => PickMade();
            // seconds per step go in 15 s measurements: a typed 50 becomes 45
            _stepSecs.LostFocus += (_, __) => { var v = _stepSecs.Value ?? 30; var snapped = Math.Max(30, Math.Round(v / 15) * 15); if (snapped != v) _stepSecs.Value = snapped; };
            _pickPair.SelectionChanged += (_, __) => UpdateEstimate();
            _pickTarget.SelectionChanged += (_, __) => UpdateEstimate();
            _install.Click += async (_, __) => await Install();
            _bench.Click += async (_, __) => await Bench();
            _start.Click += async (_, __) => await StartTest(confirm: true);
            _folder.Click += async (_, __) => await PickResults();
            _apply.Click += (_, __) => { if (_results != null) new ApplyWindow(_results).ShowDialog(this); };
            // like Rufus: CLOSE becomes CANCEL while something runs
            _close.Click += (_, __) =>
            {
                if (_busy) { _cancel?.Cancel(); _status.Text = "Cancelling..."; }
                else Close();
            };
            Closing += (_, e) => { if (_busy) { e.Cancel = true; _status.Text = "Press CANCEL first"; } };
            _tick.Tick += (_, __) => _clock.Text = _watch.Elapsed.ToString(@"hh\:mm\:ss", Inv);
            if (Application.Current != null) Application.Current.ActualThemeVariantChanged += (_, __) => RefreshRuns();
            RefreshRuns();
        }

        // ---------------------------------------------------------------- layout
        Control Layout()
        {
            var root = new StackPanel { Margin = new Thickness(12, 4, 12, 12), Spacing = 6 };
            root.Children.Add(Header("Test server"));
            root.Children.Add(Row(_serverStatus, _install));
            root.Children.Add(Header("CPU"));
            root.Children.Add(_cpuSummary);
            root.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children =
                { new TextBlock { Text = "Cores the tests may use", FontSize = 12, VerticalAlignment = VerticalAlignment.Center }, Question(Help.UseCores) } });
            root.Children.Add(new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Child = new ScrollViewer { Height = 112, Content = _cores } });
            root.Children.Add(Row(_benchStatus, _bench));

            root.Children.Add(Header("Test options"));
            root.Children.Add(Above("Test mission", _mission, Help.Mission));
            root.Children.Add(Columns(Above("AI aircraft", _ai, Help.Ai), new Panel()));
            // the automated test, or the tests picked by hand
            root.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Margin = new Thickness(0, 4, 0, 0), Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _mAuto, Question(Help.Automated) } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _mCustom, Question(Help.Custom) } },
            } });
            // each test, or each round of the automated test, with its settings under it: the automated test shows them
            // all, a test picked by hand while it is ticked
            bool Auto() => _mAuto.IsChecked == true;
            root.Children.Add(Block(_tBaseline, "Baseline", Help.Baseline, Help.AutoBaseline,
                new StackPanel { Spacing = 6, Children = { Columns(Above("Baseline cores", _baseCores), Above("Baseline workers", _baseWorkers)), _baseNote } },
                // every test runs on the baseline's cores and workers, so its settings show whenever any test is ticked
                () => Auto() || new[] { _tBaseline, _tCores, _tWorkers, _tTarget, _tAi }.Any(c => c.IsChecked == true), _tCores, _tWorkers, _tTarget, _tAi));
            root.Children.Add(Block(null, "Round 1: cores and physics workers", null, Help.AutoPairs,
                new StackPanel { Spacing = 6, Children = { _pairNote, Above("When the round is done", _pickPair, Help.PickWhen) } }, Auto));
            root.Children.Add(Block(_tCores, null, Help.Cores, null, null, null));
            root.Children.Add(Block(_tWorkers, null, Help.Workers, null, null, null));
            var order = Indented(Above("Order", _order, Help.Order));
            void OrderShown() => order.IsVisible = !Auto() && (_tCores.IsChecked == true || _tWorkers.IsChecked == true);
            foreach (var c in new ToggleButton[] { _tCores, _tWorkers, _mAuto }) c.IsCheckedChanged += (_, __) => OrderShown();
            OrderShown();
            root.Children.Add(order);
            ToolTip.SetTip(_tBase, "Your own baseline target (fps)");
            var pickTarget = Above("When the round is done", _pickTarget, Help.PickWhen);
            _mAuto.IsCheckedChanged += (_, __) => pickTarget.IsVisible = Auto();
            pickTarget.IsVisible = Auto();
            root.Children.Add(Block(_tTarget, "Round 2: frame target on the cores and workers picked", Help.Target, Help.AutoTarget, new StackPanel { Spacing = 6, Children =
            {
                Columns(Above("Start at (fps)", _tStart), Above("Step (fps)", _tStep), Above("Steps (2 to 20)", _tSteps)),
                Above("Order", Row(_tOrder, _tBase), Help.TargetOrder),
                _tList, pickTarget,
            } }, null));
            root.Children.Add(Block(_tAi, "Round 3: AI load on both picks", Help.AiLoad, Help.AutoAi, new StackPanel { Spacing = 6, Children =
            {
                Columns(Above("Start at (AI)", _aiStart), Above("Step (AI)", _aiStep), Above("Up to (AI)", _aiMax)),
                _aiList,
            } }, null));
            // how long each step measures, and how many times the whole test runs, start to finish
            root.Children.Add(Columns(Above("Seconds per step", _stepSecs, Help.StepSeconds), Above("Runs of the whole test", _repeats, Help.Repeats)));
            _advanced.Children.Add(Columns(Above("Compare at (fps)", _compareFps, Help.CompareFps), Above("Spare time wanted (%)", _spare, Help.Spare)));
            _advanced.Children.Add(Above("A frame target counts as held", _heldBy, Help.HeldBy));
            _advanced.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _power, Question(Help.Power) } });
            _advanced.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _skips, Question(Help.Skips) } });
            root.Children.Add(FoldOut("advanced options", _advanced));
            root.Children.Add(_estimate);

            root.Children.Add(Header("Results"));
            root.Children.Add(_runChips);
            var cmpTab = new StackPanel { Spacing = 6, Children =
            {
                Columns(Above("Test", _cmpKind, Help.Graph), Above("Measure", _cmpMetric)),
                _cmpBaseRow, _cmpTicks, _compare,
            } };
            root.Children.Add(_tabs = new TabControl
            {
                ItemsSource = new[]
                {
                    new TabItem { Header = "Comparison", Content = cmpTab },
                    new TabItem { Header = "Log", Content = _log },
                }
            });

            // the status and the buttons stay at the bottom; everything above scrolls if the window is small
            var bottom = new StackPanel { Margin = new Thickness(12, 0, 12, 0), Spacing = 6 };
            bottom.Children.Add(Header("Status"));
            _pickRow.Children.Add(_pickText);
            _pickRow.Children.Add(new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,Auto"), Children = { _pickBox, _pickGo } });
            Grid.SetColumn(_pickGo, 2);
            bottom.Children.Add(_pickRow);
            var bar = new Grid();
            bar.Children.Add(_progress);
            bar.Children.Add(_barText);
            bottom.Children.Add(bar);
            var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,8,Auto,*,Auto,8,Auto"), Margin = new Thickness(0, 2, 0, 8) };
            AddAt(buttons, 0, _apply);
            AddAt(buttons, 2, _folder);
            AddAt(buttons, 4, _start);
            AddAt(buttons, 6, _close);
            bottom.Children.Add(buttons);
            var statusBar = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(12, 4) };
            statusBar.Bind(Border.BorderBrushProperty, statusBar.GetResourceObservable("ThemeBorderMidBrush"));
            var sb = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,Auto") };
            AddAt(sb, 0, _status);
            AddAt(sb, 2, _clock);
            statusBar.Child = sb;
            var foot = new DockPanel();
            DockPanel.SetDock(statusBar, Dock.Bottom);
            foot.Children.Add(statusBar);
            foot.Children.Add(bottom);
            var dock = new DockPanel();
            DockPanel.SetDock(foot, Dock.Bottom);
            dock.Children.Add(foot);
            dock.Children.Add(new ScrollViewer { Content = root });
            return dock;
        }

        // a Rufus section title: bold, with a line running to the right
        internal static Control Header(string text)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,8,*"), Margin = new Thickness(0, 10, 0, 0) };
            AddAt(g, 0, new TextBlock { Text = text, FontSize = 16, FontWeight = FontWeight.SemiBold });
            var line = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 0, 0) };
            line.Bind(Border.BackgroundProperty, line.GetResourceObservable("ThemeBorderMidBrush"));
            AddAt(g, 2, line);
            return g;
        }

        static Control Row(Control left, Control right)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,Auto") };
            AddAt(g, 0, left);
            AddAt(g, 2, right);
            left.VerticalAlignment = VerticalAlignment.Center;
            right.VerticalAlignment = VerticalAlignment.Center;
            return g;
        }

        // the label above its box, as Rufus does, with a ? to hover when there is something to explain
        static Control Above(string label, Control c, string help = null)
        {
            var p = new StackPanel { Spacing = 3 };
            var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center } } };
            if (help != null) title.Children.Add(Question(help));
            p.Children.Add(title);
            p.Children.Add(c);
            return p;
        }

        // the little ? that explains an option when hovered
        static Control Question(string text)
        {
            var q = new Border { Width = 15, Height = 15, CornerRadius = new CornerRadius(7.5), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center,
                                 Background = Brushes.Transparent,
                                 Child = new TextBlock { Text = "?", FontSize = 10, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            q.Bind(Border.BorderBrushProperty, q.GetResourceObservable("ThemeBorderMidBrush"));
            ToolTip.SetTip(q, new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 });
            ToolTip.SetShowDelay(q, 150);
            return q;
        }

        // a test's tick, its ?, and its own settings under it while it is ticked
        // A test: picking the tests yourself, its tick (and ?); in the automated test, its round's title (and ?), or nothing
        // when it isn't a round of it. Its settings show for the automated test and while it is ticked, unless shown says
        // otherwise (shown is checked again when any of also changes).
        Control Block(CheckBox tick, string round, string help, string roundHelp, Control settings, Func<bool> shown, params ToggleButton[] also)
        {
            var title = new TextBlock { Text = round ?? "", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            Control tickHelp = help != null ? Question(help) : null, roundQ = roundHelp != null ? Question(roundHelp) : null;
            if (tick != null) head.Children.Add(tick);
            if (tickHelp != null) head.Children.Add(tickHelp);
            head.Children.Add(title);
            if (roundQ != null) head.Children.Add(roundQ);
            var box = settings != null ? Indented(settings) : null;
            var panel = new StackPanel { Spacing = 4, Children = { head } };
            if (box != null) panel.Children.Add(box);
            void Update()
            {
                bool auto = _mAuto.IsChecked == true;
                if (tick != null) tick.IsVisible = !auto;
                if (tickHelp != null) tickHelp.IsVisible = !auto;
                title.IsVisible = auto && round != null;
                if (roundQ != null) roundQ.IsVisible = auto;
                head.IsVisible = auto ? round != null : tick != null;
                if (box != null) box.IsVisible = shown != null ? shown() : auto ? round != null : tick?.IsChecked == true;
                panel.IsVisible = head.IsVisible || box?.IsVisible == true;   // nothing of it shown: no gap either
            }
            _mAuto.IsCheckedChanged += (_, __) => Update();
            if (tick != null) tick.IsCheckedChanged += (_, __) => Update();
            foreach (var t in also) t.IsCheckedChanged += (_, __) => Update();
            Update();
            return panel;
        }

        static Control Indented(Control c) => new Border { Margin = new Thickness(26, 0, 0, 4), Child = c };

        static Control Columns(params Control[] cs)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(",12,", cs.Select(_ => "*"))) };
            for (int i = 0; i < cs.Length; i++) AddAt(g, i * 2, cs[i]);
            return g;
        }

        // "Show advanced ..." with a small arrow, opening the options under it
        Control FoldOut(string what, Control content)
        {
            var arrow = new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse("M 0,0 L 5,4.5 L 0,9 Z"), Width = 9, Height = 9, Stretch = Stretch.None, VerticalAlignment = VerticalAlignment.Center };
            arrow.Bind(Avalonia.Controls.Shapes.Shape.FillProperty, arrow.GetResourceObservable("ThemeForegroundBrush"));
            var text = new TextBlock { Text = "Show " + what, VerticalAlignment = VerticalAlignment.Center };
            var toggle = new Button
            {
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { arrow, text } },
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0, 2),
            };
            void Set(bool open)
            {
                content.IsVisible = open;
                text.Text = (open ? "Hide " : "Show ") + what;
                arrow.Data = Geometry.Parse(open ? "M 0,0 L 9,0 L 4.5,5 Z" : "M 0,0 L 5,4.5 L 0,9 Z");
            }
            toggle.Click += (_, __) => Set(!content.IsVisible);
            _openFoldOuts += () => Set(true);
            return new StackPanel { Spacing = 6, Children = { toggle, content } };
        }

        Action _openFoldOuts;
        internal void OpenFoldOuts() => _openFoldOuts?.Invoke();

        // the tests ticked, by their saved names (cores, workers, target, ai)
        internal void TickTests(string[] kinds)
        {
            if (kinds.Contains("auto")) { _mAuto.IsChecked = true; return; }
            _mCustom.IsChecked = true;
            _tBaseline.IsChecked = kinds.Contains("baseline"); _tCores.IsChecked = kinds.Contains("cores"); _tWorkers.IsChecked = kinds.Contains("workers");
            _tTarget.IsChecked = kinds.Contains("target"); _tAi.IsChecked = kinds.Contains("ai");
        }

        static void AddAt(Grid g, int col, Control c) { Grid.SetColumn(c, col); g.Children.Add(c); }

        // the bar in the middle: READY when nothing runs, otherwise what is happening, filling as it goes
        // nothing running: READY once the test server is installed, until then that it isn't
        void Idle() => SetBar(ServerSetup.ServerInstalled ? "READY" : "TEST SERVER NOT INSTALLED", ServerSetup.ServerInstalled ? 1 : 0);

        void SetBar(string text, double progress)
        {
            _progress.Value = Math.Max(0, Math.Min(1, progress));
            _barText.Text = text;
            // dark text over the green part, the theme's own text over the empty part
            if (_progress.Value >= 0.5) _barText.Foreground = Brushes.Black;
            else _barText.ClearValue(TextBlock.ForegroundProperty);
        }

        // ---------------------------------------------------------------- the CPU
        void FillCpu()
        {
            int fast = TestPlan.FastCount;
            _cpuSummary.Text = CpuTopology.Model + "\n" + CpuTopology.Cores.Count + " cores, " + CpuTopology.Threads + " threads"
                + (CpuTopology.Hybrid ? ", " + fast + " fast, " + (CpuTopology.Cores.Count - fast) + " efficient" + (CpuTopology.TypesFromSpeedTest ? " (from the speed test)" : "") : "");
            double top = CpuTopology.Cores.Select(c => c.Score).Where(s => !double.IsNaN(s)).DefaultIfEmpty(double.NaN).Max();
            _cores.Children.Clear();
            int rank = 0;
            foreach (var c in CpuTopology.Ranked())
            {
                int index = c.Index;
                var tick = new CheckBox
                {
                    IsChecked = !_untickedCores.Contains(index), Margin = new Thickness(4, 0),
                    Content = new TextBlock { FontFamily = App.Mono, FontSize = 12, Text = string.Format(Inv, "{0,2}.  CPUs {1,-9} {2,-10} {3}", ++rank, c.CpuText, c.Type.Length > 0 ? c.Type : "",
                        double.IsNaN(c.Score) ? (c.MaxMhz > 0 ? "up to " + c.MaxMhz + " MHz" : "") : "speed " + (c.Score / top * 100).ToString("0", Inv) + "%") },
                };
                tick.IsCheckedChanged += (_, __) => { if (tick.IsChecked == true) _untickedCores.Remove(index); else _untickedCores.Add(index); UpdateEstimate(); };
                _cores.Children.Add(tick);
            }
            // the baseline's cores: every core, as a server runs out of the box, or fewer for anyone who doesn't want that
            int keep = _baseCoreValues[Math.Max(0, Math.Min(_baseCoreValues.Count - 1, _baseCores.SelectedIndex))];
            _baseCoreNames = new List<string> { "Server Default (" + CpuTopology.Cores.Count + ")" };
            _baseCoreValues = new List<int> { 0 };
            if (CpuTopology.Hybrid) { _baseCoreNames.Add("Fast cores only (" + fast + ")"); _baseCoreValues.Add(fast); }
            for (int i = 1; i < fast; i++) { _baseCoreNames.Add(i == 1 ? "1 core" : i + " cores"); _baseCoreValues.Add(i); }
            _baseCores.ItemsSource = _baseCoreNames.ToList();
            _baseCores.SelectedIndex = Math.Max(0, _baseCoreValues.IndexOf(keep));
            int keepWorkers = Math.Max(0, _baseWorkers.SelectedIndex);
            _settingWorkers = true;
            _baseWorkers.ItemsSource = new[] { "Server Default" }.Concat(Enumerable.Range(1, TestPlan.MaxWorkers(new TestOptions())).Select(i => i == 1 ? "1 worker" : i + " workers")).ToList();
            _baseWorkers.SelectedIndex = Math.Min(keepWorkers, TestPlan.MaxWorkers(new TestOptions()));
            _settingWorkers = false;
            if (!_baseWorkersPicked) DefaultWorkers();
            _benchStatus.Text = CpuTopology.Cores.Any(c => !double.IsNaN(c.Score)) ? "Cores ranked by the speed test" : "Cores ranked by what the system reports. The speed test checks them (about half a minute).";
        }

        // the baseline workers to start with: one fewer than the baseline cores, until you pick them
        bool _baseWorkersPicked, _settingWorkers;
        void DefaultWorkers()
        {
            _settingWorkers = true;
            _baseWorkers.SelectedIndex = TestPlan.DefaultBaseWorkers(Options());
            _settingWorkers = false;
        }

        void RefreshServer()
        {
            if (!ServerSetup.ServerInstalled)
            {
                _serverStatus.Text = "Not installed. The tester downloads its own copy of the dedicated server (about 1 GB) to\n" + ServerSetup.ServerDir;
                _install.Content = "INSTALL";
            }
            else
            {
                _serverStatus.Text = "Installed, build " + ServerSetup.ServerBuild() + "\n" + ServerSetup.ServerDir;
                _install.Content = "UPDATE";
            }
        }

        TestOptions Options() => new TestOptions
        {
            TestBaseline = _tBaseline.IsChecked == true, TestCores = _tCores.IsChecked == true, TestWorkers = _tWorkers.IsChecked == true, TestTarget = _tTarget.IsChecked == true, TestAiLoad = _tAi.IsChecked == true,
            Alternate = _mAuto.IsChecked == true || _order.SelectedIndex != 1,   // the automated test always alternates
            Mission = _mission.SelectedIndex == 1 ? TestPlan.RealisticBenchmark : TestPlan.StressTest,
            BaselineCores = _baseCoreValues[Math.Max(0, Math.Min(_baseCoreValues.Count - 1, _baseCores.SelectedIndex))],
            BaselineWorkers = Math.Max(0, _baseWorkers.SelectedIndex),   // 0 = Server Default, as installed
            Ai = (int)(_ai.Value ?? new TestOptions().Ai), Repeats = (int)(_repeats.Value ?? 1), KeepWindows = Math.Max(2, (int)Math.Round((_stepSecs.Value ?? 30) / 15)),
            Automated = _mAuto.IsChecked == true, PickPairByHand = _pickPair.SelectedIndex == 1, PickTargetByHand = _pickTarget.SelectedIndex == 1,
            UseCores = _untickedCores.Count == 0 ? null : CpuTopology.Cores.Where(c => !_untickedCores.Contains(c.Index)).Select(c => c.Index).ToArray(),
            HighPower = _power.IsChecked == true, VisualSkips = _skips.IsChecked == true,
            TargetStart = (int)(_tStart.Value ?? 60), TargetStep = (int)(_tStep.Value ?? 10), TargetSteps = (int)(_tSteps.Value ?? 7),
            TargetAlternate = _tOrder.SelectedIndex > 0,
            TargetBaseline = _tOrder.SelectedIndex == 2 ? (int)(_tBase.Value ?? 100) : TestPlan.CompareTarget,
            CompareFps = (int)(_compareFps.Value ?? TestPlan.CompareTarget), SparePct = (int)(_spare.Value ?? 25),
            HeldBy = HeldKeys[Math.Max(0, _heldBy.SelectedIndex)],
            AiStart = (int)(_aiStart.Value ?? 60), AiStep = (int)(_aiStep.Value ?? 10), AiMax = (int)(_aiMax.Value ?? 120),
        };

        void UpdateEstimate()
        {
            var o = Options();
            int steps = TestPlan.StepCount(o);
            double minutes = Minutes(o);
            _estimate.Text = steps + (steps == 1 ? " step" : " steps") + (o.Repeats > 1 ? " (" + o.Repeats + " runs)" : "") + ", about " + Duration(minutes)
                             + ". Keep the PC otherwise idle.";
            var t = o.Targets();
            int asked = Math.Max(2, Math.Min(20, o.TargetSteps));
            _tList.Text = "Frame targets: " + (t.Length > 0 ? string.Join(", ", t) : "none") + (o.TargetAlternate ? ", each followed by " + o.BaselineTarget : "")
                + (t.Length < asked ? " (" + (asked - t.Length) + " over the 240 limit left out)" : "") + ". All are measured.";
            _baseNote.Text = o.Automated || o.TestBaseline ? "" : "Not measured on its own; the other tests run on it.";
            _baseNote.IsVisible = _baseNote.Text.Length > 0;
            var loads = o.AiLoads();
            _aiList.Text = (loads.Length > 0 ? "AI loads: " + string.Join(", ", loads) : "No AI loads: up to " + o.AiMax + " is below the start, " + o.AiFrom)
                + (!o.OnlyAiLoad && o.AiStart < o.Ai ? " (from " + o.Ai + ", the AI aircraft the other tests run with)" : "")
                + ". Runs once, at the end, every load measured.";
            // round 1 of the automated test: how many pairs that is
            var coreValues = TestPlan.CoreValues(o);
            int fastUsable = TestPlan.UsableFast(o).Count, usable = TestPlan.Usable(o).Count;
            _pairNote.Text = "1 to " + fastUsable + " cores" + (usable > fastUsable ? ", then all " + usable + " ticked" : "") + ", each with 1 to "
                + TestPlan.MaxWorkers(o) + " physics workers: " + coreValues.Count * TestPlan.MaxWorkers(o) + " pairs" + (o.Alternate ? ", each followed by the baseline" : "")
                + (TestPlan.RetestCount(o) > 0 ? ", then the best " + Math.Min(TestPlan.RetestPairs, coreValues.Count * TestPlan.MaxWorkers(o)) + " again for 2 minutes each." : ".");
            if (!_busy) _status.Text = _estimate.Text;
        }

        // a step is the measurements it keeps and the one after the change, plus a few minutes to start the server and fill the AI
        static double Minutes(TestOptions o) => (TestPlan.StepSeconds(o) + 6 * 60) / 60.0;

        static string Duration(double minutes)
        {
            int m = (int)Math.Ceiling(minutes);
            return m < 90 ? m + " minutes" : (m / 60) + (m / 60 == 1 ? " hour " : " hours ") + (m % 60) + " minutes";
        }

        void Log(string s) => Dispatcher.UIThread.Post(() =>
        {
            _log.Text = (_log.Text ?? "") + s + "\n";
            _log.CaretIndex = _log.Text.Length;
        });

        void Busy(bool on)
        {
            _busy = on;
            _start.IsEnabled = !on; _install.IsEnabled = !on; _bench.IsEnabled = !on;
            _close.Content = on ? "CANCEL" : "CLOSE";
            _apply.IsEnabled = !on && _results != null;
            if (on) { _watch.Restart(); _clock.Text = "00:00:00"; _tick.Start(); }
            else { _watch.Stop(); _tick.Stop(); }
            RefreshRuns();
        }

        // ---------------------------------------------------------------- buttons
        async Task<bool> Install()
        {
            if (OtherCopyBusy()) return false;
            bool done = false;
            Busy(true);
            _cancel = new CancellationTokenSource();
            try
            {
                SetBar("Installing the test server", 0);
                _status.Text = "Downloading from Steam";
                bool ok = await Task.Run(() => ServerSetup.InstallOrUpdateServer(Log, p => Dispatcher.UIThread.Post(() => SetBar("Downloading " + (p * 100).ToString("0", Inv) + "%", p)), _cancel.Token));
                if (ok) ok = ServerSetup.InstallMods(Log);
                _status.Text = ok ? "Test server ready" : _cancel.IsCancellationRequested ? "Install cancelled" : "Install didn't finish: see the Log tab";
                done = ok && !_cancel.IsCancellationRequested;
            }
            catch (OperationCanceledException) { _status.Text = "Install cancelled"; Log("Install cancelled"); }
            catch (Exception e) { _status.Text = "Install failed: " + e.Message; Log(e.ToString()); }
            Idle();
            Busy(false);
            RefreshServer();
            return done;
        }

        async Task Bench()
        {
            if (OtherCopyBusy()) return;
            Busy(true);
            _cancel = new CancellationTokenSource();
            _status.Text = "Timing each core, keep the PC idle";
            bool done = await Task.Run(() => CoreBench.Run((d, t, s) => Dispatcher.UIThread.Post(() => SetBar(s, t > 0 ? (double)d / t : 0)), _cancel.Token));
            Idle();
            Busy(false);
            FillCpu();
            UpdateEstimate();
            _status.Text = done ? "Speed test done" : "Speed test cancelled; core order unchanged";
        }

        // confirm: START asks first, with the settings the test will run (the welcome's baseline was asked for already)
        async Task StartTest(bool confirm = false)
        {
            if (!ServerSetup.ServerInstalled) { _status.Text = "Install the test server first"; return; }
            if (_untickedCores.Count >= CpuTopology.Cores.Count) { _status.Text = "No cores ticked in the CPU list"; return; }
            if (OtherCopyBusy()) return;
            if (confirm && !await Confirm().ShowDialog<bool>(this)) return;
            if (!ServerSetup.InstallMods(Log)) { _status.Text = "This build has no BepInEx/NukeStats in it"; return; }
            // the new test needs a place on the graph: the oldest one comes off when it is full
            if (_runs.Count >= Palette.MaxRuns)
            {
                var old = OldestRun();
                _runs.Remove(old);
                Log("Took " + Settings.Title(old) + " (" + Settings.Detail(old) + ") off the graph to make room (" + Palette.MaxRuns + " at most)");
            }
            var o = Options();
            var live = new RunData { Live = true, Options = o, Cpu = CpuTopology.Model, Date = DateTime.Now };
            _runs.Add(live);
            Busy(true);
            _cancel = new CancellationTokenSource();
            _run = new TestRunner();
            var run = _run;
            run.Log += Log;
            run.Status += (t, p) => Dispatcher.UIThread.Post(() => SetBar(t, p));
            // the graph gets its own copies: the test keeps adding to its steps on another thread
            run.WindowAdded += (s, w) =>
            {
                if (s == null) return;
                var copy = Snapshot(run.Steps);
                Results.Relate(copy, run.Options?.Alternate ?? true);
                Dispatcher.UIThread.Post(() => { live.Steps = copy; Redraw(); RefreshTicks(); });
            };
            _status.Text = "Testing. Keep the PC otherwise idle";
            // the automated test: each round on the graph as it starts, and your pick when a round is done (if you asked to)
            run.AskPick = AskPick;
            run.RoundStarted += round => Dispatcher.UIThread.Post(() => _cmpKind.SelectedIndex = Array.IndexOf(Settings.Kinds, round == 1 ? "pair" : round == 2 ? "target" : "ai"));
            run.RetestStarted += () => Dispatcher.UIThread.Post(() => _cmpKind.SelectedIndex = Array.IndexOf(Settings.Kinds, "retest"));
            // tests picked by hand: the graph of the first one ticked
            if (!o.Automated)
                _cmpKind.SelectedIndex = Array.IndexOf(Settings.Kinds, o.TestCores ? "cores" : o.TestWorkers ? "workers" : o.TestTarget ? "target" : o.TestAiLoad ? "ai" : "baseline");
            try
            {
                _testing = true;
                try { await Task.Run(() => run.Run(o, _cancel.Token)); }
                finally { _testing = false; }
                var r = Results.Analyse(run);
                r.Save(run);
                _results = r;
                Log("Results saved in " + r.Folder);
                Log(r.Report(run));
                live.Steps = Snapshot(run.Steps);
                Results.Relate(live.Steps, o.Alternate);
                live.Results = r;
                live.Folder = r.Folder;
                live.Date = r.Date;
                _status.Text = (run.StoppedBecause == null ? "Finished. " : "Stopped: " + run.StoppedBecause + ". ") + Summary(r);
            }
            catch (Exception e) { _status.Text = "Test failed: " + e.Message; Log(e.ToString()); }
            live.Live = false;
            Idle();
            Busy(false);
        }

        // earlier tests onto the graph (and the newest one into Apply): the tests in data/results, or another folder,
        // ticked in a list that says what each one was
        async Task PickResults()
        {
            var picked = await ResultsChooser().ShowDialog<List<string>>(this);
            if (picked != null && picked.Count > 0) _status.Text = AddFolders(picked);
        }

        internal OpenWindow ResultsChooser(string folder = null)
        {
            try { Directory.CreateDirectory(ServerSetup.ResultsDir); } catch { }   // a folder the tester can't write to: the list says it is empty
            return new OpenWindow(folder ?? ServerSetup.ResultsDir, _runs.Where(r => r.Folder != null).Select(r => r.Folder), Palette.MaxRuns - _runs.Count);
        }

        // the tests in these folders, newest first: a folder with a results.json is one test, any other folder is
        // searched one level down
        internal static List<string> TestFolders(IEnumerable<string> paths)
        {
            var found = new List<string>();
            foreach (var p in paths)
            {
                try
                {
                    if (File.Exists(Path.Combine(p, "results.json"))) found.Add(p);
                    else found.AddRange(Directory.GetDirectories(p).Where(d => File.Exists(Path.Combine(d, "results.json"))));
                }
                catch { }
            }
            // by when each started, in UTC: the plugin names its folders by UTC and the tester by this PC's time, so the names
            // don't sort them (the name only when the start can't be read)
            return found.Select(Path.GetFullPath).Distinct().Select(d => (d, at: Results.StartedUtc(d)))
                        .OrderByDescending(x => x.at).ThenByDescending(x => Path.GetFileName(x.d), StringComparer.Ordinal).Select(x => x.d).ToList();
        }

        internal string AddFolders(IEnumerable<string> paths)
        {
            var tests = TestFolders(paths);
            if (tests.Count == 0) return "No test results there: pick a test's folder in data/results, or data/results itself.";
            int opened = 0, already = 0, full = 0;
            string last = "";
            Results newest = null;
            var fails = new List<string>();
            foreach (var t in tests)
            {
                if (_runs.Any(r => r.Folder != null && Path.GetFullPath(r.Folder) == t)) { already++; continue; }
                if (_runs.Count >= Palette.MaxRuns) { full++; continue; }
                if (AddFolder(t, out string m)) { opened++; last = m; newest ??= _runs[_runs.Count - 1].Results; } else fails.Add(m);
            }
            // Apply takes the newest test opened (they open newest first)
            if (newest != null && !_testing) { _results = newest; if (!_busy) _apply.IsEnabled = true; }
            // the graph on a test the opened results have, when they have nothing for the one it is on
            var shown = _runs.Where(r => r.Shown).ToList();
            bool Has(string k) => shown.Any(r => k == "baseline" ? CompareChart.BaselineSteps(r, k).Count > 0 : r.Steps.Any(s => s.Kind == k && s.Ok));
            if (opened > 0 && !Has(_compare.Kind))
            {
                string first = new[] { "pair", "cores", "workers", "target", "ai", "baseline" }.FirstOrDefault(Has);
                if (first != null) _cmpKind.SelectedIndex = Array.IndexOf(Settings.Kinds, first);
            }
            var bits = new List<string>();
            if (opened == 1) bits.Add(last);
            else if (opened > 1) bits.Add("Opened " + opened + " tests.");
            if (already > 0) bits.Add(already + " already on the graph.");
            if (full > 0) bits.Add(full + " more not opened: the graph holds " + Palette.MaxRuns + " (take some off with ×).");
            bits.AddRange(fails);
            return string.Join(" ", bits);
        }

        internal bool AddFolder(string folder, out string message)
        {
            message = "";
            string full = Path.GetFullPath(folder);
            if (_runs.Any(r => r.Folder != null && Path.GetFullPath(r.Folder) == full)) { message = "That test is on the graph already."; return false; }
            if (_runs.Count >= Palette.MaxRuns) { message = "The graph holds " + Palette.MaxRuns + " tests: take one off with its × first."; return false; }
            try
            {
                var r = Results.Load(full, out var steps, out string summary);
                _runs.Add(new RunData { Folder = full, Steps = steps, Results = r, Options = r.Options, Cpu = r.CpuModel, Date = r.Date });
                if (!_testing) { _results = r; if (!_busy) _apply.IsEnabled = true; }
                message = "Opened the test from " + summary + ". " + Summary(r);
                RefreshRuns();
                return true;
            }
            catch (Exception e) { message = "Couldn't open " + full + ": " + e.Message; return false; }
        }

        // ---------------------------------------------------------------- the tests on the graph
        // the test that has been around longest (by when it started, in UTC; the running one never)
        RunData OldestRun() => OldestOf(_runs);

        internal static RunData OldestOf(IEnumerable<RunData> runs) =>
            runs.Where(r => !r.Live).OrderBy(r => r.Results == null || r.Results.Date == default ? DateTime.MinValue : r.Results.Date.ToUniversalTime()).First();

        void Redraw() => _compare.Refresh();

        // a chip per test: tick to show it, × to take it off the graph
        void RefreshRuns()
        {
            _runChips.Children.Clear();
            foreach (var r in _runs)
            {
                var run = r;
                var show = new CheckBox
                {
                    IsChecked = run.Shown, VerticalAlignment = VerticalAlignment.Center,
                    Content = new StackPanel { Children =
                    {
                        new TextBlock { Text = Settings.Title(run), FontSize = 12 },
                        new TextBlock { Text = Settings.Detail(run), FontSize = 11, Foreground = Brushes.Gray },
                    } },
                };
                ToolTip.SetTip(show, run.Folder ?? (run.Live ? "Running now" : "Not saved"));
                show.IsCheckedChanged += (_, __) => { run.Shown = show.IsChecked == true; RefreshTicks(); Redraw(); };
                var remove = new Button { Content = "×", Padding = new Thickness(6, 0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), IsEnabled = !run.Live, VerticalAlignment = VerticalAlignment.Center };
                ToolTip.SetTip(remove, "Take it off the graph");
                remove.Click += (_, __) => { _runs.Remove(run); RefreshRuns(); };
                var chip = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 0, 1), Margin = new Thickness(0, 0, 6, 6),
                                        Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { show, remove } } };
                chip.Bind(Border.BorderBrushProperty, chip.GetResourceObservable("ThemeBorderMidBrush"));
                _runChips.Children.Add(chip);
            }
            if (_runs.Count == 0)
                _runChips.Children.Add(new TextBlock { Text = "Open results puts earlier tests on the graph, up to " + Palette.MaxRuns + ".", Foreground = Brushes.Gray, FontSize = 12 });
            _cmpTickKeys = "\0";
            RefreshTicks();
            Redraw();
        }

        // a tick for each setting of the test picked above the comparison; rebuilt only when the settings on it change
        void RefreshTicks()
        {
            Fill(_cmpTicks, _compare, _cmpHidden, _cmpSeen, ref _cmpTickKeys);
            RefreshBaselines();
        }

        // the tests on the graph that ran different baselines: ask which one is the baseline (rebuilt only when they change)
        void RefreshBaselines()
        {
            var groups = CompareChart.BaselineGroups(_runs.Where(r => r.Shown), _compare.Kind);
            string sig = _compare.Kind + "|" + string.Join("\n", groups.Select(g => g.Key + "=" + g.Label));
            if (sig == _cmpBaseSig) return;
            _cmpBaseSig = sig;
            _cmpBaseGroups = groups;
            _cmpBasePick.TryGetValue(_compare.Kind, out string want);
            _compare.BaselineKey = want;
            _cmpBaseRow.IsVisible = groups.Count > 1;
            _cmpBaseRow.Children.Clear();
            if (groups.Count < 2) return;
            _cmpBaseRow.Children.Add(new TextBlock { Text = "These tests ran different baselines. Compare against:", FontSize = 12, TextWrapping = TextWrapping.Wrap });
            _cmpBaseRow.Children.Add(_cmpBase);
            int keep = groups.FindIndex(g => g.Key == want);
            _cmpBase.ItemsSource = groups.Select(g => g.Label).ToList();
            _cmpBase.SelectedIndex = keep >= 0 ? keep : 0;
            _compare.BaselineKey = groups[Math.Max(0, keep)].Key;
        }

        void Fill(WrapPanel panel, CompareChart chart, Dictionary<string, HashSet<string>> hiddenByKind, Dictionary<string, HashSet<string>> seenByKind,
                  ref string shownKeys)
        {
            string kind = chart.Kind;
            // (the baseline graph has one setting: nothing to tick; the cores and workers graph ticks its core counts and its
            // worker counts)
            var keys = kind == null || kind == "baseline" ? new List<string>() : Settings.Keys(_runs.Where(r => r.Shown), kind);
            var ticks = Settings.PairLike(kind)
                ? keys.Select(TestPlan.PairCores).Distinct().Select(c => ("c:" + c, Settings.CoreName(_runs.FirstOrDefault(r => r.Shown)?.Options, c)))
                      .Concat(keys.Select(TestPlan.PairWorkers).Distinct().OrderBy(w => w).Select(w => ("w:" + w.ToString(Inv), Settings.Long("workers", w.ToString(Inv))))).ToList()
                : keys.Select(k => (k, Settings.Name(_runs.FirstOrDefault(r => r.Shown)?.Options, kind ?? "", k))).ToList();
            keys = ticks.Select(t => t.Item1).ToList();
            string sig = (kind ?? "") + "|" + string.Join(",", keys);
            if (sig == shownKeys) return;
            shownKeys = sig;
            panel.Children.Clear();
            panel.IsVisible = keys.Count > 0;
            if (kind == null) return;
            if (!hiddenByKind.TryGetValue(kind, out var hidden)) hiddenByKind[kind] = hidden = new HashSet<string>();
            if (!seenByKind.TryGetValue(kind, out var seen)) seenByKind[kind] = seen = new HashSet<string>();
            // a setting seen for the first time (a new step of a live test, or results opened) is ticked
            foreach (var k in keys) seen.Add(k);
            chart.Hidden = hidden;
            panel.Children.Add(new TextBlock { Text = "Show", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), FontSize = 12 });
            foreach (var (k, label) in ticks)
            {
                string key = k;
                if (Settings.PairLike(kind) && key == ticks.First(t => t.Item1.StartsWith("w:", StringComparison.Ordinal)).Item1)
                    panel.Children.Add(new TextBlock { Text = "  workers", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), FontSize = 12, Foreground = Brushes.Gray });
                var cb = new CheckBox { Content = label, IsChecked = !hidden.Contains(key), Margin = new Thickness(0, 0, 10, 0), FontSize = 12 };
                cb.IsCheckedChanged += (_, __) =>
                {
                    if (cb.IsChecked == true) hidden.Remove(key); else hidden.Add(key);
                    chart.Refresh();
                };
                panel.Children.Add(cb);
            }
        }

        static List<Step> Snapshot(List<Step> steps)
        {
            lock (steps)
                return steps.Select(st =>
                {
                    var c = new Step { Kind = st.Kind, Value = st.Value, Label = st.Label, Baseline = st.Baseline, Cpus = st.Cpus, CpusGot = st.CpusGot,
                                       Workers = st.Workers, Target = st.Target, Ai = st.Ai, Note = st.Note, Number = st.Number, Pass = st.Pass, Skipped = st.Skipped };
                    lock (st.Windows) c.Windows.AddRange(st.Windows);
                    return c;
                }).ToList();
        }

        static string Summary(Results r)
        {
            var bits = new List<string>();
            if (r.RecCores > 0) bits.Add(r.RecCores + " cores");
            if (r.RecWorkers > 0) bits.Add(r.RecWorkers + " workers");
            if (r.RecTarget > 0) bits.Add("target " + r.RecTarget);
            // nothing to suggest: the first finding says why (a frame target that couldn't be held, for example)
            return bits.Count > 0 ? "Suggested: " + string.Join(", ", bits) + "." : r.Findings.FirstOrDefault() ?? "";
        }

        // ---------------------------------------------------------------- the automated test's picks
        // a round is done: the choices (the suggestion first) at the bottom of the window, with CONTINUE; the test server
        // keeps running while it waits
        Task<string> AskPick(string question, List<KeyValuePair<string, string>> choices, CancellationToken cancel)
        {
            var wait = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.UIThread.Post(() =>
            {
                _pickText.Text = question;
                _pickKeys = choices.Select(c => c.Key).ToList();
                _pickBox.ItemsSource = choices.Select(c => c.Value).ToList();
                _pickBox.SelectedIndex = 0;
                _pickWait = wait;
                _pickRow.IsVisible = true;
                _status.Text = "Waiting for your pick: press CONTINUE to go on";
            });
            cancel.Register(() => { wait.TrySetCanceled(); Dispatcher.UIThread.Post(() => _pickRow.IsVisible = false); });
            return wait.Task;
        }

        void PickMade()
        {
            var wait = _pickWait;
            _pickWait = null;
            _pickRow.IsVisible = false;
            _status.Text = "Testing. Keep the PC otherwise idle";
            wait?.TrySetResult(_pickKeys.Count > 0 ? _pickKeys[Math.Max(0, Math.Min(_pickKeys.Count - 1, _pickBox.SelectedIndex))] : null);
        }

        // ---------------------------------------------------------------- the first time
        // no results yet: how the tester works, and a baseline to start with
        internal async void WelcomeIfFirstTime()
        {
            try
            {
                if (_busy || TestFolders(new[] { ServerSetup.ResultsDir }).Count > 0) return;
                var w = Welcome();
                if (await w.ShowDialog<bool>(this)) { _baseCores.SelectedIndex = w.Cores; await TakeBaseline(); }
            }
            catch (Exception e) { Log("Welcome: " + e.Message); }
        }

        // the settings a test will run with, from the options as they are now (the notes under them included)
        internal ConfirmWindow Confirm()
        {
            var o = Options();
            UpdateEstimate();
            int at = TestPlan.Compare(o);
            var l = new List<(string, string)>
            {
                ("Test mission", (_mission.SelectedIndex == 1 ? "Realistic Benchmark" : "Stress Test") + ", " + o.Ai + " AI aircraft"),
                ("Baseline", "Cores: " + _baseCores.SelectedItem + ", Workers: " + _baseWorkers.SelectedItem
                             + (o.Automated || o.TestBaseline || o.TestCores || o.TestWorkers ? ", at " + at + " fps" : ". Not measured on its own; the other tests run on it.")),
            };
            if (o.Automated)
            {
                l.Add(("Round 1: cores and physics workers, at " + at + " fps", _pairNote.Text + " " + PickWays[Math.Max(0, _pickPair.SelectedIndex)] + "."));
                l.Add(("Round 2: frame target on the cores and workers picked", _tList.Text + " " + PickWays[Math.Max(0, _pickTarget.SelectedIndex)] + "."));
                l.Add(("Round 3: AI load on both picks", _aiList.Text));
            }
            else
            {
                string order = o.Alternate ? ", each followed by the baseline" : ", one after another";
                if (o.TestCores) l.Add(("Core count, at " + at + " fps", string.Join(", ", TestPlan.CoreValues(o).Select(v => Settings.Long("cores", v))) + order + "."));
                if (o.TestWorkers) l.Add(("Physics workers, at " + at + " fps", "1 to " + TestPlan.MaxWorkers(o) + order + "."));
                if (o.TestTarget) l.Add(("Frame target", _tList.Text));
                if (o.TestAiLoad) l.Add(("AI load, at " + at + " fps", _aiList.Text));
            }
            l.Add(("Each step", (o.KeepWindows * 15) + " seconds" + (TestPlan.RetestCount(o) > 0 ? ", the best 3 again 2 minutes each" : "")
                                + (o.Repeats > 1 ? ", and the whole test runs " + o.Repeats + " times" : "")));
            return new ConfirmWindow(o.Automated ? "Run the automated test with these settings?" : "Run these tests with these settings?", l, _estimate.Text);
        }

        internal WelcomeWindow Welcome()
        {
            var o = Options();
            o.Automated = false; o.TestBaseline = true; o.TestCores = o.TestWorkers = o.TestTarget = o.TestAiLoad = false;
            string baseline = "Workers: " + (o.BaselineWorkers > 0 ? o.BaselineWorkers.ToString(Inv) : "Server Default") + ", " + TestPlan.Compare(o) + " fps, " + o.Ai + " AI aircraft";
            return new WelcomeWindow(baseline, _baseCoreNames, Math.Max(0, _baseCores.SelectedIndex), Duration(Minutes(o)), ServerSetup.ServerInstalled);
        }

        // the baseline on its own, on the baseline graph, installing the test server first when it isn't there
        async Task TakeBaseline()
        {
            // the baseline on its own, then the options back as they were (the automated test, ready for START)
            bool auto = _mAuto.IsChecked == true;
            var ticks = new[] { _tBaseline, _tCores, _tWorkers, _tTarget, _tAi }.Select(t => t.IsChecked == true).ToArray();
            _mCustom.IsChecked = true;
            TickTests(new[] { "baseline" });
            _cmpKind.SelectedIndex = Array.IndexOf(Settings.Kinds, "baseline");
            try
            {
                // yes to a baseline is yes to the install it needs too
                if (!ServerSetup.ServerInstalled)
                {
                    _status.Text = "Installing the test server first, then the baseline";
                    if (!await Install()) { _status.Text += ". The baseline didn't start. Press INSTALL to try again."; return; }
                }
                await StartTest();
            }
            finally
            {
                var boxes = new[] { _tBaseline, _tCores, _tWorkers, _tTarget, _tAi };
                for (int i = 0; i < boxes.Length; i++) boxes[i].IsChecked = ticks[i];
                if (auto) _mAuto.IsChecked = true; else _mCustom.IsChecked = true;
            }
        }

        // ---------------------------------------------------------------- for the screenshot check
        internal void HoverChart(Point p) => _compare.SetHover(p);
        internal Results ApplyResults => _results;

        // the test above each graph, and only these settings ticked on the comparison
        internal void PickView(int cmp, string[] only)
        {
            if (cmp >= 0) _cmpKind.SelectedIndex = cmp;
            if (only == null) return;
            var keys = Settings.Keys(_runs, _compare.Kind);
            var set = new HashSet<string>(keys.Where(k => !only.Contains(k)));
            _cmpHidden[_compare.Kind] = set;
            _cmpSeen[_compare.Kind] = new HashSet<string>(keys);
            _compare.Hidden = set;
            _cmpTickKeys = "\0";
            RefreshTicks();
        }

        // made-up numbers, to look at the layout without running a test
        internal void LoadDemo(bool automated = false)
        {
            if (_runs.Count >= Palette.MaxRuns) _runs.Remove(OldestRun());
            var rnd = new Random(3);
            if (automated)
            {
                var ao = new TestOptions { Automated = true };
                // made-up measurements for each round, which runs on the one before's pick, as a real test
                void Fill(List<Step> all, int from)
                {
                    for (int i = from; i < all.Count; i++)
                    {
                        var s = all[i];
                        s.Number = i + 1;
                        double cost = 3.0;
                        if (s.Kind == "pair" || s.Kind == "retest" && !s.Baseline)
                        {
                            string c = TestPlan.PairCores(s.Value);
                            int w = TestPlan.PairWorkers(s.Value);
                            cost = (c == "1" ? 4.5 : c == "2" ? 3.4 : 3.0) + (w == 1 ? 0.6 : w == 2 ? 0.2 : 0);
                        }
                        double fps = s.Kind == "target" ? Math.Min(s.Target, 178) : s.Kind == "ai" && s.Ai > 110 ? 128 : s.Target;
                        for (int k = 0; k < TestPlan.KeepOf(ao, s); k++)
                            s.Windows.Add(new Measurement { Fps = fps * (1 + (rnd.NextDouble() - 0.5) * 0.01), Spare = 1000 - cost * fps * (1 + (rnd.NextDouble() - 0.5) * 0.06),
                                                            Physics = 70 + rnd.Next(10), Ai = s.Ai, Workers = s.Workers > 0 ? s.Workers : 8, Target = s.Target, CpuPct = 120 });
                    }
                }
                var all = TestPlan.Build(ao);
                Fill(all, 0);
                int at = all.Count;
                all.AddRange(TestPlan.AutoRetest(ao, Results.TopPairs(all, ao)));
                Fill(all, at);
                string dp = Results.PickPair(all, ao, out _) ?? "3x3";   // the demo's own pick, from its made-up numbers
                at = all.Count;
                all.AddRange(TestPlan.AutoTargets(ao, dp));
                all.AddRange(TestPlan.AutoAi(ao, dp, 120));
                Fill(all, at);
                Results.Relate(all, true);
                var ar = new Results { PairPick = dp, PairAuto = dp, RecCores = int.TryParse(TestPlan.PairCores(dp), NumberStyles.Integer, Inv, out int dc) ? dc : 3,
                                       RecWorkers = TestPlan.PairWorkers(dp), RecTarget = 120, CpuModel = "Demo, made-up numbers" };
                _runs.Add(new RunData { Steps = all, Results = ar, Options = ao, Cpu = "Demo, made-up numbers", Date = DateTime.Now });
                _results ??= ar;
                RefreshRuns();
                _status.Text = "Finished. Suggested: " + TestPlan.PairText(ao, dp) + ", target 120.";
                return;
            }
            var steps = TestPlan.Build(new TestOptions { TestTarget = false });
            foreach (var s in steps)
            {
                double baseCost = s.Kind == "cores" ? (s.Value == "1" ? 4.5 : s.Value == "2" ? 3.4 : 3.0) : s.Kind == "workers" ? (s.Value == "1" ? 3.6 : s.Value == "2" ? 3.2 : 3.0) : 3;
                for (int k = 0; k < 3; k++)
                {
                    double cost = baseCost * (1 + (rnd.NextDouble() - 0.5) * 0.06);
                    s.Windows.Add(new Measurement { Fps = 60, Spare = 1000 - cost * 60, Physics = 70 + rnd.Next(10), Ai = 40, Workers = s.Workers > 0 ? s.Workers : 8, Target = 60, CpuPct = 120 });
                }
            }
            Results.Relate(steps, true);
            var r = new Results { RecCores = 3, RecWorkers = 4, CpuModel = "Demo, made-up numbers" };
            _runs.Add(new RunData { Steps = steps, Results = r, Options = new TestOptions { TestTarget = false }, Cpu = "Demo, made-up numbers", Date = DateTime.Now });
            _results ??= r;
            RefreshRuns();
            _status.Text = "Finished. Suggested: 3 cores, 4 workers.";
        }
    }
}
