using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ClaudeStatus;

/// <summary>
/// Local usage history for one account + organisation (one file per <see cref="UsageSnapshot.Scope"/>).
/// Only numbers are stored – no sessions, e-mail or content. Raw samples are kept for 14 days (and only
/// when a value changes or every 15 minutes); a small summary per day is kept for about 400 days for
/// the month and year views. Nothing leaves the machine except through "Export CSV".
/// </summary>
public sealed class HistoryStore
{
    public const string Session = "five_hour", Weekly = "seven_day";
    static readonly TimeSpan SampleRetention = TimeSpan.FromDays(14);
    const int DayRetention = 400;
    static readonly TimeSpan SampleEvery = TimeSpan.FromMinutes(15);
    static readonly TimeSpan IdleSaveEvery = TimeSpan.FromHours(1);

    // Bumped whenever history files are deleted or moved, so a store loaded earlier is reloaded
    // instead of writing deleted history back to disk (see IsStale).
    static int _generation;

    public static string Folder => Path.Combine(Settings.Folder, "history");

    public sealed class Sample
    {
        public long T { get; set; }          // unix seconds
        public string W { get; set; } = "";  // five_hour / seven_day
        public double P { get; set; }        // percent
        public long R { get; set; }          // window reset, unix seconds (0 = unknown)
    }

    public sealed class Day
    {
        public string D { get; set; } = "";                 // yyyy-MM-dd, local
        public double SessionPeak { get; set; }
        public double WeeklyPeak { get; set; }
        public List<long> SessionLimitHits { get; set; } = new(); // reset times of 5h windows that reached 100 %
        public int[] Activity { get; set; } = new int[24];        // per local hour: 5h usage increase, tenths of a percent
        [System.Text.Json.Serialization.JsonIgnore] public DateOnly Date => DateOnly.ParseExact(D, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    sealed class Data
    {
        public int V { get; set; } = 1;
        public List<Sample> Samples { get; set; } = new();
        public List<Day> Days { get; set; } = new();
    }

    readonly string? _path; // null = in memory only (demo)
    readonly Data _data;
    readonly int _loadedGeneration = _generation;
    // Last value seen per window (not only the stored samples), for the per-hour activity.
    readonly Dictionary<string, (long R, double P, long T)> _seen = new();
    bool _pending;        // idle samples not yet on disk
    long _lastSave;       // unix seconds

    HistoryStore(string? path, Data data)
    {
        _path = path;
        _data = data;
    }

    /// <summary>True when history files were deleted or moved after this store was loaded.</summary>
    public bool IsStale => _loadedGeneration != _generation;

    public static HistoryStore ForScope(string scope)
    {
        var path = PathFor(scope);
        Data? data = null;
        try
        {
            if (File.Exists(path)) data = JsonSerializer.Deserialize<Data>(File.ReadAllText(path));
        }
        catch
        {
            // Corrupt history: start again.
        }
        return new HistoryStore(path, data ?? new Data());
    }

    /// <summary>"accountId|orgId" → history/accountId_orgId.json (both are ids, never names or e-mail).</summary>
    static string PathFor(string scope)
    {
        var parts = scope.Split('|', 2);
        return Path.Combine(Folder, $"{Safe(parts[0])}_{Safe(parts.Length > 1 ? parts[1] : "")}.json");
    }

    static string Safe(string s) => new(s.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').ToArray());

    /// <summary>Deletes all history of an account (sign-out, policy refusal).</summary>
    public static void DeleteForAccount(string accountId)
    {
        try
        {
            if (!Directory.Exists(Folder)) return;
            _generation++;
            foreach (var f in Directory.EnumerateFiles(Folder, Safe(accountId) + "_*.json*")) File.Delete(f); // with .tmp leftovers
        }
        catch
        {
            // Best effort; retried on the next sign-out or policy check.
        }
    }

    /// <summary>
    /// Moves the history of an account entry that is merged into another one (same e-mail signed in
    /// again), so no file is left behind under an id that no longer exists.
    /// </summary>
    public static void MoveAccount(string fromId, string toId)
    {
        try
        {
            if (!Directory.Exists(Folder)) return;
            _generation++;
            var from = Safe(fromId);
            foreach (var f in Directory.EnumerateFiles(Folder, from + "_*.json*"))
            {
                var target = Path.Combine(Folder, Safe(toId) + Path.GetFileName(f)[from.Length..]);
                if (f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || File.Exists(target)) File.Delete(f);
                else File.Move(f, target);
            }
        }
        catch
        {
            // Best effort.
        }
    }

    /// <summary>Deletes the history of organisations that the AllowedOrganizations policy does not allow.</summary>
    public static void DeleteDisallowedOrganizations(Policy policy)
    {
        if (policy.AllowedOrganizations is null) return;
        try
        {
            if (!Directory.Exists(Folder)) return;
            foreach (var f in Directory.EnumerateFiles(Folder, "*_*.json*"))
            {
                var name = Path.GetFileName(f);
                var org = name[(name.IndexOf('_') + 1)..name.IndexOf(".json", StringComparison.OrdinalIgnoreCase)];
                if (policy.AllowsOrganization(org)) continue;
                _generation++;
                File.Delete(f);
            }
        }
        catch
        {
            // Best effort; retried on the next refresh.
        }
    }

    /// <summary>Deletes every account's history (policy "Turn off usage history").</summary>
    public static void DeleteAll()
    {
        try
        {
            _generation++;
            if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }

    public void Clear()
    {
        _data.Samples.Clear();
        _data.Days.Clear();
        _seen.Clear();
        _pending = false;
        try { if (_path is not null && File.Exists(_path)) File.Delete(_path); } catch { }
    }

    public void Record(UsageSnapshot snap)
    {
        var now = snap.FetchedAt;
        var local = now.LocalDateTime;
        var day = GetOrAddDay(DateOnly.FromDateTime(local));
        var changed = false;

        foreach (var key in new[] { Session, Weekly })
        {
            var w = snap.Windows.FirstOrDefault(x => x.Key == key);
            if (w is null) continue;
            var reset = w.ResetsAt?.ToUnixTimeSeconds() ?? 0;
            var prev = _data.Samples.LastOrDefault(s => s.W == key);
            var sameWindow = prev is not null && prev.R == reset;
            var t = now.ToUnixTimeSeconds();

            if (key == Session)
            {
                if (w.Percent > day.SessionPeak) { day.SessionPeak = w.Percent; changed = true; }
                // Compare with the last value seen, not the last stored sample (small steps are not stored).
                (long R, double P, long T)? seen = null;
                if (_seen.TryGetValue(key, out var last)) seen = last;
                else if (prev is not null) seen = (prev.R, prev.P, prev.T);
                // A new window that started after the last value seen (seen within the longest refresh
                // interval, so the app was running): all of its usage is new.
                var added = seen is not { } s0 ? 0
                    : s0.R == reset ? w.Percent - s0.P
                    : reset != 0 && t - s0.T <= 65 * 60 &&
                      reset - (long)Insights.Length(Session)!.Value.TotalSeconds >= s0.T ? w.Percent
                    : 0;
                if (added > 0)
                {
                    day.Activity[local.Hour] += (int)Math.Round(added * 10);
                    changed = true;
                }
                _seen[key] = (reset, w.Percent, t);
                if (w.Percent >= 100 && reset != 0 && !_data.Days.Any(d => d.SessionLimitHits.Contains(reset)))
                {
                    day.SessionLimitHits.Add(reset);
                    changed = true;
                }
            }
            else if (w.Percent > day.WeeklyPeak)
            {
                day.WeeklyPeak = w.Percent;
                changed = true;
            }

            var dueByChange = prev is null || !sameWindow || Math.Abs(prev.P - w.Percent) >= 0.5;
            if (dueByChange || t - prev!.T >= (long)SampleEvery.TotalSeconds)
            {
                _data.Samples.Add(new Sample { T = t, W = key, P = w.Percent, R = reset });
                if (dueByChange) changed = true;
                else _pending = true; // idle sample: written with the next change, or hourly
            }
        }

        Prune(now);
        if (changed || (_pending && now.ToUnixTimeSeconds() - _lastSave >= (long)IdleSaveEvery.TotalSeconds))
            Save(now);
    }

    Day GetOrAddDay(DateOnly date)
    {
        var key = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var day = _data.Days.FirstOrDefault(d => d.D == key);
        if (day is null)
        {
            day = new Day { D = key };
            _data.Days.Add(day);
        }
        return day;
    }

    void Prune(DateTimeOffset now)
    {
        var cutoff = now.Add(-SampleRetention).ToUnixTimeSeconds();
        _data.Samples.RemoveAll(s => s.T < cutoff);
        var oldest = DateOnly.FromDateTime(now.LocalDateTime).AddDays(-DayRetention);
        _data.Days.RemoveAll(d => d.Date < oldest);
    }

    void Save(DateTimeOffset now)
    {
        if (_path is null) return;
        _pending = false;
        _lastSave = now.ToUnixTimeSeconds();
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_data));
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            // History is optional; never let it break the app.
        }
    }

    // ---- Queries -------------------------------------------------------------------------------

    /// <summary>Samples of one limit window (same reset time), oldest first.</summary>
    public IReadOnlyList<Sample> Window(string key, DateTimeOffset? resetsAt)
    {
        var reset = resetsAt?.ToUnixTimeSeconds() ?? 0;
        return _data.Samples.Where(s => s.W == key && s.R == reset).OrderBy(s => s.T).ToList();
    }

    public Day? DayOf(DateOnly date) =>
        _data.Days.FirstOrDefault(d => d.D == date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    public IEnumerable<Day> Days(DateOnly from, DateOnly to) => _data.Days.Where(d => d.Date >= from && d.Date <= to);

    public bool IsEmpty => _data.Days.Count == 0;

    /// <summary>Daily summary as CSV (date, peaks, limits hit, activity).</summary>
    public string ToCsv()
    {
        var sb = new StringBuilder("date,session_peak_pct,weekly_peak_pct,session_limits_hit,session_usage_added_pct\r\n");
        foreach (var d in _data.Days.OrderBy(d => d.D))
            sb.Append(CultureInfo.InvariantCulture,
                $"{d.D},{d.SessionPeak:0.#},{d.WeeklyPeak:0.#},{d.SessionLimitHits.Count},{d.Activity.Sum() / 10.0:0.#}\r\n");
        return sb.ToString();
    }

    /// <summary>Plausible sample history for --demo runs and screenshots (in memory only).</summary>
    public static HistoryStore Demo(UsageSnapshot snap)
    {
        var now = snap.FetchedAt;
        var data = new Data();
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        for (var i = 0; i < 365; i++)
        {
            var date = today.AddDays(-i);
            var weekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            var seed = (date.DayNumber * 37) % 100;
            if (weekend && seed < 60) continue;
            var month = date.Month;
            var peak = Math.Min(100, 20 + seed * 0.6 + (month is 8 or 9 or 10 ? 20 : 0));
            var day = new Day
            {
                D = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                SessionPeak = peak,
                WeeklyPeak = Math.Min(100, 15 + ((date.DayNumber * 13) % 60) + (month is 8 or 9 ? 15 : 0)),
            };
            if (peak >= 100) day.SessionLimitHits.Add(new DateTimeOffset(date.ToDateTime(new TimeOnly(15, 0))).ToUnixTimeSeconds());
            for (var h = 0; h < 24; h++)
            {
                var work = !weekend && h is >= 9 and <= 18 && h != 13;
                var evening = h is >= 20 and <= 23;
                day.Activity[h] = work ? 40 + (h * 7 + seed) % 60 : evening ? (h + seed) % 30 : 0;
            }
            data.Days.Add(day);
        }
        // Current windows: usage rising up to the snapshot's values.
        foreach (var (key, span) in new[] { (Session, 190), (Weekly, 60 * 24 * 3) })
        {
            if (snap.Windows.FirstOrDefault(x => x.Key == key) is not { } w) continue;
            var reset = w.ResetsAt?.ToUnixTimeSeconds() ?? 0;
            for (var i = 12; i >= 0; i--)
            {
                var m = span * i / 12;
                data.Samples.Add(new Sample { T = now.AddMinutes(-m).ToUnixTimeSeconds(), W = key, P = Math.Round(w.Percent * (span - m) / span, 1), R = reset });
            }
        }
        return new HistoryStore(null, data);
    }
}
