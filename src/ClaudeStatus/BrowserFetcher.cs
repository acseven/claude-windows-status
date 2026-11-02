using System.Net.Http;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ClaudeStatus;

/// <summary>
/// Makes the claude.ai API calls from inside an embedded Edge (WebView2) page, the same browser the
/// user signed in with. claude.ai sits behind Cloudflare, which rejects plain HTTP clients but lets a
/// real browser through (and remembers that in its own cookies).
///
/// Tenancy: requests run strictly one at a time. For each one, the account's session key is set as
/// an in-memory session cookie, the request is made, and the cookie is deleted again, so one
/// account's request can never carry another account's session, and no session is written to disk.
/// </summary>
public sealed class BrowserFetcher : IDisposable
{
    const string Origin = "https://claude.ai";

    readonly SemaphoreSlim _gate = new(1, 1);
    readonly Dictionary<string, TaskCompletionSource<(int Status, string Body)>> _pending = new();
    Window? _host;
    WebView2? _web;
    Task? _ready;
    bool _disposed;
    bool _reload;

    /// <summary>
    /// Raised when something outside the app asks the hidden browser window to close (WM_CLOSE). The
    /// window itself always stays until <see cref="Dispose"/>; the app decides whether to quit.
    /// </summary>
    public static event Action? CloseRequested;

    /// <summary>Cloudflare asked for a check: reload the page (renewing its clearance) before the next request.</summary>
    public void RenewClearance() => _reload = true;

    /// <summary>Title and address of the hidden page (diagnostics).</summary>
    public async Task<string> DescribePageAsync()
    {
        if (_web?.CoreWebView2 is not { } core) return "not started";
        return $"title={await core.ExecuteScriptAsync("document.title")} url={core.Source}";
    }

    public async Task<(int Status, string Body)> GetAsync(string path, string sessionKey, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        var cookies = default(CoreWebView2CookieManager);
        try
        {
            await (_ready ??= InitAsync());
            var core = _web!.CoreWebView2;
            if (_reload)
            {
                _reload = false;
                await NavigateAsync(core);
            }
            cookies = core.CookieManager;

            var cookie = cookies.CreateCookie("sessionKey", sessionKey, ".claude.ai", "/");
            cookie.IsSecure = true;
            cookie.IsHttpOnly = true; // never visible to page script
            cookies.AddOrUpdateCookie(cookie); // no expiry: session cookie, kept in memory only

            var id = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<(int, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;
            try
            {
                var script =
                    $$"""
                    fetch({{JsonSerializer.Serialize(path)}}, { credentials: 'include', headers: { 'Accept': 'application/json' } })
                      .then(async r => chrome.webview.postMessage(JSON.stringify({ id: '{{id}}', status: r.status, body: await r.text() })))
                      .catch(e => chrome.webview.postMessage(JSON.stringify({ id: '{{id}}', status: 0, body: String(e) })));
                    """;
                await core.ExecuteScriptAsync(script);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                using (timeout.Token.Register(() => tcs.TrySetCanceled()))
                {
                    try
                    {
                        return await tcs.Task;
                    }
                    catch (TaskCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // No answer: usually the page navigated away (Cloudflare check). Reload next time.
                        _reload = true;
                        throw new HttpRequestException("claude.ai did not answer in time. Will retry.");
                    }
                }
            }
            finally
            {
                _pending.Remove(id);
            }
        }
        finally
        {
            try { cookies?.DeleteCookies("sessionKey", Origin); } catch { /* host already gone */ }
            _gate.Release();
        }
    }

    async Task InitAsync()
    {
        // WebView2 needs a real window; keep it off-screen, out of the taskbar and Alt-Tab.
        _host = new Window
        {
            Title = "Claude Status (network)",
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Width = 320,
            Height = 240,
            Left = -32000,
            Top = -32000,
        };
        _web = new WebView2();
        _host.Content = _web;
        _host.SourceInitialized += (_, _) => Native.MakeToolWindow(new WindowInteropHelper(_host).Handle);
        // WM_CLOSE sent to this window from outside (installer, Windows) is a request to close the app,
        // not just this window: pass it on and keep the window. (A posted WM_CLOSE, as from "taskkill",
        // goes to WPF's hidden owner window instead and is caught by Shell.OnThreadMessage.)
        _host.Closing += (_, e) =>
        {
            if (_disposed) return;
            e.Cancel = true;
            CloseRequested?.Invoke();
        };
        _host.Show();

        var options = new CoreWebView2EnvironmentOptions { AllowSingleSignOnUsingOSPrimaryAccount = true };
        var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Settings.Folder, "WebView2"), options);
        await _web.EnsureCoreWebView2Async(env);
        var core = _web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.WebMessageReceived += OnMessage;

        await NavigateAsync(core);
    }

    /// <summary>Loads a same-origin page to run fetch() from; passing Cloudflare here sets its clearance cookies.</summary>
    static async Task NavigateAsync(CoreWebView2 core)
    {
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Done(object? s, CoreWebView2NavigationCompletedEventArgs e)
        {
            core.NavigationCompleted -= Done;
            loaded.TrySetResult();
        }
        core.NavigationCompleted += Done;
        core.Navigate(Origin + "/robots.txt");
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // A Cloudflare check page ("Just a moment…") solves itself and reloads; a request started
        // before that would be lost with the page. Wait until the check is gone (up to 30 s).
        for (var i = 0; i < 30; i++)
        {
            var title = await core.ExecuteScriptAsync("document.title");
            if (!title.Contains("Just a moment", StringComparison.OrdinalIgnoreCase) &&
                !title.Contains("Attention Required", StringComparison.OrdinalIgnoreCase))
                break;
            await Task.Delay(1000);
        }
    }

    void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString());
            var id = doc.RootElement.GetProperty("id").GetString();
            if (id is not null && _pending.TryGetValue(id, out var tcs))
                tcs.TrySetResult((doc.RootElement.GetProperty("status").GetInt32(), doc.RootElement.GetProperty("body").GetString() ?? ""));
        }
        catch
        {
            // Not one of ours.
        }
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var tcs in _pending.Values) tcs.TrySetCanceled();
        _web?.Dispose();
        _host?.Close();
    }
}
