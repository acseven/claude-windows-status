using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ClaudeStatus;

/// <summary>
/// Welcome tour: five short steps (what the app does, the taskbar bars and tray icons, what to expect
/// the first time, details and alerts, signing in). Opens by itself once while no account is signed
/// in (ShowWelcomeTour policy), or with "ClaudeStatus.exe --welcome". The last step opens the sign-in
/// window. It is an ordinary top-level window: closing it closes only the tour, and a close request
/// from outside (taskkill) is handled by Shell like for any other window.
/// </summary>
public sealed class WelcomeWindow : Window
{
    const int Steps = 5;
    static WelcomeWindow? _open;

    readonly Action? _signIn;
    readonly bool _signedIn;
    readonly string? _email;
    readonly bool _light;
    readonly Brush _fg, _muted, _panel, _taskbar, _track, _line, _bg;
    static readonly Brush Orange = new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x57));
    static readonly Brush Amber = new SolidColorBrush(Color.FromRgb(0xF2, 0xA1, 0x2C));

    readonly ContentControl _art = new() { Height = 210 };
    readonly TextBlock _title = new() { FontSize = 24, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    readonly StackPanel _text = new();
    readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _counter = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
    readonly Button _skip = new(), _back = new(), _next = new();
    int _step;

    /// <summary>Shows the tour, or brings the open one to the front.</summary>
    /// <param name="signIn">Opens the sign-in window (null: the last step only closes the tour).</param>
    /// <param name="signedIn">An account is signed in already: the last step says so instead.</param>
    public static void ShowSingle(Action? signIn, bool signedIn)
    {
        if (_open is not null)
        {
            _open.Activate();
            return;
        }
        _open = new WelcomeWindow(signIn, signedIn);
        _open.Closed += (_, _) => _open = null;
        _open.Show();
        _open.Activate();
    }

    public static bool IsOpen => _open is not null;

    WelcomeWindow(Action? signIn, bool signedIn)
    {
        _signIn = signIn;
        _signedIn = signedIn;
        _email = signedIn ? null : WindowsAccount.SuggestedEmail;
        _light = Theme.TaskbarIsLight();
        Brush B(byte l, byte d) => new SolidColorBrush(_light ? Color.FromRgb(l, l, l) : Color.FromRgb(d, d, d));
        _bg = B(0xF9, 0x2B);
        _fg = B(0x1A, 0xF0);
        _muted = B(0x5F, 0xA8);
        _panel = B(0xEC, 0x20);
        _taskbar = B(0xDD, 0x17);
        _track = B(0xC8, 0x48);
        _line = B(0xC8, 0x50);

        Title = L.WelcomeTitle;
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Background = _bg;
        Foreground = _fg;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 14;

        _skip.Content = L.Skip;
        _back.Content = L.Back;
        foreach (var b in new[] { _skip, _back, _next })
        {
            b.Padding = new Thickness(14, 4, 14, 4);
            b.Margin = new Thickness(8, 0, 0, 0);
            b.MinWidth = 80;
        }
        _next.IsDefault = true;
        _skip.Click += (_, _) => Go(Steps - 1);
        _back.Click += (_, _) => Go(_step - 1);
        _next.Click += (_, _) => Next();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key == Key.Left && _step > 0) Go(_step - 1);
            else if (e.Key == Key.Right && _step < Steps - 1) Go(_step + 1);
        };

        var footer = new DockPanel { Margin = new Thickness(0, 22, 0, 0), LastChildFill = false };
        var progress = new StackPanel { Orientation = Orientation.Horizontal };
        progress.Children.Add(_dots);
        progress.Children.Add(_counter);
        _counter.Foreground = _muted;
        _counter.FontSize = 12;
        DockPanel.SetDock(progress, Dock.Left);
        footer.Children.Add(progress);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(_skip);
        buttons.Children.Add(_back);
        buttons.Children.Add(_next);
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);

        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        root.Children.Add(_art);
        _title.Margin = new Thickness(0, 18, 0, 8);
        root.Children.Add(_title);
        root.Children.Add(_text);
        root.Children.Add(footer);
        Content = root;
        Go(0);
    }

    void Next()
    {
        if (_step < Steps - 1) { Go(_step + 1); return; }
        Close();
        if (!_signedIn) _signIn?.Invoke();
    }

    void Go(int step)
    {
        _step = Math.Clamp(step, 0, Steps - 1);
        _text.Children.Clear();
        switch (_step)
        {
            case 0:
                _art.Content = ArtWelcome();
                _title.Text = L.Welcome1Title;
                Para(L.Welcome1Lead, _fg, 16, FontWeights.SemiBold);
                Para(L.Welcome1Text);
                break;
            case 1:
                _art.Content = ArtTaskbar();
                _title.Text = L.Welcome2Title;
                Para(L.Welcome2Text);
                break;
            case 2:
                _art.Content = ArtFirstUse();
                _title.Text = L.Welcome3Title;
                Para(L.Welcome3Text);
                _text.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0x30, 0xF2, 0xA1, 0x2C)),
                    BorderBrush = Amber,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(12, 8, 12, 8),
                    Margin = new Thickness(0, 10, 0, 0),
                    Child = new TextBlock { Text = L.Welcome3Warning, TextWrapping = TextWrapping.Wrap, FontSize = 13 },
                });
                break;
            case 3:
                _art.Content = ArtDetails();
                _title.Text = L.Welcome4Title;
                Para(L.Welcome4Text);
                break;
            default:
                _art.Content = ArtSignIn();
                _title.Text = L.Welcome5Title;
                Para(_signedIn ? L.Welcome5Done : L.Welcome5Text);
                break;
        }

        _dots.Children.Clear();
        for (var i = 0; i < Steps; i++)
            _dots.Children.Add(new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 6, 0), Fill = i == _step ? Orange : _track });
        _counter.Text = L.StepOf(_step + 1, Steps);
        var last = _step == Steps - 1;
        _skip.Visibility = last ? Visibility.Collapsed : Visibility.Visible;
        _back.Visibility = _step > 0 ? Visibility.Visible : Visibility.Collapsed;
        _next.Content = !last ? L.Next : _signedIn || _signIn is null ? L.Close : L.SignInButton;
    }

    void Para(string text, Brush? color = null, double size = 14, FontWeight? weight = null) =>
        _text.Children.Add(new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = color ?? _muted,
            FontSize = size,
            FontWeight = weight ?? FontWeights.Normal,
            LineHeight = size * 1.45,
            Margin = new Thickness(0, 0, 0, 6),
        });

    // ---- Illustrations (drawn, so they follow the language and the light/dark taskbar) -----------

    Border Panel(UIElement child, VerticalAlignment align = VerticalAlignment.Center) => new()
    {
        Background = _panel,
        CornerRadius = new CornerRadius(8),
        ClipToBounds = true,
        Child = child,
    };

    TextBlock Txt(string text, double size = 11, Brush? color = null, FontWeight? weight = null) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = color ?? _fg,
        FontWeight = weight ?? FontWeights.Normal,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>The two-row taskbar widget (5h / 7d bars) with sample values.</summary>
    StackPanel Widget()
    {
        StackPanel Row(string label, double pct, string countdown)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
            row.Children.Add(new TextBlock { Text = label, Width = 18, FontSize = 10, Foreground = _muted, VerticalAlignment = VerticalAlignment.Center });
            var track = new Grid { Width = 44, Height = 5, VerticalAlignment = VerticalAlignment.Center };
            track.Children.Add(new Border { Background = _track, CornerRadius = new CornerRadius(3) });
            track.Children.Add(new Border { Background = Orange, CornerRadius = new CornerRadius(3), Width = 44 * pct / 100, HorizontalAlignment = HorizontalAlignment.Left });
            row.Children.Add(track);
            row.Children.Add(new TextBlock { Text = $"{pct:0}%", Width = 30, TextAlignment = TextAlignment.Right, FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = _fg, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = countdown, Margin = new Thickness(5, 0, 0, 0), FontSize = 10, Foreground = _muted, VerticalAlignment = VerticalAlignment.Center });
            return row;
        }
        var w = new StackPanel();
        w.Children.Add(Row("5h", 62, L.Hours(2, 10)));
        w.Children.Add(Row("7d", 18, L.Days(4, 2)));
        return w;
    }

    static Border TrayIcon(string text, bool filled) => new()
    {
        Width = 18,
        Height = 18,
        CornerRadius = new CornerRadius(4),
        Background = filled ? Orange : Brushes.Transparent,
        BorderBrush = Orange,
        BorderThickness = new Thickness(filled ? 0 : 1.5),
        Margin = new Thickness(4, 0, 4, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = text,
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            Foreground = filled ? new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)) : Orange,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        },
    };

    StackPanel Clock()
    {
        var c = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var now = DateTime.Now;
        c.Children.Add(new TextBlock { Text = now.ToShortTimeString(), FontSize = 10, Foreground = _fg, HorizontalAlignment = HorizontalAlignment.Right });
        c.Children.Add(new TextBlock { Text = now.ToShortDateString(), FontSize = 10, Foreground = _fg, HorizontalAlignment = HorizontalAlignment.Right });
        return c;
    }

    /// <summary>A taskbar strip: what goes before the overflow arrow, the arrow, then the tray icons.</summary>
    Border Taskbar(UIElement widget, params UIElement[] tray)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 12, 0) };
        row.Children.Add(widget);
        row.Children.Add(new TextBlock { Text = "^", FontSize = 12, Foreground = _muted, Margin = new Thickness(12, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        foreach (var t in tray) row.Children.Add(t);
        row.Children.Add(Clock());
        return new Border { Height = 48, Background = _taskbar, VerticalAlignment = VerticalAlignment.Bottom, Child = row };
    }

    UIElement ArtWelcome()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var w = Widget();
        w.LayoutTransform = new ScaleTransform(1.7, 1.7);
        row.Children.Add(w);
        var icons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24, 0, 0, 0), LayoutTransform = new ScaleTransform(1.7, 1.7) };
        icons.Children.Add(TrayIcon("62", true));
        icons.Children.Add(TrayIcon("18", false));
        row.Children.Add(icons);
        return Panel(row);
    }

    UIElement ArtTaskbar()
    {
        var widget = new Border
        {
            Child = Widget(),
            Padding = new Thickness(6, 3, 6, 3),
            BorderBrush = Orange,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        return Panel(Taskbar(widget, TrayIcon("62", true), TrayIcon("18", false)));
    }

    UIElement ArtFirstUse()
    {
        var grid = new Grid();

        // Right-click menu > Display (left).
        var menu = new StackPanel();
        void Item(string text, bool check, bool bold = false)
        {
            var r = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            r.Children.Add(Txt(check ? "✓" : "", 11, Orange, FontWeights.Bold));
            ((TextBlock)r.Children[0]).Width = 16;
            r.Children.Add(Txt(text, 11, _fg, bold ? FontWeights.SemiBold : FontWeights.Normal));
            menu.Children.Add(r);
        }
        Item(L.Display + "  ›", false, true);
        Item(L.TaskbarWidget, true);
        Item(L.SessionIcon, true);
        Item(L.WeeklyIcon, true);
        grid.Children.Add(new Border
        {
            Background = _bg,
            BorderBrush = _line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 14, 8),
            Margin = new Thickness(16, 16, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = menu,
        });

        // Hidden-icons flyout above the ^ with the two icons, and the drag arrow (right).
        var hidden = new StackPanel { Orientation = Orientation.Horizontal };
        hidden.Children.Add(TrayIcon("–", true));
        hidden.Children.Add(TrayIcon("–", false));
        grid.Children.Add(new Border
        {
            Background = _bg,
            BorderBrush = _line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 40, 190, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Child = hidden,
        });
        var drag = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 88, 110, 0) };
        drag.Children.Add(new Line { X1 = 0, Y1 = 0, X2 = 60, Y2 = 50, Stroke = Orange, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 3, 2 } });
        drag.Children.Add(Txt(L.Drag, 11, Orange, FontWeights.SemiBold));
        grid.Children.Add(drag);

        var signIn = Txt(L.WidgetSignIn, 11, _muted);
        grid.Children.Add(Taskbar(signIn));
        return Panel(grid);
    }

    UIElement ArtDetails()
    {
        var card = new StackPanel();
        card.Children.Add(Txt(L.UsageTitle, 13, _fg, FontWeights.SemiBold));
        void Limit(string label, double pct, string countdown)
        {
            card.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = _fg, Margin = new Thickness(0, 8, 0, 3) });
            var track = new Grid { Height = 6 };
            track.Children.Add(new Border { Background = _track, CornerRadius = new CornerRadius(3) });
            track.Children.Add(new Border { Background = Orange, CornerRadius = new CornerRadius(3), Width = 248 * pct / 100, HorizontalAlignment = HorizontalAlignment.Left });
            card.Children.Add(track);
            card.Children.Add(new TextBlock { Text = L.Used(pct) + " · " + L.ResetsIn(countdown), FontSize = 10, Foreground = _muted, Margin = new Thickness(0, 3, 0, 0) });
        }
        Limit(L.LimitLabel("five_hour"), 62, L.Hours(2, 10));
        Limit(L.LimitLabel("seven_day"), 18, L.Days(4, 2));

        var toast = new StackPanel();
        toast.Children.Add(Txt(L.SampleAlertTitle, 12, _fg, FontWeights.SemiBold));
        toast.Children.Add(Txt(L.SampleAlertText, 11, _muted));

        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Border Box(UIElement child, Thickness pad) => new()
        {
            Width = 280,
            Background = _bg,
            BorderBrush = _line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = pad,
            Margin = new Thickness(0, 0, 0, 8),
            Child = child,
        };
        stack.Children.Add(Box(card, new Thickness(14, 10, 14, 12)));
        var t = Box(toast, new Thickness(12, 8, 12, 8));
        t.BorderBrush = Amber;
        t.Margin = new Thickness(0);
        stack.Children.Add(t);
        return Panel(stack);
    }

    UIElement ArtSignIn()
    {
        var stack = new StackPanel { Width = 380, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (_signedIn)
        {
            stack.Children.Add(new TextBlock { Text = "✓", FontSize = 56, Foreground = Orange, HorizontalAlignment = HorizontalAlignment.Center });
            return Panel(stack);
        }
        if (_email is not null)
        {
            stack.Children.Add(new TextBlock { Text = L.Welcome5Email, FontSize = 12, Foreground = _muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });
            stack.Children.Add(new TextBox
            {
                Text = _email,
                IsReadOnly = true,
                FontSize = 14,
                Height = 34,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(8, 0, 8, 0),
                Background = _bg,
                Foreground = _fg,
                BorderBrush = _line,
            });
        }
        stack.Children.Add(new TextBlock
        {
            Text = L.Welcome5Sso,
            FontSize = 12,
            Foreground = _muted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
        });
        return Panel(stack);
    }
}
