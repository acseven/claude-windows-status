using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeStatus;

public enum WidgetStyle { Bars = 0, Compact = 1, IconOnly = 2 }

/// <summary>A signed-in Claude account. The session key is stored encrypted with DPAPI (current user).</summary>
public sealed class SavedAccount
{
    /// <summary>Stable local id (notification state is keyed on it, never on the label).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = "";
    /// <summary>E-mail reported by claude.ai; identifies the account (duplicates are merged on it).</summary>
    public string? Email { get; set; }
    public string? ProtectedSessionKey { get; set; }
    public string? OrganizationId { get; set; }

    [JsonIgnore] public string? SessionKey => Settings.Unprotect(ProtectedSessionKey);
}

/// <summary>Persisted user settings.</summary>
public sealed class Settings
{
    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeStatus");

    static string FilePath => Path.Combine(Folder, "settings.json");

    public List<SavedAccount> Accounts { get; set; } = new();
    public int ActiveAccount { get; set; }

    // Single-account fields from 2026.10.1/2; moved into Accounts on load.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ProtectedSessionKey { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? OrganizationId { get; set; }

    public int RefreshMinutes { get; set; } = 2;
    /// <summary>Layout of the taskbar widget (Bars or Compact; IconOnly is the pre-2026.10.6 "widget off").</summary>
    public WidgetStyle WidgetStyle { get; set; } = WidgetStyle.Bars;
    public bool ShowTaskbarWidget { get; set; } = true;
    public bool ShowSessionTrayIcon { get; set; } = true;
    public bool ShowWeeklyTrayIcon { get; set; } = true;
    public bool NotificationsEnabled { get; set; } = true;
    public bool HistoryEnabled { get; set; } = true;

    /// <summary>"window:threshold" → resets_at of the window we last notified for, so each alert fires once per window.</summary>
    public Dictionary<string, string> Notified { get; set; } = new();

    /// <summary>Distance in physical pixels from the taskbar's right edge to the widget's right edge; null = next to the tray.</summary>
    public int? WidgetRightOffset { get; set; }

    [JsonIgnore] public SavedAccount? Active => ActiveAccount >= 0 && ActiveAccount < Accounts.Count ? Accounts[ActiveAccount] : null;
    [JsonIgnore] public string? SessionKey => Active?.SessionKey;

    /// <summary>Makes <paramref name="sessionKey"/> the active account, adding it if new.</summary>
    public SavedAccount AddOrActivate(string sessionKey)
    {
        var i = Accounts.FindIndex(a => a.SessionKey == sessionKey);
        if (i < 0)
        {
            Accounts.Add(new SavedAccount { Label = $"Account {Accounts.Count + 1}", ProtectedSessionKey = Protect(sessionKey) });
            i = Accounts.Count - 1;
        }
        ActiveAccount = i;
        return Accounts[i];
    }

    /// <summary>
    /// Records who the active account is. Older entries with the same e-mail (e.g. an expired session
    /// that was signed in again) are merged into it; entries are never merged on anything but e-mail,
    /// so two people in the same organisation stay separate.
    /// </summary>
    public void SetActiveIdentity(string? email, string fallbackLabel)
    {
        if (Active is not { } active) return;
        active.Email = email;
        active.Label = email ?? fallbackLabel;
        if (email is not null)
        {
            foreach (var old in Accounts.Where(a => a != active && string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                active.OrganizationId ??= old.OrganizationId;
                if (!InMemoryOnly) HistoryStore.MoveAccount(old.Id, active.Id); // no history left under a dropped id
                Accounts.Remove(old);
            }
        }
        ActiveAccount = Accounts.IndexOf(active);
    }

    public void Remove(SavedAccount account)
    {
        if (!InMemoryOnly) HistoryStore.DeleteForAccount(account.Id); // no history left behind for a removed account
        var active = Active;
        Accounts.Remove(account);
        ActiveAccount = active is not null && active != account ? Accounts.IndexOf(active) : 0;
    }

    /// <summary>Removes saved accounts (other than <paramref name="keep"/>) whose e-mail policy refuses.</summary>
    public int RemoveDisallowed(Policy policy, SavedAccount? keep)
    {
        var refused = Accounts.Where(a => a != keep && !policy.AllowsEmail(a.Email)).ToList();
        foreach (var a in refused) Remove(a);
        return refused.Count;
    }

    internal static string Protect(string value) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));

    internal static string? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser));
        }
        catch
        {
            return null;
        }
    }

    // Values in effect after Group Policy overrides.
    /// <summary>Widget layout in effect: Bars or Compact.</summary>
    [JsonIgnore] public WidgetStyle EffectiveStyle =>
        Policy.Current.WidgetStyle is WidgetStyle.Bars or WidgetStyle.Compact ? Policy.Current.WidgetStyle.Value
        : WidgetStyle == WidgetStyle.Compact ? WidgetStyle.Compact : WidgetStyle.Bars;

    /// <summary>Policy "tray icons only" hides the widget; policy Bars/Compact forces it on.</summary>
    [JsonIgnore] public bool EffectiveShowWidget => Policy.Current.WidgetStyle switch
    {
        WidgetStyle.IconOnly => false,
        not null => true,
        null => ShowTaskbarWidget,
    };
    [JsonIgnore] public int EffectiveRefreshMinutes => Policy.Current.RefreshMinutes ?? Math.Clamp(RefreshMinutes, 1, 60);
    /// <summary>Policy "Turn off usage history" wins over the user's choice.</summary>
    [JsonIgnore] public bool EffectiveHistory => !Policy.Current.DisableUsageHistory && HistoryEnabled;
    [JsonIgnore] public bool EffectiveNotifications => Policy.Current.DisableNotifications is bool off ? !off : NotificationsEnabled;

    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), JsonOptions) ?? new Settings();
                if (s.WidgetStyle == WidgetStyle.IconOnly)
                {
                    s.ShowTaskbarWidget = false;
                    s.WidgetStyle = WidgetStyle.Bars;
                }
                // At least one of widget / 5h icon / 7d icon stays on (2026.10.6 allowed none).
                if (!s.EffectiveShowWidget && !s.ShowSessionTrayIcon && !s.ShowWeeklyTrayIcon)
                    s.ShowSessionTrayIcon = true;
                if (s.ProtectedSessionKey is { } legacy && s.Accounts.Count == 0)
                    s.Accounts.Add(new SavedAccount { Label = "Account 1", ProtectedSessionKey = legacy, OrganizationId = s.OrganizationId });
                s.ProtectedSessionKey = null;
                s.OrganizationId = null;
                return s;
            }
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
