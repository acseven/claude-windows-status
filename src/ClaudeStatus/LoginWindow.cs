using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
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
/// When Windows knows the user's e-mail address (<see cref="WindowsAccount"/>), it is filled into the
/// claude.ai e-mail field once, and the user can change it. If the page has no such field (claude.ai
/// changed, another sign-in step) nothing is filled and sign-in works as usual.
/// </summary>
public sealed class LoginWindow : Window
{
    readonly WebView2 _web = new();
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };

    readonly string? _email;
    bool _closed;
    bool _checking;
    int _prefillTries; // gives up after a while, or once filled / typed in

    public string? SessionKey { get; private set; }

    public LoginWindow(string? email = null)
    {
        _email = email;
        Title = L.SignInTitle;
        Width = 480;
        Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (email is null)
        {
            Content = _web;
        }
        else
        {
            var notice = new Border
            {
                Background = SystemColors.InfoBrush,
                Padding = new Thickness(12, 8, 12, 8),
                Child = new TextBlock { Text = L.EmailFromWindows(email), TextWrapping = TextWrapping.Wrap, Foreground = SystemColors.InfoTextBrush },
            };
            var dock = new DockPanel();
            DockPanel.SetDock(notice, Dock.Top);
            dock.Children.Add(notice);
            dock.Children.Add(_web);
            Content = dock;
        }

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
                MessageBox.Show(this, L.WebView2Required + "\n\n" + ex.Message, Title,
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
            await PrefillEmailAsync();
            if (_closed) return;
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

    /// <summary>
    /// Fills the e-mail field of the claude.ai login page once (the page is a React app: the value is set
    /// through the native setter and an input event, as typing would). Stops when filled, when the user
    /// typed something, after ~30 s, or when the page is not claude.ai's (an identity provider).
    /// </summary>
    async Task PrefillEmailAsync()
    {
        if (_email is null || _prefillTries >= 30 || _web.CoreWebView2 is not { } core) return;
        if (!Uri.TryCreate(core.Source, UriKind.Absolute, out var page) || page.Host != "claude.ai") return;
        _prefillTries++;
        try
        {
            var script = "(function(v){var i=document.querySelector('input[type=email],input[name=email],input#email');" +
                "if(!i)return 'none';if(i.value)return 'done';" +
                "Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(i,v);" +
                "i.dispatchEvent(new Event('input',{bubbles:true}));i.dispatchEvent(new Event('change',{bubbles:true}));" +
                "return 'done';})(" + JsonSerializer.Serialize(_email) + ")";
            var result = await core.ExecuteScriptAsync(script);
            if (result.Contains("done")) _prefillTries = int.MaxValue;
        }
        catch when (!_closed)
        {
            // Page navigating or script blocked: try again on the next tick.
        }
    }
}
