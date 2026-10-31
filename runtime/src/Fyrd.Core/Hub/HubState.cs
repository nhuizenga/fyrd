using System.Text.Json;
using Fyrd.Core.CloudSessions;

namespace Fyrd.Core.Hub;

/// <summary>One pending <c>AskUserQuestion</c> item. <c>Input</c> is the raw <c>request.input</c> of its <c>can_use_tool</c> request.</summary>
public sealed record HubQuestion(
    string Id, string SessionId, string SessionTitle, string Question, IReadOnlyList<string> Options, bool MultiSelect,
    DateTimeOffset AskedAt, string ToolUseId, string RequestId, JsonElement Input);

public sealed record HubSessionView(
    string Id, string Title, string Role, string Status, string? Repo, string? Branch,
    DateTimeOffset? CreatedAt, DateTimeOffset? LastActivity, IReadOnlyList<string> Tags);

/// <summary>Result of recording one answer: the question is gone, siblings are still unanswered, or the request is complete.</summary>
public sealed record AnswerProgress(bool Pending, int WaitingFor, HubQuestion? Question, IReadOnlyDictionary<string, string>? Answers)
{
    public static readonly AnswerProgress NotPending = new(false, 0, null, null);
    public bool Ready => Pending && WaitingFor == 0;
}

/// <summary>
/// What the last hub poll pass saw. Lists are immutable snapshots replaced as a whole, so readers never lock.
/// Also tracks console answers: partial answers per <c>can_use_tool</c> request, and claims on requests whose
/// <c>control_response</c> is in flight or sent, so a stale poll cannot bring an answered question back.
/// </summary>
public sealed class HubState(TimeProvider? time = null)
{
    /// <summary>A claim outlives a poll that still sees its request for at most this long.</summary>
    public static readonly TimeSpan ClaimTtl = TimeSpan.FromMinutes(10);

    private static readonly IReadOnlyList<SessionEvent> NoEvents = [];

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private volatile IReadOnlyList<HubSessionView> _sessions = [];
    private volatile IReadOnlyList<HubQuestion> _questions = [];
    private volatile IReadOnlyDictionary<string, IReadOnlyList<SessionEvent>> _recent =
        new Dictionary<string, IReadOnlyList<SessionEvent>>();
    private volatile bool _signedOut;
    private volatile string? _lastError;
    private readonly object _gate = new();
    private DateTimeOffset? _lastPollAt;
    private readonly Dictionary<string, DateTimeOffset> _claims = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, (string Question, string Value)>> _partial = new(StringComparer.Ordinal);

    public IReadOnlyList<HubSessionView> Sessions => _sessions;
    public IReadOnlyList<HubQuestion> Questions => _questions;

    public bool SignedOut { get => _signedOut; set => _signedOut = value; }
    public string? LastError { get => _lastError; set => _lastError = value; }

    public DateTimeOffset? LastPollAt
    {
        get { lock (_gate) return _lastPollAt; }
        set { lock (_gate) _lastPollAt = value; }
    }

    /// <summary>Questions whose request is claimed are left out. A claim ends when this poll no longer sees its
    /// request as pending (the <c>control_response</c> is visible) or when it is older than <see cref="ClaimTtl"/>.</summary>
    public void Publish(IReadOnlyList<HubSessionView> sessions, IReadOnlyList<HubQuestion> questions)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var seen = questions.Select(q => q.RequestId).ToHashSet(StringComparer.Ordinal);
            foreach (var (rid, at) in _claims.ToList())
                if (!seen.Contains(rid) || now - at > ClaimTtl) _claims.Remove(rid);
            foreach (var rid in _partial.Keys.Where(k => !seen.Contains(k)).ToList()) _partial.Remove(rid);
            _sessions = sessions.ToArray();
            _questions = questions.Where(q => !_claims.ContainsKey(q.RequestId)).ToArray();
        }
    }

    /// <summary>The poll's recent-event window per session (oldest first); empty until a poll has read the session.</summary>
    public IReadOnlyList<SessionEvent> RecentEvents(string sessionId) =>
        _recent.TryGetValue(sessionId, out var events) ? events : NoEvents;

    public void PublishRecent(IReadOnlyDictionary<string, IReadOnlyList<SessionEvent>> recent) =>
        _recent = new Dictionary<string, IReadOnlyList<SessionEvent>>(recent, StringComparer.Ordinal);

    /// <summary>
    /// Records the answer to one question. Returns <see cref="AnswerProgress.NotPending"/> when the question is not
    /// waiting (unknown, already answered, or its request is claimed); otherwise the count of sibling questions in the
    /// same request still unanswered, and when that is 0 the full question-text → answer map.
    /// </summary>
    public AnswerProgress RecordAnswer(string questionId, string value)
    {
        lock (_gate)
        {
            var q = _questions.FirstOrDefault(x => x.Id == questionId);
            if (q is null || _claims.ContainsKey(q.RequestId)) return AnswerProgress.NotPending;
            if (!_partial.TryGetValue(q.RequestId, out var answers))
                _partial[q.RequestId] = answers = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
            answers[q.Id] = (q.Question, value);
            var siblings = _questions.Where(x => x.RequestId == q.RequestId).ToList();
            var waiting = siblings.Count(x => !answers.ContainsKey(x.Id));
            if (waiting > 0) return new AnswerProgress(true, waiting, q, null);
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var x in siblings) map[x.Question] = answers[x.Id].Value;
            return new AnswerProgress(true, 0, q, map);
        }
    }

    /// <summary>Atomically marks a request as being answered; false when it already is.</summary>
    public bool TryClaim(string requestId)
    {
        lock (_gate)
        {
            if (_claims.ContainsKey(requestId)) return false;
            _claims[requestId] = _time.GetUtcNow();
            return true;
        }
    }

    /// <summary>Drops a claim after the answer failed to send, so the owner can retry.</summary>
    public void Release(string requestId)
    {
        lock (_gate) _claims.Remove(requestId);
    }

    /// <summary>The request's answer was sent: its questions disappear now, before the next poll.</summary>
    public void CompleteRequest(string requestId)
    {
        lock (_gate)
        {
            _partial.Remove(requestId);
            _questions = _questions.Where(q => q.RequestId != requestId).ToArray();
        }
    }
}
