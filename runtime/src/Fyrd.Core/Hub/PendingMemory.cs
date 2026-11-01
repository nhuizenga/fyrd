using System.Text.Json;

namespace Fyrd.Core.Hub;

public sealed record PendingMemoryEntry(string Id, string Text, string Source, string SessionId, DateTimeOffset At);

/// <summary>
/// Shared-memory proposals from peasants, held until the owner approves or rejects them in the console. Kept in
/// <c>hub/pending-memory.json</c>, outside <see cref="FyrdPaths.HubMemoryDir"/>, so it never reads as a memory scope.
/// </summary>
public sealed class PendingMemory(FyrdPaths paths, HubMemory memory)
{
    public const int MaxEntries = 100;
    public const int MaxTextLength = 2000;

    private readonly object _gate = new();
    private readonly string _file = Path.Combine(paths.HubDir, "pending-memory.json");

    /// <summary>Newest first.</summary>
    public IReadOnlyList<PendingMemoryEntry> List()
    {
        lock (_gate) return Read().AsEnumerable().Reverse().ToList();
    }

    public int Count { get { lock (_gate) return Read().Count; } }

    /// <summary>
    /// Queues a proposal. Returns null when it is empty, or the same text is already pending or already in shared memory.
    /// Text is clamped to <see cref="MaxTextLength"/>; past <see cref="MaxEntries"/> the oldest entries are dropped.
    /// </summary>
    public PendingMemoryEntry? Add(string text, string source, string sessionId, DateTimeOffset at)
    {
        text = Clamp(text);
        if (text.Length == 0) return null;
        lock (_gate)
        {
            var entries = Read();
            if (entries.Any(e => e.Text == text) || memory.List(HubMemory.Shared).Any(e => e.Text.Trim() == text)) return null;
            var entry = new PendingMemoryEntry(Guid.NewGuid().ToString("N"), text, source, sessionId, at);
            entries.Add(entry);
            if (entries.Count > MaxEntries) entries.RemoveRange(0, entries.Count - MaxEntries);
            Write(entries);
            return entry;
        }
    }

    /// <summary>Moves the entry into shared memory (with edited text when given) and removes it from the queue.</summary>
    public MemoryEntry Approve(string id, string? editedText, DateTimeOffset now)
    {
        lock (_gate)
        {
            var entries = Read();
            var i = entries.FindIndex(e => e.Id == id);
            if (i < 0) throw new KeyNotFoundException($"No pending memory entry '{id}'.");
            var added = memory.Add(HubMemory.Shared, editedText ?? entries[i].Text, entries[i].Source, now);
            entries.RemoveAt(i);
            Write(entries);
            return added;
        }
    }

    public void Reject(string id)
    {
        lock (_gate)
        {
            var entries = Read();
            if (entries.RemoveAll(e => e.Id == id) == 0) throw new KeyNotFoundException($"No pending memory entry '{id}'.");
            Write(entries);
        }
    }

    private static string Clamp(string? text)
    {
        text = (text ?? "").Trim();
        return text.Length > MaxTextLength ? text[..MaxTextLength].TrimEnd() : text;
    }

    private List<PendingMemoryEntry> Read()
    {
        if (!File.Exists(_file)) return [];
        try { return JsonSerializer.Deserialize<List<PendingMemoryEntry>>(File.ReadAllText(_file), Json.Options) ?? []; }
        catch (JsonException) { return []; }
    }

    private void Write(List<PendingMemoryEntry> entries)
    {
        Directory.CreateDirectory(paths.HubDir);
        AtomicFile.Write(_file, JsonSerializer.Serialize(entries, Json.Options));
    }
}
