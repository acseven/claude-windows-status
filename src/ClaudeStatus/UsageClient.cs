using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace ClaudeStatus;

public sealed record LimitWindow(string Key, string Label, double Percent, DateTimeOffset? ResetsAt);

/// <param name="Scope">Which account and organisation the numbers belong to (keys notification state).</param>
public sealed record UsageSnapshot(IReadOnlyList<LimitWindow> Windows, string? OrganizationName, string Scope, DateTimeOffset FetchedAt)
{
    public LimitWindow? Session => Windows.FirstOrDefault(w => w.Key == "five_hour");
    public LimitWindow? Weekly => Windows.FirstOrDefault(w => w.Key == "seven_day");
}

public sealed class AuthException(string message) : Exception(message);

/// <summary>The account or organisation is not allowed by Group Policy.</summary>
public sealed class PolicyException(string message) : Exception(message);

/// <summary>
/// Client for the (undocumented) claude.ai web endpoints that the Settings → Usage page uses.
/// Requests go through <see cref="BrowserFetcher"/> (embedded Edge), because Cloudflare blocks plain HTTP clients.
/// These are the same limits Claude Desktop is subject to; they can change without notice.
/// </summary>
public sealed class UsageClient : IDisposable
{
    const string BaseUrl = "https://claude.ai";

    readonly BrowserFetcher _browser = new();

    public sealed record Organization(string Id, string Name, string[] Capabilities);

    public async Task<IReadOnlyList<Organization>> GetOrganizationsAsync(string sessionKey, CancellationToken ct)
    {
        using var doc = await GetJsonAsync("/api/organizations", sessionKey, ct);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new HttpRequestException("claude.ai returned an unexpected response. Will retry.");
        return doc.RootElement.EnumerateArray()
            .Where(o => o.TryGetProperty("uuid", out var u) && u.ValueKind == JsonValueKind.String)
            .Select(o => new Organization(
                o.GetProperty("uuid").GetString()!,
                o.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                o.TryGetProperty("capabilities", out var c) && c.ValueKind == JsonValueKind.Array
                    ? c.EnumerateArray().Select(x => x.GetString() ?? "").ToArray()
                    : []))
            .ToList();
    }

    /// <summary>The saved choice if still available, else a paid plan, else a chat-capable one, else the first.</summary>
    public static Organization Pick(IReadOnlyList<Organization> orgs, string? preferredId) =>
        orgs.FirstOrDefault(o => o.Id == preferredId)
        ?? orgs.FirstOrDefault(o => o.Capabilities.Any(c => c.StartsWith("claude_max") || c.StartsWith("claude_pro")))
        ?? orgs.FirstOrDefault(o => o.Capabilities.Contains("chat"))
        ?? orgs[0];

    /// <summary>The account's e-mail address (shown locally to tell accounts apart), or null.</summary>
    public async Task<string?> GetAccountLabelAsync(string sessionKey, CancellationToken ct)
    {
        try
        {
            using var doc = await GetJsonAsync("/api/account", sessionKey, ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new HttpRequestException("claude.ai returned an unexpected response. Will retry.");
            foreach (var name in new[] { "email_address", "email" })
                if (doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                    return v.GetString();
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Endpoint not available: fall back to the organisation name. Transient failures (network,
            // 429, Cloudflare, 5xx) propagate, so they are retried instead of being read as "no e-mail"
            // (which a domain policy would treat as a refused account and delete).
        }
        return null;
    }

    public async Task<UsageSnapshot> GetUsageAsync(string sessionKey, Organization org, string scope, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"/api/organizations/{Uri.EscapeDataString(org.Id)}/usage", sessionKey, ct);
        return new UsageSnapshot(ParseWindows(doc.RootElement), org.Name, scope, DateTimeOffset.Now);
    }

    /// <summary>
    /// Every top-level object carrying a numeric "utilization" is a limit window
    /// (five_hour, seven_day, seven_day_opus, seven_day_sonnet, extra_usage, …),
    /// so new windows show up without code changes.
    /// </summary>
    internal static List<LimitWindow> ParseWindows(JsonElement root)
    {
        var list = new List<LimitWindow>();
        if (root.ValueKind != JsonValueKind.Object) return list;

        foreach (var prop in root.EnumerateObject())
        {
            var v = prop.Value;
            if (v.ValueKind != JsonValueKind.Object) continue;
            if (!v.TryGetProperty("utilization", out var u) || u.ValueKind != JsonValueKind.Number) continue;
            if (v.TryGetProperty("is_enabled", out var en) && en.ValueKind == JsonValueKind.False) continue;

            DateTimeOffset? resets = null;
            if (v.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(r.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
                resets = dt;

            list.Add(new LimitWindow(prop.Name, LabelFor(prop.Name), Math.Clamp(u.GetDouble(), 0, 100), resets));
        }

        list.Sort((a, b) => Order(a.Key).CompareTo(Order(b.Key)));
        return list;
    }

    static int Order(string key) => key switch
    {
        "five_hour" => 0,
        "seven_day" => 1,
        "seven_day_opus" => 2,
        "seven_day_sonnet" => 3,
        "extra_usage" => 99,
        _ => 50,
    };

    static string LabelFor(string key) => key switch
    {
        "five_hour" => "Current session (5h)",
        "seven_day" => "Weekly – all models",
        "seven_day_opus" => "Weekly – Opus",
        "seven_day_sonnet" => "Weekly – Sonnet",
        "extra_usage" => "Extra usage",
        _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(key.Replace('_', ' ')),
    };

    async Task<JsonDocument> GetJsonAsync(string path, string sessionKey, CancellationToken ct)
    {
        var (status, body) = await _browser.GetAsync(path, sessionKey, ct);

        if (status == 0)
            throw new HttpRequestException("Could not reach claude.ai: " + body);
        if (status == 401)
            throw new AuthException("Session expired – sign in again.");
        if (status == 403)
        {
            // An HTML 403 is a Cloudflare challenge, a JSON 403 is a rejected session.
            if (body.TrimStart().StartsWith('{')) throw new AuthException("Session rejected – sign in again.");
            _browser.RenewClearance();
            throw new HttpRequestException("claude.ai is asking for a browser check (Cloudflare). Will retry.");
        }
        if (status == 429)
            throw new HttpRequestException("Rate limited by claude.ai. Will retry.");
        if (status is < 200 or > 299)
            throw new HttpRequestException($"claude.ai returned HTTP {status}.", null, (HttpStatusCode)status);

        // A 200 that is not JSON (captive portal, proxy, maintenance or Cloudflare page) is transient:
        // never let it read as "empty", which a tenancy policy could turn into a refusal.
        try { return JsonDocument.Parse(body); }
        catch (JsonException)
        {
            throw new HttpRequestException("claude.ai returned an unexpected (non-JSON) response. Will retry.");
        }
    }

    public void Dispose() => _browser.Dispose();
}
