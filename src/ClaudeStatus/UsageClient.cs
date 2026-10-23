using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace ClaudeStatus;

public sealed record LimitWindow(string Key, string Label, double Percent, DateTimeOffset? ResetsAt);

public sealed record UsageSnapshot(IReadOnlyList<LimitWindow> Windows, string? OrganizationName, DateTimeOffset FetchedAt)
{
    public LimitWindow? Session => Windows.FirstOrDefault(w => w.Key == "five_hour");
    public LimitWindow? Weekly => Windows.FirstOrDefault(w => w.Key == "seven_day");
}

public sealed class AuthException(string message) : Exception(message);

/// <summary>
/// Client for the (undocumented) claude.ai web endpoints that the Settings → Usage page uses.
/// These are the same limits Claude Desktop is subject to; they can change without notice.
/// </summary>
public sealed class UsageClient : IDisposable
{
    const string BaseUrl = "https://claude.ai";

    readonly HttpClient _http;

    public UsageClient()
    {
        _http = new HttpClient(new HttpClientHandler { UseCookies = false, AutomaticDecompression = DecompressionMethods.All })
        {
            BaseAddress = new Uri(BaseUrl),
            Timeout = TimeSpan.FromSeconds(20),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public sealed record Organization(string Id, string Name);

    public async Task<Organization> GetOrganizationAsync(string sessionKey, string? preferredId, CancellationToken ct)
    {
        using var doc = await GetJsonAsync("/api/organizations", sessionKey, ct);
        var orgs = doc.RootElement.EnumerateArray()
            .Select(o => (
                Id: o.GetProperty("uuid").GetString()!,
                Name: o.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                Caps: o.TryGetProperty("capabilities", out var c) && c.ValueKind == JsonValueKind.Array
                    ? c.EnumerateArray().Select(x => x.GetString() ?? "").ToArray()
                    : []))
            .ToList();
        if (orgs.Count == 0) throw new AuthException("This account has no organizations.");

        var pick = orgs.FirstOrDefault(o => o.Id == preferredId);
        if (pick.Id is null) pick = orgs.FirstOrDefault(o => o.Caps.Any(c => c.StartsWith("claude_max") || c.StartsWith("claude_pro")));
        if (pick.Id is null) pick = orgs.FirstOrDefault(o => o.Caps.Contains("chat"));
        if (pick.Id is null) pick = orgs[0];
        return new Organization(pick.Id, pick.Name);
    }

    public async Task<UsageSnapshot> GetUsageAsync(string sessionKey, Organization org, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"/api/organizations/{org.Id}/usage", sessionKey, ct);
        return new UsageSnapshot(ParseWindows(doc.RootElement), org.Name, DateTimeOffset.Now);
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
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Add("Cookie", "sessionKey=" + sessionKey);
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (resp.StatusCode is HttpStatusCode.Unauthorized)
            throw new AuthException("Session expired – sign in again.");
        if (resp.StatusCode is HttpStatusCode.Forbidden)
        {
            // An HTML 403 is a Cloudflare challenge, a JSON 403 is a rejected session.
            throw body.TrimStart().StartsWith('{')
                ? new AuthException("Session rejected – sign in again.")
                : new HttpRequestException("claude.ai blocked the request (Cloudflare). Will retry.");
        }
        if ((int)resp.StatusCode == 429)
            throw new HttpRequestException("Rate limited by claude.ai. Will retry.");
        resp.EnsureSuccessStatusCode();

        return JsonDocument.Parse(body);
    }

    public void Dispose() => _http.Dispose();
}
