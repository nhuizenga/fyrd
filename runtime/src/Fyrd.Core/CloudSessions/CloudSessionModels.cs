using System.Text.Json;

namespace Fyrd.Core.CloudSessions;

public sealed record SessionSource(string RepoUrl, string? Revision);

public sealed record CreateSessionRequest(
    string Title,
    IReadOnlyList<string> Tags,
    string EnvironmentId,
    string FirstMessage,
    string Model,
    string? AppendSystemPrompt,
    SessionSource? Source,
    string? PermissionMode = null);

public sealed record PostTurnSummary(string? StatusCategory, string? NeedsAction, string? RecentAction, string? StatusDetail);

/// <summary><c>Raw</c> keeps the whole response so callers can read fields not named here yet.</summary>
public sealed record CloudSession(
    string Id,
    string? Title,
    string? SessionStatus,
    string? ConnectionStatus,
    PostTurnSummary? Summary,
    IReadOnlyList<string> Tags,
    JsonElement Raw);

/// <summary><c>Text</c> is the joined text blocks of <c>user</c> and <c>assistant</c> events; null otherwise.</summary>
public sealed record SessionEvent(string Uuid, string Type, DateTimeOffset CreatedAt, string? Text, JsonElement Raw);

/// <summary>A saved event read from the live stream; <c>Sequence</c> is its <c>sequence_num</c> (resume after it).</summary>
public sealed record StreamedEvent(long Sequence, SessionEvent Event);

public sealed record EventPage(IReadOnlyList<SessionEvent> Events, bool HasMore, string? FirstId, string? LastId);

public sealed class CloudSessionAuthException(int status)
    : Exception($"Claude cloud sessions API rejected the credentials (HTTP {status}). Sign in with `claude auth login`.");

public sealed class CloudSessionApiException(int status, string body)
    : Exception($"Claude cloud sessions API returned HTTP {status}.")
{
    public int Status { get; } = status;
    public string Body { get; } = body;
}
