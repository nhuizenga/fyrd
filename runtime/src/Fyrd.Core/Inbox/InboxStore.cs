using Fyrd.Core.Specs;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fyrd.Core;

public sealed partial class InboxStore(FyrdPaths paths, SpecStore specs, DecisionMemory memory, TimeProvider time)
{
    private readonly object _gate = new();

    public InboxCard Add(string specId, string? unitId, string question, IReadOnlyList<string> options,
        string? recommendation, string? reason, string? details, bool allowFreeText)
    {
        if (string.IsNullOrWhiteSpace(question)) throw new ArgumentException("Question is required.", nameof(question));
        if (specs.Get(specId) is null) throw new KeyNotFoundException($"Spec '{specId}' not found.");
        var card = new InboxCard(Guid.NewGuid().ToString("N")[..12], specId, unitId, question, options,
            recommendation, reason, details, allowFreeText, time.GetUtcNow());
        lock (_gate) Write(card);
        return card;
    }

    public IReadOnlyList<InboxCard> ListOpen()
    {
        lock (_gate)
        {
            return Directory.EnumerateFiles(paths.InboxDir, "*.json")
                .Select(Read)
                .OfType<InboxCard>()
                .Where(c => c.Answer is null)
                .OrderBy(c => c.CreatedAt)
                .ToList();
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
            if (remember)
            {
                var repo = specs.Get(card.SpecId)?.Repo
                    ?? throw new KeyNotFoundException($"Spec '{card.SpecId}' not found.");
                memory.Record(repo, scope, card.Question, text, now, card.SpecId);
            }
            var answered = card with { Answer = new CardAnswer(text, remember, scope, now) };
            Write(answered);
            return answered;
        }
    }

    private string PathFor(string id) => Path.Combine(paths.InboxDir, id + ".json");

    private static InboxCard? Read(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<InboxCard>(File.ReadAllText(path), Json.Options) : null;

    private void Write(InboxCard card)
    {
        var path = PathFor(card.Id);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(card, Json.Options));
        File.Move(tmp, path, overwrite: true);
    }

    [GeneratedRegex(@"^[0-9a-f]{12}\z")]
    private static partial Regex CardIdPattern();
}
