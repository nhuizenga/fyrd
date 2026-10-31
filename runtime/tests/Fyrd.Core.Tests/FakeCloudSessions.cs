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

    /// <summary>Events returned per call; a smaller value forces multi-page listing.</summary>
    public int PageSize { get; set; } = 100;

    public List<(string SessionId, string Text)> Sent { get; } = [];
    public List<CreateSessionRequest> Created { get; } = [];
    public List<string?> EventAfterIds { get; } = [];
    public List<string> Interrupted { get; } = [];
    public List<string> Archived { get; } = [];

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
        if (_next.Count == 0) return;
        if (_next.Dequeue() is { } e) throw e;
    }

    public Task<CloudSession> CreateAsync(CreateSessionRequest request, CancellationToken ct = default)
    {
        lock (_createGate)
        {
            MaybeThrow();
            Created.Add(request);
            var s = Session("created-" + Created.Count, "running", [.. request.Tags]);
            _sessions[s.Id] = s;
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

    public Task<EventPage> ListEventsAsync(
        string sessionId, int limit = 100, string? afterId = null, string? beforeId = null, CancellationToken ct = default)
    {
        MaybeThrow();
        EventAfterIds.Add(afterId);
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
        return Task.FromResult(new EventPage(page, hasMore, page.FirstOrDefault()?.Uuid, page.LastOrDefault()?.Uuid));
    }

    public Task SendMessageAsync(string sessionId, string text, CancellationToken ct = default)
    {
        MaybeThrow();
        Sent.Add((sessionId, text));
        return Task.CompletedTask;
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
}
