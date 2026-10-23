using System.Windows.Threading;

namespace ClaudeStatus;

public enum MonitorState { SignedOut, Loading, Ok, Error }

/// <summary>Polls claude.ai on a timer and raises <see cref="Changed"/> on the UI thread.</summary>
public sealed class UsageMonitor : IDisposable
{
    readonly Settings _settings;
    readonly UsageClient _client = new();
    readonly DispatcherTimer _timer = new();
    UsageClient.Organization? _org;
    CancellationTokenSource? _cts;

    public MonitorState State { get; private set; } = MonitorState.SignedOut;
    public UsageSnapshot? Last { get; private set; }
    public string? Message { get; private set; }

    public event Action? Changed;

    public UsageMonitor(Settings settings)
    {
        _settings = settings;
        _timer.Tick += async (_, _) => await RefreshAsync();
        ApplyInterval();
    }

    public void ApplyInterval() => _timer.Interval = TimeSpan.FromMinutes(_settings.EffectiveRefreshMinutes);

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
        ], "Demo", now);
        Set(MonitorState.Ok, null);
    }

    public void SetSessionKey(string? key)
    {
        _settings.SessionKey = key;
        _settings.OrganizationId = null;
        _settings.Save();
        _org = null;
        Last = null;
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        var key = _settings.SessionKey;
        if (string.IsNullOrEmpty(key))
        {
            Set(MonitorState.SignedOut, "Not signed in.");
            return;
        }

        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        if (Last is null) Set(MonitorState.Loading, "Loading…");

        try
        {
            _org ??= await _client.GetOrganizationAsync(key, _settings.OrganizationId, cts.Token);
            if (_settings.OrganizationId != _org.Id)
            {
                _settings.OrganizationId = _org.Id;
                _settings.Save();
            }
            Last = await _client.GetUsageAsync(key, _org, cts.Token);
            Set(MonitorState.Ok, null);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Superseded by a newer refresh.
        }
        catch (AuthException ex)
        {
            Last = null;
            _org = null;
            Set(MonitorState.SignedOut, ex.Message);
        }
        catch (Exception ex)
        {
            // Keep showing the last good snapshot on transient failures.
            Set(MonitorState.Error, ex.Message);
        }
    }

    void Set(MonitorState state, string? message)
    {
        State = state;
        Message = message;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _timer.Stop();
        _cts?.Cancel();
        _client.Dispose();
    }
}
