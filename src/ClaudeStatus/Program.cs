using System.Diagnostics;
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
        if (options.SelfTestFile is { } selfTest)
        {
            RunNetworkSelfTest(selfTest);
            return;
        }
        using var mutex = new Mutex(true, @"Local\ClaudeStatus.SingleInstance", out var first);
        if (!first)
        {
            if (options.Quit)
            {
                Signal(QuitSignal);
                return;
            }
            if (!RunningCopyDiffers())
            {
                // The same copy started again (e.g. from the Start menu): show its details flyout.
                Signal(ShowSignal);
                return;
            }
            // A different version or location (an upgrade, the portable exe next to the installed
            // one): the running copy quits and this one takes over.
            Signal(QuitSignal);
            try { first = mutex.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { first = true; }
            if (!first)
            {
                Signal(ShowSignal);
                return;
            }
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

    /// <summary>
    /// --selftest-network=FILE: one unauthenticated request through the embedded browser, result written
    /// to FILE. A JSON 401/403 means claude.ai answered (Cloudflare let the browser through); an HTML
    /// 403 means Cloudflare blocked it. Used by CI; touches no settings or accounts.
    /// </summary>
    static void RunNetworkSelfTest(string file)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            using var fetcher = new BrowserFetcher();
            var log = new System.Text.StringBuilder();
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    var (status, body) = await fetcher.GetAsync("/api/organizations", "selftest-invalid", CancellationToken.None);
                    var kind = body.TrimStart().StartsWith('{') || body.TrimStart().StartsWith('[') ? "json" : "html";
                    log.AppendLine($"attempt {attempt}: status={status} kind={kind} body={body[..Math.Min(body.Length, 200)].ReplaceLineEndings(" ")}");
                    if (kind == "json") break;
                    fetcher.RenewClearance();
                }
                catch (Exception ex)
                {
                    log.AppendLine($"attempt {attempt}: error={ex.GetType().Name}: {ex.Message}");
                }
                try { log.AppendLine("  page: " + await fetcher.DescribePageAsync()); } catch { }
            }
            System.IO.File.WriteAllText(file, log.ToString());
            app.Shutdown();
        };
        app.Run();
    }

    /// <summary>True when the copy already running in this session is another file or version.</summary>
    static bool RunningCopyDiffers()
    {
        var me = Environment.ProcessPath ?? "";
        var myVersion = FileVersionInfo.GetVersionInfo(me).FileVersion;
        using var current = Process.GetCurrentProcess();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id == current.Id || p.SessionId != current.SessionId) continue;
                    var path = p.MainModule?.FileName;
                    if (path is null) continue;
                    var info = FileVersionInfo.GetVersionInfo(path);
                    if (info.ProductName != "Claude Status") continue;
                    return !string.Equals(path, me, StringComparison.OrdinalIgnoreCase) || info.FileVersion != myVersion;
                }
                catch
                {
                    // Processes we may not inspect (other users, protected) are not ours.
                }
            }
        }
        return false;
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
sealed record Options(bool Demo, bool DemoAlert, WidgetStyle? Style, bool OpenDetails, bool Quit, string? SelfTestFile)
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
        var selfTest = args.FirstOrDefault(a => a.StartsWith("--selftest-network=", StringComparison.OrdinalIgnoreCase))?["--selftest-network=".Length..];
        return new Options(Has("--demo") || Has("--demo=alert"), Has("--demo=alert"), style, Has("--open-details"), Has("--quit"), selfTest);
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
        if (options.Style is WidgetStyle.IconOnly) _settings.ShowTaskbarWidget = false;
        else if (options.Style is { } style) _settings.WidgetStyle = style;
        _monitor = new UsageMonitor(_settings);
        _tray = new TrayController(_settings, _monitor, SignIn, ShowDetails, () => _widget!.ResetPosition(), Exit);
        _widget = new TaskbarWidget(_settings, _monitor, _tray.ShowMenuAt, ShowDetails);
        _widget.ShownChanged += _tray.SetWidgetShown;
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
