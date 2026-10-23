using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClaudeStatus;

/// <summary>
/// A borderless, always-on-top strip that sits on the taskbar next to the notification area.
/// Windows 11 removed deskbands, so overlaying the taskbar is the supported-ish way to "be on the taskbar".
/// Drag it horizontally to move it; click for details; right-click for the menu.
/// </summary>
public sealed class TaskbarWidget : Window
{
    readonly Settings _settings;
    readonly UsageMonitor _monitor;
    readonly Func<System.Windows.Forms.ContextMenuStrip> _menu;
    readonly Action<Point?> _showDetails;
    readonly DispatcherTimer _placeTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    readonly Row _session = new("5h");
    readonly Row _weekly = new("7d");
    readonly TextBlock _status = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    readonly StackPanel _rows = new() { VerticalAlignment = VerticalAlignment.Center };

    IntPtr _hwnd;
    bool _visible;
    bool _light;
    int _tick;

    // Dragging state (physical pixels).
    int _dragStartCursorX, _dragStartLeft;
    bool _pressed, _dragging;

    public TaskbarWidget(Settings settings, UsageMonitor monitor,
        Func<System.Windows.Forms.ContextMenuStrip> menu, Action<Point?> showDetails)
    {
        _settings = settings;
        _monitor = monitor;
        _menu = menu;
        _showDetails = showDetails;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); // nearly transparent but hit-testable
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Title = "Claude Status";

        _rows.Children.Add(_session.Panel);
        _rows.Children.Add(_weekly.Panel);
        var root = new Grid { Margin = new Thickness(6, 2, 6, 2) };
        root.Children.Add(_rows);
        root.Children.Add(_status);
        Content = root;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Native.MakeToolWindow(_hwnd);
        };
        SizeChanged += (_, _) => Place();

        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        MouseRightButtonUp += (_, e) =>
        {
            _menu().Show(System.Windows.Forms.Cursor.Position);
            e.Handled = true;
        };

        _monitor.Changed += Render;
        _placeTimer.Tick += (_, _) => OnTick();
        ApplyTheme(Theme.TaskbarIsLight());
        Render();
    }

    public void Start()
    {
        Show();
        _visible = true;
        Place();
        _placeTimer.Start();
    }

    void OnTick()
    {
        // Theme and countdowns change slowly; check every few seconds.
        if (++_tick % 5 == 0)
        {
            var light = Theme.TaskbarIsLight();
            if (light != _light) ApplyTheme(light);
        }
        if (_tick % 30 == 0) Render();
        Place();
    }

    void ApplyTheme(bool light)
    {
        _light = light;
        var fg = new SolidColorBrush(light ? Color.FromRgb(0x1F, 0x1F, 0x1F) : Color.FromRgb(0xF3, 0xF3, 0xF3));
        var track = new SolidColorBrush(light ? Color.FromArgb(0x40, 0, 0, 0) : Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF));
        Foreground = fg;
        _status.Foreground = fg;
        _session.SetColors(fg, track);
        _weekly.SetColors(fg, track);
    }

    void Render()
    {
        var snap = _monitor.Last;
        if (snap is null)
        {
            _rows.Visibility = Visibility.Collapsed;
            _status.Visibility = Visibility.Visible;
            _status.Text = _monitor.State switch
            {
                MonitorState.SignedOut => "Claude · sign in",
                MonitorState.Loading => "Claude · …",
                _ => "Claude · offline",
            };
            ToolTip = _monitor.Message;
            return;
        }

        _status.Visibility = Visibility.Collapsed;
        _rows.Visibility = Visibility.Visible;
        _session.Set(snap.Session);
        _weekly.Set(snap.Weekly);
        ToolTip = _monitor.State == MonitorState.Error
            ? $"Last update {snap.FetchedAt:t} – {_monitor.Message}"
            : $"Updated {snap.FetchedAt:t}";
        Opacity = _monitor.State == MonitorState.Error ? 0.6 : 1.0;
    }

    /// <summary>Keep the widget glued to the taskbar and above it (the taskbar re-takes the top when clicked).</summary>
    void Place()
    {
        if (_hwnd == IntPtr.Zero) return;

        var tb = Native.GetTaskbar();
        var shouldShow = _settings.ShowTaskbarWidget && tb is not null && !tb.AutoHide && !Native.IsForegroundFullscreen();
        if (!shouldShow)
        {
            if (_visible) { Native.ShowWindow(_hwnd, Native.SW_HIDE); _visible = false; }
            return;
        }

        Native.GetWindowRect(_hwnd, out var me);
        var b = tb!.Bounds;
        var scale = Native.GetDpiForWindow(_hwnd) / 96.0;
        int x, y;

        if (tb.Edge is Native.Edge.Bottom or Native.Edge.Top)
        {
            int right = _settings.WidgetRightOffset is int off
                ? b.Right - off
                : (tb.Tray?.Left ?? b.Right - (int)(260 * scale)) - (int)(4 * scale);
            x = Math.Clamp(right - me.Width, b.Left, Math.Max(b.Left, b.Right - me.Width));
            y = b.Top + (b.Height - me.Height) / 2;
        }
        else
        {
            x = b.Left + (b.Width - me.Width) / 2;
            y = (tb.Tray?.Top ?? b.Bottom - (int)(200 * scale)) - me.Height - (int)(4 * scale);
        }

        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        _visible = true;
    }

    void OnDown(object sender, MouseButtonEventArgs e)
    {
        Native.GetWindowRect(_hwnd, out var me);
        _dragStartCursorX = System.Windows.Forms.Cursor.Position.X;
        _dragStartLeft = me.Left;
        _pressed = true;
        _dragging = false;
        CaptureMouse();
    }

    void OnMove(object sender, MouseEventArgs e)
    {
        if (!_pressed) return;
        var dx = System.Windows.Forms.Cursor.Position.X - _dragStartCursorX;
        if (!_dragging && Math.Abs(dx) < 5) return;
        _dragging = true;

        var tb = Native.GetTaskbar();
        if (tb is null || tb.Edge is Native.Edge.Left or Native.Edge.Right) return;
        Native.GetWindowRect(_hwnd, out var me);
        _settings.WidgetRightOffset = Math.Max(0, tb.Bounds.Right - (_dragStartLeft + dx + me.Width));
        Place();
    }

    void OnUp(object sender, MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        if (!_pressed) return;
        _pressed = false;
        if (_dragging) _settings.Save();
        else
        {
            Native.GetWindowRect(_hwnd, out var me);
            _showDetails(new Point(me.Right, me.Top));
        }
    }

    sealed class Row
    {
        const double TrackWidth = 44;

        public StackPanel Panel { get; } = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
        readonly TextBlock _label;
        readonly Border _track = new() { Width = TrackWidth, Height = 5, CornerRadius = new CornerRadius(2.5), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) };
        readonly Border _fill = new() { Height = 5, CornerRadius = new CornerRadius(2.5), HorizontalAlignment = HorizontalAlignment.Left };
        readonly TextBlock _pct = new() { FontSize = 11, Width = 30, TextAlignment = TextAlignment.Right, FontWeight = FontWeights.SemiBold };
        readonly TextBlock _reset = new() { FontSize = 10, Width = 44, Margin = new Thickness(5, 0, 0, 0), Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center };

        public Row(string label)
        {
            _label = new TextBlock { Text = label, FontSize = 10, Width = 16, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center };
            _track.Child = _fill;
            Panel.Children.Add(_label);
            Panel.Children.Add(_track);
            Panel.Children.Add(_pct);
            Panel.Children.Add(_reset);
        }

        public void SetColors(Brush fg, Brush track)
        {
            _label.Foreground = _pct.Foreground = _reset.Foreground = fg;
            _track.Background = track;
        }

        public void Set(LimitWindow? w)
        {
            Panel.Visibility = w is null ? Visibility.Collapsed : Visibility.Visible;
            if (w is null) return;
            _fill.Width = TrackWidth * w.Percent / 100.0;
            _fill.Background = new SolidColorBrush(Theme.Level(w.Percent));
            _pct.Text = $"{w.Percent:0}%";
            _reset.Text = Theme.Countdown(w.ResetsAt);
        }
    }
}
