using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeStatus;

/// <summary>Persisted user settings. The session key is stored encrypted with DPAPI (current user).</summary>
public sealed class Settings
{
    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeStatus");

    static string FilePath => Path.Combine(Folder, "settings.json");

    public string? ProtectedSessionKey { get; set; }
    public string? OrganizationId { get; set; }
    public int RefreshMinutes { get; set; } = 2;
    public bool ShowTaskbarWidget { get; set; } = true;

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

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
