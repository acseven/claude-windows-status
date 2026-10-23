namespace ClaudeStatus;

/// <summary>
/// Raises an OS notification when a limit crosses a threshold, once per limit window
/// (a window is identified by its reset time, so the alert re-arms after the reset).
/// </summary>
public sealed class Notifier
{
    static readonly Dictionary<string, int[]> Thresholds = new()
    {
        ["five_hour"] = [90, 100],
        ["seven_day"] = [70, 90, 100],
    };

    readonly Settings _settings;
    readonly Action<string, string> _show;

    public Notifier(Settings settings, Action<string, string> show)
    {
        _settings = settings;
        _show = show;
    }

    public void Check(UsageSnapshot snap)
    {
        var changed = false;
        foreach (var (key, levels) in Thresholds)
        {
            var w = snap.Windows.FirstOrDefault(x => x.Key == key);
            if (w is null) continue;
            var window = w.ResetsAt?.ToUniversalTime().ToString("O") ?? "";

            // Only the highest crossed level is shown; lower ones are marked as done too.
            var crossed = levels.Where(l => w.Percent >= l).ToArray();
            if (crossed.Length == 0) continue;
            var fresh = crossed.Where(l => !_settings.Notified.TryGetValue($"{key}:{l}", out var seen) || seen != window).ToArray();
            if (fresh.Length == 0) continue;

            foreach (var l in crossed) _settings.Notified[$"{key}:{l}"] = window;
            changed = true;

            if (_settings.EffectiveNotifications)
                _show(Title(key, fresh.Max()), Body(w, fresh.Max()));
        }
        if (changed) _settings.Save();
    }

    static string Title(string key, int level)
    {
        var name = key == "five_hour" ? "Claude session limit (5h)" : "Claude weekly limit (7d)";
        return level >= 100 ? $"{name} reached" : $"{name} at {level}%";
    }

    static string Body(LimitWindow w, int level)
    {
        var when = w.ResetsAt is { } r ? $"{Theme.Countdown(r)} ({r.ToLocalTime():ddd HH:mm})" : "unknown";
        return level >= 100
            ? $"Usage is paused until the limit resets in {when}."
            : $"{w.Percent:0}% used. Resets in {when}.";
    }
}
