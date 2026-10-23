using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace ClaudeStatus;

/// <summary>Flyout with every limit window, opened from the widget or the tray icon.</summary>
public sealed class DetailsWindow : Window
{
    readonly UsageMonitor _monitor;
    readonly Point _anchor; // physical pixels: bottom-right corner target
    readonly StackPanel _body = new();
    bool _closing;

    public DetailsWindow(UsageMonitor monitor, Point anchor, Action<Point> showMenu, Action quit)
    {
        _monitor = monitor;
        _anchor = anchor;

        var light = Theme.TaskbarIsLight();
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

        _body.Children.Add(new TextBlock
        {
            Text = "Claude usage" + (string.IsNullOrEmpty(_monitor.AccountLabel) ? "" : " · " + _monitor.AccountLabel),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10),
        });
        if (!string.IsNullOrEmpty(snap?.OrganizationName) && snap!.OrganizationName != _monitor.AccountLabel)
            _body.Children.Add(new TextBlock { Text = "Organization: " + snap.OrganizationName, FontSize = 11, Opacity = 0.7, Margin = new Thickness(0, -6, 0, 8) });

        if (snap is null)
        {
            _body.Children.Add(new TextBlock { Text = _monitor.Message ?? "No data yet.", TextWrapping = TextWrapping.Wrap, MaxWidth = 300 });
            return;
        }

        foreach (var w in snap.Windows)
        {
            _body.Children.Add(new TextBlock { Text = w.Label, FontSize = 12, Margin = new Thickness(0, 6, 0, 3) });
            var track = new Border
            {
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)),
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(w.Percent, 0.001), GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(100 - w.Percent, 0.001), GridUnitType.Star) });
            grid.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Theme.Level(w.Percent)) });
            track.Child = grid;
            _body.Children.Add(track);

            var reset = w.ResetsAt is { } r ? $" · resets in {Theme.Countdown(r)} ({r.ToLocalTime():ddd HH:mm})" : "";
            _body.Children.Add(new TextBlock { Text = $"{w.Percent:0}% used{reset}", FontSize = 11, Opacity = 0.7, Margin = new Thickness(0, 3, 0, 0) });
        }

        var footer = $"Updated {snap.FetchedAt:HH:mm:ss}";
        if (_monitor.State == MonitorState.Error) footer += " – " + _monitor.Message;
        _body.Children.Add(new TextBlock { Text = footer, FontSize = 10, Opacity = 0.55, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 300 });
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
