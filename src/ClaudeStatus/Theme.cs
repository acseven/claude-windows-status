using System.Windows.Media;
using Microsoft.Win32;

namespace ClaudeStatus;

static class Theme
{
    /// <summary>The taskbar follows the "Windows mode" setting (SystemUsesLightTheme), not the app mode.</summary>
    public static bool TaskbarIsLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
    }

    public static Color Level(double percent) => percent switch
    {
        >= 90 => Color.FromRgb(0xE5, 0x48, 0x4D),
        >= 70 => Color.FromRgb(0xF2, 0xA1, 0x2C),
        _ => Color.FromRgb(0xD9, 0x77, 0x57), // Claude orange
    };

    public static string Countdown(DateTimeOffset? resetsAt)
    {
        if (resetsAt is null) return "";
        var left = resetsAt.Value - DateTimeOffset.Now;
        if (left <= TimeSpan.Zero) return L.Now;
        if (left.TotalDays >= 1) return L.Days((int)left.TotalDays, left.Hours);
        if (left.TotalHours >= 1) return L.Hours((int)left.TotalHours, left.Minutes);
        return L.Minutes(Math.Max(1, (int)Math.Ceiling(left.TotalMinutes)));
    }
}
