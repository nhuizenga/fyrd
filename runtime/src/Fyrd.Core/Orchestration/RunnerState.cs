using System.Collections.Concurrent;
using Fyrd.Core.CloudSessions;

namespace Fyrd.Core.Orchestration;

/// <summary>Shared, thread-safe view of what the poll loop last saw; read by the Board endpoints.</summary>
public sealed class RunnerState
{
    private readonly ConcurrentDictionary<string, CloudSession> _sessions = new();
    private volatile bool _signedOut;
    private volatile string? _lastError;
    private long _lastPollTicks;

    public bool SignedOut { get => _signedOut; set => _signedOut = value; }

    public string? LastError { get => _lastError; set => _lastError = value; }

    public DateTimeOffset? LastPollAt
    {
        get
        {
            var t = Interlocked.Read(ref _lastPollTicks);
            return t == 0 ? null : new DateTimeOffset(t, TimeSpan.Zero);
        }
        set => Interlocked.Exchange(ref _lastPollTicks, value?.UtcTicks ?? 0);
    }

    /// <summary>Latest known cloud session per session id.</summary>
    public IReadOnlyDictionary<string, CloudSession> Sessions => _sessions;

    private readonly ConcurrentDictionary<string, bool> _lastTurnHadQuestion = new();

    /// <summary>Whether the lead's latest assistant turn contained a question (in-memory).</summary>
    public bool LastTurnHadQuestion(string specId) => _lastTurnHadQuestion.TryGetValue(specId, out var had) && had;

    public void SetQuestionFlag(string specId, bool hadQuestion) => _lastTurnHadQuestion[specId] = hadQuestion;

    /// <summary>Called once an answer is relayed, so a later permission prompt can raise an attention card.</summary>
    public void ClearQuestionFlag(string specId) => _lastTurnHadQuestion[specId] = false;

    public void Record(CloudSession session) => _sessions[session.Id] = session;
}
