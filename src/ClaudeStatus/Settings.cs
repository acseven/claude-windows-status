using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeStatus;

public enum WidgetStyle { Bars = 0, Compact = 1, IconOnly = 2 }

/// <summary>Persisted user settings. The session key is stored encrypted with DPAPI (current user).</summary>
public sealed class Settings
{
    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeStatus");

    static string FilePath => Path.Combine(Folder, "settings.json");

    public string? ProtectedSessionKey { get; set; }
    public string? OrganizationId { get; set; }
    public int RefreshMinutes { get; set; } = 2;
    public WidgetStyle WidgetStyle { get; set; } = WidgetStyle.Bars;
    public bool NotificationsEnabled { get; set; } = true;

    /// <summary>"window:threshold" → resets_at of the window we last notified for, so each alert fires once per window.</summary>
    public Dictionary<string, string> Notified { get; set; } = new();

    /// <summary>Distance in physical pixels from the taskbar's right edge to the widget's right edge; null = next to the tray.</summary>
    public int? WidgetRightOffset { get; set; }

    [JsonIgnore]
    public string? SessionKey
    {
        get
        {
            if (string.IsNullOrEmpty(ProtectedSessionKey)) return null;
            try
            {
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(ProtectedSessionKey), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return null;
            }
        }
        set
        {
            ProtectedSessionKey = string.IsNullOrEmpty(value)
                ? null
                : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
        }
    }

    // Values in effect after Group Policy overrides.
    [JsonIgnore] public WidgetStyle EffectiveStyle => Policy.Current.WidgetStyle ?? WidgetStyle;
    [JsonIgnore] public int EffectiveRefreshMinutes => Policy.Current.RefreshMinutes ?? Math.Clamp(RefreshMinutes, 1, 60);
    [JsonIgnore] public bool EffectiveNotifications => Policy.Current.DisableNotifications is bool off ? !off : NotificationsEnabled;

    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), JsonOptions) ?? new Settings();
        }
        catch
        {
            // Corrupt settings: start fresh.
        }
        return new Settings();
    }

    /// <summary>Demo runs keep settings in memory only.</summary>
    [JsonIgnore] public bool InMemoryOnly { get; init; }

    public void Save()
    {
        if (InMemoryOnly) return;
        Directory.CreateDirectory(Folder);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
