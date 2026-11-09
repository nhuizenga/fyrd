using System.Runtime.CompilerServices;
using System.Text.Json;
using Fyrd.Core.CloudSessions;

namespace Fyrd.Core.Tests;

/// <summary>Scriptable <see cref="ICloudSessions"/> double: canned events and sessions, recorded writes, injectable failures.</summary>
public sealed class FakeCloudSessions : ICloudSessions
{
    private readonly Dictionary<string, List<SessionEvent>> _events = new();
    private readonly Dictionary<string, CloudSession> _sessions = new();
    private readonly Dictionary<string, List<CloudSession>> _byTag = new();
    private readonly Queue<Exception?> _next = new();
    private readonly object _createGate = new();

    /// <summary>Runs once at the start of the next <see cref="ListEventsAsync"/> call, then is cleared.</summary>
    public Func<Task>? BeforeNextListEvents { get; set; }

    /// <summary>Events returned per call; a smaller value forces multi-page listing.</summary>
    public int PageSize { get; set; } = 100;

    /// <summary>Simulated network delay of each <see cref="ListEventsAsync"/> call.</summary>
    public TimeSpan EventLatency { get; set; }

    private int _listEventCalls, _inFlight, _maxInFlight;
    public int ListEventCalls => _listEventCalls;
    /// <summary>Highest number of <see cref="ListEventsAsync"/> calls that were running at the same time.</summary>
    public int MaxConcurrentListEvents => _maxInFlight;

    public List<CreateSessionRequest> Created { get; } = [];
    public List<string?> EventAfterIds { get; } = [];
    public List<string> Interrupted { get; } = [];
    public List<string> Archived { get; } = [];
    public List<(string SessionId, Fyrd.Core.Hub.HubQuestion Question, IReadOnlyDictionary<string, string> Answers)> Answered { get; } = [];

    /// <summary>The next call to any method throws this once (calls queue in order), then the fake behaves normally again.</summary>
    public void ThrowOnNextCall(Exception e) => _next.Enqueue(e);

    /// <summary>The next <paramref name="n"/> calls succeed; use before <see cref="ThrowOnNextCall"/> to fail a later call.</summary>
    public void SkipCalls(int n) { for (var i = 0; i < n; i++) _next.Enqueue(null); }

    public void AddEvent(string sessionId, SessionEvent e)
    {
        if (!_events.TryGetValue(sessionId, out var list)) _events[sessionId] = list = [];
        list.Add(e);
    }

    public void AddAssistantEvent(string sessionId, string uuid, string text) =>
        AddEvent(sessionId, new SessionEvent(uuid, "assistant", DateTimeOffset.UnixEpoch, text, Empty));

    /// <summary>Events each <see cref="StreamEventsAsync"/> call replays (those after its resume point), then the stream ends.</summary>
    public Dictionary<string, List<StreamedEvent>> Streams { get; } = new();

    /// <summary>The resume point of each <see cref="StreamEventsAsync"/> call, in order.</summary>
    public List<long?> StreamAfter { get; } = [];

    public void SetSession(CloudSession s) => _sessions[s.Id] = s;
    public void SetByTag(string tag, params CloudSession[] sessions) => _byTag[tag] = [.. sessions];

    public static readonly JsonElement Empty = JsonDocument.Parse("{}").RootElement.Clone();

    public static CloudSession Session(
        string id, string status = "idle", string[]? tags = null, DateTimeOffset? createdAt = null,
        string? needsAction = null, string? statusCategory = null)
    {
        var raw = JsonDocument.Parse(
            $"{{\"created_at\":\"{(createdAt ?? DateTimeOffset.UnixEpoch):O}\"}}").RootElement.Clone();
        return new CloudSession(id, id, status, "connected",
            new PostTurnSummary(statusCategory, needsAction, null, null), tags ?? [], raw);
    }

    private void MaybeThrow()
    {
        Exception? e;
        lock (_next) e = _next.Count == 0 ? null : _next.Dequeue();
        if (e is not null) throw e;
    }

    public Task<CloudSession> CreateAsync(CreateSessionRequest request, CancellationToken ct = default)
    {
        lock (_createGate)
        {
            MaybeThrow();
            Created.Add(request);
            var s = Session("created-" + Created.Count, "running", [.. request.Tags]);
            _sessions[s.Id] = s;
            foreach (var tag in request.Tags)
            {
                if (!_byTag.TryGetValue(tag, out var list)) _byTag[tag] = list = [];
                list.Add(s);
            }
            return Task.FromResult(s);
        }
    }

    public Task<CloudSession> GetAsync(string sessionId, CancellationToken ct = default)
    {
        MaybeThrow();
        return _sessions.TryGetValue(sessionId, out var s)
            ? Task.FromResult(s)
            : throw new CloudSessionApiException(404, "not found");
    }

    public async Task<EventPage> ListEventsAsync(
        string sessionId, int limit = 100, string? afterId = null, string? beforeId = null, CancellationToken ct = default)
    {
        if (BeforeNextListEvents is { } hook)
        {
            BeforeNextListEvents = null;
            await hook();
        }
        Interlocked.Increment(ref _listEventCalls);
        var now = Interlocked.Increment(ref _inFlight);
        int seen;
        while ((seen = _maxInFlight) < now && Interlocked.CompareExchange(ref _maxInFlight, now, seen) != seen) { }
        try
        {
            if (EventLatency > TimeSpan.Zero) await Task.Delay(EventLatency, ct);
        }
        finally { Interlocked.Decrement(ref _inFlight); }
        MaybeThrow();
        lock (EventAfterIds) EventAfterIds.Add(afterId);
        var all = _events.TryGetValue(sessionId, out var l) ? l : [];
        var start = 0;
        if (afterId is not null)
        {
            var idx = all.FindIndex(e => e.Uuid == afterId);
            if (idx >= 0) start = idx + 1;
        }
        var size = Math.Min(limit, PageSize);
        var page = all.Skip(start).Take(size).ToList();
        var hasMore = start + page.Count < all.Count;
        return new EventPage(page, hasMore, page.FirstOrDefault()?.Uuid, page.LastOrDefault()?.Uuid);
    }

    public Task<IReadOnlyList<CloudSession>> ListByTagAsync(string tag, CancellationToken ct = default)
    {
        MaybeThrow();
        return Task.FromResult<IReadOnlyList<CloudSession>>(_byTag.TryGetValue(tag, out var l) ? l : []);
    }

    public Task InterruptAsync(string sessionId, CancellationToken ct = default)
    {
        MaybeThrow();
        Interrupted.Add(sessionId);
        return Task.CompletedTask;
    }

    public Task ArchiveAsync(string sessionId, CancellationToken ct = default)
    {
        MaybeThrow();
        Archived.Add(sessionId);
        return Task.CompletedTask;
    }

    public List<(string Id, string Title)> Renamed { get; } = [];

    public Task RenameAsync(string sessionId, string title, CancellationToken ct = default)
    {
        MaybeThrow();
        Renamed.Add((sessionId, title));
        return Task.CompletedTask;
    }

    public Task AnswerQuestionAsync(
        string sessionId, Fyrd.Core.Hub.HubQuestion question, IReadOnlyDictionary<string, string> answers, CancellationToken ct = default)
    {
        MaybeThrow();
        Answered.Add((sessionId, question, answers));
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<StreamedEvent> StreamEventsAsync(
        string sessionId, long? afterSequence, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        StreamedEvent[] events;
        lock (Streams)
        {
            MaybeThrow();
            StreamAfter.Add(afterSequence);
            events = Streams.TryGetValue(sessionId, out var l)
                ? l.Where(e => afterSequence is not { } a || e.Sequence > a).ToArray()
                : [];
        }
        foreach (var e in events)
        {
            ct.ThrowIfCancellationRequested();
            yield return e;
        }
    }
}
