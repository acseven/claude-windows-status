using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ClaudeStatus;

/// <summary>
/// Sign in to claude.ai inside an embedded Edge (WebView2) window with its own profile,
/// then pick up the resulting <c>sessionKey</c> cookie. The user signs in exactly as in a browser
/// (email code, Google, SSO), so the app never sees a password.
/// Windows single sign-on is enabled: Microsoft work/school (Entra ID) and personal accounts known
/// to Windows are offered by Microsoft's account picker, as in Edge, with "Use another account".
/// </summary>
public sealed class LoginWindow : Window
{
    readonly WebView2 _web = new();
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };

    bool _closed;
    bool _checking;

    public string? SessionKey { get; private set; }

    public LoginWindow()
    {
        Title = "Sign in to Claude";
        Width = 480;
        Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = _web;

        Loaded += async (_, _) =>
        {
            try
            {
                var options = new CoreWebView2EnvironmentOptions { AllowSingleSignOnUsingOSPrimaryAccount = true };
                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Settings.Folder, "WebView2-SignIn"), options);
                await _web.EnsureCoreWebView2Async(env);
            }
            catch (Exception ex)
            {
                if (_closed) return;
                MessageBox.Show(this, "Microsoft Edge WebView2 Runtime is required.\n\n" + ex.Message, Title,
                    MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
                return;
            }

            if (_closed) return;

            // Own profile folder (not the hidden BrowserFetcher's "WebView2"): clearing it must not wipe the
            // fetcher's Cloudflare clearance, and the fetcher's per-request sessionKey cookie must never
            // be mistaken for (or delete) the one this window waits for.
            // Always start clean: no previously captured session, and no identity-provider session that
            // could silently sign in a different account than the one the user picks. Windows SSO still
            // offers the accounts Windows knows about.
            try
            {
                var core = _web.CoreWebView2;
                if (core is null) return;
                await core.Profile.ClearBrowsingDataAsync();
                if (_closed) return;
                core.Navigate("https://claude.ai/login");
                _poll.Tick += async (_, _) => await CheckCookieAsync();
                _poll.Start();
            }
            catch when (_closed)
            {
                // Window closed (and the browser disposed) while starting.
            }
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _poll.Stop();
        };
    }

    async Task CheckCookieAsync()
    {
        // Timer ticks do not await this handler: never run twice, never touch a closed window.
        if (_closed || _checking || _web.CoreWebView2 is null) return;
        _checking = true;
        try
        {
            var cookies = await _web.CoreWebView2.CookieManager.GetCookiesAsync("https://claude.ai");
            if (_closed) return;
            var c = cookies.FirstOrDefault(x => x.Name == "sessionKey" && !string.IsNullOrEmpty(x.Value));
            if (c is null) return;

            _poll.Stop();
            SessionKey = c.Value;
            // The only stored copy is the DPAPI one in settings.json, so signing out (or a policy refusal)
            // really deletes the session.
            _web.CoreWebView2.CookieManager.DeleteCookies("sessionKey", "https://claude.ai");
            DialogResult = true;
        }
        catch when (_closed)
        {
            // Window closed while waiting.
        }
        finally
        {
            _checking = false;
        }
    }
}
