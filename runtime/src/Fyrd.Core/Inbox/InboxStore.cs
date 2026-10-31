using Fyrd.Core.Specs;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fyrd.Core;

public sealed partial class InboxStore(FyrdPaths paths, SpecStore specs, DecisionMemory memory, TimeProvider time)
{
    private readonly object _gate = new();

    public InboxCard Add(string specId, string? unitId, string question, IReadOnlyList<string> options,
        string? recommendation, string? reason, string? details, bool allowFreeText,
        string? sourceKey = null)
    {
        if (string.IsNullOrWhiteSpace(question)) throw new ArgumentException("Question is required.", nameof(question));
        if (specs.Get(specId) is null) throw new KeyNotFoundException($"Spec '{specId}' not found.");
        var card = new InboxCard(Guid.NewGuid().ToString("N")[..12], specId, unitId, question, options,
            recommendation, reason, details, allowFreeText, time.GetUtcNow(), SourceKey: sourceKey);
        lock (_gate) Write(card);
        return card;
    }

    public IReadOnlyList<InboxCard> ListOpen()
    {
        lock (_gate)
        {
            return Directory.EnumerateFiles(paths.InboxDir, "*.json")
                .Select(ReadOrSkip)
                .OfType<InboxCard>()
                .Where(c => c.Answer is null)
                .OrderBy(c => c.CreatedAt)
                .ToList();
        }
    }

    public IReadOnlyList<InboxCard> ListOpenFor(string specId) =>
        ListOpen().Where(c => c.SpecId == specId).ToList();

    /// <summary>True for open or answered cards carrying this source key.</summary>
    public bool HasSourceKey(string sourceKey)
    {
        lock (_gate)
        {
            return Directory.EnumerateFiles(paths.InboxDir, "*.json")
                .Select(ReadOrSkip).OfType<InboxCard>()
                .Any(c => c.SourceKey == sourceKey);
        }
    }

    public InboxCard Answer(string cardId, string text, bool remember = true, MemoryScope scope = MemoryScope.Repo)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Answer text is required.", nameof(text));
        if (!CardIdPattern().IsMatch(cardId)) throw new ArgumentException($"Invalid card id '{cardId}'.", nameof(cardId));

        lock (_gate)
        {
            var card = Read(PathFor(cardId)) ?? throw new KeyNotFoundException($"Card '{cardId}' not found.");
            if (card.Answer is not null) throw new CardAlreadyAnsweredException(cardId);

            var now = time.GetUtcNow();
            // Attention answers are one-off instructions about a session, not reusable decisions.
            if (card.SourceKey?.Contains(":attention:", StringComparison.Ordinal) == true) remember = false;
            if (remember)
            {
                var repo = specs.Get(card.SpecId)?.Repo
                    ?? throw new KeyNotFoundException($"Spec '{card.SpecId}' not found.");
                memory.Record(repo, scope, card.Question, text, now, card.SpecId);
            }
            var answered = card with { Answer = new CardAnswer(text, remember, scope, now,
                RelayStatus: string.IsNullOrEmpty(card.SourceKey) ? null : "pending") };
            Write(answered);
            return answered;
        }
    }

    public InboxCard Get(string cardId)
    {
        if (!CardIdPattern().IsMatch(cardId)) throw new ArgumentException($"Invalid card id '{cardId}'.", nameof(cardId));
        lock (_gate) return Read(PathFor(cardId)) ?? throw new KeyNotFoundException($"Card '{cardId}' not found.");
    }

    public const int MaxRelayAttempts = 5;

    /// <summary>Records relay progress; a <c>failed</c> status counts one more attempt. Message only, never a body or token.</summary>
    public InboxCard SetRelay(string cardId, string status, string? error) =>
        MutateAnswered(cardId, a => a with
        {
            RelayStatus = status,
            RelayError = status == "sent" ? null : error,
            RelayAttempts = status == "failed" ? a.RelayAttempts + 1 : a.RelayAttempts,
        });

    /// <summary>Marks the answer as pending delivery again with a fresh attempt budget.</summary>
    public InboxCard ResetRelay(string cardId) =>
        MutateAnswered(cardId, a => a with { RelayStatus = "pending", RelayAttempts = 0, RelayError = null });

    public IReadOnlyList<InboxCard> ListRelaysToRetry()
    {
        lock (_gate)
        {
            return Directory.EnumerateFiles(paths.InboxDir, "*.json")
                .Select(ReadOrSkip).OfType<InboxCard>()
                .Where(c => c.Answer is { RelayStatus: "pending" or "failed" } a
                            && a.RelayAttempts < MaxRelayAttempts && !string.IsNullOrEmpty(c.SourceKey))
                .OrderBy(c => c.CreatedAt)
                .ToList();
        }
    }

    /// <summary>Answered cards whose relay to the lead failed.</summary>
    public IReadOnlyList<InboxCard> ListUndelivered()
    {
        lock (_gate)
        {
            return Directory.EnumerateFiles(paths.InboxDir, "*.json")
                .Select(ReadOrSkip).OfType<InboxCard>()
                .Where(c => c.Answer is { RelayStatus: "failed" })
                .OrderBy(c => c.CreatedAt)
                .ToList();
        }
    }

    private InboxCard MutateAnswered(string cardId, Func<CardAnswer, CardAnswer> change)
    {
        if (!CardIdPattern().IsMatch(cardId)) throw new ArgumentException($"Invalid card id '{cardId}'.", nameof(cardId));
        lock (_gate)
        {
            var card = Read(PathFor(cardId)) ?? throw new KeyNotFoundException($"Card '{cardId}' not found.");
            if (card.Answer is null) throw new CardNotAnsweredException(cardId);
            var updated = card with { Answer = change(card.Answer) };
            Write(updated);
            return updated;
        }
    }

    private string PathFor(string id) => Path.Combine(paths.InboxDir, id + ".json");

    private static InboxCard? Read(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<InboxCard>(File.ReadAllText(path), Json.Options) : null;

    // A corrupt or unreadable card must not take the whole inbox down.
    private static InboxCard? ReadOrSkip(string path)
    {
        try { return Read(path); }
        catch (Exception e) when (e is JsonException or IOException)
        {
            Console.Error.WriteLine($"fyrd: skipping unreadable inbox card '{path}': {e.Message}");
            return null;
        }
    }

    private void Write(InboxCard card) =>
        AtomicFile.Write(PathFor(card.Id), JsonSerializer.Serialize(card, Json.Options));

    [GeneratedRegex(@"^[0-9a-f]{12}\z")]
    private static partial Regex CardIdPattern();
}
