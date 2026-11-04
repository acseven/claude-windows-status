using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeStatus;

/// <summary>
/// The e-mail address suggested for signing in: the Windows sign-in name (user principal name) when it
/// looks like a real e-mail address. Many organisations sign in to Windows and to Claude (SSO) with the
/// same address. Anything else (local accounts, "user@corp.local", no domain) gives null, and sign-in
/// works as before with an empty field.
/// </summary>
static class WindowsAccount
{
    const int NameUserPrincipal = 8;

    [DllImport("secur32.dll", EntryPoint = "GetUserNameExW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetUserNameEx(int format, StringBuilder name, ref uint size);

    static readonly Lazy<string?> _email = new(Read);

    /// <summary>Suggested e-mail, or null. Never one the AllowedEmailDomains policy would refuse.</summary>
    public static string? SuggestedEmail => _email.Value is { } e && Policy.Current.AllowsEmail(e) ? e : null;

    static string? Read()
    {
        try
        {
            uint size = 512;
            var buffer = new StringBuilder((int)size);
            if (!GetUserNameEx(NameUserPrincipal, buffer, ref size)) return null; // local or unjoined account
            return LooksLikeEmail(buffer.ToString().Trim()) is { } e ? e : null;
        }
        catch
        {
            return null;
        }
    }

    static readonly string[] PrivateSuffixes = [".local", ".lan", ".internal", ".intranet", ".corp", ".home", ".localdomain", ".private"];

    /// <summary>The address, lower-cased, when it can be a public e-mail address; otherwise null.</summary>
    public static string? LooksLikeEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 254 || value.Any(char.IsWhiteSpace)) return null;
        var at = value.IndexOf('@');
        if (at <= 0 || at != value.LastIndexOf('@')) return null;
        var domain = value[(at + 1)..].ToLowerInvariant();
        if (!domain.Contains('.') || domain.StartsWith('.') || domain.EndsWith('.') || domain.Contains("..")) return null;
        if (PrivateSuffixes.Any(domain.EndsWith) || domain.EndsWith(".onmicrosoft.com")) return null;
        return value.ToLowerInvariant();
    }
}
