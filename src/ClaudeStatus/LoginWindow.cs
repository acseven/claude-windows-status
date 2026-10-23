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
/// </summary>
public sealed class LoginWindow : Window
{
    readonly WebView2 _web = new();
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };

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
                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Settings.Folder, "WebView2"));
                await _web.EnsureCoreWebView2Async(env);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Microsoft Edge WebView2 Runtime is required.\n\n" + ex.Message, Title,
                    MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
                return;
            }

            // Forget a previous (probably expired) session so we only capture a fresh one.
            _web.CoreWebView2.CookieManager.DeleteCookies("sessionKey", "https://claude.ai");
            _web.CoreWebView2.Navigate("https://claude.ai/login");
            _poll.Tick += async (_, _) => await CheckCookieAsync();
            _poll.Start();
        };
        Closed += (_, _) => _poll.Stop();
    }

    async Task CheckCookieAsync()
    {
        if (_web.CoreWebView2 is null) return;
        var cookies = await _web.CoreWebView2.CookieManager.GetCookiesAsync("https://claude.ai");
        var c = cookies.FirstOrDefault(x => x.Name == "sessionKey" && !string.IsNullOrEmpty(x.Value));
        if (c is null) return;

        _poll.Stop();
        SessionKey = c.Value;
        DialogResult = true;
    }
}
