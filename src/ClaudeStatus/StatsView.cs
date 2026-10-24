using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace ClaudeStatus;

/// <summary>The flyout's "Stats" tab: history chart (14 days / month / year), heat map, records.</summary>
public static class StatsView
{
    public const string Days14 = "14d", Month = "month", Year = "year";
    const double ChartHeight = 90;

    public static UIElement Build(HistoryStore store, string range, bool light,
        Action<string> setRange, Action export, Action clear)
    {
        var muted = new SolidColorBrush(light ? Color.FromRgb(0x55, 0x55, 0x55) : Color.FromRgb(0xB5, 0xB5, 0xB5));
        var track = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80));
        var tile = new SolidColorBrush(light ? Color.FromRgb(0xEC, 0xEC, 0xEC) : Color.FromRgb(0x34, 0x34, 0x34));
        var root = new StackPanel { Width = 520 };

        if (store.IsEmpty)
        {
            root.Children.Add(new TextBlock
            {
                Text = "No history yet. It builds up while Claude Status runs.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4),
            });
            root.Children.Add(Buttons(export, clear, enabled: false));
            return root;
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        var chart = range switch { Month => MonthChart(store, today), Year => YearChart(store, today), _ => Days14Chart(store, today) };

        // Title + range switch.
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var switcher = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (key, label) in new[] { (Days14, "14 days"), (Month, "Month"), (Year, "Year") })
        {
            var b = new ToggleButton { Content = label, IsChecked = key == range, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(2, 0, 0, 0) };
            b.Click += (_, _) => setRange(key);
            switcher.Children.Add(b);
        }
        DockPanel.SetDock(switcher, Dock.Right);
        head.Children.Add(switcher);
        head.Children.Add(new TextBlock { Text = chart.Title, FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        root.Children.Add(head);

        // Bars.
        var bars = new Grid { Height = ChartHeight };
        for (var i = 0; i < chart.Bars.Count; i++)
        {
            bars.ColumnDefinitions.Add(new ColumnDefinition());
            var (value, red, tip) = chart.Bars[i];
            var bar = new Border
            {
                Height = value is { } v ? Math.Max(2, ChartHeight * Math.Min(v, 100) / 100) : 2,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(1, 0, 1, 0),
                CornerRadius = new CornerRadius(2, 2, 0, 0),
                Background = value is null ? track : new SolidColorBrush(red ? Color.FromRgb(0xE5, 0x48, 0x4D) : Theme.Level(value.Value)),
                ToolTip = tip,
            };
            Grid.SetColumn(bar, i);
            bars.Children.Add(bar);
        }
        root.Children.Add(new Border { BorderBrush = track, BorderThickness = new Thickness(0, 0, 0, 1), Child = bars });

        var axis = new UniformGrid { Columns = chart.Axis.Count, Margin = new Thickness(0, 3, 0, 0) };
        foreach (var a in chart.Axis) axis.Children.Add(new TextBlock { Text = a, FontSize = 10, Foreground = muted, TextAlignment = TextAlignment.Center });
        root.Children.Add(axis);
        root.Children.Add(new TextBlock { Text = chart.Note, FontSize = 10, Foreground = muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 12) });

        // Heat map: weekday × hour over the selected range.
        root.Children.Add(new TextBlock { Text = "When you use Claude", FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        root.Children.Add(HeatMap(store.Days(chart.From, chart.To).ToList(), muted, track));

        // Records.
        var records = new UniformGrid { Columns = 3, Margin = new Thickness(0, 12, 0, 0) };
        foreach (var (value, label) in chart.Records)
        {
            var t = new StackPanel();
            t.Children.Add(new TextBlock { Text = value, FontSize = 18, FontWeight = FontWeights.SemiBold });
            t.Children.Add(new TextBlock { Text = label, FontSize = 10, Foreground = muted, TextWrapping = TextWrapping.Wrap });
            records.Children.Add(new Border { Background = tile, CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 6, 0), Child = t });
        }
        root.Children.Add(records);
        root.Children.Add(Buttons(export, clear, enabled: true));
        return root;
    }

    static UIElement Buttons(Action export, Action clear, bool enabled)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var e = new Button { Content = "Export CSV…", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0), IsEnabled = enabled };
        e.Click += (_, _) => export();
        var c = new Button { Content = "Clear history", Padding = new Thickness(10, 3, 10, 3), IsEnabled = enabled };
        c.Click += (_, _) => clear();
        p.Children.Add(e);
        p.Children.Add(c);
        return p;
    }

    static UIElement HeatMap(List<HistoryStore.Day> days, Brush muted, Brush empty)
    {
        var sum = new int[7, 24];
        foreach (var d in days)
            for (var h = 0; h < 24; h++)
                sum[((int)d.Date.DayOfWeek + 6) % 7, h] += d.Activity[h]; // Monday first
        var max = 1;
        foreach (var v in sum) max = Math.Max(max, v);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        for (var h = 0; h < 24; h++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        var names = CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames; // Sunday first
        for (var r = 0; r < 7; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(13) });
            var label = new TextBlock { Text = names[(r + 1) % 7], FontSize = 9, Foreground = muted, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(label, r);
            grid.Children.Add(label);
            for (var h = 0; h < 24; h++)
            {
                var v = sum[r, h];
                var cell = new Border
                {
                    Margin = new Thickness(1),
                    CornerRadius = new CornerRadius(2),
                    Background = v == 0 ? empty : new SolidColorBrush(Color.FromArgb((byte)(50 + 205 * v / max), 0xD9, 0x77, 0x57)),
                    ToolTip = $"{label.Text} {h:00}:00 – {v / 10.0:0.#}% added",
                };
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, h + 1);
                grid.Children.Add(cell);
            }
        }
        var hours = new UniformGrid { Columns = 5, Margin = new Thickness(30, 2, 0, 0) };
        foreach (var t in new[] { "00", "06", "12", "18", "23" }) hours.Children.Add(new TextBlock { Text = t, FontSize = 9, Foreground = muted });
        var p = new StackPanel();
        p.Children.Add(grid);
        p.Children.Add(hours);
        return p;
    }

    sealed record Chart(string Title, string Note, DateOnly From, DateOnly To,
        List<(double? Value, bool Red, string Tip)> Bars, List<string> Axis, List<(string Value, string Label)> Records);

    static (double? Value, bool Red, string Tip) DayBar(HistoryStore.Day? d, DateOnly date) =>
        d is null
            ? (null, false, $"{date:ddd d MMM}: no data")
            : (d.SessionPeak, d.SessionLimitHits.Count > 0,
               $"{date:ddd d MMM}: session peak {d.SessionPeak:0}%" + (d.SessionLimitHits.Count > 0 ? $", limit hit {d.SessionLimitHits.Count}×" : ""));

    static Chart Days14Chart(HistoryStore store, DateOnly today)
    {
        var from = today.AddDays(-13);
        var dates = Enumerable.Range(0, 14).Select(i => from.AddDays(i)).ToList();
        var days = dates.Select(store.DayOf).ToList();
        var withData = days.Where(d => d is not null).ToList();
        return new Chart(
            "Session peak per day – last 14 days",
            "Highest 5-hour session usage each day. Red = limit reached.",
            from, today,
            dates.Select((d, i) => DayBar(days[i], d)).ToList(),
            new[] { 0, 3, 7, 10, 13 }.Select(i => dates[i].ToString("d MMM", CultureInfo.CurrentCulture)).ToList(),
            [
                (withData.Sum(d => d!.SessionLimitHits.Count).ToString(), "session limits hit"),
                (withData.Count == 0 ? "–" : $"{withData.Average(d => d!.SessionPeak):0}%", "average daily peak"),
                (Streak(store, from, today), "longest streak without a limit"),
            ]);
    }

    static Chart MonthChart(HistoryStore store, DateOnly today)
    {
        var first = new DateOnly(today.Year, today.Month, 1);
        var count = DateTime.DaysInMonth(today.Year, today.Month);
        var dates = Enumerable.Range(0, count).Select(i => first.AddDays(i)).ToList();
        var days = dates.Select(d => d > today ? null : store.DayOf(d)).ToList();
        var withData = days.Where(d => d is not null).ToList();
        return new Chart(
            $"Session peak per day – {first.ToString("MMMM", CultureInfo.CurrentCulture)}",
            "Calendar month; days still to come are empty.",
            first, today,
            dates.Select((d, i) => DayBar(days[i], d)).ToList(),
            new[] { 1, 8, 15, 22, count }.Select(n => n.ToString(CultureInfo.CurrentCulture)).ToList(),
            [
                (withData.Sum(d => d!.SessionLimitHits.Count).ToString(), "session limits hit this month"),
                (withData.Count == 0 ? "–" : $"{withData.Average(d => d!.WeeklyPeak):0}%", "average weekly usage"),
                (Streak(store, first, today), "longest streak without a limit"),
            ]);
    }

    static Chart YearChart(HistoryStore store, DateOnly today)
    {
        var firstMonth = new DateOnly(today.Year, today.Month, 1).AddMonths(-11);
        var bars = new List<(double?, bool, string)>();
        var activity = new List<(DateOnly Month, int Sum)>();
        var hits = 0;
        for (var i = 0; i < 12; i++)
        {
            var m = firstMonth.AddMonths(i);
            var days = store.Days(m, m.AddMonths(1).AddDays(-1)).ToList();
            var monthHits = days.Sum(d => d.SessionLimitHits.Count);
            hits += monthHits;
            activity.Add((m, days.Sum(d => d.Activity.Sum())));
            // Average of each week's highest weekly usage.
            var weeks = days.GroupBy(d => d.Date.AddDays(-(((int)d.Date.DayOfWeek + 6) % 7))).Select(g => g.Max(d => d.WeeklyPeak)).ToList();
            double? avg = weeks.Count == 0 ? null : weeks.Average();
            var name = m.ToString("MMM yyyy", CultureInfo.CurrentCulture);
            bars.Add((avg, false, avg is null ? $"{name}: no data" : $"{name}: average weekly peak {avg:0}%, session limit hit {monthHits}×"));
        }
        var busiest = activity.Where(a => a.Sum > 0).OrderByDescending(a => a.Sum).Select(a => a.Month.ToString("MMM", CultureInfo.CurrentCulture)).FirstOrDefault() ?? "–";
        return new Chart(
            "Average weekly peak per month – last 12 months",
            "Average of each week's highest weekly usage; amber/red = close to or over the weekly limit.",
            firstMonth, today, bars,
            new[] { 0, 3, 6, 9, 11 }.Select(i => firstMonth.AddMonths(i).ToString("MMM", CultureInfo.CurrentCulture)).ToList(),
            [
                (hits.ToString(), "session limits hit in 12 months"),
                (busiest, "busiest month"),
                (Streak(store, firstMonth, today), "longest streak without a limit"),
            ]);
    }

    /// <summary>Longest run of consecutive recorded days without a session limit hit.</summary>
    static string Streak(HistoryStore store, DateOnly from, DateOnly to)
    {
        int best = 0, run = 0;
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            var day = store.DayOf(d);
            if (day is null) continue; // days without data neither extend nor break a streak
            run = day.SessionLimitHits.Count == 0 ? run + 1 : 0;
            best = Math.Max(best, run);
        }
        return best == 1 ? "1 day" : $"{best} days";
    }
}
