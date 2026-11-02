using Microsoft.Win32;

namespace ClaudeStatus;

/// <summary>
/// Per-user autostart via HKCU\...\Run. The MSI writes the same value under HKLM for all users,
/// in which case the user toggle is shown as managed.
/// </summary>
static class AutoStart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "ClaudeStatus";

    public static bool IsMachineEnabled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is not null;
    }

    public static bool IsUserEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is not null;
    }

    public static void SetUser(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
