using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ClaudeStatus;

/// <summary>
/// Flyout opened from the widget or a tray icon. "Now": every limit window, with pace, trend and
/// forecast for the 5-hour and 7-day limits. "Stats": history (only when usage history is on).
/// </summary>
public sealed class DetailsWindow : Window
{
    readonly UsageMonitor _monitor;
    readonly Point _anchor; // physical pixels: bottom-right corner target
    readonly StackPanel _body = new();
    readonly Func<HistoryStore?> _history; // null result = usage history is off
    readonly bool _light;
    bool _closing;
    bool _stats;
    string _range = StatsView.Days14;
    const double BarWidth = 320;

    public DetailsWindow(UsageMonitor monitor, Point anchor, Action<Point> showMenu, Action quit, Func<HistoryStore?> history, bool openStats = false)
    {
        _monitor = monitor;
        _anchor = anchor;
        _history = history;
        _stats = openStats;

        var light = _light = Theme.TaskbarIsLight();
        var bg = light ? Color.FromRgb(0xF9, 0xF9, 0xF9) : Color.FromRgb(0x2B, 0x2B, 0x2B);
        var fg = light ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Color.FromRgb(0xF0, 0xF0, 0xF0);

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Foreground = new SolidColorBrush(fg);
        Title = "Claude usage";

        Button Make(string text, RoutedEventHandler click)
        {
            var b = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) };
            b.Click += click;
            return b;
        }
        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(Make("Refresh", async (_, _) => await _monitor.RefreshAsync()));
        buttons.Children.Add(Make("Usage page", (_, _) => TrayController.OpenUsagePage()));
        Button? account = null;
        account = Make("Account ▾", (_, _) =>
        {
            // Opens the full app menu (accounts, organisation, settings) under the button.
            var p = account!.PointToScreen(new Point(0, account.ActualHeight));
            Close();
            showMenu(p);
        });
        buttons.Children.Add(account);
        if (!Policy.Current.DisableExit) // administrators can remove the user's way to quit
            buttons.Children.Add(Make("Quit", (_, _) => { Close(); quit(); }));

        var outer = new StackPanel();
        outer.Children.Add(_body);
        outer.Children.Add(buttons);

        Content = new Border
        {
            Background = new SolidColorBrush(bg),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 14, 16, 14),
            MinWidth = 300,
            Child = outer,
        };

        _monitor.Changed += Render;
        Closed += (_, _) => _monitor.Changed -= Render;
        // Close() from a button deactivates the window while it is closing; don't close it twice.
        Closing += (_, _) => _closing = true;
        Deactivated += (_, _) => { if (!_closing) { _closing = true; Close(); } };
        Loaded += (_, _) => PlaceNearAnchor();
        Render();
    }

    void Render()
    {
        _body.Children.Clear();
        var snap = _monitor.Last;
        var store = snap is null ? null : _history();
        if (store is null) _stats = false;

        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        if (store is not null)
        {
            var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0) };
            foreach (var (stats, label) in new[] { (false, "Now"), (true, "Stats") })
            {
                var t = new ToggleButton { Content = label, IsChecked = _stats == stats, Padding = new Thickness(10, 1, 10, 1), Margin = new Thickness(2, 0, 0, 0) };
                t.Click += (_, _) => { _stats = stats; Rerender(); };
                tabs.Children.Add(t);
            }
            DockPanel.SetDock(tabs, Dock.Right);
            head.Children.Add(tabs);
        }
        head.Children.Add(new TextBlock
        {
            Text = "Claude usage" + (string.IsNullOrEmpty(_monitor.AccountLabel) ? "" : " · " + _monitor.AccountLabel),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        _body.Children.Add(head);

        if (_stats && store is not null)
        {
            _body.Children.Add(StatsView.Build(store, _range, _light, r => { _range = r; Rerender(); }, () => Export(store), () => Clear(store)));
            return;
        }
        if (!string.IsNullOrEmpty(snap?.OrganizationName) && snap!.OrganizationName != _monitor.AccountLabel)
            _body.Children.Add(new TextBlock { Text = "Organization: " + snap.OrganizationName, FontSize = 11, Opacity = 0.7, Margin = new Thickness(0, -6, 0, 8) });

        if (snap is null)
        {
            _body.Children.Add(new TextBlock { Text = _monitor.Message ?? "No data yet.", TextWrapping = TextWrapping.Wrap, MaxWidth = 300 });
            return;
        }

        var now = DateTimeOffset.Now;
        foreach (var w in snap.Windows)
        {
            _body.Children.Add(new TextBlock { Text = w.Label, FontSize = 12, Margin = new Thickness(0, 6, 0, 3) });
            var track = new Border
            {
                Width = BarWidth,
                Height = 6,
                HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)),
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(w.Percent, 0.001), GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(100 - w.Percent, 0.001), GridUnitType.Star) });
            grid.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Theme.Level(w.Percent)) });
            track.Child = grid;

            // Pace marker: how much of the window has passed (usage left of it = within budget).
            var pace = Insights.Pace(w, now);
            var bar = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
            bar.Children.Add(track);
            if (pace is { } pc)
                bar.Children.Add(new Border
                {
                    Width = 2, Height = 12, Margin = new Thickness(BarWidth * pc.Fraction - 1, -3, 0, -3),
                    HorizontalAlignment = HorizontalAlignment.Left, Background = Foreground,
                    ToolTip = pc.Text,
                });
            _body.Children.Add(bar);

            var reset = w.ResetsAt is { } r ? $" · resets in {Theme.Countdown(r)} ({r.ToLocalTime():ddd HH:mm})" : "";
            _body.Children.Add(new TextBlock { Text = $"{w.Percent:0}% used{reset}", FontSize = 11, Opacity = 0.7, Margin = new Thickness(0, 3, 0, 0) });
            if (pace is { } p2)
                _body.Children.Add(new TextBlock { Text = p2.Text, FontSize = 10, Opacity = 0.55 });

            if (store is not null && Insights.For(store, w, snap.FetchedAt) is { } forecast)
            {
                _body.Children.Add(Sparkline(w, forecast));
                _body.Children.Add(new TextBlock { Text = forecast.Text, FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxWidth = BarWidth, Margin = new Thickness(0, 2, 0, 4) });
            }
        }

        var footer = $"Updated {snap.FetchedAt:HH:mm:ss}";
        if (_monitor.State == MonitorState.Error) footer += " – " + _monitor.Message;
        _body.Children.Add(new TextBlock { Text = footer, FontSize = 10, Opacity = 0.55, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 300 });
    }

    /// <summary>Usage over the current window (solid) and the forecast (dashed).</summary>
    UIElement Sparkline(LimitWindow w, Insights.Forecast f)
    {
        const double h = 30;
        var length = Insights.Length(w.Key)!.Value;
        var end = w.ResetsAt!.Value;
        var start = end - length;
        Point At(DateTimeOffset t, double p) =>
            new(Math.Clamp((t - start) / length, 0, 1) * BarWidth, h - Math.Clamp(p, 0, 100) / 100 * h);

        var canvas = new Canvas { Width = BarWidth, Height = h, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        canvas.Children.Add(new Line { X1 = 0, X2 = BarWidth, Y1 = h, Y2 = h, Stroke = new SolidColorBrush(Color.FromArgb(0x50, 0x80, 0x80, 0x80)), StrokeThickness = 1 });
        var color = new SolidColorBrush(Theme.Level(w.Percent));
        var line = new Polyline { Stroke = color, StrokeThickness = 2 };
        foreach (var (t, p) in f.Trend) line.Points.Add(At(t, p));
        canvas.Children.Add(line);
        if (f.Projection is { } proj && f.Trend.Count > 0)
        {
            var last = f.Trend[^1];
            var a = At(last.T, last.P);
            var b = At(proj.T, proj.P);
            canvas.Children.Add(new Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = color, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 2, 2 }, Opacity = 0.6 });
        }
        return canvas;
    }

    void Rerender()
    {
        Render();
        // The size changes with the tab/range: keep the flyout anchored above the taskbar.
        Dispatcher.BeginInvoke(PlaceNearAnchor, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    void Export(HistoryStore store)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"claude-usage-{DateTime.Now:yyyy-MM-dd}.csv",
            Filter = "CSV file (*.csv)|*.csv",
        };
        _closing = true; // the dialog deactivates the flyout; keep it open
        try
        {
            if (dialog.ShowDialog(this) == true) File.WriteAllText(dialog.FileName, store.ToCsv());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // E.g. the file is open in Excel or the folder is read-only: tell the user, don't crash.
            MessageBox.Show(this, "The file could not be saved: " + ex.Message, "Claude Status", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _closing = false;
            Activate();
        }
    }

    void Clear(HistoryStore store)
    {
        _closing = true;
        try
        {
            if (MessageBox.Show(this, "Delete the usage history of this account and organization?", "Claude Status",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                store.Clear();
                Rerender();
            }
        }
        finally
        {
            _closing = false;
            Activate();
        }
    }

    void PlaceNearAnchor()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.GetWindowRect(hwnd, out var me);
        var scale = Native.GetDpiForWindow(hwnd) / 96.0;
        var gap = (int)(8 * scale);

        var tb = Native.GetTaskbar();
        int x = (int)_anchor.X - me.Width, y = (int)_anchor.Y - me.Height - gap;
        if (tb is not null)
        {
            var b = tb.Bounds;
            switch (tb.Edge)
            {
                case Native.Edge.Top: y = b.Bottom + gap; break;
                case Native.Edge.Left: x = b.Right + gap; y = (int)_anchor.Y - me.Height; break;
                case Native.Edge.Right: x = b.Left - me.Width - gap; y = (int)_anchor.Y - me.Height; break;
                default: y = b.Top - me.Height - gap; break;
            }
        }
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)_anchor.X, (int)_anchor.Y)).WorkingArea;
        x = Math.Clamp(x, screen.Left + gap, Math.Max(screen.Left + gap, screen.Right - me.Width - gap));
        y = Math.Clamp(y, screen.Top + gap, Math.Max(screen.Top + gap, screen.Bottom - me.Height - gap));
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE);
        Activate();
    }
}
