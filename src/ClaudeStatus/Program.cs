using System.Windows;

namespace ClaudeStatus;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\ClaudeStatus.SingleInstance", out var first);
        if (!first) return;

        System.Windows.Forms.Application.EnableVisualStyles();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += (_, _) => new Shell(app, Options.Parse(args)).Start();
        app.Run();
    }
}

/// <summary>
/// Command line: --demo[=alert] shows sample data without signing in, --style=bars|compact|icon
/// overrides the display for this run, --open-details opens the flyout (used for screenshots).
/// </summary>
sealed record Options(bool Demo, bool DemoAlert, WidgetStyle? Style, bool OpenDetails)
{
    public static Options Parse(string[] args)
    {
        bool Has(string a) => args.Any(x => x.Equals(a, StringComparison.OrdinalIgnoreCase));
        var style = args.FirstOrDefault(a => a.StartsWith("--style=", StringComparison.OrdinalIgnoreCase))?[8..].ToLowerInvariant() switch
        {
            "bars" => WidgetStyle.Bars,
            "compact" => WidgetStyle.Compact,
            "icon" => WidgetStyle.IconOnly,
            _ => (WidgetStyle?)null,
        };
        return new Options(Has("--demo") || Has("--demo=alert"), Has("--demo=alert"), style, Has("--open-details"));
    }
}

/// <summary>Wires the monitor, tray icon, taskbar widget and notifications together.</summary>
sealed class Shell
{
    readonly Application _app;
    readonly Options _options;
    readonly Settings _settings;
    readonly UsageMonitor _monitor;
    readonly TrayController _tray;
    readonly TaskbarWidget _widget;
    readonly Notifier _notifier;
    DetailsWindow? _details;
    bool _signingIn;

    public Shell(Application app, Options options)
    {
        _app = app;
        _options = options;
        // Demo runs never touch the user's saved settings or session.
        _settings = options.Demo ? new Settings { InMemoryOnly = true } : Settings.Load();
        if (options.Style is { } style) _settings.WidgetStyle = style;
        _monitor = new UsageMonitor(_settings);
        _tray = new TrayController(_settings, _monitor, SignIn, ShowDetails, () => _widget!.ResetPosition(), Exit);
        _widget = new TaskbarWidget(_settings, _monitor, () => _tray.Menu, ShowDetails);
        _notifier = new Notifier(_settings, _tray.Notify);

        _monitor.Changed += () =>
        {
            if (_monitor.State == MonitorState.Ok && _monitor.Last is { } snap) _notifier.Check(snap);
        };
    }

    public void Start()
    {
        _widget.Start();
        if (_options.Demo)
        {
            _monitor.LoadDemo(_options.DemoAlert);
            if (_options.OpenDetails) ShowDetails(_widget.Anchor);
            return;
        }
        _monitor.Start();
        if (_options.OpenDetails) ShowDetails(_widget.Anchor);
        if (string.IsNullOrEmpty(_settings.SessionKey))
            _tray.Notify("Claude Status", "Click here or the tray icon to sign in to your Claude account.");
    }

    void SignIn()
    {
        if (_signingIn) return;
        _signingIn = true;
        try
        {
            var login = new LoginWindow();
            if (login.ShowDialog() == true && !string.IsNullOrEmpty(login.SessionKey))
                _monitor.SetSessionKey(login.SessionKey);
        }
        finally
        {
            _signingIn = false;
        }
    }

    void ShowDetails(Point? anchor)
    {
        if (_details is not null) { _details.Close(); return; }
        if (_monitor.State == MonitorState.SignedOut && _monitor.Last is null) { SignIn(); return; }

        var p = anchor ?? new Point(System.Windows.Forms.Cursor.Position.X, System.Windows.Forms.Cursor.Position.Y);
        _details = new DetailsWindow(_monitor, p, SignIn);
        _details.Closed += (_, _) => _details = null;
        _details.Show();
    }

    void Exit()
    {
        _details?.Close();
        _widget.Close();
        _tray.Dispose();
        _monitor.Dispose();
        _app.Shutdown();
    }
}
