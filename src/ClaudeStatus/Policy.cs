using Microsoft.Win32;

namespace ClaudeStatus;

/// <summary>
/// Administrator settings from Group Policy (see policy/ClaudeStatus.admx):
/// HKLM\SOFTWARE\Policies\ClaudeStatus, then HKCU\SOFTWARE\Policies\ClaudeStatus.
/// A configured value overrides the user's choice and greys out the menu item; for the "turn off"
/// switches (DisableNotifications, DisableUsageHistory, DisableExit) see their ADML help for what 0 means.
/// DisableExit removes the user-facing ways to quit (menu, flyout button, --quit, closing the window); Windows
/// sign-out/shutdown, the installer and a different version taking over still close the app.
/// AllowedEmailDomains / AllowedOrganizations restrict which Claude accounts and organisations may be
/// used (fail closed: an account whose e-mail cannot be read is refused when domains are set).
/// </summary>
public sealed record Policy(int? RefreshMinutes, WidgetStyle? WidgetStyle, bool? DisableNotifications,
    string[]? AllowedEmailDomains, string[]? AllowedOrganizations, bool DisableUsageHistory, bool DisableExit)
{
    const string KeyPath = @"SOFTWARE\Policies\ClaudeStatus";

    static Policy? _cached;
    static DateTime _cachedAt;

    /// <summary>Current policy, re-read from the registry at most every 30 seconds.</summary>
    public static Policy Current
    {
        get
        {
            if (_cached is not null && DateTime.UtcNow - _cachedAt < TimeSpan.FromSeconds(30)) return _cached;
            _cachedAt = DateTime.UtcNow;
            return _cached = Read();
        }
    }

    static Policy Read()
    {
        var refresh = ReadDword("RefreshMinutes");
        var style = ReadDword("WidgetStyle");
        var noNotify = ReadDword("DisableNotifications");
        var noHistory = ReadDword("DisableUsageHistory");
        var noExit = ReadDword("DisableExit");
        return new Policy(
            refresh is int r ? Math.Clamp(r, 1, 60) : null,
            style is int s && Enum.IsDefined(typeof(WidgetStyle), s) ? (WidgetStyle)s : null,
            noNotify is int n ? n != 0 : null,
            ReadList("AllowedEmailDomains")?.Select(d => d.TrimStart('@')).ToArray(),
            ReadList("AllowedOrganizations"),
            noHistory is int h && h != 0,
            noExit is int x && x != 0);
    }

    public bool AllowsEmail(string? email) =>
        AllowedEmailDomains is null ||
        (email is not null && AllowedEmailDomains.Any(d => email.EndsWith("@" + d, StringComparison.OrdinalIgnoreCase)));

    public bool AllowsOrganization(string id) =>
        AllowedOrganizations is null || AllowedOrganizations.Contains(id, StringComparer.OrdinalIgnoreCase);

    /// <summary>A REG_SZ list separated by ';', ',' or spaces; null when absent or empty.</summary>
    static string[]? ReadList(string name)
    {
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using var key = hive.OpenSubKey(KeyPath);
            if (key?.GetValue(name) is string v)
            {
                var items = v.Split(new[] { ';', ',', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                return items.Length > 0 ? items : null;
            }
        }
        return null;
    }

    static int? ReadDword(string name)
    {
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using var key = hive.OpenSubKey(KeyPath);
            if (key?.GetValue(name) is int v) return v;
        }
        return null;
    }
}
