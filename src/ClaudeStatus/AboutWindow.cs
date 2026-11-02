using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace ClaudeStatus;

/// <summary>
/// Small "About Claude Status" window: name, version, build and links. Closing it closes only this
/// window; a close request from outside (taskkill) is handled by Shell like for any other window.
/// </summary>
public sealed class AboutWindow : Window
{
    /// <summary>Project page, recorded by CI at build time (none in local builds).</summary>
    public static string? RepoUrl =>
        Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "RepositoryUrl")?.Value is { Length: > 0 } url &&
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps ? url : null;
    static AboutWindow? _open;

    /// <summary>Version of this build, e.g. "2026.11.3" (local builds show the project default).</summary>
    public static string Version
    {
        get
        {
            var info = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return info?.Split('+')[0] ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
        }
    }

    /// <summary>Short commit id the build was made from, when the SDK recorded one.</summary>
    public static string? Commit
    {
        get
        {
            var info = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var plus = info?.IndexOf('+') ?? -1;
            if (plus < 0) return null;
            var sha = info![(plus + 1)..];
            return sha.Length > 7 ? sha[..7] : sha;
        }
    }

    /// <summary>Shows the window, or brings the open one to the front.</summary>
    public static void ShowSingle()
    {
        if (_open is not null)
        {
            _open.Activate();
            return;
        }
        _open = new AboutWindow();
        _open.Closed += (_, _) => _open = null;
        _open.Show();
        _open.Activate();
    }

    AboutWindow()
    {
        Title = "About Claude Status";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;

        var stack = new StackPanel { Margin = new Thickness(24, 20, 24, 20), MinWidth = 320 };

        stack.Children.Add(new TextBlock { Text = "Claude Status", FontSize = 20, FontWeight = FontWeights.SemiBold });
        stack.Children.Add(new TextBlock
        {
            Text = "Claude plan usage in the Windows taskbar and notification area.",
            Margin = new Thickness(0, 4, 0, 14),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 360,
        });

        var commit = Commit;
        var versionLine = new TextBlock { Text = $"Version {Version}" + (commit is null ? "" : $"  (build {commit})") };
        stack.Children.Add(versionLine);
        stack.Children.Add(new TextBlock
        {
            Text = $".NET {Environment.Version} · {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}",
            Foreground = Brushes.Gray,
            Margin = new Thickness(0, 2, 0, 14),
        });

        if (RepoUrl is { } repo)
        {
            stack.Children.Add(Link("Project page and releases", repo));
            stack.Children.Add(Link("License (MIT)", repo + "/blob/main/LICENSE"));
        }
        else
        {
            stack.Children.Add(new TextBlock { Text = "Open source under the MIT License." });
        }
        stack.Children.Add(new TextBlock
        {
            Text = "Not affiliated with or endorsed by Anthropic.",
            Foreground = Brushes.Gray,
            Margin = new Thickness(0, 14, 0, 0),
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var copy = new Button { Content = "Copy version", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 8, 0) };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText($"Claude Status {Version}" + (commit is null ? "" : $" ({commit})")); }
            catch { /* clipboard busy */ }
        };
        var ok = new Button { Content = "OK", Padding = new Thickness(20, 3, 20, 3), IsDefault = true, IsCancel = true };
        ok.Click += (_, _) => Close();
        buttons.Children.Add(copy);
        buttons.Children.Add(ok);
        stack.Children.Add(buttons);

        Content = stack;
    }

    static TextBlock Link(string text, string url)
    {
        var link = new Hyperlink(new Run(text)) { NavigateUri = new Uri(url) };
        link.RequestNavigate += (_, e) =>
        {
            try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
            catch { /* no browser */ }
            e.Handled = true;
        };
        return new TextBlock(link) { Margin = new Thickness(0, 2, 0, 2) };
    }
}
