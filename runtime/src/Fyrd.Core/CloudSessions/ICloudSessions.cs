namespace Fyrd.Core.CloudSessions;

/// <summary>The cloud-session operations Fyrd uses; implemented by <see cref="CloudSessionClient"/>.</summary>
public interface ICloudSessions
{
    Task<CloudSession> CreateAsync(CreateSessionRequest request, CancellationToken ct = default);
    Task<CloudSession> GetAsync(string sessionId, CancellationToken ct = default);

    Task<EventPage> ListEventsAsync(
        string sessionId, int limit = 100, string? afterId = null, string? beforeId = null, CancellationToken ct = default);

    Task SendMessageAsync(string sessionId, string text, CancellationToken ct = default);
    Task<IReadOnlyList<CloudSession>> ListByTagAsync(string tag, CancellationToken ct = default);
    Task InterruptAsync(string sessionId, CancellationToken ct = default);
    Task ArchiveAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// Answers a pending <c>AskUserQuestion</c> by posting a <c>control_response</c> to its <c>can_use_tool</c> request.
    /// <paramref name="answers"/> maps question text to the chosen option label (or free text).
    /// </summary>
    Task AnswerQuestionAsync(
        string sessionId, Hub.HubQuestion question, IReadOnlyDictionary<string, string> answers, CancellationToken ct = default);
}
