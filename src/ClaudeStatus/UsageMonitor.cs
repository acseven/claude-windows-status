using System.Windows.Threading;

namespace ClaudeStatus;

public enum MonitorState { SignedOut, Loading, Ok, Error }

/// <summary>
/// Polls claude.ai for the active account on a timer and raises <see cref="Changed"/> on the UI thread.
/// Every result is checked against the account (and refresh) it was requested for, so a response that
/// arrives after switching accounts is dropped instead of being shown under the wrong account.
/// </summary>
public sealed class UsageMonitor : IDisposable
{
    readonly Settings _settings;
    readonly UsageClient _client = new();
    readonly DispatcherTimer _timer = new();
    readonly DispatcherTimer _retry = new() { Interval = TimeSpan.FromSeconds(20) }; // one quick retry after a transient error
    bool _retried;
    SavedAccount? _orgAccount;           // account that _org / Organizations belong to
    UsageClient.Organization? _org;
    CancellationTokenSource? _cts;
    int _generation;

    public MonitorState State { get; private set; } = MonitorState.SignedOut;
    public UsageSnapshot? Last { get; private set; }
    public string? Message { get; private set; }

    /// <summary>Organisations of the active account that policy allows (for the Organization menu).</summary>
    public IReadOnlyList<UsageClient.Organization> Organizations { get; private set; } = [];

    /// <summary>Label of the active account (e-mail or organisation), or null when signed out.</summary>
    public string? AccountLabel => _settings.Active?.Label;

    public event Action? Changed;

    /// <summary>Raised when an account was refused by policy and removed (message for the user).</summary>
    public event Action<string>? AccountRefused;

    public UsageMonitor(Settings settings)
    {
        _settings = settings;
        _timer.Tick += async (_, _) => await RefreshAsync();
        _retry.Tick += async (_, _) => { _retry.Stop(); await RefreshAsync(); };
        ApplyInterval();
    }

    public void ApplyInterval()
    {
        var interval = TimeSpan.FromMinutes(_settings.EffectiveRefreshMinutes);
        if (_timer.Interval != interval) _timer.Interval = interval;
    }

    public void Start()
    {
        _timer.Start();
        _ = RefreshAsync();
    }

    /// <summary>Fixed sample data for screenshots and smoke tests (--demo); nothing is fetched.</summary>
    public void LoadDemo(bool alert)
    {
        var now = DateTimeOffset.Now;
        Last = new UsageSnapshot(
        [
            new("five_hour", "Current session (5h)", alert ? 92 : 42, now.AddMinutes(alert ? 65 : 130)),
            new("seven_day", "Weekly – all models", alert ? 74 : 18, now.AddDays(4).AddHours(6)),
            new("seven_day_opus", "Weekly – Opus", alert ? 95 : 74, now.AddDays(4).AddHours(6)),
        ], "Demo", "demo", now);
        Set(MonitorState.Ok, null);
    }

    public void AddAccount(string sessionKey)
    {
        _settings.AddOrActivate(sessionKey);
        Reset();
    }

    public void SwitchTo(SavedAccount account)
    {
        var index = _settings.Accounts.IndexOf(account);
        if (index < 0 || index == _settings.ActiveAccount) return;
        _settings.ActiveAccount = index;
        Reset();
    }

    public void SetOrganization(string organizationId)
    {
        if (_settings.Active is not { } account || account.OrganizationId == organizationId) return;
        account.OrganizationId = organizationId;
        Reset();
    }

    public void SignOutActive()
    {
        if (_settings.Active is { } account) _settings.Remove(account);
        Reset();
    }

    void Reset()
    {
        _settings.Save();
        _org = null;
        _orgAccount = null;
        Organizations = [];
        Last = null;
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        var account = _settings.Active;
        var key = account?.SessionKey;
        if (account is null || string.IsNullOrEmpty(key))
        {
            Last = null;
            Organizations = [];
            Set(MonitorState.SignedOut, account is null ? "Not signed in." : "Session expired – sign in again.");
            return;
        }

        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var generation = ++_generation;
        bool Stale() => generation != _generation || _settings.Active != account;

        if (Last is null) Set(MonitorState.Loading, "Loading…");

        var ok = false;
        try
        {
            var policy = Policy.Current;
            var removed = _settings.RemoveDisallowed(policy, account);
            if (removed.Count > 0)
            {
                _settings.Save();
                // Other saved accounts were deleted with their history: tell the user.
                AccountRefused?.Invoke(removed.Count == 1
                    ? $"{removed[0].Label} is not allowed by your administrator and was removed."
                    : $"{string.Join(", ", removed.Select(a => a.Label))} are not allowed by your administrator and were removed.");
            }
            if (!_settings.InMemoryOnly) HistoryStore.DeleteDisallowedOrganizations(policy);
            ApplyInterval(); // picks up a RefreshMinutes policy changed while running

            // Re-resolve (and re-check) the identity when it is unknown or no longer allowed, e.g. when a
            // policy is added while the app runs; a refused account is removed below.
            if (_org is null || _orgAccount != account || !policy.AllowsOrganization(_org.Id) || !policy.AllowsEmail(account.Email))
            {
                var email = await _client.GetAccountLabelAsync(key, cts.Token);
                if (Stale()) return;
                if (!policy.AllowsEmail(email))
                    throw new PolicyException($"{email ?? "This account"} is not allowed by your administrator.");

                var orgs = await _client.GetOrganizationsAsync(key, cts.Token);
                if (Stale()) return;
                // No organisations at all is not a policy decision: keep the account (signed out), don't delete it.
                if (orgs.Count == 0) throw new AuthException("This account has no Claude organizations.");
                var allowed = orgs.Where(o => policy.AllowsOrganization(o.Id)).ToList();
                if (allowed.Count == 0)
                    throw new PolicyException("None of this account's organizations are allowed by your administrator.");

                _settings.SetActiveIdentity(email, account.Label);
                var org = UsageClient.Pick(allowed, account.OrganizationId);
                account.OrganizationId = org.Id;
                if (email is null) account.Label = string.IsNullOrEmpty(org.Name) ? account.Label : org.Name;
                _settings.Save();

                _org = org;
                _orgAccount = account;
                Organizations = allowed;
            }

            var scope = $"{account.Id}|{_org.Id}";
            var snapshot = await _client.GetUsageAsync(key, _org, scope, cts.Token);
            if (Stale()) return;
            Last = snapshot;
            _retried = false;
            ok = true;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Superseded by a newer refresh.
        }
        catch (PolicyException ex)
        {
            if (Stale()) return;
            // Not allowed: forget the account and its stored session entirely.
            _settings.Remove(account);
            _settings.Save();
            _org = null;
            _orgAccount = null;
            Organizations = [];
            Last = null;
            Set(MonitorState.SignedOut, ex.Message);
            AccountRefused?.Invoke(ex.Message);
            if (_settings.Active is not null) _ = RefreshAsync(); // continue with the next saved account
        }
        catch (AuthException ex)
        {
            if (Stale()) return;
            Last = null;
            _org = null;
            _orgAccount = null;
            Set(MonitorState.SignedOut, ex.Message);
        }
        catch (Exception ex)
        {
            if (Stale()) return;
            // Keep showing the last good snapshot (same account) on transient failures.
            Set(MonitorState.Error, ex.Message);
            if (!_retried)
            {
                _retried = true;
                _retry.Start();
            }
        }

        // Outside the try: subscribers (notifications, history, rendering) must not turn a good fetch into an error.
        if (ok) Set(MonitorState.Ok, null);
    }

    void Set(MonitorState state, string? message)
    {
        State = state;
        Message = message;
        try { Changed?.Invoke(); }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Changed subscriber failed: " + ex); }
    }

    public void Dispose()
    {
        _timer.Stop();
        _retry.Stop();
        _cts?.Cancel();
        _client.Dispose();
    }
}
