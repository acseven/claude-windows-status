using Microsoft.Win32;

namespace ClaudeStatus;

/// <summary>
/// Administrator settings from Group Policy (see policy/ClaudeStatus.admx):
/// HKLM\SOFTWARE\Policies\ClaudeStatus, then HKCU\SOFTWARE\Policies\ClaudeStatus.
/// A value that is present overrides the user's choice and greys out the menu item.
/// </summary>
public sealed record Policy(int? RefreshMinutes, WidgetStyle? WidgetStyle, bool? DisableNotifications)
{
    const string KeyPath = @"SOFTWARE\Policies\ClaudeStatus";

    public static Policy Current
    {
        get
        {
            var refresh = ReadDword("RefreshMinutes");
            var style = ReadDword("WidgetStyle");
            var noNotify = ReadDword("DisableNotifications");
            return new Policy(
                refresh is int r ? Math.Clamp(r, 1, 60) : null,
                style is int s && Enum.IsDefined(typeof(WidgetStyle), s) ? (WidgetStyle)s : null,
                noNotify is int n ? n != 0 : null);
        }
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
