using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using WinForms = System.Windows.Forms;

namespace ClaudeStatus;

/// <summary>
/// Two notification-area icons – 5-hour session (filled tile) and 7-day limit (outlined tile) –
/// plus the app menu shared with the widget.
/// </summary>
public sealed class TrayController : IDisposable
{
    readonly Settings _settings;
    readonly UsageMonitor _monitor;
    readonly Action _signIn;
    readonly Action<System.Windows.Point?> _showDetails;
    readonly Action _exit;

    readonly WinForms.NotifyIcon _icon = new();       // 5h; also carries the notifications
    readonly WinForms.NotifyIcon _weeklyIcon = new(); // 7d
    readonly WinForms.ToolStripMenuItem _header = new() { Enabled = false };
    readonly WinForms.ToolStripMenuItem _display = new("Display");
    readonly WinForms.ToolStripMenuItem _showWidget = new("Taskbar widget");
    readonly WinForms.ToolStripMenuItem _showSession = new("5-hour tray icon");
    readonly WinForms.ToolStripMenuItem _showWeekly = new("7-day tray icon");
    readonly WinForms.ToolStripMenuItem _bars = new("Widget: two rows") { Tag = WidgetStyle.Bars };
    readonly WinForms.ToolStripMenuItem _compact = new("Widget: one line") { Tag = WidgetStyle.Compact };
    readonly System.Windows.Forms.Timer _hideAfterNotify = new() { Interval = 15_000 };
    readonly WinForms.ToolStripMenuItem _interval = new("Refresh every");
    readonly WinForms.ToolStripMenuItem _notify = new("Notifications");
    readonly WinForms.ToolStripMenuItem _history = new("Usage history (Stats)");
    readonly WinForms.ToolStripMenuItem _autostart = new("Start with Windows");
    readonly WinForms.ToolStripMenuItem _account = new("Account");
    readonly WinForms.ToolStripMenuItem _organization = new("Organization");
    readonly WinForms.ToolStripMenuItem _signInTop = new("Sign in…");
    readonly Dictionary<WinForms.NotifyIcon, IntPtr> _handles = new();

    public WinForms.ContextMenuStrip Menu { get; } = new();

    public TrayController(Settings settings, UsageMonitor monitor, Action signIn,
        Action<System.Windows.Point?> showDetails, Action resetPosition, Action exit)
    {
        _settings = settings;
        _monitor = monitor;
        _signIn = signIn;
        _showDetails = showDetails;
        _exit = exit;

        // Display ▸ three independent switches, then the widget layout.
        _showWidget.Click += (_, _) => Toggle(() => _settings.ShowTaskbarWidget = !_settings.ShowTaskbarWidget);
        _showSession.Click += (_, _) => Toggle(() => _settings.ShowSessionTrayIcon = !_settings.ShowSessionTrayIcon);
        _showWeekly.Click += (_, _) => Toggle(() => _settings.ShowWeeklyTrayIcon = !_settings.ShowWeeklyTrayIcon);
        foreach (var item in new[] { _bars, _compact })
            item.Click += (_, _) => { _settings.WidgetStyle = (WidgetStyle)item.Tag!; _settings.Save(); SyncMenu(); };
        _display.DropDownItems.Add(_showWidget);
        _display.DropDownItems.Add(_showSession);
        _display.DropDownItems.Add(_showWeekly);
        _display.DropDownItems.Add(new WinForms.ToolStripSeparator());
        _display.DropDownItems.Add(_bars);
        _display.DropDownItems.Add(_compact);
        _display.DropDownItems.Add("Reset widget position", null, (_, _) => resetPosition());
        _hideAfterNotify.Tick += (_, _) => { _hideAfterNotify.Stop(); ApplyIconVisibility(); };

        foreach (var minutes in new[] { 1, 2, 5, 10, 15 })
        {
            var item = new WinForms.ToolStripMenuItem($"{minutes} min") { Tag = minutes };
            item.Click += (_, _) => { _settings.RefreshMinutes = minutes; _settings.Save(); _monitor.ApplyInterval(); SyncMenu(); };
            _interval.DropDownItems.Add(item);
        }

        _notify.Click += (_, _) => { _settings.NotificationsEnabled = !_settings.NotificationsEnabled; _settings.Save(); SyncMenu(); };
        _history.Click += (_, _) =>
        {
            if (_settings.HistoryEnabled)
            {
                var answer = System.Windows.MessageBox.Show(
                    "Turn off usage history? The history stored on this PC for all accounts is deleted.",
                    "Claude Status", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
                if (answer != System.Windows.MessageBoxResult.Yes) return;
                HistoryStore.DeleteAll();
            }
            _settings.HistoryEnabled = !_settings.HistoryEnabled;
            _settings.Save();
            SyncMenu();
        };
        _autostart.Click += (_, _) => { AutoStart.SetUser(!AutoStart.IsUserEnabled()); SyncMenu(); };
        _signInTop.Click += (_, _) => _signIn();

        Menu.Items.Add(_header);
        Menu.Items.Add(new WinForms.ToolStripSeparator());
        Menu.Items.Add("Show details", null, (_, _) => _showDetails(null));
        Menu.Items.Add("Refresh now", null, async (_, _) => await _monitor.RefreshAsync());
        Menu.Items.Add("Open usage page", null, (_, _) => OpenUsagePage());
        Menu.Items.Add(new WinForms.ToolStripSeparator());
        Menu.Items.Add(_display);
        Menu.ShowItemToolTips = true;
        _display.DropDown.ShowItemToolTips = true;
        Menu.Items.Add(_interval);
        Menu.Items.Add(_notify);
        Menu.Items.Add(_history);
        Menu.Items.Add(_autostart);
        Menu.Items.Add(new WinForms.ToolStripSeparator());
        Menu.Items.Add(_signInTop);
        Menu.Items.Add(_account);
        Menu.Items.Add(_organization);
        Menu.Items.Add(new WinForms.ToolStripSeparator());
        Menu.Items.Add("Quit Claude Status", null, (_, _) => _exit());
        Menu.Opening += (_, _) => SyncMenu();

        foreach (var icon in new[] { _icon, _weeklyIcon })
        {
            icon.ContextMenuStrip = Menu;
            icon.MouseClick += (_, e) =>
            {
                if (e.Button != WinForms.MouseButtons.Left) return;
                if (_monitor.State == MonitorState.SignedOut) _signIn();
                else _showDetails(null);
            };
        }
        _icon.BalloonTipClicked += (_, _) => _showDetails(null);

        _monitor.Changed += Render;
        Render();
        ApplyIconVisibility();
    }

    public static void OpenUsagePage() =>
        Process.Start(new ProcessStartInfo("https://claude.ai/settings/usage") { UseShellExecute = true });

    /// <summary>Windows 10/11 show this as a native toast in the notification centre.</summary>
    public void Notify(string title, string text)
    {
        // A notification needs a visible icon: use one that is shown, or show the 5h icon briefly.
        var icon = _icon.Visible ? _icon : _weeklyIcon.Visible ? _weeklyIcon : null;
        if (icon is null)
        {
            icon = _icon;
            icon.Visible = true;
            _hideAfterNotify.Stop();
            _hideAfterNotify.Start();
        }
        icon.ShowBalloonTip(10_000, title, text, WinForms.ToolTipIcon.Warning);
    }

    bool _widgetShown = true; // until the widget reports otherwise (avoids a fallback icon flicker at start)

    /// <summary>How many of the three (widget, 5h icon, 7d icon) are switched on.</summary>
    int SwitchedOn =>
        (_settings.EffectiveShowWidget ? 1 : 0) + (_settings.ShowSessionTrayIcon ? 1 : 0) + (_settings.ShowWeeklyTrayIcon ? 1 : 0);

    void Toggle(Action change)
    {
        change();
        if (SwitchedOn == 0) change(); // the last one cannot be switched off (menu also prevents it)
        _settings.Save();
        ApplyIconVisibility();
        SyncMenu();
    }

    /// <summary>Called by the widget when it appears on or disappears from the taskbar.</summary>
    public void SetWidgetShown(bool shown)
    {
        _widgetShown = shown;
        ApplyIconVisibility();
    }

    void ApplyIconVisibility()
    {
        // If the widget is the only thing switched on but cannot be shown right now (no free space on
        // the taskbar, full screen, auto-hide), the 5h icon stands in so the app is always reachable.
        var fallback = !_settings.ShowSessionTrayIcon && !_settings.ShowWeeklyTrayIcon && !_widgetShown;
        // Added right to left in the tray: 7d first so 5h ends up on its left.
        _weeklyIcon.Visible = _settings.ShowWeeklyTrayIcon;
        _icon.Visible = _settings.ShowSessionTrayIcon || fallback || _hideAfterNotify.Enabled;
    }

    void SyncMenu()
    {
        var p = Policy.Current;
        // At least one of the three stays on: the last one switched on is greyed out.
        var last = SwitchedOn == 1;
        _showWidget.Checked = _settings.EffectiveShowWidget;
        _showWidget.Enabled = p.WidgetStyle is null && !(last && _showWidget.Checked);
        _showSession.Checked = _settings.ShowSessionTrayIcon;
        _showSession.Enabled = !(last && _showSession.Checked);
        _showWeekly.Checked = _settings.ShowWeeklyTrayIcon;
        _showWeekly.Enabled = !(last && _showWeekly.Checked);
        foreach (var item in new[] { _showWidget, _showSession, _showWeekly })
            item.ToolTipText = item.Checked && !item.Enabled && last ? "At least one must stay on." : null;
        _bars.Checked = _settings.EffectiveStyle == WidgetStyle.Bars;
        _compact.Checked = _settings.EffectiveStyle == WidgetStyle.Compact;
        _bars.Enabled = _compact.Enabled = p.WidgetStyle is null && _settings.EffectiveShowWidget;
        foreach (WinForms.ToolStripMenuItem m in _interval.DropDownItems)
            m.Checked = (int)m.Tag! == _settings.EffectiveRefreshMinutes;
        _interval.Enabled = p.RefreshMinutes is null;
        _notify.Checked = _settings.EffectiveNotifications;
        _notify.Enabled = p.DisableNotifications is null;
        _history.Checked = _settings.EffectiveHistory;
        _history.Enabled = !p.DisableUsageHistory;
        _history.Text = p.DisableUsageHistory ? "Usage history (off by administrator)" : "Usage history (Stats)";

        var machine = AutoStart.IsMachineEnabled();
        _autostart.Checked = machine || AutoStart.IsUserEnabled();
        _autostart.Enabled = !machine;
        _autostart.Text = machine ? "Start with Windows (set by administrator)" : "Start with Windows";

        SyncAccounts();
    }

    /// <summary>Account ▸ (saved accounts, add, sign in again, sign out) and Organization ▸ (when several).</summary>
    void SyncAccounts()
    {
        var accounts = _settings.Accounts;
        _signInTop.Visible = accounts.Count == 0;
        _account.Visible = accounts.Count > 0;
        _account.DropDownItems.Clear();
        for (var i = 0; i < accounts.Count; i++)
        {
            // By reference: the list can change while the menu is open (merge, policy refusal).
            var target = accounts[i];
            var item = new WinForms.ToolStripMenuItem(target.Label) { Checked = i == _settings.ActiveAccount };
            item.Click += (_, _) => _monitor.SwitchTo(target);
            _account.DropDownItems.Add(item);
        }
        _account.DropDownItems.Add(new WinForms.ToolStripSeparator());
        _account.DropDownItems.Add("Add another account…", null, (_, _) => _signIn());
        if (_settings.Active is { } active)
        {
            if (_monitor.State == MonitorState.SignedOut)
                _account.DropDownItems.Add("Sign in again…", null, (_, _) => _signIn());
            _account.DropDownItems.Add($"Sign out of {active.Label}", null, (_, _) => _monitor.SignOutActive());
        }
        _account.Text = _settings.Active is { } a ? $"Account: {a.Label}" : "Account";

        var orgs = _monitor.Organizations;
        _organization.Visible = orgs.Count > 1;
        _organization.DropDownItems.Clear();
        foreach (var org in orgs)
        {
            var id = org.Id;
            var item = new WinForms.ToolStripMenuItem(string.IsNullOrEmpty(org.Name) ? id : org.Name)
            {
                Checked = id == _settings.Active?.OrganizationId,
            };
            item.Click += (_, _) => _monitor.SetOrganization(id);
            _organization.DropDownItems.Add(item);
        }
    }

    /// <summary>Shows the app menu at a screen point (widget right-click, flyout button).</summary>
    public void ShowMenuAt(System.Windows.Point screen)
    {
        Menu.Show(new Point((int)screen.X, (int)screen.Y));
        // Without the foreground, the menu would not close when clicking elsewhere.
        Native.SetForegroundWindow(Menu.Handle);
    }

    void Render()
    {
        var snap = _monitor.Last;
        var session = snap?.Session;
        var weekly = snap?.Weekly;

        _header.Text = snap is null
            ? "Claude – " + (_monitor.Message ?? "no data")
            : $"Claude – 5h {Pct(session)} · 7d {Pct(weekly)}";

        SetTip(_icon, snap is null ? "Claude session (5h)\n" + (_monitor.Message ?? "") : Tip("Claude session (5h)", session));
        SetTip(_weeklyIcon, snap is null ? "Claude weekly (7d)\n" + (_monitor.Message ?? "") : Tip("Claude weekly (7d)", weekly));
        SetIcon(_icon, session, outlined: false);
        SetIcon(_weeklyIcon, weekly, outlined: true);
    }

    static string Tip(string name, LimitWindow? w) =>
        w is null ? $"{name}\nno data" : $"{name}\n{w.Percent:0}% used · resets in {Theme.Countdown(w.ResetsAt)}";

    static void SetTip(WinForms.NotifyIcon icon, string tip) => icon.Text = tip.Length > 127 ? tip[..127] : tip;

    static string Pct(LimitWindow? w) => w is null ? "–" : $"{w.Percent:0}%";

    static Color ToDrawing(System.Windows.Media.Color c) => Color.FromArgb(c.R, c.G, c.B);

    void SetIcon(WinForms.NotifyIcon target, LimitWindow? w, bool outlined)
    {
        var text = w is null ? "–" : w.Percent >= 100 ? "!" : Math.Min(99, (int)Math.Round(w.Percent)).ToString();
        var level = w is null ? Color.FromArgb(0x80, 0x80, 0x80) : ToDrawing(Theme.Level(w.Percent));
        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            Color textColor;
            if (outlined)
            {
                // 7d: dark tile with a coloured ring, so the two icons are told apart at a glance.
                using var path = RoundedRect(new RectangleF(2, 2, size - 5, size - 5), 7);
                using var bg = new SolidBrush(Color.FromArgb(0x26, 0x26, 0x26));
                using var ring = new Pen(level, 4f);
                g.FillPath(bg, path);
                g.DrawPath(ring, path);
                textColor = Color.FromArgb(0xF3, 0xF3, 0xF3);
            }
            else
            {
                using var path = RoundedRect(new RectangleF(0, 0, size - 1, size - 1), 7);
                using var bg = new SolidBrush(level);
                g.FillPath(bg, path);
                textColor = Color.FromArgb(0x1A, 0x1A, 0x1A);
            }

            using var font = new Font("Segoe UI", text.Length > 1 ? 15f : 19f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var fg = new SolidBrush(textColor);
            var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, font, fg, new RectangleF(0, 1, size, size), fmt);
        }

        var h = bmp.GetHicon();
        var old = target.Icon;
        target.Icon = Icon.FromHandle(h);
        old?.Dispose();
        if (_handles.TryGetValue(target, out var prev)) Native.DestroyIcon(prev);
        _handles[target] = h;
    }

    static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public void Dispose()
    {
        _hideAfterNotify.Dispose();
        _monitor.Changed -= Render;
        foreach (var icon in new[] { _icon, _weeklyIcon })
        {
            icon.Visible = false;
            icon.Dispose();
        }
        foreach (var h in _handles.Values) Native.DestroyIcon(h);
        Menu.Dispose();
    }
}
