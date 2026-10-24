namespace ClaudeStatus;

/// <summary>Pace and forecast for the 5-hour and 7-day windows, from the local samples.</summary>
public static class Insights
{
    public static TimeSpan? Length(string key) => key switch
    {
        HistoryStore.Session => TimeSpan.FromHours(5),
        HistoryStore.Weekly => TimeSpan.FromDays(7),
        _ => null,
    };

    /// <summary>Share of the window that has passed (0–1) and a text like "3h 10m of 5h passed".</summary>
    public static (double Fraction, string Text)? Pace(LimitWindow w, DateTimeOffset now)
    {
        if (Length(w.Key) is not { } length || w.ResetsAt is not { } reset) return null;
        var passed = length - (reset - now);
        if (passed < TimeSpan.Zero || passed > length) return null;
        var f = passed / length;
        return (f, $"{Format(passed)} of {(w.Key == HistoryStore.Session ? "5h" : "7d")} passed");
    }

    public sealed record Forecast(string Text, IReadOnlyList<(DateTimeOffset T, double P)> Trend, (DateTimeOffset T, double P)? Projection);

    /// <summary>
    /// Linear trend over the recent samples of the current window (last hour for 5h, last day for 7d),
    /// up to <paramref name="now"/>, the time <paramref name="w"/> was fetched.
    /// Null when there is not enough data.
    /// </summary>
    public static Forecast? For(HistoryStore store, LimitWindow w, DateTimeOffset now)
    {
        if (Length(w.Key) is not { } length || w.ResetsAt is not { } reset) return null;
        var samples = store.Window(w.Key, w.ResetsAt);
        var trend = samples.Select(s => (DateTimeOffset.FromUnixTimeSeconds(s.T).ToLocalTime(), s.P)).ToList();
        if (trend.Count == 0 || trend[^1].Item1.ToUnixTimeSeconds() != now.ToUnixTimeSeconds()) trend.Add((now, w.Percent));

        if (w.Percent >= 100)
            return new Forecast($"Limit reached – available again in {Theme.Countdown(reset)}.", trend, null);

        var lookback = w.Key == HistoryStore.Session ? TimeSpan.FromHours(1) : TimeSpan.FromDays(1);
        var recent = trend.Where(p => p.Item1 >= now - lookback).ToList();
        if (recent.Count < 2 || (recent[^1].Item1 - recent[0].Item1) < TimeSpan.FromMinutes(10))
            return new Forecast("Collecting data for a forecast…", trend, null);

        var perHour = (recent[^1].Item2 - recent[0].Item2) / (recent[^1].Item1 - recent[0].Item1).TotalHours;
        if (perHour <= 0.05)
            return new Forecast("No recent increase – you are well within this limit.", trend, null);

        var hit = now + TimeSpan.FromHours((100 - w.Percent) / perHour);
        if (hit < reset)
        {
            var early = reset - hit;
            return new Forecast(
                $"At this pace you reach the limit around {hit.ToLocalTime():HH:mm}" +
                (w.Key == HistoryStore.Weekly ? $" on {hit.ToLocalTime():ddd}" : "") +
                $" – {Format(early)} before the reset.",
                trend, (hit, 100));
        }
        var atReset = Math.Min(100, w.Percent + perHour * (reset - now).TotalHours);
        return new Forecast($"On track for about {atReset:0}% by the reset.", trend, (reset, atReset));
    }

    static string Format(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h" :
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" :
        $"{Math.Max(1, (int)Math.Round(t.TotalMinutes))}m";
}
