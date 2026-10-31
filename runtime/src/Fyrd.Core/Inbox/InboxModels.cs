namespace Fyrd.Core;

public enum MemoryScope { Repo, Global }

public sealed record CardAnswer(
    string Text, bool Remember, MemoryScope Scope, DateTimeOffset AnsweredAt,
    string? RelayStatus = null, int RelayAttempts = 0, string? RelayError = null);

public sealed record InboxCard(
    string Id,
    string SpecId,
    string? UnitId,
    string Question,
    IReadOnlyList<string> Options,
    string? Recommendation,
    string? Reason,
    string? Details,
    bool AllowFreeText,
    DateTimeOffset CreatedAt,
    CardAnswer? Answer = null,
    string? SourceKey = null);

public sealed class CardNotAnsweredException(string cardId)
    : InvalidOperationException($"Card '{cardId}' has not been answered.");

public sealed class CardAlreadyAnsweredException(string cardId)
    : InvalidOperationException($"Card '{cardId}' has already been answered.");
