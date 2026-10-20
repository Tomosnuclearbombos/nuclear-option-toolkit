using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace ServerTester
{
    // Before a test starts (START): the settings it will run with, to check them first. True from ShowDialog means run it.
    public sealed class ConfirmWindow : Avalonia.Controls.Window
    {
        public ConfirmWindow(string question, IEnumerable<(string name, string text)> lines, string estimate)
        {
            Title = "Start the test";
            Width = 560; Height = 560; CanResize = true;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var p = new StackPanel { Margin = new Thickness(12), Spacing = 8 };
            p.Children.Add(MainWindow.Header(question));
            foreach (var (name, text) in lines)
                p.Children.Add(new StackPanel
                {
                    Spacing = 1,
                    Children =
                    {
                        new TextBlock { Text = name, FontWeight = FontWeight.Bold, FontSize = 13 },
                        new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13 },
                    },
                });
            p.Children.Add(new TextBlock { Text = estimate, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 12 });

            var ok = new Button { Content = "OK", Width = 90, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = true };
            ok.Click += (_, __) => Close(true);
            var cancel = new Button { Content = "CANCEL", Width = 90, HorizontalContentAlignment = HorizontalAlignment.Center, IsCancel = true };
            cancel.Click += (_, __) => Close(false);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right,
                                           Margin = new Thickness(12, 6, 12, 12), Children = { ok, cancel } };
            var dock = new DockPanel();
            DockPanel.SetDock(buttons, Dock.Bottom);
            dock.Children.Add(buttons);
            dock.Children.Add(new ScrollViewer { Content = p });
            Content = dock;
        }
    }
}
