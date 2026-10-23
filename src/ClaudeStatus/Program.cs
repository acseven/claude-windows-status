using System.Windows;

namespace ClaudeStatus;

static class Program
{
    const string ShowSignal = @"Local\ClaudeStatus.Show";
    const string QuitSignal = @"Local\ClaudeStatus.Quit";

    [STAThread]
    static void Main(string[] args)
    {
        var options = Options.Parse(args);
        using var mutex = new Mutex(true, @"Local\ClaudeStatus.SingleInstance", out var first);
        if (!first)
        {
            // Already running in this session: starting it again (e.g. from the Start menu) shows
            // the details flyout; --quit asks the running copy to exit.
            Signal(options.Quit ? QuitSignal : ShowSignal);
            return;
        }
        if (options.Quit) return;

        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignal);
        using var quit = new EventWaitHandle(false, EventResetMode.AutoReset, QuitSignal);

        System.Windows.Forms.Application.EnableVisualStyles();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += (_, _) =>
        {
            var shell = new Shell(app, options);
            shell.Start();
            var listener = new Thread(() =>
            {
                var handles = new WaitHandle[] { show, quit };
                while (true)
                {
                    var which = WaitHandle.WaitAny(handles);
                    app.Dispatcher.BeginInvoke(which == 0 ? shell.ShowFromSignal : shell.Exit);
                    if (which == 1) return;
                }
            }) { IsBackground = true, Name = "single-instance" };
            listener.Start();
        };
        app.Run();
    }

    static void Signal(string name)
    {
        try
        {
            using var e = EventWaitHandle.OpenExisting(name);
            e.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The other copy is starting up or shutting down; nothing to do.
        }
    }
}

/// <summary>
/// Command line: --demo[=alert] shows sample data without signing in, --style=bars|compact|icon
/// overrides the display for this run, --open-details opens the flyout (used for screenshots),
/// --quit closes a running copy.
/// </summary>
sealed record Options(bool Demo, bool DemoAlert, WidgetStyle? Style, bool OpenDetails, bool Quit)
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
        return new Options(Has("--demo") || Has("--demo=alert"), Has("--demo=alert"), style, Has("--open-details"), Has("--quit"));
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
    bool _exiting;

    public Shell(Application app, Options options)
    {
        _app = app;
        _options = options;
        // Demo runs never touch the user's saved settings or session.
        _settings = options.Demo ? new Settings { InMemoryOnly = true } : Settings.Load();
        if (options.Style is { } style) _settings.WidgetStyle = style;
        _monitor = new UsageMonitor(_settings);
        _tray = new TrayController(_settings, _monitor, SignIn, ShowDetails, () => _widget!.ResetPosition(), Exit);
        _widget = new TaskbarWidget(_settings, _monitor, _tray.ShowMenuAt, ShowDetails);
        // The installer (and Windows) ask the app to close by sending WM_CLOSE to its windows.
        _widget.Closing += (_, e) =>
        {
            if (_exiting) return;
            e.Cancel = true;
            // Close() cannot be called while the widget is inside its own Closing event.
            _widget.Dispatcher.BeginInvoke(new Action(Exit));
        };
        _notifier = new Notifier(_settings, _tray.Notify);
        _monitor.AccountRefused += message => _tray.Notify("Claude account not allowed", message);

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

    /// <summary>Signs in a Claude account in a clean browser profile and makes it the active one.</summary>
    void SignIn()
    {
        if (_signingIn) return;
        _signingIn = true;
        try
        {
            var login = new LoginWindow();
            if (login.ShowDialog() == true && !string.IsNullOrEmpty(login.SessionKey))
                _monitor.AddAccount(login.SessionKey);
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
        _details = new DetailsWindow(_monitor, p, _tray.ShowMenuAt, Exit);
        _details.Closed += (_, _) => _details = null;
        _details.Show();
    }

    /// <summary>Started again while running: show the flyout (or sign-in) where it can be seen.</summary>
    public void ShowFromSignal()
    {
        if (_details is not null) return;
        ShowDetails(_widget.Anchor);
    }

    public void Exit()
    {
        if (_exiting) return;
        _exiting = true;
        _details?.Close();
        _widget.Close();
        _tray.Dispose();
        _monitor.Dispose();
        _app.Shutdown();
    }
}
