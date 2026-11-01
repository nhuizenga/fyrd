using System.Text.Json;
using Fyrd.Core.CloudSessions;

namespace Fyrd.Core.Hub;

/// <summary>One pending <c>AskUserQuestion</c> item. <c>Input</c> is the raw <c>request.input</c> of its <c>can_use_tool</c> request.</summary>
public sealed record HubQuestion(
    string Id, string SessionId, string SessionTitle, string Question, IReadOnlyList<string> Options, bool MultiSelect,
    DateTimeOffset AskedAt, string ToolUseId, string RequestId, JsonElement Input);

public sealed record HubSessionView(
    string Id, string Title, string Role, string Status, string? Repo, string? Branch,
    DateTimeOffset? CreatedAt, DateTimeOffset? LastActivity, IReadOnlyList<string> Tags,
    HubCostView? Cost = null)
{
    /// <summary>URLs of the pull requests the session opened.</summary>
    public IReadOnlyList<string> PullRequests { get; init; } = [];
}

/// <summary>
/// One session's spend today against its limit. <c>Blocked</c> is "session", "fyrd" or null. The Controller is
/// <c>Exempt</c>: it shows cost and ratio but is never blocked and cannot be raised.
/// </summary>
public sealed record HubCostView(
    decimal CostToday, decimal LimitUsd, double Ratio, bool Estimated, string? Blocked, bool CanExtend, bool Exempt);

/// <summary>Fyrd-wide spend today. <c>Date</c> is the local day (yyyy-MM-dd) in <c>TimeZone</c>.</summary>
public sealed record HubBudgetView(
    string Date, string TimeZone, decimal FyrdCostToday, decimal FyrdLimitUsd, bool FyrdBlocked);

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


    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private volatile IReadOnlyList<HubSessionView> _sessions = [];
    private volatile IReadOnlyList<HubQuestion> _questions = [];
    private volatile HubBudgetView? _budget;
    private volatile bool _signedOut;
    private volatile string? _lastError;
    private readonly object _gate = new();
    private DateTimeOffset? _lastPollAt;
    private readonly Dictionary<string, (DateTimeOffset At, string? SessionId)> _claims = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, (string Question, string Value)>> _partial = new(StringComparer.Ordinal);

    private TaskCompletionSource _pollRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once someone has asked for a poll since the poll loop last reset it.</summary>
    public Task PollRequested => Volatile.Read(ref _pollRequest).Task;

    /// <summary>Wakes the poll loop now (for example right after adopting a Controller).</summary>
    public void RequestPoll() => Volatile.Read(ref _pollRequest).TrySetResult();

    /// <summary>Called by the poll loop before a pass; a request made during the pass wakes the next wait.</summary>
    public void ResetPollRequest()
    {
        var current = Volatile.Read(ref _pollRequest);
        if (current.Task.IsCompleted)
            Interlocked.CompareExchange(ref _pollRequest, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), current);
    }

    public IReadOnlyList<HubSessionView> Sessions => _sessions;
    public IReadOnlyList<HubQuestion> Questions => _questions;

    public HubBudgetView? Budget => _budget;

    public bool SignedOut { get => _signedOut; set => _signedOut = value; }
    public string? LastError { get => _lastError; set => _lastError = value; }

    public DateTimeOffset? LastPollAt
    {
        get { lock (_gate) return _lastPollAt; }
        set { lock (_gate) _lastPollAt = value; }
    }

    /// <summary>
    /// Questions whose request is claimed are left out. A claim ends when this poll read its session successfully and
    /// no longer sees the request as pending (the <c>control_response</c> is visible), or when it is older than
    /// <see cref="ClaimTtl"/>. Claims on sessions in <paramref name="failedSessions"/> are kept: that poll saw nothing.
    /// </summary>
    public void Publish(
        IReadOnlyList<HubSessionView> sessions, IReadOnlyList<HubQuestion> questions, IReadOnlySet<string>? failedSessions = null,
        HubBudgetView? budget = null)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var seen = questions.Select(q => q.RequestId).ToHashSet(StringComparer.Ordinal);
            foreach (var (rid, claim) in _claims.ToList())
            {
                var unread = claim.SessionId is { } sid && failedSessions?.Contains(sid) == true;
                if ((!seen.Contains(rid) && !unread) || now - claim.At > ClaimTtl) _claims.Remove(rid);
            }
            foreach (var rid in _partial.Keys.Where(k => !seen.Contains(k)).ToList()) _partial.Remove(rid);
            _sessions = sessions.ToArray();
            _budget = budget;
            _questions = questions.Where(q => !_claims.ContainsKey(q.RequestId)).ToArray();
        }
    }

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

    /// <summary>Atomically marks a request as being answered; false when it already is. Pass the request's session so a
    /// failed poll of that session does not end the claim.</summary>
    public bool TryClaim(string requestId, string? sessionId = null)
    {
        lock (_gate)
        {
            if (_claims.ContainsKey(requestId)) return false;
            _claims[requestId] = (_time.GetUtcNow(), sessionId);
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
