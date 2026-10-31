using System.Text.Json;

namespace Fyrd.Core.Hub;

/// <summary>One pending <c>AskUserQuestion</c> item. <c>Input</c> is the raw <c>request.input</c> of its <c>can_use_tool</c> request.</summary>
public sealed record HubQuestion(
    string Id, string SessionId, string SessionTitle, string Question, IReadOnlyList<string> Options, bool MultiSelect,
    DateTimeOffset AskedAt, string ToolUseId, string RequestId, JsonElement Input);

public sealed record HubSessionView(
    string Id, string Title, string Role, string Status, string? Repo, string? Branch,
    DateTimeOffset? CreatedAt, DateTimeOffset? LastActivity, IReadOnlyList<string> Tags);

/// <summary>What the last hub poll pass saw. Lists are immutable snapshots replaced as a whole, so readers never lock.</summary>
public sealed class HubState
{
    private volatile IReadOnlyList<HubSessionView> _sessions = [];
    private volatile IReadOnlyList<HubQuestion> _questions = [];
    private volatile bool _signedOut;
    private volatile string? _lastError;
    private readonly object _gate = new();
    private DateTimeOffset? _lastPollAt;

    public IReadOnlyList<HubSessionView> Sessions => _sessions;
    public IReadOnlyList<HubQuestion> Questions => _questions;

    public bool SignedOut { get => _signedOut; set => _signedOut = value; }
    public string? LastError { get => _lastError; set => _lastError = value; }

    public DateTimeOffset? LastPollAt
    {
        get { lock (_gate) return _lastPollAt; }
        set { lock (_gate) _lastPollAt = value; }
    }

    public void Publish(IReadOnlyList<HubSessionView> sessions, IReadOnlyList<HubQuestion> questions)
    {
        lock (_gate)
        {
            _sessions = sessions.ToArray();
            _questions = questions.ToArray();
        }
    }

    /// <summary>Drops one question (it was just answered) so readers stop showing it before the next poll.</summary>
    public void RemoveQuestion(string id)
    {
        lock (_gate) _questions = _questions.Where(q => q.Id != id).ToArray();
    }
}
