using System.Globalization;
using System.Text.Json;
using Fyrd.Core.CloudSessions;
using Fyrd.Core.Config;

namespace Fyrd.Core.Hub;

/// <summary>One session's spend for the current local day. <c>Estimated</c> is true while a turn in progress contributes.</summary>
public readonly record struct DayCost(decimal Cost, bool Estimated, bool HasData);

/// <summary>
/// Tracks what each hub session has spent today, from two sources the hub poll already reads: the cost of each
/// <c>result</c> event (end of a turn, dated by its <c>created_at</c> in the configured time zone) and the session
/// object's cumulative <c>external_metadata.usage.cost_usd</c> (which also covers the turn in progress).
/// Both are list-price estimates. In memory the ledger is rebuilt by the poll's full re-read after a restart;
/// <c>hub/costs.json</c> keeps only today's totals, raises and "already notified" markers so they survive one.
/// </summary>
public sealed class CostLedger(FyrdPaths paths, ConfigStore config, TimeProvider time)
{
    private sealed class SessionCost
    {
        public readonly Dictionary<DateOnly, decimal> Completed = [];
        public readonly HashSet<string> SeenResults = new(StringComparer.Ordinal);
        public decimal ResultSum;
        public decimal? LastTotal;
        public decimal? Cumulative;
        public bool HasData;
    }

    public sealed class SessionDay
    {
        public string Title { get; set; } = "";
        public decimal CostToday { get; set; }
        public decimal ExtraUsd { get; set; }
        public decimal? NotifiedAtLimit { get; set; }
    }

    private sealed class DayFile
    {
        public string Date { get; set; } = "";
        public Dictionary<string, SessionDay> Sessions { get; set; } = new(StringComparer.Ordinal);
        public decimal? FyrdNotifiedAtLimit { get; set; }
    }

    private readonly object _gate = new();
    private readonly string _file = Path.Combine(paths.HubDir, "costs.json");
    private readonly Dictionary<string, SessionCost> _sessions = new(StringComparer.Ordinal);
    private DayFile? _day;
    private bool _dirty;

    public TimeZoneInfo TimeZone => FyrdConfig.FindTimeZone(config.Load().TimeZone) ?? TimeZoneInfo.Local;

    public DateOnly Today => LocalDate(time.GetUtcNow(), TimeZone);

    public static DateOnly LocalDate(DateTimeOffset at, TimeZoneInfo tz) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, tz).DateTime);

    /// <summary>Adds each new <c>result</c> event's cost to the session's day. Events already seen (by uuid) are ignored.</summary>
    public void Feed(string sessionId, IEnumerable<SessionEvent> events)
    {
        var tz = TimeZone;
        lock (_gate)
        {
            foreach (var e in events)
            {
                if (e.Type != "result") continue;
                var s = Get(sessionId);
                if (e.Uuid.Length > 0 && !s.SeenResults.Add(e.Uuid)) continue;

                var turn = TurnCost(e.Raw);
                var total = Number(Find(e.Raw, "total_cost_usd"));
                if (turn is null && total is { } t)
                {
                    // Fall back to the running total's growth. It resets when the worker restarts, so a drop means a new process.
                    turn = s.LastTotal is { } last && t >= last ? t - last : t;
                }
                if (total is { } now) s.LastTotal = now;
                if (turn is not { } cost) continue;

                var at = e.CreatedAt == default ? time.GetUtcNow() : e.CreatedAt;
                var day = LocalDate(at, tz);
                s.Completed[day] = s.Completed.GetValueOrDefault(day) + cost;
                s.ResultSum += cost;
                s.HasData = true;
            }
        }
    }

    /// <summary>Reads the cumulative session cost from the session object (<c>external_metadata.usage.cost_usd</c>).</summary>
    public void Observe(string sessionId, JsonElement sessionRaw)
    {
        if (sessionRaw.ValueKind != JsonValueKind.Object
            || !sessionRaw.TryGetProperty("external_metadata", out var meta) || meta.ValueKind != JsonValueKind.Object
            || !meta.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object
            || Number(usage, "cost_usd") is not { } cost)
            return;
        lock (_gate)
        {
            var s = Get(sessionId);
            s.Cumulative = cost;
            s.HasData = true;
        }
    }

    /// <summary>
    /// Today's spend: completed turns dated today plus the in-flight estimate (cumulative minus every result seen,
    /// never negative). A session this process has not read yet falls back to the persisted figure.
    /// </summary>
    public DayCost TodayCost(string sessionId)
    {
        var tz = TimeZone;
        lock (_gate)
        {
            var today = LocalDate(time.GetUtcNow(), tz);
            if (_sessions.TryGetValue(sessionId, out var s) && s.HasData)
            {
                var inFlight = s.Cumulative is { } c ? Math.Max(0, c - s.ResultSum) : 0;
                return new DayCost(s.Completed.GetValueOrDefault(today) + inFlight, inFlight > 0, true);
            }
            return Day(today).Sessions.TryGetValue(sessionId, out var d) ? new DayCost(d.CostToday, false, true) : default;
        }
    }

    /// <summary>Today's persisted spend of sessions not in <paramref name="live"/> (archived earlier today).</summary>
    public decimal ArchivedToday(IReadOnlySet<string> live)
    {
        lock (_gate)
            return Day(Today).Sessions.Where(kv => !live.Contains(kv.Key)).Sum(kv => kv.Value.CostToday);
    }

    public void Record(string sessionId, string title, decimal costToday)
    {
        lock (_gate)
        {
            var d = Entry(sessionId);
            if (d.Title != title || d.CostToday != costToday) _dirty = true;
            d.Title = title;
            d.CostToday = costToday;
        }
    }

    public decimal ExtraUsd(string sessionId)
    {
        lock (_gate) return Day(Today).Sessions.TryGetValue(sessionId, out var d) ? d.ExtraUsd : 0;
    }

    public decimal? NotifiedAtLimit(string sessionId)
    {
        lock (_gate) return Day(Today).Sessions.TryGetValue(sessionId, out var d) ? d.NotifiedAtLimit : null;
    }

    public void SetNotified(string sessionId, decimal limit)
    {
        lock (_gate) { Entry(sessionId).NotifiedAtLimit = limit; _dirty = true; Save(); }
    }

    public decimal? FyrdNotifiedAtLimit
    {
        get { lock (_gate) return Day(Today).FyrdNotifiedAtLimit; }
    }

    public void SetFyrdNotified(decimal limit)
    {
        lock (_gate) { Day(Today).FyrdNotifiedAtLimit = limit; _dirty = true; Save(); }
    }

    /// <summary>Raises one session's limit for today and saves at once.</summary>
    public void AddExtra(string sessionId, decimal amountUsd)
    {
        lock (_gate)
        {
            Entry(sessionId).ExtraUsd += amountUsd;
            _dirty = true;
            Save();
        }
    }

    /// <summary>Writes costs.json when something changed since the last save.</summary>
    public void Flush()
    {
        lock (_gate) Save();
    }

    private SessionCost Get(string id) =>
        _sessions.TryGetValue(id, out var s) ? s : _sessions[id] = new SessionCost();

    private SessionDay Entry(string id)
    {
        var day = Day(Today);
        return day.Sessions.TryGetValue(id, out var d) ? d : day.Sessions[id] = new SessionDay();
    }

    /// <summary>The persisted day, replaced by an empty one when the stored date is not <paramref name="today"/>.</summary>
    private DayFile Day(DateOnly today)
    {
        var key = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        _day ??= Load();
        if (_day.Date != key)
        {
            _day = new DayFile { Date = key };
            _dirty = true;
        }
        return _day;
    }

    private DayFile Load()
    {
        try
        {
            if (File.Exists(_file))
                return JsonSerializer.Deserialize<DayFile>(File.ReadAllText(_file), Json.Options) ?? new DayFile();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { }
        return new DayFile();
    }

    private void Save()
    {
        if (!_dirty || _day is null) return;
        AtomicFile.Write(_file, JsonSerializer.Serialize(_day, Json.Options));
        _dirty = false;
    }

    /// <summary>Sum of <c>costUSD</c> over the models in <c>model_usage_since_last_result</c>; null when the map is absent or has no costs.</summary>
    private static decimal? TurnCost(JsonElement raw)
    {
        if (Find(raw, "model_usage_since_last_result") is not { ValueKind: JsonValueKind.Object } usage) return null;
        decimal sum = 0;
        var any = false;
        foreach (var model in usage.EnumerateObject())
            if (Number(model.Value, "costUSD") is { } c) { sum += c; any = true; }
        return any ? sum : null;
    }

    /// <summary>
    /// Finds a field on the event itself, or one level down in the wrappers the REST and MCP views have been seen
    /// to use (<c>internal_anthropic_catchall</c>, <c>payload</c>, <c>data</c>, <c>event</c>).
    /// </summary>
    private static JsonElement? Find(JsonElement raw, string name)
    {
        if (raw.ValueKind != JsonValueKind.Object) return null;
        if (raw.TryGetProperty(name, out var direct)) return direct;
        foreach (var wrapper in new[] { "internal_anthropic_catchall", "payload", "data", "event" })
            if (raw.TryGetProperty(wrapper, out var w) && w.ValueKind == JsonValueKind.Object && w.TryGetProperty(name, out var v))
                return v;
        return null;
    }

    private static decimal? Number(JsonElement? e) =>
        e is { ValueKind: JsonValueKind.Number } n && n.TryGetDecimal(out var d) && d >= 0 ? d : null;

    private static decimal? Number(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) ? Number(v) : null;
}
