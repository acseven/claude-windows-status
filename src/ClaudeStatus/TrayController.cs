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
    readonly WinForms.ToolStripMenuItem _style = new("Taskbar display");
    readonly WinForms.ToolStripMenuItem _interval = new("Refresh every");
    readonly WinForms.ToolStripMenuItem _notify = new("Notifications");
    readonly WinForms.ToolStripMenuItem _autostart = new("Start with Windows");
    readonly WinForms.ToolStripMenuItem _signOut = new("Sign out");
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

        foreach (var (label, style) in new[] { ("Bars (two rows)", WidgetStyle.Bars), ("Compact (one line)", WidgetStyle.Compact), ("Tray icon only", WidgetStyle.IconOnly) })
        {
            var item = new WinForms.ToolStripMenuItem(label) { Tag = style };
            item.Click += (_, _) => { _settings.WidgetStyle = style; _settings.Save(); SyncMenu(); };
            _style.DropDownItems.Add(item);
        }
        _style.DropDownItems.Add(new WinForms.ToolStripSeparator());
        _style.DropDownItems.Add("Reset position", null, (_, _) => resetPosition());

        foreach (var minutes in new[] { 1, 2, 5, 10, 15 })
        {
            var item = new WinForms.ToolStripMenuItem($"{minutes} min") { Tag = minutes };
            item.Click += (_, _) => { _settings.RefreshMinutes = minutes; _settings.Save(); _monitor.ApplyInterval(); SyncMenu(); };
            _interval.DropDownItems.Add(item);
        }

        _notify.Click += (_, _) => { _settings.NotificationsEnabled = !_settings.NotificationsEnabled; _settings.Save(); SyncMenu(); };
        _autostart.Click += (_, _) => { AutoStart.SetUser(!AutoStart.IsUserEnabled()); SyncMenu(); };
        _signOut.Click += (_, _) => _monitor.SetSessionKey(null);

        Menu.Items.Add(_header);
        Menu.Items.Add(new WinForms.ToolStripSeparator());
        Menu.Items.Add("Show details", null, (_, _) => _showDetails(null));
        Menu.Items.Add("Refresh now", null, async (_, _) => await _monitor.RefreshAsync());
        Menu.Items.Add("Open usage page", null, (_, _) => OpenUsagePage());
        Menu.Items.Add(new WinForms.ToolStripSeparator());
        Menu.Items.Add(_style);
        Menu.Items.Add(_interval);
        Menu.Items.Add(_notify);
        Menu.Items.Add(_autostart);
        Menu.Items.Add(new WinForms.ToolStripSeparator());
        Menu.Items.Add("Sign in…", null, (_, _) => _signIn());
        Menu.Items.Add(_signOut);
        Menu.Items.Add(new WinForms.ToolStripSeparator());
        Menu.Items.Add("Exit", null, (_, _) => _exit());
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
        _weeklyIcon.Visible = true;
        _icon.Visible = true;
    }

    public static void OpenUsagePage() =>
        Process.Start(new ProcessStartInfo("https://claude.ai/settings/usage") { UseShellExecute = true });

    /// <summary>Windows 10/11 show this as a native toast in the notification centre.</summary>
    public void Notify(string title, string text) =>
        _icon.ShowBalloonTip(10_000, title, text, WinForms.ToolTipIcon.Warning);

    void SyncMenu()
    {
        var p = Policy.Current;
        foreach (WinForms.ToolStripItem i in _style.DropDownItems)
            if (i is WinForms.ToolStripMenuItem m && m.Tag is WidgetStyle s) m.Checked = s == _settings.EffectiveStyle;
        _style.Enabled = p.WidgetStyle is null;
        foreach (WinForms.ToolStripMenuItem m in _interval.DropDownItems)
            m.Checked = (int)m.Tag! == _settings.EffectiveRefreshMinutes;
        _interval.Enabled = p.RefreshMinutes is null;
        _notify.Checked = _settings.EffectiveNotifications;
        _notify.Enabled = p.DisableNotifications is null;

        var machine = AutoStart.IsMachineEnabled();
        _autostart.Checked = machine || AutoStart.IsUserEnabled();
        _autostart.Enabled = !machine;
        _autostart.Text = machine ? "Start with Windows (set by administrator)" : "Start with Windows";

        _signOut.Enabled = _monitor.State != MonitorState.SignedOut;
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
