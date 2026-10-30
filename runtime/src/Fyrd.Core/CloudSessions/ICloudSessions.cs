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
}
